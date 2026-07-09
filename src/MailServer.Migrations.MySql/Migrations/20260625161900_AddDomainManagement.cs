using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DkimSelector",
                table: "mail_domains",
                type: "varchar(63)",
                maxLength: 63,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "longtext",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DkimPrivateKeyPath",
                table: "mail_domains",
                type: "varchar(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "longtext",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DkimPublicKey",
                table: "mail_domains",
                type: "varchar(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MailHostName",
                table: "mail_domains",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "domain_certificates",
                columns: table => new
                {
                    DomainId = table.Column<Guid>(type: "char(36)", nullable: false),
                    Source = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    CertificatePath = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false),
                    PrivateKeyPath = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false),
                    Subject = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false),
                    Issuer = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: false),
                    SubjectAlternativeNames = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    Thumbprint = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    NotBefore = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    NotAfter = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_certificates", x => x.DomainId);
                    table.ForeignKey(
                        name: "FK_domain_certificates_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "domain_dns_settings",
                columns: table => new
                {
                    DomainId = table.Column<Guid>(type: "char(36)", nullable: false),
                    SpfIncludeMx = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SpfIncludeA = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SpfIpv4 = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    SpfIpv6 = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    SpfIncludes = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    SpfCustomMechanisms = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    SpfAllPolicy = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    DmarcPolicy = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    DmarcSubdomainPolicy = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: true),
                    DmarcPercentage = table.Column<int>(type: "int", nullable: false),
                    DmarcAggregateReportUris = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    DmarcForensicReportUris = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    DmarcDkimAlignment = table.Column<string>(type: "varchar(4)", maxLength: 4, nullable: false),
                    DmarcSpfAlignment = table.Column<string>(type: "varchar(4)", maxLength: 4, nullable: false),
                    DmarcFailureOptions = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_dns_settings", x => x.DomainId);
                    table.ForeignKey(
                        name: "FK_domain_dns_settings_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_mail_domains_MailHostName",
                table: "mail_domains",
                column: "MailHostName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "domain_certificates");

            migrationBuilder.DropTable(
                name: "domain_dns_settings");

            migrationBuilder.DropIndex(
                name: "IX_mail_domains_MailHostName",
                table: "mail_domains");

            migrationBuilder.DropColumn(
                name: "DkimPublicKey",
                table: "mail_domains");

            migrationBuilder.DropColumn(
                name: "MailHostName",
                table: "mail_domains");

            migrationBuilder.AlterColumn<string>(
                name: "DkimSelector",
                table: "mail_domains",
                type: "longtext",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(63)",
                oldMaxLength: 63,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DkimPrivateKeyPath",
                table: "mail_domains",
                type: "longtext",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(2048)",
                oldMaxLength: 2048,
                oldNullable: true);
        }
    }
}
