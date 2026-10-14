using System.Globalization;
using System.Text.RegularExpressions;
using OpportunityPilot.Application.Campaigns;

namespace OpportunityPilot.Application.Research.Rules;

/// <param name="Max">Null for open-ended ranges such as "5+ years" or "minimum 4 years".</param>
public sealed record ExperienceRange(double Min, double? Max)
{
    public override string ToString() => Max is { } max
        ? $"{Min.ToString(CultureInfo.InvariantCulture)}–{max.ToString(CultureInfo.InvariantCulture)} years"
        : $"{Min.ToString(CultureInfo.InvariantCulture)}+ years";
}

/// <summary>A detected value plus the sentence or field note it was read from.</summary>
public sealed record Detected(string Value, string Excerpt);

/// <summary>Deterministic extraction and scoring for job postings. Pure: same input, same result.</summary>
public static partial class JobRules
{
    public const string Remote = "Remote";
    public const string Hybrid = "Hybrid";
    public const string Onsite = "Onsite";

    /// <param name="asOf">
    /// The evaluation time (UTC) for the posting-age filter. Null means "no clock", and the posting is treated like one
    /// without a date (kept).
    /// </param>
    public static RuleResult Evaluate(CampaignCriteria criteria, IReadOnlyDictionary<string, int> weights, RuleInput input, DateTime? asOf = null)
    {
        var ev = input.EvidenceId;
        var hasText = !string.IsNullOrWhiteSpace(input.Text);
        var corpus = $"{input.Title}\n{input.Text}";
        var workMode = DetectWorkModeWithEvidence(corpus, input.Location, input.WorkplaceType);
        var experience = hasText ? FindExperience(corpus) : null;
        var salary = hasText ? FindSalary(input.Text) : null;

        var scores = new List<CriterionScore>();
        var facts = new List<FactRow>();
        var gaps = new List<string>();
        var checks = new List<FilterCheck>();

        // Hard filters.
        SharedRules.ExcludeKeywords(criteria, checks, input.Title, input.Text);
        SharedRules.ExcludeOrganizations(criteria, checks, input.Organization);

        List<string> requiredFound = [];
        if (criteria.RequiredSkills.Count > 0)
        {
            if (!hasText)
            {
                checks.Add(new FilterCheck("requiredSkills", FilterResult.Unknown, "Posting has no description, so required skills cannot be checked."));
                gaps.Add("Posting description is empty");
            }
            else
            {
                requiredFound = TextMatch.Found(criteria.RequiredSkills, corpus);
                checks.Add(requiredFound.Count == 0
                    ? new FilterCheck("requiredSkills", FilterResult.Fail, $"None of the required skills ({string.Join(", ", criteria.RequiredSkills)}) appear in the posting.")
                    : new FilterCheck("requiredSkills", FilterResult.Pass, "At least one required skill appears."));
            }
        }

        if (criteria.WorkModes.Count > 0)
        {
            if (workMode is null)
            {
                checks.Add(new FilterCheck("workModes", FilterResult.Unknown, "Work mode is not stated."));
                gaps.Add("Work mode not stated");
            }
            else checks.Add(criteria.WorkModes.Contains(workMode.Value)
                ? new FilterCheck("workModes", FilterResult.Pass, $"{workMode.Value} is an accepted work mode.")
                : new FilterCheck("workModes", FilterResult.Fail, $"Work mode {workMode.Value} is not one you accept."));
        }

        var locationMatch = MatchLocation(criteria, input.Location, corpus, workMode);
        if (criteria.Locations.Count > 0)
        {
            if (locationMatch is not null)
                checks.Add(new FilterCheck("locations", FilterResult.Pass, $"Location matches {locationMatch.Value}."));
            else if (input.Location is not null && !PlaceMatch.IsUnspecific(input.Location))
                checks.Add(new FilterCheck("locations", FilterResult.Fail, $"Location \"{input.Location}\" is not in your list."));
            else
            {
                checks.Add(new FilterCheck("locations", FilterResult.Unknown, "Location is not stated."));
                gaps.Add("Location not stated");
            }
        }

        // Absence of evidence is not evidence: a posting that does not look like an agency passes, never "unknown".
        Detected? agency = null;
        if (criteria.ExcludeStaffingAgencies)
        {
            agency = DetectStaffingAgency(input.Organization, input.Text);
            checks.Add(agency is null
                ? new FilterCheck("excludeStaffingAgencies", FilterResult.Pass, "No sign of a staffing agency.")
                : new FilterCheck("excludeStaffingAgencies", FilterResult.Fail, $"Looks like a staffing agency: {agency.Excerpt}"));
        }

        // A missing date is the source's gap, not the job's: the posting is kept.
        if (criteria.MaxPostingAgeDays is { } maxAge)
        {
            if (input.PostedAt is { } posted && asOf is { } now)
                checks.Add(posted < now.AddDays(-maxAge)
                    ? new FilterCheck("maxPostingAgeDays", FilterResult.Fail, $"Posted {posted:yyyy-MM-dd}, older than {maxAge} days.")
                    : new FilterCheck("maxPostingAgeDays", FilterResult.Pass, $"Posted {posted:yyyy-MM-dd}, within {maxAge} days."));
            else
                checks.Add(new FilterCheck("maxPostingAgeDays", FilterResult.Pass, "The source gives no posting date, so the posting is kept."));
        }

        // Scored criteria; each is applicable only when the user configured something for it.
        if (criteria.RequiredSkills.Count > 0)
            scores.Add(SkillScore(CampaignWeights.MandatorySkills, "Mandatory skills", criteria.RequiredSkills, hasText ? requiredFound : null, corpus, gaps, "Required skills"));
        if (criteria.PreferredSkills.Count > 0)
            scores.Add(SkillScore(CampaignWeights.PreferredSkills, "Preferred skills", criteria.PreferredSkills,
                hasText ? TextMatch.Found(criteria.PreferredSkills, corpus) : null, corpus, gaps, "Preferred skills"));
        if (criteria.CandidateYears is { } years)
            scores.Add(ExperienceScore(years, experience, gaps));
        if (criteria.Locations.Count > 0 || criteria.WorkModes.Count > 0)
            scores.Add(LocationScore(criteria, input.Location, workMode, locationMatch));

        var (score, coverage, rows) = Scoring.Combine(weights, scores, ev);
        var (outcome, reason) = Scoring.Outcome(checks);

        if (input.Organization.Length > 0) facts.Add(new FactRow("organization", "Company", input.Organization, ev, false));
        else gaps.Add("Company not stated");
        if (input.Location is not null) facts.Add(new FactRow("location", "Location", input.Location, ev, false));
        if (workMode is not null) facts.Add(new FactRow("workMode", "Work mode", workMode.Value, ev, false));
        if (experience is { } foundExperience) facts.Add(new FactRow("experience", "Experience asked", foundExperience.Range.ToString(), ev, false));
        if (requiredFound.Count > 0) facts.Add(new FactRow("requiredSkillsFound", "Required skills mentioned", string.Join(", ", requiredFound), ev, false));
        if (salary is not null) facts.Add(new FactRow("salary", "Salary", salary, ev, false));
        if (input.PostedAt is { } at) facts.Add(new FactRow("postedAt", "Posted", at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ev, false));
        if (agency is not null) facts.Add(new FactRow("staffingAgency", "Staffing agency signal", agency.Excerpt, ev, false));

        return new RuleResult(outcome, reason, score, coverage, rows, facts, gaps.Distinct().ToList(), checks);
    }

