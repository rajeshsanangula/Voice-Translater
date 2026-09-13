using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VTTranslate.Backend.Infrastructure.Persistence.EfCore.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslationSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "translation_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientSessionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastActivityAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TerminalAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Direction = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_translation_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_translation_sessions_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_translation_sessions_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_translation_sessions_account",
                table: "translation_sessions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "ix_translation_sessions_account_client_active_unique",
                table: "translation_sessions",
                columns: new[] { "AccountId", "ClientSessionId" },
                unique: true,
                filter: "\"State\" = 'Active' AND \"ClientSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_translation_sessions_DeviceId",
                table: "translation_sessions",
                column: "DeviceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "translation_sessions");
        }
    }
}
