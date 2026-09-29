using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceTraceId",
                table: "stored_messages",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTraceId",
                table: "delivery_jobs",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "performance_minutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    ProcessId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Minute = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    Kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Module = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Errors = table.Column<long>(type: "bigint", nullable: false),
                    Slow = table.Column<long>(type: "bigint", nullable: false),
                    TotalMilliseconds = table.Column<double>(type: "double", nullable: false),
                    MaximumMilliseconds = table.Column<double>(type: "double", nullable: false),
                    HistogramJson = table.Column<string>(type: "longtext", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_minutes", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "performance_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false),
                    TraceId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    SourceTraceId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    ProcessId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Module = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    DurationMilliseconds = table.Column<double>(type: "double", nullable: false),
                    ProcessingMilliseconds = table.Column<double>(type: "double", nullable: false),
                    SqlMilliseconds = table.Column<double>(type: "double", nullable: false),
                    SqlCount = table.Column<int>(type: "int", nullable: false),
                    Slow = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Truncated = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: true),
                    DeliveryJobId = table.Column<Guid>(type: "char(36)", nullable: true),
                    Attempt = table.Column<int>(type: "int", nullable: true),
                    QueueWaitMilliseconds = table.Column<double>(type: "double", nullable: true),
                    RetryBackoffMilliseconds = table.Column<double>(type: "double", nullable: true),
                    ClockAnomaly = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DetailBytes = table.Column<int>(type: "int", nullable: false),
                    DetailsJson = table.Column<string>(type: "longtext", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_operations", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "performance_processes",
                columns: table => new
                {
                    ProcessId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false),
                    DroppedOperations = table.Column<long>(type: "bigint", nullable: false),
                    DroppedAggregates = table.Column<long>(type: "bigint", nullable: false),
                    TruncatedOperations = table.Column<long>(type: "bigint", nullable: false),
                    BufferBytes = table.Column<long>(type: "bigint", nullable: false),
                    BudgetBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageUnavailable = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_processes", x => x.ProcessId);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "performance_sql",
                columns: table => new
                {
                    Fingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Template = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performance_sql", x => x.Fingerprint);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_performance_minutes_Minute_Kind",
                table: "performance_minutes",
                columns: new[] { "Minute", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_performance_minutes_ProcessId_Minute_Kind_Module_Name",
                table: "performance_minutes",
                columns: new[] { "ProcessId", "Minute", "Kind", "Module", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_performance_operations_DeliveryJobId",
                table: "performance_operations",
                column: "DeliveryJobId");

            migrationBuilder.CreateIndex(
                name: "IX_performance_operations_MessageId",
                table: "performance_operations",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_performance_operations_SourceTraceId",
                table: "performance_operations",
                column: "SourceTraceId");

            migrationBuilder.CreateIndex(
                name: "IX_performance_operations_StartedAt_Id",
                table: "performance_operations",
                columns: new[] { "StartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_performance_operations_TraceId",
                table: "performance_operations",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_performance_sql_LastSeenAt",
                table: "performance_sql",
                column: "LastSeenAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "performance_minutes");

            migrationBuilder.DropTable(
                name: "performance_operations");

            migrationBuilder.DropTable(
                name: "performance_processes");

            migrationBuilder.DropTable(
                name: "performance_sql");

            migrationBuilder.DropColumn(
                name: "SourceTraceId",
                table: "stored_messages");

            migrationBuilder.DropColumn(
                name: "SourceTraceId",
                table: "delivery_jobs");
        }
    }
}
