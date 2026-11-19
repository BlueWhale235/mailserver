using Microsoft.Extensions.Configuration;

namespace MailServer.Infrastructure;

public sealed record CacheBudgetAllocation(long ConfigurationBytes, long FetchMetadataBytes, long DiagnosticsBytes)
{
    public long TotalBytes => ConfigurationBytes + FetchMetadataBytes + DiagnosticsBytes;
}

/// <summary>Shares the process cache allowance between the three byte-budgeted consumers.</summary>
public sealed class ResourceCacheBudget
{
    private readonly string _role;
    private CacheBudgetAllocation _allocation = new(0, 0, 0);
    public CacheBudgetAllocation Allocation => Volatile.Read(ref _allocation);
    public event Action<CacheBudgetAllocation>? Changed;

    public ResourceCacheBudget(string role, IConfiguration configuration)
    {
        _role = role;
        var bytes = configuration.GetValue("ConfigurationCache:SizeLimitBytes", 4L * 1024 * 1024);
        if (role is "api" or "cli") bytes /= 4;
        Resize(bytes, configuration.GetValue("ConfigurationCache:Enabled", true));
    }

    public CacheBudgetAllocation Resize(long bytes, bool configurationEnabled)
    {
        bytes = Math.Max(0, bytes);
        var diagnostics = _role is "api" or "cli" ? bytes : bytes / 4;
        var fetch = _role is "imap" or "all" ? bytes / 4 : 0;
        var configuration = configurationEnabled && _role is "smtp" or "imap" or "worker" or "all"
            ? bytes - diagnostics - fetch : 0;
        var allocation = new CacheBudgetAllocation(configuration, fetch, diagnostics);
        if (Interlocked.Exchange(ref _allocation, allocation) != allocation) Changed?.Invoke(allocation);
        return allocation;
    }
}
