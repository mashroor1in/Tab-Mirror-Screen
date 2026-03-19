using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TabMirror.Host.Network;

/// <summary>
/// Sends a JSON stats heartbeat to the Android client every second on port 7534.
/// The Android debug overlay reads these stats to display FPS, latency, bitrate etc.
///
/// JSON format:
/// {
///   "fps": 59.8,
///   "enc_ms": 2.1,
///   "net_rtt_ms": 4.3,
///   "bitrate_kbps": 8000,
///   "resolution": "1920x1080",
///   "encoder": "h264_nvenc",
///   "connection": "USB",
///   "abr_tier": "High"
/// }
/// </summary>
public sealed class StatsReporter : IDisposable
{
    public const int StatsPort = 7534;

    // Mutable stat fields — written by the capture loop, read by the heartbeat task
    public double Fps          { get; set; }
    public double EncodeMs     { get; set; }
    public string Resolution   { get; set; } = "";
    public string EncoderName  { get; set; } = "";
    public string AbrTier      { get; set; } = "High";
    public int    BitrateKbps  { get; set; } = 8000;

    private TcpListener? _listener;
    private TcpClient?   _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _sendTask;

    public void Start(CancellationToken parentCt)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);

        _listener = TcpListener.Create(StatsPort);
        _listener.Start();

        _acceptTask = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    client.NoDelay = true;
                    _client = client;
                    _stream = client.GetStream();
                    Console.WriteLine("[Stats] Debug overlay client connected");

                    _sendTask = Task.Run(() => SendLoopAsync(_cts.Token));
                    await _sendTask; // wait until disconnected, then accept again
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stats] Accept error: {ex.Message}");
            }
        }, _cts.Token);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);

                var stats = new
                {
                    fps          = Math.Round(Fps, 1),
                    bitrate_kbps = BitrateKbps,
                    resolution   = Resolution,
                    encoder      = EncoderName,
                    abr_tier     = AbrTier,
                    enc_ms       = Math.Round(EncodeMs, 1)
                };

                string json = JsonSerializer.Serialize(stats) + "\n";
                byte[] data = System.Text.Encoding.UTF8.GetBytes(json);

                if (_stream != null)
                    await _stream.WriteAsync(data, ct);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Client disconnected — break so outer loop can wait for reconnect
                break;
            }
        }
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _acceptTask?.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Expected on cancellation
        }
        _listener?.Stop();
        _cts?.Dispose();
    }
}
