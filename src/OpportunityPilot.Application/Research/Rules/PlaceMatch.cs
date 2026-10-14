using System.Text.RegularExpressions;

namespace OpportunityPilot.Application.Research.Rules;

/// <summary>
/// Place matching that knows a country contains its cities and that some cities have two names. A campaign that says "India" matches a posting
/// in "Ghaziabad, Uttar Pradesh, IN"; one that says "Gurgaon" matches "Gurugram". Only the posting's location field is expanded this way: free
/// text is still matched on the exact words, so a stray mention of a city in a description never counts as a place. Versioned with the rules.
/// </summary>
public static partial class PlaceMatch
{
    private static readonly string[] IndianCities =
    [
        "Bengaluru", "Bangalore", "Hyderabad", "Pune", "Mumbai", "Bombay", "Navi Mumbai", "Thane", "Delhi", "New Delhi", "Gurugram", "Gurgaon", "Noida",
        "Greater Noida", "Ghaziabad", "Faridabad", "Chennai", "Madras", "Kolkata", "Calcutta", "Ahmedabad", "Jaipur", "Kochi", "Cochin", "Thiruvananthapuram",
        "Trivandrum", "Coimbatore", "Indore", "Chandigarh", "Mohali", "Lucknow", "Nagpur", "Bhubaneswar", "Vadodara", "Surat", "Visakhapatnam", "Mysuru",
        "Mysore", "Mangaluru", "Nashik", "Patna", "Bhopal", "Kanpur", "Ludhiana", "Dehradun", "Vijayawada", "Madurai", "Raipur", "Ranchi", "Guwahati"
    ];

    private static readonly string[] IndianStates =
    [
        "Andhra Pradesh", "Arunachal Pradesh", "Assam", "Bihar", "Chhattisgarh", "Goa", "Gujarat", "Haryana", "Himachal Pradesh", "Jharkhand", "Karnataka",
        "Kerala", "Madhya Pradesh", "Maharashtra", "Manipur", "Meghalaya", "Mizoram", "Nagaland", "Odisha", "Punjab", "Rajasthan", "Sikkim", "Tamil Nadu",
        "Telangana", "Tripura", "Uttar Pradesh", "Uttarakhand", "West Bengal", "Jammu and Kashmir", "Ladakh", "Puducherry"
    ];

    /// <summary>One configured place → every way a posting's location field may name it (names, two-letter code, places inside it).</summary>
    private static readonly Dictionary<string, (string[] Names, string[] Codes)> Groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["India"] = ([.. new[] { "India", "Bharat" }, .. IndianCities, .. IndianStates], ["IN"]),
        ["Delhi NCR"] = (["Delhi", "New Delhi", "Gurugram", "Gurgaon", "Noida", "Greater Noida", "Ghaziabad", "Faridabad", "NCR"], []),
        ["NCR"] = (["Delhi", "New Delhi", "Gurugram", "Gurgaon", "Noida", "Greater Noida", "Ghaziabad", "Faridabad", "NCR"], []),
        ["Bengaluru"] = (["Bengaluru", "Bangalore"], []),
        ["Bangalore"] = (["Bengaluru", "Bangalore"], []),
        ["Gurugram"] = (["Gurugram", "Gurgaon"], []),
        ["Gurgaon"] = (["Gurugram", "Gurgaon"], []),
        ["Mumbai"] = (["Mumbai", "Bombay", "Navi Mumbai"], []),
        ["Chennai"] = (["Chennai", "Madras"], []),
        ["Kolkata"] = (["Kolkata", "Calcutta"], []),
        ["Kochi"] = (["Kochi", "Cochin"], []),
        ["United States"] = (["United States", "USA", "U.S.", "America"], ["US"]),
        ["USA"] = (["United States", "USA", "U.S.", "America"], ["US"]),
        ["United Kingdom"] = (["United Kingdom", "England", "Scotland", "Wales", "Northern Ireland", "London", "Manchester", "Birmingham", "Edinburgh"], ["UK", "GB"]),
        ["UK"] = (["United Kingdom", "England", "Scotland", "Wales", "Northern Ireland", "London", "Manchester", "Birmingham", "Edinburgh"], ["UK", "GB"]),
        ["Germany"] = (["Germany", "Deutschland", "Berlin", "Munich", "München", "Hamburg", "Frankfurt", "Cologne", "Köln", "Stuttgart"], ["DE"]),
        ["Canada"] = (["Canada", "Toronto", "Vancouver", "Montreal", "Ottawa", "Calgary"], ["CA"]),
        ["Australia"] = (["Australia", "Sydney", "Melbourne", "Brisbane", "Perth", "Adelaide"], ["AU"]),
        ["Singapore"] = (["Singapore"], ["SG"]),
        ["UAE"] = (["United Arab Emirates", "UAE", "Dubai", "Abu Dhabi", "Sharjah"], ["AE"]),
        ["United Arab Emirates"] = (["United Arab Emirates", "UAE", "Dubai", "Abu Dhabi", "Sharjah"], ["AE"])
    };

    private static readonly HashSet<string> Unspecific = new(StringComparer.OrdinalIgnoreCase)
        { "anywhere", "worldwide", "global", "remote", "everywhere", "not specified", "n/a", "any", "any location" };

    /// <summary>"Anywhere", "Worldwide", "Remote": a posting that names no real place. A place filter cannot fail on it, so it is "not stated", not "not in your list".</summary>
    public static bool IsUnspecific(string? location) => !string.IsNullOrWhiteSpace(location) && Unspecific.Contains(location.Trim());

    /// <summary>True when the posting's location field names the configured place directly, by another name, or by a place inside it.</summary>
    public static bool InField(string? location, string configured)
    {
        if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(configured)) return false;
        if (!Groups.TryGetValue(configured.Trim(), out var group)) return false;
        if (group.Names.Any(n => TextMatch.Contains(location, n))) return true;
        return group.Codes.Any(c => CodeToken(c).IsMatch(location));
    }

    // A two-letter code only counts as its own capital-letter token ("…, IN"), never the English word "in".
    private static Regex CodeToken(string code) => new($@"(?<![A-Za-z]){Regex.Escape(code)}(?![A-Za-z])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
