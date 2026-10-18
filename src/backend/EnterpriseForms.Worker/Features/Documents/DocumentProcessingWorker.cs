using EnterpriseForms.Application.Features.Documents.Processing;

namespace EnterpriseForms.Worker.Features.Documents;

/// <summary>Durable document processing loop (scan → encrypt → store) hosted by the Worker image; one leased job at a time.</summary>
public sealed partial class DocumentProcessingWorker(DocumentProcessingService processing, ILogger<DocumentProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = "worker:" + Environment.MachineName + ":" + Environment.ProcessId;
        LogStarted(logger, worker);
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                didWork = await processing.ProcessOnceAsync(worker, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or Microsoft.EntityFrameworkCore.DbUpdateException or IOException or InvalidOperationException)
            {
                LogLoopError(logger, ex.GetType().Name, ex.Message);
            }

            await Task.Delay(didWork ? 50 : 1000, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Document processing worker started as {Worker}.")]
    private static partial void LogStarted(ILogger logger, string worker);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document processing worker transient failure {ExceptionType}: {Detail}")]
    private static partial void LogLoopError(ILogger logger, string exceptionType, string detail);
}
