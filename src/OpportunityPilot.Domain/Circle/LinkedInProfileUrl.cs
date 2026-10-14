namespace OpportunityPilot.Domain.Circle;

/// <summary>
/// The one accepted form of a LinkedIn member link: <c>https://www.linkedin.com/in/{slug}</c>. Any linkedin.com host (www, country
/// subdomains such as in.linkedin.com) and http or https are accepted and rewritten; the query, fragment and anything after the
/// slug are dropped. The slug keeps its case, because LinkedIn's opaque member ids (ACoAA…) are case-sensitive.
/// </summary>
public static class LinkedInProfileUrl
{
    public const int MaxLength = 400;
    public const string Prefix = "https://www.linkedin.com/in/";
    private const int MaxSlugLength = 200;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var raw = value.Trim();
        if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host != "linkedin.com" && !host.EndsWith(".linkedin.com", StringComparison.Ordinal)) return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !segments[0].Equals("in", StringComparison.OrdinalIgnoreCase)) return false;
        var slug = segments[1];
        if (slug.Length is 0 or > MaxSlugLength) return false;
        // Slugs are letters (any script), digits, '-', '_' and '.'; the path keeps non-ASCII letters percent-encoded.
        foreach (var c in Uri.UnescapeDataString(slug))
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) return false;

        normalized = Prefix + slug;
        return normalized.Length <= MaxLength;
    }

    public static string? NormalizeOrNull(string? value) => TryNormalize(value, out var n) ? n : null;

    /// <summary>True when the text already is a normalised profile link (what the outbox and the circle store).</summary>
    public static bool IsNormalized(string? value) => value is not null && TryNormalize(value, out var n) && n == value;
}
