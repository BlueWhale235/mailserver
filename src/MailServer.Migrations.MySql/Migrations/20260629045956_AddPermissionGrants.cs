using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissionGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permission_grants",
                columns: table => new
                {
                    SubjectType = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    SubjectId = table.Column<Guid>(type: "char(36)", nullable: false),
                    PermissionCode = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    ResourceType = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, defaultValue: ""),
                    ResourceKey = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, defaultValue: ""),
                    CreatedBy = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetime", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permission_grants", x => new { x.SubjectType, x.SubjectId, x.PermissionCode, x.ResourceType, x.ResourceKey });
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_permission_grants_PermissionCode_ResourceType_ResourceKey",
                table: "permission_grants",
                columns: new[] { "PermissionCode", "ResourceType", "ResourceKey" });

            migrationBuilder.Sql("""
                INSERT IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT 'AdminUser', Id, '*', '', '', 'migration', UTC_TIMESTAMP()
                FROM admin_users;
                """);

            migrationBuilder.Sql("""
                INSERT IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT 'ApiKey', Id, '*', '', '', 'migration', UTC_TIMESTAMP()
                FROM api_keys;
                """);

            migrationBuilder.DropColumn(
                name: "Scopes",
                table: "api_keys");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "permission_grants");

            migrationBuilder.AddColumn<string>(
                name: "Scopes",
                table: "api_keys",
                type: "varchar(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");
        }
    }
}
