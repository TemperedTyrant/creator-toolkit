using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1825 // Preserve EF-generated empty array arguments
#pragma warning disable CA1861 // EF-generated migration arguments are invoked once
#pragma warning disable IDE0161 // Preserve the EF-generated namespace shape

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAnnouncements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Announcements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 10000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Announcements", x => x.Id);
                    table.CheckConstraint("CK_Announcements_Body_Length", "length(\"Body\") BETWEEN 1 AND 10000");
                    table.CheckConstraint("CK_Announcements_Revision", "\"Revision\" >= 1");
                    table.CheckConstraint("CK_Announcements_Status", "\"Status\" IN ('Draft', 'Archived')");
                    table.CheckConstraint("CK_Announcements_Timestamps", "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_Announcements_Title_Length", "length(trim(\"Title\")) BETWEEN 1 AND 200");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_Status_UpdatedAtUtc",
                table: "Announcements",
                columns: new[] { "Status", "UpdatedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_UpdatedAtUtc",
                table: "Announcements",
                column: "UpdatedAtUtc",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Announcements");
        }
    }
}
