using System.Drawing;
using System.Runtime.InteropServices;

namespace TabMirror.Host.Capture;

/// <summary>
/// Captures frames from a specific monitor using the DXGI Desktop Duplication API
/// via raw COM P/Invoke — no SharpDX or WinRT SDK required.
///
/// Works on Windows 8+ and all Windows 10/11 versions without extra SDK installs.
///
/// Pipeline: D3D11 device → IDXGIOutput1::DuplicateOutput → AcquireNextFrame
///           → CopyResource to staging texture → Map → CPU BGRA bytes → encoder
/// </summary>
public sealed unsafe class DxgiCapture : IDisposable
{
    // ── COM GUIDs ────────────────────────────────────────────────────────
    private static readonly Guid IID_IDXGIFactory1      = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_ID3D11Device        = new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid IID_IDXGIDevice         = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IDXGIAdapter        = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0");
    private static readonly Guid IID_IDXGIOutput1        = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_IDXGIOutputDuplication = new("191cfac3-a341-470d-b26e-a864f428319c");
    private static readonly Guid IID_IDXGIResource       = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
    private static readonly Guid IID_ID3D11Texture2D     = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // ── DXGI / D3D11 native imports ──────────────────────────────────────
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out nint ppFactory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        nint pAdapter, uint DriverType, nint Software, uint Flags,
        nint pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out nint ppDevice, out uint pFeatureLevel, out nint ppImmediateContext);

    // ── User32 for monitor enumeration ───────────────────────────────────
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip,
        MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    private delegate bool MonitorEnumProc(nint hMon, nint hdc, ref RECT lprc, nint lp);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    // ── COM vtable offsets (IUnknown: QI=0, AddRef=1, Release=2) ──────

    // IDXGIFactory1 vtable slots we need
    static int QueryInterfaceSlot  = 0;
    static int ReleaseSlot         = 2;
    static int EnumAdaptersSlot    = 7;  // IDXGIFactory::EnumAdapters (same in IDXGIFactory1)

    // IDXGIAdapter vtable
    static int EnumOutputsSlot     = 7;  // IDXGIAdapter::EnumOutputs

    // IDXGIOutput1 vtable
    static int DuplicateOutputSlot = 22; // IDXGIOutput1::DuplicateOutput

    // IDXGIOutputDuplication vtable
    static int AcquireNextFrameSlot = 8;
    static int ReleaseFrameSlot     = 14;

    // ID3D11Device vtable
    static int CreateTexture2DSlot  = 5;
    // ID3D11DeviceContext vtable
    static int CopyResourceSlot     = 47;
    static int MapSlot              = 14;
    static int UnmapSlot            = 15;

    // ── State fields ─────────────────────────────────────────────────────
    private nint _factory;
    private nint _adapter;
    private nint _device;
    private nint _context;
    private nint _output1;
    private nint _duplication;
    private nint _stagingTexture;
    private bool _disposed;

    public int Width  { get; }
    public int Height { get; }
    public int MonitorIndex { get; }

    // ── D3D11_TEXTURE2D_DESC (simplified) ────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public uint Format;         // DXGI_FORMAT_B8G8R8A8_UNORM = 87
        public uint SampleCount, SampleQuality;
        public uint Usage;          // D3D11_USAGE_STAGING = 3
        public uint BindFlags;      // 0 for staging
        public uint CPUAccessFlags; // D3D11_CPU_ACCESS_READ = 0x20000
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames, RectsCoalesced, ProtectedContentMasked;
        public uint PointerPosition1, PointerPosition2;
        public uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public nint pData;
        public uint RowPitch, DepthPitch;
    }

    public DxgiCapture(int monitorIndex = 0)
    {
        MonitorIndex = monitorIndex;

        // 1. Create DXGI factory
        var iid = IID_IDXGIFactory1;
        ComCheck(CreateDXGIFactory1(ref iid, out _factory), "CreateDXGIFactory1");

        // 2. Get adapter at index 0 (primary GPU)
        delegate* unmanaged[Stdcall]<nint, uint, nint*, int> enumAdapters =
            (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)GetVTable(_factory)[EnumAdaptersSlot];
        nint adapter; enumAdapters(_factory, 0, &adapter);
        _adapter = adapter;

        // 3. Create D3D11 device from adapter
        ComCheck(D3D11CreateDevice(_adapter, 0 /*HARDWARE*/, 0, 0x20 /*BGRA_SUPPORT*/,
            0, 0, 7, out _device, out _, out _context), "D3D11CreateDevice");

        // 4. Get output (monitor) from adapter
        delegate* unmanaged[Stdcall]<nint, uint, nint*, int> enumOutputs =
            (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)GetVTable(_adapter)[EnumOutputsSlot];
        nint output; enumOutputs(_adapter, (uint)monitorIndex, &output);

        // QI for IDXGIOutput1
        var iid1 = IID_IDXGIOutput1;
        nint out1; ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GetVTable(output)[QueryInterfaceSlot])(output, &iid1, &out1);
        _output1 = out1;
        ComRelease(output);

        // Read dimensions from output description (offset 0 = device name [32 wide chars = 64B], then DXGI_OUTPUT_DESC fields)
        // Use the monitor enumeration instead for cleaner code:
        var monitors = EnumerateMonitors();
        if (monitorIndex >= monitors.Count)
            throw new ArgumentException($"Monitor index {monitorIndex} not found.");
        Width  = monitors[monitorIndex].Bounds.Width;
        Height = monitors[monitorIndex].Bounds.Height;
        Console.WriteLine($"[DxgiCapture] Monitor {monitorIndex}: {Width}x{Height} ({monitors[monitorIndex].Name})");

        // 5. Create Desktop Duplication
        delegate* unmanaged[Stdcall]<nint, nint, nint*, int> dupOutput =
            (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)GetVTable(_out1)[DuplicateOutputSlot];
        nint dup; ComCheck(dupOutput(_out1, _device, &dup), "DuplicateOutput");
        _duplication = dup;

        // 6. Create staging texture for CPU readback
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)Width, Height = (uint)Height,
            MipLevels = 1, ArraySize = 1,
            Format = 87,           // DXGI_FORMAT_B8G8R8A8_UNORM
            SampleCount = 1,
            Usage = 3,             // D3D11_USAGE_STAGING
            BindFlags = 0,
            CPUAccessFlags = 0x20000, // D3D11_CPU_ACCESS_READ
        };
        delegate* unmanaged[Stdcall]<nint, D3D11_TEXTURE2D_DESC*, nint, nint*, int> createTex =
            (delegate* unmanaged[Stdcall]<nint, D3D11_TEXTURE2D_DESC*, nint, nint*, int>)GetVTable(_device)[CreateTexture2DSlot];
        nint staging; createTex(_device, &desc, 0, &staging);
        _stagingTexture = staging;
    }

    // Convenience alias
    private nint _out1 => _output1;

    /// <summary>
    /// Acquire the next changed frame. Returns BGRA bytes or null if no new frame within timeout.
    /// </summary>
    public byte[]? AcquireNextFrame(int timeoutMs = 33)
    {
        var frameInfo = new DXGI_OUTDUPL_FRAME_INFO();
        nint resource = 0;

        delegate* unmanaged[Stdcall]<nint, uint, DXGI_OUTDUPL_FRAME_INFO*, nint*, int> acquire =
            (delegate* unmanaged[Stdcall]<nint, uint, DXGI_OUTDUPL_FRAME_INFO*, nint*, int>)GetVTable(_duplication)[AcquireNextFrameSlot];

        const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        int hr = acquire(_duplication, (uint)timeoutMs, &frameInfo, &resource);
        
        if (hr < 0)
        {
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return null;
            
            // Any other error (ACCESS_LOST, SESSION_DISCONNECTED, INVALID_CALL, etc.)
            // should trigger a re-initialization of the capture system.
            throw new ResolutionChangedException($"DXGI Error 0x{hr:X8}. Triggering capture reset.");
        }
        
        if (resource == 0) return null;

        // No dirty rect metadata → frame unchanged
        if (frameInfo.TotalMetadataBufferSize == 0 && frameInfo.AccumulatedFrames == 0)
        {
            ReleaseFrame();
            ComRelease(resource);
            return null;
        }

        // QI for ID3D11Texture2D
        var iidTex = IID_ID3D11Texture2D;
        nint tex = 0;
        int hrQi = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GetVTable(resource)[0])(resource, &iidTex, &tex);
        ComRelease(resource);
        
        if (hrQi < 0 || tex == 0) {
            ReleaseFrame();
            return null;
        }

        // Copy GPU texture → staging
        delegate* unmanaged[Stdcall]<nint, nint, nint, void> copyRes =
            (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)GetVTable(_context)[CopyResourceSlot];
        copyRes(_context, _stagingTexture, tex);
        ComRelease(tex);
        ReleaseFrame();

        // Map staging texture to CPU
        delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int> mapFn =
            (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)GetVTable(_context)[MapSlot];
        var mapped = new D3D11_MAPPED_SUBRESOURCE();
        int hrMap = mapFn(_context, _stagingTexture, 0, 1 /*MAP_READ*/, 0 /*MapFlags*/, &mapped);
        
        if (hrMap < 0 || mapped.pData == 0) return null;

        int rowBytes = Width * 4;
        byte[] result = new byte[rowBytes * Height];
        if (mapped.RowPitch == (uint)rowBytes)
        {
            // Fast path: single bulk copy
            Marshal.Copy(mapped.pData, result, 0, result.Length);
        }
        else
        {
            // Padded path: copy line by line
            for (int y = 0; y < Height; y++)
            {
                Marshal.Copy(mapped.pData + y * (int)mapped.RowPitch, result, y * rowBytes, rowBytes);
            }
        }

        delegate* unmanaged[Stdcall]<nint, nint, uint, void> unmapFn =
            (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)GetVTable(_context)[UnmapSlot];
        unmapFn(_context, _stagingTexture, 0);

        return result;
    }

    public void ReleaseFrame()
    {
        delegate* unmanaged[Stdcall]<nint, int> releaseFrame =
            (delegate* unmanaged[Stdcall]<nint, int>)GetVTable(_duplication)[ReleaseFrameSlot];
        releaseFrame(_duplication);
    }

    // ── Monitor enumeration ───────────────────────────────────────────────
    [ThreadStatic] private static List<(string Name, Rectangle Bounds)>? _monitorList;

    private static bool MonitorEnumCallback(nint hMon, nint hdcMon, ref RECT lprc, nint dwData)
    {
        var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hMon, ref mi);
        _monitorList!.Add((mi.szDevice, new Rectangle(
            mi.rcMonitor.left, mi.rcMonitor.top,
            mi.rcMonitor.right  - mi.rcMonitor.left,
            mi.rcMonitor.bottom - mi.rcMonitor.top)));
        return true;
    }

    public static List<(string Name, Rectangle Bounds)> EnumerateMonitors()
    {
        _monitorList = new List<(string, Rectangle)>();
        EnumDisplayMonitors(0, 0, MonitorEnumCallback, 0);
        var result = _monitorList;
        _monitorList = null;
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static nint* GetVTable(nint comPtr) => *(nint**)comPtr;
    private static void ComRelease(nint ptr)
    {
        if (ptr == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, uint>)GetVTable(ptr)[ReleaseSlot])(ptr);
    }
    private static void ComCheck(int hr, string name)
    {
        if (hr < 0) throw new InvalidOperationException($"{name} failed: HRESULT 0x{hr:X8}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ComRelease(_stagingTexture);
        ComRelease(_duplication);
        ComRelease(_output1);
        ComRelease(_adapter);
        ComRelease(_factory);
        // Note: _device and _context are owned and released together
        ComRelease(_context);
        ComRelease(_device);
    }
}
