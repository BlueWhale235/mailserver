using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainQuotaAndTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Value",
                table: "system_settings",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AddColumn<long>(
                name: "QuotaBytes",
                table: "mail_domains",
                type: "bigint",
                nullable: false,
                defaultValue: 10737418240L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuotaBytes",
                table: "mail_domains");

            migrationBuilder.AlterColumn<string>(
                name: "Value",
                table: "system_settings",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(8192)",
                oldMaxLength: 8192);
        }
    }
}
