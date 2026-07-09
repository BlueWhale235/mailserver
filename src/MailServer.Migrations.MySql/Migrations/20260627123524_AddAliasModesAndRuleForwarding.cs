using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddAliasModesAndRuleForwarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "mail_aliases",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "SendReceive");

            migrationBuilder.AddColumn<int>(
                name: "ForwardDepth",
                table: "delivery_jobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginRuleId",
                table: "delivery_jobs",
                type: "char(36)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mail_rule_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    RuleId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Kind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ValuesJson = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_rule_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_rule_actions_mail_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "mail_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "srs_address_mappings",
                columns: table => new
                {
                    Token = table.Column<string>(type: "varchar(96)", maxLength: 96, nullable: false),
                    DomainId = table.Column<Guid>(type: "char(36)", nullable: false),
                    OriginalSender = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false),
                    ForwardDepth = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_srs_address_mappings", x => x.Token);
                    table.ForeignKey(
                        name: "FK_srs_address_mappings_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_jobs_OriginRuleId",
                table: "delivery_jobs",
                column: "OriginRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_mail_rule_actions_RuleId_Position",
                table: "mail_rule_actions",
                columns: new[] { "RuleId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_srs_address_mappings_DomainId",
                table: "srs_address_mappings",
                column: "DomainId");

            migrationBuilder.CreateIndex(
                name: "IX_srs_address_mappings_ExpiresAt",
                table: "srs_address_mappings",
                column: "ExpiresAt");

            migrationBuilder.Sql("""
                UPDATE mail_aliases alias_row
                SET alias_row.Mode = CASE
                    WHEN alias_row.IsEnabled = 0 OR alias_row.TargetAccountId IS NULL OR EXISTS (
                        SELECT 1 FROM mail_accounts account_row
                        WHERE account_row.DomainId = alias_row.DomainId
                          AND lower(account_row.LocalPart) = lower(alias_row.SourceLocalPart)
                    ) THEN 'Disabled'
                    ELSE 'SendReceive'
                END;
                UPDATE mail_aliases SET IsEnabled = 0 WHERE Mode = 'Disabled';
                INSERT INTO mail_rule_actions (Id, RuleId, Kind, ValuesJson, Position)
                SELECT Id, Id, 'MoveToFolder', JSON_ARRAY(TargetMailboxName), 0
                FROM mail_rules;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_rule_actions");

            migrationBuilder.DropTable(
                name: "srs_address_mappings");

            migrationBuilder.DropIndex(
                name: "IX_delivery_jobs_OriginRuleId",
                table: "delivery_jobs");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "mail_aliases");

            migrationBuilder.DropColumn(
                name: "ForwardDepth",
                table: "delivery_jobs");

            migrationBuilder.DropColumn(
                name: "OriginRuleId",
                table: "delivery_jobs");
        }
    }
}
