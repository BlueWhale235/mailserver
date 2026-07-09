using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

public sealed class SentCopyBackgroundService(
    SentCopyService sentCopies,
    ILogger<SentCopyBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await sentCopies.ProcessDueAsync(25, stoppingToken);
                await Task.Delay(processed > 0 ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unable to process pending sent-mail copies.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
