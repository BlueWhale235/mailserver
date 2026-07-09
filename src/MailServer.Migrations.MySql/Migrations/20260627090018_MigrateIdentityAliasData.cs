using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MailServer.Migrations.MySql.Migrations;

[DbContext(typeof(MailDbContext))]
[Migration("20260627090018_MigrateIdentityAliasData")]
public sealed class MigrateIdentityAliasData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            UPDATE mail_aliases AS alias
            INNER JOIN mail_accounts AS target ON target.DomainId = alias.DomainId
            INNER JOIN mail_domains AS domain ON domain.Id = target.DomainId
            LEFT JOIN mail_accounts AS source
              ON source.DomainId = alias.DomainId
             AND lower(source.LocalPart) = lower(alias.SourceLocalPart)
            SET alias.TargetAccountId = target.Id,
                alias.IsEnabled = target.IsEnabled
            WHERE lower(CONCAT(target.LocalPart, '@', domain.Name)) = lower(alias.DestinationAddress)
              AND source.Id IS NULL;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
