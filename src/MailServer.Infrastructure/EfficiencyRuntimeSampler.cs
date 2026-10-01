using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

/// <summary>Opt-in acceptance diagnostics. No message data or credentials are sampled.</summary>
public sealed class EfficiencyRuntimeSampler(ILogger<EfficiencyRuntimeSampler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        using var process = Process.GetCurrentProcess();
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                process.Refresh();
                var info = GC.GetGCMemoryInfo();
                logger.LogInformation(
                    "Efficiency runtime sample: workingSetBytes={WorkingSetBytes}, peakWorkingSetBytes={PeakWorkingSetBytes}, heapBytes={HeapBytes}, allocatedBytes={AllocatedBytes}, gen0={Gen0}, gen1={Gen1}, gen2={Gen2}.",
                    process.WorkingSet64, process.PeakWorkingSet64, info.HeapSizeBytes,
                    GC.GetTotalAllocatedBytes(false), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
