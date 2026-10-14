using OpportunityPilot.Application.Research.Rules;

namespace OpportunityPilot.Application.Research.Boards;

/// <summary>
/// Adzuna runs one index per country (<c>/v1/api/jobs/{country}/search</c>). This picks the country from the place the person searched
/// ("Pune" → in, "Berlin, Germany" → de, "UK" → gb) and falls back to India. When the place is only the country itself, no <c>where</c>
/// is sent: the country index already is that place.
/// </summary>
public static class AdzunaCountry
{
    public const string Default = "in";

    /// <summary>Earlier rows win, so a place that names two countries' words ("New South Wales", "New Mexico") lands on the right one.</summary>
    private static readonly (string Code, string[] Countries, string[] Places)[] Table =
    [
        ("in", ["India", "Bharat"], []),
        ("au", ["Australia"], ["New South Wales", "Victoria", "Queensland", "Sydney", "Melbourne", "Brisbane", "Perth", "Adelaide", "Canberra"]),
        ("nz", ["New Zealand", "Aotearoa"], ["Auckland", "Wellington", "Christchurch"]),
        ("ca", ["Canada"], ["Ontario", "Quebec", "British Columbia", "Alberta", "Toronto", "Vancouver", "Montreal", "Ottawa", "Calgary"]),
        ("us", ["United States", "United States of America", "USA", "U.S.", "America"],
            ["New Mexico", "New York", "San Francisco", "Seattle", "Boston", "Chicago", "Austin", "Los Angeles", "Texas", "California"]),
        ("gb", ["United Kingdom", "UK", "Great Britain", "Britain", "England", "Scotland", "Wales", "Northern Ireland"],
            ["London", "Manchester", "Birmingham", "Edinburgh", "Glasgow", "Leeds", "Bristol", "Cambridge", "Oxford"]),
        ("za", ["South Africa"], ["Johannesburg", "Cape Town", "Durban", "Pretoria"]),
        ("sg", ["Singapore"], []),
        ("de", ["Germany", "Deutschland"], ["Berlin", "Munich", "München", "Hamburg", "Frankfurt", "Cologne", "Köln", "Stuttgart", "Düsseldorf"]),
        ("fr", ["France"], ["Paris", "Lyon", "Marseille", "Toulouse", "Lille"]),
        ("nl", ["Netherlands", "The Netherlands", "Holland"], ["Amsterdam", "Rotterdam", "The Hague", "Utrecht", "Eindhoven"]),
        ("pl", ["Poland", "Polska"], ["Warsaw", "Kraków", "Krakow", "Wrocław", "Wroclaw", "Gdańsk", "Gdansk", "Poznań", "Poznan"]),
        ("it", ["Italy", "Italia"], ["Rome", "Milan", "Milano", "Turin", "Torino", "Naples", "Bologna"]),
        ("es", ["Spain", "España"], ["Madrid", "Barcelona", "Valencia", "Seville", "Sevilla", "Málaga", "Malaga"]),
        ("br", ["Brazil", "Brasil"], ["São Paulo", "Sao Paulo", "Rio de Janeiro", "Belo Horizonte"]),
        ("at", ["Austria", "Österreich"], ["Vienna", "Wien", "Graz", "Salzburg", "Linz"]),
        ("be", ["Belgium", "België", "Belgique"], ["Brussels", "Antwerp", "Ghent", "Liège"]),
        ("ch", ["Switzerland", "Schweiz", "Suisse"], ["Zurich", "Zürich", "Geneva", "Genève", "Basel", "Bern", "Lausanne"]),
        ("mx", ["Mexico", "México"], ["Mexico City", "Guadalajara", "Monterrey"])
    ];

    /// <summary>Every country code Adzuna serves, in the order above.</summary>
    public static IReadOnlyList<string> Codes { get; } = Table.Select(t => t.Code).ToArray();

    /// <summary>
    /// The Adzuna country for a place, and the <c>where</c> to send with it (null when the place is empty, says "Remote"/"Anywhere", or
    /// only names the country).
    /// </summary>
    public static (string Country, string? Where) Resolve(string? place)
    {
        var p = place?.Trim();
        if (string.IsNullOrEmpty(p) || PlaceMatch.IsUnspecific(p)) return (Default, null);
        if (FromCode(p) is { } exact) return (exact, null);
        foreach (var (code, countries, _) in Table)
            if (countries.Any(c => c.Equals(p, StringComparison.OrdinalIgnoreCase))) return (code, null);
        foreach (var (code, countries, places) in Table)
            if (countries.Concat(places).Any(n => TextMatch.Contains(p, n))) return (code, p);
        // Indian cities and states ("Gurugram", "Uttar Pradesh") are known to the place rules.
        if (PlaceMatch.InField(p, "India")) return ("in", p);
        // "Berlin, DE": a trailing two-letter code in capitals names the country.
        var last = p.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (last is { Length: 2 } && last.All(char.IsAsciiLetterUpper) && FromCode(last) is { } tail) return (tail, p);
        return (Default, p);
    }

    private static string? FromCode(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v == "uk") return "gb";
        return v.Length == 2 && Codes.Contains(v) ? v : null;
    }
}
