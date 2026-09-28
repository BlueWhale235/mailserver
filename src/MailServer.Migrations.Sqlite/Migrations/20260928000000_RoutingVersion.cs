using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MailServer.Migrations.Sqlite.Migrations;

[DbContext(typeof(MailDbContext))]
[Migration("20260928000000_RoutingVersion")]
public sealed class RoutingVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "RoutingVersion", table: "configuration_process_statuses",
            type: "INTEGER", nullable: false, defaultValue: 0L);
        migrationBuilder.Sql(
            "INSERT OR IGNORE INTO configuration_versions (\"Scope\", \"Version\", \"UpdatedAt\") VALUES ('routing', 0, 0);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM configuration_versions WHERE \"Scope\" = 'routing';");
        migrationBuilder.DropColumn(name: "RoutingVersion", table: "configuration_process_statuses");
    }
}
