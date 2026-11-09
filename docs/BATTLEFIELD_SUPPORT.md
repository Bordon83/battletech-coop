# Battlefield Support 0.1.0 — separate development module

Implemented and checked locally on 2026-11-08. Branch: `feature/battlefield-support-core`.
This is the requested first phase: data, economics, preparation contracts and host validation.
It is a usable headless development library, not an installable/playable support mod yet.

## Repository and version audit

Source: <https://github.com/Cryswar/battletech-coop>.

| Evidence | Finding |
|---|---|
| Inspected `main` | `49b1a37ea72662cee4301e787e8bcd6dbb91ce14`, co-op version **0.0.13** |
| Requested “0.8” | No `v0.8` tag was present in the fetched repository. There is **v0.0.8**, commit `85898fa5dde30fab732e31eca89b0bf544b1ae80`. Its manifest says 0.0.8. |
| v0.0.8 | Campaign combat transfer/shared orders and session cleanup exist; later ship requests and shared mission-opening coordination are absent. |
| Current mod | `src/BattletechCoop/`: net472, C# 7.3, Harmony/game assembly references, ModTek entry point |
| Headless precedent | `tests/NetTests/`: .NET 8 console runner compiling the actual Unity-free network sources |
| Since v0.0.8 | Ship snapshots/requests/updates, further company operations and shared briefing/dialogue start were added through v0.0.13. |
| Local context | No pre-existing checkout or configured cloud project was available. A complete clone was saved in this task's output folder. GitHub connector reported pull=true, push=false. |

The feature branch starts at current `main`, retaining subsequent integration work. It does not
reinterpret 0.0.8 as 0.8 or claim binary compatibility with either game integration. The co-op
mod project, manifest, network protocol and runtime source remain unchanged. The new module
can also be reviewed/cherry-picked independently, because it references none of that runtime.
No game binaries, decompiled code or private saves were added.

## Delivered layout

| Path | Responsibility |
|---|---|
| `src/BattlefieldSupport/Model.cs` | Immutable support, targeting, Career, mission, selection and plan types |
| `Catalog.cs` | Injectable catalog, original display labels and provisional economic defaults |
| `Preparation.cs` | `IMissionPreparation`, immutable panel data and availability rows |
| `HostSupportService.cs` | Host validation, shared preparation, reservation, lifecycle and paid orders |
| `Ports.cs` | Host context, verified targeting and atomic state-store contracts |
| `State.cs` | Immutable revisioned company ledger, receipts, order outbox and prototype memory store |
| `tests/SupportTests/` | Headless behavioral tests using the actual library source |
| `.github/workflows/support-tests.yml` | Windows CI definition: library build, support tests and existing network tests |

The library builds alone as net472 with C# 7.3. It has no Unity, BattleTech, ModTek, Harmony,
JSON or transport dependencies. Do not place it in `Mods/` yet: it has no ModTek entry point.

## Support scope and rules fidelity

