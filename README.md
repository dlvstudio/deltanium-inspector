# Deltanium Inspector

Independent verifier for TOKENOMICS_V2 §11 phase C. Anyone can run it; it reads only public endpoints and trusts nothing Central says until it matches the chain.

```bash
dotnet run -- --central https://api.deltanium.com --blocker https://blocker-1.deltanium.com --out report.json
```

For each ledger anchor it checks:

| Check | Catches |
|-------|---------|
| `chain-roots`, `published` | Central publishing different roots than the Blockers committed, or hiding an anchor |
| `contiguous`, `entry-window`, `entry-count` | Ledger rows skipped between anchors or moved across anchors |
| `ledger-root` | Published rows rewritten after they were anchored |
| `balances-root` | Balances edited outside the ledger, or rows hidden from the published history |
| `mint`, `hard-cap`, `conservation` | D created outside genesis, supply above 10,000,000 DLT, or D appearing / disappearing |
| `negative-balance` | An account spending more than it had |

Exit code: `0` all good, `1` at least one error finding, `2` could not reach Central or the Blocker. `--watch <seconds>` repeats the check.
