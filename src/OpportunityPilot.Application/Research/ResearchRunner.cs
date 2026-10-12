using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Campaigns;
using OpportunityPilot.Application.Imports;
using OpportunityPilot.Application.Research.Boards;
using OpportunityPilot.Application.Research.Rules;
using OpportunityPilot.Application.Sources;
using OpportunityPilot.Domain.Campaigns;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.Research;

/// <summary>
/// Runs one durable research job: Prepare → Gather (per source) → Extract → Filter → Score → Complete.
/// Not owner-scoped through <see cref="ICurrentUser"/> (there is no request); every query filters by the job's owner.
/// The only opportunity status it ever changes is New → Suggested (campaign auto-suggest), after scoring.
/// </summary>
public sealed class ResearchRunner(
    IAppDbContext db,
    IWebFetcher fetcher,
    IContentParser parser,
    JobBoardGatherer boards,
    IOptions<ResearchOptions> options,
    TimeProvider clock,
    ILogger<ResearchRunner> logger,
    Common.OperationalMetrics metrics) : IResearchRunner
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private const int ClaimTries = 5;

    private readonly ResearchOptions _limits = options.Value;
    private DateTime _lastEventAt = DateTime.MinValue;

    /// <summary>Suggestions made in this run but not yet saved, so a concurrent user status change can withdraw them.</summary>
    private readonly Dictionary<Guid, Activity> _pendingSuggestions = [];

    private sealed class LeaseLostException : Exception;

    /// <param name="Available">Items the source holds when only some were read (Csv/Agent rows beyond the run's room).</param>
    private sealed record Gathered(SourceStatus Status, string? SafeError, List<Candidate> Items, int Requests, string Message, EventLevel Level,
        int? Available = null)
    {
        public int Total => Available ?? Items.Count;
    }

    private sealed record Scored(Candidate Candidate, Evidence Evidence, RuleResult Result, string Key);

    public async Task<bool> RunNextAsync(CancellationToken ct)
    {
        var job = await ClaimAsync(ct);
        if (job is null) return false;

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["ResearchJobId"] = job.Id });
        try
        {
            await ExecuteAsync(job, ct);
        }
        catch (LeaseLostException)
        {
            logger.LogWarning("Research job {JobId} was taken over by another processor; stopping this copy", job.Id);
            db.ChangeTracker.Clear();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down: leave the job Running. Its lease expires and the next processor resumes it;
            // upserts by dedupe key keep the rerun from duplicating anything.
            db.ChangeTracker.Clear();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Research job {JobId} stopped on an unexpected error", job.Id);
            await RecordFailureAsync(job.Id, job.Attempts);
        }
        return true;
    }

    private async Task<ResearchJob?> ClaimAsync(CancellationToken ct)
    {
        for (var i = 0; i < ClaimTries; i++)
        {
            var now = Now();
            var job = await db.ResearchJobs
                .Where(j => j.State == ResearchJobState.Queued ||
                            (j.State == ResearchJobState.Running && (j.LeaseUntil == null || j.LeaseUntil < now)))
                .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id)
                .FirstOrDefaultAsync(ct);
            if (job is null) return null;

            var exhausted = job.State == ResearchJobState.Running && job.AttemptsExhausted;
            job.Claim(now, Lease);
            if (exhausted)
            {
                // A job that keeps dying (crash, restart loop) is failed instead of being retried forever.
                job.Finish(ResearchJobState.Failed, $"Research stopped after {ResearchJob.MaxAttempts} interrupted attempts.", now);
                AddEvent(job, ResearchStage.Complete, EventLevel.Error, $"Stopped after {ResearchJob.MaxAttempts} interrupted attempts.");
            }

            try
            {
                await db.SaveChangesAsync(ct);
                if (!exhausted) return job;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another processor claimed it between our read and our write (the version check caught it).
            }
            db.ChangeTracker.Clear();
        }
        return null;
    }

    private async Task ExecuteAsync(ResearchJob job, CancellationToken ct)
    {
        var ownerId = job.OwnerId;
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == job.CampaignId && c.OwnerId == ownerId, ct);
        if (campaign is null || !CampaignCriteria.IsSupportedMode(campaign.Mode))
        {
            job.Finish(ResearchJobState.Failed, campaign is null ? "The campaign no longer exists." : $"{campaign.Mode} campaigns are not supported yet.", Now());
            await SaveAsync(job, ct);
            return;
        }

        var criteria = CampaignCriteria.FromJson(campaign.CriteriaJson);
        var weights = CampaignWeights.FromJson(campaign.Mode, campaign.WeightsJson);
        var sources = await db.Sources.Where(s => s.OwnerId == ownerId && s.CampaignId == campaign.Id)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToListAsync(ct);

        // Prepare
        var counts = new ResearchCounts(Sources: sources.Count);
        AddEvent(job, ResearchStage.Prepare, EventLevel.Info, Summary(campaign, criteria, sources.Count));
        job.Progress(ResearchStage.Gather, counts.ToJson(), Now(), Lease);
        await SaveAsync(job, ct);

        // Gather, one source at a time; counts and source status are persisted after each.
        var maxCandidates = _limits.EffectiveCandidates;
        var fetchesLeft = _limits.EffectiveFetches;
        var candidates = new List<Candidate>();
        var cancelled = false;
        // Each fetching source gets an equal share of what is left of the budget, so one large board cannot starve the
        // rest; whatever a source leaves unused flows on to the next one.
        static bool Fetches(SourceKind k) => k is SourceKind.Url or SourceKind.Feed or SourceKind.Greenhouse or SourceKind.Lever or SourceKind.Adzuna or
            SourceKind.Ashby or SourceKind.SmartRecruiters or SourceKind.Recruitee or SourceKind.Workable or SourceKind.Workday or SourceKind.JobSearch or SourceKind.Remotive or SourceKind.RemoteOk or
            SourceKind.Arbeitnow or SourceKind.Jobicy or SourceKind.WeWorkRemotely;
        var fetchingLeft = sources.Count(s => Fetches(s.Kind));
        var sourcesLeft = sources.Count;
        foreach (var source in sources)
        {
            if (await CancelRequestedAsync(job, ct))
            {
                cancelled = true;
                AddEvent(job, ResearchStage.Gather, EventLevel.Warning, "Cancelled: no further sources were read. Results gathered so far are kept.");
                break;
            }

            var totalRoom = maxCandidates - candidates.Count;
            var room = totalRoom <= 0 ? 0 : Math.Max(1, totalRoom / Math.Max(1, sourcesLeft));
            var fetchShare = Fetches(source.Kind) ? Math.Max(1, fetchesLeft / Math.Max(1, fetchingLeft)) : fetchesLeft;
            if (fetchesLeft <= 0) fetchShare = 0;
            sourcesLeft--;
            if (Fetches(source.Kind)) fetchingLeft--;
            var gathered = room <= 0
                ? new Gathered(SourceStatus.Skipped, $"Skipped: this run already reached its limit of {maxCandidates} candidates.", [], 0,
                    $"{source.Label}: skipped, candidate limit ({maxCandidates}) reached.", EventLevel.Warning)
                : await GatherAsync(source, campaign.Mode, criteria, room, fetchShare, KeepAlive, ct);
            fetchesLeft -= gathered.Requests;

            var items = gathered.Items.Take(Math.Max(0, room)).ToList();
            if (gathered.Total > items.Count && gathered.Status == SourceStatus.Ok)
                AddEvent(job, ResearchStage.Gather, EventLevel.Warning,
                    $"{source.Label}: {gathered.Total - items.Count} items beyond the run's limit of {maxCandidates} candidates were not processed.");
            candidates.AddRange(items);

            // Csv and Agent sources keep their stored row count; the others record what this fetch or parse found.
            var itemCount = source.Kind is SourceKind.Csv or SourceKind.Agent || gathered.Status != SourceStatus.Ok ? source.ItemCount : gathered.Total;
            source.RecordGather(gathered.Status, gathered.SafeError, itemCount, Now());
            var failed = gathered.Status == SourceStatus.Failed;
            counts = counts with
            {
                SourcesDone = counts.SourcesDone + 1,
                SourcesFailed = counts.SourcesFailed + (failed ? 1 : 0),
                Fetched = counts.Fetched + gathered.Total
            };
            AddEvent(job, ResearchStage.Gather, gathered.Level, gathered.Message);
            job.Progress(ResearchStage.Gather, counts.ToJson(), Now(), Lease);
            await SaveAsync(job, ct);
        }

        // A job-board source can make dozens of requests; between them the lease is renewed and a cancel is noticed.
        async Task<bool> KeepAlive(CancellationToken token)
        {
            job.Progress(ResearchStage.Gather, counts.ToJson(), Now(), Lease);
            await SaveAsync(job, token);
            return !await CancelRequestedAsync(job, token);
        }
        if (!cancelled && await CancelRequestedAsync(job, ct)) cancelled = true;

        // Extract: one evidence row per candidate, reused when a rerun sees the identical excerpt.
        job.Progress(ResearchStage.Extract, counts.ToJson(), Now(), Lease);
        var evidence = await EvidenceForAsync(job, campaign, candidates, ct);
        counts = counts with { Candidates = candidates.Count };
        AddEvent(job, ResearchStage.Extract, EventLevel.Info,
            $"Extracted {candidates.Count} candidate{Plural(candidates.Count)} with rules (skills, experience, location, work mode).");

        // Filter and score (pure). Within one run the same dedupe key keeps its best-scoring copy.
        job.Progress(ResearchStage.Filter, counts.ToJson(), Now(), Lease);
        var scored = candidates.Select((c, i) =>
            {
                var input = new RuleInput(c.Title, c.Organization, c.Location, c.Text, evidence[i].Id.ToString(),
                    c.Website, c.Country, c.Industry, c.Links, c.WorkplaceType, c.PostedAt);
                var result = campaign.Mode == OpportunityMode.Job
                    ? JobRules.Evaluate(criteria, weights, input, Now())
                    : CustomerRules.Evaluate(criteria, weights, input);
                return new Scored(c, evidence[i], result,
                    Candidates.DedupeKey(campaign.Mode, c.Platform, c.ExternalId, c.Title, c.Organization, c.Website));
            })
            .GroupBy(s => s.Key)
            .Select(g => g.OrderByDescending(s => s.Result.Score).First())
            .ToList();
        counts = counts with
        {
            Qualified = scored.Count(s => s.Result.Outcome == FilterOutcome.Qualified),
            NeedsVerification = scored.Count(s => s.Result.Outcome == FilterOutcome.NeedsVerification),
            Excluded = scored.Count(s => s.Result.Outcome == FilterOutcome.Excluded)
        };
        AddEvent(job, ResearchStage.Filter, EventLevel.Info,
            $"Hard filters: {counts.Qualified} qualified, {counts.NeedsVerification} need verification, {counts.Excluded} excluded.");

        job.Progress(ResearchStage.Score, counts.ToJson(), Now(), Lease);
        var (created, updated, overLimit, suggested) = await UpsertAsync(job, campaign, scored, ct);
        AddEvent(job, ResearchStage.Score, EventLevel.Info,
            $"Saved {created} new and updated {updated} existing opportunit{(created + updated == 1 ? "y" : "ies")}." +
            (overLimit > 0 ? $" {overLimit} more new one{Plural(overLimit)} beyond the campaign's result limit ({campaign.ResultLimit}) were not saved." : "") +
            (suggested > 0 ? $" {suggested} suggested for your approval (score ≥ {campaign.AutoSuggestMinScore})." : ""));
        job.Progress(ResearchStage.Score, counts.ToJson(), Now(), Lease);
        await SaveAsync(job, ct);

        // Complete
        var state = cancelled ? ResearchJobState.Cancelled
            : counts.SourcesFailed > 0 ? ResearchJobState.CompletedWithGaps
            : ResearchJobState.Completed;
        var gap = counts.SourcesFailed > 0 ? $"{counts.SourcesFailed} source{Plural(counts.SourcesFailed)} could not be read; see the events for reasons." : null;
        job.Finish(state, gap, Now());
        metrics.Record("research.run", Now() - (job.StartedAt ?? job.CreatedAt), state == ResearchJobState.Completed ? null : state.ToString());
        AddEvent(job, ResearchStage.Complete, state == ResearchJobState.Completed ? EventLevel.Info : EventLevel.Warning, state switch
        {
            ResearchJobState.Cancelled => "Cancelled. Opportunities found before cancelling were kept.",
            ResearchJobState.CompletedWithGaps => $"Completed with gaps: {gap}",
            _ => "Completed."
        });
        await SaveAsync(job, ct);
    }

    private async Task<Gathered> GatherAsync(Source source, OpportunityMode mode, CampaignCriteria criteria, int room, int fetchesLeft,
        Func<CancellationToken, Task<bool>> keepAlive, CancellationToken ct)
    {
        switch (source.Kind)
        {
            case SourceKind.Paste:
            {
                var items = PasteParser.Parse(source.Text).Select(e => FromPaste(source, mode, e)).ToList();
                return items.Count == 0
                    ? new(SourceStatus.Skipped, "No postings or companies were found in the pasted text.", [], 0, $"{source.Label}: nothing to read.", EventLevel.Warning)
                    : new(SourceStatus.Ok, null, items, 0, $"{source.Label}: read {items.Count} pasted item{Plural(items.Count)}.", EventLevel.Info);
            }
            case SourceKind.Csv or SourceKind.Agent:
            {
                // Newest rows first, so an agent source that keeps growing is researched on what it delivered last.
                var owned = db.SourceItems.Where(i => i.SourceId == source.Id && i.OwnerId == source.OwnerId);
                var total = await owned.CountAsync(ct);
                var rows = await owned.OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.Id).Take(room).ToListAsync(ct);
                var items = rows.Select(r => FromRow(source, mode, r)).ToList();
                return total == 0
                    ? new(SourceStatus.Skipped, "The source has no rows.", [], 0, $"{source.Label}: no rows.", EventLevel.Warning)
                    : new(SourceStatus.Ok, null, items, 0, $"{source.Label}: read {items.Count} of {total} row{Plural(total)}.", EventLevel.Info, total);
            }
            case SourceKind.Url or SourceKind.Feed:
            {
                if (fetchesLeft <= 0)
                    return new(SourceStatus.Skipped, $"Skipped: this run already used its {_limits.EffectiveFetches} page fetches.", [], 0,
                        $"{source.Label}: skipped, fetch limit reached.", EventLevel.Warning);
                var result = await fetcher.FetchAsync(source.Url ?? string.Empty, ct);
                if (!result.Ok)
                    return new(SourceStatus.Failed, result.FailureReason, [], result.Requests,
                        $"{source.Label}: could not be read safely — {result.FailureReason}", EventLevel.Warning);
                return source.Kind == SourceKind.Url ? FromPage(source, mode, result) : FromFeed(source, mode, result);
            }
            case SourceKind.JobSearch:
            {
                var search = await boards.JobSearchAsync(source, criteria, mode, room, fetchesLeft, ct);
                return new(search.Status, search.SafeError, search.Items.ToList(), search.Requests, search.Message, search.Level);
            }
            case SourceKind.Greenhouse or SourceKind.Lever or SourceKind.Adzuna or SourceKind.Ashby or SourceKind.SmartRecruiters or
                SourceKind.Recruitee or SourceKind.Workable or SourceKind.Workday or SourceKind.Remotive or SourceKind.RemoteOk or
                SourceKind.Arbeitnow or SourceKind.Jobicy or SourceKind.WeWorkRemotely:
            {
                if (mode != OpportunityMode.Job)
                    return new(SourceStatus.Skipped, "Job-board sources are read for Job campaigns only.", [], 0,
                        $"{source.Label}: skipped, job boards apply to Job campaigns only.", EventLevel.Warning);
                var board = await boards.GatherAsync(source, criteria, room, fetchesLeft, keepAlive, ct);
                return new(board.Status, board.SafeError, board.Items.ToList(), board.Requests, board.Message, board.Level);
            }
            default:
                return new(SourceStatus.Skipped, "Unsupported source kind.", [], 0, $"{source.Label}: unsupported source kind.", EventLevel.Warning);
        }
    }

    private Gathered FromPage(Source source, OpportunityMode mode, FetchResult fetched)
    {
        var content = fetched.Content ?? string.Empty;
        // Plain text is encoded first so a stray "<" in it is not read as markup.
        var page = parser.ParseHtml(fetched.ContentType == "text/plain" ? $"<pre>{System.Net.WebUtility.HtmlEncode(content)}</pre>" : content,
            fetched.FinalUrl);
        if (page.Text.Length < Candidates.MinPageTextLength)
            return new(SourceStatus.Skipped,
                "NeedsManualInput: the page has too little readable text (it may need a login or JavaScript). Paste the details instead.",
                [], fetched.Requests, $"{source.Label}: needs manual input — the page shows almost no text without a login or JavaScript.", EventLevel.Warning);

        var url = fetched.FinalUrl ?? source.Url;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : source.Label;
        var text = Candidates.Bound(page.Text);
        Candidate candidate;
        if (mode == OpportunityMode.Job)
        {
            var title = page.Title?.Trim() is { Length: > 0 } t ? t : host;
            candidate = new(source.Id, source.Label, title, Candidates.Labelled(text, "Company") ?? host, Candidates.Labelled(text, "Location"),
                url, null, null, text, null, null, null, url, $"{title}\n{text}", page.Links);
        }
        else
        {
            var name = Candidates.CleanTitle(page.Title) ?? host;
            var website = uri is null ? null : $"{uri.Scheme}://{uri.Host}";
            candidate = new(source.Id, source.Label, name, name, Candidates.Labelled(text, "Location"), url, null, null, text, website,
                Candidates.Labelled(text, "Country"), Candidates.Labelled(text, "Industry"), url, $"{name}\n{text}", page.Links);
        }
        return new(SourceStatus.Ok, null, [candidate], fetched.Requests, $"{source.Label}: fetched 1 page ({text.Length:N0} characters of text).", EventLevel.Info);
    }

    private Gathered FromFeed(Source source, OpportunityMode mode, FetchResult fetched)
    {
        var feed = parser.ParseFeed(fetched.Content ?? string.Empty);
        if (!feed.IsFeed)
            return new(SourceStatus.Failed, feed.FailureReason ?? "Not an RSS or Atom feed.", [], fetched.Requests,
                $"{source.Label}: {feed.FailureReason ?? "not an RSS or Atom feed."}", EventLevel.Warning);
        if (feed.Entries.Count == 0)
            return new(SourceStatus.Skipped, "The feed has no entries.", [], fetched.Requests, $"{source.Label}: the feed has no entries.", EventLevel.Warning);

        var host = Uri.TryCreate(fetched.FinalUrl ?? source.Url, UriKind.Absolute, out var uri) ? uri.Host : source.Label;
        var items = feed.Entries.Select(e =>
        {
            var text = Candidates.Bound(e.Text);
            var link = e.Link ?? fetched.FinalUrl;
            var dated = e.PublishedAt is { } at ? $"\nPublished: {at:yyyy-MM-dd}" : "";
            return mode == OpportunityMode.Job
                ? new Candidate(source.Id, source.Label, e.Title, Candidates.Labelled(text, "Company") ?? feed.Title ?? host,
                    Candidates.Labelled(text, "Location"), link, null, null, text, null, null, null, link, $"{e.Title}{dated}\n{text}", [])
                : new Candidate(source.Id, source.Label, e.Title, e.Title, Candidates.Labelled(text, "Location"), link, null, null, text,
                    null, Candidates.Labelled(text, "Country"), Candidates.Labelled(text, "Industry"), link, $"{e.Title}{dated}\n{text}", []);
        }).ToList();
        return new(SourceStatus.Ok, null, items, fetched.Requests, $"{source.Label}: read {items.Count} feed entr{(items.Count == 1 ? "y" : "ies")}.", EventLevel.Info);
    }

    private static Candidate FromPaste(Source source, OpportunityMode mode, PastedEntry e)
    {
        var url = IsHttp(e.Url) ? e.Url : null;
        if (mode == OpportunityMode.Job)
            return new(source.Id, source.Label, e.Title, e.Company ?? string.Empty, e.Location, url, null, null,
                Candidates.Bound(e.Description), e.Website, e.Country, e.Industry, url, e.Raw, []);
        var website = e.Website is null ? null : ImportService.NormaliseWebsite(e.Website);
        return new(source.Id, source.Label, e.Title, e.Company ?? e.Title, e.Location, url ?? website, null, null,
            Candidates.Bound(e.Description), website, e.Country, e.Industry, url ?? website, e.Raw, []);
    }

    private static Candidate FromRow(Source source, OpportunityMode mode, SourceItem r)
    {
        var lines = new List<string> { r.Title };
        if (r.Organization.Length > 0 && r.Organization != r.Title) lines.Add($"Company: {r.Organization}");
        if (r.Location is not null) lines.Add($"Location: {r.Location}");
        if (r.Country is not null) lines.Add($"Country: {r.Country}");
        if (r.Industry is not null) lines.Add($"Industry: {r.Industry}");
        if (r.Website is not null) lines.Add($"Website: {r.Website}");
        if (r.Url is not null) lines.Add($"URL: {r.Url}");
        if (r.Description is not null) lines.Add("\n" + r.Description);
        var excerpt = string.Join('\n', lines);

        return mode == OpportunityMode.Job
            ? new(source.Id, source.Label, r.Title, r.Organization, r.Location, r.Url, source.Platform, r.ExternalId,
                Candidates.Bound(r.Description), r.Website, r.Country, r.Industry, r.Url, excerpt, [])
            : new(source.Id, source.Label, r.Title, r.Organization.Length > 0 ? r.Organization : r.Title, r.Location, r.Url ?? r.Website,
                null, null, Candidates.Bound(r.Description), r.Website, r.Country, r.Industry, r.Url ?? r.Website, excerpt, []);
    }

    private async Task<List<Evidence>> EvidenceForAsync(ResearchJob job, Campaign campaign, List<Candidate> candidates, CancellationToken ct)
    {
        var hashes = candidates.Select(c => Evidence.HashOf(c.Excerpt)).Distinct().ToList();
        var existing = await db.Evidence
            .Where(e => e.OwnerId == job.OwnerId && e.CampaignId == campaign.Id && hashes.Contains(e.ContentHash))
            .ToListAsync(ct);
        var byKey = existing.GroupBy(e => (e.SourceId, e.ContentHash)).ToDictionary(g => g.Key, g => g.First());

        var result = new List<Evidence>(candidates.Count);
        var now = Now();
        foreach (var c in candidates)
        {
            var key = (c.SourceId, Evidence.HashOf(c.Excerpt));
            if (!byKey.TryGetValue(key, out var row))
            {
                row = new Evidence(job.OwnerId, campaign.Id, c.SourceId, c.EvidenceUrl, now, c.Excerpt, Evidence.RulesMethod);
                db.Evidence.Add(row);
                byKey[key] = row;
            }
            result.Add(row);
        }
        return result;
    }

    private async Task<(int Created, int Updated, int OverLimit, int Suggested)> UpsertAsync(
        ResearchJob job, Campaign campaign, List<Scored> scored, CancellationToken ct)
    {
        var now = Now();
        var keys = scored.Select(s => s.Key).ToList();
        var existing = (await db.Opportunities
                .Where(o => o.OwnerId == job.OwnerId && o.CampaignId == campaign.Id && keys.Contains(o.DedupeKey))
                .ToListAsync(ct))
            .ToDictionary(o => o.DedupeKey);
        var existingIds = existing.Values.Select(o => o.Id).ToList();
        var links = await db.OpportunityEvidence.Where(l => existingIds.Contains(l.OpportunityId)).ToListAsync(ct);

        var updated = 0;
        foreach (var s in scored.Where(s => existing.ContainsKey(s.Key)))
        {
            var o = existing[s.Key];
            var previous = o.Score;
            Apply(o, s, campaign.Mode, job.Id, now);
            foreach (var stale in links.Where(l => l.OpportunityId == o.Id && l.EvidenceId != s.Evidence.Id))
                db.OpportunityEvidence.Remove(stale);
            if (!links.Any(l => l.OpportunityId == o.Id && l.EvidenceId == s.Evidence.Id))
                db.OpportunityEvidence.Add(new OpportunityEvidence(o.Id, s.Evidence.Id));
            if (previous != o.Score)
                db.Activities.Add(new Activity(o.OwnerId, o.Id, ActivityKinds.Researched, now, $"Re-scored {previous} → {o.Score} by research."));
            Suggest(o, campaign, now);
            updated++;
        }

        // The result limit caps new rows only: best outcome first, then highest score.
        var fresh = scored.Where(s => !existing.ContainsKey(s.Key))
            .OrderBy(s => s.Result.Outcome switch { FilterOutcome.Qualified => 0, FilterOutcome.NeedsVerification => 1, _ => 2 })
            .ThenByDescending(s => s.Result.Score)
            .ToList();
        var kept = fresh.Take(campaign.ResultLimit).ToList();
        foreach (var s in kept)
        {
            var o = new Opportunity(job.OwnerId, campaign.Id, campaign.Mode, s.Key, now);
            Apply(o, s, campaign.Mode, job.Id, now);
            db.Opportunities.Add(o);
            db.OpportunityEvidence.Add(new OpportunityEvidence(o.Id, s.Evidence.Id));
            db.Activities.Add(new Activity(o.OwnerId, o.Id, ActivityKinds.Researched, now,
                $"Found in \"{s.Candidate.SourceLabel}\" and scored {o.Score} ({o.Outcome})."));
            Suggest(o, campaign, now);
        }
        return (kept.Count, updated, fresh.Count - kept.Count, _pendingSuggestions.Count);
    }

    /// <summary>New → Suggested when the campaign's auto-suggest rule holds (see <see cref="Opportunity.SuggestForApproval"/>).</summary>
    private void Suggest(Opportunity o, Campaign campaign, DateTime now)
    {
        if (o.SuggestForApproval(campaign.AutoSuggestMinScore, now) is not { } activity) return;
        db.Activities.Add(activity);
        _pendingSuggestions[o.Id] = activity;
    }

    private static void Apply(Opportunity o, Scored s, OpportunityMode mode, Guid jobId, DateTime now)
    {
        var c = s.Candidate;
        var r = s.Result;
        o.ApplyResearch(c.Title, c.Organization, c.Location ?? (mode == OpportunityMode.Customer ? c.Country : null),
            c.Url, mode == OpportunityMode.Job ? c.ApplyUrl ?? c.Url : null, c.Platform, c.ExternalId, c.Text.Length > 0 ? c.Text : null,
            r.Score, r.Coverage, r.Outcome, r.OutcomeReason,
            JsonSerializer.Serialize(r.Breakdown, JsonSerializerOptions.Web),
            JsonSerializer.Serialize(r.Facts, JsonSerializerOptions.Web),
            JsonSerializer.Serialize(r.Gaps, JsonSerializerOptions.Web),
            r.Gaps.Count, jobId, now);
    }

    private async Task<bool> CancelRequestedAsync(ResearchJob job, CancellationToken ct) =>
        job.CancelRequested ||
        await db.ResearchJobs.AsNoTracking().Where(j => j.Id == job.Id).Select(j => j.CancelRequested).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Saves, resolving version conflicts caused by the user (cancel request, status change) rather than failing:
    /// properties this processor did not touch take the stored value, ours are kept, versions move past both.
    /// A conflict on the job's claim count means another processor reclaimed it, and this copy stops.
    /// </summary>
    private async Task SaveAsync(ResearchJob job, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                _pendingSuggestions.Clear();
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < 3)
            {
                foreach (var entry in ex.Entries)
                {
                    var stored = await entry.GetDatabaseValuesAsync(ct);
                    if (stored is null)
                    {
                        if (entry.Entity is ResearchJob) throw new LeaseLostException();
                        entry.State = EntityState.Detached;
                        continue;
                    }
                    if (entry.Entity is ResearchJob && stored.GetValue<int>(nameof(ResearchJob.Attempts)) != job.Attempts)
                        throw new LeaseLostException();

                    // The user moved the opportunity while this run was scoring it: their status wins and the
                    // suggestion is withdrawn, because research may only ever move New → Suggested.
                    if (entry.Entity is Opportunity o && _pendingSuggestions.TryGetValue(o.Id, out var suggestion) &&
                        stored[nameof(Opportunity.Status)] is OpportunityStatus storedStatus && storedStatus != OpportunityStatus.New)
                    {
                        entry.Property(nameof(Opportunity.Status)).IsModified = false;
                        db.Activities.Remove(suggestion);
                        _pendingSuggestions.Remove(o.Id);
                    }

                    foreach (var property in entry.Properties)
                    {
                        var value = stored[property.Metadata];
                        if (!property.IsModified) property.CurrentValue = value;
                        property.OriginalValue = value;
                    }
                    var version = entry.Metadata.FindProperty("Version");
                    if (version is not null) entry.Property(version.Name).CurrentValue = stored.GetValue<int>(version.Name) + 1;
                }
            }
        }
    }

    private async Task RecordFailureAsync(Guid jobId, int attempts)
    {
        try
        {
            db.ChangeTracker.Clear();
            var job = await db.ResearchJobs.FirstOrDefaultAsync(j => j.Id == jobId);
            if (job is not { State: ResearchJobState.Running } || job.Attempts != attempts) return;
            job.Finish(ResearchJobState.Failed, $"Research stopped on an unexpected error. It was logged with reference {jobId}.", Now());
            AddEvent(job, ResearchStage.Complete, EventLevel.Error, $"Stopped on an unexpected error (reference {jobId}). Results saved before it are kept.");
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // The lease will expire and another attempt will run; nothing more can be done here.
            logger.LogError(ex, "Could not record the failure of research job {JobId}", jobId);
        }
    }

    private void AddEvent(ResearchJob job, ResearchStage stage, EventLevel level, string message) =>
        db.ResearchEvents.Add(new ResearchEvent(job.Id, NextEventTime(), stage, level, message));

    // Strictly increasing (by at least 1 µs, Postgres' precision) so "newest first" keeps the order events happened in.
    private DateTime NextEventTime()
    {
        var now = Now();
        if (now <= _lastEventAt) now = _lastEventAt.AddTicks(10);
        _lastEventAt = now;
        return now;
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private static string Summary(Campaign campaign, CampaignCriteria c, int sources)
    {
        var parts = new List<string>();
        void Add(int count, string what) { if (count > 0) parts.Add($"{count} {what}"); }
        if (campaign.Mode == OpportunityMode.Job)
        {
            Add(c.RequiredSkills.Count, "required skill" + Plural(c.RequiredSkills.Count));
            Add(c.PreferredSkills.Count, "preferred skill" + Plural(c.PreferredSkills.Count));
            if (c.CandidateYears is { } y) parts.Add($"{y} years' experience");
            Add(c.WorkModes.Count, "work mode" + Plural(c.WorkModes.Count));
        }
        else
        {
            Add(c.Industries.Count, "industr" + (c.Industries.Count == 1 ? "y" : "ies"));
            Add(c.Problems.Count, "problem keyword" + Plural(c.Problems.Count));
            Add(c.Signals.Count, "signal" + Plural(c.Signals.Count));
        }
        Add(c.Locations.Count, "location" + Plural(c.Locations.Count));
        Add(c.ExcludeKeywords.Count + c.ExcludeOrganizations.Count, "exclusion" + Plural(c.ExcludeKeywords.Count + c.ExcludeOrganizations.Count));
        var criteria = parts.Count == 0 ? "no criteria configured" : string.Join(", ", parts);
        return $"{campaign.Mode} campaign \"{campaign.Name}\": {criteria}; {sources} source{Plural(sources)}.";
    }

    private static string Plural(int n) => n == 1 ? "" : "s";

    private static bool IsHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
