using System.Runtime.InteropServices;
using Aria.Bench;
using Aria.Bench.Rendering;

namespace Aria.Tests;

/// <summary>Préréglage Ultra RT d'Aria-Bench (ray tracing DXR 1.1) : lecture des options, constantes, shaders intégrés.</summary>
public class RayTracingTests
{
    [Theory]
    [InlineData("ultra-rt")]
    [InlineData("Ultra RT")]
    [InlineData("ultrart")]
    [InlineData("ULTRA_RT")]
    public void UltraRtIsFoundUnderItsUsualSpellings(string text)
    {
        Assert.Same(QualityPreset.UltraRt, QualityPreset.Find(text));
    }

    [Fact]
    public void UltraRtIsUltraLitByRays()
    {
        var rt = QualityPreset.UltraRt;
        var ultra = QualityPreset.Ultra;

        Assert.True(rt.RayTraced);
        Assert.Equal(4, rt.AoRays);
        // Même scène qu'Ultra : seul l'éclairage change (SSAO remplacé par les rayons d'occlusion)
        Assert.Equal((ultra.TerrainResolution, ultra.ObjectGrid, ultra.LightCount, ultra.Octaves, ultra.BloomLevels),
            (rt.TerrainResolution, rt.ObjectGrid, rt.LightCount, rt.Octaves, rt.BloomLevels));
        Assert.Equal(0, rt.SsaoSamples);
        Assert.DoesNotContain(QualityPreset.All, p => p.RayTraced);
    }

    [Fact]
    public void AllStaysWithoutRayTracingAndUltraRtMustBeAsked()
    {
        Assert.Equal(QualityPreset.All, BenchOptions.Parse(["--qualite", "tout"]).Qualities);
        Assert.Equal([QualityPreset.UltraRt], BenchOptions.Parse(["--qualite", "ultra-rt"]).Qualities);
    }

    [Fact]
    public void RayTracingConstantsKeepTheShaderLayout()
    {
        // « cbuffer Frame » des shaders : 288 octets avant les lumières, AoRays et Reflections à la place de l'ancien remplissage
        Assert.Equal(288, Marshal.SizeOf<Renderer.FrameHeader>());
        var rt = Renderer.BuildHeader(QualityPreset.UltraRt, 0f, 16f / 9f);
        var ultra = Renderer.BuildHeader(QualityPreset.Ultra, 0f, 16f / 9f);
        Assert.Equal((4u, 1u), (rt.AoRays, rt.Reflections));
        Assert.Equal((0u, 0u), (ultra.AoRays, ultra.Reflections));
    }

    [Theory]
    [InlineData("CSBuildRt")]
    [InlineData("PSTerrainRT")]
    [InlineData("PSObjectsRT")]
    public void RayTracingShadersAreBuiltIntoTheExe(string entry)
    {
        var code = ShaderLibrary.Load(entry);

        Assert.Contains(ShaderLibrary.Entries, e => e.Entry == entry);
        Assert.Equal("DXBC"u8.ToArray(), code[..4]);
        // Signé par dxil.dll : sans signature (16 octets nuls), le pilote refuse le shader
        Assert.NotEqual(new byte[16], code[4..20]);
    }
}
