using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional
#pragma warning disable CA1861 // EF-generated migration arguments are invoked once
#pragma warning disable IDE0161 // Preserve the EF-generated namespace shape

namespace TemperedTyrant.CreatorToolkit.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialIdentityAndSecurityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                    table.CheckConstraint("CK_AspNetRoles_Name_Length", "\"Name\" IS NULL OR length(\"Name\") <= 256");
                    table.CheckConstraint("CK_AspNetRoles_NormalizedName_Length", "\"NormalizedName\" IS NULL OR length(\"NormalizedName\") <= 256");
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActivatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                    table.CheckConstraint("CK_AspNetUsers_DisplayName_Length", "length(\"DisplayName\") BETWEEN 1 AND 200");
                    table.CheckConstraint("CK_AspNetUsers_Email_Length", "\"Email\" IS NULL OR length(\"Email\") <= 256");
                    table.CheckConstraint("CK_AspNetUsers_NormalizedEmail_Length", "\"NormalizedEmail\" IS NULL OR length(\"NormalizedEmail\") <= 256");
                    table.CheckConstraint("CK_AspNetUsers_NormalizedUserName_Length", "\"NormalizedUserName\" IS NULL OR length(\"NormalizedUserName\") <= 256");
                    table.CheckConstraint("CK_AspNetUsers_UserName_Length", "\"UserName\" IS NULL OR length(\"UserName\") <= 256");
                });

            migrationBuilder.CreateTable(
                name: "AuditRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EventCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DiagnosticReference = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.Id);
                    table.CheckConstraint("CK_AuditRecords_DiagnosticReference_Length", "\"DiagnosticReference\" IS NULL\nOR length(\"DiagnosticReference\") BETWEEN 1 AND 64");
                    table.CheckConstraint("CK_AuditRecords_EventCode_Length", "length(\"EventCode\") BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_AuditRecords_Outcome_Length", "length(\"Outcome\") BETWEEN 1 AND 32");
                    table.CheckConstraint("CK_AuditRecords_ReasonCode_Length", "\"ReasonCode\" IS NULL OR length(\"ReasonCode\") BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ExceptionType = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticRecords", x => x.Id);
                    table.CheckConstraint("CK_DiagnosticRecords_Category_Length", "length(\"Category\") BETWEEN 1 AND 64");
                    table.CheckConstraint("CK_DiagnosticRecords_ErrorCode_Length", "length(\"ErrorCode\") BETWEEN 1 AND 64");
                    table.CheckConstraint("CK_DiagnosticRecords_ExceptionType_Length", "\"ExceptionType\" IS NULL\nOR length(\"ExceptionType\") BETWEEN 1 AND 512");
                    table.CheckConstraint("CK_DiagnosticRecords_Operation_Length", "length(\"Operation\") BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_DiagnosticRecords_Reference_Length", "length(\"Reference\") BETWEEN 1 AND 64");
                    table.CheckConstraint("CK_DiagnosticRecords_Severity_Length", "length(\"Severity\") BETWEEN 1 AND 32");
                });

            migrationBuilder.CreateTable(
                name: "InstallationStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    InitializedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstallationStates", x => x.Id);
                    table.CheckConstraint("CK_InstallationStates_Revision", "Revision >= 0");
                    table.CheckConstraint("CK_InstallationStates_Singleton", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "ProtectedSecrets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Ciphertext = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtectedSecrets", x => x.Id);
                    table.CheckConstraint("CK_ProtectedSecrets_Ciphertext", "length(\"Ciphertext\") > 0");
                    table.CheckConstraint("CK_ProtectedSecrets_Purpose", "length(\"Purpose\") BETWEEN 1 AND 128\nAND \"Purpose\" NOT GLOB '*[^A-Za-z0-9._:-]*'");
                    table.CheckConstraint("CK_ProtectedSecrets_Revision", "Revision >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                    table.CheckConstraint("CK_Workspaces_Revision", "Revision >= 0");
                    table.CheckConstraint("CK_Workspaces_Singleton", "Id = 1");
                    table.CheckConstraint("CK_Workspaces_TimeZoneId_Length", "length(\"TimeZoneId\") BETWEEN 1 AND 255");
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityCapabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActiveSlot = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityCapabilities", x => x.Id);
                    table.CheckConstraint("CK_SecurityCapabilities_ActiveSlot_State", "\"ActiveSlot\" IS NULL\nOR (\n    length(\"ActiveSlot\") BETWEEN 1 AND 128\n    AND \"UsedAtUtc\" IS NULL\n    AND \"RevokedAtUtc\" IS NULL\n)");
                    table.CheckConstraint("CK_SecurityCapabilities_Lifetime", "julianday(\"CreatedAtUtc\") IS NOT NULL\nAND julianday(\"ExpiresAtUtc\") IS NOT NULL\nAND julianday(\"ExpiresAtUtc\") > julianday(\"CreatedAtUtc\")");
                    table.CheckConstraint("CK_SecurityCapabilities_Purpose", "\"Purpose\" IN ('BootstrapOwner', 'ActivateUser', 'RecoverOwner')");
                    table.CheckConstraint("CK_SecurityCapabilities_Revision", "Revision >= 0");
                    table.CheckConstraint("CK_SecurityCapabilities_RevocationTime", "\"RevokedAtUtc\" IS NULL\nOR (\n    julianday(\"RevokedAtUtc\") IS NOT NULL\n    AND julianday(\"RevokedAtUtc\") >= julianday(\"CreatedAtUtc\")\n)");
                    table.CheckConstraint("CK_SecurityCapabilities_TerminalState", "\"UsedAtUtc\" IS NULL OR \"RevokedAtUtc\" IS NULL");
                    table.CheckConstraint("CK_SecurityCapabilities_TokenHash_Length", "length(\"TokenHash\") = 32");
                    table.CheckConstraint("CK_SecurityCapabilities_UseTime", "\"UsedAtUtc\" IS NULL\nOR (\n    julianday(\"UsedAtUtc\") IS NOT NULL\n    AND julianday(\"UsedAtUtc\") >= julianday(\"CreatedAtUtc\")\n)");
                    table.ForeignKey(
                        name: "FK_SecurityCapabilities_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SecurityCapabilities_AspNetUsers_SubjectUserId",
                        column: x => x.SubjectUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Ownerships",
                columns: table => new
                {
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransferredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ownerships", x => x.WorkspaceId);
                    table.CheckConstraint("CK_Ownerships_Revision", "Revision >= 0");
                    table.ForeignKey(
                        name: "FK_Ownerships_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ownerships_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a01"), "owner", "Owner", "OWNER" },
                    { new Guid("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a02"), "admin", "Admin", "ADMIN" },
                    { new Guid("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a03"), "editor", "Editor", "EDITOR" },
                    { new Guid("0197fd3c-5a8d-7a20-b52b-5bd4a7dd7a04"), "viewer", "Viewer", "VIEWER" }
                });

            migrationBuilder.InsertData(
                table: "InstallationStates",
                columns: new[] { "Id", "InitializedAtUtc", "Revision" },
                values: new object[] { 1, null, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_OccurredAtUtc",
                table: "AuditRecords",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticRecords_OccurredAtUtc",
                table: "DiagnosticRecords",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticRecords_Reference",
                table: "DiagnosticRecords",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ownerships_OwnerUserId",
                table: "Ownerships",
                column: "OwnerUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityCapabilities_CreatedByUserId",
                table: "SecurityCapabilities",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityCapabilities_Purpose_ActiveSlot",
                table: "SecurityCapabilities",
                columns: new[] { "Purpose", "ActiveSlot" },
                unique: true,
                filter: "\"ActiveSlot\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityCapabilities_SubjectUserId",
                table: "SecurityCapabilities",
                column: "SubjectUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityCapabilities_TokenHash",
                table: "SecurityCapabilities",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditRecords");

            migrationBuilder.DropTable(
                name: "DiagnosticRecords");

            migrationBuilder.DropTable(
                name: "InstallationStates");

            migrationBuilder.DropTable(
                name: "Ownerships");

            migrationBuilder.DropTable(
                name: "ProtectedSecrets");

            migrationBuilder.DropTable(
                name: "SecurityCapabilities");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}
