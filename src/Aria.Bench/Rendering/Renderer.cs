using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Aria.Bench.Rendering;

/// <summary>
/// Dessine une image : la scène dans une texture à la définition demandée (1080p, 1440p ou 4K),
/// puis la recopie à la taille de l'écran, en gardant le format 16:9 (bandes noires si besoin).
/// </summary>
internal sealed unsafe class Renderer : IDisposable
{
    private const Format SceneFormat = Format.R16G16B16A16_Float;
    private const Format DepthFormat = Format.D32_Float;
    private const int GridSize = 48;          // 48 × 48 = 2 304 colonnes
    private const float Spacing = 2.0f;
    private static readonly Color4 Background = new(0.02f, 0.02f, 0.05f, 1f);

    private readonly GpuDevice _gpu;
    private readonly ID3D12RootSignature _sceneRoot, _blitRoot;
    private readonly ID3D12PipelineState _scenePso, _blitPso;
    private readonly ID3D12Resource _vertexBuffer, _indexBuffer;
    private readonly VertexBufferView _vertexView;
    private readonly uint _indexCount;
    private readonly ID3D12DescriptorHeap _rtvHeap, _dsvHeap, _srvHeap;
    private ID3D12Resource? _scene, _depth;

    public int Width { get; private set; }
    public int Height { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SceneConstants
    {
        public Matrix4x4 ViewProj;
        public Vector3 Eye;
        public float Time;
        public float GridSize;
        public float Spacing;
        public float Pad0, Pad1;
    }

    public Renderer(GpuDevice gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;

        var (sceneVs, scenePs) = ShaderLibrary.CompilePair("Scene");
        var (blitVs, blitPs) = ShaderLibrary.CompilePair("Blit");
        // Les signatures racines sont écrites dans les shaders ([RootSignature]) et lues dans le code compilé
        _sceneRoot = device.CreateRootSignature(sceneVs);
        _blitRoot = device.CreateRootSignature(blitVs);

        _scenePso = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _sceneRoot,
            VertexShader = sceneVs,
            PixelShader = scenePs,
            InputLayout = new InputLayoutDescription(
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0)),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            SampleMask = uint.MaxValue,
            RenderTargetFormats = [SceneFormat],
            DepthStencilFormat = DepthFormat,
            SampleDescription = new SampleDescription(1, 0)
        });
        _scenePso.Name = "Scène";

        _blitPso = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _blitRoot,
            VertexShader = blitVs,
            PixelShader = blitPs,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.None,
            SampleMask = uint.MaxValue,
            RenderTargetFormats = [Format.R8G8B8A8_UNorm_SRgb],
            DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0)
        });
        _blitPso.Name = "Recopie à l'écran";

        // Géométrie : un cube, lu directement en mémoire partagée (suffisant pour 24 sommets ; la vraie scène passera en mémoire vidéo)
        var (vertices, indices) = CubeMesh.Build();
        _vertexBuffer = device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer((ulong)(vertices.Length * Vertex.Size)), ResourceStates.GenericRead);
        _vertexBuffer.SetData(vertices);
        _vertexBuffer.Name = "Sommets du cube";
        _indexBuffer = device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer((ulong)(indices.Length * sizeof(ushort))), ResourceStates.GenericRead);
        _indexBuffer.SetData(indices);
        _indexBuffer.Name = "Indices du cube";
        _vertexView = new VertexBufferView(_vertexBuffer.GPUVirtualAddress, (uint)(vertices.Length * Vertex.Size), Vertex.Size);
        _indexCount = (uint)indices.Length;

        _rtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1));
        _dsvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1));
        _srvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 1, DescriptorHeapFlags.ShaderVisible));
    }

    /// <summary>Change la définition du rendu interne (la carte doit avoir fini l'image en cours).</summary>
    public void Resize(int width, int height)
    {
        _gpu.WaitIdle();
        _scene?.Dispose();
        _depth?.Dispose();
        Width = width;
        Height = height;
        var device = _gpu.Device;

        _scene = device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(SceneFormat, (uint)width, (uint)height, 1, 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget, new ClearValue(SceneFormat, Background));
        _scene.Name = $"Rendu interne {width}×{height}";
        _depth = device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(DepthFormat, (uint)width, (uint)height, 1, 1, flags: ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite, new ClearValue(DepthFormat, 1.0f, 0));
        _depth.Name = $"Profondeur {width}×{height}";

        device.CreateRenderTargetView(_scene, null, _rtvHeap.GetCPUDescriptorHandleForHeapStart());
        device.CreateDepthStencilView(_depth, null, _dsvHeap.GetCPUDescriptorHandleForHeapStart());
        device.CreateShaderResourceView(_scene, null, _srvHeap.GetCPUDescriptorHandleForHeapStart());
    }

    /// <param name="time">Temps de la scène en secondes (le même d'une passe à l'autre, pour comparer).</param>
    /// <param name="progress">Avancement global de 0 à 1, affiché par la barre du bas.</param>
    public void Render(float time, float progress)
    {
        if (_scene is null || _depth is null) throw new InvalidOperationException("Resize doit être appelé avant Render.");
        var cl = _gpu.BeginFrame();

        // 1. La scène, à la définition interne
        var rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        var dsv = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        cl.OMSetRenderTargets(rtv, dsv);
        cl.ClearRenderTargetView(rtv, Background);
        cl.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0);
        cl.RSSetViewport(new Viewport(0, 0, Width, Height));
        cl.RSSetScissorRect(Width, Height);
        cl.SetGraphicsRootSignature(_sceneRoot);
        cl.SetPipelineState(_scenePso);

        var constants = BuildConstants(time, (float)Width / Height);
        cl.SetGraphicsRoot32BitConstants(0, 24, &constants, 0);
        cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cl.IASetVertexBuffers(0, _vertexView);
        cl.IASetIndexBuffer(_indexBuffer.GPUVirtualAddress, _indexCount * sizeof(ushort), Format.R16_UInt);
        cl.DrawIndexedInstanced(_indexCount, GridSize * GridSize, 0, 0, 0);

        // 2. Recopie à l'écran
        cl.ResourceBarrierTransition(_scene, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
        cl.ResourceBarrierTransition(_gpu.BackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
        var backRtv = _gpu.BackBufferRtv;
        cl.OMSetRenderTargets(backRtv);
        cl.ClearRenderTargetView(backRtv, new Color4(0, 0, 0, 1));
        cl.RSSetViewport(FitViewport(Width, Height, _gpu.Width, _gpu.Height));
        cl.RSSetScissorRect(_gpu.Width, _gpu.Height);
        cl.SetGraphicsRootSignature(_blitRoot);
        cl.SetPipelineState(_blitPso);
        cl.SetDescriptorHeaps(_srvHeap);
        cl.SetGraphicsRootDescriptorTable(0, _srvHeap.GetGPUDescriptorHandleForHeapStart());
        var blit = new Vector4(Math.Clamp(progress, 0f, 1f), 0, 0, 0);
        cl.SetGraphicsRoot32BitConstants(1, 4, &blit, 0);
        cl.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cl.DrawInstanced(3, 1, 0, 0);

        cl.ResourceBarrierTransition(_gpu.BackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);
        cl.ResourceBarrierTransition(_scene, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
        _gpu.EndFrame();
    }

    /// <summary>Caméra en orbite lente au-dessus de la grille.</summary>
    private static SceneConstants BuildConstants(float time, float aspect)
    {
        var angle = time * 0.15f;
        var eye = new Vector3(MathF.Cos(angle) * 45f, 18f, MathF.Sin(angle) * 45f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0, 2, 0), Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, aspect, 0.1f, 300f);
        return new SceneConstants
        {
            ViewProj = view * projection,
            Eye = eye,
            Time = time,
            GridSize = GridSize,
            Spacing = Spacing
        };
    }

    /// <summary>Plus grand rectangle de même proportion que le rendu, centré dans l'écran.</summary>
    internal static Viewport FitViewport(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var scale = Math.Min((float)targetWidth / sourceWidth, (float)targetHeight / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        return new Viewport((targetWidth - width) / 2f, (targetHeight - height) / 2f, width, height);
    }

    public void Dispose()
    {
        // Les images encore en vol utilisent ces ressources : on attend la carte avant de les libérer
        try { _gpu.WaitIdle(); } catch (BenchException) { /* carte déjà perdue */ }
        _scene?.Dispose();
        _depth?.Dispose();
        _rtvHeap.Dispose();
        _dsvHeap.Dispose();
        _srvHeap.Dispose();
        _vertexBuffer.Dispose();
        _indexBuffer.Dispose();
        _scenePso.Dispose();
        _blitPso.Dispose();
        _sceneRoot.Dispose();
        _blitRoot.Dispose();
    }
}
