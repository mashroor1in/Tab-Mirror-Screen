using System.Net;
using System.Net.Sockets;

namespace TabMirror.Host.Network;

/// <summary>
/// TCP server that:
///   1. Sends encoded H.264 NAL units to the Android client (video channel)
///   2. Receives touch/input packets from the Android client (control channel)
///
/// Wire format — every message:
///   [1 byte:  msg type]  0x01=video, 0x02=touch-event, 0x03=clipboard, 0x04=keyboard
///   [4 bytes: length, big-endian]
///   [N bytes: payload]
/// </summary>
public sealed class StreamServer : IDisposable
{
    public const int VideoPort   = 7531;
    public const int ControlPort = 7532;

    private TcpListener? _videoListener;
    private TcpListener? _controlListener;

    private TcpClient?   _videoClient;
    private TcpClient?   _controlClient;

    private NetworkStream? _videoStream;
    private NetworkStream? _controlStream;

    private readonly SemaphoreSlim _videoLock   = new(1, 1);
    private readonly SemaphoreSlim _controlLock = new(1, 1);

    public bool IsConnected => _videoClient?.Connected == true;

    /// <summary>
    /// Bytes currently sitting in the OS TCP send buffer (not yet delivered to Android).
    /// The ABR controller uses this to measure network congestion pressure.
    /// </summary>
    public long PendingVideoBytes
    {
        get
        {
            try
            {
                if (_videoClient?.Client == null) return 0;
                return _videoClient.SendBufferSize - (int)_videoClient.Client.Available;
            }
            catch { return 0; }
        }
    }

    public event Action<byte[]>? TouchPacketReceived;
    public event Action<byte>?   GestureMacroReceived;
    public event Action<string>? ClipboardReceived;
    public event Action<byte[]>? KeyboardReceived;

    /// <summary>Starts listening in the background and returns immediately.</summary>
    public void Start(CancellationToken ct = default)
    {
        _videoListener   = new TcpListener(IPAddress.Any, VideoPort);
        _videoListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        
        _controlListener = new TcpListener(IPAddress.Any, ControlPort);
        _controlListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        
        _videoListener.Start();
        _controlListener.Start();

        Console.WriteLine($"[StreamServer] Listening on :{VideoPort} (video) and :{ControlPort} (control)");

        // Start background loops for continuous acceptance
        _ = AcceptVideoLoopAsync(ct);
        _ = AcceptControlLoopAsync(ct);
    }

    private async Task AcceptVideoLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _videoListener!.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                client.SendBufferSize = 1 << 20; // 1 MB
                
                await _videoLock.WaitAsync(ct);
                try
                {
                    _videoClient?.Close();
                    _videoStream?.Dispose();
                    
                    _videoClient = client;
                    _videoStream = client.GetStream();
                }
                finally { _videoLock.Release(); }

