# TMO-assisted live memory reader

`TmoAssistedMemoryRecognitionService` is a read-only bridge for one pinned pair of binaries:

- Warcraft III `2.0.4.23745`, SHA-256 `682C12552CA05E43C5FED2340EA132D3B06FE068E676DB7D1F5623D8D4633229`
- TMO.GG `1.0.0.0`, SHA-256 `AC0103AD641A5E88CEE0FFD7A7862584E5E55F8203BFC50A9265B0C207A4FA68`

It opens both processes with `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION`. It does not write
memory, invoke remote functions, inject DLLs, install hooks, or scrape TMO's stale JSON buffers.

## Live path

1. Find exactly one private TMO object whose VMT is `TMO base + 0x459788`.
2. Validate its Warcraft PID (`+0x08`), Warcraft image base (`+0x20`), generator state (`+0x120`),
   generator code (`+0x128`), and the full fixed-byte generated-decoder signature.
3. Derive the live GameUI and WorldFrame keys from the generated XOR mask and `state + 0x30`.
4. A zero GameUI returns `Waiting` with an empty `Entries` list. The service contains no last-result cache.
5. Read the generic pool at `WorldFrame + 0xB98` (count) / `+0xBA0` (pointer).
6. Keep only exact MSVC RTTI `.?AVCUnit@@` objects, deduplicate them, and select the live local owner.
7. Count the unit rawcode at `+0x178`. For exact `.?AVCAbilityInventory@@` at unit `+0x5A0`, resolve up
   to six handles through the verified Warcraft handle tables and count item rawcodes as TMO does.
8. Convert the raw `ReadUInt32` byte order directly through `RawcodeUnitMap`. Only rawcodes present in the
   app data or the pinned TMO card catalog (excluding resource pseudo-keys) enter the card inventory; local
   map/controller/helper CUnits remain visible in diagnostics and cannot invalidate a stable snapshot.
9. Locate the unique local `4C0H` controller, verify its baseline `A0K8` ability through the Warcraft handle
   table, and expose unused Green Blood when ability `A13A` is present. A missing baseline leaves only this
   special state unknown; it does not invalidate normal card counts.

A nonzero live GameUI/WorldFrame plus a stable, validated pool is authoritative: if it contains no local
catalog-backed cards, the reader returns `Ready` with an empty list so selling the last card clears the overlay.
A zero GameUI/WorldFrame remains `Waiting` and is a confirmed session boundary, so the UI clears the previous
game's cards/manual corrections. An uninitialized pool, a TMO reconnect, or wrapper discovery delay also returns
`Waiting` and clears stale automatic cards, but preserves manual corrections until a real boundary is observed.

Each result is accepted only after two complete snapshots agree. Every pass verifies the pool byte array,
local-player root/id, CUnit rawcodes, inventory handles/resolved objects, and the decoder state/GameUI/WorldFrame
before and after enumeration. A structural failure retries once, then returns a transient error without replacing
the current in-match result. A missing/corrupt/undersized card catalog fails as a configuration error.

The VMT/object address is cached only to avoid rescanning TMO's heap every 1.2 seconds. It is fully
revalidated on each sample. Unit counts and `RecognitionResult` values are never cached by this service.

## One-shot diagnostic

With TMO.GG and Warcraft III running:

```powershell
dotnet run --project SmokeTests/OrandOverlay.SmokeTests.csproj -c Release -- --live
```

Outside a match the expected result is `Waiting entries=0` with `GameUI=0 · 캐시 반환 안 함` in the
diagnostics. During a match it prints every mapped unit and count once.

For state/Luffy changes only (useful for exit/re-entry checks), replace `--live` with `--watch`.

## Compatibility limits

- Any file-version or SHA-256 mismatch fails closed. A Warcraft or TMO update needs a new audited profile.
- Pool, RTTI, field, decoder, and handle-table layouts are build-specific.
- The consistent snapshot gate detects pool/local-player changes and retries once. A second race returns a
  transient read error and does not replace the prior UI inventory.
- Windows process access policy or elevated game/TMO processes can prevent read access; the overlay then
  reports a read error rather than falling back to old data.
- Green Blood is a script ability state (`A13A` before use), not a normal locally-owned card CUnit. The reader
  now detects the unused/held state. Once applied (`A134`/`A13C` or a Seraphim transformation), it is no longer
  a held card; use manual correction only if a recommendation should treat that already-applied effect as present.
