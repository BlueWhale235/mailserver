using System.Security.Cryptography;
using MailServer.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

public sealed record AdminDashboard(
    int Domains,
    int Accounts,
    int Aliases,
    int PendingDeliveries,
    int FailedDeliveries,
    long StoredBytes);

public sealed record AccountView(
    Guid Id,
    string Address,
    bool IsEnabled,
    long QuotaBytes,
    DateTimeOffset CreatedAt);

public sealed record AliasView(Guid Id, string Source, string Destination);
public sealed record ApiKeyView(Guid Id, string Name, string Prefix, string Scopes, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt);
public sealed record CreatedApiKey(Guid Id, string Value, DateTimeOffset? ExpiresAt);

public sealed class AdminApplicationService(
    IDbContextFactory<MailDbContext> dbFactory,
    ICredentialService credentials,
    IDeliveryQueue deliveryQueue,
    DnsDiagnosticService dnsDiagnostics)
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
            admin.PasswordHash = upgraded;
            await db.SaveChangesAsync(cancellationToken);
        }

        return admin;
    }

    public async Task<AdminDashboard> GetDashboardAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return new AdminDashboard(
            await db.Domains.CountAsync(cancellationToken),
            await db.Accounts.CountAsync(cancellationToken),
            await db.Aliases.CountAsync(cancellationToken),
            await db.DeliveryJobs.CountAsync(
                x => x.Status == DeliveryStatus.Pending || x.Status == DeliveryStatus.Deferred,
                cancellationToken),
            await db.DeliveryJobs.CountAsync(x => x.Status == DeliveryStatus.Failed, cancellationToken),
            await db.Messages.SumAsync(x => (long?)x.Size, cancellationToken) ?? 0);
    }

    public async Task<IReadOnlyList<MailDomain>> GetDomainsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Domains.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<MailDomain> CreateDomainAsync(
        string name,
        OutboundMode outboundMode,
        string? dkimSelector,
        string? dkimPrivateKeyPath,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeDomain(name);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Domains.AnyAsync(x => x.Name == normalized, cancellationToken))
            throw new InvalidOperationException("Domain already exists.");
        var domain = new MailDomain
        {
            Name = normalized,
            OutboundMode = outboundMode,
            DkimSelector = NullIfWhiteSpace(dkimSelector),
            DkimPrivateKeyPath = NullIfWhiteSpace(dkimPrivateKeyPath)
        };
        db.Domains.Add(domain);
        AddAudit(db, actor, "domain.create", $"domain:{domain.Id}", normalized);
        await db.SaveChangesAsync(cancellationToken);
        return domain;
    }

    public async Task<MailDomain> UpdateDomainAsync(
        Guid id,
        bool? isActive,
        OutboundMode? outboundMode,
        string? dkimSelector,
        string? dkimPrivateKeyPath,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        if (isActive is not null) domain.IsActive = isActive.Value;
        if (outboundMode is not null) domain.OutboundMode = outboundMode.Value;
        if (dkimSelector is not null) domain.DkimSelector = NullIfWhiteSpace(dkimSelector);
        if (dkimPrivateKeyPath is not null) domain.DkimPrivateKeyPath = NullIfWhiteSpace(dkimPrivateKeyPath);
        AddAudit(db, actor, "domain.update", $"domain:{id}", null);
        await db.SaveChangesAsync(cancellationToken);
        return domain;
    }

    public async Task<IReadOnlyList<AccountView>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Accounts.AsNoTracking()
            .OrderBy(x => x.Domain.Name)
            .ThenBy(x => x.LocalPart)
            .Select(x => new AccountView(
                x.Id,
                x.LocalPart + "@" + x.Domain.Name,
                x.IsEnabled,
                x.QuotaBytes,
                x.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<AccountView> CreateAccountAsync(
        string address,
        string password,
        long? quotaBytes,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!TrySplitAddress(address, out var localPart, out var domainName))
            throw new FormatException("Invalid email address.");
        ValidatePassword(password);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Name == domainName, cancellationToken)
            ?? throw new InvalidOperationException("Domain does not exist.");
        if (await db.Accounts.AnyAsync(
                x => x.DomainId == domain.Id && x.LocalPart == localPart,
                cancellationToken))
            throw new InvalidOperationException("Account already exists.");
        var account = new MailAccount
        {
            DomainId = domain.Id,
            LocalPart = localPart,
            PasswordHash = credentials.HashPassword(password),
            QuotaBytes = quotaBytes is > 0 ? quotaBytes.Value : 1_073_741_824
        };
        db.Accounts.Add(account);
        db.Mailboxes.Add(new Mailbox { Account = account, Name = "INBOX" });
        AddAudit(db, actor, "account.create", $"account:{account.Id}", address);
        await db.SaveChangesAsync(cancellationToken);
        return new AccountView(
            account.Id,
            $"{account.LocalPart}@{domain.Name}",
            account.IsEnabled,
            account.QuotaBytes,
            account.CreatedAt);
    }

    public async Task UpdateAccountAsync(
        Guid id,
        bool? enabled,
        long? quotaBytes,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Account not found.");
        if (enabled is not null) account.IsEnabled = enabled.Value;
        if (quotaBytes is not null)
        {
            if (quotaBytes <= 0) throw new ArgumentOutOfRangeException(nameof(quotaBytes));
            account.QuotaBytes = quotaBytes.Value;
        }
        AddAudit(db, actor, "account.update", $"account:{id}", null);
        await db.SaveChangesAsync(cancellationToken);
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
        return await db.Aliases.AsNoTracking()
            .OrderBy(x => x.Domain.Name)
            .ThenBy(x => x.SourceLocalPart)
            .Select(x => new AliasView(
                x.Id,
                x.SourceLocalPart + "@" + x.Domain.Name,
                x.DestinationAddress))
            .ToListAsync(cancellationToken);
    }

    public async Task<AliasView> CreateAliasAsync(
        string source,
        string destination,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!TrySplitAddress(source, out var localPart, out var domainName)
            || !TrySplitAddress(destination, out _, out _))
            throw new FormatException("Invalid alias address.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Name == domainName, cancellationToken)
            ?? throw new InvalidOperationException("Domain does not exist.");
        if (await db.Aliases.AnyAsync(
                x => x.DomainId == domain.Id && x.SourceLocalPart == localPart,
                cancellationToken))
            throw new InvalidOperationException("Alias already exists.");
        var alias = new MailAlias
        {
            DomainId = domain.Id,
            SourceLocalPart = localPart,
            DestinationAddress = destination.Trim().ToLowerInvariant()
        };
        db.Aliases.Add(alias);
        AddAudit(db, actor, "alias.create", $"alias:{alias.Id}", $"{source}->{destination}");
        await db.SaveChangesAsync(cancellationToken);
        return new AliasView(alias.Id, $"{localPart}@{domainName}", alias.DestinationAddress);
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

    public async Task<IReadOnlyList<ApiKeyView>> GetApiKeysAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ApiKeys.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new ApiKeyView(
                x.Id,
                x.Name,
                x.Prefix,
                x.Scopes,
                x.ExpiresAt,
                x.RevokedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<CreatedApiKey> CreateApiKeyAsync(
        string name,
        IReadOnlyCollection<string>? scopes,
        DateTimeOffset? expiresAt,
        string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new FormatException("API key name is required.");
        var prefix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var record = new ApiKeyRecord
        {
            Name = name.Trim(),
            Prefix = prefix,
            SecretHash = credentials.HashApiSecret(secret),
            Scopes = string.Join(
                ' ',
                (scopes ?? []).DefaultIfEmpty("admin").Distinct(StringComparer.OrdinalIgnoreCase)),
            ExpiresAt = expiresAt
        };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.ApiKeys.Add(record);
        AddAudit(db, actor, "api-key.create", $"api-key:{record.Id}", record.Name);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedApiKey(record.Id, $"msk_{prefix}_{secret}", expiresAt);
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

    public async Task<IReadOnlyList<DeliveryJob>> GetDeliveryJobsAsync(
        DeliveryStatus? status,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.DeliveryJobs.AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        return await query.OrderByDescending(x => x.UpdatedAt).Take(200).ToListAsync(cancellationToken);
    }

    public async Task RetryDeliveryAsync(
        Guid id,
        string actor,
        CancellationToken cancellationToken)
    {
        await deliveryQueue.RetryNowAsync(id, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        AddAudit(db, actor, "queue.retry", $"delivery:{id}", null);
        await db.SaveChangesAsync(cancellationToken);
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
        ValidatePassword(password);
        var normalized = userName.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) throw new FormatException("Administrator name is required.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.AdminUsers.AnyAsync(x => x.UserName == normalized, cancellationToken))
            throw new InvalidOperationException("Administrator already exists.");
        var admin = new AdminUser
        {
            UserName = normalized,
            PasswordHash = credentials.HashPassword(password)
        };
        db.AdminUsers.Add(admin);
        await db.SaveChangesAsync(cancellationToken);
        return admin.Id;
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
        string? detail) =>
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
            Action = action,
            Resource = resource,
            Detail = detail
        });

    private static string NormalizeDomain(string name)
    {
        var value = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.Contains(' '))
            throw new FormatException("Invalid domain.");
        return value;
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
            throw new FormatException("Password must be at least 12 characters.");
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
