# Air survival layer — one coherent sense of self-preservation for commanded aircraft

**Track:** air-survival-layer_20260916 · **Date:** 2026-09-16 · **Status:** approved by user (brainstorm, 2026-09-16)
**Audit this design answers:** `conductor/designs/2026-09-16-air-self-preservation-audit.md`
**Builds on:** `air-fallback-posture_20260916` (the outnumbered posture; keep)

## 1. Goal

"Insert some sense of self-preservation into our aircraft" (user, 2026-09-16) as ONE coherent set of
layers, each owning one question, instead of a growing pile of rules — and stop the mod switching
off the self-preservation the game already has.

## 2. Problem, with evidence (from the audit)

- The mod's replacement target chooser (`AirCommand/CommanderAirCommandPilotHooks.cs`,
  `ChooseMissionTarget`) drops the game's bravery-and-threat refusal (`CombatAI.ChooseHQTarget:724`
  in the decompiled game), so a commanded fighter takes any fight the geometry allows.
- `ConstrainMissionDestination` (per attack-mode frame) drags the destination back into the patrol
  box even while the game's pilot is in `BreakOffAttack` or `RetreatStandoff`, undoing its retreat.
- Nothing in the mod reads fuel. When the game's `FuelChecker` lands an aircraft, its mission is not
  marked returning; the sortie still counts it and the deck-recovery sweep skips it.
