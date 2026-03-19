using System.Runtime.InteropServices;
using System.Text;

namespace TabMirror.Host.Network;

/// <summary>
/// Monitors the Windows clipboard for changes and syncs them to the Android client.
/// Also receives clipboard updates from the client and applies them locally.
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private readonly StreamServer _server;
    private string _lastClipboardText = "";
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;

    public ClipboardService(StreamServer server)
    {
        _server = server;
        _server.ClipboardReceived += OnClientClipboardReceived;
    }

    public void Start()
    {
        _monitorTask = Task.Run(MonitorLoopAsync);
    }

    private async Task MonitorLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                string currentText = GetClipboardText();
                if (!string.IsNullOrEmpty(currentText) && currentText != _lastClipboardText)
                {
                    _lastClipboardText = currentText;
                    Console.WriteLine($"[Clipboard] local change: {(_lastClipboardText.Length > 40 ? _lastClipboardText[..40] + "..." : _lastClipboardText)}");
                    await _server.SendClipboardAsync(currentText, _cts.Token);
                }
            }
            catch (Exception)
            {
                // Clipboard might be locked by another process
                // Console.WriteLine($"[Clipboard] Monitor error: {ex.Message}");
            }
            await Task.Delay(1000, _cts.Token);
        }
    }

    private void OnClientClipboardReceived(string text)
    {
        if (string.IsNullOrEmpty(text) || text == _lastClipboardText) return;
        _lastClipboardText = text;
        Console.WriteLine($"[Clipboard] remote change: {(text.Length > 40 ? text[..40] + "..." : text)}");
        
        // Ensure we set the clipboard on a separate thread to avoid stalling the control loop
        Task.Run(() => SetClipboardText(text));
    }

    // ── Win32 Interop ──────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    private const uint CF_UNICODETEXT = 13;
    private const uint GHND = 0x0042; // Moveable and ZeroInit

    private string GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero)) return _lastClipboardText;
        try
        {
            IntPtr handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return "";
            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return "";
            try
            {
                return Marshal.PtrToStringUni(pointer) ?? "";
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private void SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return;
        try
        {
            EmptyClipboard();
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
            IntPtr hGlobal = GlobalAlloc(GHND, (UIntPtr)bytes.Length);
            if (hGlobal == IntPtr.Zero) return;
            
            IntPtr pointer = GlobalLock(hGlobal);
            if (pointer != IntPtr.Zero)
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                GlobalUnlock(hGlobal);
                SetClipboardData(CF_UNICODETEXT, hGlobal);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Clipboard] Set error: {ex.Message}");
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _monitorTask?.Wait(500); // Give it a moment to exit gracefully
        }
        catch (Exception)
        {
            // Expected during cancellation
        }
        _cts.Dispose();
        _server.ClipboardReceived -= OnClientClipboardReceived;
    }
}
