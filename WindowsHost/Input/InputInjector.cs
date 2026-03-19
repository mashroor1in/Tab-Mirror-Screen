using System.Runtime.InteropServices;
using System.Drawing;

namespace TabMirror.Host.Input;

/// <summary>
/// Injects mouse movements and clicks into Windows using SendInput().
/// Coordinates are translated from normalised tablet coordinates (0.0–1.0)
/// to absolute virtual-desktop coordinates (0–65535) based on the
/// virtual monitor's position within the overall desktop.
/// </summary>
public static class InputInjector
{
    private static PenInjector? _penInjector;
    private static CancellationTokenSource? _smoothingCts;
    
    // Smoothing state (normalised 0.0-1.0)
    private static float _targetX, _targetY;
    private static float _currentX, _currentY;
    private static bool  _hasTarget = false;
    private static bool  _isTouchActive = false;
    private const float  SmoothingFactor = 0.45f; // Adjust for "weight" vs responsiveness

    public static void InitializePenInjector()
    {
        _penInjector = new PenInjector();
        
        // Start smoothing loop at ~120Hz (8ms)
        _smoothingCts = new CancellationTokenSource();
        Task.Run(() => SmoothingLoop(_smoothingCts.Token));
    }

    public static void DisposePenInjector()
    {
        _smoothingCts?.Cancel();
        _penInjector?.Dispose();
        _penInjector = null;
    }

    // ── Win32 interop ──────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUT_UNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int  dx, dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // Mouse event flags
    private const uint MOUSEEVENTF_MOVE        = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN    = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP      = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN   = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP     = 0x0010;
    private const uint MOUSEEVENTF_WHEEL       = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE    = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    // Keyboard event flags
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP       = 0x0002;
    private const uint KEYEVENTF_UNICODE     = 0x0004;
    private const uint KEYEVENTF_SCANCODE    = 0x0008;

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Handle an incoming touch packet from the Android client (updates targets).
    /// </summary>
    public static void HandleTouchPacket(byte[] data, Rectangle monitorBounds)
    {
        if (data.Length < 9) return;

        byte action = data[0];
        byte toolType = 1;
        float nx = 0, ny = 0, pressure = 1.0f, tiltX = 0f, tiltY = 0f;

        if (data.Length >= 22)
        {
            toolType = data[1];
            nx = BitConverter.ToSingle(data, 2);
            ny = BitConverter.ToSingle(data, 6);
            pressure = BitConverter.ToSingle(data, 10);
            tiltX = BitConverter.ToSingle(data, 14);
            tiltY = BitConverter.ToSingle(data, 18);
        }
        else
        {
            nx = BitConverter.ToSingle(data, 1);
            ny = BitConverter.ToSingle(data, 5);
        }

        nx = Math.Clamp(nx, 0f, 1f);
        ny = Math.Clamp(ny, 0f, 1f);

        // Update smoothing targets
        _targetX = nx;
        _targetY = ny;
        
        // Actions: 0=Move, 1=Down, 2=Up, 6=Hover
        if (action == 1 || action == 0 || action == 6) _isTouchActive = true;
        if (action == 2) _isTouchActive = false;

        if (!_hasTarget || action == 1) // Snap on first packet or click-down
        {
            _currentX = _targetX;
            _currentY = _targetY;
            _hasTarget = true;
        }

        // Only handle discrete actions (clicks/scroll) here. 
        // Moves (0) and HoverMoves (6) are handled by the SmoothingLoop.
        if (action == 0 || action == 6) return;

        // Map to virtual screen for immediate actions (clicks, etc.)
        _lastMonitorBounds = monitorBounds;
        int vdX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vdY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vdW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vdH = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        double physX = monitorBounds.Left + nx * monitorBounds.Width;
        double physY = monitorBounds.Top  + ny * monitorBounds.Height;
        int absX = (int)(65535.0 * (physX - vdX) / vdW);
        int absY = (int)(65535.0 * (physY - vdY) / vdH);
        
        // Track for smoothing loop
        _lastAbsX = absX; 
        _lastAbsY = absY;

        if (toolType == 2 || toolType == 3)
        {
            _penInjector?.InjectPen((int)physX, (int)physY, pressure, tiltX, tiltY, action, toolType == 3);
            return;
        }

        switch (action)
        {
            case 1: // DOWN
                SendMouseEvent(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
                SendMouseEvent(absX, absY, MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
                break;
            case 2: // UP
                SendMouseEvent(absX, absY, MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
                break;
            case 3: // scroll
                if (data.Length >= 13) {
                    float delta = BitConverter.ToSingle(data, 9);
                    SendMouseEvent(absX, absY, MOUSEEVENTF_WHEEL, (uint)(int)(delta * 120));
                }
                break;
            case 4: // right down
                SendMouseEvent(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
                SendMouseEvent(absX, absY, MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
                break;
            case 5: // right up
                SendMouseEvent(absX, absY, MOUSEEVENTF_RIGHTUP | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
                break;
        }
    }

    private static async void SmoothingLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_hasTarget)
            {
                bool isInterpolating = Math.Abs(_currentX - _targetX) > 0.0001f || Math.Abs(_currentY - _targetY) > 0.0001f;
                
                if (isInterpolating)
                {
                    // Smoothly approach target
                    _currentX += (_targetX - _currentX) * SmoothingFactor;
                    _currentY += (_targetY - _currentY) * SmoothingFactor;
                }
                else
                {
                    _currentX = _targetX;
                    _currentY = _targetY;
                }

                // ONLY inject move events if we are actively moving OR if the user is still touching/hovering.
                // This prevents the "stuck mouse" where the virtual injector fights the physical mouse 
                // when the user isn't actually moving anything.
                if (isInterpolating || _isTouchActive)
                {
                    int vdX = GetSystemMetrics(SM_XVIRTUALSCREEN);
                    int vdY = GetSystemMetrics(SM_YVIRTUALSCREEN);
                    int vdW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                    int vdH = GetSystemMetrics(SM_CYVIRTUALSCREEN);

                    double physX = _lastMonitorBounds.Left + _currentX * _lastMonitorBounds.Width;
                    double physY = _lastMonitorBounds.Top  + _currentY * _lastMonitorBounds.Height;

                    int absX = (int)(65535.0 * (physX - vdX) / vdW);
                    int absY = (int)(65535.0 * (physY - vdY) / vdH);

                    SendMouseEvent(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
                }
            }
            await Task.Delay(5, ct); // 200Hz
        }
    }

    private static int _lastAbsX, _lastAbsY; 
    private static Rectangle _lastMonitorBounds;

    /// <summary>
    /// Handle keyboard packet from Android.
    /// Format: [1B action: 0=down, 1=up][4B vkey int32][4B unicode int32]
    /// </summary>
    public static void HandleKeyboardPacket(byte[] data)
    {
        if (data.Length < 9) return;
        byte action = data[0];
        int vkey    = BitConverter.ToInt32(data, 1);
        int unicode = BitConverter.ToInt32(data, 5);

        uint flags = (action == 1) ? KEYEVENTF_KEYUP : 0;

        if (unicode > 0)
        {
            // Inject as Unicode character
            SendKeyEvent(0, (ushort)unicode, flags | KEYEVENTF_UNICODE);
        }
        else if (vkey > 0)
        {
            // Inject as Virtual Key
            SendKeyEvent((ushort)vkey, 0, flags);
        }
    }

    private static void SendKeyEvent(ushort vkey, ushort scan, uint flags)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = 1, // INPUT_KEYBOARD
                u    = new INPUT_UNION { ki = new KEYBDINPUT { wVk = vkey, wScan = scan, dwFlags = flags } }
            }
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseEvent(int x, int y, uint flags, uint mouseData = 0)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = 0, // INPUT_MOUSE
                u    = new INPUT_UNION { mi = new MOUSEINPUT { dx = x, dy = y, mouseData = mouseData, dwFlags = flags } }
            }
        };
        uint sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
            Console.WriteLine($"[InputInjector] SendInput failed: {Marshal.GetLastWin32Error()}");
    }