    /// <summary>
    /// Reads the first experience requirement in the text. Recognises "3-5 years", "3 to 5 yrs", "5+ years",
    /// "minimum 4 years" / "at least 4 years" and "4 years of experience" (read as a minimum).
    /// </summary>
    public static ExperienceRange? ParseExperience(string? text) => FindExperience(text)?.Range;

    /// <summary><see cref="ParseExperience"/> plus the sentence the requirement was read from.</summary>
    public static (ExperienceRange Range, string Excerpt)? FindExperience(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string Sentence(Match m) => Excerpts.SentenceSpan(text, m.Index, m.Index + m.Length);
        var range = RangePattern().Match(text);
        if (range.Success && Num(range.Groups["min"]) is { } lo && Num(range.Groups["max"]) is { } hi && lo <= hi && hi <= 50)
            return (new ExperienceRange(lo, hi), Sentence(range));
        var plus = PlusPattern().Match(text);
        if (plus.Success && Num(plus.Groups["min"]) is { } p && p <= 50) return (new ExperienceRange(p, null), Sentence(plus));
        var minimum = MinimumPattern().Match(text);
        if (minimum.Success && Num(minimum.Groups["min"]) is { } m && m <= 50) return (new ExperienceRange(m, null), Sentence(minimum));
        var plain = PlainPattern().Match(text);
        if (plain.Success && Num(plain.Groups["min"]) is { } n && n <= 50) return (new ExperienceRange(n, null), Sentence(plain));
        return null;
    }

    /// <summary>Remote, Hybrid or Onsite from keywords; null when the posting does not say. Hybrid wins over the others.</summary>
    public static string? DetectWorkMode(string? text, string? location = null) => DetectWorkModeWithEvidence(text, location, null)?.Value;

