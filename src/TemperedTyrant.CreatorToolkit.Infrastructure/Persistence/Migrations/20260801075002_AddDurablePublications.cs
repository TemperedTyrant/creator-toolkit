using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1825, CA1861 // EF-generated migration arguments are invoked once

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddDurablePublications : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Publications",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                AnnouncementId = table.Column<Guid>(type: "TEXT", nullable: true),
                AnnouncementRevision = table.Column<long>(type: "INTEGER", nullable: false),
                Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                SubmissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                RequestedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                RequestedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                CancellationRequestedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                TotalDeliveryCount = table.Column<int>(type: "INTEGER", nullable: false),
                SuccessfulDeliveryCount = table.Column<int>(type: "INTEGER", nullable: false),
                FailedDeliveryCount = table.Column<int>(type: "INTEGER", nullable: false),
                CancelledDeliveryCount = table.Column<int>(type: "INTEGER", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Publications", x => x.Id);
                table.CheckConstraint("CK_Publications_Counts", "\"TotalDeliveryCount\" BETWEEN 1 AND 10 AND \"SuccessfulDeliveryCount\" >= 0 AND \"FailedDeliveryCount\" >= 0 AND \"CancelledDeliveryCount\" >= 0 AND \"SuccessfulDeliveryCount\" + \"FailedDeliveryCount\" + \"CancelledDeliveryCount\" <= \"TotalDeliveryCount\"");
                table.CheckConstraint("CK_Publications_Revision", "\"Revision\" >= 1");
                table.CheckConstraint("CK_Publications_Status", "\"Status\" IN ('Queued','Processing','RetryScheduled','Succeeded','PartiallySucceeded','Failed','Cancelling','Cancelled')");
                table.ForeignKey(
                    name: "FK_Publications_Announcements_AnnouncementId",
                    column: x => x.AnnouncementId,
                    principalTable: "Announcements",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "PublicationDeliveries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PublicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                LocalDestinationId = table.Column<Guid>(type: "TEXT", nullable: true),
                ProviderDestinationId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                ServerNameSnapshot = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                ChannelNameSnapshot = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                NextAttemptAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                LeaseExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                StableNonce = table.Column<string>(type: "TEXT", maxLength: 25, nullable: false),
                LastSafeOutcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                ExternalMessageId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                StartedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                CompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                Revision = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicationDeliveries", x => x.Id);
                table.CheckConstraint("CK_PublicationDeliveries_Attempts", "\"AttemptCount\" BETWEEN 0 AND 4");
                table.CheckConstraint("CK_PublicationDeliveries_Lease", "(\"Status\" = 'Leased' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseExpiresAtUtc\" IS NOT NULL) OR (\"Status\" <> 'Leased' AND \"LeaseOwner\" IS NULL AND \"LeaseExpiresAtUtc\" IS NULL)");
                table.CheckConstraint("CK_PublicationDeliveries_Revision", "\"Revision\" >= 1");
                table.CheckConstraint("CK_PublicationDeliveries_Status", "\"Status\" IN ('Queued','Leased','RetryScheduled','Succeeded','FailedPermanent','Cancelled')");
                table.ForeignKey(
                    name: "FK_PublicationDeliveries_DiscordDestinations_LocalDestinationId",
                    column: x => x.LocalDestinationId,
                    principalTable: "DiscordDestinations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_PublicationDeliveries_Publications_PublicationId",
                    column: x => x.PublicationId,
                    principalTable: "Publications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PublicationPayloads",
            columns: table => new
            {
                PublicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                Ciphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
                PlaintextSize = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicationPayloads", x => x.PublicationId);
                table.CheckConstraint("CK_PublicationPayloads_Size", "\"PlaintextSize\" BETWEEN 1 AND 12582912 AND length(\"Ciphertext\") BETWEEN 1 AND 13631488");
                table.ForeignKey(
                    name: "FK_PublicationPayloads_Publications_PublicationId",
                    column: x => x.PublicationId,
                    principalTable: "Publications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PublicationAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                PublicationDeliveryId = table.Column<Guid>(type: "TEXT", nullable: false),
                AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                StartedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                CompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                SafeOutcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                RetryScheduledForUtc = table.Column<long>(type: "INTEGER", nullable: true),
                ExternalMessageId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                DiagnosticReference = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PublicationAttempts", x => x.Id);
                table.ForeignKey(
                    name: "FK_PublicationAttempts_PublicationDeliveries_PublicationDeliveryId",
                    column: x => x.PublicationDeliveryId,
                    principalTable: "PublicationDeliveries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PublicationAttempts_PublicationDeliveryId_AttemptNumber",
            table: "PublicationAttempts",
            columns: new[] { "PublicationDeliveryId", "AttemptNumber" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PublicationDeliveries_LocalDestinationId",
            table: "PublicationDeliveries",
            column: "LocalDestinationId");

        migrationBuilder.CreateIndex(
            name: "IX_PublicationDeliveries_PublicationId_LocalDestinationId",
            table: "PublicationDeliveries",
            columns: new[] { "PublicationId", "LocalDestinationId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PublicationDeliveries_Status_LeaseExpiresAtUtc",
            table: "PublicationDeliveries",
            columns: new[] { "Status", "LeaseExpiresAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_PublicationDeliveries_Status_NextAttemptAtUtc",
            table: "PublicationDeliveries",
            columns: new[] { "Status", "NextAttemptAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Publications_AnnouncementId",
            table: "Publications",
            column: "AnnouncementId");

        migrationBuilder.CreateIndex(
            name: "IX_Publications_RequestedAtUtc",
            table: "Publications",
            column: "RequestedAtUtc",
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "IX_Publications_Status_UpdatedAtUtc",
            table: "Publications",
            columns: new[] { "Status", "UpdatedAtUtc" },
            descending: new[] { false, true });

        migrationBuilder.CreateIndex(
            name: "IX_Publications_SubmissionId",
            table: "Publications",
            column: "SubmissionId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PublicationAttempts");

        migrationBuilder.DropTable(
            name: "PublicationPayloads");

        migrationBuilder.DropTable(
            name: "PublicationDeliveries");

        migrationBuilder.DropTable(
            name: "Publications");
    }
}
