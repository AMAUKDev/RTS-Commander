# Delivery bypass — unload in place instead of landing

**Track:** delivery-bypass_20260916 · **Date:** 2026-09-16 · **Status:** approved by user
**Survey this answers:** `conductor/designs/2026-09-16-transport-delivery-survey.md`

## 1. Goal

A cargo transport should deliver its vehicles predictably. Today it must satisfy the game's own
unload gate — radar altitude under 2 m and speed under 10 m/s — and inside 300 m the game turns
flight assist off and hovers at 20 m, which is where deliveries stall and where the tiltwing crashes.
Instead: arrive, hold low and roughly still, place the vehicles on clear ground, empty the aircraft,
send it home.

## 2. Evidence, and what this does and does not fix

From the survey, across three play blocks: 80 cargo flights launched, 16 put a vehicle on the ground.

| Failure | Flights |
|---|---|
| Shot down in transit, load and all | 18 |
| Recalled by our own route watch, load intact | 22 |
| Never left the deck | 12 |
| Stall clock tripped (hovering) | 9 |

**This track fixes the hovering and the landing crash. It does not touch the 18 shot down or the 22
we recall ourselves** — the two largest buckets. The user was shown these numbers and chose the
bypass anyway, on the grounds that predictable delivery is worth having on its own. That decision is
recorded here so nobody re-litigates it, and the two larger causes are named as separate work.

The tiltwing is the crasher: VL-49 Tarantula lost 7 of 24 to the ground against the UH-90 Ibis's 3 of
32. Two Tarantulas of one wave hit the ground seconds apart at the same landing zone.

## 3. Decisions taken (user, 2026-09-16)

Bypass **every** delivery, not only stalled ones. Vehicles appear on **clear ground nearby**. The
transport must be **near-stationary and low** first. The protection delivered vehicles get today is
**kept**.

## 4. Design

**4.1 The unload condition.** One pure rule: the transport is within the landing zone's existing
stall radius (`InsertionStallRadiusMeters`, 500), its speed is below an unload speed, and its radar
altitude is below an unload height. Reuse the low-and-slow detection the platform approach already
does rather than writing a second one. Self-checks at each boundary.

**4.2 The bounded wait, which is the risk in "must also be low".** A transport that cannot descend
would never unload — the hovering problem again. So the height test is bounded by the EXISTING stall
clock (`OperationsInsertionStallTimeoutSeconds`, 120): once it expires, the transport unloads at
whatever height it has reached, and if it is too high for that the existing parachute fallback takes
it. No new clock, no new setting.

**4.3 Where the vehicles appear.** Clear ground near the transport, found with the landing-zone
picker that already avoids trees, slopes, water and other units
(`Supply/CommanderSupplyHeliLandingZone.cs`). If nothing clear is found inside the search, they go
on the terrain directly beneath the aircraft — which is still better than not delivering.

**4.4 How they appear.** Firing a cargo mount is already how every delivered vehicle enters the
world, and firing already leaves the aircraft reading empty, so the game agrees it has nothing left
to deliver and flies home on its own. The mod already issues the trip home
(`IssueInsertionReturnToBase`). This half is reuse, not new work.

**4.5 Spacing.** The game's ramp-clear handshake is the one thing the bypass genuinely breaks: it
exists to stop one vehicle dropping onto another. Unloading in place means the mod owns the spacing,
so vehicles are released one at a time with a short gap, reusing the existing release cadence fields
(`NextCargoReleaseAt`, `ReleasedCargoCount`) rather than dropping them all in one frame.

**4.6 Kept.** The insertion shield — invulnerable until settled, righted if it lands on its side
(`Supply/CommanderSupplyHeliShield.cs`). The delivery credit forward bases and platoon lifts count.
The parachute fallback, now as the second path when the bounded wait expires too high.

**4.7 Logging gap, fixed in the same change.** The fate line naming what killed an airframe is
written only for the computer enemy commander, so the player's own transports have no such lines in
any block and their crashes are unmeasured. Extend it to the player's own faction so the tiltwing
crash rate is measurable at all.

## 5. Acceptance criteria

1. Self-checks pass at load, covering the unload condition's boundaries, the bounded wait, the
   spacing, and that a delivery still credits its order.
2. In play: deliveries succeed without the transport touching down; no transport hovers at a landing
   zone beyond the bounded wait; the tiltwing stops dying at landing zones; the player's own
   transport losses now print a fate line.
3. Delivery success rate rises from the measured 16 of 80.
4. Build 0/0, hot reload clean, no exceptions.

## 6. Out of scope, and named so it is not forgotten

The 18 flights shot down in transit and the 22 recalled by our own route watch. Both are larger than
what this track fixes and both need their own work.
