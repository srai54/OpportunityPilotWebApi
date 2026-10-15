using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Application.Common;
using OpportunityPilot.Application.Staffing;
using OpportunityPilot.Domain.Staffing;
using OpportunityPilot.Infrastructure.Persistence;
using OpportunityPilot.Infrastructure.Persistence.Repositories;

namespace OpportunityPilot.UnitTests;

/// <summary>A staffing contact's optional phone: stored, editable with a version check, limited like a candidate's phone.</summary>
public class StaffingContactPhoneTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Owner(Guid id) : ICurrentUser { public Guid OwnerId => id; }
    private sealed class Clock(DateTime now) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(now); }

    private static (StaffingService Service, InMemoryAppDbContext Db) NewService(Guid owner)
    {
        var db = new InMemoryAppDbContext(
            new DbContextOptionsBuilder<InMemoryAppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return (new StaffingService(new StaffingRepository(db), new Owner(owner), new Clock(T0)), db);
    }

    [Fact]
    public void Phone_is_optional_trimmed_and_limited_like_a_candidate_phone()
    {
        var c = new StaffingContact(Guid.NewGuid(), Guid.NewGuid(), "Asha", T0);
        c.Update("Asha", null, null, false, null, null, "  +91 98100 12345 ", T0);
        Assert.Equal("+91 98100 12345", c.Phone);
        c.Update("Asha", null, null, false, null, null, "   ", T0);
        Assert.Null(c.Phone);
        Assert.Equal(StaffingCandidate.MaxContactLength, StaffingContact.MaxPhoneLength);
        Assert.Throws<ArgumentException>(() =>
            c.Update("Asha", null, null, false, null, null, new string('9', StaffingContact.MaxPhoneLength + 1), T0));
    }

    [Fact]
    public void The_old_update_overload_keeps_the_stored_phone()
    {
        var c = new StaffingContact(Guid.NewGuid(), Guid.NewGuid(), "Asha", T0);
        c.Update("Asha", null, null, false, null, null, "+91 98100 12345", T0);
        c.Update("Asha K", "CTO", null, false, null, null, T0);
        Assert.Equal(("Asha K", "+91 98100 12345"), (c.Name, c.Phone));
    }

    [Fact]
    public async Task Create_and_update_carry_the_phone_and_a_stale_version_is_a_conflict()
    {
        var owner = Guid.NewGuid();
        var (service, _) = NewService(owner);
        var account = await service.CreateAccountAsync(new("Northwind", StaffingAccountSource.Manual, null, null, null, null), default);
        var created = await service.CreateContactAsync(account.Id,
            new("Asha", "CTO", "asha@example.com", false, null, null, "+91 98100 12345"), default);
        Assert.Equal("+91 98100 12345", created.Phone);

        var withoutPhone = await service.CreateContactAsync(account.Id, new("Ravi", null, null, false, null, null), default);
        Assert.Null(withoutPhone.Phone);

        var updated = await service.UpdateContactAsync(account.Id, created.Id,
            new("Asha", "CTO", "asha@example.com", false, null, null, "+91 98111 22222", created.Version), default);
        Assert.Equal(("+91 98111 22222", created.Version + 1), (updated.Phone, updated.Version));

        await Assert.ThrowsAsync<ConflictException>(() => service.UpdateContactAsync(account.Id, created.Id,
            new("Asha", null, null, false, null, null, null, created.Version), default));
    }

    [Fact]
    public async Task Updating_someone_elses_contact_is_not_found()
    {
        var owner = Guid.NewGuid();
        var (service, db) = NewService(owner);
        var account = await service.CreateAccountAsync(new("Northwind", StaffingAccountSource.Manual, null, null, null, null), default);
        var contact = await service.CreateContactAsync(account.Id, new("Asha", null, null, false, null, null), default);
        var stranger = new StaffingService(new StaffingRepository(db), new Owner(Guid.NewGuid()), new Clock(T0));
        await Assert.ThrowsAsync<NotFoundException>(() => stranger.UpdateContactAsync(account.Id, contact.Id,
            new("Asha", null, null, false, null, null, "+91 98100 12345", contact.Version), default));
    }
}
