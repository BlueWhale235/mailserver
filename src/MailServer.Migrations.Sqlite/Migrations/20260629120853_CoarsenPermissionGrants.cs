using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class CoarsenPermissionGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'api-key.read', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('api.key.list');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'api-key.manage', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('api.key.create', 'api.key.update', 'api.key.delete', 'api.key.refresh');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'admin.account.read', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('admin.account.list');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'admin.account.manage', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('admin.account.create', 'admin.account.update', 'admin.account.delete');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'domain.read',
                       CASE WHEN ResourceType = '' THEN 'domain' ELSE ResourceType END,
                       CASE WHEN ResourceKey = '' THEN '*' ELSE ResourceKey END,
                       CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN (
                    'domain.list', 'domain.detail', 'domain.dns.detail', 'domain.dns.check',
                    'domain.certificate.detail', 'domain.account.list', 'domain.alias.list',
                    'domain.rule.list', 'domain.audit.list');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'domain.manage',
                       'domain',
                       CASE WHEN ResourceKey = '' THEN '*' ELSE ResourceKey END,
                       CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN (
                    'domain.create', 'domain.update', 'domain.delete', 'domain.quota.update',
                    'domain.dns.update', 'domain.certificate.update', 'domain.certificate.delete',
                    'domain.account.create', 'domain.account.update', 'domain.account.password',
                    'domain.alias.create', 'domain.alias.update', 'domain.alias.delete',
                    'domain.rule.create', 'domain.rule.update', 'domain.rule.delete', 'domain.rule.reorder');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'dashboard.read', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('dashboard.view');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'firewall.read', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('admin.firewall.list');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'firewall.manage', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('admin.firewall.create', 'admin.firewall.update', 'admin.firewall.delete');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'queue.read', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('admin.queue.list');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'queue.manage', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('admin.queue.retry', 'admin.queue.delete');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'settings.read', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('admin.settings.detail');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'settings.manage', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('admin.settings.update');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT SubjectType, SubjectId, 'dns.read', '', '', CreatedBy, CreatedAt
                FROM permission_grants
                WHERE PermissionCode IN ('admin.dns.diagnose');
                """);

            migrationBuilder.Sql("""
                DELETE FROM permission_grants
                WHERE PermissionCode IN (
                    'api.key.list', 'api.key.create', 'api.key.update', 'api.key.delete', 'api.key.refresh',
                    'admin.account.list', 'admin.account.create', 'admin.account.update', 'admin.account.delete',
                    'domain.list', 'domain.create', 'domain.detail', 'domain.update', 'domain.delete',
                    'domain.quota.update', 'domain.dns.detail', 'domain.dns.update', 'domain.dns.check',
                    'domain.certificate.detail', 'domain.certificate.update', 'domain.certificate.delete',
                    'domain.account.list', 'domain.account.create', 'domain.account.update', 'domain.account.password',
                    'domain.alias.list', 'domain.alias.create', 'domain.alias.update', 'domain.alias.delete',
                    'domain.rule.list', 'domain.rule.create', 'domain.rule.update', 'domain.rule.delete',
                    'domain.rule.reorder', 'domain.audit.list', 'dashboard.view',
                    'admin.firewall.list', 'admin.firewall.create', 'admin.firewall.update', 'admin.firewall.delete',
                    'admin.queue.list', 'admin.queue.retry', 'admin.queue.delete',
                    'admin.settings.detail', 'admin.settings.update', 'admin.dns.diagnose');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Fine-grained permission codes are intentionally not restored.
        }
    }
}
