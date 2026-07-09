using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MailServer.Migrations.PostgreSql.Migrations;

[DbContext(typeof(MailDbContext))]
[Migration("20260627090008_MigrateIdentityAliasData")]
public sealed class MigrateIdentityAliasData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            UPDATE mail_aliases AS alias
            SET "TargetAccountId" = target."Id",
                "IsEnabled" = target."IsEnabled"
            FROM mail_accounts AS target
            INNER JOIN mail_domains AS domain ON domain."Id" = target."DomainId"
            WHERE target."DomainId" = alias."DomainId"
              AND lower(target."LocalPart" || '@' || domain."Name") = lower(alias."DestinationAddress")
              AND NOT EXISTS (
                  SELECT 1 FROM mail_accounts AS source
                  WHERE source."DomainId" = alias."DomainId"
                    AND lower(source."LocalPart") = lower(alias."SourceLocalPart"));
            """);

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
