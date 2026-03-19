using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TabMirror.Host.Discovery;

/// <summary>
/// Advertises Tab Mirror on the local network using a minimal hand-rolled mDNS
/// (RFC 6762 / DNS-SD RFC 6763) implementation over UDP multicast.
///
/// No external packages required — pure .NET sockets.
///
/// Android's NsdManager can discover this service type automatically.
/// Service type: _tabmirror._tcp.local.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private const string MulticastGroupIPv4 = "224.0.0.251";
    private const int    MdnsPort           = 5353;

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _announceTask;
    private bool _disposed;

    // Pre-built DNS response packet components
    private byte[]? _responsePacket;

    public async Task StartAdvertisingAsync(int videoPort, int controlPort,
        string instanceName = "TabMirror")
    {
        try
        {
            _responsePacket = BuildMdnsResponse(instanceName, videoPort, controlPort);

            _client = new UdpClient();
            _client.Client.SetSocketOption(SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

            var mdnsGroup = IPAddress.Parse(MulticastGroupIPv4);
            _client.JoinMulticastGroup(mdnsGroup);
            _client.MulticastLoopback = false;

            _cts = new CancellationTokenSource();
            _announceTask = Task.Run(() => AnnounceLoop(_cts.Token));

            Console.WriteLine($"[Discovery] mDNS advertising '{instanceName}' on port {videoPort}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Discovery] mDNS failed: {ex.Message}. Use manual IP entry.");
        }
        await Task.CompletedTask;
    }

    private async Task AnnounceLoop(CancellationToken ct)
    {
        if (_client == null || _responsePacket == null) return;
        var endpoint = new IPEndPoint(IPAddress.Parse(MulticastGroupIPv4), MdnsPort);

        // Announce immediately on startup, then every 2 minutes (RFC 6762 §11.3)
        bool[] intervals = [ true ];
        int[] delaysMs = [ 0, 1000, 2000, 60_000 ];
        int phase = 0;

        while (!ct.IsCancellationRequested)
        {
            await _client.SendAsync(_responsePacket, _responsePacket.Length, endpoint);
            int delay = phase < delaysMs.Length ? delaysMs[phase++] : 120_000;
            await Task.Delay(delay, ct).ContinueWith(_ => { }); // suppress cancellation
        }
    }

    /// <summary>
    /// Builds a minimal mDNS DNS-SD response advertising the PTR + SRV + TXT records.
    /// This is a hand-rolled DNS packet in Annex-B format.
    /// </summary>
    private static byte[] BuildMdnsResponse(string instance, int videoPort, int controlPort)
    {
        // DNS message: header (12 bytes) + answer records
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);

        // Header
        bw.Write((ushort)0);           // ID = 0 (multicast)
        bw.Write(ToBE16(0x8400));      // QR=1 (response), AA=1
        bw.Write(ToBE16(0));           // QDCOUNT = 0
        bw.Write(ToBE16(3));           // ANCOUNT = 3 (PTR + SRV + TXT)
        bw.Write(ToBE16(0));           // NSCOUNT = 0
        bw.Write(ToBE16(0));           // ARCOUNT = 0

        // ── PTR record: _tabmirror._tcp.local. → <instance>._tabmirror._tcp.local. ──
        WriteDnsName(bw, "_tabmirror._tcp.local");
        bw.Write(ToBE16(12));          // TYPE = PTR
        bw.Write(ToBE16(1));           // CLASS = IN  (0x8001 = cache-flush | IN)
        bw.Write(ToBE32(4500));        // TTL = 75 minutes
        string fullInstance = $"{instance}._tabmirror._tcp.local";
        var ptrRData = EncodeDnsName(fullInstance);
        bw.Write(ToBE16((ushort)ptrRData.Length));
        bw.Write(ptrRData);

        // ── SRV record: <instance>._tabmirror._tcp.local. → host:port ──
        WriteDnsName(bw, fullInstance);
        bw.Write(ToBE16(33));          // TYPE = SRV
        bw.Write(ToBE16(0x8001));      // CLASS = cache-flush | IN
        bw.Write(ToBE32(120));         // TTL = 2 minutes
        string hostTarget = Dns.GetHostName() + ".local";
        var targetBytes = EncodeDnsName(hostTarget);
        bw.Write(ToBE16((ushort)(6 + targetBytes.Length))); // RDLENGTH
        bw.Write(ToBE16(0));           // Priority
        bw.Write(ToBE16(0));           // Weight
        bw.Write(ToBE16((ushort)videoPort)); // Port
        bw.Write(targetBytes);

        // ── TXT record: extra metadata ──
        WriteDnsName(bw, fullInstance);
        bw.Write(ToBE16(16));          // TYPE = TXT
        bw.Write(ToBE16(0x8001));      // CLASS = cache-flush | IN
        bw.Write(ToBE32(4500));        // TTL
        string txtData = $"video_port={videoPort}\tcontrol_port={controlPort}\tversion=1.0";
        byte[] txtBytes = EncodeTxtRecord(txtData);
        bw.Write(ToBE16((ushort)txtBytes.Length));
        bw.Write(txtBytes);

        return ms.ToArray();
    }

    private static void WriteDnsName(System.IO.BinaryWriter bw, string name)
        => bw.Write(EncodeDnsName(name));

    private static byte[] EncodeDnsName(string fqdn)
    {
        var result = new System.IO.MemoryStream();
        foreach (var label in fqdn.TrimEnd('.').Split('.'))
        {
            var lb = System.Text.Encoding.ASCII.GetBytes(label);
            result.WriteByte((byte)lb.Length);
            result.Write(lb);
        }
        result.WriteByte(0); // root
        return result.ToArray();
    }

    private static byte[] EncodeTxtRecord(string data)
    {
        var result = new System.IO.MemoryStream();
        foreach (var kv in data.Split('\t'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(kv);
            result.WriteByte((byte)bytes.Length);
            result.Write(bytes);
        }
        return result.ToArray();
    }

    private static byte[] ToBE16(ushort v) => [(byte)(v >> 8), (byte)v];
    private static byte[] ToBE32(uint v)   => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _client?.Dispose();
    }
}
