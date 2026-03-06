using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

/// <summary>
/// Checks for a real network connection (WiFi/Ethernet with routable IPv4).
/// If none is found, creates a Wi-Fi hotspot using NetworkManager (nmcli) so
/// clients can always reach the device.
///
/// Hotspot defaults:
///   SSID     : BPControl
///   Password : bpcontrol1
///   IP       : 192.168.4.1  (NM assigns this automatically for shared connections)
///   Interface: wlan0
/// </summary>
public static class HotspotManager
{
    public const string HotspotSsid         = "BPControl";
    public const string HotspotPassword     = "bpcontrol1";
    private const string HotspotInterface    = "wlan0";
    private const string HotspotConnectionName = "bp-hotspot";

    // IP that NetworkManager assigns to the hotspot interface (NM default for shared mode)
    public const string HotspotIp = "10.42.0.1";

    /// <summary>
    /// Checks connectivity and, if needed, starts a hotspot.
    /// Returns the IP address the server should bind/advertise on.
    /// </summary>
    public static async Task<string> EnsureNetworkAsync()
    {
        string? existingIp = GetRoutableIpAddress();

        if (existingIp != null)
        {
            Logger.Notice($"Network available, IP: {existingIp}");
            return existingIp;
        }

        Logger.Notice("No routable network found. Starting Wi-Fi hotspot...");
        bool ok = await StartHotspotAsync();

        if (ok)
        {
            Logger.Notice($"Hotspot started — SSID: {HotspotSsid}, Password: {HotspotPassword}, IP: {HotspotIp}");
            return HotspotIp;
        }

        Logger.Error("Failed to start hotspot. Server will use fallback IP.");
        return "127.0.0.1";
    }

    /// <summary>
    /// Returns the first routable IPv4 address (not loopback, not link-local),
    /// or null if the device has no real network connection.
    /// </summary>
    public static string? GetRoutableIpAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .FirstOrDefault(ip =>
                    !ip.StartsWith("127.")   &&   // loopback
                    !ip.StartsWith("169.254")); // link-local / no DHCP
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a short connection-type label for the given resolved IP:
    /// "Hotspot", "WiFi", "ETH", or "Network".
    /// On Linux, NetworkInterfaceType is unreliable for wireless adapters (always
    /// reports Ethernet), so we use the interface name and sysfs instead.
    /// </summary>
    public static string GetConnectionType(string ip)
    {
        if (ip == HotspotIp) return "Hotspot";
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ifaceIp = iface.GetIPProperties().UnicastAddresses
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .FirstOrDefault(a => !a.StartsWith("127.") && !a.StartsWith("169.254"));

                if (ifaceIp != ip) continue;

                return IsWirelessInterface(iface.Name) ? "WiFi" : "ETH";
            }
        }
        catch { }
        return "Network";
    }

    /// <summary>
    /// Determines whether a network interface is wireless.
    /// Checks sysfs on Linux (most reliable), falls back to name prefix heuristics,
    /// and finally the .NET NetworkInterfaceType as a last resort.
    /// </summary>
    private static bool IsWirelessInterface(string name)
    {
        // Linux sysfs: /sys/class/net/{name}/wireless exists only for WiFi adapters
        if (OperatingSystem.IsLinux())
        {
            if (Directory.Exists($"/sys/class/net/{name}/wireless"))
                return true;
            // Some drivers use 80211 phy instead — check phy80211 symlink
            if (File.Exists($"/sys/class/net/{name}/phy80211"))
                return true;
        }

        // Name-prefix heuristic (wlan0, wlp2s0, wlx..., ap0, etc.)
        if (name.StartsWith("wl", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ap",  StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if we are currently running as the hotspot (no upstream network).
    /// </summary>
    public static bool IsHotspotActive()
        => GetRoutableIpAddress() == null;

    // ── nmcli helpers ──────────────────────────────────────────────────────────

    private static async Task<bool> StartHotspotAsync()
    {
        // Remove any stale profile from a previous run
        await RunNmcli($"connection delete \"{HotspotConnectionName}\"", ignoreErrors: true);

        // Create a new hotspot profile (mode=ap, ipv4.method=shared → NM handles DHCP)
        int rc = await RunNmcli(
            $"connection add type wifi ifname {HotspotInterface} " +
            $"con-name \"{HotspotConnectionName}\" " +
            $"autoconnect no ssid \"{HotspotSsid}\" " +
            $"802-11-wireless.mode ap " +
            $"802-11-wireless-security.key-mgmt wpa-psk " +
            $"802-11-wireless-security.psk \"{HotspotPassword}\" " +
            $"ipv4.method shared");

        if (rc != 0)
        {
            Logger.Error("nmcli: failed to create hotspot connection profile.");
            return false;
        }

        rc = await RunNmcli($"connection up \"{HotspotConnectionName}\"");
        if (rc != 0)
        {
            Logger.Error("nmcli: failed to bring up hotspot connection.");
            return false;
        }

        // Give NetworkManager a moment to assign the interface IP
        await Task.Delay(2000);
        return true;
    }

    private static async Task<int> RunNmcli(string args, bool ignoreErrors = false)
    {
        try
        {
            var psi = new ProcessStartInfo("nmcli", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start nmcli process.");

            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            int code = proc.ExitCode;

            if (!string.IsNullOrWhiteSpace(stdout))
                Logger.Debug($"nmcli: {stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderr) && (code != 0 || !ignoreErrors))
                Logger.Warn($"nmcli stderr: {stderr.Trim()}");

            if (code != 0 && !ignoreErrors)
                Logger.Warn($"nmcli exited with code {code} (args: {args})");

            return code;
        }
        catch (Exception ex)
        {
            Logger.Error($"nmcli error: {ex.Message}");
            return -1;
        }
    }
}
