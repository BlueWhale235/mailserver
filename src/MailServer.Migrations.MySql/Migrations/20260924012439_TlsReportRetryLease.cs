using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class TlsReportRetryLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastManualRetryKey",
                table: "tls_report_deliveries",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "tls_report_deliveries",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseUntil",
                table: "tls_report_deliveries",
                type: "datetime",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tls_report_deliveries_LeaseUntil",
                table: "tls_report_deliveries",
                column: "LeaseUntil");

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_Action_Resource_RequestId",
                table: "audit_records",
                columns: new[] { "Action", "Resource", "RequestId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tls_report_deliveries_LeaseUntil",
                table: "tls_report_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_audit_records_Action_Resource_RequestId",
                table: "audit_records");

            migrationBuilder.DropColumn(
                name: "LastManualRetryKey",
                table: "tls_report_deliveries");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "tls_report_deliveries");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                table: "tls_report_deliveries");
        }
    }
}