    /// <summary>
    /// The source's structured work-mode field when it has one ("workplaceType field = remote"); otherwise keywords in the
    /// location field, then in the text (Hybrid wins, then Remote, then Onsite), with the sentence they were found in.
    /// </summary>
    public static Detected? DetectWorkModeWithEvidence(string? text, string? location, string? workplaceType)
    {
        if (FieldWorkMode(workplaceType) is { } fromField)
            return new Detected(fromField, Excerpts.Field("workplaceType", workplaceType!.Trim().ToLowerInvariant()));

        foreach (var (mode, pattern) in new[] { (Hybrid, HybridPattern()), (Remote, RemotePattern()), (Onsite, OnsitePattern()) })
        {
            if (!string.IsNullOrWhiteSpace(location) && pattern.IsMatch(location)) return new Detected(mode, Excerpts.Field("location", location));
            if (!string.IsNullOrWhiteSpace(text) && pattern.Match(text) is { Success: true } m)
                return new Detected(mode, Excerpts.SentenceSpan(text, m.Index, m.Index + m.Length));
        }
        return null;
    }

    /// <summary>Lever's <c>workplaceType</c> values; anything else is not a usable field value.</summary>
    public static string? FieldWorkMode(string? workplaceType) => workplaceType?.Trim().ToLowerInvariant() switch
    {
        "remote" => Remote,
        "hybrid" => Hybrid,
        "on-site" or "onsite" => Onsite,
        _ => null
    };

