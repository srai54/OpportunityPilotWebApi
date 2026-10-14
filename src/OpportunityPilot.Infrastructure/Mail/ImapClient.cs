using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using OpportunityPilot.Application.Mail;

namespace OpportunityPilot.Infrastructure.Mail;

/// <summary>The IMAP server said no (<see cref="LoginRefused"/> for a refused LOGIN) or answered something this client cannot read. The message is internal, never shown.</summary>
public sealed class ImapException(string message, bool loginRefused = false) : Exception(message)
{
    public bool LoginRefused { get; } = loginRefused;
}

/// <summary>
/// Just enough IMAP4rev1 (RFC 3501) to read message headers, over implicit TLS: LOGIN, EXAMINE INBOX (read-only), UID SEARCH SINCE,
/// UID FETCH with BODY.PEEK (so nothing is marked read) and LOGOUT. Handles <c>{n}</c> literals and untagged responses. Never sends
/// STORE, COPY, EXPUNGE or SELECT, so the mailbox cannot change. The password is never logged or put in an exception.
/// </summary>
public sealed partial class ImapClient : IAsyncDisposable
{
    public const string HeaderFields = "FROM SUBJECT DATE IN-REPLY-TO REFERENCES MESSAGE-ID AUTO-SUBMITTED";
    private const int MaxLineBytes = 64 * 1024;
    private const int MaxLiteralBytes = 256 * 1024;

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _start, _end, _tag;

    private ImapClient(TcpClient tcp, Stream stream) { _tcp = tcp; _stream = stream; }

    /// <summary>One server response: its text (literals left as <c>{n}</c>) and the literal payloads in order.</summary>
    public sealed record Response(string Text, IReadOnlyList<byte[]> Literals);

