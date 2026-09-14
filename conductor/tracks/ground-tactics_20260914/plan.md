# Plan: Ground tactics — wide ring, defence arc, bounding advance, counter-attack

**Track**: ground-tactics_20260914 · **Design**: design.md (approved 2026-09-14), decision-log
DECISION-012
**Expected agent runs**: 1 executor (this session) + 1 review + evaluation, per Track shape.
**Build after every task**: PowerShell `$env:NUCLEAR_OPTION_DIR = "I:\SteamLibrary\steamapps\common\Nuclear Option"; dotnet build GroundControlRts.csproj -c Release`
— must end `0 Warning(s)` / `0 Error(s)`. No install scripts, no `build-dev.bat`, no commits.

**Concurrency**: two other agents hold the AIR side (`Ai/*`, `AirCommand/*`,
`Operations/CommanderOperationsAir.cs`) and share `Core/CommanderSettings.cs` and `CHANGELOG.md`.
Every file is re-read immediately before it is edited; the two shared files take additive edits
only, each followed by a build.

## Architecture

No new service. Ground tactics is posture inside the platoon state machine the operations service
already runs (design Reuse: "Not reused: a separate tactics service — duplicates the move seam and
the state machine"). The geometry and its pure rules live in one new partial,
`Operations/CommanderOperationsTactics.cs`, beside the existing `…Front.cs` / `…Offensive.cs`
partials; the movement tick gains posture branches, and `CommanderPlatoon` gains the posture fields
those branches keep between ticks.

Every rule that carries a number is a pure static over floats and ints — ring spacing, arc bearing,
bound sizing, the garrison boundary — so `SelfCheck()` drives it at plugin load, the only automated
test this mod has.

Moves reuse the one seam: `CommanderMoveService.IssuePlatoonMove(members, destination, shape,
issued, facingDegrees)` for anything in formation, `DriveMembersToPosts` for anything on individual
posts. No new formation shape and no new move method are needed — Line, Wedge and the existing
facing parameter cover all four postures.

## Tasks

- [x] T1 **Settings and the tactics partial.** `Core/CommanderSettings.cs` (re-read first, additive):
  `DefenceArcStandoffMeters` (Operations, 400), `BoundMeters` (Operations, 800),
  `OffRoadRangeMeters` (Operations, 1000), each with a rationale comment and a warm-up touch. New
  `Operations/CommanderOperationsTactics.cs` partial holding the track's constants with `<summary>`
  rationale (`RingMinSpacingMeters` 250, `InnerRingFraction` 0.5, `AirDefenceRingFraction` 0.3,
  `DefenceArcSpacingMeters` 150, `DefenceArcReaimDegrees` 15, `DefenceArcHoldSeconds` 60,
  `BoundTimeoutSeconds` 90, `CounterAttackReachMeters`, `CounterAttackFlankMeters` 1000,
  `ScreenRangeMeters` 800, `ScreenRoadSnapMeters`) and an empty `CheckGroundTactics(List<string>)`
  wired into `CommanderOperationsService.SelfCheck`. Verification: build clean, self-check still
  passes at load.

- [x] T2 **Ring geometry (design §1).** Pure `RingSlotsFor(radius, wanted, minSpacingMeters)` — the
  most evenly spaced posts a circle of that radius carries at that neighbour chord, clamped to
  `wanted` and at least 1 — and pure `PlanHoldPosts(radius, threatBearingDegrees, outerCount,
  airDefenceCount, innerCount, minSpacingMeters, List<CommanderPostSlot> slots)` filling
  (radius, bearing) slots: the outer ring at the full hold radius, its oversubscribed remainder on
  an inner ring at `radius × InnerRingFraction`, the air-defence pair at
  `radius × AirDefenceRingFraction` centred on the threat bearing, the carrier/truck posts at the
  same fraction on the far side. Each slot's facing is its own bearing from the centre (outward).
  SelfCheck cases in `CheckGroundTactics`: neighbour spacing never below 250 m; an oversubscribed
  ring pushes the remainder inward; air defence lands at 0.3 R on the threat half and the inner far
  side at 0.3 R opposite; facing equals bearing.

- [x] T3 **Role-to-post assignment (design §1).** `EnsureHoldPosts` keeps its signature and meaning
  (the outer ring's positions — `CommanderOperationsInsertion.cs:628` reads it) but builds from
  `PlanHoldPosts` at the full radius, cached per point by wanted-slot count and threat-bearing
  bucket. New `EnsureHoldPostSet(point, members)` returns the three post lists; `DriveToHoldPosts`
  splits members by `CommanderPlatoonRoles.Of` and calls the existing `DriveMembersToPosts` once per
  group (air defence → inner pair, carrier and truck → inner far side, armour and anything else →
  outer ring), so the stable instance-id sort, the on-station skip and the `Issued` write are reused
  unchanged. Threat bearing from the nearest tracked hostile inside the point's threat ring, else the
  bearing to the nearest enemy asset, else 0. Verification: build clean; in-game the ring marker
  spread is the design's acceptance test.

- [x] T4 **Posture state and marker texts (design §5).** `CommanderGroundPosture` enum (Ring,
  DefenceArc, Bounding, CounterAttack, Screen) and the fields the postures keep on
  `CommanderPlatoon` (`Posture`, `PostureBearing`, `BoundTarget`/`BoundFor`/`BoundIssuedAt`/
  `BoundLogged`, `FlankPosition`/`FlankReached`), all cleared by the sweep that resets a platoon.
  `MarkerSituation` gains the posture argument and the four texts `Defence line at <label>`,
  `Bounding to <label>`, `Counter-attacking from the flank`, `Screening <label>`; `Holding <label>`
  and every existing text are unchanged for `Ring`. SelfCheck: the four new cases beside the
  existing ones in `CheckMarkerLabels`.

- [x] T5 **Defence arc geometry (design §2).** Pure `DefenceArcDistanceMeters(radius, standoff)`
  (radius plus the standoff, the standoff cut to the radius on a small point — departure 1),
  `DefenceArcBearing(threatBearing, slotIndex, spacingMeters, arcDistanceMeters)` (slot 0 on the
  bearing, neighbours alternating either side at 150 m of arc), `ArcNeedsReaim(aimed, wanted,
  reaimDegrees)` across the 0/360 seam, and `HoldsDefenceArc(inContactUntil, now, contactHold,
  arcHold)` — which answers the freshness question through the existing `LossIsRecent`.
  SelfCheck: centre tank on the bearing; neighbours ±150 m of arc; the small-radius clamp; re-aim
  fires past 15° and not at 15°; the arc holds 60 s after contact lapses and drops at 61 s.

- [x] T6 **Defence arc wired (design §2).** In the `Holding` branch of `TickMovement`, after
  `DetectHoldingContact`: while the platoon or its mission is in contact, armour and carrier members
  drive arc posts on the threat bearing (tanks central, by `MarchRank`), air defence stays on its
  ring posts, carriers with no armour rating and the truck take the inner far-side posts; the arc is
  re-aimed only when the bearing has moved more than 15°; 60 s after contact lapses the platoon
  returns to the ring. Log once per transition:
  `<platoon> forms a defence arc toward <bearing>° at <label>` and `<platoon> returns to the ring`.

- [x] T7 **Bounding geometry (design §3).** Pure `NextBound(from, objective, boundMeters)` (the
  objective itself once inside one bound), `BoundComplete(inCohesion, memberCount, secondsAtBound,
  timeoutSeconds)` (half the platoon closed up, or 90 s), and `BoundsCrossCountry(attackLaunched,
  distanceToNearestTrackedHostileMeters, offRoadRangeMeters)`. SelfCheck: an 800 m bound on a 2 km
  leg stops at 800 m and the last bound lands exactly on the objective; half-closed releases the
  next bound and one short of half does not; the timeout releases at 90 s; a destination 1000 m from
  a tracked enemy bounds and 1001 m does not.

- [x] T8 **Bounding wired (design §3).** An `Attacking` platoon whose attack has launched advances
  from where it stands in `BoundMeters` bounds toward `platoon.Objective`, `OrderForMarch` first and
  `CommanderFormationShape.Line` with the heading toward the objective, each bound issued through
  `IssuePlatoonMove`. The contact drill keeps priority (it is tested before the bound branch, as it
  is today). One log line per attack:
  `<platoon> leaves the road at the release point; bounding to <label> in 800 m steps`.

- [x] T9 **The 1 km off-road rule (design §3).** A `Moving` platoon whose nearest tracked hostile is
  inside `OffRoadRangeMeters` bounds to its objective by the same branch instead of taking the
  direct Wedge move, so no destination near a tracked enemy is a long road-following path.

- [x] T10 **Reinforcement posture, the decision (design §4).** Pure
  `ReinforcementPosture(isReinforcing, garrisonPresent, minGarrison, enemyTracked)` returning
  Ring / CounterAttack / Screen, and pure `FlankBearing(threatBearing, takeLeft)`. Garrison present
  counts the live members of the mission's OTHER platoons (and its picket members) inside the
  point's radius. SelfCheck: at the garrison boundary a reinforcing platoon counter-attacks with an
  enemy tracked, screens without one, and folds into the ring one vehicle below minimum; a
  non-reinforcing platoon always rings.

- [x] T11 **Counter-attack wired (design §4).** A reinforcing platoon at a held point with an enemy
  tracked moves to the flank position `CounterAttackFlankMeters` off the threat bearing on the side
  farther from tracked hostiles, then attacks the nearest tracked hostile in Line, bounding by T8's
  branch. Marker reads `Counter-attacking from the flank`.

- [x] T12 **Screen wired (design §4).** With nothing tracked, the same platoon screens
  `ScreenRangeMeters` out on the most threatened approach — the bearing of the nearest hostile the
  tracking database still remembers, else the bearing to the enemy's nearest asset — snapped to the
  nearest road within `ScreenRoadSnapMeters` of that position, in a Line facing outward. Needs a
  small helper in `Points/CommanderStrategicPointService.cs`: pure
  `TryNearestRoadPoint(roads, position, maxMeters, out closest, out distance)` with
  `NearestRoadDistanceMeters` delegating to it (Reuse rule 5, behaviour-neutral) and a live wrapper;
  and `TryNearestEnemyAsset` generalised out of `NearestEnemyAssetDistance` in `…Front.cs`, which
  keeps its behaviour by calling it. Marker reads `Screening <label>`. SelfCheck: the road snap
  takes the nearest segment point and refuses past the cap.

- [x] T13 **Fold into the ring, and diagnostics.** A reinforcing platoon whose point's garrison has
  dropped below `PointsMinGarrison` reverts to Ring posture and takes ring posts; the review
  diagnostics line carries each platoon's posture beside its state, so a match can be read back from
  `LogOutput.log` alone.

- [x] T14 **CHANGELOG and final rebuild.** `CHANGELOG.md` (re-read first, additive) Unreleased entry
  in the existing player-facing voice; `dotnet build GroundControlRts.csproj -c Release -t:Rebuild`
  ending `0 Warning(s)` / `0 Error(s)`.

## Departures from the design

Recorded as they are taken.

1. **Defence arc standoff (design §2).** The design asks for the arc at `Radius +
   DefenceArcStandoffMeters` and also says "no vehicle leaves the hold radius", which cannot both
   hold once §1 puts the ring on the FULL hold radius: clamping the arc inside the radius would make
   the 400 m setting dead, and a dead setting is a placeholder. Implemented as the design's stated
   intent (tanks and IFVs forward of the ring): arc distance is `radius + min(standoff, radius)`, so
   a small point clamps the standoff to its own radius and a normal one puts the arc 400 m outside
   the ring. The point keeps paying because the air-defence pair, the truck and any carrier with no
   armour rating stay on their ring posts inside the radius — `PointsMinGarrison` is 2 and those are
   three vehicles.
2. **Facing outward (design §1).** The engine exposes no facing order: `TrySetDestination` takes a
   destination only, and `IssuePlatoonMove`'s `facingDegrees` lays out formation SLOTS, it does not
   turn a vehicle. Each post therefore records its outward bearing (used by the self-check and the
   arc's slot layout) and the outward facing is delivered by geometry: a member drives its post from
   inside the ring, so it arrives pointing out.
3. **The contact drill over the counter-attack (design §4).** §3 says the contact drill keeps
   priority over bounding; §4 has the counter-attack bound by "Section 3 rules" but does not repeat
   the sentence. It is applied there too — a counter-attacking platoon that can see a hostile
   deploys into its firing line rather than driving past it — because the alternative is the one
   platoon in the fight that never marks contact, and the contact mark is what calls the air wing
   in over it.
4. **A screen marks contact (design §4).** A screen is a standing position like a hold post, so the
   platoon-on-its-posts detection from the 2026-09-14 addendum runs for it. Without this a screen
   being overrun would never ask for air support.
5. **`EnsureHoldPosts` and the picket insertion.** The insertion's landing posts
   (`Operations/CommanderOperationsInsertion.cs:628`) ask for a ring by slot count and have no
   platoon composition to spread by. Rather than let two callers with different needs rebuild the
   cache against each other every tick, that caller is handed whatever ring the point already has
   and only plans one when none exists.
