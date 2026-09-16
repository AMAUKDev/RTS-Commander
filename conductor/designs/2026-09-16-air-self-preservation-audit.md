# Air self-preservation audit

Read-only audit, 2026-09-16. Prompted by the user's words: *"insert some sense of self-preservation
into our aircraft — i feel like we might have a mess of current systems"*.

Scope: every rule in the mod, and in the game underneath it, that decides whether a
commander-controlled aircraft engages, holds, retreats, waits, is sent, or is written off. Nothing
below was changed.

---

## 1. Inventory

### 1.1 What the GAME already does, per aircraft

All of this lives in `AIPilotCombatModes` (fixed wing) and `AIHeloCombatState` (rotary), decompiled at
`/tmp/no-decomp/Assembly-CSharp.decompiled.cs`.

| Rule | Where | Trigger | Effect | Clock |
|---|---|---|---|---|
| Bravery/threat target refusal | `CombatAI.ChooseHQTarget:724` | best opportunity × bravery × 2 < 0.35, HQ-rated threat of that target higher, and range > 2 × weapon max range | the target is discarded; the pilot goes to `NoTarget` | 5 s (`AssessHQTargets`, `:12525`) |
| Threat-vector avoidance | `ApplyThreatAvoidance:12574` | flying to a target with a live threat vector | destination rotated away from the summed threat, scaled by `bravery + friendly-proximity morale` | 1 s, inside `FlyToTarget` |
| Friendly-proximity morale | `FriendlyAircraftProximity.CheckMorale:16130` | friendlies airborne within 5 km | raises the avoidance denominator, so a lone aircraft bends away harder than one in a formation | 5 s, cached |
| Break off attack | `BreakOffAttack:12740`, entered from ten sites (bomb release, minimum-range missile shot, gun overshoot, blocked line) | per-mode conditions | flies at the nearest own airbase for a timed break, throttle 100 % | 1 s check, per-frame run |
| Retreat to standoff | `RetreatToStandoff:12800` | inside the current weapon's minimum range | flies directly away to 2 × turning radius or 2 × min range | 1 s |
| Missile evasion | `ChooseEvadeMode:13326`, `EvadeModeRadar:13470`, `EvadeModeIR:13455` | missile warning | notching, flares, terrain hugging; the autopilot is given `evadeDestination`, computed **after** the attack mode ran | per frame |
| Flare pre-emption | `AIPilotCombatState_On1sInterval:12495` | inside a high anti-air target's max range with > 50 % flares | pops flares | 1 s |
| Radar-warning descent | `AICombat_OnRadarWarning:12518` | painted by an emitter | target height drops by 20–50 m × the emitter's anti-air rating | event |
| Fuel-out landing | `FuelChecker:16150`, called in `FlyToTarget` and `NoTarget` | fuel below 20 % | pilot switched into the landing state | 5 s cached, checked at 1 s |
| Idle landing | `NoTarget:12644` | 15 ticks with no target and no missile alerts | pilot switched into the landing state | 1 s |
| Ejection | `EjectionCheck:13537` | below sea level, stopped on the deck, or > 12 % of the airframe detached | ejects the crew | 5 s |

### 1.2 What the MOD adds, per aircraft (the mission record)

