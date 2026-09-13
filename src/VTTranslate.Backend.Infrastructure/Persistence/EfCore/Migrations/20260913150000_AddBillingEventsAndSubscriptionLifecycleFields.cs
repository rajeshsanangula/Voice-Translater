using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VTTranslate.Backend.Infrastructure.Persistence.EfCore;

#nullable disable

namespace VTTranslate.Backend.Infrastructure.Persistence.EfCore.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AutraxisDbContext))]
    [Migration("20260913150000_AddBillingEventsAndSubscriptionLifecycleFields")]
    public partial class AddBillingEventsAndSubscriptionLifecycleFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscriptions_account",
                table: "subscriptions");

            migrationBuilder.AddColumn<bool>(
                name: "CancelAtPeriodEnd",
                table: "subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastBillingEventAt",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastBillingEventPrecedence",
                table: "subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LocallyCancelledAt",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "billing_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProviderEventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProcessingStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RawPayloadHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_billing_events_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_billing_events_subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_account_live_unique",
                table: "subscriptions",
                column: "AccountId",
                unique: true,
                filter: "\"Status\" IN ('Trial','Active','PastDue','GracePeriod')");

            migrationBuilder.CreateIndex(
                name: "ix_billing_events_provider_event_unique",
                table: "billing_events",
                columns: new[] { "Provider", "ProviderEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_billing_events_account_time",
                table: "billing_events",
                columns: new[] { "AccountId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_billing_events_SubscriptionId",
                table: "billing_events",
                column: "SubscriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_events");

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_account_live_unique",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "CancelAtPeriodEnd",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "LastBillingEventAt",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "LastBillingEventPrecedence",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "LocallyCancelledAt",
                table: "subscriptions");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_account",
                table: "subscriptions",
                column: "AccountId",
                unique: true);
        }
    }
}
