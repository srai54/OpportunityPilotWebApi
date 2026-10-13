namespace OpportunityPilot.Application.Research;

/// <summary>
/// Research limits from configuration (Research:*). Values may be lowered but never raised above the server
/// ceilings, so a misconfiguration cannot turn the processor into an unbounded crawler.
/// </summary>
public sealed class ResearchOptions
{
    public const string Section = "Research";

    public const int CeilingCandidates = 100;
    public const int CeilingFetches = 50;
    public const int CeilingTimeoutSeconds = 30;
    public const int CeilingBytes = 1024 * 1024;
    public const int CeilingConcurrency = 4;

    /// <summary>Hosted processor on/off. Tests turn it off and drive <see cref="IResearchRunner"/> directly.</summary>
    public bool ProcessorEnabled { get; set; } = true;

    public int PollSeconds { get; set; } = 2;
    public int MaxCandidates { get; set; } = CeilingCandidates;
    public int MaxFetches { get; set; } = CeilingFetches;
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxBytes { get; set; } = CeilingBytes;
    public int Concurrency { get; set; } = 2;

    /// <summary>
    /// Job-board API roots. They exist so integration tests can point research at a loopback server; that server is
    /// reachable only because the test project swaps in its own fetch address policy. In any real deployment the
    /// strict policy and the https-only rule of the safe fetcher still apply to whatever is configured here.
    /// </summary>
    public string GreenhouseApiBase { get; set; } = DefaultGreenhouseApiBase;
    public string LeverApiBase { get; set; } = DefaultLeverApiBase;
    public string AdzunaApiBase { get; set; } = DefaultAdzunaApiBase;
    public string AshbyApiBase { get; set; } = DefaultAshbyApiBase;
    public string SmartRecruitersApiBase { get; set; } = DefaultSmartRecruitersApiBase;
    public string RecruiteeHostSuffix { get; set; } = DefaultRecruiteeHostSuffix;
    public string WorkableApiBase { get; set; } = DefaultWorkableApiBase;
    public string RemotiveApiBase { get; set; } = DefaultRemotiveApiBase;
    /// <summary>Empty in real use (each source names its own <c>*.myworkdayjobs.com</c> host); tests point it at a loopback server.</summary>
    public string WorkdayApiBase { get; set; } = "";
    public string RemoteOkApiBase { get; set; } = DefaultRemoteOkApiBase;
    public string ArbeitnowApiBase { get; set; } = DefaultArbeitnowApiBase;
    public string JobicyApiBase { get; set; } = DefaultJobicyApiBase;
    public string HimalayasApiBase { get; set; } = DefaultHimalayasApiBase;
    public string WeWorkRemotelyBase { get; set; } = DefaultWeWorkRemotelyBase;

    public const string DefaultGreenhouseApiBase = "https://boards-api.greenhouse.io";
    public const string DefaultLeverApiBase = "https://api.lever.co";
    public const string DefaultAdzunaApiBase = "https://api.adzuna.com";
    public const string DefaultAshbyApiBase = "https://api.ashbyhq.com";
    public const string DefaultSmartRecruitersApiBase = "https://api.smartrecruiters.com";
    public const string DefaultRecruiteeHostSuffix = "recruitee.com";
    public const string DefaultWorkableApiBase = "https://apply.workable.com";
    public const string DefaultRemotiveApiBase = "https://remotive.com";
    public const string DefaultRemoteOkApiBase = "https://remoteok.com";
    public const string DefaultArbeitnowApiBase = "https://www.arbeitnow.com";
    public const string DefaultJobicyApiBase = "https://jobicy.com";
    public const string DefaultHimalayasApiBase = "https://himalayas.app";
    public const string DefaultWeWorkRemotelyBase = "https://weworkremotely.com";

    /// <summary>Some public feeds (Arbeitnow, about 3 MB a page) are bigger than the usual 1 MB read limit; those few get this larger ceiling.</summary>
    public const int LargeFeedBytes = 5 * 1024 * 1024;

    public int EffectiveCandidates => Math.Clamp(MaxCandidates, 1, CeilingCandidates);
    public int EffectiveFetches => Math.Clamp(MaxFetches, 1, CeilingFetches);
    public TimeSpan EffectiveTimeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, CeilingTimeoutSeconds));
    public int EffectiveBytes => Math.Clamp(MaxBytes, 1024, CeilingBytes);
    public int EffectiveConcurrency => Math.Clamp(Concurrency, 1, CeilingConcurrency);
    public TimeSpan EffectivePoll => TimeSpan.FromSeconds(Math.Clamp(PollSeconds, 1, 60));
}
