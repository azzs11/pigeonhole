using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentSendGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "agent_run_id",
                table: "messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                table: "messages",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "run_id",
                table: "api_keys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    trusted_recipients = table.Column<string[]>(type: "text[]", nullable: false),
                    policy = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_runs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    risk_score = table.Column<int>(type: "integer", nullable: false),
                    reasons = table.Column<string[]>(type: "text[]", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approvals", x => x.id);
                    table.ForeignKey(
                        name: "fk_approvals_agent_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "agent_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_approvals_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guard_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    risk_score = table.Column<int>(type: "integer", nullable: false),
                    reasons = table.Column<string[]>(type: "text[]", nullable: false),
                    actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guard_audit", x => x.id);
                    table.ForeignKey(
                        name: "fk_guard_audit_agent_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "agent_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_messages_agent_run_id",
                table: "messages",
                column: "agent_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_tenant_id_content_hash",
                table: "messages",
                columns: new[] { "tenant_id", "content_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_tenant_id",
                table: "agent_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_message_id",
                table: "approvals",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_run_id",
                table: "approvals",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_tenant_id_status_expires_at",
                table: "approvals",
                columns: new[] { "tenant_id", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_guard_audit_run_id_at",
                table: "guard_audit",
                columns: new[] { "run_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approvals");

            migrationBuilder.DropTable(
                name: "guard_audit");

            migrationBuilder.DropTable(
                name: "agent_runs");

            migrationBuilder.DropIndex(
                name: "ix_messages_agent_run_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_tenant_id_content_hash",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "agent_run_id",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "content_hash",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "run_id",
                table: "api_keys");
        }
    }
}
