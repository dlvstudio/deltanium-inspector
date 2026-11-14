using System.Text.Json;

namespace Deltanium.Inspector;

/// <summary>Reads only public endpoints: Central anchors/entries/economics and a Blocker's explorer.</summary>
public class InspectorClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly string _central;
    private readonly string _blocker;

    public InspectorClient(string central, string blocker, HttpClient? http = null)
    {
        _central = central.TrimEnd('/');
        _blocker = blocker.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<List<Anchor>> AnchorsAsync() =>
        JsonSerializer.Deserialize<List<Anchor>>(await _http.GetStringAsync($"{_central}/api/wallet/anchors?limit=500"), Json) ?? new();

    public async Task<List<LedgerEntry>> EntriesAsync(long seq)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{_central}/api/wallet/anchors/{seq}/entries"));
        return doc.RootElement.GetProperty("entries").Deserialize<List<LedgerEntry>>(Json) ?? new();
    }

    public async Task<decimal?> HardCapAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{_central}/api/wallet/economics"));
            return doc.RootElement.TryGetProperty("hardCap", out var h) && h.TryGetDecimal(out var v) ? v : null;
        }
        catch { return null; }
    }

    public async Task<List<ChainAnchor>> ChainAnchorsAsync()
    {
        var result = new List<ChainAnchor>();
        for (var offset = 0; ; offset += 100)
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{_blocker}/api/explorer/blocks?limit=100&offset={offset}"));
            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() == 0) break;
            foreach (var b in items.EnumerateArray())
            {
                var seq = b.TryGetProperty("ledgerAnchorSeq", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                if (seq <= 0) continue;
                result.Add(new ChainAnchor
                {
                    Height = b.GetProperty("height").GetInt64(),
                    BlockId = b.GetProperty("id").GetString() ?? "",
                    LedgerAnchorSeq = seq,
                    LedgerRoot = b.GetProperty("ledgerRoot").GetString() ?? "",
                    BalancesRoot = b.GetProperty("balancesRoot").GetString() ?? ""
                });
            }
            if (items.GetArrayLength() < 100) break;
        }
        return result;
    }
}