| Rule | Where | Trigger | Effect | Clock | Settings |
|---|---|---|---|---|---|
| Target choice replaced wholesale | `AirCommand/CommanderAirCommandPilotHooks.cs:33`, patch at `CommanderAirCommandPatches.cs:19` | the aircraft has a commander mission | the game's `ChooseHQTarget` never runs; a mod scorer picks the target | 5 s | — |
| Going home means no targets | `CommanderAirCommandPilotHooks.cs:50` | `Returning` or a commanded landing base | returns "no target, out of ammo" | 5 s | — |
| Self-defence leash | `CommanderAirCommandPilotHooks.cs:338`, rule at `:273` | the mission carries a self-defence radius | targets beyond that radius from the aircraft are ignored unless commanded | 5 s | `AirSelfDefenceRadiusMeters` (6 km) |
| Mission-area target filter | `CommanderAirCommandPilotHooks.cs:344` | CAS, ARAD or strike mode | targets outside the mission ring are ignored | 5 s | — |
| Air-superiority range gate | `CommanderAirCommandPilotHooks.cs:352` | AirGuard mode | targets beyond 1.05 × weapon max range are ignored | 5 s | — |
| Out-of-ammo ends the mission | `CommanderAirCommandPilotHooks.cs:417` | no loaded eligible store, route finished | `Returning` set; the aircraft turns for home | 5 s | — |
| Idle-landing timer suppressed | `CommanderAirCommandPatches.cs:58` | the aircraft has a hold point | `timeWithoutTarget` zeroed every tick | per frame | — |
| Destination forced to route/hold | `CommanderAirCommandPatches.cs:68` | same | destination overwritten with the mission's route point or hold | per frame | — |
| Attack destination clamped into the area | `CommanderAirCommandPilotHooks.cs:221`, patch `CommanderAirCommandPatches.cs:88` | AirGuard or AWACS, route finished, no hold override | destination pulled back to 0.75 × the mission radius of the area centre | per frame | — |
| Station altitude forced | `CommanderAirCommandPilotHooks.cs:203` | AirGuard or AWACS with a band | target height overwritten | per frame | `CapBand*Meters` |
| Internal cannon emptied | `CommanderAirCommandMissions.cs:313`, sweep at `CommanderOperationsAirRadarWatch.cs:564` | every commander launch | 0 gun rounds, so the pilot cannot make strafing runs and reads as Winchester sooner | launch + 30 s | `AirIncludeInternalCannons` |
| Deck recovery | `CommanderAirCommandRecovery.cs:203` | RTB aircraft stopped on the deck for 3 s near a friendly base | returned to inventory, funds refunded | 2 s | — |
| Landing airbase substituted | `CommanderAirCommandLanding.cs:392` | the mission names a landing base | that base is used instead of the game's search | on entering the pattern | — |

### 1.3 What the MOD adds, per sortie (the operations review)

| Rule | Where | Trigger | Effect | Clock | Settings |
|---|---|---|---|---|---|
| Outnumbered fall-back | `Operations/CommanderOperationsAirPosture.cs:267`, rule `:37` | hostiles exceed our present fighters by the margin, inside the posture ring of the objective *or of any of our fighters* | hold point 15 km toward the nearest own base, self-defence leash on every bound aeroplane, demand raised, sortie marked in contact | 5 s (logistics watch, `CommanderOperationsLogistics.cs:80`) | `AirFallbackMargin` 2, `AirPostureRingMeters` 20 km, `AirFallbackDistanceMeters` 15 km, `AirSelfDefenceRadiusMeters` 6 km |
| Re-engage | `CommanderOperationsAirPosture.cs:307`, rule `:45` | our fighters exceed theirs by the margin | hold and leash cleared | 5 s | `AirReengageMargin` 1 |
| Stand down | `CommanderOperationsAirPosture.cs:314`, `:490` | 5 minutes falling back without relief | airframes released, sortie takes the loss cooldown, a strike is abandoned | 5 s | `AirFallbackGiveUpMinutes` 5 |
| Clear fall-back point | `CommanderOperationsAirPosture.cs:379` | every watch while falling back | the hold point is walked further toward the base in 2 km steps until no hostile is within the ring | 5 s | `AirPostureRingMeters` |
| Package form-up wait | `CommanderOperationsAirPackages.cs:299`, point search `:844` | a package is short of its element | everyone orbits 12 km out on the friendly side, at a point clear of every enemy asset and tracked hostile by 20 km | 30 s | `PackageFormUpSeconds` 180 |
| ARAD-first hold | `CommanderOperationsAirArad.cs:609`, refresh `CommanderOperationsAirPackages.cs:743` | a suppression sortie near the objective has not reached the belt | the CAS package holds at form-up | 30 s | `AradClusterMinimum` |
| Rotary CAS held behind suppression | `CommanderOperationsAirWing.cs:1233` | air defence observed and no suppression flown yet | helicopters are not sent; jets go instead | 30 s | — |
| Escort sizing | `CommanderOperationsAirPackages.cs:92`, `CommanderOperationsAirPlatoonCap.cs:420` | every review | escorts never fewer than the hostile aircraft tracked, never fewer than two for a transport, capped by the room under the airborne ceiling | 30 s | `AirborneCeiling` family, `LiftEscortMinimum` |
| Lift launch gate | `CommanderOperationsAirPlatoonCap.cs:400` | a load wants to leave the deck | it waits for its escort to be up and the sweep gone in, or 180 s | 5 s | `PackageFormUpSeconds` |
| Delivery hope rule | `CommanderOperationsLogistics.cs:43`, watch `:122` | route threatened, landing inside the standoff, or escorts lost with hostile air near the route | transports recalled or diverted; the order is cancelled after 6 minutes | 5 s | `LiftHopelessMinutes`, `LiftAbortStandoffMeters` |
| Insertion route threat | `CommanderOperationsInsertion.cs:672` | air defence or any aircraft within the threat radius of the route samples | the insertion is refused or withdrawn | 5 s | `HeliInsertionEnemyStandoffMeters` |
| Retask to contact | `CommanderOperationsAirRetask.cs:24` | a sortie is in contact and short | takes an airframe off the quietest sortie; strike packages, lift covers and suppression sorties are protected | 30 s | — |
| Loss cooldown | `CommanderOperationsAirRadarWatch.cs:621` | a bound airframe dies while bound and not returning | the sortie buys nothing for 2 min, doubled with two or more air-defence vehicles observed; 10 min for the radar aeroplane | 30 s | `CasLossCooldownMinutes` |
| Winchester / transport sent home | `CommanderOperationsAirIdle.cs:136` | idle, nothing but guns left | ordered home | 30 s | — |
| Strike-tier and radar refused the patrol | `CommanderOperationsAirIdle.cs:164` | idle ground-attack, helicopter or radar airframe | sent home rather than put on an air-superiority orbit | 30 s | — |
| Stuck on deck written off | `CommanderOperationsAirPlatoonCap.cs:101` | 4 minutes on a deck with a mission, never airborne | despawned and refunded, explicitly not counted as a loss | 30 s | `StuckOnDeckMinutes` |

