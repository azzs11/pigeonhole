using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QuotasSuppressionWebhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_provider_name_provider_message_id",
                table: "messages");

            migrationBuilder.AddColumn<int>(
                name: "daily_quota",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 10000);

            migrationBuilder.AddColumn<string>(
                name: "pause_reason",
                table: "tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "quota_day",
                table: "tenants",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "quota_used",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sending_paused_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "provider_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_messages_provider_message_id",
                table: "messages",
                column: "provider_message_id");

            migrationBuilder.CreateIndex(
                name: "ux_provider_events_provider_event_id",
                table: "provider_events",
                columns: new[] { "provider", "event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_events");

            migrationBuilder.DropIndex(
                name: "ix_messages_provider_message_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "daily_quota",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "pause_reason",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "quota_day",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "quota_used",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "sending_paused_at",
                table: "tenants");

            migrationBuilder.CreateIndex(
                name: "ix_messages_provider_name_provider_message_id",
                table: "messages",
                columns: new[] { "provider_name", "provider_message_id" });
        }
    }
}
