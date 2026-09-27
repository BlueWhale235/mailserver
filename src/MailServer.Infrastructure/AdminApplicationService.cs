using System.Data;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MailServer.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

/// <summary>
/// 管理后台首页统计视图，聚合域名、账号、队列和存储使用量。
/// </summary>
public sealed record AdminDashboard(
    int Domains,
    int Accounts,
    int Aliases,
    int PendingDeliveries,
    int FailedDeliveries,
    long StoredBytes,
    int DirectMxHealthyDomains,
    int DirectMxAttentionDomains,
    int DirectMxUncheckedDomains,
    int ExpiringCertificates,
    int DeliveredLast24Hours,
    int FailedLast24Hours,
    DateTimeOffset? OldestPendingDeliveryAt,
    DateTimeOffset GeneratedAt);

public sealed record OverviewTrafficBucket(
    DateTimeOffset From,
    DateTimeOffset To,
    int StoredMessages,
    int Delivered,
    int TemporaryFailures,
    int PermanentFailures);

public sealed record OverviewTraffic(
    int Days,
    DateTimeOffset GeneratedAt,
    long? P95DeliveryMilliseconds,
    IReadOnlyList<OverviewTrafficBucket> Buckets);

public sealed record AccountView(
    Guid Id,
    string Address,
    bool IsEnabled,
    long QuotaBytes,
    long UsedBytes,
    DateTimeOffset CreatedAt,
    int AliasCount);

public sealed record AliasView(
    Guid Id,
    string Source,
    Guid? TargetAccountId,
    string? TargetAddress,
    bool IsEnabled,
    string? LegacyDestinationAddress,
    MailAliasMode Mode,
    bool CanReceive,
    bool CanSend);
public enum ApiKeyStatusFilter
{
    Any = 0,
    Active = 1,
    Revoked = 2,
    Expired = 3
}

public enum AdminUserStatusFilter
{
    Any = 0,
    Enabled = 1,
    Disabled = 2
}