                Console.WriteLine($"[StreamServer] Video client connected from {client.Client.RemoteEndPoint}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Task.Delay(1000, ct); // Avoid tight loop on error
            }
        }
    }

    private async Task AcceptControlLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _controlListener!.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                client.ReceiveBufferSize = 64 * 1024;
                
                await _controlLock.WaitAsync(ct);
                try
                {
                    _controlClient?.Close();
                    _controlStream?.Dispose();
                    
                    _controlClient = client;
                    _controlStream = client.GetStream();
                }
                finally { _controlLock.Release(); }

                Console.WriteLine($"[StreamServer] Control client connected from {client.Client.RemoteEndPoint}");
                
                // Start receiving for THIS client
                _ = ReceiveControlLoopAsync(client, _controlStream!, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// Send an H.264 NAL unit to the Android client.
    /// Thread-safe — can be called from the capture/encode loop.
    /// </summary>
    public async Task SendVideoFrameAsync(byte[] nalData, CancellationToken ct = default)
    {
        await _videoLock.WaitAsync(ct);
        try
        {
            if (_videoStream == null || _videoClient?.Connected != true) return;
            
            // Header: [type=0x01][length 4B big-endian]
            byte[] header = [0x01, 0, 0, 0, 0];
            WriteInt32BE(header.AsSpan(1), nalData.Length);
            await _videoStream.WriteAsync(header, ct);
            await _videoStream.WriteAsync(nalData, ct);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            // Silent drop for disconnects
        }
        finally
        {
            _videoLock.Release();
        }
    }

    /// <summary>Send clipboard text to the Android client.</summary>
    public async Task SendClipboardAsync(string text, CancellationToken ct)
    {
        await _controlLock.WaitAsync(ct);
        try
        {
            if (_controlStream == null || _controlClient?.Connected != true) return;
            
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(text);
            byte[] header = new byte[5];
            header[0] = 0x03; // Clipboard
            header[1] = (byte)(payload.Length >> 24);
            header[2] = (byte)(payload.Length >> 16);
            header[3] = (byte)(payload.Length >> 8);
            header[4] = (byte)(payload.Length);
            
            await _controlStream.WriteAsync(header, ct);
            await _controlStream.WriteAsync(payload, ct);
        }
        catch { }
        finally { _controlLock.Release(); }
    }

    public async Task SendResolutionAsync(int width, int height, CancellationToken ct)
    {
        await _videoLock.WaitAsync(ct);
        try
        {
            if (_videoStream == null || _videoClient?.Connected != true) return;
            
            byte[] payload = new byte[8];
            payload[0] = (byte)(width >> 24);
            payload[1] = (byte)(width >> 16);
            payload[2] = (byte)(width >> 8);
            payload[3] = (byte)(width);
            payload[4] = (byte)(height >> 24);
            payload[5] = (byte)(height >> 16);
            payload[6] = (byte)(height >> 8);
            payload[7] = (byte)(height);

            byte[] header = new byte[5];
            header[0] = 0x05; // Resolution Change (on Video Port)
            header[1] = 0; header[2] = 0; header[3] = 0; header[4] = 8;
            
            await _videoStream.WriteAsync(header, ct);
            await _videoStream.WriteAsync(payload, ct);
        }
        catch { }
        finally { _videoLock.Release(); }
    }

    private async Task ReceiveControlLoopAsync(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[5];
        try
        {
            while (!ct.IsCancellationRequested && client.Connected)
            {
                await ReadExactAsync(stream, header, ct);
                // header[0] = msg type (0x02 = touch)
                int length = ReadInt32BE(header, 1);
                byte[] payload = new byte[length];
                await ReadExactAsync(stream, payload, ct);

                if (header[0] == 0x02)
                {
                    if (payload.Length >= 2 && payload[0] == 0x10)
                        GestureMacroReceived?.Invoke(payload[1]);  // 0x10 = gesture macro sub-type
                    else
                        TouchPacketReceived?.Invoke(payload);
                }
                else if (header[0] == 0x03)
                {
                    ClipboardReceived?.Invoke(System.Text.Encoding.UTF8.GetString(payload));
                }
                else if (header[0] == 0x04)
                {
                    KeyboardReceived?.Invoke(payload);
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            // Expected on disconnect
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────
    private static void WriteInt32BE(Span<byte> dest, int value)
    {
        dest[0] = (byte)(value >> 24);
        dest[1] = (byte)(value >> 16);
        dest[2] = (byte)(value >> 8);
        dest[3] = (byte)(value);
    }

    private static int ReadInt32BE(byte[] src, int offset) =>
        (src[offset] << 24) | (src[offset + 1] << 16) |
        (src[offset + 2] << 8) | src[offset + 3];

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
            read += await stream.ReadAsync(buf.AsMemory(read), ct);
    }

    public void Dispose()
    {
        _videoStream?.Dispose();
        _controlStream?.Dispose();
        
        _videoClient?.Dispose();
        _controlClient?.Dispose();
        
        _videoListener?.Stop();
        _controlListener?.Stop();
        
        _videoLock.Dispose();
        _controlLock.Dispose();
    }
}
