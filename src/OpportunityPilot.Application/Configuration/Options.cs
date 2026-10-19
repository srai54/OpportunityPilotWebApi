namespace OpportunityPilot.Application.Configuration;

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>SqlServer (local LocalDB development) or Postgres (Supabase cloud and integration tests).</summary>
    public string Provider { get; set; } = "SqlServer";
}

public sealed class FeatureOptions
{
    public const string Section = "Features";
    public bool GeminiEnabled { get; set; }
    public bool MongoArchiveEnabled { get; set; }
    public bool GmailEnabled { get; set; }
}

public sealed class AiOptions
{
    public const string Section = "Ai";
    public string Mode { get; set; } = "Rules";
    public string? GeminiApiKey { get; set; }
    public string GeminiModel { get; set; } = "gemini-3.8-flash";
    /// <summary>Groq (OpenAI-compatible, free tier). When set it is used instead of Gemini. Render: <c>Groq__ApiKey</c> or <c>Ai__GroqApiKey</c>.</summary>
    public string? GroqApiKey { get; set; }
    public string GroqModel { get; set; } = "openai/gpt-oss-120b";
    public bool UsesGroq => !string.IsNullOrWhiteSpace(GroqApiKey);
    public bool HasKey => UsesGroq || !string.IsNullOrWhiteSpace(GeminiApiKey);
    public string ProviderName => UsesGroq ? "Groq" : "Gemini";
    public string ActiveModel => UsesGroq ? GroqModel : GeminiModel;
    public bool AllowPaidUsage { get; set; }
    public int MaxCallsPerRun { get; set; } = 12;
    public int MaxOutputTokens { get; set; } = 1500;
    public int DailyCallLimit { get; set; } = 200;
    public int TimeoutSeconds { get; set; } = 20;
}

/// <summary>
/// Adzuna job search (free developer keys from developer.adzuna.com). Both values are server-side secrets: they are
/// never logged, returned, or written into research events (the request URL that carries them is never recorded).
/// Without both, Adzuna sources fail safely and the capability reports NotConfigured.
/// </summary>
public sealed class AdzunaOptions
{
    public const string Section = "Adzuna";
    public string? AppId { get; set; }
    public string? AppKey { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppKey);
}

/// <summary>
/// JSearch (OpenWeb Ninja on RapidAPI): a licensed Google-for-Jobs aggregator used to show live Indeed postings, because
/// Indeed has no public API (its RSS returns 404 and its search pages block bots). The key is a server-side secret:
/// never logged, returned or stored. Without it the Indeed tab keeps the official-search handoff.
/// </summary>
public sealed class JsearchOptions
{
    public const string Section = "Jsearch";
    public string? Key { get; set; }
    public string Host { get; set; } = "jsearch.p.rapidapi.com";

    /// <summary>Search path on the host. JSearch moves it between API versions, so it is configuration (Jsearch__SearchPath).</summary>
    public string SearchPath { get; set; } = "/search-v2";

    /// <summary>The free plan allows 200 requests a month, so identical searches are served from memory for this long.</summary>
    public int CacheMinutes { get; set; } = 360;

    public bool Configured => !string.IsNullOrWhiteSpace(Key);
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Supabase project URL, e.g. https://abc.supabase.co. Issuer is {SupabaseUrl}/auth/v1.</summary>
    public string? SupabaseUrl { get; set; }

    /// <summary>Supabase access tokens use "authenticated".</summary>
    public string Audience { get; set; } = "authenticated";

    /// <summary>Only for legacy Supabase projects still signing with the shared HS256 secret. Prefer JWKS.</summary>
    public string? LegacyJwtSecret { get; set; }

    /// <summary>
    /// Hanko API URL from the Hanko console, e.g. https://abc123.hanko.io (https only). When set, Hanko session tokens
    /// are accepted next to Supabase ones; keys come from {HankoApiUrl}/.well-known/jwks.json.
    /// </summary>
    public string? HankoApiUrl { get; set; }

    /// <summary>
    /// Optional, recommended: the Hanko token audience, comma-separated. Hanko sets "aud" to the passkey relying-party id,
    /// which is the web app's domain (e.g. opportunitypilot.vercel.app). Unset: the audience is not checked.
    /// </summary>
    public string? HankoAudience { get; set; }

    /// <summary>
    /// Optional: the exact "iss" Hanko tokens carry, only if the Hanko project sets a custom issuer. Unset: a token may
    /// carry no issuer or the Hanko API URL; any other issuer is rejected.
    /// </summary>
    public string? HankoIssuer { get; set; }

    /// <summary>
    /// Keep "Continue as guest" next to real accounts once SupabaseUrl or HankoApiUrl is set (default true). Guests always get their
    /// own isolated identity; set false to require an account.
    /// </summary>
    public bool AllowGuests { get; set; } = true;

    /// <summary>Local development only. Startup fails if this is true outside the Development environment.</summary>
    public bool DevBypass { get; set; }
}
