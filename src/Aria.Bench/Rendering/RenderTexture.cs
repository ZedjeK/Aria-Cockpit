using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Aria.Bench.Rendering;

/// <summary>Texture de rendu avec son état courant : les transitions inutiles sont évitées, les oublis impossibles.</summary>
internal sealed class RenderTexture : IDisposable
{
    public ID3D12Resource Resource { get; }
    public ResourceStates State { get; private set; }
    public Format Format { get; }
    public int Width { get; }
    public int Height { get; }
    public CpuDescriptorHandle View { get; }   // RTV, ou DSV pour une profondeur

    private RenderTexture(ID3D12Resource resource, ResourceStates state, Format format, int width, int height, CpuDescriptorHandle view)
    {
        Resource = resource;
        State = state;
        Format = format;
        Width = width;
        Height = height;
        View = view;
    }

    public static RenderTexture Color(ID3D12Device device, string name, Format format, int width, int height, CpuDescriptorHandle rtv, Color4 clear)
    {
        var resource = device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(format, (uint)width, (uint)height, 1, 1, flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.PixelShaderResource, new ClearValue(format, clear));
        resource.Name = name;
        device.CreateRenderTargetView(resource, null, rtv);
        return new RenderTexture(resource, ResourceStates.PixelShaderResource, format, width, height, rtv);
    }

    /// <param name="typeless">Format sans type pour la ressource (si on doit aussi la lire), sinon le format de profondeur.</param>
    public static RenderTexture Depth(ID3D12Device device, string name, Format depthFormat, Format typeless, int width, int height,
        CpuDescriptorHandle dsv, ResourceStates initial)
    {
        var resource = device.CreateCommittedResource(HeapType.Default,
            ResourceDescription.Texture2D(typeless, (uint)width, (uint)height, 1, 1, flags: ResourceFlags.AllowDepthStencil),
            initial, new ClearValue(depthFormat, 1.0f, 0));
        resource.Name = name;
        device.CreateDepthStencilView(resource, new DepthStencilViewDescription
        {
            Format = depthFormat,
            ViewDimension = DepthStencilViewDimension.Texture2D
        }, dsv);
        return new RenderTexture(resource, initial, typeless, width, height, dsv);
    }

    public void Transition(ID3D12GraphicsCommandList list, ResourceStates to)
    {
        if (State == to) return;
        list.ResourceBarrierTransition(Resource, State, to);
        State = to;
    }

    public Viewport FullViewport => new(0, 0, Width, Height);

    public void Dispose() => Resource.Dispose();
}

/// <summary>Tas de descripteurs avec attribution séquentielle, remis à zéro quand on reconstruit les cibles.</summary>
internal sealed class DescriptorAllocator : IDisposable
{
    private readonly uint _increment;
    private int _next;

    public ID3D12DescriptorHeap Heap { get; }
    public int Capacity { get; }

    public DescriptorAllocator(ID3D12Device device, DescriptorHeapType type, int capacity, bool shaderVisible = false)
    {
        Capacity = capacity;
        Heap = device.CreateDescriptorHeap(new DescriptorHeapDescription(type, (uint)capacity,
            shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None));
        _increment = device.GetDescriptorHandleIncrementSize(type);
    }

    public void Reset() => _next = 0;

    /// <summary>Réserve <paramref name="count"/> descripteurs consécutifs ; renvoie l'indice du premier.</summary>
    public int Allocate(int count = 1)
    {
        if (_next + count > Capacity) throw new InvalidOperationException($"Tas de descripteurs plein ({Capacity}).");
        var index = _next;
        _next += count;
        return index;
    }

    public CpuDescriptorHandle Cpu(int index) => Heap.GetCPUDescriptorHandleForHeapStart() + index * (int)_increment;
    public GpuDescriptorHandle Gpu(int index) => Heap.GetGPUDescriptorHandleForHeapStart() + index * (int)_increment;

    public void Dispose() => Heap.Dispose();
}
