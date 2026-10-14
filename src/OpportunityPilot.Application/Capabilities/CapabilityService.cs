using Microsoft.Extensions.Options;
using OpportunityPilot.Application.Configuration;

namespace OpportunityPilot.Application.Capabilities;

/// <summary>
/// Plain status labels. "Ready" is reserved for something this build can actually do;
/// "NotBuilt" marks a planned capability whose milestone has not been implemented yet;
/// "LocalAgent" means the work happens in the user's own browser via the desktop agent, not on this server.
/// </summary>
public enum CapabilityStatus
{
    Ready,
    Configured,
    NotConfigured,
    Disabled,
    ManualHandoff,
    NotBuilt,
    LocalAgent
}

public sealed record CapabilityDto(string Key, string Name, string Category, CapabilityStatus Status, string Detail, string[] Can, string[] Cannot);

/// <param name="SetupRequired">Plain-language list of missing configuration; empty when the deployment is complete.</param>
/// <param name="GuestSignIn">"Continue as guest" is offered (demo mode, or next to accounts when allowed).</param>
/// <param name="TemporaryStorage">Demo mode: no database, so data is kept in memory until the server restarts.</param>
/// <param name="SignInProviders">Account sign-in systems whose tokens are accepted: "hanko" and/or "supabase"; empty in demo mode.</param>
public sealed record CapabilitiesDto(
    string Environment, string DatabaseProvider, string AiMode,
    IReadOnlyList<string> SetupRequired, bool GuestSignIn, bool TemporaryStorage,
    IReadOnlyList<CapabilityDto> Items, IReadOnlyList<string> SignInProviders);

