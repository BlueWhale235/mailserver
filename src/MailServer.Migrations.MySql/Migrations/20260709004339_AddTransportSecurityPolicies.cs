using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddTransportSecurityPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DaneEnabled",
                table: "domain_dns_settings",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DaneTlsaRecords",
                table: "domain_dns_settings",
                type: "varchar(8192)",
                maxLength: 8192,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "MtaStsMaxAgeSeconds",
                table: "domain_dns_settings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MtaStsMode",
                table: "domain_dns_settings",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MtaStsMxPatterns",
                table: "domain_dns_settings",
                type: "varchar(4096)",
                maxLength: 4096,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MtaStsPolicyId",
                table: "domain_dns_settings",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TlsReportUris",
                table: "domain_dns_settings",
                type: "varchar(4096)",
                maxLength: 4096,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "mta_sts_policy_cache",
                columns: table => new
                {
                    Domain = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    PolicyId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Mode = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    MxPatterns = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    MaxAgeSeconds = table.Column<int>(type: "int", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    LastRefreshAttemptAt = table.Column<DateTimeOffset>(type: "datetime", nullable: true),
                    LastError = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mta_sts_policy_cache", x => x.Domain);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "tls_delivery_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    PolicyDomain = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    MxHost = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    PolicyType = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    PolicyString = table.Column<string>(type: "varchar(8192)", maxLength: 8192, nullable: false),
                    ResultType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    FailureReason = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: false),
                    SendingIp = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    ReceivingIp = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    Reported = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tls_delivery_events", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "tls_report_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    PolicyDomain = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    ReportUri = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    ReportJson = table.Column<string>(type: "longtext", maxLength: 1048576, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: true),
                    LastError = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tls_report_deliveries", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_mta_sts_policy_cache_ExpiresAt",
                table: "mta_sts_policy_cache",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_tls_delivery_events_Reported_OccurredAt",
                table: "tls_delivery_events",
                columns: new[] { "Reported", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tls_report_deliveries_CompletedAt_NextAttemptAt",
                table: "tls_report_deliveries",
                columns: new[] { "CompletedAt", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mta_sts_policy_cache");

            migrationBuilder.DropTable(
                name: "tls_delivery_events");

            migrationBuilder.DropTable(
                name: "tls_report_deliveries");

            migrationBuilder.DropColumn(
                name: "DaneEnabled",
                table: "domain_dns_settings");

            migrationBuilder.DropColumn(
                name: "DaneTlsaRecords",
                table: "domain_dns_settings");

            migrationBuilder.DropColumn(
                name: "MtaStsMaxAgeSeconds",
                table: "domain_dns_settings");

            migrationBuilder.DropColumn(
                name: "MtaStsMode",
                table: "domain_dns_settings");

            migrationBuilder.DropColumn(
                name: "MtaStsMxPatterns",
                table: "domain_dns_settings");

            migrationBuilder.DropColumn(
                name: "MtaStsPolicyId",
                table: "domain_dns_settings");

            migrationBuilder.DropColumn(
                name: "TlsReportUris",
                table: "domain_dns_settings");
        }
    }
}
