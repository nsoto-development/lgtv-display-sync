using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LgtvDisplaySync.App;

// Wake-on-LAN. Sends the magic packet to the subnet-directed broadcast of the local NIC
// whose subnet contains the TV (e.g. 192.168.100.255), sourced from that NIC — not the
// all-NICs / network-address broadcast, which is what leaks WoL out the VPN adapter.
internal static class Wol
{
    public static void Send(string macAddress, string targetIp, Action<string> log)
    {
        var mac = ParseMac(macAddress);
        if (mac is null) { log($"WoL: bad MAC '{macAddress}'"); return; }

        var magic = new byte[102];
        for (var i = 0; i < 6; i++) magic[i] = 0xFF;
        for (var i = 1; i <= 16; i++) Array.Copy(mac, 0, magic, i * 6, 6);

        var target = IPAddress.Parse(targetIp);
        var sent = false;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var local = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask?.GetAddressBytes();
                if (mask is null || mask.Length != 4) continue;
                var tgt = target.GetAddressBytes();
                if (!SameSubnet(local, tgt, mask)) continue;

                // directed broadcast = network | ~mask
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++) bcast[i] = (byte)(local[i] & mask[i] | ~mask[i]);
                try
                {
                    using var udp = new UdpClient(new IPEndPoint(ua.Address, 0)) { EnableBroadcast = true };
                    udp.Send(magic, magic.Length, new IPEndPoint(new IPAddress(bcast), 9));
                    log($"WoL: sent to {new IPAddress(bcast)}:9 from {ua.Address} ({ni.Name})");
                    sent = true;
                }
                catch (Exception ex) { log($"WoL: send failed from {ua.Address}: {ex.Message}"); }
            }
        }

        // Unicast fallback straight at the TV (Quick Start+ keeps it network-alive).
        try
        {
            using var udp = new UdpClient();
            udp.Send(magic, magic.Length, new IPEndPoint(target, 9));
            if (!sent) log($"WoL: sent unicast to {targetIp}:9 (no matching subnet NIC)");
        }
        catch { /* ignore */ }
    }

    private static bool SameSubnet(byte[] a, byte[] b, byte[] mask)
    {
        for (var i = 0; i < 4; i++)
            if ((a[i] & mask[i]) != (b[i] & mask[i])) return false;
        return true;
    }

    private static byte[]? ParseMac(string mac)
    {
        var parts = mac.Split(':', '-');
        if (parts.Length != 6) return null;
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                return null;
        return bytes;
    }
}
