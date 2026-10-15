using Microsoft.EntityFrameworkCore;
using OpportunityPilot.Application.Abstractions;
using OpportunityPilot.Domain.Agents;
using OpportunityPilot.Domain.Applications;
using OpportunityPilot.Domain.Auth;
using OpportunityPilot.Domain.Automation;
using OpportunityPilot.Domain.Campaigns;
using OpportunityPilot.Domain.Drafts;
using OpportunityPilot.Domain.Opportunities;
using OpportunityPilot.Domain.Outreach;
using OpportunityPilot.Domain.Profiles;
using OpportunityPilot.Domain.Research;
using OpportunityPilot.Domain.Sales;
using OpportunityPilot.Domain.Staffing;
using OpportunityPilot.Domain.Wellfound;

namespace OpportunityPilot.Infrastructure.Persistence;

/// <summary>
/// Provider-neutral model. Each provider has its own subclass so it gets a separate migration set
/// (SqlServer for local LocalDB, Postgres for Supabase). Tables live in the "app" schema, which
/// Supabase's Data API does not expose by default.
/// </summary>
public abstract class AppDbContext(DbContextOptions options) : DbContext(options), IAppDbContext
{
    public const string Schema = "app";

    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<ProfileVersion> ProfileVersions => Set<ProfileVersion>();
    public DbSet<JobApplication> JobApplications => Set<JobApplication>();
    public DbSet<AgentKey> AgentKeys => Set<AgentKey>();
    public DbSet<GuestSession> GuestSessions => Set<GuestSession>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<SourceItem> SourceItems => Set<SourceItem>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ResearchJob> ResearchJobs => Set<ResearchJob>();
    public DbSet<ResearchEvent> ResearchEvents => Set<ResearchEvent>();
    public DbSet<Evidence> Evidence => Set<Evidence>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<OpportunityEvidence> OpportunityEvidence => Set<OpportunityEvidence>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<OutreachDraft> OutreachDrafts => Set<OutreachDraft>();
    public DbSet<Suppression> Suppressions => Set<Suppression>();
    public DbSet<NextAction> NextActions => Set<NextAction>();
    public DbSet<SalesProject> SalesProjects => Set<SalesProject>();
    public DbSet<SalesBid> SalesBids => Set<SalesBid>();
    public DbSet<UpworkOpportunity> UpworkOpportunities => Set<UpworkOpportunity>();
    public DbSet<CampaignSchedule> CampaignSchedules => Set<CampaignSchedule>();
    public DbSet<StaffingAccount> StaffingAccounts => Set<StaffingAccount>();
    public DbSet<StaffingContact> StaffingContacts => Set<StaffingContact>();
    public DbSet<StaffingDeal> StaffingDeals => Set<StaffingDeal>();
    public DbSet<StaffingDealActivity> StaffingDealActivities => Set<StaffingDealActivity>();
    public DbSet<StaffingCandidate> StaffingCandidates => Set<StaffingCandidate>();
    public DbSet<StaffingSubmission> StaffingSubmissions => Set<StaffingSubmission>();
    public DbSet<StaffingInterview> StaffingInterviews => Set<StaffingInterview>();
    public DbSet<StaffingFeedback> StaffingFeedbackEntries => Set<StaffingFeedback>();
    public DbSet<StaffingOffer> StaffingOffers => Set<StaffingOffer>();
    public DbSet<StaffingRateCard> StaffingRateCards => Set<StaffingRateCard>();
    public DbSet<StaffingProposal> StaffingProposals => Set<StaffingProposal>();
    public DbSet<StaffingMessage> StaffingMessages => Set<StaffingMessage>();
    public DbSet<Domain.Auth.SecurityEvent> SecurityEvents => Set<Domain.Auth.SecurityEvent>();
    public DbSet<Domain.Ai.AiUsage> AiUsages => Set<Domain.Ai.AiUsage>();
    public DbSet<Domain.Outreach.ProviderExecution> ProviderExecutions => Set<Domain.Outreach.ProviderExecution>();
    public DbSet<StaffingMeeting> StaffingMeetings => Set<StaffingMeeting>();
    public DbSet<WellfoundJob> WellfoundJobs => Set<WellfoundJob>();
    public DbSet<WellfoundApplication> WellfoundApplications => Set<WellfoundApplication>();
    public DbSet<WellfoundActivity> WellfoundActivities => Set<WellfoundActivity>();
    public DbSet<Domain.Mail.MailboxConnection> MailboxConnections => Set<Domain.Mail.MailboxConnection>();
    public DbSet<Domain.Integrations.IntegrationConnection> IntegrationConnections => Set<Domain.Integrations.IntegrationConnection>();
    public DbSet<CandidateProgressLink> CandidateProgressLinks => Set<CandidateProgressLink>();
    public DbSet<UserEvent> UserEvents => Set<UserEvent>();
    public DbSet<Domain.Circle.CirclePerson> CirclePeople => Set<Domain.Circle.CirclePerson>();
    public DbSet<Domain.Interviews.JobInterview> JobInterviews => Set<Domain.Interviews.JobInterview>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Profile>(e =>
        {
            e.ToTable("profiles");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedNever();
            e.Property(p => p.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.StructuredDataJson).IsRequired();
            e.Property(p => p.Version).IsConcurrencyToken();
            e.HasIndex(p => new { p.OwnerId, p.UpdatedAt });
        });

