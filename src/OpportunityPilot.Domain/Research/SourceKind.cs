namespace OpportunityPilot.Domain.Research;

public enum SourceKind
{
    Paste,
    Csv,
    Url,
    Feed,
    Agent,

    /// <summary>A company's public Greenhouse job board; <see cref="Source.Url"/> holds the board token.</summary>
    Greenhouse,

    /// <summary>A company's public Lever postings; <see cref="Source.Url"/> holds the company slug.</summary>
    Lever,

    /// <summary>Adzuna job search (server keys) driven by the campaign's keywords and first location.</summary>
    Adzuna,

    /// <summary>A company's public Ashby job board; <see cref="Source.Url"/> holds the organization slug.</summary>
    Ashby,

    /// <summary>A company's public SmartRecruiters postings; <see cref="Source.Url"/> holds the company identifier.</summary>
    SmartRecruiters,

    /// <summary>A company's public Recruitee offers; <see cref="Source.Url"/> holds the account slug.</summary>
    Recruitee,

    /// <summary>A company's public Workable widget; <see cref="Source.Url"/> holds the account slug.</summary>
    Workable,

    /// <summary>
    /// Live postings from one job board (Indeed, LinkedIn or SEEK) found through JSearch (server key) with the campaign's
    /// keywords and first location; <see cref="Source.Url"/> holds the board name. Job campaigns get jobs; other modes get
    /// the hiring companies as leads.
    /// </summary>
    JobSearch,

    /// <summary>A company's public Workday careers site; <see cref="Source.Url"/> holds <c>tenant.wdN/site</c>.</summary>
    Workday,

    /// <summary>The public, board-wide Remotive remote-jobs feed.</summary>
    Remotive,

    /// <summary>The public, board-wide Remote OK jobs feed.</summary>
    RemoteOk,

    /// <summary>The free public Arbeitnow job-board API (Germany, Europe and remote).</summary>
    Arbeitnow,

    /// <summary>The free public Jobicy remote-jobs API.</summary>
    Jobicy,

    /// <summary>We Work Remotely's public RSS feed of remote jobs.</summary>
    WeWorkRemotely
}
