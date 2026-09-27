using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.Sqlite.Migrations
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
                    ProcessId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    InstanceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Roles = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RulesVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    FirewallVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    SettingsVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    EstimatedCacheBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    EntryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Hits = table.Column<long>(type: "INTEGER", nullable: false),
                    Misses = table.Column<long>(type: "INTEGER", nullable: false),
                    ReloadFailures = table.Column<long>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuration_process_statuses", x => x.ProcessId);
                });

            migrationBuilder.CreateTable(
                name: "configuration_versions",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_configuration_versions", x => x.Scope);
                });

            migrationBuilder.CreateIndex(
                name: "IX_configuration_process_statuses_LastSeenAt",
                table: "configuration_process_statuses",
                column: "LastSeenAt");

            migrationBuilder.Sql("""
                INSERT INTO configuration_versions ("Scope", "Version", "UpdatedAt") VALUES
                ('rules', 0, 0), ('firewall', 0, 0), ('settings', 0, 0);
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
