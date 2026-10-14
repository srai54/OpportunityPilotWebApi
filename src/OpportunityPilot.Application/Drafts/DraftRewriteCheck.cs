using System.Text.RegularExpressions;

namespace OpportunityPilot.Application.Drafts;

/// <summary>
/// Server-side guard for AI rewrites: compares a suggestion with the text it was made from. A new email address or link
/// sets <see cref="Result.AddedContact"/> (the suggestion is dropped); a new number or capitalised name becomes a warning.
/// </summary>
public static partial class DraftRewriteCheck
{
    public const int MaxWarnings = 10;

    public sealed record Result(bool AddedContact, IReadOnlyList<string> Warnings);

    /// <summary>Capitalised words that start sentences or greetings in ordinary emails; not treated as new names.</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "i", "i'm", "i'd", "i've", "i'll", "im", "id", "ive", "ill", "we", "our", "you", "your", "my", "me", "us", "it", "its",
        "this", "that", "these", "those", "there", "here", "if", "as", "at", "by", "for", "from", "in", "on", "of", "to", "with", "and",
        "but", "or", "so", "also", "just", "please", "thanks", "thank", "thankyou", "hi", "hello", "hey", "dear", "greetings", "best",
        "regards", "kind", "warm", "warmly", "sincerely", "cheers", "yours", "respectfully", "cordially", "looking", "happy", "glad",
        "would", "could", "can", "may", "might", "will", "shall", "should", "let", "let's", "lets", "do", "does", "is", "are", "was",
        "be", "have", "has", "had", "any", "all", "some", "no", "yes", "not", "when", "what", "how", "why", "who", "which", "while",
        "after", "before", "since", "given", "about", "re", "subject", "team", "hiring", "manager", "role", "quick", "short", "brief",
        "great", "excited", "interested", "noticed", "saw", "seen", "reaching", "following", "regarding", "together", "both"
    };

    public static Result Compare(string input, string suggestion)
    {
        var addedContact = false;
        var warnings = new List<string>();

        var inputEmails = EmailPattern().Matches(input).Select(m => m.Value.ToLowerInvariant()).ToHashSet();
        foreach (var email in EmailPattern().Matches(suggestion).Select(m => m.Value.ToLowerInvariant()).Distinct())
            if (!inputEmails.Contains(email)) { addedContact = true; warnings.Add($"New email address not in your draft or profile: {email}"); }

        var inputUrls = UrlPattern().Matches(input).Select(m => NormaliseUrl(m.Value)).ToHashSet();
        var inputLower = input.ToLowerInvariant();
        foreach (var url in UrlPattern().Matches(suggestion).Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')')).Distinct())
        {
            var normalised = NormaliseUrl(url);
            if (!inputUrls.Contains(normalised) && !inputLower.Contains(normalised)) { addedContact = true; warnings.Add($"New link not in your draft or profile: {url}"); }
        }

        var withoutContacts = UrlPattern().Replace(EmailPattern().Replace(suggestion, " "), " ");
        var inputNumbers = NumberPattern().Matches(input).Select(m => NormaliseNumber(m.Value)).ToHashSet();
        foreach (var number in NumberPattern().Matches(withoutContacts).Select(m => m.Value).Distinct())
            if (!inputNumbers.Contains(NormaliseNumber(number))) warnings.Add($"New number not in your draft or profile: {number}");

        static string Key(string word) => word.Replace('’', '\'').ToLowerInvariant();
        var inputWords = WordPattern().Matches(input).Select(m => Key(m.Value)).ToHashSet();
        foreach (var word in CapitalisedPattern().Matches(withoutContacts).Select(m => m.Value).Distinct())
            if (word.Length > 1 && !inputWords.Contains(Key(word)) && !CommonWords.Contains(Key(word)))
                warnings.Add($"New name not in your draft or profile: {word}");

        return new Result(addedContact, warnings.Take(MaxWarnings).ToList());
    }

    private static string NormaliseUrl(string url)
    {
        var value = url.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')').ToLowerInvariant();
        foreach (var prefix in new[] { "https://", "http://" })
            if (value.StartsWith(prefix, StringComparison.Ordinal)) value = value[prefix.Length..];
        if (value.StartsWith("www.", StringComparison.Ordinal)) value = value[4..];
        return value.TrimEnd('/');
    }

    private static string NormaliseNumber(string number) => number.Replace(",", string.Empty).TrimEnd('.');

    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""'\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)?")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\b\p{Lu}[\p{L}\p{N}]*(?:['’]\p{L}+)?")]
    private static partial Regex CapitalisedPattern();
}
