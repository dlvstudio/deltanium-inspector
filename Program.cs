using System.Text.Json;
using Deltanium.Inspector;

// Deltanium Inspector (TOKENOMICS_V2 §11 phase C): independent check of Central's ledger against the chain.
// Usage: deltanium-inspector --central https://api.example --blocker https://blocker.example [--watch 600] [--out report.json]
string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

var central = Arg("--central") ?? Environment.GetEnvironmentVariable("CENTRAL_API_URL");
var blocker = Arg("--blocker") ?? Environment.GetEnvironmentVariable("BLOCKER_URL");
if (string.IsNullOrWhiteSpace(central) || string.IsNullOrWhiteSpace(blocker))
{
    Console.Error.WriteLine("usage: deltanium-inspector --central <url> --blocker <url> [--watch <seconds>] [--out <file>]");
    return 2;
}
var watch = int.TryParse(Arg("--watch"), out var w) ? w : 0;
var outFile = Arg("--out");
var client = new InspectorClient(central, blocker);
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

while (true)
{
    int code;
    try
    {
        var anchors = await client.AnchorsAsync();
        var entries = new Dictionary<long, List<LedgerEntry>>();
        foreach (var a in anchors) entries[a.Seq] = await client.EntriesAsync(a.Seq);
        var report = Verifier.Verify(anchors, entries, await client.ChainAnchorsAsync(), await client.HardCapAsync());
        var text = JsonSerializer.Serialize(report, json);
        Console.WriteLine(text);
        if (outFile != null) await File.WriteAllTextAsync(outFile, text);
        code = report.Ok ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"inspector: cannot complete check: {ex.Message}");
        code = 2;
    }
    if (watch <= 0) return code;
    await Task.Delay(TimeSpan.FromSeconds(watch));
}
