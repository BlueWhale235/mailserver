using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MailServer.Migrations.Sqlite.Migrations;

[DbContext(typeof(MailDbContext))]
[Migration("20260627085954_PrepareIdentityAliasTarget")]
public sealed class PrepareIdentityAliasTarget : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_mail_accounts_alias_target_parent ON mail_accounts (Id, DomainId);");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP INDEX IF EXISTS IX_mail_accounts_alias_target_parent;");
}
