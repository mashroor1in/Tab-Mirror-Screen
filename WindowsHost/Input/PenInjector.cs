using System;
using System.Runtime.InteropServices;
using System.Drawing;

namespace TabMirror.Host.Input;

/// <summary>
/// Injects native Windows Ink stylus events (pressure, tilt, hover) using the Windows 8+ Synthetic Pointer API.
/// This allows professional drawing apps (like Photoshop) to recognise pressure sensitivity.
/// </summary>
public class PenInjector : IDisposable
{
    // ── Win32 / Pointer Injection P/Invoke ───────────────────────────────
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] pointerInfo, uint count);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern void DestroySyntheticPointerDevice(IntPtr device);

    // ── Constants ────────────────────────────────────────────────────────
    private const uint PT_PEN = 2;
    
    // Status Flags
    private const uint POINTER_FLAG_NONE        = 0x00000000;
    private const uint POINTER_FLAG_NEW         = 0x00000001;
    private const uint POINTER_FLAG_INRANGE     = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT   = 0x00000004;
    private const uint POINTER_FLAG_FIRSTBUTTON = 0x00000010;
    private const uint POINTER_FLAG_SECONDBUTTON= 0x00000020;
    
    private const uint POINTER_FLAG_DOWN   = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP     = 0x00040000;
    
    // Pen Masks
    private const uint PEN_MASK_PRESSURE = 0x00000001;
    private const uint PEN_MASK_ROTATION = 0x00000002;
    private const uint PEN_MASK_TILT_X   = 0x00000004;
    private const uint PEN_MASK_TILT_Y   = 0x00000008;

    // Optional Pen Flags
    private const uint PEN_FLAG_ERASER   = 0x00000004;

    // ── Structs ──────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)]
        public uint type;
        
        // Offset 8 accounts for 64-bit alignment pad after a 4-byte uint
        [FieldOffset(8)]
        public POINTER_PEN_INFO penInfo;
    }

    // ── Implementation ───────────────────────────────────────────────────
    private IntPtr _device;
    private uint _pointerId = 100; // Arbitrary distinct ID
    private bool _wasInRange = false;

    public PenInjector()
    {
        // pointerType=2 (Pen), maxCount=5 (standard), mode=1 (POINTER_FEEDBACK_DEFAULT)
        _device = CreateSyntheticPointerDevice(PT_PEN, 5, 1);
        if (_device == IntPtr.Zero)
            Console.WriteLine($"[PenInjector] Failed to create synthetic pen device! Err: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>
    /// Injects a stylus frame
    /// </summary>
    /// <param name="absX">Absolute physical pixel X</param>
    /// <param name="absY">Absolute physical pixel Y</param>
    /// <param name="pressure">0.0 to 1.0 pressure ratio</param>
    /// <param name="tiltX">Tilt -1.0 to 1.0</param>
    /// <param name="tiltY">Tilt -1.0 to 1.0</param>
    /// <param name="action">0=move, 1=down, 2=up, 4=hover-enter, 5=hover-exit, 6=hover-move</param>
    /// <param name="isEraser">true if using tail eraser</param>
    public void InjectPen(int absX, int absY, float pressure, float tiltX, float tiltY, byte action, bool isEraser = false)
    {
        if (_device == IntPtr.Zero) return;

        var info = new POINTER_TYPE_INFO { type = PT_PEN };
        info.penInfo.pointerInfo.pointerType = PT_PEN;
        info.penInfo.pointerInfo.pointerId = _pointerId;
        info.penInfo.pointerInfo.ptPixelLocation.X = absX;
        info.penInfo.pointerInfo.ptPixelLocation.Y = absY;
        
        // Pressure maps 0.0-1.0 to 0-1024
        info.penInfo.penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y;
        info.penInfo.pressure = (uint)Math.Clamp(pressure * 1024f, 0, 1024);
        
        // Tilt maps -1.0..1.0 to -90..90 degrees
        info.penInfo.tiltX = (int)Math.Clamp(tiltX * 90f, -90, 90);
        info.penInfo.tiltY = (int)Math.Clamp(tiltY * 90f, -90, 90);

        if (isEraser)
            info.penInfo.penFlags = PEN_FLAG_ERASER;

        uint flags = POINTER_FLAG_INRANGE;
        
        switch (action)
        {
            case 1: // DOWN
                flags |= POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN | POINTER_FLAG_FIRSTBUTTON;
                if (!_wasInRange) flags |= POINTER_FLAG_NEW;
                break;
            case 0: // DRAG/MOVE
                flags |= POINTER_FLAG_INCONTACT | POINTER_FLAG_UPDATE | POINTER_FLAG_FIRSTBUTTON;
                break;
            case 2: // UP
                flags |= POINTER_FLAG_UP;
                break;
            case 4: // HOVER ENTER
                flags |= POINTER_FLAG_UPDATE | POINTER_FLAG_NEW;
                break;
            case 6: // HOVER MOVE
                flags |= POINTER_FLAG_UPDATE;
                break;
            case 5: // HOVER EXIT
                flags = POINTER_FLAG_UPDATE; // Remove INRANGE flag to exit hover
                break;
        }

        info.penInfo.pointerInfo.pointerFlags = flags;

        // Apply
        bool success = InjectSyntheticPointerInput(_device, new[] { info }, 1);
        if (!success)
        {
            Console.WriteLine($"[PenInjector] Injection failed! Err: {Marshal.GetLastWin32Error()}");
        }

        _wasInRange = (flags & POINTER_FLAG_INRANGE) != 0;
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            DestroySyntheticPointerDevice(_device);
            _device = IntPtr.Zero;
        }
    }
}
