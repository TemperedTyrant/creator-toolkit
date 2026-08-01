using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddAnnouncementMedia : Migration
{
    private static readonly string[] AnnouncementOrderColumns = ["AnnouncementId", "SortOrder"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
                name: "AnnouncementMediaAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnnouncementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtectedContent = table.Column<byte[]>(type: "BLOB", maxLength: 9437184, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ByteLength = table.Column<int>(type: "INTEGER", nullable: false),
                    Sha256Digest = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    GeneratedFileName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    AltText = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    IsSpoiler = table.Column<bool>(type: "INTEGER", nullable: false),
                    Presentation = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnouncementMediaAssets", x => x.Id);
                    table.CheckConstraint("CK_AnnouncementMediaAssets_ByteLength", "\"ByteLength\" BETWEEN 1 AND 8388608");
                    table.CheckConstraint("CK_AnnouncementMediaAssets_ContentType", "\"ContentType\" IN ('image/jpeg','image/png','image/webp','image/gif')");
                    table.CheckConstraint("CK_AnnouncementMediaAssets_Presentation", "\"Presentation\" IN ('Attachment','FeaturedImage')");
                    table.CheckConstraint("CK_AnnouncementMediaAssets_Revision", "\"Revision\" >= 1");
                    table.CheckConstraint("CK_AnnouncementMediaAssets_SortOrder", "\"SortOrder\" BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_AnnouncementMediaAssets_Timestamps", "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_AnnouncementMediaAssets_Announcements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

        migrationBuilder.CreateIndex(
            name: "IX_AnnouncementMediaAssets_AnnouncementId_SortOrder",
            table: "AnnouncementMediaAssets",
            columns: AnnouncementOrderColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AnnouncementMediaAssets");
    }
}
