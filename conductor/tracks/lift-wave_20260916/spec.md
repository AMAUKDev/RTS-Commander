# lift-wave_20260916 — An air-mobile platoon goes in ONE wave, or it does not go

## Goal

A platoon lift launches every load it still owes in the same review, to the same landing zone, under
the same escort. If it cannot assemble the whole wave, nothing leaves the deck and the order waits;
if the wait runs out, the order is cancelled the way a failed lift already is and the platoon drives.

## Problem, with evidence

`DispatchFobAirFlights` (`Operations/CommanderOperationsFob.cs:1940`) sends one load at a time:

```
if (CountFobFlightsFor(state, order) > 0 || order.Delivered >= order.LoadsWanted) { return; }
```

The comment above it (lines 1946–1953) records why: "Three concurrent requests to one point would be
the first time the supply side is asked to hold three cargo missions for the same
`CommanderStrategicPoint`, and its per-point calls … are written to the invariant that a point
carries at most one." A platoon therefore arrives as three separate flights over fifteen minutes and
the marker reads `1ST PLATOON — 0/3 delivered` for most of it. User instruction, 2026-09-16: "should
only happen if all 3 aircraft can fly at once to deploy full platoon in one flight (plus escort)."

The per-point invariant is real and was verified against the current code. Five places break with
more than one flight per point:

| Place | What breaks with N flights |
|---|---|
| `CancelInsertion` (`Supply/CommanderSupplyHeliMission.cs:1267`) | Cancels EVERY queued and live mission for the point. One crew ejecting on a deck would recall the whole wave. |
| `TryNoteFobLaunchFailed` (`Operations/CommanderOperationsFob.cs:2557`) | Drops ONE flight record but calls the point-wide `CancelInsertion`, leaving two records with no mission behind them. |
| `PruneFobFlights` stale and stall branches (`…Fob.cs:2353`, `:2392`) | Same: one record removed, every mission on the point cancelled. |
| `TryConvertInsertionToAirdrop` (`Supply/CommanderSupplyHeliLandingZone.cs:446`) | Converts the first mission it finds on the point, not the flight that actually stalled. |
| `TryTakeFobDelivery` (`…Fob.cs:2421`) | For a platoon lift, credits the first flight record on the point regardless of which transport unloaded, so a wingman's flight is never marked `Delivered` and its recovery is then counted as a LOST load by the sweep. |

`pendingAircraftSpawn` (`Supply/CommanderSupplyHeliService.cs:71`) holds exactly one spawn, but it is
transient: `TrySpawnCargoRunAtAirbase` sets it, registration clears it, and requests that arrive while
it is occupied are enqueued on `queuedCargoSpawns` and drained by `TryProcessQueuedCargoSpawns` on the
one-second supply tick, one per tick. It does not need to hold N. Three loads requested in one review
therefore leave the ground within about two seconds of each other. That is the honest limit of "the
same review" and is recorded here rather than papered over.

`TryBindFobAircraft` already binds "the first flight of this point with no aircraft", which is
correct for N — but binding by point alone is guesswork once the wave exists, so it is made exact.

## The existing code being reused (Reuse rules 1–5)

- `LiftMayLaunch` (`Operations/CommanderOperationsAirPlatoonCap.cs:400`) and `LiftCoverIsUp`
  (`…Fob.cs:2112`) — the escort gate and the bounded wait, UNCHANGED. No second clock is added.
- `LiftAffordable` (`…Fob.cs:1045`) — the money gate, unchanged; only the price handed to it grows.
- `CancelFobOrder` (`…Fob.cs:2703`) — the give-up-and-drive path, unchanged.
- `WithdrawLiftFlights` (`…Fob.cs:1824`) — its per-flight body is EXTRACTED to
  `WithdrawLiftFlight` so the partial-wave recall calls the one definition (Reuse rules 3 and 5).
- `CommanderSettings.OperationsHeliInsertionLimit` (6), `LiftLoadsPerPlatoon` (3),
  `LiftFundsMultiple` (2), `PackageFormUpSeconds` — all existing. **No new setting is added**; every
  number the wave needs already exists, and inventing a second airborne ceiling or a second form-up
  clock is exactly what Reuse rule 4 forbids.
- No new service, no new Harmony patch, no new scheduler.

## Requirements

1. **All loads or none.** Before a platoon lift launches anything it must have: room under the shared
   airborne ceiling for EVERY outstanding load at once; funds for every outstanding load at once at
   the existing multiple; the escort up by the existing `LiftCoverIsUp`; and the existing route,
   hopeless and landing-zone gates satisfied. Any failure launches NOTHING and logs once in the
   existing "holds at the form-up point" / denial voice.
2. **The wave.** When it may go, every outstanding load launches in the same review, each with its
   own hold post, all to the same landing zone, under the one escort.
3. **One flight, one identity.** A lift flight carries a slot id through the supply side
   (`QueuedCargoSpawn`, `PendingAircraftSpawn`, `CargoMission`), so cancel, airdrop conversion,
   aircraft binding and vehicle delivery address ONE flight. A slot id of zero means "not slotted"
   and every existing caller — every picket insertion, every SAM run, every UI cargo run — keeps
   today's point-wide behaviour exactly.
4. **Give up and drive.** No new clock. The existing `FobOrderTimeoutSeconds` stale valve and the
   existing hopeless rule cancel the order, and `CancelFobOrder` hands the platoon back to an
   ordinary driving platoon as it does today.
5. **Forward-base construction is untouched.** `FobDeliveries` is 1, so a construction order's
   outstanding count is never above one and every new gate collapses to the expression it replaced.
   A self-check pins that equivalence at every input.
6. **The counts still read correctly.** A flight carries its own 1-based load ordinal so three
   transports in the air do not all read `1/3`. The order-level marker is unchanged.

## Acceptance criteria

- A platoon lift with three loads owed, funds for three, room for three and its escort up launches
  three flights in one review and logs one wave line.
- Any one of those four short: nothing launches, one line says which, the order waits.
- A construction order behaves identically to today, pinned by self-check.
- One load of a wave lost in flight: the other two are untouched, `order.Lost` rises by one, and the
  next review launches a replacement wave of one under the same gates.
- A crew abandoned on the deck withdraws ONLY that load.

## Files in scope

`Operations/CommanderOperationsFob.cs`, `Operations/CommanderOperationsInsertion.cs`,
`Operations/CommanderOperationsService.cs`, `Operations/CommanderOperationsAirMarkers.cs`,
`Supply/CommanderSupplyHeliMission.cs`, `Supply/CommanderSupplyHeliLandingZone.cs`,
`CHANGELOG.md`, `conductor/decision-log.md` (DECISION-046).

## Out of scope

Road convoys. Picket insertions. SAM foundation drops. The naval supply path. Any change to what a
platoon is made of or to how a landing zone is chosen. Making `pendingAircraftSpawn` hold more than
one spawn — the queue already serialises correctly and widening it would be a real concurrency risk
for no gain.
