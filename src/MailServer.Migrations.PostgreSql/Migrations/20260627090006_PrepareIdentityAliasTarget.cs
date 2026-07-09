using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MailServer.Migrations.PostgreSql.Migrations;

[DbContext(typeof(MailDbContext))]
[Migration("20260627090006_PrepareIdentityAliasTarget")]
public sealed class PrepareIdentityAliasTarget : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
