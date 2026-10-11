using Aria.Core.Models;

namespace Aria.Core.Diagnostics;

/// <summary>Contrôle en lecture seule du matériel.</summary>
public interface IDiagnosticCheck
{
    /// <summary>Nom lisible, utilisé si le contrôle échoue.</summary>
    string Name { get; }

    IEnumerable<DiagnosticFinding> Run(DiagnosticContext context);
}

public sealed class DiagnosticContext(HardwareProfile hardware, BiosGuide bios)
{
    public HardwareProfile Hardware { get; } = hardware;
    public BiosGuide Bios { get; } = bios;

    /// <summary>"amd" ou "intel" : suffixe des options BIOS qui dépendent de la plateforme.</summary>
    public string Platform => Hardware.Cpu.IsAmd ? "amd" : "intel";

    /// <summary>Étapes BIOS pour une option propre à la plateforme (ex. "memory_profile" → "memory_profile.amd").</summary>
    public IReadOnlyList<string> PlatformSteps(string optionBase) => Bios.Steps($"{optionBase}.{Platform}");
}
