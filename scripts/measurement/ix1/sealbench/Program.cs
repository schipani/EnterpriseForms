using System.Diagnostics;
using System.Security.Cryptography;
using EnterpriseForms.Integrity.Merkle;
using EnterpriseForms.Integrity.Search;

// IX-1 measurement: local ES256 verification throughput and seal-message encoding cost (Release, this workstation).
var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var pub = SearchSealSignatureV1.ImportPinnedPublicKey(key.ExportSubjectPublicKeyInfo());
var org = Guid.CreateVersion7(); var form = Guid.CreateVersion7(); var record = Guid.CreateVersion7(); var op = Guid.CreateVersion7();
var coverages = new List<SearchSealCoverage>(); var postings = new List<SearchSealPosting>();
for (var i = 0; i < 20; i++)
{
    var field = Guid.CreateVersion7(); var approval = Guid.CreateVersion7(); var item = Guid.CreateVersion7();
    coverages.Add(new(field, approval, "String", "Internal", "Standard", ["equals", "startsWith"]));
    postings.Add(new(field, approval, MerkleManifestV1.ItemKey(MerkleItemKind.FieldValue, item), item, "field_string_values", "String", SHA256.HashData(item.ToByteArray()), new SearchSealValue.NormalizedString("VALUE NUMBER " + i)));
}
var input = new SearchEntrySealInput("search-entry-seal-v1", 1, org, form, record, op, 3, SHA256.HashData(op.ToByteArray()), "Submitted", coverages, postings, []);
var message = SearchEntrySealV1.Encode(input);
var signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
for (var i = 0; i < 2000; i++) { SearchSealSignatureV1.Verify(pub, message, signature); SearchEntrySealV1.Encode(input); }

const int N = 20000;
var sw = Stopwatch.StartNew();
for (var i = 0; i < N; i++) SearchEntrySealV1.Encode(input);
var encodeUs = sw.Elapsed.TotalMicroseconds / N;
sw.Restart();
for (var i = 0; i < N; i++) if (!SearchSealSignatureV1.Verify(pub, message, signature)) throw new Exception();
var verifySingle = N / sw.Elapsed.TotalSeconds;
var cores = Environment.ProcessorCount;
var keys = Enumerable.Range(0, cores).Select(_ => SearchSealSignatureV1.ImportPinnedPublicKey(key.ExportSubjectPublicKeyInfo())).ToArray();
sw.Restart();
Parallel.For(0, cores, c => { for (var i = 0; i < N; i++) SearchSealSignatureV1.Verify(keys[c], message, signature); });
var verifyParallel = cores * (double)N / sw.Elapsed.TotalSeconds;
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
{
    environment = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}, {cores} logical cores, Release",
    message_bytes = message.Length, postings = postings.Count,
    encode_microseconds = Math.Round(encodeUs, 2),
    verify_per_second_single_thread = Math.Round(verifySingle),
    verify_per_second_all_cores = Math.Round(verifyParallel),
    verify_microseconds_single_thread = Math.Round(1e6 / verifySingle, 1),
}));
