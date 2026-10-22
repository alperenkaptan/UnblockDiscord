using System.Buffers.Binary;
using System.Text;

namespace UnblockDiscord;

internal static class DiscordTraffic
{
    // Suffix matching includes updates, gateway, CDN, media and regional voice hosts.
    internal static readonly string[] Domains =
        ["discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media",
         "discordcdn.com", "discord.gift", "discord.new", "discordstatus.com", "dis.gd"];

    internal static bool IsDiscordHost(string host) => Domains.Any(domain =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    // Input is the handshake payload, without TLS record headers. No TLS decryption.
    internal static string? ServerName(ReadOnlySpan<byte> hello)
    {
        if (hello.Length < 39 || hello[0] != 1) return null;
        var end = 4 + (hello[1] << 16 | hello[2] << 8 | hello[3]);
        if (end > hello.Length) return null;
        var offset = 38;
        if (!Skip(hello, ref offset, 1) || !Skip(hello, ref offset, 2) ||
            !Skip(hello, ref offset, 1) || offset + 2 > end) return null;
        var extensionsEnd = offset + 2 + BinaryPrimitives.ReadUInt16BigEndian(hello[offset..]);
        offset += 2;
        if (extensionsEnd > end) return null;
        while (offset + 4 <= extensionsEnd)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(hello[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(hello[(offset + 2)..]);
            offset += 4;
            if (offset + length > extensionsEnd) return null;
            if (type == 0 && length >= 5)
            {
                var listEnd = offset + 2 + BinaryPrimitives.ReadUInt16BigEndian(hello[offset..]);
                if (listEnd != offset + length) return null;
                var item = offset + 2;
                while (item + 3 <= listEnd)
                {
                    var nameType = hello[item];
                    var nameLength = BinaryPrimitives.ReadUInt16BigEndian(hello[(item + 1)..]);
                    item += 3;
                    if (item + nameLength > listEnd) return null;
                    if (nameType == 0)
                    {
                        var name = Encoding.ASCII.GetString(hello.Slice(item, nameLength));
                        return Uri.CheckHostName(name) == UriHostNameType.Dns ? name : null;
                    }
                    item += nameLength;
                }
            }
            offset += length;
        }
        return null;
    }

    private static bool Skip(ReadOnlySpan<byte> data, ref int offset, int size)
    {
        if (offset + size > data.Length) return false;
        var length = size == 1 ? data[offset] : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += size + length;
        return offset <= data.Length;
    }
}
