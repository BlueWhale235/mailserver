using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.MySql.Migrations
{
    /// <inheritdoc />
    public partial class CoarsenPermissionGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT IGNORE INTO permission_grants
                    (SubjectType, SubjectId, PermissionCode, ResourceType, ResourceKey, CreatedBy, CreatedAt)
                SELECT
                    SubjectType,
                    SubjectId,
                    CASE
                        WHEN PermissionCode IN ('api.key.list') THEN 'api-key.read'
                        WHEN PermissionCode IN ('api.key.create', 'api.key.update', 'api.key.delete', 'api.key.refresh') THEN 'api-key.manage'
                        WHEN PermissionCode IN ('admin.account.list') THEN 'admin.account.read'
                        WHEN PermissionCode IN ('admin.account.create', 'admin.account.update', 'admin.account.delete') THEN 'admin.account.manage'
                        WHEN PermissionCode IN (
                            'domain.list', 'domain.detail', 'domain.dns.detail', 'domain.dns.check',
                            'domain.certificate.detail', 'domain.account.list', 'domain.alias.list',
                            'domain.rule.list', 'domain.audit.list') THEN 'domain.read'
                        WHEN PermissionCode IN (
                            'domain.create', 'domain.update', 'domain.delete', 'domain.quota.update',
                            'domain.dns.update', 'domain.certificate.update', 'domain.certificate.delete',
                            'domain.account.create', 'domain.account.update', 'domain.account.password',
                            'domain.alias.create', 'domain.alias.update', 'domain.alias.delete',
                            'domain.rule.create', 'domain.rule.update', 'domain.rule.delete', 'domain.rule.reorder') THEN 'domain.manage'
                        WHEN PermissionCode IN ('dashboard.view') THEN 'dashboard.read'
                        WHEN PermissionCode IN ('admin.firewall.list') THEN 'firewall.read'
                        WHEN PermissionCode IN ('admin.firewall.create', 'admin.firewall.update', 'admin.firewall.delete') THEN 'firewall.manage'
                        WHEN PermissionCode IN ('admin.queue.list') THEN 'queue.read'
                        WHEN PermissionCode IN ('admin.queue.retry', 'admin.queue.delete') THEN 'queue.manage'
                        WHEN PermissionCode IN ('admin.settings.detail') THEN 'settings.read'
                        WHEN PermissionCode IN ('admin.settings.update') THEN 'settings.manage'
                        WHEN PermissionCode IN ('admin.dns.diagnose') THEN 'dns.read'
                    END,
                    CASE WHEN PermissionCode LIKE 'domain.%' THEN 'domain' ELSE '' END,
                    CASE WHEN PermissionCode LIKE 'domain.%' THEN COALESCE(NULLIF(ResourceKey, ''), '*') ELSE '' END,
                    CreatedBy,
                    CreatedAt
                FROM permission_grants
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
