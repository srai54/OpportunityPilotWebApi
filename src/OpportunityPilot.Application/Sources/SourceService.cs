using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.JobBoards;
using OpportunityPilot.Application.Research;
using OpportunityPilot.Application.Research.Boards;
using OpportunityPilot.Domain.Common;
using OpportunityPilot.Domain.Research;

namespace OpportunityPilot.Application.Sources;

public sealed class SourceService(IAppDbContext db, ICurrentUser user, TimeProvider clock, IWebFetcher fetcher)
{
    public const int MaxSourcesPerCampaign = 20;

    public async Task<IReadOnlyList<SourceDto>> ListAsync(Guid campaignId, CancellationToken ct)
    {
        await EnsureCampaignAsync(campaignId, ct);
        var sources = await db.Sources
            .Where(s => s.OwnerId == user.OwnerId && s.CampaignId == campaignId)
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .ToListAsync(ct);
        return sources.Select(ToDto).ToList();
    }

    public async Task<SourceDto> CreateAsync(Guid campaignId, CreateSourceRequest request, CancellationToken ct)
    {
        var mode = await EnsureCampaignAsync(campaignId, ct);
        if (request is null) throw new RequestValidationException(new Dictionary<string, string[]> { ["body"] = ["Request body is required."] });

        var errors = new Dictionary<string, string[]>();
        string? url = null, text = null;
        switch (request.Kind)
        {
            case SourceKind.Paste:
                text = request.Text?.Trim();
                if (string.IsNullOrEmpty(text)) errors["text"] = ["Paste at least one posting or company."];
                else if (text.Length > Source.MaxTextLength) errors["text"] = [$"Pasted text must be at most {Source.MaxTextLength.ToString("N0", CultureInfo.InvariantCulture)} characters."];
                else if (PasteParser.Parse(text).Count == 0) errors["text"] = ["No postings or companies were found in the pasted text."];
                break;
            case SourceKind.Url or SourceKind.Feed:
                url = request.Url?.Trim();
                if (string.IsNullOrEmpty(url)) errors["url"] = ["URL is required."];
                else if (url.Length > Source.MaxUrlLength) errors["url"] = [$"URL must be at most {Source.MaxUrlLength} characters."];
                else if (fetcher.CheckUrl(url) is { } reason) errors["url"] = [reason];
                break;
            case SourceKind.Greenhouse or SourceKind.Lever or SourceKind.Adzuna or SourceKind.Ashby or SourceKind.SmartRecruiters or
                SourceKind.Recruitee or SourceKind.Workable or SourceKind.Workday or SourceKind.Remotive or SourceKind.RemoteOk or SourceKind.Arbeitnow or SourceKind.Jobicy or
                SourceKind.WeWorkRemotely or SourceKind.TheMuse when mode != OpportunityMode.Job:
                errors["kind"] = [$"{request.Kind} sources list jobs, so they can only be added to Job campaigns."];
                break;
            case SourceKind.Greenhouse:
                // Only the format is checked; the board is not contacted until research runs.
                url = BoardIdentifiers.Greenhouse(request.Url);
                if (url is null)
                    errors["url"] = [string.IsNullOrWhiteSpace(request.Url)
                        ? "Enter the company's Greenhouse board token (e.g. stripe) or its board URL."
                        : "Enter a Greenhouse board token (letters, digits and hyphens, up to 100) or a boards.greenhouse.io / job-boards.greenhouse.io URL."];
                break;
            case SourceKind.Lever:
                url = BoardIdentifiers.Lever(request.Url);
                if (url is null)
                    errors["url"] = [string.IsNullOrWhiteSpace(request.Url)
                        ? "Enter the company's Lever slug (e.g. leverdemo) or its jobs.lever.co URL."
                        : "Enter a Lever company slug (letters, digits and hyphens, up to 100) or a jobs.lever.co URL."];
                break;
            case SourceKind.Adzuna:
                // Searches with the campaign's keywords and first location; nothing to store.
                break;
            case SourceKind.Ashby:
                Board(SourceKind.Ashby, BoardIdentifiers.Ashby(request.Url), "Ashby organization slug (e.g. ashby) or jobs.ashbyhq.com URL");
                break;
            case SourceKind.SmartRecruiters:
                Board(SourceKind.SmartRecruiters, BoardIdentifiers.SmartRecruiters(request.Url), "SmartRecruiters company identifier or careers URL");
                break;
            case SourceKind.Recruitee:
                Board(SourceKind.Recruitee, BoardIdentifiers.Recruitee(request.Url), "Recruitee account slug or careers URL");
                break;
            case SourceKind.Workable:
                Board(SourceKind.Workable, BoardIdentifiers.Workable(request.Url), "Workable account slug or apply.workable.com URL");
                break;
            case SourceKind.Workday:
                // Only the format is checked; the careers site is not contacted until research runs.
                url = WorkdayBoard.Parse(request.Url)?.ToString();
                if (url is null)
                    errors["url"] = [string.IsNullOrWhiteSpace(request.Url)
                        ? "Enter the company's Workday careers URL, e.g. https://nvidia.wd5.myworkdayjobs.com/NVIDIAExternalCareerSite."
                        : "Enter a public careers URL on myworkdayjobs.com (https://<company>.wd<N>.myworkdayjobs.com/<site>); sign-in and account pages are not accepted."];
                break;
            case SourceKind.Remotive or SourceKind.RemoteOk or SourceKind.Arbeitnow or SourceKind.Jobicy or SourceKind.WeWorkRemotely or SourceKind.TheMuse:
                break;
            case SourceKind.JobSearch:
                // The board is stored; the search itself uses the campaign's keywords and first location at run time.
                if (string.IsNullOrWhiteSpace(request.Url)) url = nameof(JobBoard.Indeed);
                else if (Enum.TryParse<JobBoard>(request.Url.Trim(), ignoreCase: true, out var board) && Enum.IsDefined(board)) url = board.ToString();
                else errors["url"] = ["Choose the board to search: Indeed, LinkedIn or Seek."];
                break;
            case SourceKind.Csv:
                errors["kind"] = ["CSV sources are created by committing an import preview (POST /api/v1/imports/preview)."];
                break;
            case SourceKind.Agent:
                errors["kind"] = ["Agent sources are created by the desktop agent."];
                break;
            default:
                errors["kind"] = ["Unknown source kind."];
                break;
        }

        void Board(SourceKind kind, string? value, string help)
        {
            url = value;
            if (url is null) errors["url"] = [$"Enter a valid {help}."];
        }

        var label = request.Label?.Trim();
        if (label is { Length: > Source.MaxLabelLength }) errors["label"] = [$"Label must be at most {Source.MaxLabelLength} characters."];
        var note = request.PermissionNote?.Trim();
        if (note is { Length: > Source.MaxPermissionNoteLength })
            errors["permissionNote"] = [$"Permission note must be at most {Source.MaxPermissionNoteLength} characters."];
        if (errors.Count > 0) throw new RequestValidationException(errors);

        if (await db.Sources.CountAsync(s => s.OwnerId == user.OwnerId && s.CampaignId == campaignId, ct) >= MaxSourcesPerCampaign)
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["kind"] = [$"A campaign can have at most {MaxSourcesPerCampaign} sources. Delete one you no longer use first."]
            });

        var source = new Source(user.OwnerId, campaignId, request.Kind, string.IsNullOrEmpty(label) ? DefaultLabel(request.Kind, url) : label,
            url, text, note, platform: null, clock.GetUtcNow().UtcDateTime);
        if (request.Kind == SourceKind.Paste) source.SetItemCount(PasteParser.Parse(text).Count);
        db.Sources.Add(source);
        await db.SaveChangesAsync(ct);
        return ToDto(source);
    }

    /// <summary>Removes the source and its rows. Evidence already gathered from it stays, so existing scores remain traceable.</summary>
    public async Task DeleteAsync(Guid campaignId, Guid sourceId, CancellationToken ct)
    {
        await EnsureCampaignAsync(campaignId, ct);
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == sourceId && s.CampaignId == campaignId && s.OwnerId == user.OwnerId, ct)
            ?? throw new NotFoundException("Source not found.");
        // Explicit, because InMemory (demo mode) does not run database cascades.
        db.SourceItems.RemoveRange(await db.SourceItems.Where(i => i.SourceId == source.Id).ToListAsync(ct));
        db.Sources.Remove(source);
        await db.SaveChangesAsync(ct);
    }

    public static SourceDto ToDto(Source s) => new(
        s.Id, s.CampaignId, s.Kind, s.Label, s.Url, s.Platform, s.PermissionNote, s.Status, s.LastFetchedAt, s.SafeError,
        s.ItemCount, s.Text?.Length ?? 0, s.CreatedAt);

    private async Task<OpportunityMode> EnsureCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var modes = await db.Campaigns.Where(c => c.Id == campaignId && c.OwnerId == user.OwnerId).Select(c => c.Mode).ToListAsync(ct);
        return modes.Count == 1 ? modes[0] : throw new NotFoundException("Campaign not found.");
    }

    private static string DefaultLabel(SourceKind kind, string? url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
        return kind switch
        {
            SourceKind.Paste => "Pasted text",
            SourceKind.Url => host ?? "Web page",
            SourceKind.Feed => host is null ? "Feed" : $"{host} feed",
            SourceKind.Greenhouse => $"Greenhouse board {url}",
            SourceKind.Lever => $"Lever company {url}",
            SourceKind.Adzuna => "Adzuna search",
            SourceKind.Ashby => $"Ashby board {url}",
            SourceKind.SmartRecruiters => $"SmartRecruiters company {url}",
            SourceKind.Recruitee => $"Recruitee company {url}",
            SourceKind.Workable => $"Workable company {url}",
            SourceKind.JobSearch => $"{(url == nameof(JobBoard.Seek) ? "SEEK" : url ?? "Indeed")} search",
            SourceKind.Workday => $"Workday careers {url}",
            SourceKind.Remotive => "Remotive remote jobs",
            SourceKind.RemoteOk => "Remote OK jobs",
            SourceKind.Arbeitnow => "Arbeitnow jobs (Germany, Europe, remote)",
            SourceKind.Jobicy => "Jobicy remote jobs",
            SourceKind.WeWorkRemotely => "We Work Remotely jobs",
            SourceKind.TheMuse => "The Muse jobs",
            _ => kind.ToString()
        };
    }
}
