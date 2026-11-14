using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Deltanium.Inspector;

/// <summary>
/// TOKENOMICS_V2 §11 phase C. Recomputes every ledger anchor from the rows Central publishes and checks it
/// against what Blockers committed on chain. Needs no trust in Central: a mismatch means the published
/// ledger was rewritten, rows were hidden, balances were edited outside the ledger, or D was minted.
/// </summary>
public static class Verifier
{
    public static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static DateTime ParseUtc(string raw) =>
        DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).UtcDateTime;

    private static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime();

    public static string F10(decimal d) => d.ToString("F10", CultureInfo.InvariantCulture);

    /// <summary>Must match Central's EconomicsService.LedgerLeaf.</summary>
    public static string LedgerLeaf(LedgerEntry e) => Sha256Hex(string.Join("|",
        e.Id, e.FromPublicKey ?? "", e.ToPublicKey ?? "", F10(e.Amount), e.Kind, e.RefId ?? "",
        e.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture)));

    public static string BalanceLeaf(string account, decimal balance) => Sha256Hex($"{account}|{F10(balance)}");

    public static string MerkleRoot(IEnumerable<string> leaves)
    {
        var level = leaves.ToList();
        if (level.Count == 0) return Sha256Hex("");
        while (level.Count > 1)
        {
            var next = new List<string>();
            for (var i = 0; i < level.Count; i += 2)
                next.Add(i + 1 < level.Count ? Sha256Hex(level[i] + level[i + 1]) : level[i]);
            level = next;
        }
        return level[0];
    }

    public static string BalancesRoot(Dictionary<string, decimal> balances) =>
        MerkleRoot(balances.Where(kv => kv.Value != 0m).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => BalanceLeaf(kv.Key, kv.Value)));

    /// <param name="anchors">All anchors Central publishes.</param>
    /// <param name="entries">Published ledger rows per anchor seq.</param>
    /// <param name="chain">Anchor fields found in Blocker blocks.</param>
    /// <param name="hardCap">Max supply in D (from /api/wallet/economics), or null to skip the mint check.</param>
    public static Report Verify(IReadOnlyList<Anchor> anchors, IReadOnlyDictionary<long, List<LedgerEntry>> entries,
        IReadOnlyList<ChainAnchor> chain, decimal? hardCap)
    {
        var report = new Report { HardCap = hardCap };
        void Add(string sev, long? seq, string check, string detail) =>
            report.Findings.Add(new Finding { Severity = sev, Seq = seq, Check = check, Detail = detail });

        var ordered = anchors.OrderBy(a => a.Seq).ToList();
        var bySeq = ordered.GroupBy(a => a.Seq).ToDictionary(g => g.Key, g => g.First());
        foreach (var dup in ordered.GroupBy(a => a.Seq).Where(g => g.Count() > 1))
            Add("error", dup.Key, "unique-seq", "Central publishes the same anchor seq more than once");

        // Chain ↔ Central: every committed anchor must be published with the same roots.
        var chainBySeq = chain.Where(c => c.LedgerAnchorSeq > 0).GroupBy(c => c.LedgerAnchorSeq).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (seq, blocks) in chainBySeq)
        {
            if (blocks.Select(b => (b.LedgerRoot, b.BalancesRoot)).Distinct().Count() > 1)
                Add("error", seq, "chain-consistency", "blocks commit different roots for the same anchor seq");
            if (!bySeq.TryGetValue(seq, out var a))
            {
                Add("error", seq, "published", $"anchor committed in block {blocks[0].Height} is not published by Central");
                continue;
            }
            var b = blocks[0];
            if (!Eq(a.LedgerRoot, b.LedgerRoot) || !Eq(a.BalancesRoot, b.BalancesRoot))
                Add("error", seq, "chain-roots", $"published roots differ from block {b.Height} ({b.BlockId})");
        }

        var balances = new Dictionary<string, decimal>();
        Anchor? prev = null;
        foreach (var a in ordered)
        {
            report.AnchorsChecked++;
            if (chainBySeq.ContainsKey(a.Seq)) report.AnchorsInChain++;
            else Add("warn", a.Seq, "committed", "anchor not (yet) committed in this Blocker's chain");

            var expectedSeq = (prev?.Seq ?? 0) + 1;
            if (a.Seq != expectedSeq) Add("error", a.Seq, "contiguous", $"expected seq {expectedSeq}");
            if (prev != null && Utc(a.FromExclusive) != Utc(prev.UpToInclusive))
                Add("error", a.Seq, "contiguous", "FromExclusive does not continue the previous anchor's UpToInclusive (rows could be skipped)");

            if (!entries.TryGetValue(a.Seq, out var rows))
            {
                Add("error", a.Seq, "entries", "ledger rows for this anchor are not available");
                prev = a;
                continue;
            }
            if (rows.Count != a.EntryCount) Add("error", a.Seq, "entry-count", $"anchor says {a.EntryCount}, published {rows.Count}");
            var from = Utc(a.FromExclusive);
            var upTo = Utc(a.UpToInclusive);
            if (rows.Any(r => r.CreatedAt <= from || r.CreatedAt > upTo))
                Add("error", a.Seq, "entry-window", "a published row lies outside the anchor's time window");
            if (rows.Select(r => r.Id).Distinct().Count() != rows.Count)
                Add("error", a.Seq, "entry-unique", "duplicate ledger row ids");

            var ledgerRoot = MerkleRoot(rows.Select(LedgerLeaf));
            if (!Eq(ledgerRoot, a.LedgerRoot)) Add("error", a.Seq, "ledger-root", $"recomputed {ledgerRoot}, anchored {a.LedgerRoot}");

            foreach (var r in rows)
            {
                if (r.Amount <= 0) Add("error", a.Seq, "amount", $"row {r.Id} moves a non-positive amount");
                if (string.IsNullOrEmpty(r.FromPublicKey))
                {
                    report.Minted += r.Amount;
                    if (r.Kind != "genesis") Add("error", a.Seq, "mint", $"row {r.Id} creates {F10(r.Amount)} D outside genesis (kind {r.Kind})");
                }
                else balances[r.FromPublicKey] = (balances.TryGetValue(r.FromPublicKey, out var f) ? f : 0m) - r.Amount;
                if (!string.IsNullOrEmpty(r.ToPublicKey))
                    balances[r.ToPublicKey] = (balances.TryGetValue(r.ToPublicKey, out var t) ? t : 0m) + r.Amount;
                else Add("error", a.Seq, "burn", $"row {r.Id} has no recipient");
            }
            foreach (var k in balances.Where(kv => kv.Value == 0m).Select(kv => kv.Key).ToList()) balances.Remove(k);
            report.EntriesReplayed += rows.Count;

            foreach (var neg in balances.Where(kv => kv.Value < 0 && kv.Key != "deltanium-treasury"))
                Add("error", a.Seq, "negative-balance", $"{neg.Key} goes to {F10(neg.Value)}");

            var balancesRoot = BalancesRoot(balances);
            if (!Eq(balancesRoot, a.BalancesRoot))
                Add("error", a.Seq, "balances-root", "BalancesRoot is not the replay of the published ledger (balances changed outside the ledger, or rows hidden)");

            prev = a;
        }

        report.LatestSeq = prev?.Seq;
        report.TotalSupply = balances.Values.Sum();
        if (report.TotalSupply != report.Minted)
            Add("error", null, "conservation", $"sum of balances {F10(report.TotalSupply)} ≠ minted {F10(report.Minted)}");
        if (hardCap.HasValue && report.Minted > hardCap.Value)
            Add("error", null, "hard-cap", $"minted {F10(report.Minted)} D exceeds hard cap {F10(hardCap.Value)} D");
        return report;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
