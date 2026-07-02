using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RdpManager.Core
{
    /// <summary>
    /// Wake-on-LAN: parsowanie adresu MAC i budowa „magic packet"
    /// (6×FF + 16× MAC). Wysyłka broadcastem UDP na porty 9 i 7.
    /// </summary>
    public static class WakeOnLan
    {
        /// <summary>Akceptuje formaty AA:BB:CC:DD:EE:FF, AA-BB-…, AABB.CCDD.EEFF i gołe 12 hex.</summary>
        public static bool TryParseMac(string text, out byte[] mac)
        {
            mac = null;
            string hex = (text ?? "").Replace(":", "").Replace("-", "").Replace(".", "").Replace(" ", "").Trim();
            if (hex.Length != 12) return false;

            var result = new byte[6];
            for (int i = 0; i < 6; i++)
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out result[i])) return false;
            mac = result;
            return true;
        }

        public static byte[] BuildMagicPacket(byte[] mac)
        {
            if (mac == null || mac.Length != 6) throw new ArgumentException("MAC musi mieć 6 bajtów.", nameof(mac));
            var packet = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int r = 0; r < 16; r++) Buffer.BlockCopy(mac, 0, packet, 6 + r * 6, 6);
            return packet;
        }

        /// <summary>Wysyła magic packet broadcastem (UDP 9 i 7 — różne płyty słuchają na różnych).</summary>
        public static void Send(byte[] mac)
        {
            var packet = BuildMagicPacket(mac);
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Broadcast, 7));
            }
        }
    }
}