        modelBuilder.Entity<ProfileVersion>(e =>
        {
            e.ToTable("profile_versions");
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).ValueGeneratedNever();
            e.Property(v => v.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(v => v.Name).HasMaxLength(200).IsRequired();
            e.Property(v => v.StructuredDataJson).IsRequired();
            e.HasOne<Profile>().WithMany().HasForeignKey(v => v.ProfileId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(v => new { v.OwnerId, v.ProfileId, v.Version }).IsUnique();
        });

        modelBuilder.Entity<JobApplication>(e =>
        {
            e.ToTable("job_applications");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.Platform).HasConversion<string>().HasMaxLength(32);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(a => a.ExternalJobId).HasMaxLength(100).IsRequired();
            e.Property(a => a.JobUrl).HasMaxLength(1000).IsRequired();
            e.Property(a => a.Title).HasMaxLength(300).IsRequired();
            e.Property(a => a.Company).HasMaxLength(300).IsRequired();
            e.Property(a => a.Location).HasMaxLength(200);
            e.Property(a => a.Detail).HasMaxLength(1000);
            e.HasIndex(a => new { a.OwnerId, a.Platform, a.ExternalJobId }).IsUnique();
            e.HasIndex(a => new { a.OwnerId, a.OccurredAt });
        });

        modelBuilder.Entity<Domain.Outreach.ProviderExecution>(e =>
        {
            e.ToTable("provider_executions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Subject).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ContentHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(Domain.Outreach.ProviderExecution.MaxKeyLength).IsRequired();
            e.Property(x => x.Receipt).HasMaxLength(Domain.Outreach.ProviderExecution.MaxReceiptLength);
            e.Property(x => x.ProviderReference).HasMaxLength(Domain.Outreach.ProviderExecution.MaxReferenceLength);
            e.Property(x => x.SafeFailure).HasMaxLength(Domain.Outreach.ProviderExecution.MaxFailureLength);
            // Two requests racing to execute the same approved content: the database lets only one through.
            e.HasIndex(x => new { x.OwnerId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.OwnerId, x.SubjectId });
        });

        modelBuilder.Entity<Domain.Mail.MailboxConnection>(e =>
        {
            e.ToTable("mailbox_connections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.EmailAddress).HasMaxLength(Domain.Mail.MailboxConnection.MaxEmailLength).IsRequired();
            e.Property(x => x.EncryptedRefreshToken).HasMaxLength(Domain.Mail.MailboxConnection.MaxTokenLength).IsRequired();
            e.HasIndex(x => new { x.OwnerId, x.Provider }).IsUnique();
        });

        modelBuilder.Entity<Domain.Integrations.IntegrationConnection>(e =>
        {
            e.ToTable("integration_connections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Label).HasMaxLength(Domain.Integrations.IntegrationConnection.MaxLabelLength).IsRequired();
            e.Property(x => x.EncryptedSecret).HasMaxLength(Domain.Integrations.IntegrationConnection.MaxSecretLength).IsRequired();
            e.Property(x => x.SettingsJson).HasMaxLength(Domain.Integrations.IntegrationConnection.MaxSettingsLength).IsRequired();
            e.Property(x => x.Events).HasMaxLength(Domain.Integrations.IntegrationConnection.MaxEventsLength).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(Domain.Integrations.IntegrationConnection.MaxErrorLength);
            e.HasIndex(x => new { x.OwnerId, x.Kind }).IsUnique();
        });

        modelBuilder.Entity<CandidateProgressLink>(e =>
        {
            e.ToTable("candidate_progress_links");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Show).HasConversion<int>();
            // The public page finds a link by the hash of the token, so it must be unique and fast to look up.
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.OwnerId, x.CandidateId });
        });

