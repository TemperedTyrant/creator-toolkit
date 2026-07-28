using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TemperedTyrant.CreatorToolkit.Core.Audit;
using TemperedTyrant.CreatorToolkit.Core.Diagnostics;
using TemperedTyrant.CreatorToolkit.Core.Identity;
using TemperedTyrant.CreatorToolkit.Core.Setup;
using TemperedTyrant.CreatorToolkit.Infrastructure.Identity;

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

    public DbSet<DiagnosticRecord> DiagnosticRecords => Set<DiagnosticRecord>();

    internal DbSet<ProtectedSecretRecord> ProtectedSecrets => Set<ProtectedSecretRecord>();

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
