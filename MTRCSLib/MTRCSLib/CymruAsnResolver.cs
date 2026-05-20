using System.Net;
using System.Net.Sockets;
using MTRCSLib.Abstractions;

namespace MTRCSLib;

/// <summary>
/// Resolves ASN information using Team Cymru's DNS TXT service at <c>origin.asn.cymru.com</c>.
///
/// Query format: reverse the IPv4 octets and append <c>.origin.asn.cymru.com</c>.
/// Example: 8.8.8.8 → <c>8.8.8.8.origin.asn.cymru.com</c> TXT
/// Response: <c>"15169 | 8.8.8.0/24 | US | arin | 1992-12-01"</c>
///
/// The ASN name is then resolved via <c>{asn}.asn.cymru.com</c> TXT.
/// Response: <c>"15169 | US | arin | 1992-12-01 | GOOGLE, US"</c>
/// </summary>
public sealed class CymruAsnResolver : IAsnResolver
{
    private const string OriginSuffix = ".origin.asn.cymru.com";
    private const string AsnSuffix    = ".asn.cymru.com";

    /// <inheritdoc/>
    public async ValueTask<AsnInfo?> ResolveAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        if (!NetworkUtils.IsIPv4(address))
            return null;

        try
        {
            string reversed = ReverseIp(address);
            string originHost = reversed + OriginSuffix;

            string? originTxt = await QueryFirstTxtAsync(originHost, cancellationToken).ConfigureAwait(false);
            if (originTxt is null)
                return null;

            // Expected format: "ASN | prefix | CC | registry | date"
            // Multi-origin prefixes may return space-separated ASNs (e.g. "15169 64512"); use the first.
            string asnField = ParseField(originTxt, 0);
            if (string.IsNullOrWhiteSpace(asnField))
                return null;

            int spaceIdx = asnField.IndexOf(' ');
            string asnNumber = spaceIdx > 0 ? asnField[..spaceIdx] : asnField;

            // CC field (e.g. "US") is always present; use it as a reliable fallback description.
            string cc = ParseField(originTxt, 2);

            // Look up the AS name.
            string asnHost = asnNumber + AsnSuffix;
            string? asnTxt = await QueryFirstTxtAsync(asnHost, cancellationToken).ConfigureAwait(false);

            // Expected format: "ASN | CC | registry | date | description"
            string description = asnTxt is not null
                ? ParseField(asnTxt, 4)
                : string.Empty;

            // Trim trailing ", CC" suffixes that Cymru sometimes appends (e.g. "GOOGLE, US").
            int comma = description.LastIndexOf(',');
            if (comma > 0 && description.Length - comma <= 4)
                description = description[..comma].TrimEnd();

            // Fall back to the CC country code when the name lookup yields nothing.
            if (string.IsNullOrWhiteSpace(description))
                description = cc;

            return new AsnInfo($"AS{asnNumber}", description);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ASN resolution is best-effort; swallow transient DNS/network failures.
            return null;
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Reverses the octets of an IPv4 address, e.g. "8.8.8.8" → "8.8.8.8".</summary>
    private static string ReverseIp(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}";
    }

    /// <summary>
    /// Resolves TXT records for <paramref name="host"/> and returns the first record's value,
    /// or <see langword="null"/> if none found.
    /// </summary>
    private static async ValueTask<string?> QueryFirstTxtAsync(string host, CancellationToken ct)
    {
        // .NET's managed DNS does not expose TXT record queries directly.
        // We use a lightweight raw UDP DNS query to avoid pulling in a third-party library.
        byte[] query = BuildTxtQuery(host);

        using var udpClient = new UdpClient();
        udpClient.Client.ReceiveTimeout = 3000;

        // Use the system's default DNS server (8.8.8.8 as public fallback).
        IPEndPoint dnsEndpoint = new(IPAddress.Parse("8.8.8.8"), 53);

        await udpClient.SendAsync(query, query.Length, dnsEndpoint).ConfigureAwait(false);

        UdpReceiveResult received;
        try
        {
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(3000);
            received = await udpClient.ReceiveAsync(cts2.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // Timeout — not a user cancel.
        }

        return ParseFirstTxtRecord(received.Buffer);
    }

    /// <summary>Builds a minimal DNS TXT query packet for <paramref name="host"/>.</summary>
    private static byte[] BuildTxtQuery(string host)
    {
        // Header: ID=0x1337, QR=0 (query), OPCODE=0, RD=1, QDCOUNT=1
        // Question: host name, QTYPE=TXT(16), QCLASS=IN(1)
        using var ms = new System.IO.MemoryStream(512);

        // Transaction ID
        ms.WriteByte(0x13); ms.WriteByte(0x37);
        // Flags: standard query, recursion desired
        ms.WriteByte(0x01); ms.WriteByte(0x00);
        // QDCOUNT=1
        ms.WriteByte(0x00); ms.WriteByte(0x01);
        // ANCOUNT, NSCOUNT, ARCOUNT = 0
        ms.WriteByte(0x00); ms.WriteByte(0x00);
        ms.WriteByte(0x00); ms.WriteByte(0x00);
        ms.WriteByte(0x00); ms.WriteByte(0x00);

        // Encode QNAME
        foreach (string label in host.Split('.'))
        {
            byte[] labelBytes = System.Text.Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)labelBytes.Length);
            ms.Write(labelBytes);
        }
        ms.WriteByte(0); // root label

        // QTYPE=TXT(16), QCLASS=IN(1)
        ms.WriteByte(0x00); ms.WriteByte(0x10);
        ms.WriteByte(0x00); ms.WriteByte(0x01);

        return ms.ToArray();
    }

    /// <summary>Extracts the first TXT record string from a raw DNS response.</summary>
    private static string? ParseFirstTxtRecord(byte[] response)
    {
        if (response.Length < 12)
            return null;

        int qdCount = (response[4] << 8) | response[5];
        int anCount = (response[6] << 8) | response[7];

        if (anCount == 0)
            return null;

        int pos = 12;

        // Skip questions
        for (int q = 0; q < qdCount; q++)
        {
            pos = SkipName(response, pos);
            pos += 4; // QTYPE + QCLASS
        }

        // Read first answer
        pos = SkipName(response, pos); // NAME (may be pointer)
        if (pos + 10 > response.Length) return null;

        int type   = (response[pos] << 8) | response[pos + 1];
        int rdLen  = (response[pos + 8] << 8) | response[pos + 9];
        pos += 10; // TYPE + CLASS + TTL + RDLENGTH

        if (type != 16) // TXT
            return null;

        if (pos + rdLen > response.Length)
            return null;

        // TXT RDATA: one or more <length, data> strings
        int end = pos + rdLen;
        var sb = new System.Text.StringBuilder();
        while (pos < end)
        {
            int strLen = response[pos++];
            sb.Append(System.Text.Encoding.ASCII.GetString(response, pos, strLen));
            pos += strLen;
        }

        return sb.ToString();
    }

    private static int SkipName(byte[] buf, int pos)
    {
        while (pos < buf.Length)
        {
            int len = buf[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0) { pos += 2; break; } // pointer
            pos += 1 + len;
        }
        return pos;
    }

    private static string ParseField(string txt, int fieldIndex)
    {
        ReadOnlySpan<char> span = txt.AsSpan();
        int field = 0;
        int start = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || span[i] == '|')
            {
                if (field == fieldIndex)
                    return span[start..i].Trim().ToString();
                field++;
                start = i + 1;
            }
        }
        return string.Empty;
    }
}