### 1.4 What the MOD adds, per faction (the buy ladder)

| Rule | Where | Trigger | Effect | Clock |
|---|---|---|---|---|
| Airborne ceiling | `CommanderOperationsAirRadarWatch.cs:1087`, enforced `Ai/CommanderEnemyCommanderAirBuy.cs:176` and `:708` | aircraft airborne at or above the ceiling | no purchase, and the element order breaks mid-way | 30 s |
| Attrition brake | `Ai/CommanderEnemyCommanderAttrition.cs:421`, hold `:368` | losses × 3 > launches over 10 minutes with at least 6 launches, and the best affordable pick is no better than what is dying | that wing side buys nothing and the airborne ceiling halves | 30 s |
| Attrition escalation | `Ai/CommanderEnemyCommanderAttrition.cs:213` | bleeding | buys the best affordable instead of the cheapest, drops the package type pin | 30 s |
| Quiet release | `Ai/CommanderEnemyCommanderAttrition.cs:349` | 5 minutes with no loss on that side | brake lifted and the whole 10-minute window erased | 30 s |
| Home-CAP loss memory | `CommanderOperationsAirHomeCap.cs:31` | a patrol fighter dies with hostile air within 15 km | one more patrol fighter wanted for 10 minutes | 10 s |
| Cheapest-airframe ban | `Ai/CommanderEnemyCommanderAir.cs:910` | any role-capable airframe exists at any price | the bottom-tier type is never bought | 30 s |

---

## 2. Overlaps and conflicts

**The mod discards the game's only refusal to attack.** The Harmony prefix on `CombatAI.ChooseHQTarget`
(`CommanderAirCommandPatches.cs:19`) returns false for every commanded aircraft, so the bravery and
HQ-threat test at decompiled line 724 never executes. `Aircraft.bravery` still exists, still defaults
to 0.5, and still feeds threat avoidance — but the one place it could say *"do not take this fight"*
is bypassed. `ChooseMissionTarget` has no threat term at all: it scores opportunity ÷ range and picks
the best. This is the single largest reason a commanded fighter flies at something it cannot beat.
The mod's scorer also drops the game's energy-weapon charge test for "out of ammo", and replaces the
game's range penalty with a plain divide by range.

**The mod partially undoes the game's disengagement.** `ConstrainMissionDestination` runs as a postfix
on `RunAttackMode`, which executes every physics frame. For air-superiority and radar missions with
the route finished, it pulls the destination back to within 0.75 × the mission radius of the area
centre. Break-off, retreat-to-standoff and threat avoidance all write their destination on the 1 s
check and are then clamped back toward the patrol box on the very next frame. With a CAP ring of
6 km that box is 4.5 km wide, so a fighter told by the game to run for its airbase is held in the
fight. Missile evasion survives, because the autopilot is given `evadeDestination` computed after
`RunAttackMode` — that is luck, not design.

