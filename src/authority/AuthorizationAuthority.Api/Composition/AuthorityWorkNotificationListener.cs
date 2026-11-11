using AuthorizationAuthority.Application.Common;
using Npgsql;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// C-150 R56-B: listens on <see cref="AuthorityWorkOptions.NotificationChannel"/> with one dedicated, unpooled AuthorityDb connection
/// and turns each notification into a work-loop wake-up (<see cref="AuthorityWorkSignal"/>). PostgreSQL delivers a notification only
/// for a committed transaction, to every listening session, so every Authority instance wakes for a change committed by any of them.
/// <para>
/// Loss is expected and harmless: notifications are not durable (a connection lost before delivery, a restart, a full queue). On every
/// (re)connection the loop is woken once, because anything committed meanwhile is still found by its durable scan; between failures the
/// loop's own 250 ms poll is the fallback. The payload is empty: the notification names no organization, record or principal.
/// </para>
/// </summary>
public sealed partial class AuthorityWorkNotificationListener(string connectionString, AuthorityWorkSignal signal, TimeProvider clock, ILogger<AuthorityWorkNotificationListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HostExecutionMode.IsOpenApiDocumentGeneration)
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 15, ApplicationName = "authority-work-listener" };
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                connection.Notification += (_, _) => signal.Signal(AuthorityWorkSignalSource.Notification);
                await connection.OpenAsync(stoppingToken).ConfigureAwait(false);
                await using (var listen = new NpgsqlCommand("LISTEN " + AuthorityWorkOptions.NotificationChannel, connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                }

                signal.ListenerConnected = true;
                signal.Signal(AuthorityWorkSignalSource.Reconnect); // anything committed while not listening is found by the loop's scan
                LogListening(logger);
                backoff = TimeSpan.FromSeconds(1);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or System.IO.IOException or TimeoutException)
            {
                LogListenerLost(logger, ex.GetType().Name, backoff.TotalSeconds);
            }
            finally
            {
                signal.ListenerConnected = false;
            }

            try
            {
                await Task.Delay(backoff, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    [LoggerMessage(EventId = 5310, Level = LogLevel.Information, Message = "R56-B work notification listener connected; committed authorization changes now wake the work loop.")]
    private static partial void LogListening(ILogger logger);

    [LoggerMessage(EventId = 5311, Level = LogLevel.Warning, Message = "R56-B work notification listener lost its connection ({ExceptionType}); reconnecting in {BackoffSeconds} s. The work loop's poll continues meanwhile.")]
    private static partial void LogListenerLost(ILogger logger, string exceptionType, double backoffSeconds);
}