    // ── Gesture Macro Key Injection ───────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_WIN   = 0x5B;
    private const byte VK_CTRL  = 0x11;
    private const byte VK_ALT   = 0x12;
    private const byte VK_TAB   = 0x09;
    private const byte VK_D     = 0x44;
    private const byte VK_LEFT  = 0x25;
    private const byte VK_RIGHT = 0x27;
    private const byte VK_F4    = 0x73;
    private const byte VK_Z     = 0x5A;
    /// <summary>
    /// Execute a gesture macro from the Android app.
    /// Macro IDs match GestureMacro.kt constants.
    /// </summary>
    public static void HandleGestureMacro(byte macroId)
    {
        Console.WriteLine($"[Macro] Received gesture macro: 0x{macroId:X2}");
        switch (macroId)
        {
            case 0x01: // Task View (Win+Tab)
                keybd_event(VK_WIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_TAB, 0, 0, UIntPtr.Zero);
                keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_WIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                break;
            case 0x02: // Show Desktop (Win+D)
                keybd_event(VK_WIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_D, 0, 0, UIntPtr.Zero);
                keybd_event(VK_D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_WIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                break;
            case 0x03: // Previous Virtual Desktop (Ctrl+Win+Left)
                keybd_event(VK_CTRL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_WIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_LEFT, 0, 0, UIntPtr.Zero);
                keybd_event(VK_LEFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_WIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CTRL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                break;
            case 0x04: // Next Virtual Desktop (Ctrl+Win+Right)
                keybd_event(VK_CTRL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_WIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_RIGHT, 0, 0, UIntPtr.Zero);
                keybd_event(VK_RIGHT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_WIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CTRL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                break;
            case 0x05: // Close Window (Alt+F4)
                keybd_event(VK_ALT, 0, 0, UIntPtr.Zero);
                keybd_event(VK_F4, 0, 0, UIntPtr.Zero);
                keybd_event(VK_F4, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                break;
            case 0x06: // Undo (Ctrl+Z)
                keybd_event(VK_CTRL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_Z, 0, 0, UIntPtr.Zero);
                keybd_event(VK_Z, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CTRL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                break;
        }
    }
}