**Two rules decide "fight only what is near me", differently.** The self-defence leash
(`CommanderAirCommandPilotHooks.cs:338`) exists only while a sortie is falling back. The
air-superiority range gate (`:352`) is permanently on and already refuses anything beyond 1.05 ×
weapon range. A CAP fighter therefore has a leash at all times, and a second, tighter leash only in
the state the posture is trying to leave.

**The fall-back cannot lower the number it is measured against.** `AirFallbackDistanceMeters` is 15 km
and `AirPostureRingMeters` is 20 km. `HostileCountsAgainstSortie` counts a hostile within the ring of
the objective *or* of any of our fighters, so retreating 15 km leaves the objective inside 20 km and
the count unchanged. Reinforcement or the 5-minute stand-down are the only exits. The doc comment at
`Core/CommanderSettings.cs:666` still describes an 8 km ring that was replaced on 2026-09-16 and is
now stale.

**`AirPostureRingMeters` does two unrelated jobs.** Besides the outnumbered ring it is the package
form-up standoff (`CommanderOperationsAirPackages.cs:880`). Retuning the retreat trigger silently
moves every package's orbit.

**Three ladders size the escort and can disagree.** The package table
(`CommanderOperationsAirPackages.cs:147`), the live re-size against the room under the airborne
ceiling (`:92`), and the fall-back's call for help (`CommanderOperationsAirPosture.cs:424`) each write
`CapsWanted`. Under the brake the ceiling halves, so the sortie that is losing asks for more fighters
at the exact moment the headroom that caps the request has been cut in half.

**The attrition brake acts only on the wallet.** It refuses purchases and halves the ceiling. It never
recalls, lands or thins out anything already in the air, so the sky that produced the losses keeps
flying. Its release also erases the whole 10-minute window, so a side that gets five quiet minutes
starts again from nothing.

**Four "wait then give up" clocks with four different numbers.** Fall-back 5 min, delivery 6 min,
insertion stall 120 s, strike package 12 min. Five "after a loss, wait" clocks: CAS 2 min, insertion
10 min, strike point 10 min, FOB 15 min, radar 10 min. None share a definition.

---

## 3. Gaps — where a lone aircraft still flies to its death

1. **A damaged airframe is never considered.** Nothing in the mod reads `partDamageTracker`,
   `GetDetachedRatio` or any condition value for an aircraft. `AutoRetreatDamaged` and
   `RetreatConditionPercent` exist but are ground-unit only (`Units/CommanderMoveService.cs:836`) and
   ship disabled. A fighter with a wing half off keeps its station.
2. **A game-initiated landing leaves the mod's books wrong.** When the fuel checker or the idle timer
   switches the pilot into the landing state, nothing sets `Returning`. The sortie still counts the
   aircraft as on station (`PruneSortieAirframes` only unbinds on `Returning`), and the deck recovery
   sweep skips it because it requires `Returning && RtbIssued` — so it is handed to the taxi state,
   which is exactly the path `CommanderAirCommandRecovery.cs:12` documents as where these airframes
   die.
3. **Fuel is never a commander-level concern.** 20 % is the game's only threshold. Nothing shortens a
   station, brings a patrol home early, or refuses to send an aircraft to a target it cannot reach and
   return from.
4. **A single fighter bound to a sortie has no fall-back until the next 5 s watch**, and the posture
   refuses to act at all until `CountFightersUp > 0` and a nearest own airbase exists. A commander with
   no airbase left never falls back (`BeginFallback` returns early at `:351`).
5. **Helicopters are excluded from the posture by design** (§4.6 of the design note). Their only
   protection is the game's own threat avoidance, and their break-off (`AIHeloCombatState:10767`)
   flies at the nearest airbase with no timer discipline.
6. **A suppression sortie that has gone in is never retasked, never falls back and never gives up.**
   `CommanderOperationsAirRetask.cs:89` protects it as a source; the posture applies to it, but a
   single ARAD airframe over a live belt is the classic write-off.
7. **A SAM belt with nothing able to carry an anti-radiation missile opens no suppression sortie at
   all** (`CommanderOperationsAirArad.cs:426`) and nothing else changes: the CAS package flies in
   anyway with no hold and no extra standoff.
