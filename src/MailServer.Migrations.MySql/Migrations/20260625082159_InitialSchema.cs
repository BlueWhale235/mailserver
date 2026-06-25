using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "admin_users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    UserName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_users", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Prefix = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    SecretHash = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Scopes = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetime", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Actor = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Resource = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false),
                    Detail = table.Column<string>(type: "longtext", nullable: true),
                    RemoteAddress = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_records", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "mail_domains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    OutboundMode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    DkimSelector = table.Column<string>(type: "longtext", nullable: true),
                    DkimPrivateKeyPath = table.Column<string>(type: "longtext", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_domains", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "stored_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    ContentKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    EnvelopeFrom = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false),
                    InternetMessageId = table.Column<string>(type: "varchar(998)", maxLength: 998, nullable: true),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stored_messages", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "mail_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    DomainId = table.Column<Guid>(type: "char(36)", nullable: false),
                    LocalPart = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    QuotaBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_accounts_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "mail_aliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    DomainId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SourceLocalPart = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    DestinationAddress = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_aliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_aliases_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "delivery_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Sender = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false),
                    Recipient = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false),
                    RouteMode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    LeaseOwner = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetime", nullable: true),
                    LastError = table.Column<string>(type: "longtext", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_jobs_stored_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "stored_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "mailboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    AccountId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: false),
                    UidNext = table.Column<long>(type: "bigint", nullable: false),
                    HighestModSeq = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailboxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mailboxes_mail_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "mail_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    DeliveryJobId = table.Column<Guid>(type: "char(36)", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    Succeeded = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PermanentFailure = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RemoteHost = table.Column<string>(type: "longtext", nullable: true),
                    Response = table.Column<string>(type: "longtext", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_attempts_delivery_jobs_DeliveryJobId",
                        column: x => x.DeliveryJobId,
                        principalTable: "delivery_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "mailbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    MailboxId = table.Column<Guid>(type: "char(36)", nullable: false),
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Uid = table.Column<long>(type: "bigint", nullable: false),
                    Flags = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false),
                    ModSeq = table.Column<long>(type: "bigint", nullable: false),
                    InternalDate = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mailbox_messages_mailboxes_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "mailboxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_mailbox_messages_stored_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "stored_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_admin_users_UserName",
                table: "admin_users",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_Prefix",
                table: "api_keys",
                column: "Prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_CreatedAt",
                table: "audit_records",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_DeliveryJobId",
                table: "delivery_attempts",
                column: "DeliveryJobId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_jobs_MessageId",
                table: "delivery_jobs",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_jobs_Status_NextAttemptAt",
                table: "delivery_jobs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_accounts_DomainId_LocalPart",
                table: "mail_accounts",
                columns: new[] { "DomainId", "LocalPart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_aliases_DomainId_SourceLocalPart",
                table: "mail_aliases",
                columns: new[] { "DomainId", "SourceLocalPart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_domains_Name",
                table: "mail_domains",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_messages_MailboxId_Uid",
                table: "mailbox_messages",
                columns: new[] { "MailboxId", "Uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_messages_MessageId",
                table: "mailbox_messages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_mailboxes_AccountId_Name",
                table: "mailboxes",
                columns: new[] { "AccountId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stored_messages_ContentKey",
                table: "stored_messages",
                column: "ContentKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_users");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "audit_records");

            migrationBuilder.DropTable(
                name: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "mail_aliases");

            migrationBuilder.DropTable(
                name: "mailbox_messages");

            migrationBuilder.DropTable(
                name: "delivery_jobs");

            migrationBuilder.DropTable(
                name: "mailboxes");

            migrationBuilder.DropTable(
                name: "stored_messages");

            migrationBuilder.DropTable(
                name: "mail_accounts");

            migrationBuilder.DropTable(
                name: "mail_domains");
        }
    }
}
