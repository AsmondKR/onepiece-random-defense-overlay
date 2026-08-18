# Warcraft III direct memory reader

`WarcraftMemoryRecognitionService` is a read-only direct reader for pinned Warcraft III builds.
TMO.GG is not required and is not used.

## Activation gates (fail-closed)

A profile activates only when **all** of these pass:

1. `enabled: true` and `verified: true` in `memory-profiles.json`
2. `sha256` field is non-empty and matches the SHA-256 of the running `Warcraft III.exe`
3. `MemoryProfileValidator.Validate` finds no structural errors in the profile fields

Any gate failure returns a non-`Ready` `RecognitionState` and leaves the current inventory unchanged.
No fallback path (screen capture, manual correction, or a second process) exists.

## Live path

1. Locate the unit list root via `locatorKind`:
   - `SignatureRelative`: scan the WC3 module image for the unique byte pattern in `signature`,
     resolve the RIP-relative displacement, and cache the resulting address keyed by process ID,
     start time, module base, profile ID, and profile revision. A zero-match or multi-match scan
     returns `TransientReadError` without replacing the prior inventory.
   - `ModuleOffset`: add the fixed `moduleOffset` directly to the module base address.
2. Follow `pointerOffsets` from the root to reach the live unit-list structure.
3. Call `ReadConsistentSnapshot` twice and compare; a structural change between the two reads
   retries once, then returns `TransientReadError` (last good inventory is preserved).
4. Discard entries whose owner slot does not match `localPlayerSlot`.
5. Read `rawcodeOffset` from each owned CUnit. Count entries per rawcode.
6. Map rawcodes through `RawcodeUnitMap`. Only rawcodes in the app catalog or the auxiliary
   card catalog (excluding resource pseudo-keys) enter the card inventory; unmapped CUnits are
   counted as `UnknownObjects` in diagnostics and cannot invalidate a stable snapshot.
7. Apply fail-closed guards before returning `Ready`:
   - `OwnedObjects == 0` → `Waiting` (no local cards visible yet)
   - `catalogRatio < minimumCatalogMatchRatio (0.6)` → `TransientReadError`
   - `ListCount > maximumUnits` → `TransientReadError`
8. Return a `RecognitionResult` with `State = Ready` and the mapped `Entries`.

## State semantics

| State | Meaning | UI action |
|---|---|---|
| `Waiting` | WC3 not running, or `OwnedObjects == 0` (pre-match) | Clear prior game's cards; wait |
| `TransientReadError` | Short race or guard failure | Keep last good inventory; continue recommendations |
| `UnverifiedProfile` | `enabled`/`verified` false, or SHA-256 mismatch | Red banner; suspend recommendations |
| `Unsupported` | Running WC3 version has no matching profile | Red banner; suspend recommendations |
| `ConfigurationError` | Profile JSON error or rawcode catalog missing | Red banner; suspend recommendations |
| `Ready` | Snapshot validated and mapped | Replace inventory; show recommendations |

## One-shot diagnostic

With Warcraft III running:

```powershell
dotnet run --project SmokeTests/OrandOverlay.SmokeTests.csproj -c Release -- --live
```

Outside a match the expected result is `Waiting entries=0`. During a match it prints every
mapped unit and count once. Replace `--live` with `--watch` to stream state changes only.

## Compatibility limits

- Any `sha256` mismatch fails closed. A WC3 update requires a new profile with a fresh
  live-session verification pass before `verified` can be set to `true`.
- Signature scan finds 0 or > 1 matches → `TransientReadError` (profile is not activated).
- Pool, RTTI, field, and handle-table layouts are build-specific; pointer offsets are stored
  per profile in `memory-profiles.json`.
- Windows process access policy or elevated WC3 processes can prevent read access;
  the overlay reports a read error rather than falling back to any other source.

## Related documents

- [`../../ARCHITECTURE.md`](../../ARCHITECTURE.md) — workspace-level architecture (single memory recognition path)
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — app module map
- [`docs/superpowers/specs/2026-08-19-tmo-independent-root-scanner-design.md`](docs/superpowers/specs/2026-08-19-tmo-independent-root-scanner-design.md)
  — root scanner design. Its §4 dual-pass (TMO fallback) and the §2/§6 "keep the current structure if R"
  clauses are superseded by this single-path implementation.
