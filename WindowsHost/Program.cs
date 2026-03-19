using System.Drawing;
using TabMirror.Host.Capture;
using TabMirror.Host.Discovery;
using TabMirror.Host.Encoding;
using TabMirror.Host.Input;
using TabMirror.Host.Network;

namespace TabMirror.Host;

/// <summary>
/// Tab Mirror — Windows Host
/// ─────────────────────────
/// Captures a virtual (or physical) monitor via DXGI Desktop Duplication,
/// encodes frames as H.264 (via ffmpeg.exe), and streams them over TCP to
/// the Android client. Receives touch input and injects it as mouse events.
///
/// Dependencies: ffmpeg.exe on PATH (winget install Gyan.FFmpeg)
/// </summary>
internal class Program
{
    private const int TargetFps       = 60;
    private const int BitRateKbps     = 8000;
    private const int FrameIntervalMs = 1000 / TargetFps;

    static async Task Main(string[] args)
    {
        Console.Title = "Tab Mirror Host";
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════╗");
        Console.WriteLine("║         TAB MIRROR HOST  v1.0         ║");
        Console.WriteLine("╚═══════════════════════════════════════╝");
        Console.ResetColor();

        // ── 1. Enumerate and select monitor ────────────────────────────────
        var outputs = DxgiCapture.EnumerateMonitors();
        Console.WriteLine("\nAvailable monitors:");
        for (int i = 0; i < outputs.Count; i++)
        {
            var (name, bounds) = outputs[i];
            Console.WriteLine($"  [{i}] {name}  {bounds.Width}x{bounds.Height} @ ({bounds.Left},{bounds.Top})");
        }

        int selectedIndex = 0;
        if (outputs.Count > 1)
        {
            Console.Write($"\nSelect monitor index [0–{outputs.Count - 1}] (default=0): ");
            var input = Console.ReadLine();
            if (int.TryParse(input, out int idx) && idx >= 0 && idx < outputs.Count)
                selectedIndex = idx;
        }

        var (selName, monitorBounds) = outputs[selectedIndex];
        Console.WriteLine($"\n[Host] Streaming: {selName} ({monitorBounds.Width}x{monitorBounds.Height})");

        // ── 2. Set up components ────────────────────────────────────────────
        using var server    = new StreamServer();
        using var discovery = new DiscoveryService();
        using var stats     = new StatsReporter();
        using var clipboard = new ClipboardService(server);
        using var cts       = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // ── 3. Touch + Gesture input handlers ────────────────────────────────
        server.TouchPacketReceived += (payload) =>
        {
            // Note: monitorBounds is updated inside the loop below
            InputInjector.HandleTouchPacket(payload, monitorBounds);
        };
        server.GestureMacroReceived += (macroId) =>
        {
            InputInjector.HandleGestureMacro(macroId);
        };
        server.KeyboardReceived += (payload) =>
        {
            InputInjector.HandleKeyboardPacket(payload);
        };

        // ── 4. Start mDNS advertisement ─────────────────────────────────────
        await discovery.StartAdvertisingAsync(StreamServer.VideoPort, StreamServer.ControlPort);

        // ── 5. Print connection info ────────────────────────────────────────
        var localIPs = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
            .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(ip => ip.ToString());

        Console.WriteLine("\n[Host] Waiting for Android client...");
        Console.WriteLine($"       PC IP: {string.Join(", ", localIPs)}");
        Console.WriteLine($"       Video port: {StreamServer.VideoPort} | Control: {StreamServer.ControlPort} | Stats: {StatsReporter.StatsPort}");
        Console.WriteLine("\n[Host] For USB/ADB mode run on PC:");
        Console.WriteLine($"       adb reverse tcp:{StreamServer.VideoPort} tcp:{StreamServer.VideoPort}");
        Console.WriteLine($"       adb reverse tcp:{StreamServer.ControlPort} tcp:{StreamServer.ControlPort}");
        Console.WriteLine($"       adb reverse tcp:{StatsReporter.StatsPort} tcp:{StatsReporter.StatsPort}");
        Console.WriteLine("       Then connect Android app to: 127.0.0.1\n");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        // ── 6. Wait for client ──────────────────────────────────────────────
        InputInjector.InitializePenInjector();
        stats.Start(cts.Token);
        clipboard.Start();

        server.Start(cts.Token);
        
        if (cts.Token.IsCancellationRequested) goto Cleanup;

        // ── 7. Resolution-aware capture/encode loop ───────────────────────
        while (!cts.Token.IsCancellationRequested)
        {
            // Re-enumerate to get current resolution/bounds
            var currentMonitors = DxgiCapture.EnumerateMonitors();
            if (selectedIndex >= currentMonitors.Count) selectedIndex = 0;
            var (_, currentBounds) = currentMonitors[selectedIndex];
            monitorBounds = currentBounds;

            try
            {
                using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                
                DxgiCapture capture;
                FfmpegEncoder encoder;
                AbrController abrLoop;

                try
                {
                    var mons = DxgiCapture.EnumerateMonitors();
                    if (selectedIndex >= mons.Count)
                    {
                        Console.WriteLine($"[Host] Warning: Monitor {selectedIndex} lost. Falling back to default.");
                        selectedIndex = 0;
                    }
                    
                    var (name, bounds) = mons[selectedIndex];
                    Console.WriteLine($"[Host] Re-initializing capture: {name} ({bounds.Width}x{bounds.Height})...");

                    capture = new DxgiCapture(selectedIndex);
                    // Change GOP to 1 second (-g {fps}) and profile to 'main' for better Android compatibility
                    encoder = new FfmpegEncoder(capture.Width, capture.Height, TargetFps, BitRateKbps, gopSize: TargetFps, profile: "main");
                    abrLoop = new AbrController(encoder, () => server.PendingVideoBytes);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[Host] Capture initialization failed: {ex.Message}. Waiting 2s for display to settle...");
                    Console.ResetColor();
                    await Task.Delay(2000, cts.Token);
                    continue;
                }

                using (capture)
                using (encoder)
                using (abrLoop)
                {
                    monitorBounds = new Rectangle(0, 0, capture.Width, capture.Height);
                    await server.SendResolutionAsync(capture.Width, capture.Height, cts.Token);
                    
                    abrLoop.Start();
                    stats.EncoderName = encoder.EncoderName;
                    stats.Resolution  = $"{capture.Width}x{capture.Height}";

                    var forwardTask = Task.Run(async () =>
                    {
                        if (encoder.OutputData == null) return;
                        var buffer = new byte[512 * 1024]; // 512KB buffer
                        int totalInBuff = 0;

                        try
                        {
                            while (!loopCts.Token.IsCancellationRequested)
                            {
                                // Add a timeout to the ffmpeg read task to ensure it doesn't hang if ffmpeg crashes.
                                int read = await encoder.OutputData.ReadAsync(buffer.AsMemory(totalInBuff), loopCts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), loopCts.Token);
                                if (read == 0) break;
                                totalInBuff += read;

                                int searchPos = 0;
                                while (searchPos < totalInBuff - 3)
                                {
                                    // Search for Annex-B start code (0x00 0x00 0x01 or 0x00 0x00 0x00 0x01)
                                    if (buffer[searchPos] == 0 && buffer[searchPos + 1] == 0 && buffer[searchPos + 2] == 1)
                                    {
                                        int nalStart = searchPos;
                                        if (searchPos > 0 && buffer[searchPos - 1] == 0) nalStart--;

                                        if (nalStart > 0)
                                        {
                                            await server.SendVideoFrameAsync(buffer[..nalStart], loopCts.Token);
                                            Array.Copy(buffer, nalStart, buffer, 0, totalInBuff - nalStart);
                                            totalInBuff -= nalStart;
                                            searchPos = 0; // restart search from the new start code
                                            continue;
                                        }
                                    }
                                    searchPos++;
                                }
                                if (totalInBuff > buffer.Length - 16384) totalInBuff = 0;
                            }
                        } catch { }
                    }, loopCts.Token);

                    long frameCount = 0;
                    var lastFpsLog  = DateTime.UtcNow;
                    byte[] lastFrame = new byte[capture.Width * capture.Height * 4];

                    try
                    {
                        while (!loopCts.Token.IsCancellationRequested)
                        {
                            var frameStart = DateTime.UtcNow;
                            try
                            {
                                byte[]? bgraData = capture.AcquireNextFrame(timeoutMs: FrameIntervalMs);
                                if (bgraData != null) lastFrame = bgraData;
                            }
                            catch (ResolutionChangedException)
                            {
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine("\n[Host] Resolution change detected! Resetting capture...");
                                Console.ResetColor();
                                break; 
                            }

                            await encoder.WriteFrameAsync(lastFrame, loopCts.Token);
                            frameCount++;

                            var now = DateTime.UtcNow;
                            if ((now - lastFpsLog).TotalSeconds >= 5)
                            {
                                double fps = frameCount / (now - lastFpsLog).TotalSeconds;
                                Console.WriteLine($"[Host] {fps:F1} fps | {capture.Width}x{capture.Height} | ABR: {abrLoop.CurrentTier}");
                                frameCount = 0; lastFpsLog = now;
                                stats.Fps = fps; stats.EncodeMs = encoder.LastEncodeMs;
                                stats.AbrTier = abrLoop.CurrentTier.ToString();
                                stats.BitrateKbps = encoder.CurrentBitrateKbps;
                            }

                            var elapsed = (DateTime.UtcNow - frameStart).TotalMilliseconds;
                            if (elapsed < FrameIntervalMs)
                                await Task.Delay((int)(FrameIntervalMs - elapsed), loopCts.Token);
                        }
                    }
                    finally
                    {
                        loopCts.Cancel();
                        try { await forwardTask.WaitAsync(TimeSpan.FromSeconds(1), cts.Token); } catch { }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[Host] Critical loop error: {ex.Message}. Attempting recovery...");
                Console.ResetColor();
            }

            if (cts.Token.IsCancellationRequested) break;
            await Task.Delay(1000, cts.Token); // Wait for switch to settle
        }

    Cleanup:
        InputInjector.DisposePenInjector();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[Host] Stream stopped. Goodbye!");
        Console.ResetColor();
    }
}