    /// <summary>Connects to exactly these addresses and checks the certificate against <paramref name="host"/> (or the test-only callback).</summary>
    public static async Task<ImapClient> ConnectAsync(IPAddress[] addresses, string host, int port, RemoteCertificateValidationCallback? certificateCheck, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(addresses, port, ct);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, RemoteCertificateValidationCallback = certificateCheck }, ct);
            var client = new ImapClient(tcp, ssl);
            var greeting = await client.ReadResponseAsync(ct);
            if (!greeting.Text.StartsWith("* OK", StringComparison.OrdinalIgnoreCase) && !greeting.Text.StartsWith("* PREAUTH", StringComparison.OrdinalIgnoreCase))
                throw new ImapException("The server did not greet with OK.");
            return client;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Logs in, opens INBOX read-only, finds messages since that day and fetches their headers (the newest <paramref name="max"/>), then logs out.
    /// </summary>
    public static async Task<IReadOnlyList<MailHeader>> ReadHeadersAsync(
        IPAddress[] addresses, string host, int port, string username, string password, DateTime sinceUtc, int max,
        RemoteCertificateValidationCallback? certificateCheck, CancellationToken ct)
    {
        var user = Quote(username) ?? throw new ArgumentException("The username cannot be sent to an IMAP server.", nameof(username));
        var pass = Quote(password) ?? throw new ArgumentException("The password cannot be sent to an IMAP server.", nameof(password));
        await using var client = await ConnectAsync(addresses, host, port, certificateCheck, ct);

        var (status, _) = await client.CommandAsync($"LOGIN {user} {pass}", ct);
        if (status != "OK") throw new ImapException("LOGIN was refused.", loginRefused: status == "NO");
        await client.ExpectOkAsync("EXAMINE INBOX", ct);

        var since = sinceUtc.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture);
        var search = await client.ExpectOkAsync($"UID SEARCH SINCE {since}", ct);
        var uids = search.Where(r => r.Text.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Text["* SEARCH".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(s => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? u : 0u)
            .Where(u => u > 0).Distinct().Order().ToList();
        if (uids.Count > max) uids = uids.Skip(uids.Count - max).ToList();

        var headers = new List<MailHeader>();
        if (uids.Count > 0)
        {
            var fetched = await client.ExpectOkAsync($"UID FETCH {UidSet(uids)} (UID INTERNALDATE BODY.PEEK[HEADER.FIELDS ({HeaderFields})])", ct);
            foreach (var response in fetched)
                if (ParseFetch(response) is { } header) headers.Add(header);
        }
        await client.LogoutAsync(ct);
        return headers.OrderByDescending(h => h.ReceivedAt).ToList();
    }

    /// <summary>An IMAP quoted string, or null when the text has a line break or NUL (which could inject a command) or is not 7-bit.</summary>
    public static string? Quote(string? value)
    {
        if (value is null || value.Any(c => c is '\r' or '\n' or '\0' || c > 0x7E || c < 0x20 && c != '\t')) return null;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Sorted UIDs as a compact set: 1,2,3,7 → 1:3,7.</summary>
    public static string UidSet(IReadOnlyList<uint> sorted)
    {
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i];
            while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1) i++;
            parts.Add(start == sorted[i] ? start.ToString(CultureInfo.InvariantCulture) : $"{start}:{sorted[i]}");
        }
        return string.Join(',', parts);
    }

    private async Task<List<Response>> ExpectOkAsync(string command, CancellationToken ct)
    {
        var (status, untagged) = await CommandAsync(command, ct);
        if (status != "OK") throw new ImapException($"{command.Split(' ')[0]} answered {status}.");
        return untagged;
    }

    /// <summary>Sends one tagged command and collects untagged responses until its tagged OK, NO or BAD.</summary>
    private async Task<(string Status, List<Response> Untagged)> CommandAsync(string command, CancellationToken ct)
    {
        var tag = $"A{++_tag:D3}";
        await _stream.WriteAsync(Encoding.ASCII.GetBytes($"{tag} {command}\r\n"), ct);
        await _stream.FlushAsync(ct);
        var untagged = new List<Response>();
        while (true)
        {
            var response = await ReadResponseAsync(ct);
            if (response.Text.StartsWith(tag + " ", StringComparison.Ordinal))
            {
                var status = response.Text[(tag.Length + 1)..].Split(' ', 2)[0].ToUpperInvariant();
                return (status is "OK" or "NO" or "BAD" ? status : "BAD", untagged);
            }
            if (response.Text.StartsWith('+')) throw new ImapException("The server asked for more input than this client sends.");
            untagged.Add(response);
        }
    }

    private async Task LogoutAsync(CancellationToken ct)
    {
        try { await CommandAsync("LOGOUT", ct); }
        catch (Exception e) when (e is ImapException or IOException) { /* the server may close straight after BYE */ }
    }

    /// <summary>Reads one response line; a line ending in <c>{n}</c> is followed by n bytes of literal and then the rest of the response.</summary>
    public async Task<Response> ReadResponseAsync(CancellationToken ct)
    {
        var text = new StringBuilder();
        var literals = new List<byte[]>();
        while (true)
        {
            var line = await ReadLineAsync(ct);
            text.Append(line);
            var match = LiteralAtEnd().Match(line);
            if (!match.Success) return new(text.ToString(), literals);
            var size = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (size > MaxLiteralBytes || literals.Sum(l => l.Length) + size > MaxLiteralBytes * 4) throw new ImapException("A literal was larger than this client accepts.");
            literals.Add(await ReadExactAsync(size, ct));
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var line = new List<byte>();
        while (true)
        {
            if (_start == _end) await FillAsync(ct);
            var span = _buffer.AsSpan(_start, _end - _start);
            var lf = span.IndexOf((byte)'\n');
            if (lf < 0)
            {
                line.AddRange(span.ToArray());
                _start = _end;
            }
            else
            {
                line.AddRange(span[..lf].ToArray());
                _start += lf + 1;
                if (line.Count > 0 && line[^1] == '\r') line.RemoveAt(line.Count - 1);
                return Encoding.UTF8.GetString(line.ToArray());
            }
            if (line.Count > MaxLineBytes) throw new ImapException("A response line was longer than this client accepts.");
        }
    }

    private async Task<byte[]> ReadExactAsync(int size, CancellationToken ct)
    {
        var result = new byte[size];
        var filled = 0;
        while (filled < size)
        {
            if (_start == _end) await FillAsync(ct);
            var take = Math.Min(size - filled, _end - _start);
            Buffer.BlockCopy(_buffer, _start, result, filled, take);
            _start += take;
            filled += take;
        }
        return result;
    }

    private async Task FillAsync(CancellationToken ct)
    {
        _start = 0;
        _end = await _stream.ReadAsync(_buffer, ct);
        if (_end == 0) throw new ImapException("The server closed the connection.");
    }

    /// <summary>One <c>* n FETCH (…)</c> response with a header literal; anything else (an unsolicited FLAGS update) gives null.</summary>
    public static MailHeader? ParseFetch(Response response)
    {
        if (!FetchLine().IsMatch(response.Text)) return null;
        var bodyAt = response.Text.IndexOf("BODY[HEADER.FIELDS", StringComparison.OrdinalIgnoreCase);
        if (bodyAt < 0) return null;
        // The header block is the literal that follows BODY[…]: count the literals that come before it.
        var index = LiteralMarker().Matches(response.Text[..bodyAt]).Count;
        if (index >= response.Literals.Count) return null;
        var fields = MailHeaderText.Parse(Encoding.UTF8.GetString(response.Literals[index]));

        DateTime? arrived = InternalDate().Match(response.Text) is { Success: true } m ? MailHeaderText.ParseImapDate(m.Groups[1].Value) : null;
        var received = arrived ?? MailHeaderText.ParseDate(fields.GetValueOrDefault("date"));
        var from = MailHeaderText.Address(fields.GetValueOrDefault("from"));
        if (received is null || from is null) return null;
        var subject = MailHeaderText.DecodeWords(fields.GetValueOrDefault("subject"));
        return new MailHeader(from, subject, received.Value, fields.GetValueOrDefault("message-id"), fields.GetValueOrDefault("in-reply-to"),
            fields.GetValueOrDefault("references"), MailHeaderText.IsAutoReply(fields.GetValueOrDefault("auto-submitted"), subject));
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }

    [GeneratedRegex(@"\{(\d{1,9})\+?\}$")]
    private static partial Regex LiteralAtEnd();

    [GeneratedRegex(@"\{\d{1,9}\+?\}")]
    private static partial Regex LiteralMarker();

    [GeneratedRegex(@"^\* \d+ FETCH \(", RegexOptions.IgnoreCase)]
    private static partial Regex FetchLine();

    [GeneratedRegex("INTERNALDATE \"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InternalDate();
}

/// <summary>Reading the few header fields reply detection needs: unfolding, RFC 2047 encoded words, the From address and dates.</summary>
public static partial class MailHeaderText
{
    /// <summary>Field name (lower case) → unfolded value; the first occurrence wins.</summary>
    public static Dictionary<string, string> Parse(string block)
    {
        var unfolded = Regex.Replace(block, @"\r?\n[ \t]+", " ");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in unfolded.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim().ToLowerInvariant();
            if (name.Length == 0 || name.Any(char.IsWhiteSpace)) continue;
            fields.TryAdd(name, line[(colon + 1)..].Trim());
        }
        return fields;
    }

    /// <summary>The address in a From value (<c>"Name" &lt;a@b&gt;</c> or a bare address), lower-cased; null when there is none.</summary>
    public static string? Address(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;
        var angle = AngleAddress().Match(from);
        var candidate = angle.Success ? angle.Groups[1].Value : BareAddress().Match(from).Value;
        return System.Net.Mail.MailAddress.TryCreate(candidate.Trim(), out var a) ? a.Address.ToLowerInvariant() : null;
    }

    /// <summary>Decodes <c>=?charset?B|Q?…?=</c> words; whitespace between two encoded words is dropped, as RFC 2047 says. Control characters are removed.</summary>
    public static string? DecodeWords(string? value)
    {
        if (value is null) return null;
        var joined = BetweenWords().Replace(value, "?==?");
        var decoded = EncodedWord().Replace(joined, m =>
        {
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(m.Groups[1].Value); } catch (ArgumentException) { encoding = Encoding.UTF8; }
            try
            {
                var text = m.Groups[3].Value;
                var bytes = m.Groups[2].Value is "B" or "b" ? Convert.FromBase64String(text) : QDecode(text);
                return encoding.GetString(bytes);
            }
            catch (FormatException) { return m.Value; }
        });
        return new string(decoded.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }

    private static byte[] QDecode(string text)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_') bytes.Add((byte)' ');
            else if (text[i] == '=' && i + 2 < text.Length && byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) { bytes.Add(b); i += 2; }
            else bytes.Add((byte)text[i]);
        }
        return bytes.ToArray();
    }

    /// <summary>INTERNALDATE: <c>13-Oct-2026 09:15:02 +0530</c> (the day may be space-padded), as UTC.</summary>
    public static DateTime? ParseImapDate(string value)
    {
        var m = ImapDate().Match(value.Trim());
        if (!m.Success) return null;
        return Build(m.Groups["d"].Value, m.Groups["mon"].Value, m.Groups["y"].Value, m.Groups["h"].Value, m.Groups["mi"].Value, m.Groups["s"].Value, m.Groups["z"].Value);
    }

    /// <summary>An RFC 5322 Date (<c>Mon, 13 Oct 2026 09:15:02 +0530 (IST)</c>), as UTC; null when it cannot be read.</summary>
    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = HeaderDate().Match(value);
        if (!m.Success) return null;
        var year = m.Groups["y"].Value;
        if (year.Length == 2) year = (int.Parse(year, CultureInfo.InvariantCulture) < 50 ? "20" : "19") + year;
        return Build(m.Groups["d"].Value, m.Groups["mon"].Value, year, m.Groups["h"].Value, m.Groups["mi"].Value,
            m.Groups["s"].Success ? m.Groups["s"].Value : "00", m.Groups["z"].Success ? m.Groups["z"].Value : "+0000");
    }

    private static DateTime? Build(string d, string mon, string y, string h, string mi, string s, string zone)
    {
        var month = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames, n => n.Length > 0 && string.Equals(n, mon, StringComparison.OrdinalIgnoreCase)) + 1;
        if (month == 0) return null;
        var offset = TimeSpan.Zero;
        if (zone.Length == 5 && zone[0] is '+' or '-')
        {
            offset = new TimeSpan(int.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture), int.Parse(zone.AsSpan(3, 2), CultureInfo.InvariantCulture), 0);
            if (zone[0] == '-') offset = -offset;
        }
        try
        {
            return new DateTimeOffset(int.Parse(y, CultureInfo.InvariantCulture), month, int.Parse(d, CultureInfo.InvariantCulture),
                int.Parse(h, CultureInfo.InvariantCulture), int.Parse(mi, CultureInfo.InvariantCulture), int.Parse(s, CultureInfo.InvariantCulture), offset).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Out-of-office and other automatic answers do not count as a reply.</summary>
    public static bool IsAutoReply(string? autoSubmitted, string? subject) =>
        !string.IsNullOrWhiteSpace(autoSubmitted) && !autoSubmitted.Trim().StartsWith("no", StringComparison.OrdinalIgnoreCase)
        || subject is not null && AutoSubject().IsMatch(subject);

    [GeneratedRegex(@"<([^<>\s]+@[^<>\s]+)>")]
    private static partial Regex AngleAddress();

    [GeneratedRegex(@"[^\s<>""(),;:]+@[^\s<>""(),;:]+")]
    private static partial Regex BareAddress();

    [GeneratedRegex(@"\?=\s+=\?")]
    private static partial Regex BetweenWords();

    [GeneratedRegex(@"=\?([^?\s]+)\?([BbQq])\?([^?\s]*)\?=")]
    private static partial Regex EncodedWord();

    [GeneratedRegex(@"^(?<d>\d{1,2})-(?<mon>[A-Za-z]{3})-(?<y>\d{4}) (?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2}) (?<z>[+-]\d{4})$")]
    private static partial Regex ImapDate();

    [GeneratedRegex(@"(?<d>\d{1,2})\s+(?<mon>[A-Za-z]{3})\s+(?<y>\d{4}|\d{2})\s+(?<h>\d{1,2}):(?<mi>\d{2})(?::(?<s>\d{2}))?(?:\s+(?<z>[+-]\d{4}))?")]
    private static partial Regex HeaderDate();

    [GeneratedRegex(@"^\s*(automatic reply|auto:|autoreply|auto-reply|out of (the )?office|abwesenheit|réponse automatique)", RegexOptions.IgnoreCase)]
    private static partial Regex AutoSubject();
}
