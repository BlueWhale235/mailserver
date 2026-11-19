using MailServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

public sealed record DirectoryAccount(long Id, string Address, bool? IsEnabled, long? QuotaBytes,
    long? UsedBytes, int? AliasCount, string? MatchedAlias, int MatchedAliasCount);
public sealed record DirectoryAlias(long Id, string Source, long? TargetAccountId, string? TargetAddress,
    MailAliasMode Mode, bool CanReceive, bool CanSend, string? UnavailableReason);

public sealed class AccountDirectoryService(IDbContextFactory<MailDbContext> factory)
{
    public async Task<string> DomainNameAsync(long domainId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Domains.Where(x => x.Id == domainId).Select(x => x.Name).SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("邮件域不存在。");
    }

    public async Task<PagedResult<DirectoryAccount>> SearchAsync(long domainId, string? query,
        bool? enabled, int page, int pageSize, bool readAccounts, bool readAliases, long? accountId, CancellationToken ct)
    {
        var name = await DomainNameAsync(domainId, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var text = query?.Trim().ToLowerInvariant() ?? "";
        var accounts = db.Accounts.AsNoTracking().Where(x => x.DomainId == domainId);
        if (accountId is not null) accounts = accounts.Where(x => x.Id == accountId);
        if (enabled is not null && readAccounts) accounts = accounts.Where(x => x.IsEnabled == enabled);
        if (text.Length > 0) accounts = accounts.Where(x => (x.LocalPart + "@" + name).Contains(text)
            || (readAliases && db.Aliases.Any(a => a.DomainId == domainId && a.TargetAccountId == x.Id
                && (a.SourceLocalPart + "@" + name).Contains(text))));
        var total = await accounts.CountAsync(ct);
        var rows = await accounts.OrderBy(x => x.LocalPart).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new DirectoryAccount(x.Id, x.LocalPart + "@" + name,
                readAccounts ? x.IsEnabled : null, readAccounts ? x.QuotaBytes : null, readAccounts ? x.UsedBytes : null,
                readAliases ? db.Aliases.Count(a => a.DomainId == domainId && a.TargetAccountId == x.Id) : null,
                readAliases && text != "" ? db.Aliases.Where(a => a.DomainId == domainId && a.TargetAccountId == x.Id
                    && (a.SourceLocalPart + "@" + name).Contains(text)).OrderBy(a => a.SourceLocalPart)
                    .Select(a => a.SourceLocalPart + "@" + name).FirstOrDefault() : null,
                readAliases && text != "" ? db.Aliases.Count(a => a.DomainId == domainId && a.TargetAccountId == x.Id
                    && (a.SourceLocalPart + "@" + name).Contains(text)) : 0)).ToListAsync(ct);
        return new PagedResult<DirectoryAccount>(rows, page, pageSize, total);
    }

    public async Task<PagedResult<DirectoryAlias>> AliasesAsync(long domainId, long? accountId,
        bool pending, string? query, int page, int pageSize, CancellationToken ct)
    {
        var name = await DomainNameAsync(domainId, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (accountId is not null && !await db.Accounts.AnyAsync(x => x.Id == accountId && x.DomainId == domainId, ct))
            throw new KeyNotFoundException("邮箱账号不存在。");
        var domainActive = await db.Domains.Where(x => x.Id == domainId).Select(x => x.IsActive).SingleAsync(ct);
        var aliases = db.Aliases.AsNoTracking().Where(x => x.DomainId == domainId);
        if (accountId is not null) aliases = aliases.Where(x => x.TargetAccountId == accountId);
        if (pending) aliases = aliases.Where(x => !db.Accounts.Any(a => a.Id == x.TargetAccountId && a.DomainId == domainId));
        var text = query?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(text)) aliases = aliases.Where(x => (x.SourceLocalPart + "@" + name).Contains(text));
        var total = await aliases.CountAsync(ct);
        var rows = await aliases.OrderBy(x => x.SourceLocalPart).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new {
                x.Id, x.SourceLocalPart, x.TargetAccountId, x.Mode, x.IsEnabled,
                TargetAddress = db.Accounts.Where(a => a.Id == x.TargetAccountId && a.DomainId == domainId)
                    .Select(a => a.LocalPart + "@" + name).FirstOrDefault(),
                TargetEnabled = db.Accounts.Any(a => a.Id == x.TargetAccountId && a.DomainId == domainId && a.IsEnabled)
            }).ToListAsync(ct);
        return new PagedResult<DirectoryAlias>(rows.Select(x => {
            var reason = !domainActive ? "所属邮件域已停用" : x.TargetAddress is null ? "目标账号不存在或已删除" : !x.TargetEnabled ? "所属账号已停用"
                : !x.IsEnabled || x.Mode == MailAliasMode.Disabled ? "别名已停用" : null;
            return new DirectoryAlias(x.Id, x.SourceLocalPart + "@" + name, x.TargetAccountId, x.TargetAddress,
                x.Mode, reason is null, reason is null && x.Mode == MailAliasMode.SendReceive, reason);
        }).ToArray(), page, pageSize, total);
    }
}
