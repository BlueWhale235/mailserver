using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddMailRulesAndIdentityAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_mail_accounts_Id_DomainId",
                table: "mail_accounts",
                columns: new[] { "Id", "DomainId" });

            migrationBuilder.AlterColumn<string>(
                name: "DestinationAddress",
                table: "mail_aliases",
                type: "TEXT",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 320);

            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "mail_aliases",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetAccountId",
                table: "mail_aliases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "domain_dns_checks",
                columns: table => new
                {
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", maxLength: 32768, nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CheckedAt = table.Column<long>(type: "INTEGER", nullable: true)
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
                });

            migrationBuilder.CreateTable(
                name: "mail_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MatchMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetMailboxName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "mail_rule_conditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Operator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ValuesJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "sent_copy_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetMailboxName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    IdentityKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClientMailboxMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ServerMailboxMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReadyAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeduplicateUntil = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
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
                });

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
                type: "TEXT",
                maxLength: 320,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 320,
                oldNullable: true);
        }
    }
}