    /// <summary>
    /// A staffing-agency signal: a phrase in the body ("our client", "staffing", "recruitment agency", "on behalf of our",
    /// "C2H", "contract to hire") — its sentence is the excerpt — or else an agency-like company name ("staffing",
    /// "consultanc…", "recruit…", "manpower", "talent solutions"). Null when there is no signal.
    /// </summary>
    public static Detected? DetectStaffingAgency(string? organization, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text) && AgencyBodyPattern().Match(text) is { Success: true } m)
            return new Detected("yes", Excerpts.SentenceSpan(text, m.Index, m.Index + m.Length));
        if (!string.IsNullOrWhiteSpace(organization) && AgencyNamePattern().IsMatch(organization))
            return new Detected("yes", Excerpts.Bound($"company name \"{organization.Trim()}\" matches an agency pattern")!);
        return null;
    }

    public static string? FindSalary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = SalaryPattern().Match(text);
        if (m.Success) return m.Value.Trim();
        var lpa = LpaPattern().Match(text);
        return lpa.Success ? lpa.Value.Trim() : null;
    }

    private static CriterionScore SkillScore(string key, string label, IReadOnlyList<string> wanted, List<string>? found, string corpus,
        List<string> gaps, string what)
    {
        if (found is null)
        {
            gaps.Add("Posting description is empty");
            return new CriterionScore(key, label, null, $"{what} cannot be checked: the posting has no description.");
        }
        var missing = wanted.Where(w => !found.Contains(w)).ToList();
        var value = Scoring.FractionValue(found.Count, wanted.Count);
        var reason = found.Count == wanted.Count
            ? $"All {wanted.Count} found: {string.Join(", ", found)}."
            : found.Count == 0
                ? $"None of {wanted.Count} found. Missing: {string.Join(", ", missing)}."
                : $"Found {found.Count} of {wanted.Count}: {string.Join(", ", found)}. Missing: {string.Join(", ", missing)}.";
        return new CriterionScore(key, label, value, reason, Excerpts.ForTerms(corpus, found));
    }

    private static CriterionScore ExperienceScore(double years, (ExperienceRange Range, string Excerpt)? found, List<string> gaps)
    {
        const string label = "Experience";
        var y = years.ToString(CultureInfo.InvariantCulture);
        if (found is not { } f)
        {
            gaps.Add("Experience requirement not stated");
            return new CriterionScore(CampaignWeights.Experience, label, null, "The posting does not state an experience range.");
        }
        var range = f.Range;
        var distance = years < range.Min ? range.Min - years : range.Max is { } max && years > max ? years - max : 0;
        return distance switch
        {
            0.0 => new CriterionScore(CampaignWeights.Experience, label, 1, $"Your {y} years are within the asked {range}.", f.Excerpt),
            <= 1.0 => new CriterionScore(CampaignWeights.Experience, label, 0.5, $"Your {y} years are within a year of the asked {range}.", f.Excerpt),
            _ => new CriterionScore(CampaignWeights.Experience, label, 0, $"Your {y} years are outside the asked {range}.", f.Excerpt)
        };
    }

    private static CriterionScore LocationScore(CampaignCriteria criteria, string? location, Detected? workMode, Detected? match)
    {
        const string label = "Location and work mode";
        if (criteria.WorkModes.Count > 0 && workMode is not null && !criteria.WorkModes.Contains(workMode.Value))
            return new CriterionScore(CampaignWeights.Location, label, 0, $"Work mode {workMode.Value} is not one you accept.", workMode.Excerpt);

        if (criteria.Locations.Count > 0)
        {
            if (match is not null) return new CriterionScore(CampaignWeights.Location, label, 1, $"Location matches {match.Value}.", match.Excerpt);
            if (location is not null && !PlaceMatch.IsUnspecific(location))
                return new CriterionScore(CampaignWeights.Location, label, 0, $"Location \"{location}\" is not in your list.", Excerpts.Field("location", location));
            return new CriterionScore(CampaignWeights.Location, label, null, "The posting does not state a location.");
        }

        return workMode is null
            ? new CriterionScore(CampaignWeights.Location, label, null, "The posting does not state a work mode.")
            : new CriterionScore(CampaignWeights.Location, label, 1, $"Work mode {workMode.Value} is accepted.", workMode.Excerpt);
    }

    /// <summary>
    /// The configured location that matches the location field or text ("Remote" also matches a remote posting), with
    /// the field note or sentence that matched.
    /// </summary>
    private static Detected? MatchLocation(CampaignCriteria criteria, string? location, string corpus, Detected? workMode)
    {
        foreach (var l in criteria.Locations)
        {
            if (TextMatch.Contains(location, l) || PlaceMatch.InField(location, l)) return new Detected(l, Excerpts.Field("location", location!));
            if (Excerpts.ForTerm(corpus, l) is { } sentence) return new Detected(l, sentence);
            if (string.Equals(l, Remote, StringComparison.OrdinalIgnoreCase) && workMode?.Value == Remote) return new Detected(l, workMode.Excerpt);
        }
        return null;
    }

    private static double? Num(Group g) =>
        g.Success && double.TryParse(g.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private const string Years = @"(?:years?|yrs?)";

    [GeneratedRegex(@"(?<![\d.])(?<min>\d{1,2}(?:\.\d)?)\s*(?:-|–|—|to)\s*(?<max>\d{1,2}(?:\.\d)?)\s*\+?\s*" + Years + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"(?<![\d.])(?<min>\d{1,2}(?:\.\d)?)\s*\+\s*" + Years + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlusPattern();

    [GeneratedRegex(@"\b(?:minimum|min\.?|at\s+least|atleast)\s*(?:of\s+)?(?<min>\d{1,2}(?:\.\d)?)\s*\+?\s*" + Years + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MinimumPattern();

    [GeneratedRegex(@"(?<![\d.])(?<min>\d{1,2}(?:\.\d)?)\s*" + Years + @"\s*(?:of\s+)?(?:\w+\s+)?(?:experience|exp)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlainPattern();

    [GeneratedRegex(@"\bhybrid\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HybridPattern();

    [GeneratedRegex(@"\b(?:remote|work\s+from\s+home|wfh|work\s+from\s+anywhere)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemotePattern();

    [GeneratedRegex(@"\b(?:on-?site|on\s+site|in-?office|in\s+office|work\s+from\s+office|wfo)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnsitePattern();

    // Versioned with the rules (harness AGENCY_PAT / AGENCY_NAME): changing these changes outcomes.
    [GeneratedRegex(@"\b(?:our\s+client|staffing|recruitment\s+agency|on\s+behalf\s+of\s+our|c2h|contract\s+to\s+hire)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgencyBodyPattern();

    [GeneratedRegex(@"\b(?:staffing\b|consultanc|recruit|manpower\b|talent\s+solutions\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgencyNamePattern();

    [GeneratedRegex(@"(?:₹|rs\.?|inr|\$|usd|€|eur|£|gbp)\s?\d[\d,]*(?:\.\d+)?\s?(?:(?:lpa|lakhs?|cr|k|l)\b)?(?:\s*(?:-|–|to)\s*(?:₹|rs\.?|inr|\$|usd|€|eur|£|gbp)?\s?\d[\d,]*(?:\.\d+)?\s?(?:(?:lpa|lakhs?|cr|k|l)\b)?)?(?:\s*(?:per\s+(?:year|annum|month)|/\s?(?:yr|year|month|mo)|p\.?a\.?))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SalaryPattern();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d+)?\s*(?:-|–|to)\s*\d{1,3}(?:\.\d+)?\s*(?:lpa|lakhs?\s+per\s+annum)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LpaPattern();
}
