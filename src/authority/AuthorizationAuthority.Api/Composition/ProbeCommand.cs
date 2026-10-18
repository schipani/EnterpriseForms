namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// Container health probe for shell-less (chiseled) images: <c>dotnet AuthorizationAuthority.Api.dll --probe http://127.0.0.1:8080/...</c>.
/// Loopback only; exit 0 on 2xx, 1 on failure, 2 on invalid argument. Used by Docker Compose healthchecks.
/// </summary>
public static class ProbeCommand
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        var index = Array.IndexOf(args, "--probe");
        if (index < 0)
        {
            return false;
        }

        if (index + 1 >= args.Length || !Uri.TryCreate(args[index + 1], UriKind.Absolute, out var target) || !target.IsLoopback)
        {
            exitCode = 2;
            return true;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = client.GetAsync(target).GetAwaiter().GetResult();
            exitCode = response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            exitCode = 1;
        }
        catch (TaskCanceledException)
        {
            exitCode = 1;
        }

        return true;
    }
}
