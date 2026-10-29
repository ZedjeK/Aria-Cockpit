using Aria.Bench.Rendering;
using SharpGen.Runtime;
using Vortice.Direct3D12;

namespace Aria.Bench.Stability;

/// <summary>
/// Socle des contrôles de stabilité : signature racine et pipelines de calcul (Fill, Verify, Burn), sur un device à part
/// de la carte choisie par <see cref="GpuDevice"/> (sans chaîne d'affichage), à partir des shaders précompilés de Shaders/Vram.hlsl.
/// </summary>
internal sealed class ComputeKit : IDisposable
{
    public GpuDevice Gpu { get; }
    public ID3D12Device Device { get; }
    public ID3D12RootSignature Root { get; }
    public ID3D12PipelineState Fill { get; }
    public ID3D12PipelineState Verify { get; }
    public ID3D12PipelineState Burn { get; }

    public ComputeKit(GpuDevice gpu)
    {
        Gpu = gpu;
        Device = gpu.CreateSeparateDevice();
        var fill = ShaderLibrary.Load("CSFill");
        // La signature racine est écrite dans le shader : deux tampons bruts (u0, u1) et 8 constantes (b0)
        Root = Device.CreateRootSignature(fill);
        Fill = Pipeline(fill);
        Verify = Pipeline(ShaderLibrary.Load("CSVerify"));
        Burn = Pipeline(ShaderLibrary.Load("CSBurn"));
    }

    /// <summary>Tampon en mémoire vidéo accessible en écriture par les shaders.</summary>
    public ID3D12Resource Buffer(ulong bytes, string name)
    {
        var buffer = Device.CreateCommittedResource(HeapType.Default, ResourceDescription.Buffer(bytes, ResourceFlags.AllowUnorderedAccess),
            ResourceStates.Common);
        buffer.Name = name;
        return buffer;
    }

    /// <summary>Raison donnée par Windows si la carte a été retirée (plantage ou réinitialisation du pilote).</summary>
    public string? RemovedReason()
    {
        var reason = Device.DeviceRemovedReason;
        return reason.Success ? null : GpuDevice.Explain(reason.Code);
    }

    private ID3D12PipelineState Pipeline(byte[] code) =>
        Device.CreateComputePipelineState(new ComputePipelineStateDescription { RootSignature = Root, ComputeShader = code });

    public void Dispose()
    {
        Fill.Dispose();
        Verify.Dispose();
        Burn.Dispose();
        Root.Dispose();
        Device.Dispose();
    }
}

/// <summary>
/// Une file de commandes à part, avec sa liste et sa synchronisation : la mémoire et le calcul tournent sur deux files
/// en parallèle, sans passer par la file d'affichage du banc.
/// </summary>
internal sealed class Lane : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ComputeKit _kit;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _event = new(false);
    private ulong _value;

    public ID3D12CommandQueue Queue { get; }
    public ID3D12GraphicsCommandList List { get; }

    public Lane(ComputeKit kit, CommandListType type, string name)
    {
        _kit = kit;
        Queue = kit.Device.CreateCommandQueue(type);
        Queue.Name = name;
        _allocator = kit.Device.CreateCommandAllocator(type);
        List = kit.Device.CreateCommandList<ID3D12GraphicsCommandList>(type, _allocator);
        List.Close();
        _fence = kit.Device.CreateFence(0);
    }

    public ID3D12GraphicsCommandList Begin()
    {
        _allocator.Reset();
        List.Reset(_allocator);
        List.SetComputeRootSignature(_kit.Root);
        return List;
    }

    /// <summary>Envoie la liste et attend la fin ; un échec veut presque toujours dire que le pilote a lâché.</summary>
    public void Run()
    {
        List.Close();
        Queue.ExecuteCommandList(List);
        var value = ++_value;
        Check(Queue.Signal(_fence, value));
        if (_fence.CompletedValue >= value) return;
        Check(_fence.SetEventOnCompletion(value, _event));
        if (!_event.WaitOne(Timeout))
            throw new BenchException($"La carte ne répond plus depuis {Timeout.TotalSeconds:0} s. {_kit.RemovedReason()}".Trim());
    }

    private void Check(Result result)
    {
        if (result.Success) return;
        throw new BenchException($"Échec de synchronisation avec la carte : {GpuDevice.Explain(result.Code)}. {_kit.RemovedReason()}".Trim());
    }

    public void Dispose()
    {
        List.Dispose();
        _allocator.Dispose();
        _fence.Dispose();
        _event.Dispose();
        Queue.Dispose();
    }
}
