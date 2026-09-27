using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurationCaching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "configuration_process_statuses",
                columns: table => new
                {
                    ProcessId = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false),
                    InstanceName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Roles = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    RulesVersion = table.Column<long>(type: "bigint", nullable: false),
                    FirewallVersion = table.Column<long>(type: "bigint", nullable: false),
                    SettingsVersion = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedCacheBytes = table.Column<long>(type: "bigint", nullable: false),
                    EntryCount = table.Column<int>(type: "int", nullable: false),
                    Hits = table.Column<long>(type: "bigint", nullable: false),
                    Misses = table.Column<long>(type: "bigint", nullable: false),
                    ReloadFailures = table.Column<long>(type: "bigint", nullable: false),
                    LastError = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuration_process_statuses", x => x.ProcessId);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "configuration_versions",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuration_versions", x => x.Scope);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_configuration_process_statuses_LastSeenAt",
                table: "configuration_process_statuses",
                column: "LastSeenAt");

            migrationBuilder.Sql("""
                INSERT INTO configuration_versions (`Scope`, `Version`, `UpdatedAt`) VALUES
                ('rules', 0, CURRENT_TIMESTAMP),
                ('firewall', 0, CURRENT_TIMESTAMP),
                ('settings', 0, CURRENT_TIMESTAMP);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "configuration_process_statuses");

            migrationBuilder.DropTable(
                name: "configuration_versions");
        }
    }
}
