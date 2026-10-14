using System.Text.RegularExpressions;
using OpportunityPilot.Application.Campaigns;

namespace OpportunityPilot.Application.Research.Rules;

/// <summary>
/// Deterministic scoring for prospective customers (companies). Keyword rules only: they show what the source says,
/// they do not understand a business. Missing evidence stays unknown — no published signal is not a "no".
/// </summary>
public static partial class CustomerRules
{
    public static RuleResult Evaluate(CampaignCriteria criteria, IReadOnlyDictionary<string, int> weights, RuleInput input)
    {
        var ev = input.EvidenceId;
        var corpus = $"{input.Title}\n{input.Industry}\n{input.Text}";
        var hasText = !string.IsNullOrWhiteSpace(input.Text) || !string.IsNullOrWhiteSpace(input.Industry);
        var place = string.Join(", ", new[] { input.Location, input.Country }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var knownPlace = place.Length > 0 ? place : null;

        var scores = new List<CriterionScore>();
        var facts = new List<FactRow>();
        var gaps = new List<string>();
        var checks = new List<FilterCheck>();

        SharedRules.ExcludeKeywords(criteria, checks, input.Title, input.Text);
        SharedRules.ExcludeOrganizations(criteria, checks, input.Organization);

        var geoMatch = criteria.Locations.FirstOrDefault(l => TextMatch.Contains(knownPlace, l) || PlaceMatch.InField(knownPlace, l) || TextMatch.Contains(corpus, l));
        if (criteria.Locations.Count > 0)
        {
            if (geoMatch is not null) checks.Add(new FilterCheck("locations", FilterResult.Pass, $"Location matches {geoMatch}."));
            else if (knownPlace is not null && !PlaceMatch.IsUnspecific(input.Location)) checks.Add(new FilterCheck("locations", FilterResult.Fail, $"Location \"{knownPlace}\" is not in your list."));
            else checks.Add(new FilterCheck("locations", FilterResult.Unknown, "Location is not stated."));
        }

        if (criteria.Industries.Count > 0)
        {
            var found = TextMatch.Found(criteria.Industries, corpus);
            if (found.Count > 0)
            {
                scores.Add(new(CampaignWeights.Industry, "Industry fit", 1, $"Mentions {string.Join(", ", found)}.", TermsExcerpt(input, corpus, found)));
                facts.Add(new FactRow("industriesMatched", "Industry terms mentioned", string.Join(", ", found), ev, false));
            }
            else if (hasText) scores.Add(new(CampaignWeights.Industry, "Industry fit", 0, "None of your industries is mentioned."));
            else
            {
                scores.Add(new(CampaignWeights.Industry, "Industry fit", null, "The source gives no description or industry."));
                gaps.Add("No description or industry");
            }
        }

        if (criteria.Problems.Count > 0)
        {
            var found = TextMatch.Found(criteria.Problems, corpus);
            if (!hasText)
            {
                scores.Add(new(CampaignWeights.Problem, "Business problem", null, "The source gives no description to check."));
                gaps.Add("No description or industry");
            }
            else
            {
                var single = criteria.Problems.Count == 1;
                double value = found.Count >= 2 || (single && found.Count == 1) ? 1 : found.Count == 1 ? 0.5 : 0;
                var why = found.Count == 0
                    ? "None of your problem keywords is mentioned."
                    : $"Mentions {found.Count} of {criteria.Problems.Count}: {string.Join(", ", found)}.";
                scores.Add(new(CampaignWeights.Problem, "Business problem", value, why, TermsExcerpt(input, corpus, found)));
                if (found.Count > 0) facts.Add(new FactRow("problemsMatched", "Problem keywords mentioned", string.Join(", ", found), ev, false));
            }
        }

        if (criteria.Locations.Count > 0)
        {
            if (geoMatch is not null) scores.Add(new(CampaignWeights.Geography, "Geography", 1, $"Location matches {geoMatch}.", PlaceExcerpt(input, corpus, geoMatch)));
            else if (knownPlace is not null && !PlaceMatch.IsUnspecific(input.Location)) scores.Add(new(CampaignWeights.Geography, "Geography", 0, $"Location \"{knownPlace}\" is not in your list.", Excerpts.Field("location", knownPlace)));
            else
            {
                scores.Add(new(CampaignWeights.Geography, "Geography", null, "The source does not state a location."));
                gaps.Add("Location not stated");
            }
        }

        if (criteria.Signals.Count > 0)
        {
            var found = TextMatch.Found(criteria.Signals, corpus);
            if (found.Count > 0)
            {
                scores.Add(new(CampaignWeights.Signal, "Published signal", 1, $"Mentions {string.Join(", ", found)}.", Excerpts.ForTerms(corpus, found)));
                facts.Add(new FactRow("signals", "Signals mentioned", string.Join(", ", found), ev, false));
            }
            else
            {
                scores.Add(new(CampaignWeights.Signal, "Published signal", null, "No published signal found. That is unknown, not a sign of no interest."));
                gaps.Add("No published signal found");
            }
        }

        // Always applicable: there is nothing to configure, and a reachable path matters in every customer campaign.
        var contact = ContactPath(input);
        if (contact is not null)
        {
            scores.Add(new(CampaignWeights.ContactPath, "Contact path", 1, $"Reachable via {contact}."));
            facts.Add(new FactRow("contactPath", "Contact path", contact, ev, false));
        }
        else
        {
            scores.Add(new(CampaignWeights.ContactPath, "Contact path", null, "No website, contact page or email found."));
            gaps.Add("No contact path found");
        }

        facts.Insert(0, new FactRow("organization", "Company", input.Organization.Length > 0 ? input.Organization : input.Title, ev, false));
        if (knownPlace is not null) facts.Add(new FactRow("location", "Location", knownPlace, ev, false));
        if (!string.IsNullOrWhiteSpace(input.Industry)) facts.Add(new FactRow("industry", "Industry", input.Industry!, ev, false));

        var (score, coverage, rows) = Scoring.Combine(weights, scores, ev);
        var (outcome, reason) = Scoring.Outcome(checks);
        return new RuleResult(outcome, reason, score, coverage, rows, facts, gaps.Distinct().ToList(), checks);
    }

    /// <summary>A website, a contact/careers link, an email address, or a "contact us" mention. Null when none is present.</summary>
    public static string? ContactPath(RuleInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.Website)) return input.Website!.Trim();
        var link = input.Links?.FirstOrDefault(l => ContactLink().IsMatch(l));
        if (link is not null) return link;
        var email = Email().Match(input.Text ?? string.Empty);
        if (email.Success) return email.Value;
        var url = ContactUrl().Match(input.Text ?? string.Empty);
        if (url.Success) return url.Value;
        return TextMatch.Contains(input.Text, "contact us") ? "a \"contact us\" mention" : null;
    }

    private static string? TermsExcerpt(RuleInput input, string corpus, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return null;
        var fieldMatches = new[]
        {
            terms.Any(t => TextMatch.Contains(input.Industry, t)) && input.Industry is not null
                ? Excerpts.Field("industry", input.Industry)
                : null
        };
        return Excerpts.Join(fieldMatches.Concat([Excerpts.ForTerms(corpus, terms)]));
    }

    private static string? PlaceExcerpt(RuleInput input, string corpus, string place)
    {
        if (TextMatch.Contains(input.Location, place) && input.Location is not null)
            return Excerpts.Field("location", input.Location);
        if (TextMatch.Contains(input.Country, place) && input.Country is not null)
            return Excerpts.Field("country", input.Country);
        return Excerpts.ForTerm(corpus, place);
    }

    [GeneratedRegex(@"mailto:|/(?:contact|careers|jobs|partners?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContactLink();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"https?://[^\s""'<>]+/(?:contact|careers)[^\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContactUrl();
}
