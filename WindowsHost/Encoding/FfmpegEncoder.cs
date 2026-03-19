using System.Diagnostics;

namespace TabMirror.Host.Encoding;

/// <summary>
/// H.264 encoder that pipes raw BGRA frames to an external ffmpeg.exe process.
///
/// Why ffmpeg.exe rather than a NuGet binding?
/// - Zero NuGet dependencies → `dotnet build` works out of the box.
/// - ffmpeg.exe auto-selects the best available hardware encoder
///   (NVENC, QSV, AMF, or x264) on the host machine.
/// - The user already needs ffmpeg.exe for full codec support anyway.
///
/// Install ffmpeg:  winget install Gyan.FFmpeg
/// Or download from https://ffmpeg.org/download.html (put ffmpeg.exe on PATH).
///
/// Pipe format:
///   ffmpeg reads raw BGRA frames from stdin, encodes to H.264/HEVC Annex-B,
///   and writes NAL units to stdout which we forward to the TCP client.
/// </summary>
public enum CodecPreference { Auto, H264, Hevc }

public sealed class FfmpegEncoder : IDisposable
{
    public int Width  { get; }
    public int Height { get; }
    public int Fps    { get; }
    public string EncoderName { get; private set; } = "unknown";

    private Process? _process;
    private Stream? _stdin;
    private Stream? _stdout;
    private bool _disposed;
    private int _currentBitrateKbps;

    // Expose last encode timing for the stats overlay
    public double LastEncodeMs { get; private set; }
    private readonly System.Diagnostics.Stopwatch _sw = new();

    private readonly byte[] _bgraBuffer;

    public FfmpegEncoder(int width, int height, int fps = 60, int bitrateKbps = 8000, CodecPreference codec = CodecPreference.Auto, int gopSize = 60, string profile = "high")
    {
        Width  = width;
        Height = height;
        Fps    = fps;
        _currentBitrateKbps = bitrateKbps;
        _bgraBuffer = new byte[width * height * 4];

        // Select hardware encoder based on preference
        bool wantHevc = codec == CodecPreference.Hevc;
        EncoderName = DetectBestEncoder(wantHevc);

        string encoderOptions = "";
        string rateControlOpts = "";

        if (EncoderName is "h264_nvenc" or "hevc_nvenc")
        {
            // p1 is the lowest-latency/fastest-encoding preset for NVENC (recommended for gaming/streaming)
            encoderOptions = "-preset p1 -tune ull -delay 0 -zerolatency 1 -spatial-aq 1";
            rateControlOpts = "-rc constqp -qp 18";
        }
        else if (EncoderName is "h264_qsv" or "hevc_qsv")
        {
            encoderOptions = "-preset veryfast -async_depth 1";
            rateControlOpts = "-global_quality 18";
        }
        else if (EncoderName is "h264_amf" or "hevc_amf")
        {
            encoderOptions = "-quality speed -usage ultralowlatency";
            rateControlOpts = $"-b:v {bitrateKbps * 2}k";
        }
        else
        {
            encoderOptions = "-preset ultrafast -tune zerolatency";
            rateControlOpts = $"-b:v {bitrateKbps}k -maxrate {bitrateKbps * 2}k -bufsize {bitrateKbps * 2}k";
        }

        // Choose output format container based on codec
        string outFormat = EncoderName.StartsWith("hevc") ? "hevc" : "h264";

        string args =
            $"-f rawvideo -pix_fmt bgra -s {width}x{height} -r {fps} -i pipe:0 " +
            $"-c:v {EncoderName} {encoderOptions} " +
            $"-profile:v {profile} {rateControlOpts} " +
            $"-g {gopSize} -bf 0 " +
            $"-f {outFormat} pipe:1";

        Console.WriteLine($"[Encoder] Using: {EncoderName}");
        Console.WriteLine($"[Encoder] Command: ffmpeg {args}");

        var psi = new ProcessStartInfo("ffmpeg", args)
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Failed to start ffmpeg. Is it installed and on PATH?\n" +
                "  Install: winget install Gyan.FFmpeg");

        _stdin  = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;

        // Log ffmpeg stderr (codec diagnostics) without blocking
        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = _process.StandardError;
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                    if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"[ffmpeg] {line}");
            }
            catch { /* process ended */ }
        });
    }

    /// <summary>
    /// Write a raw BGRA frame to ffmpeg's stdin.
    /// ffmpeg encodes it and the output becomes available from <see cref="OutputData"/>.
    /// </summary>
    public async Task WriteFrameAsync(byte[] bgraData, CancellationToken ct = default)
    {
        if (_stdin == null) return;
        _sw.Restart();
        await _stdin.WriteAsync(bgraData.AsMemory(), ct);
        await _stdin.FlushAsync(ct);
        LastEncodeMs = _sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Requests a live bitrate change by sending an ffmpeg signalling sequence.
    /// NOTE: For NVENC/QSV, ffmpeg does NOT support live bitrate changes after init.
    /// The only reliable way is to restart the encoder process with a new bitrate.
    /// This method restarts it gracefully.
    /// </summary>
    public void SetBitrate(int newBitrateKbps)
    {
        if (newBitrateKbps == _currentBitrateKbps) return;
        _currentBitrateKbps = newBitrateKbps;
        // Signal the process to restart with new bitrate on next call
        // (simplest robust strategy: mark as dirty; Program.cs handles restart)
        NeedsBitrateRestart = true;
    }

    /// <summary>Set to true by SetBitrate; cleared by the caller after restart.</summary>
    public bool NeedsBitrateRestart { get; private set; }
    public int  CurrentBitrateKbps => _currentBitrateKbps;

    /// <summary>
    /// The raw H.264 Annex-B byte stream coming from ffmpeg's stdout.
    /// Read from this stream in a background task and forward to the TCP client.
    /// </summary>
    public Stream? OutputData => _stdout;

    private static string DetectBestEncoder(bool wantHevc = false)
    {
        string[] candidates = wantHevc
            ? ["hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265"]
            : ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];
        
        foreach (var enc in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo("ffmpeg",
                    $"-f lavfi -i nullsrc=s=320x240:d=0.1 -c:v {enc} -f null -")
                {
                    RedirectStandardError = true,
                    UseShellExecute       = false,
                    CreateNoWindow        = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0)
                    return enc;
            }
            catch { }
        }
        return wantHevc ? "libx265" : "libx264";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stdin?.Close(); } catch { }
        try { _process?.WaitForExit(2000); } catch { }
        _process?.Dispose();
    }
}
