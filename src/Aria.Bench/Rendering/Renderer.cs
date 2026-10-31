using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Aria.Bench.Rendering;

/// <summary>
/// Le rendu d'une image, dans l'ordre d'un jeu :
/// 1. ombres du soleil (carte de profondeur vue du soleil) ;
/// 2. scène à la définition interne : ciel, terrain, tours et rochers, éclairés (soleil, néons, ciel) ;
/// 3. occlusion ambiante (SSAO), halo lumineux (bloom) ;
/// 4. image finale à la taille de l'écran : tonemapping, bandes noires 16:9, barre de progression.
/// </summary>
internal sealed unsafe class Renderer : IDisposable
{
    public const float TerrainSize = 1000f;
    public const float TerrainHeight = 90f;
    private const Format HdrFormat = Format.R16G16B16A16_Float;
    private const Format NormalDepthFormat = Format.R16G16B16A16_Float;
    private const Format AoFormat = Format.R16_Float;
    private const Format BloomFormat = Format.R11G11B10_Float;
    private const Format DepthFormat = Format.D32_Float;
    private const Format FinalFormat = Format.R8G8B8A8_UNorm_SRgb;
    private const Format CheckFormat = Format.R8G8B8A8_UNorm;
    /// <summary>Instant de la scène utilisé pour l'image de contrôle (le même à chaque vérification).</summary>
    public const float CheckTime = 7.5f;
    private const int FrameBufferSize = 4608;   // FrameHeader (288 octets) + 256 lumières, arrondi à 256
    private const uint DefaultComponentMapping = 0x1688;
    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.55f, 0.32f, 0.42f));

    private readonly GpuDevice _gpu;
    private readonly ID3D12RootSignature _sceneRoot, _postRoot;
    private readonly ID3D12PipelineState _skyPso, _terrainPso, _objectsPso, _terrainShadowPso, _objectsShadowPso;
    private readonly ID3D12PipelineState _ssaoPso, _bloomDownPso, _bloomUpPso, _compositePso, _checkPso;
    private readonly ID3D12CommandAllocator _checkAllocator;
    private readonly ID3D12GraphicsCommandList _checkList;
    private readonly ID3D12Resource _cubeVertices, _cubeIndices;
    private readonly VertexBufferView _cubeVertexView;
    private readonly uint _cubeIndexCount;
    private readonly ID3D12Resource[] _frameBuffers = new ID3D12Resource[GpuDevice.BufferCount];
    private readonly byte*[] _frameData = new byte*[GpuDevice.BufferCount];
    private readonly DescriptorAllocator _rtv, _dsv, _srv;

    private ID3D12Resource? _terrainIndices;
    private uint _terrainIndexCount;
    private int _terrainResolution;
    private RenderTexture? _hdr, _normalDepth, _depth, _shadow, _ao;
    private RenderTexture[] _bloom = [];
    private RenderTexture? _check;
    private ID3D12Resource? _readback;
    private PlacedSubresourceFootPrint _checkFootprint;
    private int _shadowTable, _ssaoTable, _compositeTable;
    private int[] _bloomDownTables = [], _bloomUpTables = [];

    public QualityPreset Preset { get; private set; } = QualityPreset.High;
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Début du tampon de constantes de la scène (même disposition que « cbuffer Frame » des shaders).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameHeader
    {
        public Matrix4x4 ViewProj, InvViewProj, LightViewProj;
        public Vector3 Eye; public float Time;
        public Vector3 SunDir; public float TerrainSize;
        public Vector3 SunColor; public float TerrainHeight;
        public Vector3 LightsCenter; public float FogDensity;
        public uint LightCount, Octaves, PcfRadius, TerrainRes;
        public uint ObjectGrid; public float ShadowTexel; public float Pad0, Pad1;
        public const int Size = 288;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PostConstants
    {
        public Vector2 TexelSize;
        public float Progress;
        public uint Samples;
        public float BloomStrength;
        public float AoStrength;
        public float Exposure;
        public uint Mode;
    }

    public Renderer(GpuDevice gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;
        var code = ShaderLibrary.LoadAll();

        // Signatures racines écrites dans les shaders ([RootSignature]) et lues dans le code compilé
        _sceneRoot = device.CreateRootSignature(code["VSTerrain"]);
        _postRoot = device.CreateRootSignature(code["VSFull"]);

        var objectLayout = new InputLayoutDescription(
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0));
        var shadowRaster = new RasterizerDescription(CullMode.None, FillMode.Solid, false, 0, 0f, 2.0f, true, false, false, 0,
            ConservativeRasterizationMode.Off);

        ID3D12PipelineState Scene(string name, string vs, string? ps, InputLayoutDescription? layout, bool shadow, DepthStencilDescription depth) =>
            Named(device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = _sceneRoot,
                VertexShader = code[vs],
                PixelShader = ps is null ? ReadOnlyMemory<byte>.Empty : code[ps],
                InputLayout = layout ?? new InputLayoutDescription(),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RasterizerState = shadow ? shadowRaster : RasterizerDescription.CullNone,
                BlendState = BlendDescription.Opaque,
                DepthStencilState = depth,
                SampleMask = uint.MaxValue,
                RenderTargetFormats = shadow ? [] : [HdrFormat, NormalDepthFormat],
                DepthStencilFormat = DepthFormat,
                SampleDescription = new SampleDescription(1, 0)
            }), name);

        _skyPso = Scene("Ciel", "VSSky", "PSSky", null, false, DepthStencilDescription.None);
        _terrainPso = Scene("Terrain", "VSTerrain", "PSTerrain", null, false, DepthStencilDescription.Default);
        _objectsPso = Scene("Objets", "VSObjects", "PSObjects", objectLayout, false, DepthStencilDescription.Default);
        _terrainShadowPso = Scene("Terrain (ombre)", "VSTerrain", null, null, true, DepthStencilDescription.Default);
        _objectsShadowPso = Scene("Objets (ombre)", "VSObjects", null, objectLayout, true, DepthStencilDescription.Default);

        ID3D12PipelineState Post(string name, string ps, Format target, BlendDescription blend) =>
            Named(device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
            {
                RootSignature = _postRoot,
                VertexShader = code["VSFull"],
                PixelShader = code[ps],
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = blend,
                DepthStencilState = DepthStencilDescription.None,
                SampleMask = uint.MaxValue,
                RenderTargetFormats = [target],
                DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0)
            }), name);

        _ssaoPso = Post("SSAO", "PSSsao", AoFormat, BlendDescription.Opaque);
        _bloomDownPso = Post("Bloom (réduction)", "PSBloomDown", BloomFormat, BlendDescription.Opaque);
        _bloomUpPso = Post("Bloom (agrandissement)", "PSBloomUp", BloomFormat, BlendDescription.Additive);
        _compositePso = Post("Image finale", "PSComposite", FinalFormat, BlendDescription.Opaque);
        _checkPso = Post("Image de contrôle", "PSComposite", CheckFormat, BlendDescription.Opaque);
        _checkAllocator = device.CreateCommandAllocator(CommandListType.Direct);
        _checkList = device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _checkAllocator);
        _checkList.Close();

        var (vertices, indices) = CubeMesh.Build();
        _cubeVertices = gpu.CreateBuffer(vertices, ResourceStates.VertexAndConstantBuffer, "Sommets du cube");
        _cubeIndices = gpu.CreateBuffer(indices, ResourceStates.IndexBuffer, "Indices du cube");
        _cubeVertexView = new VertexBufferView(_cubeVertices.GPUVirtualAddress, (uint)(vertices.Length * Vertex.Size), Vertex.Size);
        _cubeIndexCount = (uint)indices.Length;

        // Un tampon de constantes par image en vol, écrit directement en mémoire partagée (laissé ouvert)
        for (var i = 0; i < GpuDevice.BufferCount; i++)
        {
            _frameBuffers[i] = device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(FrameBufferSize), ResourceStates.GenericRead);
            _frameBuffers[i].Name = $"Constantes de l'image {i}";
            _frameData[i] = _frameBuffers[i].Map<byte>(0);
        }

        _rtv = new DescriptorAllocator(device, DescriptorHeapType.RenderTargetView, 32);
        _dsv = new DescriptorAllocator(device, DescriptorHeapType.DepthStencilView, 4);
        _srv = new DescriptorAllocator(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 64, shaderVisible: true);
    }

    private static ID3D12PipelineState Named(ID3D12PipelineState pso, string name)
    {
        pso.Name = name;
        return pso;
    }

    /// <summary>Prépare un préréglage à une définition : cibles de rendu, carte d'ombre, maillage du terrain.</summary>
    public void Configure(QualityPreset preset, int width, int height)
    {
        _gpu.WaitIdle();
        DisposeTargets();
        Preset = preset;
        Width = width;
        Height = height;
        var device = _gpu.Device;

        if (_terrainResolution != preset.TerrainResolution)
        {
            _terrainIndices?.Dispose();
            var indices = TerrainIndices(preset.TerrainResolution);
            _terrainIndices = _gpu.CreateBuffer(indices, ResourceStates.IndexBuffer, $"Terrain {preset.TerrainResolution}²");
            _terrainIndexCount = (uint)indices.Length;
            _terrainResolution = preset.TerrainResolution;
        }

        _rtv.Reset();
        _dsv.Reset();
        _srv.Reset();
        _hdr = RenderTexture.Color(device, "Couleur HDR", HdrFormat, width, height, _rtv.Cpu(_rtv.Allocate()), new Color4(0, 0, 0, 0));
        _normalDepth = RenderTexture.Color(device, "Normales et distance", NormalDepthFormat, width, height, _rtv.Cpu(_rtv.Allocate()), new Color4(0, 0, 0, 0));
        _depth = RenderTexture.Depth(device, "Profondeur", DepthFormat, DepthFormat, width, height, _dsv.Cpu(_dsv.Allocate()), ResourceStates.DepthWrite);
        _shadow = RenderTexture.Depth(device, "Carte d'ombre", DepthFormat, Format.R32_Typeless, preset.ShadowMapSize, preset.ShadowMapSize,
            _dsv.Cpu(_dsv.Allocate()), ResourceStates.PixelShaderResource);
        if (preset.SsaoSamples > 0)
            _ao = RenderTexture.Color(device, "Occlusion ambiante", AoFormat, width, height, _rtv.Cpu(_rtv.Allocate()), new Color4(1, 1, 1, 1));

        _bloom = new RenderTexture[preset.BloomLevels];
        for (var i = 0; i < _bloom.Length; i++)
            _bloom[i] = RenderTexture.Color(device, $"Bloom {i}", BloomFormat, Math.Max(1, width >> (i + 1)), Math.Max(1, height >> (i + 1)),
                _rtv.Cpu(_rtv.Allocate()), new Color4(0, 0, 0, 0));

        _check = RenderTexture.Color(device, "Image de contrôle", CheckFormat, width, height, _rtv.Cpu(_rtv.Allocate()), new Color4(0, 0, 0, 1));
        var footprints = new PlacedSubresourceFootPrint[1];
        device.GetCopyableFootprints(_check.Resource.Description, 0, 1, 0, footprints, new uint[1], new ulong[1], out var readbackSize);
        _checkFootprint = footprints[0];
        _readback = device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(readbackSize), ResourceStates.CopyDest);
        _readback.Name = "Relecture de l'image de contrôle";

        _shadowTable = Table((_shadow, Format.R32_Float));
        _ssaoTable = Table((_normalDepth, NormalDepthFormat));
        _bloomDownTables = [.. Enumerable.Range(0, _bloom.Length).Select(i => i == 0 ? Table((_hdr, HdrFormat)) : Table((_bloom[i - 1], BloomFormat)))];
        _bloomUpTables = [.. _bloom.Select(b => Table((b, BloomFormat)))];
        _compositeTable = Table((_hdr, HdrFormat), (_ao, AoFormat), (_bloom.Length > 0 ? _bloom[0] : null, BloomFormat));
    }

    /// <summary>Quatre descripteurs consécutifs (t0 à t3) ; les places vides reçoivent une vue nulle.</summary>
    private int Table(params (RenderTexture? Texture, Format Format)[] items)
    {
        var start = _srv.Allocate(4);
        for (var i = 0; i < 4; i++)
        {
            var (texture, format) = i < items.Length ? items[i] : (null, Format.R8G8B8A8_UNorm);
            _gpu.Device.CreateShaderResourceView(texture?.Resource, new ShaderResourceViewDescription
            {
                Format = texture is null ? Format.R8G8B8A8_UNorm : format,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = DefaultComponentMapping,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
            }, _srv.Cpu(start + i));
        }
        return start;
    }

    /// <summary>Indices du maillage du terrain : (r + 1)² sommets générés par le shader, 2 triangles par carré.</summary>
    internal static uint[] TerrainIndices(int resolution)
    {
        var row = (uint)resolution + 1;
        var indices = new uint[resolution * resolution * 6];
        var k = 0;
        for (uint z = 0; z < resolution; z++)
            for (uint x = 0; x < resolution; x++)
            {
                var i0 = z * row + x;
                var i2 = i0 + row;
                indices[k++] = i0; indices[k++] = i2; indices[k++] = i0 + 1;
                indices[k++] = i0 + 1; indices[k++] = i2; indices[k++] = i2 + 1;
            }
        return indices;
    }

    /// <param name="time">Temps de la scène en secondes (le même d'une passe à l'autre, pour comparer).</param>
    /// <param name="progress">Avancement global de 0 à 1, affiché par la barre du bas.</param>
    public void Render(float time, float progress)
    {
        var cl = _gpu.BeginFrame();
        var frame = _gpu.FrameIndex;
        WriteFrameConstants(frame, time);
        RecordScene(cl, _frameBuffers[frame].GPUVirtualAddress);

        // 4. Image finale à l'écran
        cl.ResourceBarrierTransition(_gpu.BackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
        var backRtv = _gpu.BackBufferRtv;
        cl.OMSetRenderTargets(backRtv);
        cl.ClearRenderTargetView(backRtv, new Color4(0, 0, 0, 1));
        RecordComposite(cl, _compositePso, FitViewport(Width, Height, _gpu.Width, _gpu.Height), _gpu.Width, _gpu.Height, progress);
        cl.ResourceBarrierTransition(_gpu.BackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);
        _gpu.EndFrame();
    }

    /// <summary>
    /// Calcule l'image de contrôle (instant <see cref="CheckTime"/>, sans barre de progression) à la définition interne
    /// et la relit en mémoire : pixels RGBA, ligne après ligne. Attend la carte avant et après (hors mesure).
    /// </summary>
    public byte[] RenderCheck()
    {
        if (_check is null || _readback is null) throw new InvalidOperationException("Configure doit être appelé avant RenderCheck.");
        _gpu.WaitIdle();
        // La carte est au repos : n'importe quel tampon de constantes peut servir
        WriteFrameConstants(0, CheckTime);
        _checkAllocator.Reset();
        var cl = _checkList;
        cl.Reset(_checkAllocator);
        RecordScene(cl, _frameBuffers[0].GPUVirtualAddress);

        _check.Transition(cl, ResourceStates.RenderTarget);
        cl.OMSetRenderTargets(_check.View);
        RecordComposite(cl, _checkPso, _check.FullViewport, Width, Height, 0f);
        _check.Transition(cl, ResourceStates.CopySource);
        cl.CopyTextureRegion(new TextureCopyLocation(_readback, _checkFootprint), 0, 0, 0, new TextureCopyLocation(_check.Resource, 0));
        _check.Transition(cl, ResourceStates.PixelShaderResource);
        _gpu.Execute(cl);

        var rowBytes = Width * 4;
        var pixels = new byte[rowBytes * Height];
        var source = _readback.Map<byte>(0);
        try
        {
            var pitch = (int)_checkFootprint.Footprint.RowPitch;
            for (var y = 0; y < Height; y++)
                new ReadOnlySpan<byte>(source + (long)y * pitch, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
        }
        finally
        {
            _readback.Unmap(0);
        }
        return pixels;
    }

    /// <summary>Étapes 1 à 3 : ombres, scène, effets. Laisse la signature racine des effets en place pour l'image finale.</summary>
    private void RecordScene(ID3D12GraphicsCommandList cl, ulong constants)
    {
        if (_hdr is null || _normalDepth is null || _depth is null || _shadow is null)
            throw new InvalidOperationException("Configure doit être appelé avant le rendu.");
        cl.SetDescriptorHeaps(_srv.Heap);
        cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // 1. Ombres du soleil
        _shadow.Transition(cl, ResourceStates.DepthWrite);
        cl.OMSetRenderTargets(ReadOnlySpan<CpuDescriptorHandle>.Empty, _shadow.View);
        cl.ClearDepthStencilView(_shadow.View, ClearFlags.Depth, 1.0f, 0);
        cl.RSSetViewport(_shadow.FullViewport);
        cl.RSSetScissorRect(_shadow.Width, _shadow.Height);
        cl.SetGraphicsRootSignature(_sceneRoot);
        cl.SetGraphicsRootConstantBufferView(0, constants);
        cl.SetGraphicsRoot32BitConstant(1, 1u, 0);
        DrawTerrain(cl, _terrainShadowPso);
        DrawObjects(cl, _objectsShadowPso);
        _shadow.Transition(cl, ResourceStates.PixelShaderResource);

        // 2. Scène
        _hdr.Transition(cl, ResourceStates.RenderTarget);
        _normalDepth.Transition(cl, ResourceStates.RenderTarget);
        cl.OMSetRenderTargets([_hdr.View, _normalDepth.View], _depth.View);
        cl.ClearDepthStencilView(_depth.View, ClearFlags.Depth, 1.0f, 0);
        cl.RSSetViewport(_hdr.FullViewport);
        cl.RSSetScissorRect(Width, Height);
        cl.SetGraphicsRoot32BitConstant(1, 0u, 0);
        cl.SetGraphicsRootDescriptorTable(2, _srv.Gpu(_shadowTable));
        cl.SetPipelineState(_skyPso);
        cl.DrawInstanced(3, 1, 0, 0);
        DrawTerrain(cl, _terrainPso);
        DrawObjects(cl, _objectsPso);
        _hdr.Transition(cl, ResourceStates.PixelShaderResource);
        _normalDepth.Transition(cl, ResourceStates.PixelShaderResource);

        // 3. Effets
        cl.SetGraphicsRootSignature(_postRoot);
        cl.SetGraphicsRootConstantBufferView(0, constants);
        if (_ao is not null)
            PostPass(cl, _ssaoPso, _ao, _ssaoTable, new PostConstants { Samples = (uint)Preset.SsaoSamples, AoStrength = 1.4f });
        for (var i = 0; i < _bloom.Length; i++)
        {
            var source = i == 0 ? _hdr : _bloom[i - 1];
            PostPass(cl, _bloomDownPso, _bloom[i], _bloomDownTables[i],
                new PostConstants { TexelSize = new Vector2(1f / source.Width, 1f / source.Height), Mode = i == 0 ? 1u : 0u });
        }
        for (var i = _bloom.Length - 1; i > 0; i--)
            PostPass(cl, _bloomUpPso, _bloom[i - 1], _bloomUpTables[i],
                new PostConstants { TexelSize = new Vector2(1f / _bloom[i].Width, 1f / _bloom[i].Height) });
    }

    /// <summary>Image finale (tonemapping + barre de progression) dans la cible déjà en place.</summary>
    private void RecordComposite(ID3D12GraphicsCommandList cl, ID3D12PipelineState pso, Viewport viewport, int scissorWidth, int scissorHeight, float progress)
    {
        cl.RSSetViewport(viewport);
        cl.RSSetScissorRect(scissorWidth, scissorHeight);
        cl.SetPipelineState(pso);
        cl.SetGraphicsRootDescriptorTable(1, _srv.Gpu(_compositeTable));
        var composite = new PostConstants
        {
            Progress = Math.Clamp(progress, 0f, 1f),
            BloomStrength = 0.08f,
            Exposure = 1.0f,
            Mode = (_ao is not null ? 1u : 0u) | (_bloom.Length > 0 ? 2u : 0u)
        };
        cl.SetGraphicsRoot32BitConstants(2, 8, &composite, 0);
        cl.DrawInstanced(3, 1, 0, 0);
    }

    private void PostPass(ID3D12GraphicsCommandList cl, ID3D12PipelineState pso, RenderTexture target, int table, PostConstants values)
    {
        target.Transition(cl, ResourceStates.RenderTarget);
        cl.OMSetRenderTargets(target.View);
        cl.RSSetViewport(target.FullViewport);
        cl.RSSetScissorRect(target.Width, target.Height);
        cl.SetPipelineState(pso);
        cl.SetGraphicsRootDescriptorTable(1, _srv.Gpu(table));
        cl.SetGraphicsRoot32BitConstants(2, 8, &values, 0);
        cl.DrawInstanced(3, 1, 0, 0);
        target.Transition(cl, ResourceStates.PixelShaderResource);
    }

    private void DrawTerrain(ID3D12GraphicsCommandList cl, ID3D12PipelineState pso)
    {
        cl.SetPipelineState(pso);
        cl.IASetIndexBuffer(_terrainIndices!.GPUVirtualAddress, _terrainIndexCount * sizeof(uint), Format.R32_UInt);
        cl.DrawIndexedInstanced(_terrainIndexCount, 1, 0, 0, 0);
    }

    private void DrawObjects(ID3D12GraphicsCommandList cl, ID3D12PipelineState pso)
    {
        cl.SetPipelineState(pso);
        cl.IASetVertexBuffers(0, _cubeVertexView);
        cl.IASetIndexBuffer(_cubeIndices.GPUVirtualAddress, _cubeIndexCount * sizeof(ushort), Format.R16_UInt);
        cl.DrawIndexedInstanced(_cubeIndexCount, (uint)Preset.ObjectCount, 0, 0, 0);
    }

    private void WriteFrameConstants(int frame, float time)
    {
        var header = BuildHeader(Preset, time, (float)Width / Height);
        var data = _frameData[frame];
        *(FrameHeader*)data = header;
        var lights = (Vector4*)(data + FrameHeader.Size);
        for (var i = 0; i < Preset.LightCount; i++)
        {
            var (position, radius, color, intensity) = Light(i, Preset.LightCount, time);
            lights[i * 2] = new Vector4(position, radius);
            lights[i * 2 + 1] = new Vector4(color, intensity);
        }
    }

    /// <summary>Caméra en survol lent de la ville, soleil de fin de journée (ombres longues).</summary>
    internal static FrameHeader BuildHeader(QualityPreset preset, float time, float aspect)
    {
        var angle = time * 0.05f + 0.6f;
        const float radius = 130f;
        var eye = new Vector3(MathF.Cos(angle) * radius, 55f, MathF.Sin(angle) * radius);
        var target = new Vector3(MathF.Cos(angle + 0.35f) * radius * 0.35f, 8f, MathF.Sin(angle + 0.35f) * radius * 0.35f);
        var viewProj = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY)
                       * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, 0.5f, 2000f);
        Matrix4x4.Invert(viewProj, out var inverse);
        var lightViewProj = Matrix4x4.CreateLookAt(SunDirection * 900f, Vector3.Zero, Vector3.UnitY)
                            * Matrix4x4.CreateOrthographic(1500f, 1500f, 1f, 2000f);  // couvre la diagonale du terrain vue du soleil

        return new FrameHeader
        {
            ViewProj = viewProj,
            InvViewProj = inverse,
            LightViewProj = lightViewProj,
            Eye = eye,
            Time = time,
            SunDir = SunDirection,
            TerrainSize = TerrainSize,
            SunColor = new Vector3(3.0f, 2.4f, 1.8f),
            TerrainHeight = TerrainHeight,
            LightsCenter = Vector3.Zero,
            FogDensity = preset.FogDensity,
            LightCount = (uint)Math.Min(preset.LightCount, QualityPreset.MaxLights),
            Octaves = (uint)preset.Octaves,
            PcfRadius = (uint)preset.PcfRadius,
            TerrainRes = (uint)preset.TerrainResolution,
            ObjectGrid = (uint)preset.ObjectGrid,
            ShadowTexel = 1f / preset.ShadowMapSize
        };
    }

    private static readonly Vector3[] NeonColors =
        [new(0.1f, 0.8f, 1.0f), new(1.0f, 0.2f, 0.7f), new(1.0f, 0.55f, 0.1f), new(0.6f, 0.3f, 1.0f)];

    /// <summary>Lumières néon réparties en spirale au-dessus de la ville, qui tournent lentement.</summary>
    internal static (Vector3 Position, float Radius, Vector3 Color, float Intensity) Light(int index, int count, float time)
    {
        var distance = 160f * MathF.Sqrt((index + 0.5f) / count);
        var angle = index * 2.3999632f + time * (0.15f + 0.05f * (index % 3));
        var height = 10f + 8f * (index * 0.618034f % 1f) + 3f * MathF.Sin(time * 0.7f + index);
        return (new Vector3(MathF.Cos(angle) * distance, height, MathF.Sin(angle) * distance), 38f, NeonColors[index % NeonColors.Length], 9f);
    }

    /// <summary>Plus grand rectangle de même proportion que le rendu, centré dans l'écran.</summary>
    internal static Viewport FitViewport(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var scale = Math.Min((float)targetWidth / sourceWidth, (float)targetHeight / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new Viewport((targetWidth - width) / 2f, (targetHeight - height) / 2f, width, height);
    }

    private void DisposeTargets()
    {
        _hdr?.Dispose();
        _normalDepth?.Dispose();
        _depth?.Dispose();
        _shadow?.Dispose();
        _ao?.Dispose();
        foreach (var b in _bloom) b.Dispose();
        _check?.Dispose();
        _readback?.Dispose();
        _readback = null;
        _hdr = _normalDepth = _depth = _shadow = _ao = _check = null;
        _bloom = [];
    }

    public void Dispose()
    {
        // Les images encore en vol utilisent ces ressources : on attend la carte avant de les libérer
        try { _gpu.WaitIdle(); } catch (BenchException) { /* carte déjà perdue */ }
        DisposeTargets();
        _terrainIndices?.Dispose();
        foreach (var b in _frameBuffers) { b.Unmap(0); b.Dispose(); }
        _cubeVertices.Dispose();
        _cubeIndices.Dispose();
        _rtv.Dispose();
        _dsv.Dispose();
        _srv.Dispose();
        foreach (var pso in new[] { _skyPso, _terrainPso, _objectsPso, _terrainShadowPso, _objectsShadowPso, _ssaoPso, _bloomDownPso, _bloomUpPso, _compositePso, _checkPso })
            pso.Dispose();
        _checkList.Dispose();
        _checkAllocator.Dispose();
        _sceneRoot.Dispose();
        _postRoot.Dispose();
    }
}
