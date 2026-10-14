namespace OpportunityPilot.Domain.Circle;

/// <summary>Where a circle person came from. Stored as a string.</summary>
public enum CircleSource
{
    /// <summary>Read by the desktop agent from LinkedIn people search in the person's own logged-in browser.</summary>
    LinkedInAgent,
    Hunter,
    Apollo,
    /// <summary>Added by the person by hand.</summary>
    Manual
}
