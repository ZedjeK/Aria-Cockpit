using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Aria.Bench.Rendering;

/// <summary>
/// Structures d'accélération du ray tracing (DXR 1.1) pour le préréglage Ultra RT : une BLAS pour le terrain, une pour le
/// cube, puis une TLAS avec une instance par tour ou rocher et une pour le terrain. La scène ne bouge pas : tout est
/// construit une fois, à la configuration. Sommets du terrain et placement des objets viennent du shader CSBuildRt, qui
/// reprend les fonctions du rendu : les rayons voient exactement la scène dessinée.
/// </summary>
internal sealed unsafe class RayTracingScene : IDisposable
{
    /// <summary>Taille d'une description d'instance DXR (D3D12_RAYTRACING_INSTANCE_DESC).</summary>
    private const int InstanceSize = 64;
    private const int TerrainVertexSize = 12;

    private readonly List<ID3D12Resource> _resources = [];

    /// <summary>Adresse de la TLAS, liée au registre t1 des shaders de la scène.</summary>
    public ulong TlasAddress { get; }

    /// <param name="frameConstants">Constantes de la scène déjà écrites pour ce préréglage (grille, terrain).</param>
    public RayTracingScene(GpuDevice gpu, ID3D12RootSignature buildRoot, ID3D12PipelineState buildPso, ulong frameConstants, QualityPreset preset,
        ID3D12Resource terrainIndices, uint terrainIndexCount, ID3D12Resource cubeVertices, uint cubeVertexCount, ID3D12Resource cubeIndices,
        uint cubeIndexCount)
    {
        using var device = gpu.Device.QueryInterface<ID3D12Device5>();
        var row = (uint)preset.TerrainResolution + 1;
        var vertexCount = row * row;
        var instanceCount = (uint)preset.ObjectCount + 1;

        var vertices = Keep(Buffer(device, (ulong)vertexCount * TerrainVertexSize, ResourceStates.UnorderedAccess, "RT : sommets du terrain"));
        var instances = Keep(Buffer(device, (ulong)instanceCount * InstanceSize, ResourceStates.UnorderedAccess, "RT : instances"));

        var terrainInputs = BottomLevel(new RaytracingGeometryTrianglesDescription(
            new GpuVirtualAddressAndStride(vertices.GPUVirtualAddress, TerrainVertexSize), Format.R32G32B32_Float, vertexCount, 0,
            terrainIndices.GPUVirtualAddress, Format.R32_UInt, terrainIndexCount));
        var cubeInputs = BottomLevel(new RaytracingGeometryTrianglesDescription(
            new GpuVirtualAddressAndStride(cubeVertices.GPUVirtualAddress, Vertex.Size), Format.R32G32B32_Float, cubeVertexCount, 0,
            cubeIndices.GPUVirtualAddress, Format.R16_UInt, cubeIndexCount));
        var topInputs = new BuildRaytracingAccelerationStructureInputs
        {
            Type = RaytracingAccelerationStructureType.TopLevel,
            Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
            Layout = ElementsLayout.Array,
            DescriptorsCount = instanceCount,
            InstanceDescriptions = instances.GPUVirtualAddress
        };

        var (terrainBlas, terrainScratch) = Allocate(device, terrainInputs, "RT : BLAS du terrain");
        var (cubeBlas, cubeScratch) = Allocate(device, cubeInputs, "RT : BLAS du cube");
        var (tlas, topScratch) = Allocate(device, topInputs, "RT : TLAS");
        TlasAddress = tlas.GPUVirtualAddress;

        using var allocator = device.CreateCommandAllocator(CommandListType.Direct);
        using var list = device.CreateCommandList<ID3D12GraphicsCommandList4>(CommandListType.Direct, allocator);

        // 1. Sommets du terrain et instances, calculés par la carte avec les fonctions du rendu
        list.SetComputeRootSignature(buildRoot);
        list.SetPipelineState(buildPso);
        list.SetComputeRootConstantBufferView(0, frameConstants);
        var blas = stackalloc uint[4]
        {
            (uint)terrainBlas.GPUVirtualAddress, (uint)(terrainBlas.GPUVirtualAddress >> 32),
            (uint)cubeBlas.GPUVirtualAddress, (uint)(cubeBlas.GPUVirtualAddress >> 32)
        };
        list.SetComputeRoot32BitConstants(1, 4, blas, 0);
        list.SetComputeRootUnorderedAccessView(2, instances.GPUVirtualAddress);
        list.SetComputeRootUnorderedAccessView(3, vertices.GPUVirtualAddress);
        list.Dispatch((Math.Max(vertexCount, instanceCount) + 63) / 64, 1, 1);

        // 2. BLAS : les tampons de géométrie doivent être lisibles par la construction (NON_PIXEL_SHADER_RESOURCE)
        list.ResourceBarrierTransition(vertices, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        list.ResourceBarrierTransition(instances, ResourceStates.UnorderedAccess, ResourceStates.NonPixelShaderResource);
        list.ResourceBarrierTransition(terrainIndices, ResourceStates.IndexBuffer, ResourceStates.NonPixelShaderResource);
        list.ResourceBarrierTransition(cubeVertices, ResourceStates.VertexAndConstantBuffer, ResourceStates.NonPixelShaderResource);
        list.ResourceBarrierTransition(cubeIndices, ResourceStates.IndexBuffer, ResourceStates.NonPixelShaderResource);
        list.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(
            terrainBlas.GPUVirtualAddress, terrainInputs, 0, terrainScratch.GPUVirtualAddress));
        list.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(
            cubeBlas.GPUVirtualAddress, cubeInputs, 0, cubeScratch.GPUVirtualAddress));
        list.ResourceBarrierUnorderedAccessView(terrainBlas);
        list.ResourceBarrierUnorderedAccessView(cubeBlas);

