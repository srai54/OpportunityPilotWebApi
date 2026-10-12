namespace OpportunityPilot.Domain.Opportunities;

/// <summary>
/// Where a job posting came from. LinkedIn, Naukri and InstaHyre are handled by the local desktop agent; postings from the
/// public job-board sources (Greenhouse, Lever, Adzuna) are applied to by the user through their apply URL.
/// </summary>
public enum JobPlatform
{
    LinkedIn,
    Naukri,
    Instahyre,
    Other,
    Greenhouse,
    Lever,
    Adzuna,
    Ashby,
    SmartRecruiters,
    Recruitee,
    Workable,
    Remotive,
    RemoteOk,
    Indeed,
    Seek,
    Workday,
    Arbeitnow,
    Jobicy,
    WeWorkRemotely
}
