using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pinned_provider",
                table: "outbox",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "internet_message_id",
                table: "messages",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "provider_message_id",
                table: "messages",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_name",
                table: "messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // Backfill existing rows before the unique index. xmin is a system column, so no column is added for it.
            migrationBuilder.Sql("UPDATE messages SET internet_message_id = '<' || replace(id::text, '-', '') || '@relay.test>' WHERE internet_message_id = '';");

            migrationBuilder.CreateIndex(
                name: "ix_messages_internet_message_id",
                table: "messages",
                column: "internet_message_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_provider_name_provider_message_id",
                table: "messages",
                columns: new[] { "provider_name", "provider_message_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_internet_message_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_provider_name_provider_message_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "pinned_provider",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "internet_message_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "provider_message_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "provider_name",
                table: "messages");
        }
    }
}
