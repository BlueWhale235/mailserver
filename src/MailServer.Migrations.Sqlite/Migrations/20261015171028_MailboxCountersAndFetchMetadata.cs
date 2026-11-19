using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MailboxCountersAndFetchMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "mailboxes",
                type: "TEXT",
                maxLength: 255,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 255);

            migrationBuilder.AddColumn<long>(
                name: "MessageCount",
                table: "mailboxes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "UnseenCount",
                table: "mailboxes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "message_fetch_metadata",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ParserVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Envelope = table.Column<string>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    BodyStructure = table.Column<string>(type: "TEXT", nullable: false),
                    HeaderEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    BodyStart = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_fetch_metadata", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_message_fetch_metadata_stored_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "stored_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.Sql("""
                UPDATE mailboxes SET
                    MessageCount = (SELECT COUNT(*) FROM mailbox_messages m WHERE m.MailboxId = mailboxes.Id AND m.DeletedAt IS NULL),
                    UnseenCount = (SELECT COUNT(*) FROM mailbox_messages m WHERE m.MailboxId = mailboxes.Id AND m.DeletedAt IS NULL AND instr(' ' || lower(m.Flags) || ' ', ' \seen ') = 0),
                    UidNext = MAX(UidNext, COALESCE((SELECT MAX(m.Uid) + 1 FROM mailbox_messages m WHERE m.MailboxId = mailboxes.Id), 1));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_fetch_metadata");

            migrationBuilder.DropColumn(
                name: "MessageCount",
                table: "mailboxes");

            migrationBuilder.DropColumn(
                name: "UnseenCount",
                table: "mailboxes");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "mailboxes",
                type: "TEXT",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 255,
                oldCollation: "NOCASE");
        }
    }
}
