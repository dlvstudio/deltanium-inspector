using System.Text.Json.Serialization;

namespace Deltanium.Inspector;

public class Anchor
{
    public long Seq { get; set; }
    public DateTime FromExclusive { get; set; }
    public DateTime UpToInclusive { get; set; }
    public int EntryCount { get; set; }
    public string LedgerRoot { get; set; } = "";
    public string BalancesRoot { get; set; } = "";
}

public class LedgerEntry
{
    public string Id { get; set; } = "";
    public string? FromPublicKey { get; set; }
    public string? ToPublicKey { get; set; }
    public decimal Amount { get; set; }
    public string Kind { get; set; } = "";
    public string? RefId { get; set; }
    /// <summary>Kept as text so the exact instant (to the microsecond) survives parsing.</summary>
    [JsonPropertyName("createdAt")]
    public string CreatedAtRaw { get; set; } = "";

    [JsonIgnore]
    public DateTime CreatedAt => Verifier.ParseUtc(CreatedAtRaw);
}

/// <summary>Anchor fields as committed in a block of the Blocker chain.</summary>
public class ChainAnchor
{
    public long Height { get; set; }
    public string BlockId { get; set; } = "";
    public long LedgerAnchorSeq { get; set; }
    public string LedgerRoot { get; set; } = "";
    public string BalancesRoot { get; set; } = "";
}

public class Finding
{
    public string Severity { get; set; } = "error"; // error | warn | info
    public long? Seq { get; set; }
    public string Check { get; set; } = "";
    public string Detail { get; set; } = "";
}

public class Report
{
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public int AnchorsChecked { get; set; }
    public int AnchorsInChain { get; set; }
    public int EntriesReplayed { get; set; }
    public long? LatestSeq { get; set; }
    public decimal TotalSupply { get; set; }
    public decimal Minted { get; set; }
    public decimal? HardCap { get; set; }
    public List<Finding> Findings { get; set; } = new();
    public bool Ok => Findings.All(f => f.Severity != "error");
}