- "Out of ammo" is decided twice with different rules (`IsWinchester`,
  `CommanderAirCommandService.cs:558`; the chooser's `outOfAmmo` mark in `TryChooseMissionTarget`).
- The attrition brake (`Ai/CommanderEnemyCommanderAttrition.cs`) only stops buying; nothing thins
  what is already up.
- Four "wait then give up" clocks and five "after a loss, wait" clocks share no definition, though
  `LiftWaitedTooLong` and `SortieLossCooldownMinutes` already exist to hold them.
- `AirPostureRingMeters` now does two jobs (posture ring and package form-up standoff).

## 3. Decisions taken (user, 2026-09-16)

Both numbers and fuel/ammo/belt in one coherent layer. Survival beats the sortie, never the player's
own Air Command orders. Triggers: outnumbered (existing posture), out of ammo, heavy enemy air
defence ahead with no strike/anti-radar aircraft going in or gone in nearby. Damage-based retreat NOT
chosen (later toggle). Approach A: four layers.

## 4. Design

**Layer 1 — the game's pilot owns the seconds.**
- Pure `TargetIsTooDangerous(opportunity, bravery, hqThreat, range, weaponMaxRange)` beside
  `TargetOutsideSelfDefence`, transcribing the three-clause refusal at `ChooseHQTarget:724`
  (`opportunity * bravery * 2 < 0.35 && hqThreat > opportunity * bravery * 2 && range > maxRange * 2`),
  applied in `ChooseMissionTarget` to the best candidate before it is returned, never to a
  `ForcedTarget`. Self-checks at the three boundaries.
- `ConstrainMissionDestination` returns early while the pilot's `attackMode` is `BreakOffAttack` or
  `RetreatStandoff` (read with `AccessTools.Field(typeof(AIPilotCombatModes), "attackMode")`, the
  reflection pattern already used for `DestinationField`).

**Layer 2 — per-aircraft survival check (new file `AirCommand/CommanderAirCommandSurvival.cs`,
partial class of `CommanderAirCommandService`).** Runs from the existing 2 s mission-prune clock
(`CommanderAirCommandService.cs:201`, beside `ProcessReturningMissions`) over every AI mission that is
not `Returning`, not player-owned (`aircraft.Player == null` and the mission is commander-issued —
the same test `IsTaskableAircraft` makes), and not already landing:
- Out of ammo: ONE definition — `IsWinchester` moves into this file and the chooser's `outOfAmmo`
  mark calls the same predicate (Reuse rule 4). Route points still to fly are respected as today.
- Fuel: `aircraft.GetFuelLevel() <= AirSurvivalFuelFraction` → return; and if the pilot is already in
  `AIPilotLandingState` (the game's own fuel landing) while the mission is not `Returning`, mark it
  returning so the sortie frees the slot and the recovery sweep sees it.
- Action is always `RequestReturnToBase(aircraft)` (`:389`), the one door that already handles rotary
  pads, recovery and relaunch. One log line per reason: `<type> goes home: out of ammo` /
  `… : fuel at 22 %` / `… : the pilot is landing for fuel; the sortie frees its slot`.

**Layer 3 — the sortie posture owns the formation (`Operations/CommanderOperationsAirPosture.cs`).**
Add a second hold reason, "belt ahead": a BELT WORTH SUPPRESSING within `AirBeltHoldRadiusMeters`
of the objective and no strike or anti-radar aircraft of ours gone in within that ring
(`state.AirSorties` with `Kind == Arad` and `GoneIn`, or a strike `GoneIn`) → the sortie holds at its
clear fallback/form-up point with the same stamps the outnumbered posture uses, logs once
`<sortie>: holds — air defence over the objective (N sites) and no sweep in`, and raises `AradWanted`
through the existing `HardArad` path so a sweep is bought. Clears when a sweep is in or the belt is
gone; the outnumbered give-up clock applies. Self-defence: delete `AirSelfDefenceRadiusMeters`; the
leash is the closing-hostile rule (`SelfDefenceEngages`) plus the existing air-superiority range gate.
Split the form-up standoff off the posture ring: `PackageFormUpStandoffMeters` (20 000).

*Amended 2026-09-16 (user decision: "only hold for a belt worth sweeping").* "A belt worth
suppressing" is ONE pure rule, `BeltWorthSuppressing(clusteredLaunchers, clusterMinimum)` in
`Operations/CommanderOperationsAirArad.cs`, read by both `AradWanted` (which sizes the sweep) and
`BeltHoldsSortie` (which decides the hold), so the two can never disagree about what a belt is. The
first version held on ONE tracked launcher in a ring while a sweep was only ever opened for a cluster
of `AradClusterMinimum` (3): the running log showed twelve holds — five over one site, seven over two
— none of which could clear, and two sorties stood down after five minutes for nothing. The hold now
counts CLUSTERED launchers, using the same `CollectTrackedAirDefence` and `BuildAirDefenceClusters`
pass the suppression demand makes, run once per commander at the top of the watch and read by every
sortie in it (`RefreshPostureBelts` / `LargestBeltNear`) — cheaper than the per-sortie ring count it
replaces. Below the threshold the sortie flies on and the aircraft's own threat dodging and the
restored bravery refusal keep it alive.

**Layer 4 — the review owns the wallet: escalate, never hold (revised, user decision 2026-09-16).**
The original shape of this layer — thin the sky when the brake fires — is GONE, because the brake it
hung on is gone. The brake fed itself and froze both commanders: halving the airborne ceiling
collapsed the air budget (sized as room-under-the-ceiling × cheapest wanted airframe) to a single
airframe, with one airframe's budget the best affordable pick is always the cheapest one, so the
escalation could never fire, the hold was re-applied every review and nothing was ever quiet for five
minutes. The observable was `air saved 174 (cap 174)` against a balance of 2,376 with a dozen open
requests, and one fighter bought every few minutes while the money piled up.

What the layer is now: a bleeding side buys BETTER, never nothing. The ledger still watches losses
per side, still records the type most often lost, and still makes the buy take the best affordable
airframe rather than the cheapest while that side is bleeding (`AttritionEscalates`,
`AttritionSideRating`, `MostLostType`) — the user's original 2026-09-15 request, unchanged. When the
best affordable airframe is no better than the type that is dying, that is now a fact the commander
reports once per window and buys through (`already flying the best it can afford (<type>) — buying
on`). Removed with the hold: `record.Held`, `AttritionHoldsBuy`, `AttritionQuietMinutes`, the quiet
release, `AttritionCeiling` and the ceiling halving inside `EffectiveAirborneCeiling`, and the
home-CAP baseline exemption that only existed to work around the hold. Nothing thins the sky; there
is no longer a transition to hang it on.

**Clocks merged (behaviour-neutral).** Insertion stall (`OperationsInsertionStallTimeoutSeconds`) and
the strike maximum call `LiftWaitedTooLong`. Insertion, strike-point and FOB loss cooldowns call
`SortieLossCooldownMinutes` parameterised. Existing self-checks move with them.

## 5. Settings

| Add | Default | Remove |
|---|---|---|
| `AirSurvivalFuelFraction` | 0.25 | `AirSelfDefenceRadiusMeters` |
| `AirBeltHoldRadiusMeters` | 20 000 | |
| `PackageFormUpStandoffMeters` | 20 000 | |

## 6. Acceptance criteria

1. Self-checks: bravery refusal boundaries; fuel fraction 0 = off, 0.25 boundary; winchester one
   definition; belt-hold pure rule (belt + no sweep → hold; belt + sweep in → no hold; no belt → no
   hold); clocks merged with their old cases still passing; `self-check FAILED` absent at load.
2. In game: a fighter with no missiles left logs `goes home: out of ammo` and lands; a fighter under
   25 % fuel logs `goes home: fuel at N %`; a sortie over a tracked belt with no sweep logs the hold
   line and its aircraft hold at the fallback/form-up point; after an anti-radar sortie goes in, the
   sortie logs `sweep in; goes in`.
3. A commanded fighter offered a fight the bravery rule refuses does not fly at it (no `tasks … on
   CAP` line followed by a loss within the minute against a superior target is the observable).
4. Build 0/0; hot reload clean; no exceptions.

## 7. Files in scope

New: `AirCommand/CommanderAirCommandSurvival.cs`. Modified: `AirCommand/CommanderAirCommandPilotHooks.cs`,
`AirCommand/CommanderAirCommandService.cs`, `AirCommand/CommanderAirCommandPatches.cs`,
`Operations/CommanderOperationsAirPosture.cs`, `Operations/CommanderOperationsAirPackages.cs`,
`Operations/CommanderOperationsAirArad.cs`, `Operations/CommanderOperationsInsertion.cs`,
`Operations/CommanderOperationsAirIdle.cs`, `Operations/CommanderOperationsFob.cs`,
`Ai/CommanderEnemyCommanderAttrition.cs`, `Core/CommanderSettings.cs`, `CHANGELOG.md`.

## 8. Out of scope

Damage-based retreat; player-tasked aircraft; helicopter posture; retuning the airborne ceiling
income constants (flagged by the audit as inert — separate decision).
