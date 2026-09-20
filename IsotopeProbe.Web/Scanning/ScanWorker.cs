using IsotopeProbe.Queue;

namespace IsotopeProbe.Web.Scanning;

public sealed class ScanWorker(IServiceScopeFactory scopes, WebScanOptions options, ILogger<ScanWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, options.ConcurrentScans).Select(_ => RunSlotAsync(stoppingToken)));

    // Slots are tracked and awaited by the host; limits apply only within this instance.
    private async Task RunSlotAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<WebScanDispatcher>().RunNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Avoid logging connection strings or scanner payloads in exception messages.
                logger.LogError("Web scan dispatch failed ({ExceptionType}). A claimed execution may remain Running; inspect before recovery.", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