8. **The out-of-ammo rule keeps an aircraft on station while it still has travel points to fly**
   (`CommanderAirCommandPilotHooks.cs:60`). Intended for transports, but a strike jet whose racks
   emptied mid-route keeps flying the route into the target area.
9. **No per-aircraft "I am losing this fight" signal exists.** Every retreat decision is either the
   game's per-frame geometry or the mod's 5 s/30 s sortie-level arithmetic. There is nothing in
   between that reads *this* aeroplane's missile warnings, damage, fuel and the threat around it.

---

## 4. Suggested shape

Four layers, each owning one question, with nothing owning two.

**Layer 1 — the game's pilot owns the seconds.** Missile evasion, break-off, retreat-to-standoff,
threat-vector avoidance, flares, terrain. The mod should stop fighting it:

- Restore the bravery refusal inside `ChooseMissionTarget` (`CommanderAirCommandPilotHooks.cs:278`).
  Move the three-clause test from decompiled `ChooseHQTarget:724` into a pure
  `TargetIsTooDangerous(opportunity, bravery, hqThreat, range, maxRange)` next to
  `TargetOutsideSelfDefence` (`:273`), with its own self-check cases. One definition; the enemy
  commander and the player share it.
- Stop clamping the destination while the pilot is disengaging. `ConstrainMissionDestination`
  (`:221`) should also skip when the pilot's attack mode is `BreakOffAttack` or `RetreatStandoff`,
  read through the existing `CommanderAirCommandPatches.GetStateAircraft` reflection pattern.

**Layer 2 — a per-aircraft survival state that does not exist yet.** One new field group on
`AirMission` (`CommanderAirCommandTypes.cs:185`) and one evaluator on the existing 2 s mission-prune
clock in `CommanderAirCommandService.cs:201`, beside `ProcessReturningMissions`. It answers one
question: *should this airframe leave?* Inputs it can already reach: `partDamageTracker`
detached ratio (the game's own ejection test uses it), `GetFuelLevel`, missile alert count, and the
hostile count the posture already computes. Output: set `Returning` through the existing
`RequestReturnToBase` path, which is the one door that already handles rotary pads, recovery and
relaunch. This layer also fixes gap 2 for free: if the pilot is in a landing state and the mission is
not `Returning`, mark it so.

Reuse, not new code: `CommanderAirCommandService.IsWinchester:558` is already this shape and should
move into the same evaluator. `TryReturnAiAircraftHome:515` is the action.

**Layer 3 — the sortie posture owns the formation.** Keep `CommanderOperationsAirPosture.cs` exactly
as it is, minus two fixes: raise `AirFallbackDistanceMeters` above `AirPostureRingMeters` (or measure
the fall-back against the ring it is leaving), and split the form-up standoff off
`AirPostureRingMeters` into its own constant. Delete `AirSelfDefenceRadiusMeters` and have the leash
reuse the air-superiority range gate at `CommanderAirCommandPilotHooks.cs:352`, which already exists
and already answers "close enough to shoot".

**Layer 4 — the review owns the wallet.** The attrition brake, the ceiling and the loss cooldowns stay
where they are. Add one thing: when the brake fires, the review should thin the sky rather than only
stop buying — release the sorties that are over their loss cooldown through the existing
`ReleaseBoundAirframes` door (`CommanderOperationsAirAwacs.cs:829`).

**Settings after the merge.** Keep `AirFallbackMargin`, `AirReengageMargin`,
`AirFallbackDistanceMeters`, `AirFallbackGiveUpMinutes`, `AirPostureRingMeters`, and add exactly two:
a damage fraction and a fuel fraction for layer 2. Delete `AirSelfDefenceRadiusMeters` (merged into the
range gate) and add `PackageFormUpStandoffMeters` so the posture ring stops doing two jobs.
`AirborneIncomePerAirframe` and `AirborneCeilingMax` are inert at shipped income rates and should be
either retuned or removed.

**What to delete or merge.** One "wait then give up" helper already exists —
`LiftWaitedTooLong` (`CommanderOperationsLogistics.cs:55`), which `FallbackGaveUp` already forwards to.
Point the insertion stall and the strike maximum at it too. One "after a loss, wait" helper exists —
`SortieLossCooldownMinutes` (`CommanderOperationsAirWing.cs:1262`); the insertion, strike-point and FOB
cooldowns should be parameterised calls to it rather than three separate clocks.
