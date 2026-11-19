using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class MailboxCountersAndFetchMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MessageCount",
                table: "mailboxes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "UnseenCount",
                table: "mailboxes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "message_fetch_metadata",
                columns: table => new
                {
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    ContentKey = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    ParserVersion = table.Column<int>(type: "int", nullable: false),
                    Envelope = table.Column<string>(type: "longtext", nullable: false),
                    Body = table.Column<string>(type: "longtext", nullable: false),
                    BodyStructure = table.Column<string>(type: "longtext", nullable: false),
                    HeaderEnd = table.Column<long>(type: "bigint", nullable: false),
                    BodyStart = table.Column<long>(type: "bigint", nullable: false)
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
                })
                .Annotation("MySQL:Charset", "utf8mb4");
            migrationBuilder.Sql("""
                UPDATE mailboxes b LEFT JOIN
                    (SELECT MailboxId, SUM(DeletedAt IS NULL) AS MessageCount,
                     SUM(DeletedAt IS NULL AND LOCATE(CONCAT(' ', CHAR(92), 'seen '), CONCAT(' ', LOWER(Flags), ' ')) = 0) AS UnseenCount,
                     MAX(Uid) + 1 AS UidNext FROM mailbox_messages GROUP BY MailboxId) m ON m.MailboxId = b.Id
                    SET b.MessageCount = COALESCE(m.MessageCount, 0), b.UnseenCount = COALESCE(m.UnseenCount, 0),
                        b.UidNext = GREATEST(b.UidNext, COALESCE(m.UidNext, 1));
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
        }
    }
}
