using System.Net;
using MailServer.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MailServer.Infrastructure;

/// <summary>
/// 在审计记录写入数据库前统一补充请求来源，避免业务服务各自解析代理头或遗漏节点信息。
/// </summary>
public sealed class AuditStampInterceptor(
    IHttpContextAccessor httpContextAccessor) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is not MailDbContext db) return;
        var http = httpContextAccessor.HttpContext;
        var operationIp = Normalize(http?.Connection.RemoteIpAddress);
        foreach (var entry in db.ChangeTracker.Entries<AuditRecord>()
                     .Where(x => x.State == EntityState.Added))
        {
            entry.Entity.NodeId ??= Environment.MachineName;
            entry.Entity.RequestId ??= http?.TraceIdentifier;
            entry.Entity.RemoteAddress ??= operationIp;
        }
    }

    internal static string? Normalize(IPAddress? address)
    {
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }
}
