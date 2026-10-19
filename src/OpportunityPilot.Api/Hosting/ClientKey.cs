using System.Net;

namespace OpportunityPilot.Api.Hosting;

/// <summary>
/// Who a request counts against for rate limiting: the signed-in owner, else the visitor's own address. The live API sits behind
/// Cloudflare (then Render's proxy), so the connection's peer address is the proxy's and every anonymous visitor would share one
/// bucket (one person creating ten guest sessions blocked guest sign-in for everyone). Cloudflare sets <c>CF-Connecting-IP</c> to the
/// visitor's address and overwrites any value a client sends, so it is used when it holds a valid address; the peer address otherwise
/// (local runs, tests). <c>X-Forwarded-For</c> is still not trusted: a client can prepend to it.
/// </summary>
public static class ClientKey
{
    public const string CloudflareHeader = "CF-Connecting-IP";

    public static string For(HttpContext ctx) =>
        ctx.User.FindFirst("sub")?.Value is { Length: > 0 } sub ? $"user:{sub}" : $"ip:{Address(ctx)}";

    public static string Address(HttpContext ctx)
    {
        var header = ctx.Request.Headers[CloudflareHeader].ToString().Trim();
        if (header.Length is > 0 and <= 45 && IPAddress.TryParse(header, out var visitor)) return visitor.ToString();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }
}
