using Deltanium.Inspector;
using FluentAssertions;
using Xunit;

namespace Deltanium.Inspector.Tests;

public class VerifierTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static LedgerEntry Row(string id, string? from, string to, decimal amount, string kind, int sec) => new()
    {
        Id = id, FromPublicKey = from, ToPublicKey = to, Amount = amount, Kind = kind,
        CreatedAtRaw = T0.AddSeconds(sec).ToString("O")
    };

    /// <summary>Build anchors exactly like Central does, so a clean history verifies.</summary>
    private static (List<Anchor> Anchors, Dictionary<long, List<LedgerEntry>> Entries, List<ChainAnchor> Chain) History(params List<LedgerEntry>[] batches)
    {
        var anchors = new List<Anchor>();
        var entries = new Dictionary<long, List<LedgerEntry>>();
        var chain = new List<ChainAnchor>();
        var balances = new Dictionary<string, decimal>();
        var from = DateTime.MinValue;
        for (var i = 0; i < batches.Length; i++)
        {
            var rows = batches[i];
            foreach (var r in rows)
            {
                if (r.FromPublicKey != null) balances[r.FromPublicKey] = balances.GetValueOrDefault(r.FromPublicKey) - r.Amount;
                balances[r.ToPublicKey!] = balances.GetValueOrDefault(r.ToPublicKey!) + r.Amount;
            }
            foreach (var k in balances.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList()) balances.Remove(k);
            var a = new Anchor
            {
                Seq = i + 1, FromExclusive = from, UpToInclusive = rows[^1].CreatedAt, EntryCount = rows.Count,
                LedgerRoot = Verifier.MerkleRoot(rows.Select(Verifier.LedgerLeaf)), BalancesRoot = Verifier.BalancesRoot(balances)
            };
            from = a.UpToInclusive;
            anchors.Add(a);
            entries[a.Seq] = rows;
            chain.Add(new ChainAnchor { Height = i + 1, BlockId = "b" + i, LedgerAnchorSeq = a.Seq, LedgerRoot = a.LedgerRoot, BalancesRoot = a.BalancesRoot });
        }
        return (anchors, entries, chain);
    }

    private static (List<Anchor>, Dictionary<long, List<LedgerEntry>>, List<ChainAnchor>) Clean() => History(
        new List<LedgerEntry> { Row("g", null, "deltanium-treasury", 1000, "genesis", 1) },
        new List<LedgerEntry> { Row("t1", "deltanium-treasury", "alice", 300, "topup", 10), Row("t2", "alice", "bob", 100, "transfer", 11) });

    [Fact]
    public void Clean_history_verifies()
    {
        var (a, e, c) = Clean();
        var r = Verifier.Verify(a, e, c, 1000);
        r.Ok.Should().BeTrue(string.Join("; ", r.Findings.Select(f => f.Check + ":" + f.Detail)));
        r.TotalSupply.Should().Be(1000);
        r.AnchorsInChain.Should().Be(2);
    }

    [Fact]
    public void Rewritten_row_breaks_ledger_root()
    {
        var (a, e, c) = Clean();
        e[2][1].Amount = 1;
        Verifier.Verify(a, e, c, 1000).Findings.Should().Contain(f => f.Check == "ledger-root" && f.Seq == 2);
    }

    [Fact]
    public void Hidden_row_is_detected()
    {
        var (a, e, c) = Clean();
        e[2].RemoveAt(1);
        var checks = Verifier.Verify(a, e, c, 1000).Findings.Select(f => f.Check).ToList();
        checks.Should().Contain(new[] { "entry-count", "ledger-root", "balances-root" });
    }

    [Fact]
    public void Balance_edited_outside_ledger_breaks_balances_root()
    {
        var (a, e, c) = Clean();
        var forged = new Dictionary<string, decimal> { ["deltanium-treasury"] = 700, ["alice"] = 5000, ["bob"] = 100 };
        a[1].BalancesRoot = Verifier.BalancesRoot(forged);
        c[1].BalancesRoot = a[1].BalancesRoot; // even if a Blocker committed it
        Verifier.Verify(a, e, c, 1000).Findings.Should().Contain(f => f.Check == "balances-root");
    }

    [Fact]
    public void Minting_outside_genesis_and_above_cap_is_flagged()
    {
        var (a, e, c) = History(
            new List<LedgerEntry> { Row("g", null, "deltanium-treasury", 1000, "genesis", 1) },
            new List<LedgerEntry> { Row("m", null, "alice", 50, "adjustment", 5) });
        var checks = Verifier.Verify(a, e, c, 1000).Findings.Select(f => f.Check).ToList();
        checks.Should().Contain("mint").And.Contain("hard-cap");
    }

    [Fact]
    public void Roots_published_differently_from_chain_are_flagged()
    {
        var (a, e, c) = Clean();
        c[0].LedgerRoot = new string('a', 64);
        Verifier.Verify(a, e, c, 1000).Findings.Should().Contain(f => f.Check == "chain-roots" && f.Seq == 1);

        var (a2, e2, c2) = Clean();
        a2.RemoveAt(1);
        Verifier.Verify(a2, e2, c2, 1000).Findings.Should().Contain(f => f.Check == "published" && f.Seq == 2);
    }

    [Fact]
    public void Gap_between_anchors_is_flagged()
    {
        var (a, e, c) = Clean();
        a[1].FromExclusive = a[1].FromExclusive.AddSeconds(2);
        Verifier.Verify(a, e, c, 1000).Findings.Should().Contain(f => f.Check == "contiguous");
    }

    [Fact]
    public void Leaf_format_matches_central()
    {
        var row = new LedgerEntry { Id = "x", FromPublicKey = null, ToPublicKey = "t", Amount = 1.5m, Kind = "genesis", RefId = "r", CreatedAtRaw = "2026-11-01T00:00:00.1234567Z" };
        Verifier.LedgerLeaf(row).Should().Be(Verifier.Sha256Hex("x||t|1.5000000000|genesis|r|2026-11-01T00:00:00.123456Z"));
    }
}
