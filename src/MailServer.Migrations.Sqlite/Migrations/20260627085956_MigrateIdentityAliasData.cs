using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MailServer.Migrations.Sqlite.Migrations;

[DbContext(typeof(MailDbContext))]
[Migration("20260627085956_MigrateIdentityAliasData")]
public sealed class MigrateIdentityAliasData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE mail_aliases
            SET TargetAccountId = (
                SELECT target.Id
                FROM mail_accounts AS target
                INNER JOIN mail_domains AS domain ON domain.Id = target.DomainId
                WHERE target.DomainId = mail_aliases.DomainId
                  AND lower(target.LocalPart || '@' || domain.Name) = lower(mail_aliases.DestinationAddress)
                  AND NOT EXISTS (
                      SELECT 1 FROM mail_accounts AS source
                      WHERE source.DomainId = mail_aliases.DomainId
                        AND lower(source.LocalPart) = lower(mail_aliases.SourceLocalPart))
                LIMIT 1
            );
            """);
        migrationBuilder.Sql(
            """
            UPDATE mail_aliases
            SET IsEnabled = COALESCE((
                SELECT target.IsEnabled FROM mail_accounts AS target
                WHERE target.Id = mail_aliases.TargetAccountId
            ), 0)
            WHERE TargetAccountId IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
