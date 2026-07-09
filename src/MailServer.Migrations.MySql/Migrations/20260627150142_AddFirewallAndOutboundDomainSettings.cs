using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddFirewallAndOutboundDomainSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OutboundIpAddresses",
                table: "mail_domains",
                type: "varchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpHostName",
                table: "mail_domains",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RouteDomainId",
                table: "delivery_jobs",
                type: "char(36)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "firewall_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    Cidr = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_firewall_rules", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_jobs_RouteDomainId",
                table: "delivery_jobs",
                column: "RouteDomainId");

            migrationBuilder.CreateIndex(
                name: "IX_firewall_rules_Cidr",
                table: "firewall_rules",
                column: "Cidr",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_delivery_jobs_mail_domains_RouteDomainId",
                table: "delivery_jobs",
                column: "RouteDomainId",
                principalTable: "mail_domains",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_delivery_jobs_mail_domains_RouteDomainId",
                table: "delivery_jobs");

            migrationBuilder.DropTable(
                name: "firewall_rules");

            migrationBuilder.DropIndex(
                name: "IX_delivery_jobs_RouteDomainId",
                table: "delivery_jobs");

            migrationBuilder.DropColumn(
                name: "OutboundIpAddresses",
                table: "mail_domains");

            migrationBuilder.DropColumn(
                name: "SmtpHostName",
                table: "mail_domains");

            migrationBuilder.DropColumn(
                name: "RouteDomainId",
                table: "delivery_jobs");
        }
    }
}
