using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Miningcore.Blockchain.Kaspa;

public static class KaspaBech32Encoder
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    public static string EncodeAddress(KaspaNetwork network, byte version, ReadOnlySpan<byte> payload)
    {
        if(payload.IsEmpty)
            throw new ArgumentException("Payload must not be empty", nameof(payload));

        var hrp = network.GetAddressPrefix();
        var encodedPayload = EncodePayload(hrp, version, payload);
        return string.Concat(hrp, ":", encodedPayload);
    }

    private static string EncodePayload(string hrp, byte version, ReadOnlySpan<byte> payload)
    {
        var payloadWithVersion = new byte[1 + payload.Length];
        payloadWithVersion[0] = version;
        payload.CopyTo(payloadWithVersion.AsSpan(1));

        var fiveBitPayload = Convert8To5(payloadWithVersion);
        var prefixValues = hrp.Select(c => (byte) (c & 0x1f));
        var checksum = ComputeChecksum(fiveBitPayload, prefixValues);

        Span<byte> checksumBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(checksumBytes, checksum);
        var checksumPayload = Convert8To5(checksumBytes[3..].ToArray());

        var builder = new StringBuilder(fiveBitPayload.Length + checksumPayload.Length);

        foreach(var value in fiveBitPayload)
            builder.Append(Charset[value]);

        foreach(var value in checksumPayload)
            builder.Append(Charset[value]);

        return builder.ToString();
    }

    private static ulong ComputeChecksum(IReadOnlyList<byte> payload, IEnumerable<byte> prefixValues)
    {
        var values = prefixValues
            .Concat(new byte[] {0})
            .Concat(payload)
            .Concat(new byte[8]);

        return Polymod(values);
    }

    private static ulong Polymod(IEnumerable<byte> values)
    {
        var c = 1UL;

        foreach(var d in values)
        {
            var c0 = c >> 35;
            c = ((c & 0x07ffffffffUL) << 5) ^ d;

            if((c0 & 0x01) != 0)
                c ^= 0x98f2bc8e61UL;
            if((c0 & 0x02) != 0)
                c ^= 0x79b76d99e2UL;
            if((c0 & 0x04) != 0)
                c ^= 0xf33e5fb3c4UL;
            if((c0 & 0x08) != 0)
                c ^= 0xae2eabe2a8UL;
            if((c0 & 0x10) != 0)
                c ^= 0x1e4f43e470UL;
        }

        return c ^ 1UL;
    }

    private static byte[] Convert8To5(ReadOnlySpan<byte> payload)
    {
        var padding = payload.Length % 5 == 0 ? 0 : 1;
        var fiveBit = new byte[payload.Length * 8 / 5 + padding];
        var currentIndex = 0;
        var buffer = 0;
        var bits = 0;

        foreach(var value in payload)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while(bits >= 5)
            {
                bits -= 5;
                fiveBit[currentIndex] = (byte) ((buffer >> bits) & 0x1f);
                buffer &= (1 << bits) - 1;
                currentIndex++;
            }
        }

        if(bits > 0)
            fiveBit[currentIndex] = (byte) ((buffer << (5 - bits)) & 0x1f);

        return fiveBit;
    }
}
