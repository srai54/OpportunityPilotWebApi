using System.Net;
using OpportunityPilot.Application.Mail;

namespace OpportunityPilot.UnitTests;

/// <summary>A custom SMTP/IMAP host may only resolve to public addresses (the server connects to it with the person's login).</summary>
public class MailHostsTests
{
    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::7f00:1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("2001:0:a00:1::1")]
    [InlineData("2001:db8::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    public void Private_and_special_addresses_are_refused(string address) =>
        Assert.True(MailHosts.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("142.250.27.108")]
    [InlineData("52.96.0.1")]
    [InlineData("2607:f8b0:4004:c1b::6c")]
    [InlineData("2a01:111:f400::1")]
    public void Public_addresses_are_allowed(string address) =>
        Assert.False(MailHosts.IsPrivate(IPAddress.Parse(address)));
}
