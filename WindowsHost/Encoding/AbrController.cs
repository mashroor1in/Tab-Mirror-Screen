using System;
using System.Threading;
using System.Threading.Tasks;

namespace TabMirror.Host.Encoding;

/// <summary>
/// Adaptive Bitrate (ABR) controller.
///
/// Monitors the TCP send-queue depth of the StreamServer every 500ms.
/// When the queue grows (network congested) → lowers encoder bitrate.
/// When the queue drains (network recovered) → steps bitrate back up.
///
/// Three-tier model:
///   HIGH   → 8 Mbps  (default, excellent conditions)
///   MEDIUM → 5 Mbps  (moderate congestion)
///   LOW    → 2.5 Mbps (severe congestion or Wi-Fi degradation)
/// </summary>
public sealed class AbrController : IDisposable
{
    public enum Tier { High, Medium, Low }

    // Thresholds — bytes pending in the OS socket send buffer
    private const int MediumThreshold = 512 * 1024;   // 512 KB → drop to Medium
    private const int LowThreshold    = 2  * 1024 * 1024; // 2 MB → drop to Low
    private const int RecoverThreshold = 64 * 1024;    // 64 KB → step up

    private readonly FfmpegEncoder _encoder;
    private readonly Func<long> _getPendingBytes;
    private Tier _currentTier = Tier.High;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public Tier CurrentTier => _currentTier;
    
    // Expose current bitrate for stats
    public int CurrentBitrateKbps => _currentTier switch
    {
        Tier.High   => 8000,
        Tier.Medium => 5000,
        Tier.Low    => 2500,
        _ => 8000
    };

    public AbrController(FfmpegEncoder encoder, Func<long> getPendingBytes)
    {
        _encoder = encoder;
        _getPendingBytes = getPendingBytes;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(500, ct);

            long pending = _getPendingBytes();
            Tier newTier = _currentTier;

            // Step DOWN on congestion
            if (pending > LowThreshold)
                newTier = Tier.Low;
            else if (pending > MediumThreshold)
                newTier = Tier.Medium >= _currentTier ? _currentTier : Tier.Medium;

            // Step UP when network recovers — only one tier at a time
            if (pending < RecoverThreshold && _currentTier != Tier.High)
                newTier = _currentTier == Tier.Low ? Tier.Medium : Tier.High;

            if (newTier != _currentTier)
            {
                _currentTier = newTier;
                int kbps = CurrentBitrateKbps;
                Console.WriteLine($"[ABR] Tier → {_currentTier} ({kbps} kbps) | Pending: {pending / 1024} KB");
                _encoder.SetBitrate(kbps);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _task?.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Expected on cancellation
        }
        _cts?.Dispose();
    }
}
