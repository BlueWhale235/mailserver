using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyUsageMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "api_keys",
                type: "varchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "system");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUsedAt",
                table: "api_keys",
                type: "datetime",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_LastUsedAt",
                table: "api_keys",
                column: "LastUsedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_api_keys_LastUsedAt",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "api_keys");
        }
    }
}
