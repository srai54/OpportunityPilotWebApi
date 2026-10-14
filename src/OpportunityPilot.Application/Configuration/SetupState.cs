namespace OpportunityPilot.Application.Configuration;

/// <summary>
/// What is missing from this deployment's configuration. The API starts in a bounded demo mode where possible
/// (in-memory storage and/or guest access) and reports every gap through /api/v1/capabilities.
/// </summary>
public sealed class SetupState
{
    private readonly List<string> _missing = [];

    public bool DatabaseConfigured { get; private set; } = true;
    public bool AuthConfigured { get; private set; } = true;

    /// <summary>Guests can sign in: always in demo mode, and next to real accounts unless Auth:AllowGuests is false.</summary>
    public bool GuestsEnabled { get; set; }

    /// <summary>Account sign-in systems whose tokens this API accepts: "hanko" and/or "supabase" (guests are separate).</summary>
    public IReadOnlyList<string> SignInProviders { get; set; } = [];
    public IReadOnlyList<string> Missing => _missing;

    /// <summary>A setting is present but unusable while another one still keeps sign-in working.</summary>
    public void ConfigProblem(string reason) => _missing.Add(reason);

    public void DatabaseMissing(string reason)
    {
        DatabaseConfigured = false;
        _missing.Add(reason);
    }

    public void AuthMissing(string reason)
    {
        AuthConfigured = false;
        _missing.Add(reason);
    }
}
