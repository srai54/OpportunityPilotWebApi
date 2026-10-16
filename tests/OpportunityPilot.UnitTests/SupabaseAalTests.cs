using OpportunityPilot.Api.Auth;

namespace OpportunityPilot.UnitTests;

public sealed class SupabaseAalTests
{
    [Theory]
    [InlineData("aal2", true)]
    [InlineData("aal1", false)]
    [InlineData(null, false)]
    [InlineData("AAL2", false)]
    public void Only_a_second_factor_session_is_accepted(string? aal, bool ok) => Assert.Equal(ok, SupabaseAal.IsTwoFactor(aal));
}
