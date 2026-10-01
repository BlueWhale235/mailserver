using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageSearchRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_search_records",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: false),
                    ParserVersion = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: false),
                    Subject = table.Column<string>(type: "longtext", nullable: false),
                    HeadersJson = table.Column<string>(type: "longtext", nullable: false),
                    Body = table.Column<string>(type: "longtext", nullable: false),
                    Complete = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RetryAfter = table.Column<DateTimeOffset>(type: "datetime", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_search_records", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_message_search_records_stored_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "stored_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_search_records");
        }
    }
}
