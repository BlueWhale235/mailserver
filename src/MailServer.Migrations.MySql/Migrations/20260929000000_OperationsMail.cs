using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MailServer.Migrations.MySql.Migrations;

[DbContext(typeof(MailDbContext))]
[Migration("20260929000000_OperationsMail")]
public sealed class OperationsMail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("InternetMessageIdHash", "stored_messages", type: "varchar(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("SourceType", "stored_messages", type: "varchar(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<Guid>("AuthenticatedAccountId", "stored_messages", type: "char(36)", nullable: true);
        migrationBuilder.AddColumn<string>("AuthenticatedAccountName", "stored_messages", type: "varchar(320)", maxLength: 320, nullable: true);
        migrationBuilder.AddColumn<string>("SourceIp", "stored_messages", type: "varchar(128)", maxLength: 128, nullable: true);
        migrationBuilder.CreateIndex("IX_stored_messages_InternetMessageIdHash", "stored_messages", "InternetMessageIdHash");
        migrationBuilder.CreateIndex("IX_stored_messages_ReceivedAt", "stored_messages", "ReceivedAt");
        migrationBuilder.CreateTable("stored_message_recipients", table => new
        {
            Id = table.Column<Guid>(type: "char(36)", nullable: false),
            MessageId = table.Column<Guid>(type: "char(36)", nullable: false),
            Address = table.Column<string>(type: "varchar(320)", maxLength: 320, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_stored_message_recipients", x => x.Id);
            table.ForeignKey("FK_stored_message_recipients_stored_messages_MessageId", x => x.MessageId,
                "stored_messages", "Id", onDelete: ReferentialAction.Cascade);
        }).Annotation("MySQL:Charset", "utf8mb4");
        migrationBuilder.CreateIndex("IX_stored_message_recipients_Address", "stored_message_recipients", "Address");
        migrationBuilder.CreateIndex("IX_stored_message_recipients_MessageId", "stored_message_recipients", "MessageId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("stored_message_recipients");
        migrationBuilder.DropIndex("IX_stored_messages_InternetMessageIdHash", "stored_messages");
        migrationBuilder.DropIndex("IX_stored_messages_ReceivedAt", "stored_messages");
        foreach (var column in new[] { "InternetMessageIdHash", "SourceType", "AuthenticatedAccountId", "AuthenticatedAccountName", "SourceIp" })
            migrationBuilder.DropColumn(column, "stored_messages");
    }
}
