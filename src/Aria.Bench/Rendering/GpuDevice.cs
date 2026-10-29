using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

namespace Aria.Bench.Rendering;

/// <summary>
/// Le socle Direct3D 12 : choix de la carte (NVIDIA en priorité, jamais la puce intégrée si une carte dédiée existe),
/// file de commandes, chaîne d'affichage à 3 tampons sans V-Sync, et synchronisation processeur ↔ carte.
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    public const int BufferCount = 3;
    public const Format BackBufferFormat = Format.R8G8B8A8_UNorm;
    private const uint NvidiaVendorId = 0x10DE;
    private static readonly TimeSpan GpuTimeout = TimeSpan.FromSeconds(10);

    private readonly IDXGIFactory6 _factory;
    private readonly IDXGIAdapter1 _adapter;
    private readonly ID3D12CommandAllocator[] _allocators = new ID3D12CommandAllocator[BufferCount];
    private readonly ID3D12Resource[] _backBuffers = new ID3D12Resource[BufferCount];
    private readonly ulong[] _frameFenceValues = new ulong[BufferCount];
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly uint _rtvSize;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private readonly bool _tearing;
    private readonly ID3D12InfoQueue? _infoQueue;
    private ulong _fenceCounter;

    public ID3D12Device Device { get; }
    public ID3D12CommandQueue Queue { get; }
    public IDXGISwapChain3 SwapChain { get; }
    public ID3D12GraphicsCommandList CommandList { get; }
    public GpuInfo Info { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameIndex => (int)SwapChain.CurrentBackBufferIndex;
    public ID3D12Resource BackBuffer => _backBuffers[FrameIndex];
    public CpuDescriptorHandle BackBufferRtv => _rtvHeap.GetCPUDescriptorHandleForHeapStart() + FrameIndex * (int)_rtvSize;

    public GpuDevice(IntPtr hwnd, int width, int height, bool debug, ICollection<string> notes)
    {
        Width = width;
        Height = height;

        if (debug)
        {
            if (D3D12.D3D12GetDebugInterface(out ID3D12Debug? debugLayer).Success && debugLayer is not null)
            {
                debugLayer.EnableDebugLayer();
                debugLayer.Dispose();
                notes.Add("Couche de diagnostic Direct3D activée (--debug) : les performances mesurées ne sont pas représentatives.");
            }
            else
            {
                debug = false;
                notes.Add("Couche de diagnostic Direct3D absente (fonctionnalité facultative « Outils graphiques » de Windows) : --debug ignoré.");
            }
        }

        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory6>(debug);
        (_adapter, var all) = PickAdapter(_factory);
        var desc = _adapter.Description1;

        if (D3D12.D3D12CreateDevice(_adapter, FeatureLevel.Level_12_0, out ID3D12Device? device).Failure || device is null)
            throw new BenchException($"La carte « {desc.Description.Trim()} » ne prend pas en charge Direct3D 12 (niveau 12_0 requis).");
        Device = device;
        Device.Name = "Aria-Bench";
        if (debug) _infoQueue = Device.QueryInterfaceOrNull<ID3D12InfoQueue>();

        Info = Describe(_adapter, desc, all);

        Queue = Device.CreateCommandQueue(CommandListType.Direct);
        Queue.Name = "Aria-Bench file directe";

        // Affichage sans V-Sync : il faut l'autorisation de « tearing » (Windows 10 1809+, pilotes récents)
        _tearing = _factory.PresentAllowTearing;
        if (!_tearing) notes.Add("Affichage sans V-Sync non pris en charge : les FPS sont plafonnés à la fréquence de l'écran.");

        var swapDesc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = BackBufferFormat,
            BufferCount = BufferCount,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = _tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None
        };
        using (var swapChain1 = _factory.CreateSwapChainForHwnd(Queue, hwnd, swapDesc))
        {
            SwapChain = swapChain1.QueryInterface<IDXGISwapChain3>();
        }
        _factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        // Vues des tampons d'affichage en sRGB : les shaders calculent en linéaire, la conversion est faite à l'écriture
        _rtvHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, BufferCount));
        _rtvSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        var rtvDesc = new RenderTargetViewDescription { Format = Format.R8G8B8A8_UNorm_SRgb, ViewDimension = RenderTargetViewDimension.Texture2D };
        for (var i = 0; i < BufferCount; i++)
        {
            _backBuffers[i] = SwapChain.GetBuffer<ID3D12Resource>((uint)i);
            _backBuffers[i].Name = $"Tampon d'affichage {i}";
            Device.CreateRenderTargetView(_backBuffers[i], rtvDesc, _rtvHeap.GetCPUDescriptorHandleForHeapStart() + i * (int)_rtvSize);
            _allocators[i] = Device.CreateCommandAllocator(CommandListType.Direct);
        }

        CommandList = Device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _allocators[0]);
        CommandList.Close();
        _fence = Device.CreateFence(0);
    }

    /// <summary>Attend que le tampon d'affichage courant soit libre, puis ouvre la liste de commandes de l'image.</summary>
    public ID3D12GraphicsCommandList BeginFrame()
    {
        var index = FrameIndex;
        WaitForFence(_frameFenceValues[index], "l'image précédente");
        _allocators[index].Reset();
        CommandList.Reset(_allocators[index]);
        return CommandList;
    }

    /// <summary>Envoie la liste à la carte et affiche l'image, sans attendre la synchronisation verticale.</summary>
    public void EndFrame()
    {
        var index = FrameIndex;
        CommandList.Close();
        Queue.ExecuteCommandList(CommandList);
        Check(SwapChain.Present(0, _tearing ? PresentFlags.AllowTearing : PresentFlags.None), "l'affichage de l'image");
        _frameFenceValues[index] = Signal();
    }

    /// <summary>
    /// Tampon en mémoire vidéo rempli avec <paramref name="data"/> : copie depuis un tampon intermédiaire,
    /// puis passage à l'état d'utilisation. Attend la fin de la copie (à faire hors mesure).
    /// </summary>
    public ID3D12Resource CreateBuffer<T>(T[] data, ResourceStates finalState, string name) where T : unmanaged
    {
        var size = (ulong)(data.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        var buffer = Device.CreateCommittedResource(HeapType.Default, ResourceDescription.Buffer(size), ResourceStates.Common);
        buffer.Name = name;
        using var staging = Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(size), ResourceStates.GenericRead);
        staging.SetData(data);

        using var allocator = Device.CreateCommandAllocator(CommandListType.Direct);
        using var list = Device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, allocator);
        // Un tampon passe seul de COMMON à COPY_DEST à la copie (promotion implicite)
        list.CopyBufferRegion(buffer, 0, staging, 0, size);
        list.ResourceBarrierTransition(buffer, ResourceStates.CopyDest, finalState);
        list.Close();
        Queue.ExecuteCommandList(list);
        WaitIdle();
        return buffer;
    }

    /// <summary>Exécute une liste ponctuelle (hors affichage) et attend qu'elle soit terminée.</summary>
    public void Execute(ID3D12GraphicsCommandList list)
    {
        list.Close();
        Queue.ExecuteCommandList(list);
        WaitIdle();
    }

    /// <summary>
    /// Un second device sur la même carte, sans chaîne d'affichage : les contrôles de stabilité y tournent à part du rendu,
    /// comme dans l'outil d'origine (Test VRAM discriminant).
    /// </summary>
    public ID3D12Device CreateSeparateDevice()
    {
        if (D3D12.D3D12CreateDevice(_adapter, FeatureLevel.Level_12_0, out ID3D12Device? device).Failure || device is null)
            throw new BenchException("Impossible de créer un second accès Direct3D 12 à la carte pour le contrôle de stabilité.");
        device.Name = "Aria-Bench contrôle de stabilité";
        return device;
    }

    /// <summary>Mémoire vidéo dédiée : budget accordé par Windows et part déjà utilisée (octets).</summary>
    public (ulong Budget, ulong Usage) LocalMemory()
    {
        using var adapter = _adapter.QueryInterface<IDXGIAdapter3>();
        var info = adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
        return (info.Budget, info.CurrentUsage);
    }

    /// <summary>Attend que la carte ait fini tout le travail envoyé.</summary>
    public void WaitIdle() => WaitForFence(Signal(), "la fin du travail en cours");

    /// <summary>Messages de la couche de diagnostic (seulement avec --debug).</summary>
    public IEnumerable<string> DrainDebugMessages()
    {
        if (_infoQueue is null) yield break;
        var count = _infoQueue.NumStoredMessages;
        for (ulong i = 0; i < count; i++)
        {
            var message = _infoQueue.GetMessage(i);
            if (message.Severity <= MessageSeverity.Warning)
                yield return $"{message.Severity} · {message.Id} · {message.Description}";
        }
        _infoQueue.ClearStoredMessages();
    }

    private ulong Signal()
    {
        var value = ++_fenceCounter;
        Check(Queue.Signal(_fence, value), "la synchronisation avec la carte");
        return value;
    }

    private void WaitForFence(ulong value, string what)
    {
        if (_fence.CompletedValue >= value) return;
        Check(_fence.SetEventOnCompletion(value, _fenceEvent), "la synchronisation avec la carte");
        if (!_fenceEvent.WaitOne(GpuTimeout))
            throw new BenchException($"La carte ne répond plus : plus de {GpuTimeout.TotalSeconds:0} s d'attente pour {what}. {RemovedReason()}".Trim());
    }

    /// <summary>Un échec à l'affichage ou à la synchronisation veut presque toujours dire que le pilote a lâché.</summary>
    private void Check(Result result, string what)
    {
        if (result.Success) return;
        throw new BenchException($"Échec pendant {what} : {Explain(result.Code)}. {RemovedReason()}".Trim());
    }

    private string RemovedReason()
    {
        var reason = Device.DeviceRemovedReason;
        return reason.Success ? "" : $"Raison donnée par Windows : {Explain(reason.Code)}.";
    }

    internal static string Explain(int hresult) => unchecked((uint)hresult) switch
    {
        0x887A0005 => "la carte a été retirée (DXGI_ERROR_DEVICE_REMOVED), généralement un plantage du pilote",
        0x887A0006 => "la carte ne répond plus (DXGI_ERROR_DEVICE_HUNG), souvent un réglage instable",
        0x887A0007 => "la carte a été réinitialisée (DXGI_ERROR_DEVICE_RESET)",
        0x887A0020 => "erreur interne du pilote (DXGI_ERROR_DRIVER_INTERNAL_ERROR)",
        0x8007000E => "mémoire insuffisante (E_OUTOFMEMORY)",
        var code => $"code 0x{code:X8}"
    };

    /// <summary>Carte haute performance d'abord (ordre donné par Windows), et parmi elles NVIDIA en priorité.</summary>
    private static (IDXGIAdapter1 Chosen, List<string> All) PickAdapter(IDXGIFactory6 factory)
    {
        var candidates = new List<IDXGIAdapter1>();
        var all = new List<string>();
        for (uint i = 0; factory.EnumAdapterByGpuPreference(i, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success; i++)
        {
            var d = adapter!.Description1;
            if ((d.Flags & AdapterFlags.Software) != 0) { adapter.Dispose(); continue; }
            all.Add($"{d.Description.Trim()} ({(ulong)d.DedicatedVideoMemory / (1024 * 1024)} Mo)");
            candidates.Add(adapter);
        }
        if (candidates.Count == 0) throw new BenchException("Aucune carte graphique compatible n'a été trouvée.");

        var chosen = candidates.FirstOrDefault(a => a.Description1.VendorId == NvidiaVendorId && D3D12.IsSupported(a, FeatureLevel.Level_12_0))
                     ?? candidates.FirstOrDefault(a => D3D12.IsSupported(a, FeatureLevel.Level_12_0))
                     ?? candidates[0];
        foreach (var other in candidates.Where(a => !ReferenceEquals(a, chosen))) other.Dispose();
        return (chosen, all);
    }

    private static GpuInfo Describe(IDXGIAdapter1 adapter, AdapterDescription1 d, List<string> all)
    {
        string windows = "inconnue";
        string? nvidia = null;
        try
        {
            if (adapter.CheckInterfaceSupport<IDXGIDevice>(out long umd))
            {
                windows = DriverVersion.Windows(umd);
                if (d.VendorId == NvidiaVendorId) nvidia = DriverVersion.Nvidia(umd);
            }
        }
        catch (SharpGenException) { /* version illisible : sans conséquence */ }

        return new GpuInfo(d.Description.Trim(), $"0x{d.VendorId:X4}", $"0x{d.DeviceId:X4}",
            (long)((ulong)d.DedicatedVideoMemory / (1024 * 1024)), windows, nvidia, all);
    }

    public void Dispose()
    {
        try { WaitIdle(); } catch (BenchException) { /* carte déjà perdue */ }
        CommandList.Dispose();
        foreach (var a in _allocators) a?.Dispose();
        foreach (var b in _backBuffers) b?.Dispose();
        _rtvHeap.Dispose();
        _fence.Dispose();
        _fenceEvent.Dispose();
        _infoQueue?.Dispose();
        SwapChain.Dispose();
        Queue.Dispose();
        Device.Dispose();
        _adapter.Dispose();
        _factory.Dispose();
    }
}