public sealed record ApiKeyView(
    Guid Id,
    string Name,
    string Prefix,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

public sealed record PermissionDomainOption(Guid Id, string Name);

public sealed record DeliveryRawMessageView(
    Guid DeliveryJobId,
    string FileName,
    string Content);
public sealed record DeliveryMessageDownloadView(
    Guid DeliveryJobId,
    string FileName,
    Stream Content);
public sealed record DeliveryAttemptDetail(
    Guid Id,
    int AttemptNumber,
    bool Succeeded,
    bool PermanentFailure,
    string Stage,
    string? MxHost,
    string? RemoteAddress,
    string? SmtpStatusCode,
    string? EnhancedStatusCode,
    string? TlsProtocol,
    string? TlsCipherSuite,
    long DurationMilliseconds,
    string? FailureCategory,
    DateTimeOffset? NextRetryAt,
    string? Response,
    string? SessionSummary,
    DateTimeOffset CreatedAt);
public sealed record DeliveryJobView(
    Guid Id,
    string Sender,
    string Recipient,
    DeliveryStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
public sealed record DeliveryQueueSummary(
    int Pending,
    int Processing,
    int Deferred,
    int Failed,
    int DeliveredLast24Hours,
    int FailedLast24Hours,
    double AverageAttemptMillisecondsLast24Hours);
public sealed record DeliveryBatchItemResult(Guid Id, string Status, string? Detail);
public sealed record DeliveryBatchResult(
    IReadOnlyList<DeliveryBatchItemResult> Items,
    int Succeeded,
    int Failed);
public enum DeliveryJobScope
{
    Any = 0,
    Active = 1,
    History = 2,
    Failed = 3
}
public enum TlsReportDeliveryStatus
{
    Pending = 0,
    Retrying = 1,
    Failed = 2,
    Completed = 3
}
public enum TlsReportDeliveryStatusFilter
{
    Any = 0,
    Pending = 1,
    Retrying = 2,
    Failed = 3,
    Completed = 4
}
public sealed record TlsReportDeliveryView(
    Guid Id,
    string PolicyDomain,
    string ReportUri,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    TlsReportDeliveryStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? CompletedAt,
    string? LastError);
public sealed record TlsReportDeliveryDetail(
    Guid Id,
    string PolicyDomain,
    string ReportUri,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    TlsReportDeliveryStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? CompletedAt,
    string? LastError,
    string ReportJson);
public sealed record TlsReportRetryResult(bool Scheduled, bool Replayed);
public sealed record CreatedApiKey(Guid Id, string Value, DateTimeOffset? ExpiresAt);
public sealed record AdminUserView(
    Guid Id,
    string UserName,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    bool IsPrimary);
public sealed record FirewallRuleView(
    Guid Id,
    string Cidr,
    string? Note,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
public sealed record DomainHealthView(
    int AccountCount,
    int EnabledAccountCount,
    int AliasCount,
    int PendingDeliveries,
    int FailedDeliveries,
    bool HasCertificate,
    DateTimeOffset? CertificateNotAfter,
    bool HasDkim,
    DateTimeOffset? LastAuditAt,
    int PendingQuarantineCount = 0);
public sealed record DomainAuditView(
    Guid Id,
    string Actor,
    string Action,
    string Resource,
    string? Detail,
    DateTimeOffset CreatedAt);
public sealed record ClientConnectionEndpoint(
    string Protocol,
    string HostName,
    int Port,
    ConnectionSecurity Security,
    bool AuthenticationRequired,
    string UserNameFormat,
    string Description);
public sealed record OutboundAddressOption(
    string Address,
    string InterfaceName,
    string AddressFamily);
public sealed record DomainManagementView(
    MailDomain Domain,
    DomainCertificate? Certificate,
    DomainDnsSettings DnsSettings,
    IReadOnlyList<ClientConnectionEndpoint> Connections,
    IReadOnlyList<OutboundAddressOption> OutboundAddressOptions);
public sealed record DomainListItemView(
    Guid Id,
    string Name,
    string? MailHostName,
    bool IsActive,
    DomainDnsCheckStatus DnsStatus,
    DateTimeOffset? DnsCheckedAt,
    bool HasCertificate,
    string? CertificateSubject,
    string? CertificateIssuer,
    DateTimeOffset? CertificateNotBefore,
    DateTimeOffset? CertificateNotAfter,
    string? DkimSelector,
    int AccountCount,
    long QuotaBytes,
    long UsedBytes,
    DateTimeOffset CreatedAt);

public enum DomainExpiryFilter
{
    Any = 0,
    Expired = 1,
    Within7Days = 2,
    Within30Days = 3,
    Within60Days = 4,
    Later = 5
}

public sealed record DomainSearchFilter(
    string? Search,
    bool? IsActive,
    bool? HasCertificate,
    DomainExpiryFilter Expiry = DomainExpiryFilter.Any,
    DomainDnsCheckStatus? DnsStatus = null);
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>
/// 管理端应用服务，承载 JSON API 的业务操作，避免 HTTP 端点直接操作 DbContext。
/// </summary>
public sealed class AdminApplicationService(
    IDbContextFactory<MailDbContext> dbFactory,
    ICredentialService credentials,
    IDeliveryQueue deliveryQueue,
    DnsDiagnosticService dnsDiagnostics,
    IConfiguration configuration,
    SystemSettingsService systemSettings,
    IMessageContentStore contentStore,
    DomainCertificateProvider certificateProvider,
    PermissionService permissions,
    ConfigurationSnapshotCache? configurationCache = null)
{
    public async Task<AdminUser?> AuthenticateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var normalized = userName.Trim().ToLowerInvariant();
        var admin = await db.AdminUsers.SingleOrDefaultAsync(
            x => x.UserName == normalized && x.IsEnabled,
            cancellationToken);
        if (admin is null || !credentials.VerifyPassword(admin.PasswordHash, password, out var upgraded))
        {
            return null;
        }

        if (upgraded is not null)
        {
            // 登录时顺手升级旧密码哈希，降低单独迁移凭据算法的运维成本。
            admin.PasswordHash = upgraded;
            await db.SaveChangesAsync(cancellationToken);
        }

        return admin;
    }

    public async Task<AdminDashboard> GetDashboardAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var last24Hours = now.AddHours(-24);
        var domains = await db.Domains.CountAsync(x => x.DeletedAt == null, cancellationToken);
        var activeDomains = db.Domains.Where(x => x.DeletedAt == null && x.IsActive);
        var healthyDomains = await activeDomains.CountAsync(x =>
            db.DomainDnsChecks.Any(check => check.DomainId == x.Id && check.Status == DomainDnsCheckStatus.Healthy), cancellationToken);
        var attentionDomains = await activeDomains.CountAsync(x =>
            db.DomainDnsChecks.Any(check => check.DomainId == x.Id && check.Status == DomainDnsCheckStatus.Unhealthy), cancellationToken);
        var checkedDomains = await activeDomains.CountAsync(x =>
            db.DomainDnsChecks.Any(check => check.DomainId == x.Id
                && (check.Status == DomainDnsCheckStatus.Healthy || check.Status == DomainDnsCheckStatus.Unhealthy)), cancellationToken);
        var activeDomainCount = await activeDomains.CountAsync(cancellationToken);
        return new AdminDashboard(
            domains,
            await db.Accounts.CountAsync(cancellationToken),
            await db.Aliases.CountAsync(cancellationToken),
            await db.DeliveryJobs.CountAsync(
                x => x.Status == DeliveryStatus.Pending || x.Status == DeliveryStatus.Deferred,
                cancellationToken),
            await db.DeliveryJobs.CountAsync(x => x.Status == DeliveryStatus.Failed, cancellationToken),
            await db.Messages.SumAsync(x => (long?)x.Size, cancellationToken) ?? 0,
            healthyDomains,
            attentionDomains,
            Math.Max(0, activeDomainCount - checkedDomains),
            await db.DomainCertificates.CountAsync(x => x.NotAfter <= now.AddDays(30), cancellationToken),
            await db.DeliveryJobs.CountAsync(x => x.Status == DeliveryStatus.Delivered && x.UpdatedAt >= last24Hours, cancellationToken),
            await db.DeliveryJobs.CountAsync(x => x.Status == DeliveryStatus.Failed && x.UpdatedAt >= last24Hours, cancellationToken),
            await db.DeliveryJobs.AsNoTracking()
                .Where(x => x.Status == DeliveryStatus.Pending || x.Status == DeliveryStatus.Deferred)
                .OrderBy(x => x.CreatedAt)
                .Select(x => (DateTimeOffset?)x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken),
            now);
    }

    public async Task<OverviewTraffic> GetOverviewTrafficAsync(int days, CancellationToken cancellationToken)
    {
        if (days is not (1 or 7))
            throw new ArgumentOutOfRangeException(nameof(days), "Traffic range must be 1 or 7 days.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var bucketCount = days == 1 ? 6 : 7;
        var bucketHours = days == 1 ? 4 : 24;
        var start = now.AddDays(-days);
        var buckets = new List<OverviewTrafficBucket>(bucketCount);
        for (var index = 0; index < bucketCount; index++)
        {
            var from = start.AddHours(index * bucketHours);
            var to = index == bucketCount - 1 ? now : from.AddHours(bucketHours);
            buckets.Add(new OverviewTrafficBucket(
                from,
                to,
                await db.Messages.CountAsync(x => x.ReceivedAt >= from && x.ReceivedAt < to, cancellationToken),
                await db.DeliveryAttempts.CountAsync(x => x.CreatedAt >= from && x.CreatedAt < to && x.Succeeded, cancellationToken),
                await db.DeliveryAttempts.CountAsync(x => x.CreatedAt >= from && x.CreatedAt < to && !x.Succeeded && !x.PermanentFailure, cancellationToken),
                await db.DeliveryAttempts.CountAsync(x => x.CreatedAt >= from && x.CreatedAt < to && !x.Succeeded && x.PermanentFailure, cancellationToken)));
        }

        var successful = db.DeliveryAttempts.AsNoTracking()
            .Where(x => x.CreatedAt >= start && x.CreatedAt < now && x.Succeeded);
        var count = await successful.CountAsync(cancellationToken);
        var p95 = count == 0 ? null : (long?)await successful
            .OrderBy(x => x.DurationMilliseconds)
            .Skip((int)Math.Ceiling(count * 0.95) - 1)
            .Select(x => x.DurationMilliseconds)
            .FirstAsync(cancellationToken);
        return new OverviewTraffic(days, now, p95, buckets);
    }

    public async Task<IReadOnlyList<FirewallRuleView>> GetFirewallRulesAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FirewallRules.AsNoTracking()
            .OrderByDescending(x => x.IsEnabled)
            .ThenBy(x => x.Cidr)
            .Select(x => new FirewallRuleView(x.Id, x.Cidr, x.Note, x.IsEnabled, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<FirewallRuleView>> SearchFirewallRulesAsync(
        string? search,
        bool? isEnabled,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        var normalizedSearch = NullIfWhiteSpace(search)?.ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.FirewallRules.AsNoTracking();
        if (normalizedSearch is not null)
        {
            query = query.Where(x =>
                x.Cidr.ToLower().Contains(normalizedSearch)
                || (x.Note != null && x.Note.ToLower().Contains(normalizedSearch)));
        }
        if (isEnabled is { } enabled) query = query.Where(x => x.IsEnabled == enabled);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.IsEnabled)
            .ThenBy(x => x.Cidr)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new FirewallRuleView(x.Id, x.Cidr, x.Note, x.IsEnabled, x.CreatedAt, x.UpdatedAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<FirewallRuleView>(rows, page, pageSize, total);
    }

    public async Task<FirewallRuleView> CreateFirewallRuleAsync(
        string cidr,
        string? note,
        string actor,
        CancellationToken cancellationToken,
        string? requestId = null)
    {
        var normalized = IpFirewallRules.NormalizeCidr(cidr);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (await db.FirewallRules.AnyAsync(x => x.Cidr == normalized, cancellationToken))
            throw new InvalidOperationException("Firewall rule already exists.");
        var now = DateTimeOffset.UtcNow;
        var rule = new FirewallRule
        {
            Cidr = normalized,
            Note = NullIfWhiteSpace(note),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.FirewallRules.Add(rule);
        AddAudit(db, actor, "firewall.create", $"firewall:{rule.Id}", normalized, requestId);
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Firewall, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        configurationCache?.ObserveCommittedVersion(ConfigurationScopes.Firewall, version);
        return new FirewallRuleView(rule.Id, rule.Cidr, rule.Note, rule.IsEnabled, rule.CreatedAt, rule.UpdatedAt);
    }

    public async Task<FirewallRuleView> UpdateFirewallRuleAsync(
        Guid id,
        bool isEnabled,
        string? note,
        string actor,
        CancellationToken cancellationToken,
        string? requestId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var rule = await db.FirewallRules.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Firewall rule not found.");
        rule.IsEnabled = isEnabled;
        rule.Note = NullIfWhiteSpace(note);
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        AddAudit(db, actor, "firewall.update", $"firewall:{rule.Id}", $"{rule.Cidr}; enabled={isEnabled}", requestId);
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Firewall, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        configurationCache?.ObserveCommittedVersion(ConfigurationScopes.Firewall, version);
        return new FirewallRuleView(rule.Id, rule.Cidr, rule.Note, rule.IsEnabled, rule.CreatedAt, rule.UpdatedAt);
    }

    public async Task DeleteFirewallRuleAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken,
        string? requestId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var rule = await db.FirewallRules.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Firewall rule not found.");
        db.FirewallRules.Remove(rule);
        AddAudit(db, actor, "firewall.delete", $"firewall:{rule.Id}", rule.Cidr, requestId);
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Firewall, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        configurationCache?.ObserveCommittedVersion(ConfigurationScopes.Firewall, version);
    }

    public async Task<IReadOnlyList<MailDomain>> GetDomainsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildDomainQuery(db, null)
            .Include(x => x.Certificate)
            .Include(x => x.DnsSettings)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailDomain>> GetDomainsAsync(
        PermittedDomainScope permittedScope,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildDomainQuery(db, permittedScope)
            .Include(x => x.Certificate)
            .Include(x => x.DnsSettings)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<PermissionDomainOption>> SearchPermissionDomainsAsync(
        string? search,
        int page,
        int pageSize,
        PermittedDomainScope permittedScope,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        var normalizedSearch = NullIfWhiteSpace(search)?.ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = BuildDomainQuery(db, permittedScope);
        if (normalizedSearch is not null)
            query = query.Where(domain => domain.Name.ToLower().Contains(normalizedSearch));
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(domain => domain.Name).ThenBy(domain => domain.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(domain => new PermissionDomainOption(domain.Id, domain.Name))
            .ToListAsync(cancellationToken);
        return new PagedResult<PermissionDomainOption>(items, page, pageSize, total);
    }

    public async Task<IReadOnlyList<PermissionDomainOption>> ResolvePermissionDomainsAsync(
        IReadOnlyCollection<Guid> ids,
        PermittedDomainScope permittedScope,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildDomainQuery(db, permittedScope)
            .Where(domain => ids.Contains(domain.Id))
            .OrderBy(domain => domain.Name)
            .Select(domain => new PermissionDomainOption(domain.Id, domain.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<DomainListItemView>> SearchDomainsAsync(
        DomainSearchFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        await SearchDomainsAsync(filter, page, pageSize, null, cancellationToken);

    public async Task<PagedResult<DomainListItemView>> SearchDomainsAsync(
        DomainSearchFilter filter,
        int page,
        int pageSize,
        PermittedDomainScope? permittedScope,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        var normalizedSearch = NullIfWhiteSpace(filter.Search)?.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = BuildDomainQuery(db, permittedScope);
        if (normalizedSearch is not null)
        {
            query = query.Where(x =>
                x.Name.Contains(normalizedSearch)
                || (x.MailHostName != null && x.MailHostName.Contains(normalizedSearch)));
        }
        if (filter.IsActive is { } isActive) query = query.Where(x => x.IsActive == isActive);
        if (filter.HasCertificate is { } hasCertificate)
            query = query.Where(x => (x.Certificate != null) == hasCertificate);
        if (filter.DnsStatus is { } dnsStatus)
        {
            query = query.Where(x =>
                db.DomainDnsChecks.Any(check => check.DomainId == x.Id && check.Status == dnsStatus)
                || (dnsStatus == DomainDnsCheckStatus.PendingConfiguration
                    && !db.DomainDnsChecks.Any(check => check.DomainId == x.Id)));
        }
        query = filter.Expiry switch
        {
            DomainExpiryFilter.Expired => query.Where(x => x.Certificate != null && x.Certificate.NotAfter < now),
            DomainExpiryFilter.Within7Days => query.Where(x => x.Certificate != null && x.Certificate.NotAfter >= now && x.Certificate.NotAfter <= now.AddDays(7)),
            DomainExpiryFilter.Within30Days => query.Where(x => x.Certificate != null && x.Certificate.NotAfter > now.AddDays(7) && x.Certificate.NotAfter <= now.AddDays(30)),
            DomainExpiryFilter.Within60Days => query.Where(x => x.Certificate != null && x.Certificate.NotAfter > now.AddDays(30) && x.Certificate.NotAfter <= now.AddDays(60)),
            DomainExpiryFilter.Later => query.Where(x => x.Certificate != null && x.Certificate.NotAfter > now.AddDays(60)),
            _ => query
        };

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(x => x.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new DomainListItemView(
                x.Id,
                x.Name,
                x.MailHostName,
                x.IsActive,
                db.DomainDnsChecks.Where(check => check.DomainId == x.Id)
                    .Select(check => (DomainDnsCheckStatus?)check.Status)
                    .SingleOrDefault() ?? DomainDnsCheckStatus.PendingConfiguration,
                db.DomainDnsChecks.Where(check => check.DomainId == x.Id)
                    .Select(check => check.CheckedAt)
                    .SingleOrDefault(),
                x.Certificate != null,
                x.Certificate == null ? null : x.Certificate.Subject,
                x.Certificate == null ? null : x.Certificate.Issuer,
                x.Certificate == null ? null : x.Certificate.NotBefore,
                x.Certificate == null ? null : x.Certificate.NotAfter,
                x.DkimSelector,
                db.Accounts.Count(account => account.DomainId == x.Id),
                x.QuotaBytes,
                x.UsedBytes,
                x.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<DomainListItemView>(rows, page, pageSize, total);
    }

    public Task<PagedResult<DomainListItemView>> SearchDomainsAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        SearchDomainsAsync(new DomainSearchFilter(search, null, null), page, pageSize, cancellationToken);

    public async Task DeleteDomainsAsync(
        IReadOnlyCollection<Guid> domainIds,
        string actor,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeDomainIds(domainIds);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var domains = await db.Domains.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        if (domains.Count != ids.Length) throw new KeyNotFoundException("One or more domains were not found.");
        var activeNames = domains.Where(x => x.IsActive).Select(x => x.Name).ToArray();
        if (activeNames.Length > 0)
            throw new InvalidOperationException($"Disable domains before deletion: {string.Join(", ", activeNames)}.");
        var now = DateTimeOffset.UtcNow;
        var purgeAfter = now.AddDays(Math.Clamp(configuration.GetValue("Maintenance:SoftDeleteRetentionDays", 30), 1, 3650));
        foreach (var domain in domains)
        {
            domain.DeletedAt = now; domain.DeletedBy = actor; domain.PurgeAfter = purgeAfter; domain.IsActive = false;
            var accounts = await db.Accounts.Where(x => x.DomainId == domain.Id).ToListAsync(cancellationToken);
            var accountIds = accounts.Select(x => x.Id).ToArray();
            foreach (var account in accounts)
            {
                account.DeletedAt = now; account.DeletedBy = actor; account.PurgeAfter = purgeAfter; account.IsEnabled = false;
            }
            var mailboxes = await db.Mailboxes.Where(x => accountIds.Contains(x.AccountId)).ToListAsync(cancellationToken);
            var mailboxIds = mailboxes.Select(x => x.Id).ToArray();
            foreach (var mailbox in mailboxes)
            {
                mailbox.DeletedAt = now; mailbox.DeletedBy = actor; mailbox.PurgeAfter = purgeAfter;
            }
            foreach (var message in await db.MailboxMessages.Where(x => mailboxIds.Contains(x.MailboxId)).ToListAsync(cancellationToken))
            {
                message.DeletedAt = now; message.DeletedBy = actor; message.PurgeAfter = purgeAfter;
            }
            db.CleanupQueue.Add(new CleanupQueueItem
            {
                ResourceType = CleanupResourceType.Domain, ResourceId = domain.Id, DomainId = domain.Id,
                EstimatedBytes = domain.UsedBytes, CreatedAt = now, PurgeAfter = purgeAfter
            });
            AddAudit(db, actor, "domain.soft-delete", $"domain:{domain.Id}", $"{domain.Name}; purgeAfter={purgeAfter:O}");
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        certificateProvider.Invalidate();
    }

    private async Task DeleteDomainsImmediatelyAsync(
        IReadOnlyCollection<Guid> domainIds,
        string actor,
        CancellationToken cancellationToken)
    {
        var ids = NormalizeDomainIds(domainIds);
        var securityRoot = configuration["SecurityAssets:RootPath"] ?? "security-assets";
        var managedFiles = new List<string>();
        var contentKeys = new List<string>();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var domains = await db.Domains.Include(x => x.Certificate)
            .Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        if (domains.Count != ids.Length) throw new KeyNotFoundException("One or more domains were not found.");
        var activeNames = domains.Where(x => x.IsActive).Select(x => x.Name).ToArray();
        if (activeNames.Length > 0)
            throw new InvalidOperationException($"请先停用域名再删除：{string.Join("、", activeNames)}。");
        var messageIds = new HashSet<Guid>();
        foreach (var domain in domains)
        {
            var accountIds = await db.Accounts.Where(x => x.DomainId == domain.Id).Select(x => x.Id).ToListAsync(cancellationToken);
            var mailboxMessageIds = await db.MailboxMessages
                .Where(x => accountIds.Contains(x.Mailbox.AccountId))
                .Select(x => x.MessageId).ToListAsync(cancellationToken);
            messageIds.UnionWith(mailboxMessageIds);
            var suffix = "@" + domain.Name;
            var jobs = await db.DeliveryJobs
                .Where(x => x.Sender.EndsWith(suffix) || x.Recipient.EndsWith(suffix))
                .ToListAsync(cancellationToken);
            messageIds.UnionWith(jobs.Select(x => x.MessageId));
            db.DeliveryJobs.RemoveRange(jobs);
            if (domain.Certificate?.Source == CertificateSource.ManagedUpload)
            {
                managedFiles.Add(domain.Certificate.CertificatePath);
                managedFiles.Add(domain.Certificate.PrivateKeyPath);
            }
            if (!string.IsNullOrWhiteSpace(domain.DkimPrivateKeyPath)) managedFiles.Add(domain.DkimPrivateKeyPath);
            AddAudit(db, actor, "domain.delete", $"domain:{domain.Id}", domain.Name);
        }

        await db.Aliases.Where(x => ids.Contains(x.DomainId)).ExecuteDeleteAsync(cancellationToken);
        db.Domains.RemoveRange(domains);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var messageId in messageIds)
        {
            var referenced = await db.DeliveryJobs.AnyAsync(x => x.MessageId == messageId, cancellationToken)
                             || await db.MailboxMessages.AnyAsync(x => x.MessageId == messageId, cancellationToken)
                             || await db.SentCopyJobs.AnyAsync(x => x.MessageId == messageId, cancellationToken);
            if (referenced) continue;
            var message = await db.Messages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
            if (message is null) continue;
            contentKeys.Add(message.ContentKey);
            db.Messages.Remove(message);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var path in managedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            SecurityAssetFiles.DeleteManagedFile(securityRoot, path);
        certificateProvider.Invalidate();
        foreach (var key in contentKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            await contentStore.DeleteIfUnreferencedAsync(key, cancellationToken);
    }

    public async Task<DomainManagementView> GetDomainAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.AsNoTracking()
            .Include(x => x.Certificate)
            .Include(x => x.DnsSettings)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        return new DomainManagementView(
            domain,
            domain.Certificate,
            domain.DnsSettings ?? new DomainDnsSettings { DomainId = domain.Id },
            BuildConnections(domain),
            BuildOutboundAddressOptions(domain));
    }

    public async Task<DomainHealthView> GetDomainHealthAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.AsNoTracking()
            .Include(x => x.Certificate)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");

        var suffix = "@" + domain.Name;
        var auditResources = await GetDomainAuditResourcesAsync(db, id, cancellationToken);
        var lastAuditAt = await db.AuditRecords.AsNoTracking()
            .Where(x => auditResources.Contains(x.Resource))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => (DateTimeOffset?)x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new DomainHealthView(
            await db.Accounts.CountAsync(x => x.DomainId == id, cancellationToken),
            await db.Accounts.CountAsync(x => x.DomainId == id && x.IsEnabled, cancellationToken),
            await db.Aliases.CountAsync(x => x.DomainId == id && x.IsEnabled && x.TargetAccountId != null, cancellationToken),
            await db.DeliveryJobs.CountAsync(
                x => (x.Status == DeliveryStatus.Pending || x.Status == DeliveryStatus.Deferred)
                     && (x.Sender.EndsWith(suffix) || x.Recipient.EndsWith(suffix)),
                cancellationToken),
            await db.DeliveryJobs.CountAsync(
                x => x.Status == DeliveryStatus.Failed
                     && (x.Sender.EndsWith(suffix) || x.Recipient.EndsWith(suffix)),
                cancellationToken),
            domain.Certificate is not null,
            domain.Certificate?.NotAfter,
            !string.IsNullOrWhiteSpace(domain.DkimSelector) && !string.IsNullOrWhiteSpace(domain.DkimPublicKey),
            lastAuditAt,
            await db.QuarantineRecords.CountAsync(
                x => x.DomainId == id && x.Status == QuarantineStatus.PendingReview,
                cancellationToken));
    }

    public async Task<MailDomain> CreateDomainAsync(
        string name,
        string? dkimSelector,
        string? dkimPrivateKeyPath,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeDomain(name);
        var defaultMailHostName = $"mail.{normalized}";
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Domains.AnyAsync(x => x.Name == normalized, cancellationToken))
            throw new InvalidOperationException("Domain already exists.");
        if (await db.Domains.AnyAsync(x => x.MailHostName == defaultMailHostName, cancellationToken))
            throw new InvalidOperationException($"Default mail host name '{defaultMailHostName}' is already assigned.");
        var domain = new MailDomain
        {
            Name = normalized,
            MailHostName = defaultMailHostName,
            SmtpHostName = defaultMailHostName,
            DirectDeliverSameDomain = true,
            DkimSelector = NullIfWhiteSpace(dkimSelector),
            DkimPrivateKeyPath = NullIfWhiteSpace(dkimPrivateKeyPath),
            DnsSettings = new DomainDnsSettings()
        };
        db.Domains.Add(domain);
        AddAudit(db, actor, "domain.create", $"domain:{domain.Id}", normalized);
        await db.SaveChangesAsync(cancellationToken);
        return domain;
    }

    public async Task<MailDomain> UpdateDomainDetailsAsync(
        Guid id,
        string mailHostName,
        bool isActive,
        string actor,
        CancellationToken cancellationToken)
        => await UpdateDomainDetailsAsync(
            id,
            mailHostName,
            isActive,
            null,
            null,
            null,
            actor,
            cancellationToken);

    public async Task<MailDomain> UpdateDomainDetailsAsync(
        Guid id,
        string mailHostName,
        bool isActive,
        string? smtpHostName,
        string? outboundIpAddresses,
        string actor,
        CancellationToken cancellationToken)
        => await UpdateDomainDetailsAsync(
            id,
            mailHostName,
            isActive,
            smtpHostName,
            outboundIpAddresses,
            null,
            actor,
            cancellationToken);

    public async Task<MailDomain> UpdateDomainDetailsAsync(
        Guid id,
        string mailHostName,
        bool isActive,
        string? smtpHostName,
        string? outboundIpAddresses,
        bool? directDeliverSameDomain,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalizedHost = NormalizeDomain(mailHostName);
        var normalizedSmtpHost = string.IsNullOrWhiteSpace(smtpHostName)
            ? null
            : NormalizeDomain(smtpHostName);
        var normalizedOutboundIps = NormalizeOutboundIpAddresses(outboundIpAddresses);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.Include(x => x.Certificate)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        if (await db.Domains.AnyAsync(
                x => x.Id != id && x.MailHostName == normalizedHost,
                cancellationToken))
            throw new InvalidOperationException("Mail host name is already assigned to another domain.");
        if (domain.Certificate is not null
            && !domain.Certificate.SubjectAlternativeNames
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(name => CertificateNames.Matches(name, normalizedHost)))
            throw new InvalidOperationException("The active TLS certificate does not cover the new mail host name. Replace or delete the certificate first.");
        domain.MailHostName = normalizedHost;
        domain.SmtpHostName = normalizedSmtpHost;
        domain.OutboundIpAddresses = normalizedOutboundIps;
        if (directDeliverSameDomain is not null)
            domain.DirectDeliverSameDomain = directDeliverSameDomain.Value;
        domain.IsActive = isActive;
        AddAudit(db, actor, "domain.details.update", $"domain:{id}", normalizedHost);
        await db.SaveChangesAsync(cancellationToken);
        certificateProvider.Invalidate();
        return domain;
    }

    public async Task<MailDomain> UpdateDomainAsync(
        Guid id,
        bool? isActive,
        string? dkimSelector,
        string? dkimPrivateKeyPath,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        if (isActive is not null) domain.IsActive = isActive.Value;
        if (dkimSelector is not null) domain.DkimSelector = NullIfWhiteSpace(dkimSelector);
        if (dkimPrivateKeyPath is not null) domain.DkimPrivateKeyPath = NullIfWhiteSpace(dkimPrivateKeyPath);
        AddAudit(db, actor, "domain.update", $"domain:{id}", null);
        await db.SaveChangesAsync(cancellationToken);
        if (isActive is not null) certificateProvider.Invalidate();
        return domain;
    }

    public async Task<MailDomain> UpdateDomainQuotaAsync(
        Guid id,
        long quotaBytes,
        string actor,
        CancellationToken cancellationToken)
    {
        if (quotaBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(quotaBytes), "Domain quota must be greater than zero.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        var usedBytes = domain.UsedBytes;
        if (quotaBytes < usedBytes)
            throw new InvalidOperationException("Domain quota cannot be lower than the currently used capacity.");
        var largestAccountQuota = await db.Accounts
            .Where(x => x.DomainId == id)
            .MaxAsync(x => (long?)x.QuotaBytes, cancellationToken) ?? 0;
        if (quotaBytes < largestAccountQuota)
            throw new InvalidOperationException("Domain quota cannot be lower than an account quota.");
        domain.QuotaBytes = quotaBytes;
        AddAudit(db, actor, "domain.quota.update", $"domain:{id}", quotaBytes.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return domain;
    }

    public async Task<IReadOnlyList<AccountView>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildAccountQuery(db, null)
            .OrderBy(x => x.Domain.Name)
            .ThenBy(x => x.LocalPart)
            .Select(x => new AccountView(
                x.Id,
                x.LocalPart + "@" + x.Domain.Name,
                x.IsEnabled,
                x.QuotaBytes,
                x.UsedBytes,
                x.CreatedAt,
                db.Aliases.Count(alias => alias.TargetAccountId == x.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AccountView>> GetAccountsAsync(
        PermittedDomainScope permittedScope,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildAccountQuery(db, permittedScope)
            .OrderBy(x => x.Domain.Name)
            .ThenBy(x => x.LocalPart)
            .Select(x => new AccountView(
                x.Id,
                x.LocalPart + "@" + x.Domain.Name,
                x.IsEnabled,
                x.QuotaBytes,
                x.UsedBytes,
                x.CreatedAt,
                db.Aliases.Count(alias => alias.TargetAccountId == x.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AccountView>> GetDomainAccountsAsync(
        Guid domainId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domainName = await db.Domains.AsNoTracking()
            .Where(x => x.Id == domainId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        return await db.Accounts.AsNoTracking()
            .Where(x => x.DomainId == domainId)
            .OrderByDescending(x => x.IsEnabled)
            .ThenBy(x => x.LocalPart)
            .Select(x => new AccountView(
                x.Id,
                x.LocalPart + "@" + domainName,
                x.IsEnabled,
                x.QuotaBytes,
                x.UsedBytes,
                x.CreatedAt,
                db.Aliases.Count(alias => alias.TargetAccountId == x.Id)))
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<AccountView>> SearchDomainAccountsAsync(
        Guid domainId,
        string? search,
        bool? isEnabled,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domainName = await db.Domains.AsNoTracking().Where(x => x.Id == domainId)
            .Select(x => x.Name).SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        var query = db.Accounts.AsNoTracking().Where(x => x.DomainId == domainId);
        if (isEnabled is not null) query = query.Where(x => x.IsEnabled == isEnabled.Value);
        var normalizedSearch = search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
            query = query.Where(x => (x.LocalPart + "@" + domainName).Contains(normalizedSearch));
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.IsEnabled).ThenBy(x => x.LocalPart)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new AccountView(
                x.Id,
                x.LocalPart + "@" + domainName,
                x.IsEnabled,
                x.QuotaBytes,
                x.UsedBytes,
                x.CreatedAt,
                db.Aliases.Count(alias => alias.TargetAccountId == x.Id)))
            .ToListAsync(cancellationToken);
        return new PagedResult<AccountView>(rows, page, pageSize, total);
    }

    public async Task<AccountView> CreateAccountAsync(
        string address,
        string password,
        long? quotaBytes,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!TrySplitAddress(address, out var localPart, out var domainName))
            throw new FormatException("邮箱地址格式无效。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Name == domainName, cancellationToken)
            ?? throw new InvalidOperationException("Domain does not exist.");
        var result = await CreateAccountCoreAsync(db, domain, localPart, password, quotaBytes, actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<AccountView> CreateDomainAccountAsync(
        Guid domainId,
        string localPart,
        string password,
        long? quotaBytes,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Id == domainId, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        var normalizedLocalPart = NormalizeLocalPartForDomain(localPart, domain.Name);
        var result = await CreateAccountCoreAsync(db, domain, normalizedLocalPart, password, quotaBytes, actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task UpdateAccountAsync(
        Guid id,
        bool? enabled,
        long? quotaBytes,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts
            .Include(x => x.Domain)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Account not found.");
        if (enabled is not null) account.IsEnabled = enabled.Value;
        if (quotaBytes is not null)
        {
            if (quotaBytes <= 0) throw new ArgumentOutOfRangeException(nameof(quotaBytes));
            if (quotaBytes.Value > account.Domain.QuotaBytes)
                throw new InvalidOperationException("账号配额不能超过邮件域配额。");
            var usedBytes = account.UsedBytes;
            if (quotaBytes.Value < usedBytes)
                throw new InvalidOperationException("账号配额不能小于当前已用容量。");
            account.QuotaBytes = quotaBytes.Value;
        }
        AddAudit(db, actor, "account.update", $"account:{id}", null);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAccountAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var account = await db.Accounts.Include(x => x.Domain).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Account not found.");
        if (account.IsEnabled) throw new InvalidOperationException("请先停用账号，再执行软删除。");
        var now = DateTimeOffset.UtcNow;
        var purgeAfter = now.AddDays(Math.Clamp(configuration.GetValue("Maintenance:SoftDeleteRetentionDays", 30), 1, 3650));
        account.DeletedAt = now; account.DeletedBy = actor; account.PurgeAfter = purgeAfter;
        var mailboxes = await db.Mailboxes.Where(x => x.AccountId == id).ToListAsync(cancellationToken);
        foreach (var mailbox in mailboxes)
        {
            mailbox.DeletedAt = now; mailbox.DeletedBy = actor; mailbox.PurgeAfter = purgeAfter;
        }
        var mailboxIds = mailboxes.Select(x => x.Id).ToArray();
        foreach (var message in await db.MailboxMessages.Where(x => mailboxIds.Contains(x.MailboxId)).ToListAsync(cancellationToken))
        {
            message.DeletedAt = now; message.DeletedBy = actor; message.PurgeAfter = purgeAfter;
        }
        db.CleanupQueue.Add(new CleanupQueueItem
        {
            ResourceType = CleanupResourceType.Account,
            ResourceId = account.Id,
            AccountId = account.Id,
            DomainId = account.DomainId,
            EstimatedBytes = account.UsedBytes,
            CreatedAt = now,
            PurgeAfter = purgeAfter
        });
        AddAudit(db, actor, "account.soft-delete", $"account:{account.Id}", $"purgeAfter={purgeAfter:O}");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DisableDomainAccountsAsync(
        Guid domainId,
        IReadOnlyCollection<Guid> accountIds,
        string actor,
        CancellationToken cancellationToken)
    {
        var ids = accountIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await db.Accounts
            .Where(x => x.DomainId == domainId && ids.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (accounts.Count != ids.Length) throw new KeyNotFoundException("One or more accounts were not found in this domain.");
        foreach (var account in accounts) account.IsEnabled = false;
        AddAudit(db, actor, "account.batch-disable", $"domain:{domainId}", $"count={accounts.Count}");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task EnableDomainAccountsAsync(
        Guid domainId,
        IReadOnlyCollection<Guid> accountIds,
        string actor,
        CancellationToken cancellationToken)
    {
        var ids = accountIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var accounts = await db.Accounts
            .Where(x => x.DomainId == domainId && ids.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (accounts.Count != ids.Length) throw new KeyNotFoundException("One or more accounts were not found in this domain.");
        foreach (var account in accounts) account.IsEnabled = true;
        AddAudit(db, actor, "account.batch-enable", $"domain:{domainId}", $"count={accounts.Count}");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDomainAccountsAsync(
        Guid domainId,
        IReadOnlyCollection<Guid> accountIds,
        string actor,
        CancellationToken cancellationToken)
    {
        var ids = accountIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var accounts = await db.Accounts.Include(x => x.Domain)
            .Where(x => x.DomainId == domainId && ids.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (accounts.Count != ids.Length) throw new KeyNotFoundException("One or more accounts were not found in this domain.");
        if (accounts.Any(x => x.IsEnabled)) throw new InvalidOperationException("请先停用全部所选账号，再执行软删除。");

        var now = DateTimeOffset.UtcNow;
        var purgeAfter = now.AddDays(Math.Clamp(configuration.GetValue("Maintenance:SoftDeleteRetentionDays", 30), 1, 3650));
        var mailboxes = await db.Mailboxes.Where(x => ids.Contains(x.AccountId)).ToListAsync(cancellationToken);
        var mailboxIds = mailboxes.Select(x => x.Id).ToArray();
        var messages = await db.MailboxMessages.Where(x => mailboxIds.Contains(x.MailboxId)).ToListAsync(cancellationToken);
        foreach (var account in accounts)
        {
            account.DeletedAt = now; account.DeletedBy = actor; account.PurgeAfter = purgeAfter;
            db.CleanupQueue.Add(new CleanupQueueItem
            {
                ResourceType = CleanupResourceType.Account,
                ResourceId = account.Id,
                AccountId = account.Id,
                DomainId = account.DomainId,
                EstimatedBytes = account.UsedBytes,
                CreatedAt = now,
                PurgeAfter = purgeAfter
            });
        }
        foreach (var mailbox in mailboxes) { mailbox.DeletedAt = now; mailbox.DeletedBy = actor; mailbox.PurgeAfter = purgeAfter; }
        foreach (var message in messages) { message.DeletedAt = now; message.DeletedBy = actor; message.PurgeAfter = purgeAfter; }
        AddAudit(db, actor, "account.batch-soft-delete", $"domain:{domainId}", $"count={accounts.Count};purgeAfter={purgeAfter:O}");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetAccountPasswordAsync(
        Guid id,
        string password,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Account not found.");
        account.PasswordHash = credentials.HashPassword(password);
        AddAudit(db, actor, "account.password", $"account:{id}", null);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AliasView>> GetAliasesAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildAliasQuery(db, null)
            .Include(x => x.TargetAccount).ThenInclude(x => x!.Domain)
            .OrderBy(x => x.Domain.Name)
            .ThenBy(x => x.SourceLocalPart)
            .Select(x => new AliasView(
                x.Id,
                x.SourceLocalPart + "@" + x.Domain.Name,
                x.TargetAccountId,
                x.TargetAccount == null ? null : x.TargetAccount.LocalPart + "@" + x.TargetAccount.Domain.Name,
                x.IsEnabled && x.Mode != MailAliasMode.Disabled && x.TargetAccountId != null && x.TargetAccount!.IsEnabled,
                x.DestinationAddress,
                x.Mode,
                x.IsEnabled && x.Mode != MailAliasMode.Disabled && x.TargetAccountId != null && x.TargetAccount!.IsEnabled,
                x.IsEnabled && x.Mode == MailAliasMode.SendReceive && x.TargetAccountId != null && x.TargetAccount!.IsEnabled))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AliasView>> GetAliasesAsync(
        PermittedDomainScope permittedScope,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await BuildAliasQuery(db, permittedScope)
            .Include(x => x.TargetAccount).ThenInclude(x => x!.Domain)
            .OrderBy(x => x.Domain.Name)
            .ThenBy(x => x.SourceLocalPart)
            .Select(x => new AliasView(
                x.Id,
                x.SourceLocalPart + "@" + x.Domain.Name,
                x.TargetAccountId,
                x.TargetAccount == null ? null : x.TargetAccount.LocalPart + "@" + x.TargetAccount.Domain.Name,
                x.IsEnabled && x.Mode != MailAliasMode.Disabled && x.TargetAccountId != null && x.TargetAccount!.IsEnabled,
                x.DestinationAddress,
                x.Mode,
                x.IsEnabled && x.Mode != MailAliasMode.Disabled && x.TargetAccountId != null && x.TargetAccount!.IsEnabled,
                x.IsEnabled && x.Mode == MailAliasMode.SendReceive && x.TargetAccountId != null && x.TargetAccount!.IsEnabled))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AliasView>> GetDomainAliasesAsync(
        Guid domainId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domainName = await db.Domains.AsNoTracking()
            .Where(x => x.Id == domainId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        return await db.Aliases.AsNoTracking()
            .Include(x => x.TargetAccount).ThenInclude(x => x!.Domain)
            .Where(x => x.DomainId == domainId)
            .OrderBy(x => x.SourceLocalPart)
            .Select(x => new AliasView(
                x.Id,
                x.SourceLocalPart + "@" + domainName,
                x.TargetAccountId,
                x.TargetAccount == null ? null : x.TargetAccount.LocalPart + "@" + x.TargetAccount.Domain.Name,
                x.IsEnabled && x.Mode != MailAliasMode.Disabled && x.TargetAccountId != null && x.TargetAccount!.IsEnabled,
                x.DestinationAddress,
                x.Mode,
                x.IsEnabled && x.Mode != MailAliasMode.Disabled && x.TargetAccountId != null && x.TargetAccount!.IsEnabled,
                x.IsEnabled && x.Mode == MailAliasMode.SendReceive && x.TargetAccountId != null && x.TargetAccount!.IsEnabled))
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<AliasView>> SearchDomainAliasesAsync(
        Guid domainId,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domainName = await db.Domains.AsNoTracking().Where(x => x.Id == domainId)
            .Select(x => x.Name).SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        var query = db.Aliases.AsNoTracking()
            .Include(x => x.TargetAccount).ThenInclude(x => x!.Domain)
            .Where(x => x.DomainId == domainId);
        var normalizedSearch = search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(x => (x.SourceLocalPart + "@" + domainName).Contains(normalizedSearch)
                || (x.DestinationAddress != null && x.DestinationAddress.Contains(normalizedSearch))
                || (x.TargetAccount != null && (x.TargetAccount.LocalPart + "@" + x.TargetAccount.Domain.Name).Contains(normalizedSearch)));
        }
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.SourceLocalPart)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new AliasView(
                x.Id,
                x.SourceLocalPart + "@" + domainName,
                x.TargetAccountId,
                x.TargetAccount == null ? null : x.TargetAccount.LocalPart + "@" + x.TargetAccount.Domain.Name,
                x.IsEnabled && x.Mode != MailAliasMode.Disabled && x.TargetAccountId != null && x.TargetAccount!.IsEnabled,
                x.DestinationAddress,
                x.Mode,
                x.IsEnabled && x.Mode != MailAliasMode.Disabled && x.TargetAccountId != null && x.TargetAccount!.IsEnabled,
                x.IsEnabled && x.Mode == MailAliasMode.SendReceive && x.TargetAccountId != null && x.TargetAccount!.IsEnabled))
            .ToListAsync(cancellationToken);
        return new PagedResult<AliasView>(rows, page, pageSize, total);
    }

    public async Task<AliasView> CreateAliasAsync(
        string source,
        Guid targetAccountId,
        string actor,
        CancellationToken cancellationToken)
        => await CreateAliasAsync(source, targetAccountId, MailAliasMode.SendReceive, actor, cancellationToken);

    public async Task<AliasView> CreateAliasAsync(
        string source,
        Guid targetAccountId,
        MailAliasMode mode,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!TrySplitAddress(source, out var localPart, out var domainName))
            throw new FormatException("Invalid alias address.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Name == domainName, cancellationToken)
            ?? throw new InvalidOperationException("Domain does not exist.");
        var result = await CreateAliasCoreAsync(db, domain, localPart, targetAccountId, mode, actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<AliasView> CreateDomainAliasAsync(
        Guid domainId,
        string sourceLocalPart,
        Guid targetAccountId,
        string actor,
        CancellationToken cancellationToken)
        => await CreateDomainAliasAsync(domainId, sourceLocalPart, targetAccountId, MailAliasMode.SendReceive, actor, cancellationToken);

    public async Task<AliasView> CreateDomainAliasAsync(
        Guid domainId,
        string sourceLocalPart,
        Guid targetAccountId,
        MailAliasMode mode,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Id == domainId, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        var localPart = NormalizeLocalPartForDomain(sourceLocalPart, domain.Name);
        var result = await CreateAliasCoreAsync(db, domain, localPart, targetAccountId, mode, actor, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<AliasView> CreateDomainAliasAsync(
        Guid domainId,
        string sourceLocalPart,
        string targetAddress,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!TrySplitAddress(targetAddress, out var localPart, out var domainName))
            throw new FormatException("Invalid target account address.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var targetAccountId = await db.Accounts
            .Where(x => x.DomainId == domainId && x.LocalPart == localPart && x.Domain.Name == domainName)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Target account does not exist in this domain.");
        return await CreateDomainAliasAsync(domainId, sourceLocalPart, targetAccountId, actor, cancellationToken);
    }

    public async Task<AliasView> RebindAliasAsync(
        Guid id,
        Guid targetAccountId,
        string actor,
        CancellationToken cancellationToken)
        => await UpdateAliasAsync(id, null, targetAccountId, MailAliasMode.SendReceive, actor, cancellationToken);

    public async Task<AliasView> UpdateAliasAsync(
        Guid id,
        string? sourceLocalPart,
        Guid? targetAccountId,
        MailAliasMode? mode,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var alias = await db.Aliases.Include(x => x.Domain)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Alias not found.");
        var normalizedLocalPart = sourceLocalPart is null
            ? alias.SourceLocalPart
            : NormalizeLocalPartForDomain(sourceLocalPart, alias.Domain.Name);
        if (await db.Aliases.AnyAsync(
                x => x.Id != alias.Id && x.DomainId == alias.DomainId && x.SourceLocalPart == normalizedLocalPart,
                cancellationToken))
            throw new InvalidOperationException("别名地址已存在。");
        if (await db.Accounts.AnyAsync(
                x => x.DomainId == alias.DomainId && x.LocalPart == normalizedLocalPart,
                cancellationToken))
            throw new InvalidOperationException("该地址已被邮箱账号使用。");
        var requestedMode = mode ?? alias.Mode;
        var requestedTargetId = targetAccountId ?? alias.TargetAccountId;
        if (requestedTargetId is null)
        {
            if (requestedMode != MailAliasMode.Disabled)
                throw new InvalidOperationException("启用别名必须绑定目标账号。");
            alias.SourceLocalPart = normalizedLocalPart;
            alias.Mode = MailAliasMode.Disabled;
            alias.IsEnabled = false;
            AddAudit(db, actor, "alias.update", $"alias:{alias.Id}", $"{normalizedLocalPart}:Disabled");
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToAliasView(alias, null);
        }
        var target = await db.Accounts.Include(x => x.Domain)
            .SingleOrDefaultAsync(x => x.Id == requestedTargetId.Value, cancellationToken)
            ?? throw new KeyNotFoundException("目标账号不存在。");
        if (target.DomainId != alias.DomainId)
            throw new InvalidOperationException("别名与目标账号必须属于同一邮件域。");
        if (!target.IsEnabled && requestedMode != MailAliasMode.Disabled)
            throw new InvalidOperationException("目标账号已停用，请选择启用的账号。");
        alias.SourceLocalPart = normalizedLocalPart;
        alias.TargetAccountId = target.Id;
        alias.DestinationAddress = $"{target.LocalPart}@{target.Domain.Name}";
        alias.Mode = requestedMode;
        alias.IsEnabled = requestedMode != MailAliasMode.Disabled;
        AddAudit(db, actor, "alias.update", $"alias:{alias.Id}", $"{normalizedLocalPart}:{requestedMode}->{alias.DestinationAddress}");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToAliasView(alias, target);
    }

    public async Task DeleteAliasAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var alias = await db.Aliases.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Alias not found.");
        db.Aliases.Remove(alias);
        AddAudit(db, actor, "alias.delete", $"alias:{id}", null);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateDomainAliasesModeAsync(
        Guid domainId,
        IReadOnlyCollection<Guid> aliasIds,
        MailAliasMode mode,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var ids = aliasIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var aliases = await db.Aliases.Include(x => x.TargetAccount)
            .Where(x => x.DomainId == domainId && ids.Contains(x.Id))
            .ToListAsync(cancellationToken);
        if (aliases.Count != ids.Length) throw new KeyNotFoundException("One or more aliases were not found in this domain.");
        if (mode != MailAliasMode.Disabled && aliases.Any(x => x.TargetAccountId == null || x.TargetAccount == null || !x.TargetAccount.IsEnabled))
            throw new InvalidOperationException("Enabled aliases require an enabled target account.");
        foreach (var alias in aliases) { alias.Mode = mode; alias.IsEnabled = mode != MailAliasMode.Disabled; }
        AddAudit(db, actor, "alias.batch-mode", $"domain:{domainId}", $"count={aliases.Count};mode={mode}");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDomainAliasesAsync(
        Guid domainId,
        IReadOnlyCollection<Guid> aliasIds,
        string actor,
        CancellationToken cancellationToken)
    {
        var ids = aliasIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var aliases = await db.Aliases.Where(x => x.DomainId == domainId && ids.Contains(x.Id)).ToListAsync(cancellationToken);
        if (aliases.Count != ids.Length) throw new KeyNotFoundException("One or more aliases were not found in this domain.");
        db.Aliases.RemoveRange(aliases);
        AddAudit(db, actor, "alias.batch-delete", $"domain:{domainId}", $"count={aliases.Count}");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DomainAuditView>> GetDomainAuditAsync(
        Guid domainId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Domains.AnyAsync(x => x.Id == domainId, cancellationToken))
            throw new KeyNotFoundException("Domain not found.");
        var resources = await GetDomainAuditResourcesAsync(db, domainId, cancellationToken);
        resources.Add("settings:mailboxes");
        return await db.AuditRecords.AsNoTracking()
            .Where(x => resources.Contains(x.Resource))
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new DomainAuditView(
                x.Id,
                x.Actor,
                x.Action,
                x.Resource,
                x.Detail,
                x.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ApiKeyView>> GetApiKeysAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ApiKeys.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new ApiKeyView(
                x.Id,
                x.Name,
                x.Prefix,
                x.CreatedBy,
                x.CreatedAt,
                x.LastUsedAt,
                x.ExpiresAt,
                x.RevokedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<ApiKeyView>> SearchApiKeysAsync(
        string? search,
        ApiKeyStatusFilter status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        var normalizedSearch = NullIfWhiteSpace(search)?.ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ApiKeys.AsNoTracking();
        if (normalizedSearch is not null)
        {
            query = query.Where(x =>
                x.Name.ToLower().Contains(normalizedSearch)
                || x.Prefix.ToLower().Contains(normalizedSearch)
                || x.CreatedBy.ToLower().Contains(normalizedSearch));
        }

        query = status switch
        {
            ApiKeyStatusFilter.Active => query.Where(x => x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now)),
            ApiKeyStatusFilter.Revoked => query.Where(x => x.RevokedAt != null),
            ApiKeyStatusFilter.Expired => query.Where(x => x.RevokedAt == null && x.ExpiresAt != null && x.ExpiresAt <= now),
            _ => query
        };

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ApiKeyView(
                x.Id,
                x.Name,
                x.Prefix,
                x.CreatedBy,
                x.CreatedAt,
                x.LastUsedAt,
                x.ExpiresAt,
                x.RevokedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<ApiKeyView>(rows, page, pageSize, total);
    }

    public async Task<CreatedApiKey> CreateApiKeyAsync(
        string name,
        IReadOnlyCollection<PermissionGrantInput> grants,
        DateTimeOffset? expiresAt,
        string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new FormatException("API key name is required.");
        var normalizedGrants = PermissionService.NormalizeGrantInputs(grants);
        if (normalizedGrants.Count == 0)
            throw new InvalidOperationException("At least one permission grant is required.");
        var record = new ApiKeyRecord
        {
            Name = name.Trim(),
            Prefix = string.Empty,
            SecretHash = string.Empty,
            CreatedBy = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
            ExpiresAt = expiresAt
        };
        var value = AssignApiKeySecret(record);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ValidatePermissionGrantDomainsAsync(db, normalizedGrants, cancellationToken);
        db.ApiKeys.Add(record);
        AddPermissionGrants(db, PermissionSubjectType.ApiKey, record.Id, normalizedGrants, actor);
        AddAudit(db, actor, "api-key.create", $"api-key:{record.Id}", record.Name);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedApiKey(record.Id, value, expiresAt);
    }

    public Task<CreatedApiKey> CreateApiKeyAsync(
        string name,
        DateTimeOffset? expiresAt,
        string actor,
        CancellationToken cancellationToken) =>
        CreateApiKeyAsync(name, [PermissionService.FullAccessGrant()], expiresAt, actor, cancellationToken);

    public async Task<CreatedApiKey> RefreshApiKeyAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = await db.ApiKeys.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("API key not found.");
        var value = AssignApiKeySecret(key);
        key.LastUsedAt = null;
        key.RevokedAt = null;
        AddAudit(db, actor, "api-key.refresh", $"api-key:{id}", key.Name);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedApiKey(key.Id, value, key.ExpiresAt);
    }

    public async Task RevokeApiKeyAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = await db.ApiKeys.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("API key not found.");
        key.RevokedAt = DateTimeOffset.UtcNow;
        AddAudit(db, actor, "api-key.revoke", $"api-key:{id}", key.Name);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteApiKeyAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = await db.ApiKeys.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("API key not found.");
        await db.PermissionGrants
            .Where(x => x.SubjectType == PermissionSubjectType.ApiKey && x.SubjectId == id)
            .ExecuteDeleteAsync(cancellationToken);
        db.ApiKeys.Remove(key);
        AddAudit(db, actor, "api-key.delete", $"api-key:{id}", key.Name);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryJob>> GetDeliveryJobsAsync(
        DeliveryStatus? status,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.DeliveryJobs.AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        var jobs = await query.OrderByDescending(x => x.UpdatedAt).Take(200).ToListAsync(cancellationToken);
        foreach (var job in jobs) job.LastError = DeliverySessionSummary.SanitizeResponse(job.LastError);
        return jobs;
    }

    public async Task<IReadOnlyList<DeliveryAttemptDetail>> GetDeliveryAttemptsAsync(
        Guid deliveryJobId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.DeliveryJobs.AsNoTracking().AnyAsync(x => x.Id == deliveryJobId, cancellationToken))
            throw new KeyNotFoundException("Delivery job not found.");
        var attempts = await db.DeliveryAttempts.AsNoTracking()
            .Where(x => x.DeliveryJobId == deliveryJobId)
            .OrderByDescending(x => x.AttemptNumber)
            .Select(x => new DeliveryAttemptDetail(
                x.Id, x.AttemptNumber, x.Succeeded, x.PermanentFailure, x.Stage,
                x.RemoteHost, x.RemoteAddress, x.SmtpStatusCode, x.EnhancedStatusCode,
                x.TlsProtocol, x.TlsCipherSuite, x.DurationMilliseconds, x.FailureCategory,
                x.NextRetryAt, x.Response, x.SessionSummary, x.CreatedAt))
            .ToListAsync(cancellationToken);
        // 旧版本保存的摘要也必须在 API 边界脱敏，避免升级后继续泄露历史数据。
        return attempts.Select(attempt => attempt with
        {
            Response = DeliverySessionSummary.SanitizeResponse(attempt.Response),
            SessionSummary = DeliverySessionSummary.Sanitize(attempt.SessionSummary)
        }).ToArray();
    }

    public async Task<PagedResult<DeliveryJobView>> SearchDeliveryJobsAsync(
        string? search,
        DeliveryStatus? status,
        DeliveryJobScope scope,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        Guid? routeDomainId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        var normalizedSearch = NullIfWhiteSpace(search);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.DeliveryJobs.AsNoTracking();
        if (routeDomainId is not null) query = query.Where(x => x.RouteDomainId == routeDomainId);
        query = scope switch
        {
            DeliveryJobScope.Active => query.Where(x => x.Status != DeliveryStatus.Delivered),
            DeliveryJobScope.History => query.Where(x => x.Status == DeliveryStatus.Delivered),
            DeliveryJobScope.Failed => query.Where(x => x.Status == DeliveryStatus.Failed),
            _ => query
        };
        if (status is not null) query = query.Where(x => x.Status == status);
        if (normalizedSearch is not null)
        {
            query = query.Where(x =>
                x.Sender.Contains(normalizedSearch)
                || x.Recipient.Contains(normalizedSearch)
                || (x.LastError != null && x.LastError.Contains(normalizedSearch)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new DeliveryJobView(
                x.Id,
                x.Sender,
                x.Recipient,
                x.Status,
                x.Attempts,
                x.NextAttemptAt,
                x.LastError,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);
        return new PagedResult<DeliveryJobView>(rows.Select(row => row with
        {
            LastError = DeliverySessionSummary.SanitizeResponse(row.LastError)
        }).ToArray(), page, pageSize, total);
    }

    public async Task<DeliveryQueueSummary> GetDeliveryQueueSummaryAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        return new DeliveryQueueSummary(
            await db.DeliveryJobs.CountAsync(x => x.Status == DeliveryStatus.Pending, cancellationToken),
            await db.DeliveryJobs.CountAsync(x => x.Status == DeliveryStatus.Processing, cancellationToken),
            await db.DeliveryJobs.CountAsync(x => x.Status == DeliveryStatus.Deferred, cancellationToken),
            await db.DeliveryJobs.CountAsync(x => x.Status == DeliveryStatus.Failed, cancellationToken),
            await db.DeliveryJobs.CountAsync(x => x.Status == DeliveryStatus.Delivered && x.UpdatedAt >= since, cancellationToken),
            await db.DeliveryJobs.CountAsync(x => x.Status == DeliveryStatus.Failed && x.UpdatedAt >= since, cancellationToken),
            await db.DeliveryAttempts.Where(x => x.CreatedAt >= since)
                .AverageAsync(x => (double?)x.DurationMilliseconds, cancellationToken) ?? 0);
    }

    public async Task<PagedResult<TlsReportDeliveryView>> SearchTlsReportDeliveriesAsync(
        string? search,
        TlsReportDeliveryStatusFilter status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        var normalizedSearch = NullIfWhiteSpace(search);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.TlsReportDeliveries.AsNoTracking();
        if (normalizedSearch is not null)
        {
            query = query.Where(x => x.PolicyDomain.Contains(normalizedSearch)
                || x.ReportUri.Contains(normalizedSearch)
                || (x.LastError != null && x.LastError.Contains(normalizedSearch)));
        }

        query = status switch
        {
            TlsReportDeliveryStatusFilter.Pending => query.Where(x => x.CompletedAt == null && x.Attempts == 0),
            TlsReportDeliveryStatusFilter.Retrying => query.Where(x => x.CompletedAt == null && x.Attempts > 0 && x.Attempts < 5),
            TlsReportDeliveryStatusFilter.Failed => query.Where(x => x.CompletedAt == null && x.Attempts >= 5),
            TlsReportDeliveryStatusFilter.Completed => query.Where(x => x.CompletedAt != null),
            _ => query
        };

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.PeriodEnd)
            .ThenBy(x => x.PolicyDomain)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new TlsReportDeliveryView(
                x.Id,
                x.PolicyDomain,
                x.ReportUri,
                x.PeriodStart,
                x.PeriodEnd,
                x.CompletedAt != null
                    ? TlsReportDeliveryStatus.Completed
                    : x.Attempts >= 5
                        ? TlsReportDeliveryStatus.Failed
                        : x.Attempts > 0
                            ? TlsReportDeliveryStatus.Retrying
                            : TlsReportDeliveryStatus.Pending,
                x.Attempts,
                x.NextAttemptAt,
                x.CompletedAt,
                x.LastError))
            .ToListAsync(cancellationToken);
        return new PagedResult<TlsReportDeliveryView>(rows, page, pageSize, total);
    }

    public async Task<TlsReportDeliveryDetail> GetTlsReportDeliveryAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var report = await db.TlsReportDeliveries.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("TLS report delivery not found.");
        return new TlsReportDeliveryDetail(
            report.Id,
            report.PolicyDomain,
            report.ReportUri,
            report.PeriodStart,
            report.PeriodEnd,
            GetTlsReportStatus(report),
            report.Attempts,
            report.NextAttemptAt,
            report.CompletedAt,
            report.LastError,
            report.ReportJson);
    }

    public async Task<TlsReportRetryResult> RetryTlsReportDeliveryAsync(
        Guid id,
        string? idempotencyKey,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(idempotencyKey, out var key))
            throw new FormatException("TLS report retry requires a UUID Idempotency-Key header.");
        var normalizedKey = key.ToString("N");
        var resource = $"tls-report:{id}";
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.AuditRecords.AsNoTracking().AnyAsync(x => x.Action == "tls-report.retry"
            && x.Resource == resource && x.RequestId == normalizedKey, cancellationToken))
            return new TlsReportRetryResult(false, true);

        var now = DateTimeOffset.UtcNow;
        if (!db.Database.IsRelational())
        {
            var report = await db.TlsReportDeliveries.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("TLS report delivery not found.");
            if (report.LastManualRetryKey == normalizedKey) return new TlsReportRetryResult(false, true);
            if (report.CompletedAt is not null || report.LeaseUntil > now)
                throw new InvalidOperationException("Completed or actively sending TLS reports cannot be retried.");
            if (report.Attempts == 0 || report.Attempts < 5 && report.NextAttemptAt <= now)
                return new TlsReportRetryResult(false, false);
            report.Attempts = 0;
            report.NextAttemptAt = now;
            report.LastError = null;
            report.LastManualRetryKey = normalizedKey;
            AddAudit(db, actor, "tls-report.retry", resource, report.PolicyDomain, normalizedKey);
            await db.SaveChangesAsync(cancellationToken);
            return new TlsReportRetryResult(true, false);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var changed = await db.TlsReportDeliveries
            .Where(x => x.Id == id && x.CompletedAt == null
                && (x.LeaseUntil == null || x.LeaseUntil <= now)
                && x.LastManualRetryKey != normalizedKey
                && (x.Attempts >= 5 || x.Attempts > 0 && x.NextAttemptAt > now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Attempts, 0)
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LastError, (string?)null)
                .SetProperty(x => x.LastManualRetryKey, normalizedKey), cancellationToken);
        var current = await db.TlsReportDeliveries.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.PolicyDomain, x.CompletedAt, x.LeaseUntil, x.LastManualRetryKey })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("TLS report delivery not found.");
        if (changed == 0)
        {
            if (current.LastManualRetryKey == normalizedKey) return new TlsReportRetryResult(false, true);
            if (current.CompletedAt is not null || current.LeaseUntil > now)
                throw new InvalidOperationException("Completed or actively sending TLS reports cannot be retried.");
            return new TlsReportRetryResult(false, false);
        }
        AddAudit(db, actor, "tls-report.retry", resource, current.PolicyDomain, normalizedKey);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TlsReportRetryResult(true, false);
    }

    private static TlsReportDeliveryStatus GetTlsReportStatus(TlsReportDelivery report) =>
        report.CompletedAt is not null
            ? TlsReportDeliveryStatus.Completed
            : report.Attempts >= 5
                ? TlsReportDeliveryStatus.Failed
                : report.Attempts > 0
                    ? TlsReportDeliveryStatus.Retrying
                    : TlsReportDeliveryStatus.Pending;

    public async Task<DeliveryRawMessageView> GetDeliveryRawMessageAsync(
        Guid id,
        CancellationToken cancellationToken,
        Guid? routeDomainId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DeliveryJobs.AsNoTracking()
            .Include(x => x.Message)
            .SingleOrDefaultAsync(
                x => x.Id == id && (routeDomainId == null || x.RouteDomainId == routeDomainId),
                cancellationToken)
            ?? throw new KeyNotFoundException("Delivery job not found.");

        try
        {
            await using var content = await contentStore.OpenReadAsync(job.Message.ContentKey, cancellationToken);
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var raw = await reader.ReadToEndAsync(cancellationToken);
            return new DeliveryRawMessageView(
                job.Id,
                $"delivery-{job.Id:N}.eml",
                raw);
        }
        catch (FileNotFoundException exception)
        {
            throw new FileNotFoundException("Original message content was not found or has been cleaned up.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileNotFoundException("Original message content was not found or has been cleaned up.", exception);
        }
    }

    public async Task<DeliveryRawMessageView> GetDeliveryMessageHeadersAsync(
        Guid id,
        CancellationToken cancellationToken,
        Guid? routeDomainId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DeliveryJobs.AsNoTracking()
            .Include(x => x.Message)
            .SingleOrDefaultAsync(
                x => x.Id == id && (routeDomainId == null || x.RouteDomainId == routeDomainId),
                cancellationToken)
            ?? throw new KeyNotFoundException("Delivery job not found.");

        try
        {
            await using var content = await contentStore.OpenReadAsync(job.Message.ContentKey, cancellationToken);
            var headers = await ReadMessageHeadersAsync(content, cancellationToken);
            return new DeliveryRawMessageView(
                job.Id,
                $"delivery-{job.Id:N}-headers.txt",
                headers);
        }
        catch (FileNotFoundException exception)
        {
            throw new FileNotFoundException("Original message content was not found or has been cleaned up.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileNotFoundException("Original message content was not found or has been cleaned up.", exception);
        }
    }

    public async Task<DeliveryMessageDownloadView> OpenDeliveryMessageDownloadAsync(
        Guid id,
        CancellationToken cancellationToken,
        Guid? routeDomainId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DeliveryJobs.AsNoTracking()
            .Include(x => x.Message)
            .SingleOrDefaultAsync(
                x => x.Id == id && (routeDomainId == null || x.RouteDomainId == routeDomainId),
                cancellationToken)
            ?? throw new KeyNotFoundException("Delivery job not found.");

        try
        {
            var content = await contentStore.OpenReadAsync(job.Message.ContentKey, cancellationToken);
            return new DeliveryMessageDownloadView(job.Id, $"delivery-{job.Id:N}.eml", content);
        }
        catch (FileNotFoundException exception)
        {
            throw new FileNotFoundException("Original message content was not found or has been cleaned up.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileNotFoundException("Original message content was not found or has been cleaned up.", exception);
        }
    }

    private static async Task<string> ReadMessageHeadersAsync(Stream content, CancellationToken cancellationToken)
    {
        const int maximumHeaderLength = 256 * 1024;
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var headers = new StringBuilder();

        while (headers.Length < maximumHeaderLength)
        {
            var remaining = maximumHeaderLength - headers.Length;
            var read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                headers.Append(buffer[index]);
                if (headers.Length >= 4 && headers[^4] == '\r' && headers[^3] == '\n' && headers[^2] == '\r' && headers[^1] == '\n')
                {
                    return headers.ToString().TrimEnd('\r', '\n');
                }

                if (headers.Length >= 2 && headers[^2] == '\n' && headers[^1] == '\n')
                {
                    return headers.ToString().TrimEnd('\r', '\n');
                }
            }
        }

        return headers.ToString().TrimEnd('\r', '\n');
    }

    public async Task RetryDeliveryAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken,
        Guid? routeDomainId = null,
        int? expectedAttempts = null)
    {
        if (routeDomainId is not null)
        {
            await EnsureDeliveryBelongsToDomainAsync(id, routeDomainId.Value, cancellationToken);
        }

        await deliveryQueue.RetryNowAsync(id, cancellationToken, expectedAttempts);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        AddAudit(db, actor, "queue.retry", $"delivery:{id}", null);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDeliveryAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken,
        Guid? routeDomainId = null)
    {
        string contentKey;
        Guid messageId;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var job = await db.DeliveryJobs.Include(x => x.Message)
                .SingleOrDefaultAsync(
                    x => x.Id == id && (routeDomainId == null || x.RouteDomainId == routeDomainId),
                    cancellationToken)
                ?? throw new KeyNotFoundException("Delivery job not found.");
            contentKey = job.Message.ContentKey;
            messageId = job.MessageId;
            if (!db.Database.IsRelational())
            {
                if (job.Status == DeliveryStatus.Processing || job.LeaseOwner is not null)
                    throw new DeliveryStateConflictException("A job held by a delivery worker cannot be deleted.");
                db.DeliveryJobs.Remove(job);
            }
            else
            {
                var deleted = await db.DeliveryJobs
                    .Where(x => x.Id == id && x.Status != DeliveryStatus.Processing && x.LeaseOwner == null)
                    .ExecuteDeleteAsync(cancellationToken);
                if (deleted == 0)
                    throw new DeliveryStateConflictException("A job held by a delivery worker cannot be deleted.");
                db.Entry(job).State = EntityState.Detached;
            }
            AddAudit(db, actor, "queue.delete", $"delivery:{id}", job.Recipient);
            await db.SaveChangesAsync(cancellationToken);

            var stillReferenced = await db.DeliveryJobs.AnyAsync(x => x.MessageId == messageId, cancellationToken)
                                  || await db.MailboxMessages.AnyAsync(x => x.MessageId == messageId, cancellationToken)
                                  || await db.SentCopyJobs.AnyAsync(x => x.MessageId == messageId, cancellationToken);
            if (!stillReferenced)
            {
                var message = await db.Messages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
                if (message is not null)
                {
                    db.Messages.Remove(message);
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await contentStore.DeleteIfUnreferencedAsync(contentKey, cancellationToken);
    }

    public async Task<DeliveryBatchResult> BatchRetryDeliveriesAsync(
        IReadOnlyList<Guid> ids, string actor, CancellationToken cancellationToken, Guid? routeDomainId = null,
        IReadOnlyDictionary<Guid, int>? expectedAttempts = null) =>
        await BatchDeliveriesAsync(ids, actor, "retry", routeDomainId, cancellationToken, expectedAttempts);

    public async Task<DeliveryBatchResult> BatchDeleteDeliveriesAsync(
        IReadOnlyList<Guid> ids, string actor, CancellationToken cancellationToken, Guid? routeDomainId = null) =>
        await BatchDeliveriesAsync(ids, actor, "delete", routeDomainId, cancellationToken, null);

    private async Task<DeliveryBatchResult> BatchDeliveriesAsync(
        IReadOnlyList<Guid> ids, string actor, string operation, Guid? routeDomainId, CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, int>? expectedAttempts)
    {
        if (ids is null || ids.Count is < 1 or > 100 || ids.Any(x => x == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Provide 1 to 100 distinct, nonempty delivery IDs.", nameof(ids));
        if (expectedAttempts is not null && (expectedAttempts.Count != ids.Count
            || ids.Any(id => !expectedAttempts.TryGetValue(id, out var attempts) || attempts < 0)))
            throw new ArgumentException("Every delivery ID must have a nonnegative expected attempt count.", nameof(expectedAttempts));

        var items = new List<DeliveryBatchItemResult>(ids.Count);
        foreach (var id in ids)
        {
            try
            {
                if (operation == "retry")
                    await RetryDeliveryAsync(id, actor, cancellationToken, routeDomainId,
                        expectedAttempts is null ? null : expectedAttempts[id]);
                else
                    await DeleteDeliveryAsync(id, actor, cancellationToken, routeDomainId);
                items.Add(new DeliveryBatchItemResult(id, "Succeeded", null));
            }
            catch (KeyNotFoundException)
            {
                items.Add(new DeliveryBatchItemResult(id, "NotFound", "投递任务不存在或无权访问。"));
            }
            catch (DeliveryStateConflictException)
            {
                items.Add(new DeliveryBatchItemResult(id, "Conflict", "任务状态已变化，请刷新列表后重试。"));
            }
        }

        var succeeded = items.Count(x => x.Status == "Succeeded");
        // 审计仅保存目标 ID 的摘要，避免将收件地址或邮件内容写入批量审计。
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(',', ids.Order()))))[..16];
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        AddAudit(db, actor, $"queue.batch-{operation}", $"delivery-batch:{digest}",
            $"total={ids.Count};succeeded={succeeded};failed={items.Count - succeeded};domain={routeDomainId}");
        await db.SaveChangesAsync(cancellationToken);
        return new DeliveryBatchResult(items, succeeded, items.Count - succeeded);
    }

    private async Task EnsureDeliveryBelongsToDomainAsync(
        Guid deliveryId,
        Guid domainId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.DeliveryJobs.AsNoTracking()
                .AnyAsync(x => x.Id == deliveryId && x.RouteDomainId == domainId, cancellationToken))
        {
            throw new KeyNotFoundException("Delivery job not found in this domain.");
        }
    }

    public Task<DeliverySettingsView> GetDeliverySettingsAsync(CancellationToken cancellationToken) =>
        systemSettings.GetDeliverySettingsAsync(cancellationToken);

    public async Task<DeliverySettingsView> UpdateDeliverySettingsAsync(
        DeliverySettingsView settings,
        string actor,
        CancellationToken cancellationToken)
    {
        var saved = await systemSettings.SaveDeliverySettingsAsync(settings, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        AddAudit(db, actor, "settings.delivery.update", "settings:delivery", null);
        await db.SaveChangesAsync(cancellationToken);
        return saved;
    }

    public Task<SmtpSettingsView> GetSmtpSettingsAsync(CancellationToken cancellationToken) =>
        systemSettings.GetSmtpSettingsAsync(cancellationToken);

    public async Task<SmtpSettingsView> UpdateSmtpSettingsAsync(
        SmtpSettingsView settings,
        string actor,
        CancellationToken cancellationToken)
    {
        var saved = await systemSettings.SaveSmtpSettingsAsync(settings, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        AddAudit(db, actor, "settings.smtp.update", "settings:smtp", $"timeout={settings.CommandTimeoutSeconds}");
        await db.SaveChangesAsync(cancellationToken);
        return saved;
    }

    public Task<MailboxSettingsView> GetMailboxSettingsAsync(CancellationToken cancellationToken) =>
        systemSettings.GetMailboxSettingsAsync(cancellationToken);

    public async Task<MailboxSettingsView> UpdateMailboxSettingsAsync(
        MailboxSettingsView settings,
        string actor,
        CancellationToken cancellationToken)
    {
        var saved = await systemSettings.SaveMailboxSettingsAsync(settings, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        AddAudit(db, actor, "settings.mailboxes.update", "settings:mailboxes", string.Join('\n', saved.DefaultFolders));
        await db.SaveChangesAsync(cancellationToken);
        return saved;
    }

    public Task<MailboxSettingsView> GetDomainMailboxSettingsAsync(Guid domainId, CancellationToken cancellationToken) =>
        systemSettings.GetDomainMailboxSettingsAsync(domainId, cancellationToken);

    public async Task<MailboxSettingsView> UpdateDomainMailboxSettingsAsync(
        Guid domainId,
        MailboxSettingsView settings,
        string actor,
        CancellationToken cancellationToken)
    {
        var current = await systemSettings.GetDomainMailboxSettingsAsync(domainId, cancellationToken);
        var requested = MailboxNames.NormalizeDefaults(settings.DefaultFolders);
        var removed = current.DefaultFolders
            .Where(folder => !MailboxNames.IsSystemFolder(folder))
            .Where(folder => !requested.Contains(folder, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (removed.Length > 0)
            throw new InvalidOperationException("Folders must be removed through the safe deletion workflow.");
        var saved = await systemSettings.SaveDomainMailboxSettingsAsync(domainId, settings, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        AddAudit(db, actor, "settings.mailboxes.update", $"domain:{domainId}", string.Join('\n', saved.DefaultFolders));
        await db.SaveChangesAsync(cancellationToken);
        return saved;
    }

    public Task<MessageTemplateSettingsView> GetMessageTemplateSettingsAsync(
        CancellationToken cancellationToken) =>
        systemSettings.GetMessageTemplateSettingsAsync(cancellationToken);

    public async Task<MessageTemplateSettingsView> UpdateMessageTemplateSettingsAsync(
        MessageTemplateSettingsView settings,
        string actor,
        CancellationToken cancellationToken)
    {
        var saved = await systemSettings.SaveMessageTemplateSettingsAsync(settings, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        AddAudit(db, actor, "settings.message-templates.update", "settings:message-templates", null);
        await db.SaveChangesAsync(cancellationToken);
        return saved;
    }

    public Task<CertificateSelectionSettingsView> GetCertificateSelectionSettingsAsync(
        CancellationToken cancellationToken) =>
        systemSettings.GetCertificateSelectionSettingsAsync(cancellationToken);

    public async Task<CertificateSelectionSettingsView> UpdateCertificateSelectionSettingsAsync(
        CertificateSelectionSettingsView settings,
        string actor,
        CancellationToken cancellationToken)
    {
        var saved = await systemSettings.SaveCertificateSelectionSettingsAsync(settings, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        AddAudit(
            db,
            actor,
            "settings.certificates.update",
            "settings:certificates",
            $"{saved.DefaultMailHostName ?? string.Empty};fallback={saved.AllowSingleCertificateFallback}");
        await db.SaveChangesAsync(cancellationToken);
        certificateProvider.Invalidate();
        return saved;
    }

    public Task<DnsDiagnosticResult> DiagnoseDnsAsync(
        string domain,
        string? selector,
        CancellationToken cancellationToken) =>
        dnsDiagnostics.InspectAsync(domain, selector, cancellationToken);

    public async Task<Guid> CreateAdminAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        var admin = await CreateAdminUserAsync(userName, password, true, "system", cancellationToken);
        return admin.Id;
    }

    public async Task<PagedResult<AdminUserView>> SearchAdminUsersAsync(
        string? search,
        AdminUserStatusFilter status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        var normalizedSearch = NullIfWhiteSpace(search);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var primaryAdminId = await db.AdminUsers.AsNoTracking()
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var query = db.AdminUsers.AsNoTracking();
        if (normalizedSearch is not null)
        {
            query = query.Where(x => x.UserName.Contains(normalizedSearch));
        }

        query = status switch
        {
            AdminUserStatusFilter.Enabled => query.Where(x => x.IsEnabled),
            AdminUserStatusFilter.Disabled => query.Where(x => !x.IsEnabled),
            _ => query
        };

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.IsEnabled)
            .ThenBy(x => x.UserName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AdminUserView(x.Id, x.UserName, x.IsEnabled, x.CreatedAt, x.Id == primaryAdminId))
            .ToListAsync(cancellationToken);
        return new PagedResult<AdminUserView>(rows, page, pageSize, total);
    }

    public async Task<AdminUserView> CreateAdminUserAsync(
        string userName,
        string password,
        bool isEnabled,
        string actor,
        CancellationToken cancellationToken) =>
        await CreateAdminUserAsync(
            userName,
            password,
            isEnabled,
            [PermissionService.FullAccessGrant()],
            actor,
            cancellationToken);

    public async Task<AdminUserView> CreateAdminUserAsync(
        string userName,
        string password,
        bool isEnabled,
        IReadOnlyCollection<PermissionGrantInput> grants,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        var normalizedGrants = PermissionService.NormalizeGrantInputs(grants);
        if (normalizedGrants.Count == 0)
            throw new InvalidOperationException("At least one permission grant is required.");
        var normalized = userName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) throw new FormatException("Administrator name is required.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var isFirstAdmin = !await db.AdminUsers.AnyAsync(cancellationToken);
        if (isFirstAdmin)
        {
            normalizedGrants = [PermissionService.FullAccessGrant()];
            isEnabled = true;
        }
        await ValidatePermissionGrantDomainsAsync(db, normalizedGrants, cancellationToken);
        if (await db.AdminUsers.AnyAsync(x => x.UserName == normalized, cancellationToken))
            throw new InvalidOperationException("Administrator already exists.");
        var admin = new AdminUser
        {
            UserName = normalized,
            PasswordHash = credentials.HashPassword(password),
            IsEnabled = isEnabled
        };
        db.AdminUsers.Add(admin);
        AddPermissionGrants(db, PermissionSubjectType.AdminUser, admin.Id, normalizedGrants, actor);
        AddAudit(db, actor, "admin.create", $"admin:{admin.Id}", admin.UserName);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminUserView(admin.Id, admin.UserName, admin.IsEnabled, admin.CreatedAt, isFirstAdmin);
    }

    public async Task<AdminUserView> UpdateAdminUserAsync(
        Guid id,
        string userName,
        bool isEnabled,
        string? newPassword,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = userName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) throw new FormatException("Administrator name is required.");
        if (!string.IsNullOrWhiteSpace(newPassword)) ValidatePassword(newPassword);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var admin = await db.AdminUsers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Administrator not found.");
        var isPrimary = await IsPrimaryAdminAsync(db, id, cancellationToken);
        if (isPrimary && (!string.Equals(admin.UserName, normalized, StringComparison.Ordinal) || !isEnabled))
            throw new InvalidOperationException("The initial administrator name and enabled status cannot be modified.");
        if (await db.AdminUsers.AnyAsync(x => x.Id != id && x.UserName == normalized, cancellationToken))
            throw new InvalidOperationException("Administrator already exists.");
        if (!isEnabled && admin.IsEnabled)
        {
            await permissions.EnsureAdminCanBeDisabledOrDeletedAsync(id, cancellationToken);
            var remainingEnabled = await db.AdminUsers.CountAsync(x => x.IsEnabled && x.Id != id, cancellationToken);
            if (remainingEnabled == 0)
                throw new InvalidOperationException("At least one enabled administrator is required.");
        }

        admin.UserName = normalized;
        admin.IsEnabled = isEnabled;
        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            admin.PasswordHash = credentials.HashPassword(newPassword);
        }

        AddAudit(db, actor, "admin.update", $"admin:{id}", admin.UserName);
        await db.SaveChangesAsync(cancellationToken);
        return new AdminUserView(admin.Id, admin.UserName, admin.IsEnabled, admin.CreatedAt, isPrimary);
    }

    public async Task DeleteAdminUserAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var admin = await db.AdminUsers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Administrator not found.");
        if (await IsPrimaryAdminAsync(db, id, cancellationToken))
            throw new InvalidOperationException("The initial administrator cannot be deleted.");
        if (admin.IsEnabled)
        {
            await permissions.EnsureAdminCanBeDisabledOrDeletedAsync(id, cancellationToken);
            var remainingEnabled = await db.AdminUsers.CountAsync(x => x.IsEnabled && x.Id != id, cancellationToken);
            if (remainingEnabled == 0)
                throw new InvalidOperationException("At least one enabled administrator is required.");
        }

        await db.PermissionGrants
            .Where(x => x.SubjectType == PermissionSubjectType.AdminUser && x.SubjectId == id)
            .ExecuteDeleteAsync(cancellationToken);
        db.AdminUsers.Remove(admin);
        AddAudit(db, actor, "admin.delete", $"admin:{id}", admin.UserName);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<bool> IsPrimaryAdminAsync(
        MailDbContext db,
        Guid adminId,
        CancellationToken cancellationToken)
    {
        var primaryAdminId = await db.AdminUsers.AsNoTracking()
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return primaryAdminId == adminId;
    }

    public async Task ChangeAdminPasswordAsync(
        Guid id,
        string? currentPassword,
        string newPassword,
        bool requireCurrentPassword,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidatePassword(newPassword);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var admin = await db.AdminUsers.SingleOrDefaultAsync(x => x.Id == id && x.IsEnabled, cancellationToken)
            ?? throw new KeyNotFoundException("Administrator not found.");
        if (requireCurrentPassword
            && (currentPassword is null
                || !credentials.VerifyPassword(admin.PasswordHash, currentPassword, out _)))
            throw new UnauthorizedAccessException("Current password is invalid.");
        admin.PasswordHash = credentials.HashPassword(newPassword);
        AddAudit(db, actor, "admin.password", $"admin:{id}", null);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void AddAudit(
        MailDbContext db,
        string actor,
        string action,
        string resource,
        string? detail,
        string? requestId = null) =>
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
            Action = action,
            Resource = resource,
            Detail = detail,
            RequestId = requestId
        });

    private static void AddPermissionGrants(
        MailDbContext db,
        PermissionSubjectType subjectType,
        Guid subjectId,
        IReadOnlyList<PermissionGrantInput> grants,
        string actor)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var grant in grants)
        {
            db.PermissionGrants.Add(new PermissionGrant
            {
                SubjectType = subjectType,
                SubjectId = subjectId,
                PermissionCode = grant.PermissionCode,
                ResourceType = grant.ResourceType ?? string.Empty,
                ResourceKey = grant.ResourceKey ?? string.Empty,
                CreatedBy = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
                CreatedAt = now
            });
        }
    }

    private static async Task ValidatePermissionGrantDomainsAsync(
        MailDbContext db,
        IReadOnlyList<PermissionGrantInput> grants,
        CancellationToken cancellationToken)
    {
        var domainIds = grants
            .Where(x => string.Equals(x.ResourceType, PermissionService.DomainResourceType, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(x.ResourceKey, PermissionService.AllResources, StringComparison.Ordinal))
            .Select(x => Guid.Parse(x.ResourceKey!))
            .Distinct()
            .ToArray();
        if (domainIds.Length == 0) return;
        var existing = await db.Domains.CountAsync(x => domainIds.Contains(x.Id), cancellationToken);
        if (existing != domainIds.Length)
            throw new KeyNotFoundException("One or more permission domains were not found.");
    }

    private static IQueryable<MailDomain> BuildDomainQuery(
        MailDbContext db,
        PermittedDomainScope? permittedScope)
    {
        var query = db.Domains.AsNoTracking();
        if (permittedScope is null || permittedScope.AllDomains)
        {
            return query;
        }

        if (permittedScope.DomainIds.Count == 0)
        {
            return query.Where(_ => false);
        }

        var ids = permittedScope.DomainIds.ToArray();
        return query.Where(x => ids.Contains(x.Id));
    }

    private static IQueryable<MailAccount> BuildAccountQuery(
        MailDbContext db,
        PermittedDomainScope? permittedScope)
    {
        var query = db.Accounts.AsNoTracking();
        if (permittedScope is null || permittedScope.AllDomains) return query;
        if (permittedScope.DomainIds.Count == 0) return query.Where(_ => false);
        var ids = permittedScope.DomainIds.ToArray();
        return query.Where(x => ids.Contains(x.DomainId));
    }

    private static IQueryable<MailAlias> BuildAliasQuery(
        MailDbContext db,
        PermittedDomainScope? permittedScope)
    {
        var query = db.Aliases.AsNoTracking();
        if (permittedScope is null || permittedScope.AllDomains) return query;
        if (permittedScope.DomainIds.Count == 0) return query.Where(_ => false);
        var ids = permittedScope.DomainIds.ToArray();
        return query.Where(x => ids.Contains(x.DomainId));
    }

    private string AssignApiKeySecret(ApiKeyRecord record)
    {
        var prefix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        record.Prefix = prefix;
        record.SecretHash = credentials.HashApiSecret(secret);
        return $"msk_{prefix}_{secret}";
    }

    private async Task<AccountView> CreateAccountCoreAsync(
        MailDbContext db,
        MailDomain domain,
        string localPart,
        string password,
        long? quotaBytes,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidatePassword(password);
        var mailboxSettings = await systemSettings.GetDomainMailboxSettingsAsync(domain.Id, cancellationToken);
        if (await db.Accounts.AnyAsync(
                x => x.DomainId == domain.Id && x.LocalPart == localPart,
                cancellationToken))
            throw new InvalidOperationException("账号地址已存在。");
        if (await db.Aliases.AnyAsync(
                x => x.DomainId == domain.Id && x.SourceLocalPart == localPart,
                cancellationToken))
            throw new InvalidOperationException("该地址已被邮箱别名使用。");

        var account = new MailAccount
        {
            DomainId = domain.Id,
            LocalPart = localPart,
            PasswordHash = credentials.HashPassword(password),
            QuotaBytes = quotaBytes is > 0 ? quotaBytes.Value : Math.Min(1_073_741_824, domain.QuotaBytes)
        };
        if (account.QuotaBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(quotaBytes), "Account quota must be greater than zero.");
        if (account.QuotaBytes > domain.QuotaBytes)
            throw new InvalidOperationException("账号配额不能超过邮件域配额。");
        db.Accounts.Add(account);
        foreach (var folder in mailboxSettings.DefaultFolders)
        {
            db.Mailboxes.Add(new Mailbox { Account = account, Name = folder });
        }

        var address = $"{account.LocalPart}@{domain.Name}";
        AddAudit(db, actor, "account.create", $"account:{account.Id}", address);
        await db.SaveChangesAsync(cancellationToken);
        return new AccountView(
            account.Id,
            address,
            account.IsEnabled,
            account.QuotaBytes,
            0,
            account.CreatedAt,
            0);
    }

    private static async Task<AliasView> CreateAliasCoreAsync(
        MailDbContext db,
        MailDomain domain,
        string localPart,
        Guid targetAccountId,
        MailAliasMode mode,
        string actor,
        CancellationToken cancellationToken)
    {
        if (await db.Aliases.AnyAsync(
                x => x.DomainId == domain.Id && x.SourceLocalPart == localPart,
                cancellationToken))
            throw new InvalidOperationException("别名地址已存在。");
        if (await db.Accounts.AnyAsync(
                x => x.DomainId == domain.Id && x.LocalPart == localPart,
                cancellationToken))
            throw new InvalidOperationException("该地址已被邮箱账号使用。");
        var target = await db.Accounts.Include(x => x.Domain)
            .SingleOrDefaultAsync(x => x.Id == targetAccountId, cancellationToken)
            ?? throw new KeyNotFoundException("目标账号不存在。");
        if (target.DomainId != domain.Id)
            throw new InvalidOperationException("别名与目标账号必须属于同一邮件域。");
        if (!target.IsEnabled && mode != MailAliasMode.Disabled)
            throw new InvalidOperationException("目标账号已停用，请选择启用的账号。");
        var targetAddress = $"{target.LocalPart}@{target.Domain.Name}";
        var alias = new MailAlias
        {
            DomainId = domain.Id,
            SourceLocalPart = localPart,
            TargetAccountId = target.Id,
            DestinationAddress = targetAddress,
            Mode = mode,
            IsEnabled = mode != MailAliasMode.Disabled
        };
        db.Aliases.Add(alias);
        AddAudit(db, actor, "alias.create", $"alias:{alias.Id}", $"{localPart}@{domain.Name}->{targetAddress}");
        await db.SaveChangesAsync(cancellationToken);
        return ToAliasView(alias, target);
    }

    private static AliasView ToAliasView(MailAlias alias, MailAccount? target)
    {
        var targetEnabled = target?.IsEnabled == true;
        var canReceive = alias.IsEnabled && alias.Mode != MailAliasMode.Disabled && targetEnabled;
        var canSend = canReceive && alias.Mode == MailAliasMode.SendReceive;
        return new AliasView(
            alias.Id,
            $"{alias.SourceLocalPart}@{alias.Domain.Name}",
            alias.TargetAccountId,
            target is null ? alias.DestinationAddress : $"{target.LocalPart}@{target.Domain.Name}",
            canReceive,
            alias.DestinationAddress,
            alias.Mode,
            canReceive,
            canSend);
    }

    private static async Task<HashSet<string>> GetDomainAuditResourcesAsync(
        MailDbContext db,
        Guid domainId,
        CancellationToken cancellationToken)
    {
        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"domain:{domainId}" };
        var accountResources = await db.Accounts.AsNoTracking()
            .Where(x => x.DomainId == domainId)
            .Select(x => "account:" + x.Id)
            .ToListAsync(cancellationToken);
        var aliasResources = await db.Aliases.AsNoTracking()
            .Where(x => x.DomainId == domainId)
            .Select(x => "alias:" + x.Id)
            .ToListAsync(cancellationToken);
        resources.UnionWith(accountResources);
        resources.UnionWith(aliasResources);
        var ruleResources = await db.MailRules.AsNoTracking()
            .Where(x => x.DomainId == domainId)
            .Select(x => "mail-rule:" + x.Id)
            .ToListAsync(cancellationToken);
        resources.UnionWith(ruleResources);
        return resources;
    }

    private static string NormalizeDomain(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new FormatException("Invalid domain.");
        var value = name.Trim().TrimEnd('.').ToLowerInvariant();
        var labels = value.Split('.');
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 253
            || labels.Length < 2
            || labels.Any(label => label.Length is < 1 or > 63
                || !IsAsciiLetterOrDigit(label[0])
                || !IsAsciiLetterOrDigit(label[^1])
                || label.Any(character => !IsAsciiLetterOrDigit(character) && character != '-')))
            throw new FormatException("Invalid domain.");
        return value;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static string? NormalizeOutboundIpAddresses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var addresses = value.Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item =>
            {
                if (!IPAddress.TryParse(item, out var address))
                    throw new FormatException($"Invalid outbound IP address: {item}");
                return address.ToString();
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return addresses.Length == 0 ? null : string.Join(Environment.NewLine, addresses);
    }

    private static IReadOnlyList<OutboundAddressOption> BuildOutboundAddressOptions(MailDomain domain)
    {
        var options = new Dictionary<string, OutboundAddressOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (!IsPublicAddress(address)) continue;
                var text = address.ToString();
                options.TryAdd(
                    text,
                    new OutboundAddressOption(
                        text,
                        networkInterface.Name,
                        address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4"));
            }
        }

        foreach (var configured in ParseOutboundIpAddresses(domain.OutboundIpAddresses))
        {
            options.TryAdd(
                configured,
                new OutboundAddressOption(
                    configured,
                    "已保存配置",
                    IPAddress.Parse(configured).AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4"));
        }

        return options.Values
            .OrderBy(x => x.AddressFamily)
            .ThenBy(x => x.Address)
            .ToArray();
    }

    private static IEnumerable<string> ParseOutboundIpAddresses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (var item in value.Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(item, out var address)) yield return address.ToString();
        }
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                >= 224 => false,
                _ => true
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return !address.IsIPv6LinkLocal
                && !address.IsIPv6Multicast
                && !address.IsIPv6SiteLocal
                && (bytes[0] & 0xfe) != 0xfc;
        }

        return false;
    }

    private static Guid[] NormalizeDomainIds(IReadOnlyCollection<Guid> domainIds)
    {
        var ids = domainIds.Where(x => x != Guid.Empty).Distinct().ToArray();
        if (ids.Length is < 1 or > 100) throw new InvalidOperationException("Select between 1 and 100 domains.");
        return ids;
    }

    private static string NormalizeLocalPartForDomain(string value, string domainName)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed.Contains('@'))
        {
            if (!TrySplitAddress(trimmed, out var localPart, out var suppliedDomain)
                || !string.Equals(suppliedDomain, domainName, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("邮箱地址格式无效。");
            return localPart;
        }

        if (!TrySplitAddress($"{trimmed}@{domainName}", out var normalizedLocalPart, out _))
            throw new FormatException("邮箱地址格式无效。");
        return normalizedLocalPart;
    }

    private static bool TrySplitAddress(string address, out string localPart, out string domain)
    {
        var normalized = address.Trim().ToLowerInvariant();
        var split = normalized.LastIndexOf('@');
        if (split <= 0 || split == normalized.Length - 1)
        {
            localPart = string.Empty;
            domain = string.Empty;
            return false;
        }
        localPart = normalized[..split];
        domain = NormalizeDomain(normalized[(split + 1)..]);
        return localPart.Length <= 128
               && !localPart.Any(character => char.IsWhiteSpace(character) || character is '<' or '>' or '@');
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new FormatException("密码至少需要 12 个字符。");
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private IReadOnlyList<ClientConnectionEndpoint> BuildConnections(MailDomain domain)
    {
        var hostName = domain.MailHostName ?? $"mail.{domain.Name}";
        var result = new List<ClientConnectionEndpoint>();
        foreach (var endpoint in ReadEndpoints("Smtp"))
        {
            result.Add(new ClientConnectionEndpoint(
                "SMTP",
                hostName,
                endpoint.Port,
                endpoint.Security,
                endpoint.RequireAuthentication,
                $"user@{domain.Name}",
                endpoint.Security == ConnectionSecurity.StartTls
                    ? "客户端先建立明文连接，再升级到 STARTTLS。"
                    : endpoint.Security == ConnectionSecurity.ImplicitTls
                        ? "客户端连接后立即进行 SSL/TLS 握手。"
                        : "未加密连接。"));
        }
        foreach (var endpoint in ReadEndpoints("Imap"))
        {
            result.Add(new ClientConnectionEndpoint(
                "IMAP",
                hostName,
                endpoint.Port,
                endpoint.Security,
                AuthenticationRequired: true,
                $"user@{domain.Name}",
                endpoint.Security == ConnectionSecurity.StartTls
                    ? "客户端先建立明文连接，再升级到 STARTTLS。"
                    : endpoint.Security == ConnectionSecurity.ImplicitTls
                        ? "客户端连接后立即进行 SSL/TLS 握手。"
                        : "未加密连接。"));
        }
        return result;
    }

    private IReadOnlyList<ConnectionEndpointConfiguration> ReadEndpoints(string sectionName)
    {
        var endpoints = configuration.GetSection($"{sectionName}:Endpoints")
            .Get<List<ConnectionEndpointConfiguration>>() ?? [];
        foreach (var endpoint in endpoints)
        {
            endpoint.ResolveSecurity(sectionName);
        }
        return endpoints;
    }

    private sealed class ConnectionEndpointConfiguration
    {
        public int Port { get; set; }
        public ConnectionSecurity Security { get; set; }
        public bool ImplicitTls { get; set; }
        public bool RequireAuthentication { get; set; }

        public void ResolveSecurity(string protocol)
        {
            if (ImplicitTls)
            {
                Security = ConnectionSecurity.ImplicitTls;
            }
            else if (Security == ConnectionSecurity.Plain
                     && (!string.Equals(protocol, "Smtp", StringComparison.OrdinalIgnoreCase) || Port != 25))
            {
                Security = ConnectionSecurity.StartTls;
            }
        }
    }
}
