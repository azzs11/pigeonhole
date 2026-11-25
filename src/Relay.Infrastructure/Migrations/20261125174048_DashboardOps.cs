using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DashboardOps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_health",
                columns: table => new
                {
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    circuit_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provider_health", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_switches",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    down_providers = table.Column<string[]>(type: "text[]", nullable: false),
                    delay_milliseconds = table.Column<int>(type: "integer", nullable: false),
                    crash_rate = table.Column<double>(type: "double precision", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_switches", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_health");

            migrationBuilder.DropTable(
                name: "sandbox_switches");
        }
    }
}