        modelBuilder.Entity<Domain.Ai.AiUsage>(e =>
        {
            e.ToTable("ai_usage");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Operation).HasMaxLength(Domain.Ai.AiUsage.MaxOperationLength).IsRequired();
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Reason).HasMaxLength(Domain.Ai.AiUsage.MaxReasonLength);
            e.HasIndex(x => new { x.OwnerId, x.OccurredAt });
            e.Ignore(x => x.Counts);
        });

        modelBuilder.Entity<Domain.Auth.SecurityEvent>(e =>
        {
            e.ToTable("security_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Detail).HasMaxLength(Domain.Auth.SecurityEvent.MaxDetailLength);
            e.HasIndex(x => new { x.OwnerId, x.OccurredAt });
        });

        modelBuilder.Entity<AgentKey>(e =>
        {
            e.ToTable("agent_keys");
            e.Property(k => k.Scopes).HasConversion<int>().HasDefaultValue(AgentKeyScope.All);  // 15 since People; migration AddJdCircle moved stored 7 to 15
            e.HasKey(k => k.Id);
            e.Property(k => k.Id).ValueGeneratedNever();
            e.Property(k => k.Name).HasMaxLength(100).IsRequired();
            e.Property(k => k.KeyHash).HasMaxLength(64).IsRequired();
            e.Property(k => k.Prefix).HasMaxLength(12).IsRequired();
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.HasIndex(k => k.OwnerId);
        });

        modelBuilder.Entity<GuestSession>(e =>
        {
            e.ToTable("guest_sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasIndex(s => s.ExpiresAt);
        });

        modelBuilder.Entity<Campaign>(e =>
        {
            e.ToTable("campaigns");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.Mode).HasConversion<string>().HasMaxLength(32);
            e.Property(c => c.Name).HasMaxLength(Campaign.MaxNameLength).IsRequired();
            e.Property(c => c.Goal).HasMaxLength(Campaign.MaxGoalLength).IsRequired();
            e.Property(c => c.CriteriaJson).IsRequired();
            e.Property(c => c.WeightsJson).IsRequired();
            e.Property(c => c.Version).IsConcurrencyToken();
            e.HasOne<Profile>().WithMany().HasForeignKey(c => c.ProfileId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(c => new { c.OwnerId, c.CreatedAt });
        });

        modelBuilder.Entity<Source>(e =>
        {
            e.ToTable("sources");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.Platform).HasConversion<string>().HasMaxLength(32);
            e.Property(s => s.Label).HasMaxLength(Source.MaxLabelLength).IsRequired();
            e.Property(s => s.Url).HasMaxLength(Source.MaxUrlLength);
            e.Property(s => s.Text).HasMaxLength(Source.MaxTextLength);
            e.Property(s => s.PermissionNote).HasMaxLength(Source.MaxPermissionNoteLength);
            e.Property(s => s.SafeError).HasMaxLength(Source.MaxSafeErrorLength);
            e.HasOne<Campaign>().WithMany().HasForeignKey(s => s.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.OwnerId, s.CampaignId, s.CreatedAt });
        });

        modelBuilder.Entity<SourceItem>(e =>
        {
            e.ToTable("source_items");
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).ValueGeneratedNever();
            e.Property(i => i.ExternalId).HasMaxLength(SourceItem.MaxExternalIdLength);
            e.Property(i => i.Title).HasMaxLength(SourceItem.MaxTitleLength).IsRequired();
            e.Property(i => i.Organization).HasMaxLength(SourceItem.MaxOrganizationLength).IsRequired();
            e.Property(i => i.Location).HasMaxLength(SourceItem.MaxLocationLength);
            e.Property(i => i.Url).HasMaxLength(SourceItem.MaxUrlLength);
            e.Property(i => i.Description).HasMaxLength(SourceItem.MaxDescriptionLength);
            e.Property(i => i.Website).HasMaxLength(SourceItem.MaxUrlLength);
            e.Property(i => i.Country).HasMaxLength(SourceItem.MaxCountryLength);
            e.Property(i => i.Industry).HasMaxLength(SourceItem.MaxIndustryLength);
            e.HasOne<Source>().WithMany().HasForeignKey(i => i.SourceId).OnDelete(DeleteBehavior.Cascade);
            // Providers add "ExternalId IS NOT NULL" (SqlServer does so by itself; Postgres in its subclass).
            e.HasIndex(i => new { i.SourceId, i.ExternalId }).IsUnique();
            e.HasIndex(i => new { i.SourceId, i.UpdatedAt });
        });

        modelBuilder.Entity<ImportBatch>(e =>
        {
            e.ToTable("import_batches");
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).ValueGeneratedNever();
            e.Property(b => b.RowsJson).IsRequired();
            // Two simultaneous commits of one preview: the second update finds Committed already true and fails.
            e.Property(b => b.Committed).IsConcurrencyToken();
            e.HasOne<Campaign>().WithMany().HasForeignKey(b => b.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(b => new { b.OwnerId, b.CreatedAt });
        });

        modelBuilder.Entity<ResearchJob>(e =>
        {
            e.ToTable("research_jobs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Id).ValueGeneratedNever();
            e.Property(j => j.State).HasConversion<string>().HasMaxLength(32);
            e.Property(j => j.Stage).HasConversion<string>().HasMaxLength(32);
            e.Property(j => j.CountsJson).IsRequired();
            e.Property(j => j.ProfileSnapshotJson).IsRequired().HasDefaultValue("{}");
            e.Property(j => j.CriteriaSnapshotJson).IsRequired().HasDefaultValue("{}");
            e.Property(j => j.SafeError).HasMaxLength(ResearchJob.MaxSafeErrorLength);
            e.Property(j => j.Version).IsConcurrencyToken();
            e.HasOne<Campaign>().WithMany().HasForeignKey(j => j.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(j => new { j.OwnerId, j.CampaignId, j.CreatedAt });
            // The processor's poll: queued jobs and running jobs whose lease may have expired.
            e.HasIndex(j => new { j.State, j.CreatedAt });
        });

        modelBuilder.Entity<ResearchEvent>(e =>
        {
            e.ToTable("research_events");
            e.HasKey(ev => ev.Id);
            e.Property(ev => ev.Id).ValueGeneratedNever();
            e.Property(ev => ev.Stage).HasConversion<string>().HasMaxLength(32);
            e.Property(ev => ev.Level).HasConversion<string>().HasMaxLength(32);
            e.Property(ev => ev.Message).HasMaxLength(ResearchEvent.MaxMessageLength).IsRequired();
            e.HasOne<ResearchJob>().WithMany().HasForeignKey(ev => ev.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(ev => new { ev.JobId, ev.At });
        });

        modelBuilder.Entity<Evidence>(e =>
        {
            e.ToTable("evidence");
            e.HasKey(ev => ev.Id);
            e.Property(ev => ev.Id).ValueGeneratedNever();
            e.Property(ev => ev.Url).HasMaxLength(Domain.Research.Evidence.MaxUrlLength);
            e.Property(ev => ev.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(ev => ev.Excerpt).HasMaxLength(Domain.Research.Evidence.MaxExcerptLength).IsRequired();
            e.Property(ev => ev.ExtractionMethod).HasMaxLength(32).IsRequired();
            e.HasOne<Campaign>().WithMany().HasForeignKey(ev => ev.CampaignId).OnDelete(DeleteBehavior.Cascade);
            // No FK to the source: deleting a source must not erase the evidence behind existing scores.
            e.HasIndex(ev => new { ev.CampaignId, ev.SourceId, ev.ContentHash });
            e.HasIndex(ev => ev.OwnerId);
        });

        modelBuilder.Entity<Opportunity>(e =>
        {
            e.ToTable("opportunities");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).ValueGeneratedNever();
            e.Property(o => o.Mode).HasConversion<string>().HasMaxLength(32);
            e.Property(o => o.Outcome).HasConversion<string>().HasMaxLength(32);
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(o => o.Platform).HasConversion<string>().HasMaxLength(32);
            e.Property(o => o.DedupeKey).HasMaxLength(Opportunity.MaxDedupeKeyLength).IsRequired();
            e.Property(o => o.Title).HasMaxLength(Opportunity.MaxTitleLength).IsRequired();
            e.Property(o => o.Organization).HasMaxLength(Opportunity.MaxOrganizationLength).IsRequired();
            e.Property(o => o.Location).HasMaxLength(Opportunity.MaxLocationLength);
            e.Property(o => o.Url).HasMaxLength(Opportunity.MaxUrlLength);
            e.Property(o => o.ApplyUrl).HasMaxLength(Opportunity.MaxUrlLength);
            e.Property(o => o.ExternalId).HasMaxLength(Opportunity.MaxExternalIdLength);
            e.Property(o => o.Description).HasMaxLength(Opportunity.MaxDescriptionLength);
            e.Property(o => o.OutcomeReason).HasMaxLength(Opportunity.MaxOutcomeReasonLength);
            e.Property(o => o.BreakdownJson).IsRequired();
            e.Property(o => o.FactsJson).IsRequired();
            e.Property(o => o.GapsJson).IsRequired();
            e.Property(o => o.Version).IsConcurrencyToken();
            e.HasOne<Campaign>().WithMany().HasForeignKey(o => o.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(o => new { o.CampaignId, o.DedupeKey }).IsUnique();
            e.HasIndex(o => new { o.OwnerId, o.CampaignId, o.Score });
            e.HasIndex(o => new { o.OwnerId, o.Status, o.UpdatedAt });
            e.Ignore(o => o.CircleWaiting);
        });

        modelBuilder.Entity<Domain.Circle.CirclePerson>(e =>
        {
            e.ToTable("circle_people");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(Domain.Circle.CirclePerson.MaxNameLength).IsRequired();
            e.Property(x => x.Headline).HasMaxLength(Domain.Circle.CirclePerson.MaxHeadlineLength);
            e.Property(x => x.ProfileUrl).HasMaxLength(Domain.Circle.LinkedInProfileUrl.MaxLength).IsRequired();
            e.Property(x => x.Relation).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            // One row per person per lead; InMemory does not enforce it, so CircleService checks too.
            e.HasIndex(x => new { x.OwnerId, x.OpportunityId, x.ProfileUrl }).IsUnique();
        });

        modelBuilder.Entity<Domain.Interviews.JobInterview>(e =>
        {
            e.ToTable("job_interviews");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.MeetingUrl).HasMaxLength(Domain.Interviews.JobInterview.MaxLinkLength);
            e.Property(x => x.Notes).HasMaxLength(Domain.Interviews.JobInterview.MaxNotesLength);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.OpportunityId, x.Round });
            e.HasIndex(x => new { x.OwnerId, x.ScheduledAt });
        });

        modelBuilder.Entity<OpportunityEvidence>(e =>
        {
            e.ToTable("opportunity_evidence");
            e.HasKey(l => new { l.OpportunityId, l.EvidenceId });
            e.HasOne<Opportunity>().WithMany().HasForeignKey(l => l.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            // Restrict, not cascade: SQL Server rejects two cascade paths from campaigns (via opportunities and via evidence).
            e.HasOne<Evidence>().WithMany().HasForeignKey(l => l.EvidenceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => l.EvidenceId);
        });

        modelBuilder.Entity<Activity>(e =>
        {
            e.ToTable("activities");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.Kind).HasMaxLength(32).IsRequired();
            e.Property(a => a.Detail).HasMaxLength(Activity.MaxDetailLength).IsRequired();
            e.HasOne<Opportunity>().WithMany().HasForeignKey(a => a.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.OpportunityId, a.OccurredAt });
            e.HasIndex(a => a.OwnerId);
        });

        modelBuilder.Entity<UserEvent>(e =>
        {
            e.ToTable("user_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(UserEvent.MaxDetailLength).IsRequired();
            e.Property(x => x.Title).HasMaxLength(UserEvent.MaxTitleLength);
            e.HasIndex(x => new { x.OwnerId, x.OccurredAt });
        });

        modelBuilder.Entity<OutreachDraft>(e =>
        {
            e.ToTable("outreach_drafts");
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).ValueGeneratedNever();
            e.Property(d => d.Channel).HasConversion<string>().HasMaxLength(32);
            e.Property(d => d.State).HasConversion<string>().HasMaxLength(32);
            e.Property(d => d.Source).HasConversion<string>().HasMaxLength(32);
            e.Property(d => d.Recipient).HasMaxLength(OutreachDraft.MaxRecipientLength);
            e.Property(d => d.Subject).HasMaxLength(OutreachDraft.MaxSubjectLength);
            e.Property(d => d.Body).HasMaxLength(OutreachDraft.MaxBodyLength).IsRequired();
            e.Property(d => d.ApprovedHash).HasMaxLength(OutreachDraft.HashLength);
            e.Property(d => d.ClaimsJson).IsRequired();
            e.Property(d => d.Version).IsConcurrencyToken();
            e.HasOne<Opportunity>().WithMany().HasForeignKey(d => d.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => new { d.OwnerId, d.OpportunityId, d.UpdatedAt });
            e.HasIndex(d => new { d.OwnerId, d.State, d.UpdatedAt });
            e.HasIndex(d => new { d.OpportunityId, d.Channel }).IsUnique();
        });

        modelBuilder.Entity<Suppression>(e =>
        {
            e.ToTable("suppressions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.NormalizedRecipient).HasMaxLength(Suppression.MaxRecipientLength).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(Suppression.MaxReasonLength).IsRequired();
            e.HasIndex(x => new { x.OwnerId, x.NormalizedRecipient }).IsUnique();
        });

        modelBuilder.Entity<NextAction>(e =>
        {
            e.ToTable("next_actions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Note).HasMaxLength(NextAction.MaxNoteLength).IsRequired();
            e.Property(x => x.TimeZone).HasMaxLength(NextAction.MaxTimeZoneLength).IsRequired();
            e.HasOne<Opportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.State, x.DueAt });
            e.HasIndex(x => new { x.OpportunityId, x.DueAt });
        });

        modelBuilder.Entity<SalesProject>(e =>
        {
            e.ToTable("sales_projects");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).ValueGeneratedNever();
            e.Property(p => p.Source).HasConversion<string>().HasMaxLength(32);
            e.Property(p => p.ExternalId).HasMaxLength(SalesProject.MaxExternalIdLength);
            e.Property(p => p.Title).HasMaxLength(SalesProject.MaxTitleLength).IsRequired();
            e.Property(p => p.Buyer).HasMaxLength(SalesProject.MaxBuyerLength);
            e.Property(p => p.Description).HasMaxLength(SalesProject.MaxDescriptionLength);
            e.Property(p => p.Url).HasMaxLength(SalesProject.MaxUrlLength);
            e.Property(p => p.State).HasConversion<string>().HasMaxLength(32);
            e.Property(p => p.EvidenceJson).IsRequired();
            e.Property(p => p.Version).IsConcurrencyToken();
            e.HasIndex(p => new { p.OwnerId, p.UpdatedAt });
            e.HasIndex(p => new { p.OwnerId, p.Source, p.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<SalesBid>(e =>
        {
            e.ToTable("sales_bids");
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).ValueGeneratedNever();
            e.Property(b => b.Amount).HasPrecision(18, 2);
            e.Property(b => b.Currency).HasMaxLength(SalesBid.MaxCurrencyLength).IsRequired();
            e.Property(b => b.Proposal).HasMaxLength(SalesBid.MaxProposalLength).IsRequired();
            e.Property(b => b.ClaimsJson).IsRequired();
            e.Property(b => b.State).HasConversion<string>().HasMaxLength(32);
            e.Property(b => b.ApprovedHash).HasMaxLength(SalesBid.HashLength);
            e.Property(b => b.Version).IsConcurrencyToken();
            e.HasOne<SalesProject>().WithMany().HasForeignKey(b => b.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(b => new { b.OwnerId, b.ProjectId, b.UpdatedAt });
        });

        modelBuilder.Entity<UpworkOpportunity>(e =>
        {
            e.ToTable("upwork_opportunities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ProviderJobId).HasMaxLength(UpworkOpportunity.MaxProviderJobIdLength).IsRequired();
            e.Property(x => x.Title).HasMaxLength(UpworkOpportunity.MaxTitleLength).IsRequired();
            e.Property(x => x.Url).HasMaxLength(UpworkOpportunity.MaxUrlLength).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(UpworkOpportunity.MaxSummaryLength);
            e.Property(x => x.Location).HasMaxLength(UpworkOpportunity.MaxLocationLength);
            e.Property(x => x.BudgetType).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.BudgetMin).HasPrecision(18, 2);
            e.Property(x => x.BudgetMax).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(UpworkOpportunity.MaxCurrencyLength);
            e.Property(x => x.ExperienceLevel).HasMaxLength(UpworkOpportunity.MaxExperienceLevelLength);
            e.Property(x => x.EvidenceJson).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<SalesProject>().WithMany().HasForeignKey(x => x.SalesProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.OwnerId, x.ProviderJobId }).IsUnique();
            e.HasIndex(x => new { x.OwnerId, x.State, x.ObservedAt });
        });

        modelBuilder.Entity<CampaignSchedule>(e =>
        {
            e.ToTable("campaign_schedules");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.TimeZone).HasMaxLength(CampaignSchedule.MaxTimeZoneLength).IsRequired();
            e.Property(s => s.LastSafeError).HasMaxLength(CampaignSchedule.MaxSafeErrorLength);
            e.Property(s => s.Version).IsConcurrencyToken();
            e.HasOne<Campaign>().WithMany().HasForeignKey(s => s.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.OwnerId, s.CampaignId }).IsUnique();
            e.HasIndex(s => new { s.Paused, s.NextRunAt });
        });

        modelBuilder.Entity<StaffingAccount>(e =>
        {
            e.ToTable("staffing_accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(StaffingAccount.MaxNameLength).IsRequired();
            e.Property(x => x.Domain).HasMaxLength(StaffingAccount.MaxDomainLength);
            e.Property(x => x.Industry).HasMaxLength(StaffingAccount.MaxIndustryLength);
            e.Property(x => x.Location).HasMaxLength(StaffingAccount.MaxLocationLength);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.SourceReference).HasMaxLength(StaffingAccount.MaxSourceReferenceLength);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
            e.HasIndex(x => new { x.OwnerId, x.Domain });
        });

        modelBuilder.Entity<StaffingContact>(e =>
        {
            e.ToTable("staffing_contacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(StaffingContact.MaxNameLength).IsRequired();
            e.Property(x => x.Title).HasMaxLength(StaffingContact.MaxTitleLength);
            e.Property(x => x.Email).HasMaxLength(StaffingContact.MaxEmailLength);
            e.Property(x => x.LinkedInUrl).HasMaxLength(StaffingContact.MaxLinkedInUrlLength);
            e.Property(x => x.Evidence).HasMaxLength(StaffingContact.MaxEvidenceLength);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<StaffingAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.AccountId, x.Name });
        });

        modelBuilder.Entity<StaffingDeal>(e =>
        {
            e.ToTable("staffing_deals");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(StaffingDeal.MaxTitleLength).IsRequired();
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ExternalReference).HasMaxLength(StaffingDeal.MaxExternalReferenceLength);
            e.Property(x => x.EstimatedValue).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(StaffingDeal.MaxCurrencyLength);
            e.Property(x => x.Stage).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.StageBeforeHold).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.NextAction).HasMaxLength(StaffingDeal.MaxNextActionLength);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<StaffingAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StaffingContact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.Stage, x.UpdatedAt });
            e.HasIndex(x => new { x.OwnerId, x.Source, x.ExternalReference });
        });

        modelBuilder.Entity<StaffingDealActivity>(e =>
        {
            e.ToTable("staffing_deal_activities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Detail).HasMaxLength(StaffingDealActivity.MaxDetailLength).IsRequired();
            e.HasOne<StaffingDeal>().WithMany().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.DealId, x.OccurredAt });
        });

        modelBuilder.Entity<StaffingCandidate>(e =>
        {
            e.ToTable("staffing_candidates");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(StaffingCandidate.MaxNameLength).IsRequired();
            e.Property(x => x.Headline).HasMaxLength(StaffingCandidate.MaxHeadlineLength);
            e.Property(x => x.Email).HasMaxLength(StaffingCandidate.MaxContactLength);
            e.Property(x => x.Phone).HasMaxLength(StaffingCandidate.MaxContactLength);
            e.Property(x => x.Location).HasMaxLength(StaffingCandidate.MaxLocationLength);
            e.Property(x => x.Skills).HasMaxLength(StaffingCandidate.MaxSkillsLength);
            e.Property(x => x.Availability).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.RateAmount).HasPrecision(18, 2);
            e.Property(x => x.RateCurrency).HasMaxLength(3);
            e.Property(x => x.RateUnit).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ResumeText).HasMaxLength(StaffingCandidate.MaxResumeLength);
            e.Property(x => x.Consent).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ConsentEvidence).HasMaxLength(StaffingCandidate.MaxConsentEvidenceLength);
            e.Property(x => x.ShareableFields).HasConversion<int>();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
        });

        modelBuilder.Entity<StaffingSubmission>(e =>
        {
            e.ToTable("staffing_submissions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SharedFields).HasConversion<int>();
            e.Property(x => x.SnapshotJson).HasMaxLength(StaffingSubmission.MaxSnapshotLength).IsRequired();
            e.Property(x => x.Note).HasMaxLength(StaffingSubmission.MaxNoteLength);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Receipt).HasMaxLength(StaffingSubmission.MaxReceiptLength);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<StaffingDeal>().WithMany().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StaffingCandidate>().WithMany().HasForeignKey(x => x.CandidateId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.DealId });
            e.HasIndex(x => new { x.OwnerId, x.CandidateId });
        });

        modelBuilder.Entity<StaffingInterview>(e =>
        {
            e.ToTable("staffing_interviews");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.TimeZone).HasMaxLength(StaffingInterview.MaxTimeZoneLength);
            e.Property(x => x.Location).HasMaxLength(StaffingInterview.MaxLocationLength);
            e.Property(x => x.CandidateNotes).HasMaxLength(StaffingInterview.MaxNotesLength);
            e.Property(x => x.InternalNotes).HasMaxLength(StaffingInterview.MaxNotesLength);
            e.Property(x => x.CandidateNotification).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<StaffingDeal>().WithMany().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StaffingSubmission>().WithMany().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.DealId, x.Round });
            e.HasIndex(x => new { x.OwnerId, x.ScheduledAt });
        });

        modelBuilder.Entity<StaffingFeedback>(e =>
        {
            e.ToTable("staffing_feedback");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Decision).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Detail).HasMaxLength(StaffingFeedback.MaxDetailLength).IsRequired();
            e.HasOne<StaffingDeal>().WithMany().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StaffingSubmission>().WithMany().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.SubmissionId, x.RecordedAt });
        });

        modelBuilder.Entity<StaffingOffer>(e =>
        {
            e.ToTable("staffing_offers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ClientRate).HasPrecision(18, 2);
            e.Property(x => x.CandidatePay).HasPrecision(18, 2);
            e.Property(x => x.PlacementValue).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Unit).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Contract).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ContractVersion).HasMaxLength(StaffingOffer.MaxContractVersionLength);
            e.Property(x => x.SignatureProvider).HasMaxLength(StaffingOffer.MaxReferenceLength);
            e.Property(x => x.SignedDocumentReference).HasMaxLength(StaffingOffer.MaxReferenceLength);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Notes).HasMaxLength(StaffingOffer.MaxNotesLength);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<StaffingDeal>().WithMany().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StaffingSubmission>().WithMany().HasForeignKey(x => x.SubmissionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.DealId });
        });

        modelBuilder.Entity<StaffingRateCard>(e =>
        {
            e.ToTable("staffing_rate_cards");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(StaffingRateCard.MaxNameLength).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.LinesJson).HasMaxLength(StaffingRateCard.MaxLinesLength).IsRequired();
            e.Property(x => x.Terms).HasMaxLength(StaffingRateCard.MaxTermsLength);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.OwnerId, x.Status });
        });

        modelBuilder.Entity<StaffingProposal>(e =>
        {
            e.ToTable("staffing_proposals");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(StaffingProposal.MaxTitleLength).IsRequired();
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.LinesJson).HasMaxLength(StaffingRateCard.MaxLinesLength).IsRequired();
            e.Property(x => x.Terms).HasMaxLength(StaffingRateCard.MaxTermsLength);
            e.Property(x => x.Body).HasMaxLength(StaffingProposal.MaxBodyLength);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Receipt).HasMaxLength(1_000);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<StaffingDeal>().WithMany().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StaffingRateCard>().WithMany().HasForeignKey(x => x.RateCardId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.DealId });
        });

        modelBuilder.Entity<StaffingMessage>(e =>
        {
            e.ToTable("staffing_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Counterpart).HasMaxLength(StaffingMessage.MaxRecipientLength).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(StaffingMessage.MaxSubjectLength);
            e.Property(x => x.Body).HasMaxLength(StaffingMessage.MaxBodyLength).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Receipt).HasMaxLength(StaffingMessage.MaxReceiptLength);
            e.Property(x => x.Intent).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<StaffingDeal>().WithMany().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.DealId, x.CreatedAt });
            e.HasIndex(x => new { x.OwnerId, x.State });
        });

        modelBuilder.Entity<StaffingMeeting>(e =>
        {
            e.ToTable("staffing_meetings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(StaffingMeeting.MaxTitleLength).IsRequired();
            e.Property(x => x.TimeZone).HasMaxLength(64).IsRequired();
            e.Property(x => x.Invitees).HasMaxLength(StaffingMeeting.MaxInviteesLength).IsRequired();
            e.Property(x => x.Agenda).HasMaxLength(StaffingMeeting.MaxAgendaLength);
            e.Property(x => x.Link).HasMaxLength(StaffingMeeting.MaxLinkLength);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Receipt).HasMaxLength(1_000);
            e.Property(x => x.Outcome).HasMaxLength(StaffingMeeting.MaxAgendaLength);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<StaffingDeal>().WithMany().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.StartsAt });
        });

        modelBuilder.Entity<WellfoundJob>(e =>
        {
            e.ToTable("wellfound_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ProviderJobId).HasMaxLength(WellfoundJob.MaxProviderIdLength).IsRequired();
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(WellfoundJob.MaxNameLength).IsRequired();
            e.Property(x => x.CompanyName).HasMaxLength(WellfoundJob.MaxNameLength).IsRequired();
            e.Property(x => x.CompanyLogoUrl).HasMaxLength(WellfoundJob.MaxUrlLength);
            e.Property(x => x.Location).HasMaxLength(WellfoundJob.MaxShortLength);
            e.Property(x => x.RemoteType).HasMaxLength(WellfoundJob.MaxShortLength);
            e.Property(x => x.SalaryMin).HasPrecision(18, 2);
            e.Property(x => x.SalaryMax).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.EquityMin).HasPrecision(8, 4);
            e.Property(x => x.EquityMax).HasPrecision(8, 4);
            e.Property(x => x.ExperienceLevel).HasMaxLength(WellfoundJob.MaxShortLength);
            e.Property(x => x.EmploymentType).HasMaxLength(WellfoundJob.MaxShortLength);
            e.Property(x => x.Industry).HasMaxLength(WellfoundJob.MaxShortLength);
            e.Property(x => x.FundingStage).HasMaxLength(WellfoundJob.MaxShortLength);
            e.Property(x => x.EmployeeCount).HasMaxLength(WellfoundJob.MaxShortLength);
            e.Property(x => x.ApplyUrl).HasMaxLength(WellfoundJob.MaxUrlLength).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(WellfoundJob.MaxSummaryLength);
            e.Property(x => x.SkillsJson).IsRequired();
            e.Property(x => x.EvidenceJson).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.OwnerId, x.ProviderJobId }).IsUnique();
            e.HasIndex(x => new { x.OwnerId, x.Scope, x.State, x.PostedAt });
        });

        modelBuilder.Entity<WellfoundApplication>(e =>
        {
            e.ToTable("wellfound_applications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ProviderApplicationId).HasMaxLength(WellfoundApplication.MaxProviderIdLength).IsRequired();
            e.Property(x => x.CandidateName).HasMaxLength(WellfoundApplication.MaxNameLength).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.EvidenceJson).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<WellfoundJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.OwnerId, x.ProviderApplicationId }).IsUnique();
            e.HasIndex(x => new { x.OwnerId, x.State, x.UpdatedAt });
        });

        modelBuilder.Entity<WellfoundActivity>(e =>
        {
            e.ToTable("wellfound_activities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Detail).HasMaxLength(WellfoundActivity.MaxDetailLength).IsRequired();
            e.HasOne<WellfoundJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WellfoundApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.OccurredAt });
        });
    }
}
