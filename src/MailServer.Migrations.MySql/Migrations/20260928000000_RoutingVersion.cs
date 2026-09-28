using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MailServer.Migrations.MySql.Migrations;

[DbContext(typeof(MailDbContext))]
[Migration("20260928000000_RoutingVersion")]
public sealed class RoutingVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "INSERT IGNORE INTO configuration_versions (`Scope`, `Version`, `UpdatedAt`) VALUES ('routing', 0, CURRENT_TIMESTAMP);");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "DELETE FROM configuration_versions WHERE `Scope` = 'routing';");
}
