using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpportunityPilot.Api.Hosting;

namespace OpportunityPilot.UnitTests;

public sealed class ClientKeyTests
{
    private static DefaultHttpContext Ctx(string peer, string? cf = null, string? sub = null, string? xff = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (cf is not null) ctx.Request.Headers[ClientKey.CloudflareHeader] = cf;
        if (xff is not null) ctx.Request.Headers["X-Forwarded-For"] = xff;
        if (sub is not null) ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub)], "test"));
        return ctx;
    }

    [Fact]
    public void Anonymous_visitors_behind_the_proxy_get_their_own_bucket()
    {
        // Same proxy peer, two visitors: before 2026-10-19 both counted against 10.0.0.1 and blocked each other.
        Assert.Equal("ip:203.0.113.7", ClientKey.For(Ctx("10.0.0.1", cf: "203.0.113.7")));
        Assert.Equal("ip:198.51.100.9", ClientKey.For(Ctx("10.0.0.1", cf: "198.51.100.9")));
    }

    [Fact]
    public void A_signed_in_person_is_counted_by_account_and_forged_headers_are_ignored()
    {
        Assert.Equal("user:abc", ClientKey.For(Ctx("10.0.0.1", cf: "203.0.113.7", sub: "abc")));
        Assert.Equal("ip:10.0.0.1", ClientKey.For(Ctx("10.0.0.1", cf: "not-an-ip")));
        Assert.Equal("ip:10.0.0.1", ClientKey.For(Ctx("10.0.0.1", xff: "1.2.3.4")));
    }
}
