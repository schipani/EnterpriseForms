using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Domain.Features.Documents;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.Infrastructure.Features.Documents;

public sealed class MalwareScannerOptions
{
    public const string SectionName = "MalwareScanner";

    /// <summary>"ClamAv" or "Unconfigured" (fail closed: documents stay ScanPending).</summary>
    public string Provider { get; set; } = "Unconfigured";

    public string Host { get; set; } = "clamav";

    public int Port { get; set; } = 3310;

    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// ClamAV clamd INSTREAM client over TCP (real scanner verdicts only). Protocol: "zINSTREAM\0", then length-prefixed
/// chunks (4-byte big-endian), a zero-length terminator, and a NUL-terminated reply such as "stream: OK" or
/// "stream: Eicar-Test-Signature FOUND". Connection failures surface as <see cref="ScannerUnavailableException"/>.
/// </summary>
public sealed class ClamAvTcpScanner(IOptions<MalwareScannerOptions> options) : IFileMalwareScanner
{
    private const int ChunkSize = 1024 * 1024;

    public string ScannerName => "ClamAV clamd (" + options.Value.Host + ")";

    public async Task<ScanResult> ScanAsync(Stream content, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var settings = options.Value;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds)));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(settings.Host, settings.Port, cts.Token).ConfigureAwait(false);
            await using var network = client.GetStream();
            await network.WriteAsync("zINSTREAM\0"u8.ToArray(), cts.Token).ConfigureAwait(false);
            var buffer = new byte[ChunkSize];
            var prefix = new byte[4];
            int read;
            while ((read = await content.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                BinaryPrimitives.WriteInt32BigEndian(prefix, read);
                await network.WriteAsync(prefix, cts.Token).ConfigureAwait(false);
                await network.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
            }

            BinaryPrimitives.WriteInt32BigEndian(prefix, 0);
            await network.WriteAsync(prefix, cts.Token).ConfigureAwait(false);
            await network.FlushAsync(cts.Token).ConfigureAwait(false);

            var response = new MemoryStream();
            var reply = new byte[512];
            while ((read = await network.ReadAsync(reply, cts.Token).ConfigureAwait(false)) > 0)
            {
                response.Write(reply, 0, read);
                if (reply.AsSpan(0, read).Contains((byte)0))
                {
                    break;
                }
            }

            var text = Encoding.ASCII.GetString(response.ToArray()).TrimEnd('\0', '\n', '\r', ' ');
            return Parse(text);
        }
        catch (SocketException ex)
        {
            throw new ScannerUnavailableException("clamd unreachable: " + ex.SocketErrorCode, ex);
        }
        catch (IOException ex)
        {
            throw new ScannerUnavailableException("clamd connection failed: " + ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ScannerUnavailableException("clamd timeout", ex);
        }
    }

    public async Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            await client.ConnectAsync(settings.Host, settings.Port, cts.Token).ConfigureAwait(false);
            await using var network = client.GetStream();
            await network.WriteAsync("zPING\0"u8.ToArray(), cts.Token).ConfigureAwait(false);
            var reply = new byte[16];
            var read = await network.ReadAsync(reply, cts.Token).ConfigureAwait(false);
            var text = Encoding.ASCII.GetString(reply, 0, read).TrimEnd('\0', '\n');
            return text == "PONG" ? (true, "clamd PONG") : (false, "clamd replied " + text);
        }
        catch (SocketException ex)
        {
            return (false, "ScannerUnavailable:" + ex.SocketErrorCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "ScannerTimeout");
        }
    }

    internal ScanResult Parse(string reply)
    {
        if (reply.EndsWith(": OK", StringComparison.Ordinal))
        {
            return new ScanResult(ScanVerdicts.Clean, ScannerName, null, null);
        }

        if (reply.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var colon = reply.IndexOf(':', StringComparison.Ordinal);
            var signature = reply[(colon + 1)..^6].Trim();
            return new ScanResult(ScanVerdicts.Infected, ScannerName, signature.Length > 200 ? signature[..200] : signature, null);
        }

        if (reply.EndsWith(" ERROR", StringComparison.Ordinal))
        {
            return new ScanResult(ScanVerdicts.ScanError, ScannerName, null, reply.Length > 200 ? reply[..200] : reply);
        }

        throw new ScannerUnavailableException("unrecognized clamd reply: " + (reply.Length > 80 ? reply[..80] : reply));
    }
}

/// <summary>Fail-closed scanner used when none is configured: never returns a verdict.</summary>
public sealed class UnavailableMalwareScanner(string reason) : IFileMalwareScanner
{
    public string ScannerName => "Unavailable";

    public Task<ScanResult> ScanAsync(Stream content, long length, CancellationToken cancellationToken) => throw new ScannerUnavailableException(reason);

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((false, reason));
}
