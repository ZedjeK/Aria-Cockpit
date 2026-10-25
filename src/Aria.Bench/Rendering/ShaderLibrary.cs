using System.Reflection;
using Vortice.Dxc;

namespace Aria.Bench.Rendering;

/// <summary>Shaders HLSL intégrés à l'exe, compilés en DXIL (modèle 6.0) par DXC au lancement.</summary>
public static class ShaderLibrary
{
    public static IReadOnlyList<string> Names { get; } = ["Scene", "Blit"];

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

    /// <summary>Les deux étapes d'un shader : sommets (VSMain, qui porte la signature racine) et pixels (PSMain).</summary>
    public static (byte[] Vertex, byte[] Pixel) CompilePair(string name) =>
        (Compile(name, DxcShaderStage.Vertex, "VSMain"), Compile(name, DxcShaderStage.Pixel, "PSMain"));
}