        // 3. TLAS, puis les tampons du rendu reprennent leur état habituel
        list.BuildRaytracingAccelerationStructure(new BuildRaytracingAccelerationStructureDescription(
            tlas.GPUVirtualAddress, topInputs, 0, topScratch.GPUVirtualAddress));
        list.ResourceBarrierUnorderedAccessView(tlas);
        list.ResourceBarrierTransition(terrainIndices, ResourceStates.NonPixelShaderResource, ResourceStates.IndexBuffer);
        list.ResourceBarrierTransition(cubeVertices, ResourceStates.NonPixelShaderResource, ResourceStates.VertexAndConstantBuffer);
        list.ResourceBarrierTransition(cubeIndices, ResourceStates.NonPixelShaderResource, ResourceStates.IndexBuffer);
        gpu.Execute(list);

        // Les tampons de travail ne servent plus ; les sommets et instances restent référencés par les BLAS et la TLAS
        terrainScratch.Dispose();
        cubeScratch.Dispose();
        topScratch.Dispose();
    }

    private static BuildRaytracingAccelerationStructureInputs BottomLevel(RaytracingGeometryTrianglesDescription triangles) => new()
    {
        Type = RaytracingAccelerationStructureType.BottomLevel,
        Flags = RaytracingAccelerationStructureBuildFlags.PreferFastTrace,
        Layout = ElementsLayout.Array,
        DescriptorsCount = 1,
        GeometryDescriptions = [new RaytracingGeometryDescription(triangles, RaytracingGeometryFlags.Opaque)]
    };

    /// <summary>Structure d'accélération (gardée) et son tampon de travail (libéré après la construction).</summary>
    private (ID3D12Resource Structure, ID3D12Resource Scratch) Allocate(ID3D12Device5 device, BuildRaytracingAccelerationStructureInputs inputs, string name)
    {
        var info = device.GetRaytracingAccelerationStructurePrebuildInfo(inputs);
        var structure = Keep(Buffer(device, info.ResultDataMaxSizeInBytes, ResourceStates.RaytracingAccelerationStructure, name));
        var scratch = Buffer(device, info.ScratchDataSizeInBytes, ResourceStates.UnorderedAccess, $"{name} (travail)");
        return (structure, scratch);
    }

    private static ID3D12Resource Buffer(ID3D12Device5 device, ulong bytes, ResourceStates state, string name)
    {
        var buffer = device.CreateCommittedResource(HeapType.Default, ResourceDescription.Buffer(Math.Max(bytes, 256), ResourceFlags.AllowUnorderedAccess), state);
        buffer.Name = name;
        return buffer;
    }

    private ID3D12Resource Keep(ID3D12Resource resource)
    {
        _resources.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        foreach (var resource in _resources) resource.Dispose();
        _resources.Clear();
    }
}