The public publisher rulebook provides a category-level reference for artillery, minefields
and aerospace support, including preparation requirements for artillery/mines:
[Catalyst's Mercenaries rulebook](https://battletech.com/wp-content/uploads/2025/07/BatttleTech-Mercenaries-Rulebook-1st-Print-1.pdf).
This is not a verification of the exact contents/rules of a particular Battlefield Support
Deck printing. An edition-specific rule comparison remains necessary before combat work.

| Prototype definition | Type / PC target | C-Bills per use | Contracts / contact rank | Equipment | Slots / max uses |
|---|---|---:|---|---|---|
| Thumper artillery | Artillery / area, preplanned | 5,000 | 0 / artillery 1 | — | 1 / 2 |
| Sniper artillery | Artillery / area, preplanned | 15,000 | 5 / artillery 2 | artillery.link | 2 / 2 |
| Long Tom artillery | Artillery / area, preplanned | 35,000 | 15 / artillery 3 | artillery.link | 3 / 1 |
| Light minefield | Minefield / area, preplanned | 3,000 | 0 / engineering 1 | — | 1 / 2 |
| Heavy minefield | Minefield / area, preplanned | 10,000 | 5 / engineering 2 | minelayer | 2 / 1 |
| Strafing run | Offensive air / line with distinct endpoints | 20,000 | 5 / aerospace 2 | — | 2 / 2 |
| Bombing run | Offensive air / area | 25,000 | 10 / aerospace 3 | air.control | 3 / 1 |
| Defensive air cover | Defensive air / untargeted | 12,000 | 5 / aerospace 2 | — | 2 / 2 |

Every price, slot, progression/contact/equipment requirement and quantity in this table is
**provisional mod balancing**, not an official card value. Stock means available company-wide
support uses supplied by the future Career adapter. No starting stock or money is granted to
the real company. These are prototype definitions, not a full card deck or a damage simulation.
Reconnaissance, electronic warfare and resupply are excluded from this baseline because the
earlier conversation did not establish them as original deck cards.

No card images, card prose, rule tables or scanned assets are included. Names and short labels
identify the support roles; any eventual UI descriptions must be newly written. Damage,
scatter, attack timing, mine triggering/clearing, friendly fire and air interception still need
edition-specific design and verified engine capabilities. Area radius/geometry is intentionally
not guessed. The target coordinate units are opaque until the game adapter defines them.

## Economics and lifecycle

1. `GetPreparation(peer, mission)` returns a read-only view: revision, prices, shared stock,
   unlock/shortage reasons, current shared selection, money and Career/mission limits. Individual
   option ceilings apply if that option is selected alone; combined decks still need validation.
2. `Prepare(peer, request, revision, mission, selections)` replaces the **whole shared deck**.
   It checks definition IDs, duplicate entries, unlocks, allowed support kinds, company stock,
   per-definition limits, total slots/uses, targets and affordability. Preparation reserves the
   full quoted cost but does not deduct money or consume stock. A zero-item deck is allowed.
3. `Launch(...)` is restricted to the host's mission manager. It freezes the selection and
   rechecks all preparation requirements against current host data. Launch does not charge.
4. `RequestUse(...)` requires a deployed mission, remaining prepared use and legal host-validated
   target/capability. A successful atomic commit deducts the frozen per-use C-Bills price,
   consumes one stock unit, reduces the reservation and appends one uniquely identified order.
   **This authorizes and charges a pending accounting order; it does not execute a game effect.**
5. `Close(...)`, restricted to the mission manager, cancels preparation or ends/aborts deployment.
   It releases unused reservations. Unused stock was never deducted, so it needs no refund.
   Accepted/charged orders and consumed stock remain in the audit history. An old mission ID
   cannot reopen allowances. Only one mission may be active for the company at a time.

Example: 10,000 C-Bills and two planned Thumper uses reserve 10,000, leaving zero available.
One accepted use leaves a 5,000 balance, a 5,000 reservation and one remaining use. Closing then
releases the unused reservation: the balance/available amount is 5,000. A rejected use costs zero.

All costs use checked `long` arithmetic and integer basis-point multipliers, rounded up.
The default multiplier is 10,000 (=100%); configured multipliers range from 1 to 100,000.
A valid use always costs at least one C-Bill. Overflow rejects the request without writes.
Changing the cost multiplier after preparation does not reprice that mission's frozen quote.

Career capacity defaults are `2 + min(4, completedContracts / 5)` preparation slots and
`2 + min(4, completedContracts / 10)` total mission uses. Mission restrictions can lower both.
Contacts/equipment gate individual definitions. These are our policy fields, not assumed
BattleTech property names. Contacts, equipment, mission restrictions, stock and money are
rechecked at launch/use; loss of any requirement conservatively blocks further use of the plan
until the host resolves it or closes it. Spending elsewhere can invalidate a reservation.

## Authority, concurrency and persistence boundary

- The future transport adapter must derive `peerId` from `CoopSession.GameMessage`'s authenticated
  sender metadata. Never accept sender identity, prices, stock, unlocks or mission limits from
  a client payload. Only the host constructs the service and trusted environment/store adapters.
- Both host and guest requests pass the same validation; only lifecycle management additionally
  requires `CanManageMission`. Membership is checked before returning cached receipts.
- Preparation and usage compete on one company revision. First valid compare-and-swap wins;
  a stale request must refresh the snapshot. There are no per-player allowances or wallets.
- Accepted request IDs are scoped by peer identity. Exact retries return the original receipt
  and revision/order ID, even after later state changes. Different contents with the same ID
  are rejected. Selection ordering is canonicalized. Failed requests are not cached or charged.
- The returned revision on an idempotent reply is the original accepted revision, not the latest
  snapshot revision. Refresh the preparation view/state before sending a different command.
- `IHostSupportStore.TryCommit` must atomically persist balance, stock, plan, closed mission IDs,
  receipts and order outbox. Compare-and-swap loss writes nothing. Host environment changes must
  be serialized with these operations and advance the company revision when they affect validity.
- `MemorySupportStore` demonstrates these semantics in one process. The restart test reconstructs
  the service over the same store; it does **not** establish disk/save or crash recovery.
- `WithEconomy` supports trusted host economy refreshes through the same store revision. It can
  invalidate reservations; the preparation view can show a negative available amount until
  the host resolves the shortage. It is not a money API for guests.
- **Do not debit the game wallet and then commit the support ledger separately.** A real adapter
  needs coordinated save/transaction semantics with every other Career money/stock writer.
  No such adapter is provided or presumed safe in this phase.
- The future effect sequencer must deduplicate the outbox's `OrderId` and persist completion.
  Exactly-once accounting here is not exactly-once execution in the game. Do not automatically
  retry, refund or close with unresolved effects until execution/compensation policy exists.
- Identifiers are bounded printable ASCII (96 characters); definitions/selections use ordinal
  comparison. There are at most 64 entries in a preparation command. Network payload length,
  parser bounds and request-rate limits still belong in the unimplemented wire adapter.
- Accepted history is bounded at 4,096 writes per store lifetime. The final slot is reserved
  for close/cancellation, so a full history cannot trap the current reservation. Other new
  commands fail with `HistoryFull` after 4,095 writes; exact retries still work. This is a
  prototype guard, not a finished long-Career retention policy. Implement durable compaction
  with replay safety and an early capacity warning before enabling live Career use.

## PC preparation and control contract

The future panel reads `IMissionPreparation.GetPreparation`, displays support name/type,
price, remaining company supply, selected quantity and the reason for locked options. It shows
reserved and available C-Bills plus slot/use limits. Selecting a row edits a local draft;
Confirm sends the complete selection with the displayed revision. A stale result refreshes
the shared deck and requires a new deliberate confirmation.

Point/area support uses mouse map selection; strafing uses two distinct points. Artillery/mines
carry their predeployment map positions with `Selection`. This prototype locks each selected
definition to one such position for its planned uses; multiple positions for the same type
and a full printed-card designation model are future extensions. `ITargetPolicy` validates
preparation and use separately. The default `UnsupportedTargets` rejects every target/effect
authorization, including untargeted air cover. Tests supply explicit fake capabilities.

The contract exists; no PC screen, keyboard shortcut, Unity widget or battle HUD is installed.
A future adapter must offer the preplanning map at a verified point before launch. An abstract
`Launch` call here is not proof that the existing briefing can provide that timing/map access.

## Integration points verified by repository inspection

| Existing component | Observed behavior | Proposed connection / outstanding proof |
|---|---|---|
| `CoopController.OnGameMessage` | Dispatches ship, mission-start and combat channels | Add an explicit support dispatcher before the broad `channel >= ChannelCombatStart` branch. No support channel numbers have been allocated. |
| `Net/Core/CoopSession.cs` | Host delivers player identity from its joined transport peer; guest game messages are attributed to host index 0 | Translate sender to a stable session-scoped identity; validate membership. Numeric peer indices alone must not survive identity reuse/reconnect. |
| `Sync/ShipRequests.cs` | Existing host/guest requests and host updates use channels 21–23 | Reuse the request/result/snapshot pattern, not the guest's optimistic finance writes. Support must never charge on a guest. |
| `Sync/ShipView.cs` | Company snapshots/refresh use channels 19–20 | Add an explicitly versioned support snapshot, tied to the same company and save generation. Current saves do not contain this module's state. |
| `Sync/MissionStart.cs` | Readiness, shared Begin and dialogue use channels 24–28 | Coordinate host plan lock with all peers' briefing readiness; verify an earlier hook for map predesignation. Do not guess game method patches. |
| `Sync/CombatSync.cs` | Ordered invocation stream, quiet-state barriers, save transfer and desync checks | Future support effects must enter this authoritative sequence, with deterministic RNG and result replay. Independent local effects would desynchronize combat. |
| `Sync/SaveGuard.cs` | Restricts guest saves; real saves must be protected | Persist support on host only with schema migration and coordinated save generation. Test only in isolated game copies. |
| `ModManifest.cs` / protocol handshake | Matching mod lists/versions are required | Agree on catalog, schema, adapter and effect versions for all peers before activation. Keep the module disabled on mismatch. |

These are source-level observations, not new runtime verification of game APIs. The project's
design document refers to private `ca/` analysis that is absent from this checkout. No actual
BattleTech/Unity assemblies, effect invocation types, finance calls or patch signatures were
available for verification in this task. The existing mod itself was not game-built or played.

## Verification and reproduction

Prerequisite: .NET 8 SDK. From repository root:

```powershell
dotnet build src/BattlefieldSupport -c Release
dotnet run --project tests/SupportTests -c Release
dotnet run --project tests/NetTests -c Release
```

Local results, 2026-11-08, Windows, SDK 8.0.427:

- net472 independent library: **passed**, zero warnings/errors.
- Support behavior: **31/31 passed**. Includes unlocks, limits, insufficient money/stock,
  target restrictions, read-only preparation view, no-capability rejection, stale edits,
  idempotent retries, conflicting request IDs, failed storage commit, concurrent host/guest
  spending, frozen quotes, cancellation, history bounds and service reconstruction.
- Existing network suite: **12/12 passed** over local TCP. An initial restricted run produced
  socket-permission errors/timeouts (1/12); rerunning with local socket access allowed passed.
- CI workflow is provided but has **not run on GitHub**, since this branch was not uploaded.
  Action usage follows the upstream [checkout](https://github.com/actions/checkout) and
  [setup-dotnet](https://github.com/actions/setup-dotnet) documentation.
- No in-game, Steam, two-copy game, real save, disk-persistence, damage/effect or end-to-end UI
  tests were claimed. No card media or text were copied.

## Next development steps and exit criteria

1. Verify the selected deck edition from a legally held reference. Record a mechanics checklist
   in original prose and resolve predesignation, geometry, timing and defensive-air semantics.
2. Inspect the user's target build's assemblies using the repository's read-only analysis tools.
   Prove finance mutation/save coordination, progression/contact mapping, preparation map access
   and support effect sequencing. Keep all binaries/dumps outside git.
3. Implement a durable host store with schema migration, save-generation binding, retention,
   capacity warnings and order completion/compensation. Test crashes before/after each step;
   demonstrate no duplicated cost, lost debit, reopened allowance or repeated effect.
4. Implement bounded, versioned wire request/result/snapshot adapters and stable sender identity.
   Extend manifest negotiation, reconnect/resync and request-rate tests; reject spoofed/old data.
5. Build the PC preparation panel and map selection on verified hooks. Test shared edits, stale
   revisions, cancel/back, mission switches, all-peers readiness and host disconnect.
6. Implement one deterministic artillery effect through the existing ordered stream, then mines
   and offensive/defensive air support. Every peer must replay the same outcome. No accepted
   paid order may be dropped silently; rejected requests must leave money/stock unchanged.
7. Run the existing bot-driven two-copy tests on game copies with saving disabled. Add support
   desync, reload/retry, wallet conflict and mission-abort scenarios. Only then add a ModTek
   entry point/release package and enable live Career use.

To publish this work later, push `feature/battlefield-support-core` to a writable repository
or fork and open a review. No remote branch, pull request or cloud save was created here.
