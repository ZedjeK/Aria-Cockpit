using System.Reflection;
using Vortice.Dxc;

namespace Aria.Bench.Rendering;

/// <summary>Shaders HLSL intégrés à l'exe, compilés en DXIL (modèle 6.0) par DXC au lancement.</summary>
public static class ShaderLibrary
{
    public static IReadOnlyList<string> Names { get; } = ["Scene", "Post"];

    /// <summary>Tous les points d'entrée utilisés par le moteur (fichier, étape, nom) : vérifiés par les tests.</summary>
    public static IReadOnlyList<(string File, DxcShaderStage Stage, string Entry)> Entries { get; } =
    [
        ("Scene", DxcShaderStage.Vertex, "VSSky"), ("Scene", DxcShaderStage.Pixel, "PSSky"),
        ("Scene", DxcShaderStage.Vertex, "VSTerrain"), ("Scene", DxcShaderStage.Pixel, "PSTerrain"),
        ("Scene", DxcShaderStage.Vertex, "VSObjects"), ("Scene", DxcShaderStage.Pixel, "PSObjects"),
        ("Post", DxcShaderStage.Vertex, "VSFull"), ("Post", DxcShaderStage.Pixel, "PSSsao"),
        ("Post", DxcShaderStage.Pixel, "PSBloomDown"), ("Post", DxcShaderStage.Pixel, "PSBloomUp"),
        ("Post", DxcShaderStage.Pixel, "PSComposite")
    ];

    public static string Source(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Aria.Bench.Shaders.{name}.hlsl")
            ?? throw new BenchException($"Shader introuvable dans l'exe : {name}.hlsl");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static byte[] Compile(string name, DxcShaderStage stage, string entryPoint)
    {
        var options = new DxcCompilerOptions { ShaderModel = DxcShaderModel.Model6_0 };
        using var result = DxcCompiler.Compile(stage, Source(name), entryPoint, options, $"{name}.hlsl");
        var bytecode = result.GetStatus().Success ? result.GetObjectBytecodeArray() : [];
        if (bytecode.Length == 0)
            throw new BenchException($"Le shader {name}.hlsl ({entryPoint}) ne compile pas : {result.GetErrors()}".Trim());
        return bytecode;
    }

    /// <summary>Compile tous les points d'entrée une fois ; clé : nom du point d'entrée.</summary>
    public static Dictionary<string, byte[]> CompileAll() =>
        Entries.ToDictionary(e => e.Entry, e => Compile(e.File, e.Stage, e.Entry));
}
