using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF-generated migration arguments are invoked once

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddDiscordDestinations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DiscordConnections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                ProtectedSecretId = table.Column<Guid>(type: "TEXT", nullable: false),
                ApplicationId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                BotUserId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                BotUsernameSnapshot = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscordConnections", x => x.Id);
                table.CheckConstraint("CK_DiscordConnections_ApplicationId", "length(\"ApplicationId\") BETWEEN 1 AND 20 AND \"ApplicationId\" NOT GLOB '*[^0-9]*'");
                table.CheckConstraint("CK_DiscordConnections_BotUserId", "length(\"BotUserId\") BETWEEN 1 AND 20 AND \"BotUserId\" NOT GLOB '*[^0-9]*'");
                table.CheckConstraint("CK_DiscordConnections_BotUsername_Length", "length(\"BotUsernameSnapshot\") BETWEEN 1 AND 100");
                table.CheckConstraint("CK_DiscordConnections_Name_Length", "length(trim(\"Name\")) BETWEEN 1 AND 100");
                table.CheckConstraint("CK_DiscordConnections_Revision", "\"Revision\" >= 1");
                table.CheckConstraint("CK_DiscordConnections_Timestamps", "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
            });

        migrationBuilder.CreateTable(
            name: "DiscordDestinations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DiscordConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                GuildId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                GuildNameSnapshot = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                ChannelId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                ChannelNameSnapshot = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                ChannelType = table.Column<int>(type: "INTEGER", nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscordDestinations", x => x.Id);
                table.CheckConstraint("CK_DiscordDestinations_ChannelId", "length(\"ChannelId\") BETWEEN 1 AND 20 AND \"ChannelId\" NOT GLOB '*[^0-9]*'");
                table.CheckConstraint("CK_DiscordDestinations_ChannelName_Length", "length(\"ChannelNameSnapshot\") BETWEEN 1 AND 100");
                table.CheckConstraint("CK_DiscordDestinations_ChannelType", "\"ChannelType\" IN (0, 5)");
                table.CheckConstraint("CK_DiscordDestinations_GuildId", "length(\"GuildId\") BETWEEN 1 AND 20 AND \"GuildId\" NOT GLOB '*[^0-9]*'");
                table.CheckConstraint("CK_DiscordDestinations_GuildName_Length", "length(\"GuildNameSnapshot\") BETWEEN 1 AND 100");
                table.CheckConstraint("CK_DiscordDestinations_Revision", "\"Revision\" >= 1");
                table.CheckConstraint("CK_DiscordDestinations_Timestamps", "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
                table.ForeignKey(
                    name: "FK_DiscordDestinations_DiscordConnections_DiscordConnectionId",
                    column: x => x.DiscordConnectionId,
                    principalTable: "DiscordConnections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DiscordConnections_Name",
            table: "DiscordConnections",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_DiscordConnections_ProtectedSecretId",
            table: "DiscordConnections",
            column: "ProtectedSecretId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_DiscordDestinations_DiscordConnectionId_ChannelId",
            table: "DiscordDestinations",
            columns: new[] { "DiscordConnectionId", "ChannelId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_DiscordDestinations_DiscordConnectionId_GuildId_Enabled",
            table: "DiscordDestinations",
            columns: new[] { "DiscordConnectionId", "GuildId", "Enabled" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DiscordDestinations");

        migrationBuilder.DropTable(
            name: "DiscordConnections");
    }
}
