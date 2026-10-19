using System.Globalization;
using System.Text;
using EnterpriseForms.SecurityTests.Harness;

namespace EnterpriseForms.StageBTests.Harness;

/// <summary>
/// Reads the Documents blob emulator the way an attacker with the storage account key (or a storage breach) would: a
/// SharedKey-signed request executed inside the Azurite container, bypassing the API entirely. Used to prove that what is
/// at rest is ciphertext and that staged plaintext is gone. Test-only; never part of the product.
/// </summary>
public sealed class AzuriteProbe(ComposeStack stack)
{
    private const string Script = """
        const http = require('http'), crypto = require('crypto');
        const [action, container, name, account, key] = process.argv.slice(2);
        const query = action === 'blocks' ? { blocklisttype: 'all', comp: 'blocklist' } : {};
        const date = new Date().toUTCString(), version = '2021-08-06';
        const names = Object.keys(query).sort();
        const resource = '/' + account + '/' + account + '/' + container + '/' + name + names.map(k => '\n' + k + ':' + query[k]).join('');
        const toSign = ['GET', '', '', '', '', '', '', '', '', '', '', '', 'x-ms-date:' + date + '\nx-ms-version:' + version + '\n' + resource].join('\n');
        const signature = crypto.createHmac('sha256', Buffer.from(key, 'base64')).update(toSign, 'utf8').digest('base64');
        const path = '/' + account + '/' + container + '/' + name.split('/').map(encodeURIComponent).join('/') + (names.length ? '?' + names.map(k => k + '=' + query[k]).join('&') : '');
        http.get({ host: '127.0.0.1', port: 10000, path, headers: { 'x-ms-date': date, 'x-ms-version': version, Authorization: 'SharedKey ' + account + ':' + signature } }, res => {
          const chunks = [];
          res.on('data', c => chunks.push(c));
          res.on('end', () => process.stdout.write(res.statusCode + '\n' + Buffer.concat(chunks).toString('base64') + '\n'));
        }).on('error', e => { process.stdout.write('0\n' + Buffer.from(String(e)).toString('base64') + '\n'); });
        """;

    private readonly Lazy<(string Account, string Key)> credentials = new(ReadCredentials);

    /// <summary>GET Blob: status code and raw bytes (404 when the committed blob does not exist).</summary>
    public (int Status, byte[] Body) GetBlob(string container, string blobName) => Run("get", container, blobName);

    /// <summary>Get Block List (committed + uncommitted): 404 when neither a committed blob nor uncommitted blocks exist.</summary>
    public (int Status, string Body) GetBlockList(string container, string blobName)
    {
        var (status, body) = Run("blocks", container, blobName);
        return (status, Encoding.UTF8.GetString(body));
    }

    private (int Status, byte[] Body) Run(string action, string container, string blobName)
    {
        var (account, key) = credentials.Value;
        var encodedScript = Convert.ToBase64String(Encoding.UTF8.GetBytes(Script));
        var output = stack.Compose($"exec -T azurite sh -c \"echo {encodedScript} | base64 -d > /tmp/probe.js && node /tmp/probe.js {action} {container} {blobName} {account} {key}\"", 60);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var statusLine = Array.FindLastIndex(lines, l => l.Length is > 0 and <= 3 && l.All(char.IsDigit));
        if (statusLine < 0)
        {
            throw new InvalidOperationException("azurite probe produced no status: " + output);
        }

        var body = statusLine + 1 < lines.Length ? Convert.FromBase64String(lines[statusLine + 1]) : [];
        return (int.Parse(lines[statusLine], CultureInfo.InvariantCulture), body);
    }

    private static (string Account, string Key) ReadCredentials()
    {
        var envFile = Path.Combine(ComposeStack.DockerDirectory, File.Exists(Path.Combine(ComposeStack.DockerDirectory, ".env")) ? ".env" : "env.example");
        var values = File.ReadAllLines(envFile)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('=', StringComparison.Ordinal))
            .Select(l => (Name: l[..l.IndexOf('=', StringComparison.Ordinal)], Value: l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..].Trim('"')))
            .GroupBy(v => v.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        return (values["DOCUMENTS_ACCOUNT_NAME"], values["DOCUMENTS_ACCOUNT_KEY"]);
    }
}
