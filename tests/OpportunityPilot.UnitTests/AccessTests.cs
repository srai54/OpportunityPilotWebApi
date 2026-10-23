using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpportunityPilot.Api.Auth;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Access;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Domain.Access;
using OpportunityPilot.Infrastructure.Persistence;

namespace OpportunityPilot.UnitTests;

/// <summary>Role-based access (2026-10-22): no role = both views as before; one sign-up choice; only the admin changes roles.</summary>
public class AccessTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }
    private sealed class Caller(string? email, bool guest = false) : ICallerInfo { public string? Email => email; public bool IsGuest => guest; }
    private sealed class Clock(DateTime now) : TimeProvider { public DateTime Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => new(Now); }

    private static InMemoryAppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AccessService Service(InMemoryAppDbContext db, Guid owner, ICallerInfo caller, AccessOptions? options = null, Clock? clock = null) =>
        new(db, new Owner(owner), caller, Options.Create(options ?? new AccessOptions()), clock ?? new Clock(T0));

    [Fact]
    public async Task A_user_without_a_role_keeps_both_views_and_is_recorded_once()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var me = await Service(db, owner, new Caller("asha@example.com")).MeAsync(default);
        Assert.Equal(("Both", false, false, true), (me.Role, me.RoleChosen, me.IsAdmin, me.NewAccount));
        var later = await Service(db, owner, new Caller("asha@example.com"), clock: new Clock(T0.AddHours(2))).MeAsync(default);
        Assert.False(later.NewAccount);
        var row = await db.UserAccess.SingleAsync();
        Assert.Equal(("asha@example.com", UserRole.None), (row.Email, row.Role));
    }

    [Fact]
    public async Task Guests_are_not_recorded_never_admin_and_cannot_choose()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var options = new AccessOptions { AdminOwnerIds = owner.ToString() };
        var me = await Service(db, owner, new Caller(null, guest: true), options).MeAsync(default);
        Assert.Equal(("Both", false, true), (me.Role, me.IsAdmin, me.Guest));
        Assert.Empty(await db.UserAccess.ToListAsync());
        await Assert.ThrowsAsync<RequestValidationException>(() => Service(db, owner, new Caller(null, guest: true)).ChooseAsync(new RoleRequest("Sales"), default));
    }

    [Fact]
    public async Task The_sign_up_choice_is_kept_and_cannot_be_widened_later()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var s = Service(db, owner, new Caller("ravi@example.com"));
        var me = await s.ChooseAsync(new RoleRequest("Candidate"), default);
        Assert.Equal(("Candidate", true), (me.Role, me.RoleChosen));
        await Assert.ThrowsAsync<ConflictException>(() => s.ChooseAsync(new RoleRequest("Both"), default));
        await Assert.ThrowsAsync<RequestValidationException>(() => Service(NewDb(), owner, new Caller(null)).ChooseAsync(new RoleRequest("Admin"), default));
        Assert.Equal("Candidate", (await s.MeAsync(default)).Role);
    }

    [Fact]
    public async Task The_admin_is_recognised_by_token_email_or_account_id_and_sets_any_role()
    {
        var db = NewDb();
        var userId = Guid.NewGuid();
        await Service(db, userId, new Caller("user@example.com")).ChooseAsync(new RoleRequest("Candidate"), default);

        var options = new AccessOptions { AdminEmails = "Owner@Example.com, other@example.com" };
        var admin = Service(db, Guid.NewGuid(), new Caller("owner@example.com"), options);
        Assert.True(admin.IsAdmin());
        Assert.True(Service(db, Guid.Parse("11111111-1111-1111-1111-111111111111"), new Caller(null), new AccessOptions { AdminOwnerIds = "11111111-1111-1111-1111-111111111111" }).IsAdmin());
        Assert.False(Service(db, Guid.NewGuid(), new Caller("someone@example.com"), options).IsAdmin());

        var row = await admin.SetRoleAsync(userId, new RoleRequest("Both"), default);
        Assert.Equal(("Both", true, true), (row.Role, row.RoleChosen, row.RoleSetByAdmin));
        Assert.Equal("Both", (await Service(db, userId, new Caller("user@example.com")).MeAsync(default)).Role);
        Assert.Contains(await admin.UsersAsync(default), u => u.AccountId == userId && u.Email == "user@example.com");
        await Assert.ThrowsAsync<NotFoundException>(() => admin.SetRoleAsync(Guid.NewGuid(), new RoleRequest("Sales"), default));
    }

    [Fact]
    public async Task A_browser_label_names_the_user_for_the_admin_but_never_makes_anyone_admin()
    {
        var db = NewDb();
        var owner = Guid.NewGuid();
        var options = new AccessOptions { AdminEmails = "owner@example.com" };
        var me = await Service(db, owner, new Caller(null), options).MeAsync(default, "owner@example.com");
        Assert.False(me.IsAdmin);
        Assert.Equal("owner@example.com", (await db.UserAccess.SingleAsync()).Email);
        await Service(db, owner, new Caller("real@example.com"), options).MeAsync(default, "fake@example.com");
        Assert.Equal("real@example.com", (await db.UserAccess.SingleAsync()).Email);
    }

    [Theory]
    [InlineData("""{"address":"me@example.com","is_verified":true,"is_primary":true}""", "me@example.com")]
    [InlineData("""{"address":"me@example.com","is_verified":false}""", null)]
    [InlineData("me@example.com", "me@example.com")]
    [InlineData("not-an-email", null)]
    [InlineData("""{"broken""", null)]
    public void The_Hanko_email_is_kept_only_when_it_is_a_verified_address(string claim, string? expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "hanko-user-1"), new Claim("email", claim)], "test"));
        Assert.Equal(expected, HankoAuth.EmailFrom(principal));
        Assert.Equal(expected, HankoAuth.ToOwnerPrincipal(principal)?.FindFirst("email")?.Value);
    }
}
