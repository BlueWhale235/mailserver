using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundDeliveryPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DirectDeliverSameDomain",
                table: "mail_domains",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectDeliverSameDomain",
                table: "mail_domains");
        }
    }
}
