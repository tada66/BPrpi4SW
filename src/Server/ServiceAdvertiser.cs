using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Advertises the device control service via mDNS (multicast DNS / Bonjour / Avahi).
/// Broadcasts a _bpcontrol._tcp.local service with TXT records containing port info.
/// 
/// This is a minimal self-contained mDNS responder — no external NuGet dependency required.
/// It responds to queries for the service and periodically announces its presence.
/// </summary>
public class ServiceAdvertiser : IDisposable
{
    private const string SERVICE_TYPE = "_bpcontrol._tcp.local";
    private const int MDNS_PORT = 5353;
    private static readonly IPAddress MdnsMulticastAddress = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MdnsEndpoint = new(MdnsMulticastAddress, MDNS_PORT);

    private readonly string _hostname;
    private readonly string _instanceName;
    private readonly int _wsPort;
    private readonly int _udpPort;
    private readonly int _httpPort;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public ServiceAdvertiser(int wsPort = 4400, int udpPort = 4401, int httpPort = 4402, string? deviceName = null)
    {
        _wsPort = wsPort;
        _udpPort = udpPort;
        _httpPort = httpPort;
        _hostname = Environment.MachineName.ToLower();
        _instanceName = deviceName ?? $"StarTracker-{_hostname}";
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, MDNS_PORT));
            _udp.JoinMulticastGroup(MdnsMulticastAddress);

            _task = Task.Run(() => RunAsync(_cts.Token));
            Logger.Notice($"mDNS service advertiser started: {_instanceName}.{SERVICE_TYPE}");
            Logger.Info($"  TXT: ws={_wsPort}, udp={_udpPort}, http={_httpPort}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"mDNS advertiser failed to start: {ex.Message}. Service discovery will not be available.");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Send initial announcement
        SendAnnouncement();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Listen for queries and re-announce periodically
                var receiveTask = _udp!.ReceiveAsync(ct).AsTask();
                var delayTask = Task.Delay(TimeSpan.FromMinutes(2), ct);

                var completed = await Task.WhenAny(receiveTask, delayTask);

                if (completed == receiveTask && receiveTask.IsCompletedSuccessfully)
                {
                    var result = receiveTask.Result;
                    if (IsMdnsQuery(result.Buffer))
                    {
                        // Small delay to avoid storms
                        await Task.Delay(Random.Shared.Next(20, 120), ct);
                        SendAnnouncement();
                    }
                }
                else
                {
                    // Periodic re-announcement
                    SendAnnouncement();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Debug($"mDNS error: {ex.Message}");
                await Task.Delay(5000, ct);
            }
        }
    }

    private void SendAnnouncement()
    {
        try
        {
            byte[] packet = BuildAnnouncementPacket();
            _udp?.Send(packet, packet.Length, MdnsEndpoint);
            Logger.Trace("mDNS announcement sent.");
        }
        catch (Exception ex)
        {
            Logger.Debug($"mDNS send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Very basic check if this looks like an mDNS query (QR bit = 0, QDCOUNT > 0).
    /// </summary>
    private static bool IsMdnsQuery(byte[] data)
    {
        if (data.Length < 12) return false;
        // QR bit is the high bit of byte 2
        bool isQuery = (data[2] & 0x80) == 0;
        // QDCOUNT at bytes 4-5
        int qdcount = (data[4] << 8) | data[5];
        return isQuery && qdcount > 0;
    }

    /// <summary>
    /// Build a minimal mDNS response packet advertising our service.
    /// Contains PTR, SRV, TXT, and A records.
    /// </summary>
    private byte[] BuildAnnouncementPacket()
    {
        var packet = new List<byte>();

        // ── DNS Header (12 bytes) ──
        packet.AddRange(new byte[] { 0x00, 0x00 }); // Transaction ID
        packet.AddRange(new byte[] { 0x84, 0x00 }); // Flags: QR=1 (response), AA=1
        packet.AddRange(new byte[] { 0x00, 0x00 }); // QDCOUNT = 0
        packet.AddRange(new byte[] { 0x00, 0x04 }); // ANCOUNT = 4 (PTR, SRV, TXT, A)
        packet.AddRange(new byte[] { 0x00, 0x00 }); // NSCOUNT = 0
        packet.AddRange(new byte[] { 0x00, 0x00 }); // ARCOUNT = 0

        string serviceName = SERVICE_TYPE;
        string instanceName = $"{_instanceName}.{SERVICE_TYPE}";
        string hostName = $"{_hostname}.local";

        // Get local IP
        byte[] ipBytes = GetLocalIPBytes();

        // ── PTR Record: _bpcontrol._tcp.local → instance._bpcontrol._tcp.local ──
        WriteName(packet, serviceName);
        WriteUInt16(packet, 12);        // Type: PTR
        WriteUInt16(packet, 0x8001);    // Class: IN, cache-flush
        WriteUInt32(packet, 4500);      // TTL
        byte[] ptrRdata = EncodeName(instanceName);
        WriteUInt16(packet, (ushort)ptrRdata.Length); // RDLENGTH
        packet.AddRange(ptrRdata);

        // ── SRV Record: instance._bpcontrol._tcp.local → hostname:wsPort ──
        WriteName(packet, instanceName);
        WriteUInt16(packet, 33);        // Type: SRV
        WriteUInt16(packet, 0x8001);    // Class: IN, cache-flush
        WriteUInt32(packet, 120);       // TTL
        byte[] target = EncodeName(hostName);
        WriteUInt16(packet, (ushort)(6 + target.Length)); // RDLENGTH
        WriteUInt16(packet, 0);         // Priority
        WriteUInt16(packet, 0);         // Weight
        WriteUInt16(packet, (ushort)_wsPort); // Port
        packet.AddRange(target);

        // ── TXT Record ──
        WriteName(packet, instanceName);
        WriteUInt16(packet, 16);        // Type: TXT
        WriteUInt16(packet, 0x8001);    // Class: IN, cache-flush
        WriteUInt32(packet, 4500);      // TTL
        byte[] txtRdata = BuildTxtRdata();
        WriteUInt16(packet, (ushort)txtRdata.Length);
        packet.AddRange(txtRdata);

        // ── A Record: hostname.local → IP ──
        WriteName(packet, hostName);
        WriteUInt16(packet, 1);         // Type: A
        WriteUInt16(packet, 0x8001);    // Class: IN, cache-flush
        WriteUInt32(packet, 120);       // TTL
        WriteUInt16(packet, 4);         // RDLENGTH
        packet.AddRange(ipBytes);

        return packet.ToArray();
    }

    private byte[] BuildTxtRdata()
    {
        var entries = new[]
        {
            $"ws={_wsPort}",
            $"udp={_udpPort}",
            $"http={_httpPort}",
            $"name={_instanceName}"
        };

        var rdata = new List<byte>();
        foreach (var entry in entries)
        {
            byte[] entryBytes = Encoding.UTF8.GetBytes(entry);
            rdata.Add((byte)entryBytes.Length);
            rdata.AddRange(entryBytes);
        }
        return rdata.ToArray();
    }

    private static byte[] EncodeName(string name)
    {
        var result = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            byte[] labelBytes = Encoding.UTF8.GetBytes(label);
            result.Add((byte)labelBytes.Length);
            result.AddRange(labelBytes);
        }
        result.Add(0x00); // Terminator
        return result.ToArray();
    }

    private static void WriteName(List<byte> packet, string name)
    {
        packet.AddRange(EncodeName(name));
    }

    private static void WriteUInt16(List<byte> packet, ushort value)
    {
        packet.Add((byte)(value >> 8));
        packet.Add((byte)(value & 0xFF));
    }

    private static void WriteUInt16(List<byte> packet, int value) => WriteUInt16(packet, (ushort)value);

    private static void WriteUInt32(List<byte> packet, uint value)
    {
        packet.Add((byte)(value >> 24));
        packet.Add((byte)((value >> 16) & 0xFF));
        packet.Add((byte)((value >> 8) & 0xFF));
        packet.Add((byte)(value & 0xFF));
    }

    private static byte[] GetLocalIPBytes()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var iface in interfaces)
            {
                var ip = iface.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address)
                    .FirstOrDefault();
                if (ip != null) return ip.GetAddressBytes();
            }
        }
        catch { }
        return new byte[] { 127, 0, 0, 1 };
    }
}