public sealed class CapabilityService(
    IOptions<FeatureOptions> features,
    IOptions<AiOptions> ai,
    IOptions<DatabaseOptions> database,
    IOptions<AdzunaOptions> adzuna,
    IOptions<JsearchOptions> jsearch,
    SetupState setup,
    IOptions<JobBoards.RapidApiBoardsOptions>? rapid = null)
{
    public CapabilitiesDto Get(string environment)
    {
        var f = features.Value;
        var a = ai.Value;
        var geminiKeyPresent = f.GeminiEnabled && !string.IsNullOrWhiteSpace(a.GeminiApiKey);

        var items = new List<CapabilityDto>
        {
            setup.DatabaseConfigured
                ? new("database", "Database", "Core", CapabilityStatus.Ready,
                    $"{database.Value.Provider} is the active relational store.",
                    ["Store profiles owned by your account"], [])
                : new("database", "Database", "Core", CapabilityStatus.NotConfigured,
                    "Demo mode: no database connection string (ConnectionStrings__Main), so data is kept in memory and resets when the server restarts.",
                    ["Store profiles until the server restarts"], ["Keep data across restarts"]),

            setup.AuthConfigured
                ? new("auth", "Sign-in", "Core", CapabilityStatus.Ready,
                    "Requests are accepted only with a valid signed token." + SignInDetail(setup.SignInProviders),
                    ["Keep each account's data separate"], [])
                : new("auth", "Sign-in", "Core", CapabilityStatus.NotConfigured,
                    "Demo mode: no Supabase project URL (Auth__SupabaseUrl), so each browser continues as a random guest. Guests cannot see each other's data.",
                    ["Continue as a guest"], ["Create accounts", "Sign in from another device"]),

            new("gemini", "Gemini API", "AI",
                geminiKeyPresent ? CapabilityStatus.Configured : CapabilityStatus.NotConfigured,
                geminiKeyPresent
                    ? $"Server key present for {a.GeminiModel}. Goal parsing uses Gemini with validated rules fallback; live provider verification is still required."
                    : "No server-side key. Rules/templates mode applies. The key is set in server configuration and is never returned to the browser.",
                geminiKeyPresent ? ["Parse campaign goals with rules fallback"] : [], ["Extract facts with AI", "Summarise matches with AI"]),

            new("rules", "Rules and templates", "AI", CapabilityStatus.Ready,
                "Deterministic extraction and transparent scoring for Job and Customer campaigns.",
                ["Extract skills, experience range, location and work mode", "Score every campaign mode with a per-criterion breakdown, coverage and gaps", "Fill reviewed outreach templates"],
                ["Understand needs beyond configured terms"]),

            new("csv-import", "CSV and pasted text", "Sources", CapabilityStatus.Ready,
                "Import with preview and row-level errors.",
                ["Import job postings or companies from CSV (1 MB, 1000 rows)", "Paste postings or company notes"],
                ["Read Excel files or PDFs"]),

            new("public-urls", "Public company URLs", "Sources", CapabilityStatus.Ready,
                "Safe fetch with private-address blocking; pages that need a login or JavaScript are marked for manual input.",
                ["Read public pages you supply"], ["Read pages behind a login", "Run JavaScript", "Reach private or internal addresses"]),

            new("feeds", "Permitted RSS/Atom feeds", "Sources", CapabilityStatus.Ready,
                "Discovers only what a permitted RSS/Atom feed contains.",
                ["Read RSS 2.0 and Atom feeds you supply (up to 100 entries)"], ["Search the web"]),

            new("greenhouse", "Greenhouse job boards", "Sources", CapabilityStatus.Ready,
                "Public job boards of companies that use Greenhouse; no key needed.",
                ["Read a company's open jobs from its public Greenhouse board (board token or URL)",
                    "Pre-filter titles by your keywords and required skills before reading descriptions"],
                ["Search across companies", "Apply for you — open the application page yourself"]),

            new("lever", "Lever job postings", "Sources", CapabilityStatus.Ready,
                "Public job postings of companies that use Lever; no key needed.",
                ["Read up to 100 open postings from a company's public Lever page (slug or URL)"],
                ["Search across companies", "Apply for you — open the application page yourself"]),

            adzuna.Value.Configured
                ? new("adzuna", "Adzuna job search", "Sources", CapabilityStatus.Ready,
                    "Searches Adzuna in the country of your place (India when none is given) with your campaign keywords, and in Find → All sites; keys are set on the server and never shown.",
                    ["Search up to 3 keywords in your first location, jobs from the last 14 days", "Search Adzuna together with the free sites in Find → All sites"],
                    ["Read full job descriptions (Adzuna returns a snippet)", "Apply for you — open the application page yourself"])
                : new("adzuna", "Adzuna job search", "Sources", CapabilityStatus.NotConfigured,
                    "Server keys missing (Adzuna__AppId and Adzuna__AppKey). Adzuna sources can be added but fail until the keys are set.",
                    [], ["Search Adzuna"]),

            jsearch.Value.Configured
                ? new("job-boards", "Indeed, LinkedIn and SEEK postings", "Sources", CapabilityStatus.Ready,
                    "Shows current postings published on Indeed, LinkedIn or SEEK through JSearch, a licensed Google-for-Jobs data service. The key is set on the server and never shown.",
                    ["Search one board with your filters (Job discovery)", "Add Indeed as a source in a Job campaign: postings are scored and go to Approvals", "Link every posting to the board itself", "Reuse a search for a few hours to save the free quota"],
                    ["Apply for you", "Read your Indeed profile or LinkedIn feed"])
                : new("job-boards", "Indeed, LinkedIn and SEEK postings", "Sources", CapabilityStatus.NotConfigured,
                    "Server key missing (Jsearch__Key, free RapidAPI plan). Until it is set, Job discovery opens the same search on each board instead.",
                    ["Open the search on Indeed, LinkedIn or SEEK"], ["Show live postings in the app"]),

            PublicBoard("ashby", "Ashby job boards", "a company's public Ashby careers board"),
            PublicBoard("smartrecruiters", "SmartRecruiters postings", "a company's public SmartRecruiters postings"),
            PublicBoard("recruitee", "Recruitee offers", "a company's public Recruitee offers"),
            PublicBoard("workable", "Workable jobs", "a company's public Workable careers widget"),
            PublicBoard("workday", "Workday careers sites", "a company's public Workday careers site (myworkdayjobs.com), searched with your keywords"),
            PublicBoard("remotive", "Remotive remote jobs", "the public Remotive remote-jobs feed", company: false),
            PublicBoard("remoteok", "Remote OK jobs", "the public Remote OK jobs feed", company: false),
            rapid?.Value.Configured == true
                ? new("regional-boards", "Regional job boards (RapidAPI)", "Sources", CapabilityStatus.Ready,
                    $"Searched together in Find → All sites: {string.Join(", ", rapid.Value.Active.Select(b => b.Value.Name))}. The key is set on the server and never shown.",
                    ["Search these boards with every other site", "Show each job linked to its own page"], ["Apply for you", "Read pages behind a login"])
                : new("regional-boards", "Regional job boards (RapidAPI)", "Sources", CapabilityStatus.NotConfigured,
                    "Not set up. Subscribe to a regional board's RapidAPI listing, then set RapidApi__Key and that board's host, path and field names on the server.",
                    [], ["Search Gulf, Southeast Asia or DACH boards"]),
            PublicBoard("arbeitnow", "Arbeitnow jobs", "the free public Arbeitnow job board (Germany, Europe and remote)", company: false),
            PublicBoard("jobicy", "Jobicy remote jobs", "the free public Jobicy remote-jobs feed", company: false),
            PublicBoard("weworkremotely", "We Work Remotely jobs", "We Work Remotely's public RSS feed of remote jobs", company: false),
            PublicBoard("themuse", "The Muse jobs", "the free public The Muse jobs API (its newest postings)", company: false),

            new("upwork", "Upwork projects", "Sources", CapabilityStatus.NotBuilt,
                "Upwork's official API can search marketplace projects for the Sales workspace, but each app needs Upwork's approval and your Upwork account connected (OAuth). Upwork retired its job RSS feeds in 2024.",
                [], ["Search Upwork projects", "Send proposals or spend Connects"]),

            new("gmail", "Gmail", "Outreach",
                f.GmailEnabled ? CapabilityStatus.NotBuilt : CapabilityStatus.Disabled,
                "OAuth drafts and reviewed sending. Milestone M6. Nothing is sent from this build.",
                [], ["Create drafts", "Send approved messages"]),

            new("linkedin", "LinkedIn", "Platforms", CapabilityStatus.LocalAgent,
                "Applies through your own logged-in browser on your computer with the OpportunityPilot agent. Unofficial automation: LinkedIn may restrict accounts that use it.",
                ["Search jobs with your filters", "Fill and submit Easy Apply forms from your saved answers", "Record each application here",
                    "Map the people around a job (recruiters, hiring managers, your connections) when you ask",
                    "Send your approved connection notes and messages (a dry run unless you allow sending)"],
                ["Answer questions your saved answers do not cover", "Get past security checks — the agent stops and asks you",
                    "Send anything you have not approved, or more than your daily cap"]),

            new("naukri", "Naukri", "Platforms", CapabilityStatus.LocalAgent,
                "Applies through your own logged-in browser on your computer with the OpportunityPilot agent. Unofficial automation: Naukri may restrict accounts that use it.",
                ["Search jobs with your filters", "Apply and answer the recruiter questionnaire from your saved answers", "Record each application here"],
                ["Apply on company websites", "Answer questions your saved answers do not cover"]),

            new("instahyre", "InstaHyre", "Platforms", CapabilityStatus.LocalAgent,
                "Uses your own logged-in browser. The adapter stops before unknown forms or security checks and requires live selector verification.",
                ["Collect personalised opportunities", "Dry-run or apply only to your shortlist"], ["Bypass security checks", "Invent form answers"]),

            new("mongo-archive", "MongoDB research archive", "Optional",
                f.MongoArchiveEnabled ? CapabilityStatus.NotBuilt : CapabilityStatus.Disabled,
                "Optional archive of bounded research metadata. Milestone M8.", [], ["Archive raw research"]),

            new("scheduler", "Scheduled research", "Optional", CapabilityStatus.Ready,
                "Durable owner schedules queue research through a leased worker while the API is awake. Render free-tier sleep can delay, but never duplicate, a due run.",
                ["Set campaign cadence, time zone, next run, pause and resume"], ["Guarantee an exact wall-clock start while the hosting service is asleep"])
            ,new("notifications", "Follow-up notifications", "Optional", CapabilityStatus.ManualHandoff,
                "Due follow-ups are shown inside the app; no email or push delivery is claimed.",
                ["Show due and overdue next actions"], ["Deliver reminders outside the app"])
        };

        return new CapabilitiesDto(
            environment,
            setup.DatabaseConfigured ? database.Value.Provider : "In-memory (temporary)",
            geminiKeyPresent ? "Gemini (key present)" : "Rules",
            setup.Missing, setup.GuestsEnabled, !setup.DatabaseConfigured, items, setup.SignInProviders);

        static string SignInDetail(IReadOnlyList<string> providers) =>
            (providers.Contains("hanko"), providers.Contains("supabase")) switch
            {
                (true, true) => " Accounts: Hanko (passkeys, passwords, email codes, two-factor) and the older Supabase sign-in.",
                (true, false) => " Accounts: Hanko (passkeys, passwords, email codes, two-factor).",
                (false, true) => " Accounts: Supabase.",
                _ => ""
            };

        static CapabilityDto PublicBoard(string key, string name, string detail, bool company = true) =>
            new(key, name, "Sources", CapabilityStatus.Ready,
                $"Reads {detail}; no key or login is needed.",
                [company ? "Read open jobs from one company per source" : "Read the board-wide public feed"],
                ["Apply for you — open the application page yourself"]);
    }
}
