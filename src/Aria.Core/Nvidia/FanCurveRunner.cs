namespace Aria.Core.Nvidia;

/// <summary>Dernier relevé de la courbe : température lue, mode et vitesse imposée.</summary>
public sealed record FanStatus(uint? TemperatureC, FanMode Mode, uint Percent);

/// <summary>
/// Fait tourner la courbe : un relevé par seconde, tant qu'elle est démarrée. Les ventilateurs sont rendus au pilote
/// au démarrage (état connu), à l'arrêt, et à la première erreur, qui arrête la courbe.
/// </summary>
public sealed class FanCurveRunner(IFanDevice device, FanCurveController controller)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _stop;
    private Task? _loop;

    /// <summary>Après chaque relevé réussi.</summary>
    public event Action<FanStatus>? Updated;

    /// <summary>La courbe s'est arrêtée sur une erreur ; les ventilateurs ont été rendus au pilote.</summary>
    public event Action<string>? Failed;

    public bool Running => _loop is { IsCompleted: false };

    /// <summary>Rend la main au pilote, puis lance la boucle. Lève <see cref="GpuTuningException"/> si le pilote refuse.</summary>
    public void Start()
    {
        if (Running) return;
        device.RestoreAuto();
        controller.Reset();
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = Task.Run(() => LoopAsync(token));
    }

    /// <summary>Arrête la boucle et rend la main au pilote.</summary>
    public async Task StopAsync()
    {
        if (_stop is null) return;
        _stop.Cancel();
        try { await _loop!; }
        catch (OperationCanceledException) { }
        _stop.Dispose();
        _stop = null;
        _loop = null;
        controller.Reset();
        device.RestoreAuto();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            if (!Tick()) return;
        } while (await timer.WaitForNextTickAsync(token));
    }

    /// <summary>Un relevé : lecture, décision, envoi. Faux si une erreur a arrêté la courbe.</summary>
    public bool Tick()
    {
        FanStatus status;
        try
        {
            var temperature = device.ReadTemperature();
            if (controller.Next(temperature) is { } command)
            {
                if (command.Mode == FanMode.Auto) device.RestoreAuto();
                else device.SetSpeed(command.Percent);
            }
            status = new FanStatus(temperature, controller.Mode, controller.Percent);
        }
        catch (Exception e)
        {
            var message = e.Message;
            controller.Reset();
            try { device.RestoreAuto(); }
            catch (Exception again) { message += " " + again.Message; }
            Failed?.Invoke(message);
            return false;
        }
        Updated?.Invoke(status);
        return true;
    }
}
