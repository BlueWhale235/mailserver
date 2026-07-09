using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddMailRulesAndIdentityAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DestinationAddress",
                table: "mail_aliases",
                type: "varchar(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(320)",
                oldMaxLength: 320);

            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "mail_aliases",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetAccountId",
                table: "mail_aliases",
                type: "char(36)",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_mail_accounts_Id_DomainId",
                table: "mail_accounts",
                columns: new[] { "Id", "DomainId" });

            migrationBuilder.CreateTable(
                name: "domain_dns_checks",
                columns: table => new
                {
                    DomainId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    DetailsJson = table.Column<string>(type: "longtext", maxLength: 32768, nullable: false),
                    LastError = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: true),
                    CheckedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_dns_checks", x => x.DomainId);
                    table.ForeignKey(
                        name: "FK_domain_dns_checks_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "mail_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    DomainId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Direction = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    MatchMode = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    TargetMailboxName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_rules_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "mail_rule_conditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    RuleId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Field = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Operator = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ValuesJson = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_rule_conditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_rule_conditions_mail_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "mail_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "sent_copy_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false),
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    RuleId = table.Column<Guid>(type: "char(36)", nullable: true),
                    TargetMailboxName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    IdentityKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    State = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ClientMailboxMessageId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ServerMailboxMessageId = table.Column<Guid>(type: "char(36)", nullable: true),
                    ReadyAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    DeduplicateUntil = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sent_copy_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sent_copy_jobs_mail_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "mail_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sent_copy_jobs_mail_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "mail_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sent_copy_jobs_stored_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "stored_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_mail_aliases_TargetAccountId_DomainId",
                table: "mail_aliases",
                columns: new[] { "TargetAccountId", "DomainId" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_rule_conditions_RuleId_Position",
                table: "mail_rule_conditions",
                columns: new[] { "RuleId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_rules_DomainId_Direction_Priority",
                table: "mail_rules",
                columns: new[] { "DomainId", "Direction", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_sent_copy_jobs_AccountId_DeduplicateUntil",
                table: "sent_copy_jobs",
                columns: new[] { "AccountId", "DeduplicateUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_sent_copy_jobs_MessageId",
                table: "sent_copy_jobs",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_sent_copy_jobs_RuleId",
                table: "sent_copy_jobs",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_sent_copy_jobs_State_ReadyAt",
                table: "sent_copy_jobs",
                columns: new[] { "State", "ReadyAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_mail_aliases_mail_accounts_TargetAccountId_DomainId",
                table: "mail_aliases",
                columns: new[] { "TargetAccountId", "DomainId" },
                principalTable: "mail_accounts",
                principalColumns: new[] { "Id", "DomainId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_mail_aliases_mail_accounts_TargetAccountId_DomainId",
                table: "mail_aliases");

            migrationBuilder.DropTable(
                name: "domain_dns_checks");

            migrationBuilder.DropTable(
                name: "mail_rule_conditions");

            migrationBuilder.DropTable(
                name: "sent_copy_jobs");

            migrationBuilder.DropTable(
                name: "mail_rules");

            migrationBuilder.DropIndex(
                name: "IX_mail_aliases_TargetAccountId_DomainId",
                table: "mail_aliases");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_mail_accounts_Id_DomainId",
                table: "mail_accounts");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "mail_aliases");

            migrationBuilder.DropColumn(
                name: "TargetAccountId",
                table: "mail_aliases");

            migrationBuilder.AlterColumn<string>(
                name: "DestinationAddress",
                table: "mail_aliases",
                type: "varchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "varchar(320)",
                oldMaxLength: 320,
                oldNullable: true);
        }
    }
}
