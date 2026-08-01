using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Announcements;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Core.Identity;
using TemperedTyrant.CreatorToolkit.Core.Publications;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Announcements;
using TemperedTyrant.CreatorToolkit.Infrastructure.Discord;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;
using TemperedTyrant.CreatorToolkit.Infrastructure.Publications;

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence;

public sealed class CreatorToolkitDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public CreatorToolkitDbContext(DbContextOptions<CreatorToolkitDbContext> options)
        : base(options)
    {
    }

    public DbSet<InstallationState> InstallationStates => Set<InstallationState>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<Ownership> Ownerships => Set<Ownership>();

    public DbSet<SecurityCapability> SecurityCapabilities => Set<SecurityCapability>();

    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    public DbSet<Announcement> Announcements => Set<Announcement>();

    public DbSet<AnnouncementMediaAsset> AnnouncementMediaAssets => Set<AnnouncementMediaAsset>();

    public DbSet<DiscordConnection> DiscordConnections => Set<DiscordConnection>();

    public DbSet<DiscordDestination> DiscordDestinations => Set<DiscordDestination>();

    public DbSet<Publication> Publications => Set<Publication>();

    public DbSet<PublicationDelivery> PublicationDeliveries => Set<PublicationDelivery>();

    public DbSet<PublicationAttempt> PublicationAttempts => Set<PublicationAttempt>();

    public DbSet<DiagnosticRecord> DiagnosticRecords => Set<DiagnosticRecord>();

    internal DbSet<ProtectedSecretRecord> ProtectedSecrets => Set<ProtectedSecretRecord>();

    internal DbSet<PublicationPayload> PublicationPayloads => Set<PublicationPayload>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectAuditMutation();
        AdvanceRevisionTokens();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        RejectAuditMutation();
        AdvanceRevisionTokens();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        ConfigureIdentity(builder);
        ConfigureInstallationState(builder);
        ConfigureWorkspace(builder);
        ConfigureOwnership(builder);
        ConfigureSecurityCapability(builder);
        ConfigureProtectedSecret(builder);
        ConfigureAuditRecord(builder);
        ConfigureDiagnosticRecord(builder);
        ConfigureAnnouncement(builder);
        ConfigureAnnouncementMedia(builder);
        ConfigureDiscord(builder);
        ConfigurePublications(builder);
    }

    private static void ConfigureIdentity(ModelBuilder builder)
    {
        builder.Entity<ApplicationUser>(user =>
        {
            user.ToTable(
                "AspNetUsers",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_AspNetUsers_DisplayName_Length",
                        """length("DisplayName") BETWEEN 1 AND 200""");
                    table.HasCheckConstraint(
                        "CK_AspNetUsers_UserName_Length",
                        "\"UserName\" IS NULL OR length(\"UserName\") <= 256");
                    table.HasCheckConstraint(
                        "CK_AspNetUsers_NormalizedUserName_Length",
                        "\"NormalizedUserName\" IS NULL OR length(\"NormalizedUserName\") <= 256");
                    table.HasCheckConstraint(
                        "CK_AspNetUsers_Email_Length",
                        "\"Email\" IS NULL OR length(\"Email\") <= 256");
                    table.HasCheckConstraint(
                        "CK_AspNetUsers_NormalizedEmail_Length",
                        "\"NormalizedEmail\" IS NULL OR length(\"NormalizedEmail\") <= 256");
                });
            user.Property(value => value.DisplayName).HasMaxLength(200).IsRequired();
            user.Property(value => value.IsEnabled).HasDefaultValue(true);
            user.Property(value => value.CreatedAtUtc).IsRequired();
        });

        builder.Entity<IdentityRole<Guid>>(role =>
        {
            role.ToTable(
                "AspNetRoles",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_AspNetRoles_Name_Length",
                        "\"Name\" IS NULL OR length(\"Name\") <= 256");
                    table.HasCheckConstraint(
                        "CK_AspNetRoles_NormalizedName_Length",
                        "\"NormalizedName\" IS NULL OR length(\"NormalizedName\") <= 256");
                });
            role.HasData(SystemRoleSeed.All);
        });
    }

    private static void ConfigureInstallationState(ModelBuilder builder)
    {
        builder.Entity<InstallationState>(state =>
        {
            state.ToTable(
                "InstallationStates",
                table => table.HasCheckConstraint(
                    "CK_InstallationStates_Singleton",
                    $"Id = {InstallationState.SingletonId}"));
            state.HasKey(value => value.Id);
            state.Property(value => value.Id).ValueGeneratedNever();
            state.Property(value => value.Revision).IsConcurrencyToken();
            state.ToTable(
                table => table.HasCheckConstraint(
                    "CK_InstallationStates_Revision",
                    """Revision >= 0"""));
            state.HasData(CreateInstallationStateSeed());
        });
    }

    private static void ConfigureWorkspace(ModelBuilder builder)
    {
        builder.Entity<Workspace>(workspace =>
        {
            workspace.ToTable(
                "Workspaces",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_Workspaces_Singleton",
                        $"Id = {Workspace.SingletonId}");
                    table.HasCheckConstraint(
                        "CK_Workspaces_TimeZoneId_Length",
                        """length("TimeZoneId") BETWEEN 1 AND 255""");
                    table.HasCheckConstraint(
                        "CK_Workspaces_Revision",
                        """Revision >= 0""");
                });
            workspace.HasKey(value => value.Id);
            workspace.Property(value => value.Id).ValueGeneratedNever();
            workspace.Property(value => value.TimeZoneId).HasMaxLength(255).IsRequired();
            workspace.Property(value => value.Revision).IsConcurrencyToken();
        });
    }

    private static void ConfigureOwnership(ModelBuilder builder)
    {
        builder.Entity<Ownership>(ownership =>
        {
            ownership.ToTable(
                "Ownerships",
                table => table.HasCheckConstraint(
                    "CK_Ownerships_Revision",
                    """Revision >= 0"""));
            ownership.HasKey(value => value.WorkspaceId);
            ownership.HasIndex(value => value.OwnerUserId).IsUnique();
            ownership.Property(value => value.Revision).IsConcurrencyToken();
            ownership
                .HasOne<Workspace>()
                .WithOne()
                .HasForeignKey<Ownership>(value => value.WorkspaceId)
                .OnDelete(DeleteBehavior.Restrict);
            ownership
                .HasOne<ApplicationUser>()
                .WithOne()
                .HasForeignKey<Ownership>(value => value.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureSecurityCapability(ModelBuilder builder)
    {
        builder.Entity<SecurityCapability>(capability =>
        {
            capability.ToTable(
                "SecurityCapabilities",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_SecurityCapabilities_Purpose",
                        """
                        "Purpose" IN ('BootstrapOwner', 'ActivateUser', 'RecoverOwner')
                        """);
                    table.HasCheckConstraint(
                        "CK_SecurityCapabilities_TokenHash_Length",
                        """length("TokenHash") = 32""");
                    table.HasCheckConstraint(
                        "CK_SecurityCapabilities_ActiveSlot_State",
                        """
                        "ActiveSlot" IS NULL
                        OR (
                            length("ActiveSlot") BETWEEN 1 AND 128
                            AND "UsedAtUtc" IS NULL
                            AND "RevokedAtUtc" IS NULL
                        )
                        """);
                    table.HasCheckConstraint(
                        "CK_SecurityCapabilities_Lifetime",
                        """
                        julianday("CreatedAtUtc") IS NOT NULL
                        AND julianday("ExpiresAtUtc") IS NOT NULL
                        AND julianday("ExpiresAtUtc") > julianday("CreatedAtUtc")
                        """);
                    table.HasCheckConstraint(
                        "CK_SecurityCapabilities_UseTime",
                        """
                        "UsedAtUtc" IS NULL
                        OR (
                            julianday("UsedAtUtc") IS NOT NULL
                            AND julianday("UsedAtUtc") >= julianday("CreatedAtUtc")
                        )
                        """);
                    table.HasCheckConstraint(
                        "CK_SecurityCapabilities_RevocationTime",
                        """
                        "RevokedAtUtc" IS NULL
                        OR (
                            julianday("RevokedAtUtc") IS NOT NULL
                            AND julianday("RevokedAtUtc") >= julianday("CreatedAtUtc")
                        )
                        """);
                    table.HasCheckConstraint(
                        "CK_SecurityCapabilities_TerminalState",
                        """
                        "UsedAtUtc" IS NULL OR "RevokedAtUtc" IS NULL
                        """);
                    table.HasCheckConstraint(
                        "CK_SecurityCapabilities_Revision",
                        """Revision >= 0""");
                });
            capability.HasKey(value => value.Id);
            capability.Property(value => value.Purpose).HasConversion<string>().HasMaxLength(32);
            capability.Property(value => value.TokenHash).HasMaxLength(32).IsRequired();
            capability.Property(value => value.ActiveSlot).HasMaxLength(128);
            capability.Property(value => value.Revision).IsConcurrencyToken();
            capability.HasIndex(value => value.TokenHash).IsUnique();
            capability
                .HasIndex(value => new { value.Purpose, value.ActiveSlot })
                .IsUnique()
                .HasFilter("\"ActiveSlot\" IS NOT NULL");
            capability
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(value => value.SubjectUserId)
                .OnDelete(DeleteBehavior.SetNull);
            capability
                .HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(value => value.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ConfigureProtectedSecret(ModelBuilder builder)
    {
        builder.Entity<ProtectedSecretRecord>(secret =>
        {
            secret.ToTable(
                "ProtectedSecrets",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_ProtectedSecrets_Purpose",
                        """
                        length("Purpose") BETWEEN 1 AND 128
                        AND "Purpose" NOT GLOB '*[^A-Za-z0-9._:-]*'
                        """);
                    table.HasCheckConstraint(
                        "CK_ProtectedSecrets_Ciphertext",
                        """length("Ciphertext") > 0""");
                    table.HasCheckConstraint(
                        "CK_ProtectedSecrets_Revision",
                        """Revision >= 0""");
                });
            secret.HasKey(value => value.Id);
            secret.Property(value => value.Purpose).HasMaxLength(128).IsRequired();
            secret.Property(value => value.Ciphertext).IsRequired();
            secret.Property(value => value.Revision).IsConcurrencyToken();
        });
    }

    private static void ConfigureAuditRecord(ModelBuilder builder)
    {
        builder.Entity<AuditRecord>(audit =>
        {
            audit.ToTable(
                "AuditRecords",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_AuditRecords_EventCode_Length",
                        """length("EventCode") BETWEEN 1 AND 100""");
                    table.HasCheckConstraint(
                        "CK_AuditRecords_Outcome_Length",
                        """length("Outcome") BETWEEN 1 AND 32""");
                    table.HasCheckConstraint(
                        "CK_AuditRecords_ReasonCode_Length",
                        "\"ReasonCode\" IS NULL OR length(\"ReasonCode\") BETWEEN 1 AND 100");
                    table.HasCheckConstraint(
                        "CK_AuditRecords_DiagnosticReference_Length",
                        """
                        "DiagnosticReference" IS NULL
                        OR length("DiagnosticReference") BETWEEN 1 AND 64
                        """);
                });
            audit.HasKey(value => value.Id);
            audit.Property(value => value.EventCode).HasMaxLength(100).IsRequired();
            audit.Property(value => value.Outcome).HasMaxLength(32).IsRequired();
            audit.Property(value => value.ReasonCode).HasMaxLength(100);
            audit.Property(value => value.DiagnosticReference).HasMaxLength(64);
            audit.HasIndex(value => value.OccurredAtUtc);
        });
    }

    private static void ConfigureDiagnosticRecord(ModelBuilder builder)
    {
        builder.Entity<DiagnosticRecord>(diagnostic =>
        {
            diagnostic.ToTable(
                "DiagnosticRecords",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_DiagnosticRecords_Reference_Length",
                        """length("Reference") BETWEEN 1 AND 64""");
                    table.HasCheckConstraint(
                        "CK_DiagnosticRecords_Severity_Length",
                        """length("Severity") BETWEEN 1 AND 32""");
                    table.HasCheckConstraint(
                        "CK_DiagnosticRecords_Category_Length",
                        """length("Category") BETWEEN 1 AND 64""");
                    table.HasCheckConstraint(
                        "CK_DiagnosticRecords_ErrorCode_Length",
                        """length("ErrorCode") BETWEEN 1 AND 64""");
                    table.HasCheckConstraint(
                        "CK_DiagnosticRecords_Operation_Length",
                        """length("Operation") BETWEEN 1 AND 100""");
                    table.HasCheckConstraint(
                        "CK_DiagnosticRecords_ExceptionType_Length",
                        """
                        "ExceptionType" IS NULL
                        OR length("ExceptionType") BETWEEN 1 AND 512
                        """);
                });
            diagnostic.HasKey(value => value.Id);
            diagnostic.Property(value => value.Reference).HasMaxLength(64).IsRequired();
            diagnostic.Property(value => value.Severity).HasMaxLength(32).IsRequired();
            diagnostic.Property(value => value.Category).HasMaxLength(64).IsRequired();
            diagnostic.Property(value => value.ErrorCode).HasMaxLength(64).IsRequired();
            diagnostic.Property(value => value.Operation).HasMaxLength(100).IsRequired();
            diagnostic.Property(value => value.ExceptionType).HasMaxLength(512);
            diagnostic.HasIndex(value => value.Reference).IsUnique();
            diagnostic.HasIndex(value => value.OccurredAtUtc);
        });
    }

    private static void ConfigureAnnouncement(ModelBuilder builder)
    {
        builder.Entity<Announcement>(announcement =>
        {
            announcement.ToTable(
                "Announcements",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_Announcements_Title_Length",
                        """length(trim("Title")) BETWEEN 1 AND 200""");
                    table.HasCheckConstraint(
                        "CK_Announcements_Body_Length",
                        """length("Body") BETWEEN 1 AND 10000""");
                    table.HasCheckConstraint(
                        "CK_Announcements_Status",
                        "\"Status\" IN ('Draft', 'Archived')");
                    table.HasCheckConstraint(
                        "CK_Announcements_Timestamps",
                        "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                    table.HasCheckConstraint(
                        "CK_Announcements_Revision",
                        "\"Revision\" >= 1");
                });
            announcement.HasKey(value => value.Id);
            announcement.Property(value => value.Id).ValueGeneratedNever();
            announcement.Property(value => value.Title).HasMaxLength(200).IsRequired();
            announcement.Property(value => value.MessageContent)
                .HasColumnName("Body")
                .HasMaxLength(10_000)
                .IsRequired();
            announcement
                .Property(value => value.Status)
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();
            announcement
                .Property(value => value.CreatedAtUtc)
                .HasConversion(
                    value => value.ToUnixTimeMilliseconds(),
                    value => DateTimeOffset.FromUnixTimeMilliseconds(value))
                .IsRequired();
            announcement
                .Property(value => value.UpdatedAtUtc)
                .HasConversion(
                    value => value.ToUnixTimeMilliseconds(),
                    value => DateTimeOffset.FromUnixTimeMilliseconds(value))
                .IsRequired();
            announcement.Property(value => value.CreatedByUserId).IsRequired();
            announcement.Property(value => value.UpdatedByUserId).IsRequired();
            announcement.Property(value => value.Revision).IsConcurrencyToken();
            announcement
                .HasIndex(value => new { value.Status, value.UpdatedAtUtc })
                .IsDescending(false, true);
            announcement
                .HasIndex(value => value.UpdatedAtUtc)
                .IsDescending(true);
        });
    }

    private static void ConfigureAnnouncementMedia(ModelBuilder builder)
    {
        builder.Entity<AnnouncementMediaAsset>(media =>
        {
            media.ToTable(
                "AnnouncementMediaAssets",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_AnnouncementMediaAssets_ByteLength",
                        "\"ByteLength\" BETWEEN 1 AND 8388608");
                    table.HasCheckConstraint(
                        "CK_AnnouncementMediaAssets_ContentType",
                        "\"ContentType\" IN ('image/jpeg','image/png','image/webp','image/gif')");
                    table.HasCheckConstraint(
                        "CK_AnnouncementMediaAssets_Presentation",
                        "\"Presentation\" IN ('Attachment','FeaturedImage')");
                    table.HasCheckConstraint(
                        "CK_AnnouncementMediaAssets_Revision",
                        "\"Revision\" >= 1");
                    table.HasCheckConstraint(
                        "CK_AnnouncementMediaAssets_SortOrder",
                        "\"SortOrder\" BETWEEN 0 AND 3");
                    table.HasCheckConstraint(
                        "CK_AnnouncementMediaAssets_Timestamps",
                        "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                });
            media.HasKey(value => value.Id);
            media.Property(value => value.Id).ValueGeneratedNever();
            media.Property(value => value.ProtectedContent)
                .HasMaxLength(AnnouncementMediaProtector.MaximumCiphertextBytes)
                .IsRequired();
            media.Property(value => value.ContentType).HasMaxLength(32).IsRequired();
            media.Property(value => value.Sha256Digest).HasMaxLength(32).IsRequired();
            media.Property(value => value.GeneratedFileName).HasMaxLength(80).IsRequired();
            media.Property(value => value.AltText)
                .HasMaxLength(AnnouncementMediaAsset.MaximumAltTextLength);
            media.Property(value => value.Presentation)
                .HasConversion<string>()
                .HasMaxLength(24)
                .IsRequired();
            media.Property(value => value.CreatedAtUtc)
                .HasConversion(
                    value => value.ToUnixTimeMilliseconds(),
                    value => DateTimeOffset.FromUnixTimeMilliseconds(value))
                .IsRequired();
            media.Property(value => value.UpdatedAtUtc)
                .HasConversion(
                    value => value.ToUnixTimeMilliseconds(),
                    value => DateTimeOffset.FromUnixTimeMilliseconds(value))
                .IsRequired();
            media.Property(value => value.Revision).IsConcurrencyToken();
            media.HasOne(value => value.Announcement)
                .WithMany(value => value.Media)
                .HasForeignKey(value => value.AnnouncementId)
                .OnDelete(DeleteBehavior.Cascade);
            // Ordering is enforced transactionally by AnnouncementService. A unique
            // SQLite index makes a valid 0/1 swap fail during intermediate updates.
            media.HasIndex(value => new { value.AnnouncementId, value.SortOrder });
        });
    }

    private static void ConfigureDiscord(ModelBuilder builder)
    {
        builder.Entity<DiscordConnection>(connection =>
        {
            connection.ToTable(
                "DiscordConnections",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_DiscordConnections_Name_Length",
                        "length(trim(\"Name\")) BETWEEN 1 AND 100");
                    table.HasCheckConstraint(
                        "CK_DiscordConnections_ApplicationId",
                        "length(\"ApplicationId\") BETWEEN 1 AND 20 AND \"ApplicationId\" NOT GLOB '*[^0-9]*'");
                    table.HasCheckConstraint(
                        "CK_DiscordConnections_BotUserId",
                        "length(\"BotUserId\") BETWEEN 1 AND 20 AND \"BotUserId\" NOT GLOB '*[^0-9]*'");
                    table.HasCheckConstraint(
                        "CK_DiscordConnections_BotUsername_Length",
                        "length(\"BotUsernameSnapshot\") BETWEEN 1 AND 100");
                    table.HasCheckConstraint(
                        "CK_DiscordConnections_Timestamps",
                        "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                    table.HasCheckConstraint(
                        "CK_DiscordConnections_Revision",
                        "\"Revision\" >= 1");
                });
            connection.HasKey(value => value.Id);
            connection.Property(value => value.Id).ValueGeneratedNever();
            connection.Property(value => value.Name).HasMaxLength(100).IsRequired();
            connection.Property(value => value.ApplicationId).HasMaxLength(20).IsRequired();
            connection.Property(value => value.BotUserId).HasMaxLength(20).IsRequired();
            connection.Property(value => value.BotUsernameSnapshot).HasMaxLength(100).IsRequired();
            connection.Property(value => value.Revision).IsConcurrencyToken();
            ConfigureUnixTimestamp(connection.Property(value => value.CreatedAtUtc));
            ConfigureUnixTimestamp(connection.Property(value => value.UpdatedAtUtc));
            connection.HasIndex(value => value.Name);
            connection.HasIndex(value => value.ProtectedSecretId).IsUnique();
        });

        builder.Entity<DiscordDestination>(destination =>
        {
            destination.ToTable(
                "DiscordDestinations",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_DiscordDestinations_GuildId",
                        "length(\"GuildId\") BETWEEN 1 AND 20 AND \"GuildId\" NOT GLOB '*[^0-9]*'");
                    table.HasCheckConstraint(
                        "CK_DiscordDestinations_ChannelId",
                        "length(\"ChannelId\") BETWEEN 1 AND 20 AND \"ChannelId\" NOT GLOB '*[^0-9]*'");
                    table.HasCheckConstraint(
                        "CK_DiscordDestinations_GuildName_Length",
                        "length(\"GuildNameSnapshot\") BETWEEN 1 AND 100");
                    table.HasCheckConstraint(
                        "CK_DiscordDestinations_ChannelName_Length",
                        "length(\"ChannelNameSnapshot\") BETWEEN 1 AND 100");
                    table.HasCheckConstraint(
                        "CK_DiscordDestinations_ChannelType",
                        "\"ChannelType\" IN (0, 5)");
                    table.HasCheckConstraint(
                        "CK_DiscordDestinations_Timestamps",
                        "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                    table.HasCheckConstraint(
                        "CK_DiscordDestinations_Revision",
                        "\"Revision\" >= 1");
                });
            destination.HasKey(value => value.Id);
            destination.Property(value => value.Id).ValueGeneratedNever();
            destination.Property(value => value.GuildId).HasMaxLength(20).IsRequired();
            destination.Property(value => value.GuildNameSnapshot).HasMaxLength(100).IsRequired();
            destination.Property(value => value.ChannelId).HasMaxLength(20).IsRequired();
            destination.Property(value => value.ChannelNameSnapshot).HasMaxLength(100).IsRequired();
            destination.Property(value => value.Revision).IsConcurrencyToken();
            ConfigureUnixTimestamp(destination.Property(value => value.CreatedAtUtc));
            ConfigureUnixTimestamp(destination.Property(value => value.UpdatedAtUtc));
            destination.HasIndex(value => new { value.DiscordConnectionId, value.ChannelId }).IsUnique();
            destination.HasIndex(value => new { value.DiscordConnectionId, value.GuildId, value.Enabled });
            destination
                .HasOne(value => value.Connection)
                .WithMany(value => value.Destinations)
                .HasForeignKey(value => value.DiscordConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureUnixTimestamp(
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<DateTimeOffset> property)
    {
        property
            .HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value))
            .IsRequired();
    }

    private static void ConfigurePublications(ModelBuilder builder)
    {
        builder.Entity<Publication>(publication =>
        {
            publication.ToTable(
                "Publications",
                table =>
                {
                    table.HasCheckConstraint(
                        "CK_Publications_Status",
                        "\"Status\" IN ('Queued','Processing','RetryScheduled','Succeeded','PartiallySucceeded','Failed','Cancelling','Cancelled')");
                    table.HasCheckConstraint("CK_Publications_Counts", "\"TotalDeliveryCount\" BETWEEN 1 AND 10 AND \"SuccessfulDeliveryCount\" >= 0 AND \"FailedDeliveryCount\" >= 0 AND \"CancelledDeliveryCount\" >= 0 AND \"SuccessfulDeliveryCount\" + \"FailedDeliveryCount\" + \"CancelledDeliveryCount\" <= \"TotalDeliveryCount\"");
                    table.HasCheckConstraint("CK_Publications_Revision", "\"Revision\" >= 1");
                });
            publication.HasKey(value => value.Id);
            publication.Property(value => value.Id).ValueGeneratedNever();
            publication.Property(value => value.Provider).HasConversion<string>().HasMaxLength(16);
            publication.Property(value => value.Status).HasConversion<string>().HasMaxLength(32);
            ConfigureUnixTimestamp(publication.Property(value => value.RequestedAtUtc));
            ConfigureUnixTimestamp(publication.Property(value => value.UpdatedAtUtc));
            ConfigureOptionalUnixTimestamp(publication.Property(value => value.CancellationRequestedAtUtc));
            publication.Property(value => value.Revision).IsConcurrencyToken();
            publication.HasIndex(value => value.SubmissionId).IsUnique();
            publication.HasIndex(value => new { value.Status, value.UpdatedAtUtc }).IsDescending(false, true);
            publication.HasIndex(value => value.RequestedAtUtc).IsDescending(true);
            publication
                .HasOne<Announcement>()
                .WithMany()
                .HasForeignKey(value => value.AnnouncementId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<PublicationDelivery>(delivery =>
        {
            delivery.ToTable(
                "PublicationDeliveries",
                table =>
                {
                    table.HasCheckConstraint("CK_PublicationDeliveries_Status", "\"Status\" IN ('Queued','Leased','RetryScheduled','Succeeded','FailedPermanent','Cancelled')");
                    table.HasCheckConstraint("CK_PublicationDeliveries_Attempts", "\"AttemptCount\" BETWEEN 0 AND 4");
                    table.HasCheckConstraint("CK_PublicationDeliveries_Revision", "\"Revision\" >= 1");
                    table.HasCheckConstraint("CK_PublicationDeliveries_Lease", "(\"Status\" = 'Leased' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseExpiresAtUtc\" IS NOT NULL) OR (\"Status\" <> 'Leased' AND \"LeaseOwner\" IS NULL AND \"LeaseExpiresAtUtc\" IS NULL)");
                });
            delivery.HasKey(value => value.Id);
            delivery.Property(value => value.Id).ValueGeneratedNever();
            delivery.Property(value => value.ProviderDestinationId).HasMaxLength(20).IsRequired();
            delivery.Property(value => value.ServerNameSnapshot).HasMaxLength(100).IsRequired();
            delivery.Property(value => value.ChannelNameSnapshot).HasMaxLength(100).IsRequired();
            delivery.Property(value => value.Status).HasConversion<string>().HasMaxLength(32);
            delivery.Property(value => value.LeaseOwner).HasMaxLength(64);
            delivery.Property(value => value.StableNonce).HasMaxLength(25).IsRequired();
            delivery.Property(value => value.LastSafeOutcome).HasMaxLength(64);
            delivery.Property(value => value.ExternalMessageId).HasMaxLength(20);
            ConfigureUnixTimestamp(delivery.Property(value => value.NextAttemptAtUtc));
            ConfigureOptionalUnixTimestamp(delivery.Property(value => value.LeaseExpiresAtUtc));
            ConfigureOptionalUnixTimestamp(delivery.Property(value => value.StartedAtUtc));
            ConfigureOptionalUnixTimestamp(delivery.Property(value => value.CompletedAtUtc));
            delivery.Property(value => value.Revision).IsConcurrencyToken();
            delivery.HasIndex(value => new { value.PublicationId, value.LocalDestinationId }).IsUnique();
            delivery.HasIndex(value => new { value.Status, value.NextAttemptAtUtc });
            delivery.HasIndex(value => new { value.Status, value.LeaseExpiresAtUtc });
            delivery.HasOne(value => value.Publication).WithMany(value => value.Deliveries)
                .HasForeignKey(value => value.PublicationId).OnDelete(DeleteBehavior.Cascade);
            delivery.HasOne<DiscordDestination>().WithMany().HasForeignKey(value => value.LocalDestinationId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<PublicationAttempt>(attempt =>
        {
            attempt.ToTable("PublicationAttempts");
            attempt.HasKey(value => value.Id);
            attempt.Property(value => value.Id).ValueGeneratedNever();
            attempt.Property(value => value.SafeOutcome).HasMaxLength(64).IsRequired();
            attempt.Property(value => value.ExternalMessageId).HasMaxLength(20);
            attempt.Property(value => value.DiagnosticReference).HasMaxLength(64);
            ConfigureUnixTimestamp(attempt.Property(value => value.StartedAtUtc));
            ConfigureOptionalUnixTimestamp(attempt.Property(value => value.CompletedAtUtc));
            ConfigureOptionalUnixTimestamp(attempt.Property(value => value.RetryScheduledForUtc));
            attempt.HasIndex(value => new { value.PublicationDeliveryId, value.AttemptNumber }).IsUnique();
            attempt.HasOne(value => value.Delivery).WithMany(value => value.Attempts)
                .HasForeignKey(value => value.PublicationDeliveryId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PublicationPayload>(payload =>
        {
            payload.ToTable("PublicationPayloads", table => table.HasCheckConstraint(
                "CK_PublicationPayloads_Size",
                $"\"PlaintextSize\" BETWEEN 1 AND {PublicationPayloadProtector.MaximumPlaintextBytes} AND length(\"Ciphertext\") BETWEEN 1 AND {PublicationPayloadProtector.MaximumCiphertextBytes}"));
            payload.HasKey(value => value.PublicationId);
            payload.Property(value => value.Ciphertext).IsRequired();
            ConfigureUnixTimestamp(payload.Property(value => value.CreatedAtUtc));
            payload.HasOne(value => value.Publication).WithOne().HasForeignKey<PublicationPayload>(value => value.PublicationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureOptionalUnixTimestamp(
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<DateTimeOffset?> property)
    {
        property.HasConversion(
            value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : (long?)null,
            value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);
    }

    private void RejectAuditMutation()
    {
        bool hasUnsupportedMutation = ChangeTracker
            .Entries<AuditRecord>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted);

        if (hasUnsupportedMutation)
        {
            throw new InvalidOperationException(
                "Audit records cannot be updated or deleted through supported application operations.");
        }
    }

    private void AdvanceRevisionTokens()
    {
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State == EntityState.Modified))
        {
            if (entry.Entity is Announcement or DiscordConnection or DiscordDestination)
            {
                continue;
            }

            var revisionMetadata = entry.Metadata.FindProperty("Revision");
            if (revisionMetadata is null
                || revisionMetadata.ClrType != typeof(long)
                || !revisionMetadata.IsConcurrencyToken)
            {
                continue;
            }

            var revision = entry.Property("Revision");
            long originalRevision = (long)(revision.OriginalValue ?? 0L);
            revision.CurrentValue = checked(originalRevision + 1);
        }
    }

    private static InstallationState CreateInstallationStateSeed()
    {
        return (InstallationState)Activator.CreateInstance(typeof(InstallationState), nonPublic: true)!;
    }
}
