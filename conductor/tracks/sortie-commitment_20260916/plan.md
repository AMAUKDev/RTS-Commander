# Sortie Commitment Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use orchestrator-supaconductor:executing-plans to implement this plan task-by-task.

**Goal:** A sortie knows its own strength, refuses to hold over its own runway, arrives whole, and asks for a number of aircraft the wing could actually produce. Four parts, one track, all approved by the user on 2026-09-16.

**Architecture:** Design in `conductor/tracks/sortie-commitment_20260916/design.md`; the measured evidence in `conductor/designs/2026-09-16-sortie-standdown-investigation.md`. No new service, no new Harmony patch, no new scheduler, no new setting. Every change edits an existing rule in `Operations/CommanderOperationsAir*.cs` in place, on the existing partial class `CommanderOperationsService`.

**Tech stack:** C# / net472 / BepInEx 5 / Harmony. `dotnet build` to check compilation as you go; ONE `.\build-and-install.ps1 -Dev` at the end. The developer's game is NOT running, so installing is free, but there is also no reload to observe: every in-game behaviour is marked "not observed: game not running; verify at next launch".

**Rules that apply:** `.claude/CLAUDE.md` Reuse and Testing rules. Move code, never paraphrase it; one definition, more callers; every threshold and decision table gets a named `Expect` self-check beside the existing ones; constants at class level with a `<summary>` saying what the number means and why. Never run a git command that changes state. Files are UTF-8 with BOM and CRLF (`conductor/tracks.md` is UTF-8 with BOM and LF) — preserve both exactly. `asymmetry-build` and `platoon-reseat` are editing other files in this repo right now: re-read any shared file immediately before editing, make the smallest edit that works, and never rewrite a whole file.

---

## What was found by reading the code first (Reuse rule 1)

Three findings change the shape of the work against the design's own description:

1. **The form-up hold has exactly one user today.** `GoneIn` defaults to `true` on `CommanderAirSortie` (`Operations/CommanderOperationsAirWing.cs:209`) and the ONLY place that sets it false is the deliberate strike sortie (`Operations/CommanderOperationsOffensive.cs:1363`). So although `UpdatePackages` is written generically against `SortieIsPackage`, no objective sortie has ever formed up: `if (sortie.GoneIn) continue;` fires on the review it is created. Widening the rule is therefore not enough — the widened rule has to be the thing that sets `GoneIn`.
2. **`SortieIsPackage` is re-derived every review from numbers that move.** `Wanted` and `CapsWanted` are rebuilt by the sizing every thirty seconds, so a sortie could fall out of "is a package" mid-form-up and its airframes would be re-pointed without anything logging it. Deciding once, at creation, and carrying the answer in `GoneIn` through the reconcile is both simpler and the fix for that latent bug.
3. **`CountFightersUp` counts `Caps` only**, while the design's Part 1 asks for `Caps` + `Cas`. The lift launch gate and the "escort N of M up" line read `CountFightersUp` and must keep counting escorts only. Resolved by extracting the list-level counter as the one definition and leaving `CountFightersUp` as its named reader (Reuse rule 5), not by widening `CountFightersUp` under its existing callers.

## Decisions this plan takes, to be reported

- **In-contact exemption (Part 3).** A sortie whose objective is in GROUND contact never forms up, and a sortie already forming goes in the moment its objective comes into ground contact. Hostile aircraft tracked nearby do NOT exempt it. Reasoning: `SortieIsInContact` is true whenever any hostile aircraft is tracked in the ring, so exempting on it would turn Part 3 off in precisely the fights it exists for — and the investigation's 96 `its fighters are gone` sorties are what arriving one at a time into hostile air costs. Ground contact is different in kind: a platoon under fire cannot wait three minutes, and `RetaskSourceRank` already ranks ground contact above cover. Both directions get a self-check.
- **The fallback call gets a cap, not a headroom clamp (Part 4).** `StrikeEscortWanted` clamps by room under the airborne ceiling because an escort that cannot be bought is not worth wanting. Applying the same clamp to a patrol would be wrong: sortie demand also drives the RETASK, which moves aircraft that are already airborne and already under the ceiling, so a headroom clamp would stop a contested objective drawing fighters off quiet ones exactly when the sky is full. The fallback call is capped by a per-sortie constant instead.
- **A quiet objective is unchanged.** Today a quiet objective asks for one fighter and no ground-attack airframe (`CapWanted(0)` = 1, `CasWanted(0)` = 0). The design's prose says "a quiet objective draws a pair"; the user's instruction says not to change what a quiet objective asks for. The current value is kept as the floor and this is stated in the report.

---

## Tasks

### Task 1 [x]: One definition of "aircraft this sortie has up"
**Files:** `Operations/CommanderOperationsAirPosture.cs` (`CountFightersUp`, ~:215).
Extract `internal static int CountFixedWingUp(List<Aircraft> bound)` as the one definition (the body of the existing loop, MOVED not retyped). `CountFightersUp(CommanderAirSortie?)` becomes its named reader over `sortie.Caps` and keeps its existing summary and every existing caller unchanged. Add the pure `internal static int SortieStrength(int fightersUp, int strikeUp)` — the sortie's own aircraft, nothing about where they are and nothing about whether it is holding. Self-checks in `CheckAirPosture`: strength is the sum; nothing up is no strength; a negative read never subtracts.
Verify: `dotnet build` clean.

### Task 2 [x]: The strength a sortie decides on is what is assigned to it
**Files:** `Operations/CommanderOperationsAirPosture.cs` (`PresentRadiusMeters` ~:247, `FighterPresent` ~:316, `FighterPresentFor` ~:326, `CountFightersPresent` ~:342, `FighterCountsPresent` ~:369, `WatchAirPosture` ~:399 and ~:413, `CheckAirPosture`).
Delete all five members. `WatchAirPosture` reads `int ours = SortieStrength(CountFightersUp(sortie), CountFixedWingUp(sortie.Cas));` ONCE at the top and uses that same number for the "nothing airborne" guard and for both decisions — one number, one definition, and the guard's line becomes "its aircraft are gone; the hold ends". Delete the five self-checks that drove the removed rules. Add, in `CheckAirPosture`:
- "a sortie's strength is the same whether or not it is holding" — build a `CommanderAirSortie`, count it with `HoldReason = None` and again with `HoldReason = Outnumbered` and a far `FallbackPoint`; the two must be equal. This is the regression that must never return.
- the no-flap sweep: for `ours` 0–20 against `hostiles` 0–20, a sortie that falls back at those numbers must never also re-engage at them, at the shipped margins. This pins at the rule level that the two decisions cannot both be true of one reading.
Verify: `dotnet build` clean; no reference to a deleted member remains (`grep`).

### Task 3 [x]: One stand-down door, two reasons
**Files:** `Operations/CommanderOperationsAirPosture.cs` (`GiveUpSortie` ~:738, `ClearFallbackPoint` ~:624).
MOVE the body of `GiveUpSortie` into `private void StandDownSortie(FactionHQ hq, OperationsState state, CommanderAirSortie sortie)` — end the hold, take the loss cooldown, close the strike package if this was it, release `Cas` then `Caps` through `ReleaseBoundAirframes`. `GiveUpSortie` becomes its five-minute caller: it writes `not reinforced in N min; stands down` and calls the door. Give `ClearFallbackPoint` an `out bool collapsedOnBase` set when the clear walk returned zero along the leg — the point IS the airbase and there is no clear ground to hold.
Verify: `dotnet build` clean; behaviour unchanged so far.

### Task 4 [x]: A hold point on our own runway means the sortie is not viable
**Files:** `Operations/CommanderOperationsAirPosture.cs` (`BeginFallback` ~:593, `BeginBeltHold` ~:526, `WatchAirPosture` ~:470, `WatchBeltHold` ~:571).
Every one of the four places that re-finds the hold point checks the new flag. When it is set, the sortie does not hold: it calls `StandDownSortie` and logs `{Label}: no clear ground to hold — its fighters join the home patrol.` The home patrol has had no minimum since DECISION-038 (`LendableHomeCap`), so the released fighters are immediately re-assignable. `BeginFallback` and `BeginBeltHold` become instance methods and take `OperationsState` so they can reach the door (they are called only from `WatchAirPosture`, which has both).
Verify: `dotnet build` clean. In-game: not observed, game not running.

### Task 5 [x]: One rule for "does this sortie gather before it goes in"
**Files:** `Operations/CommanderOperationsAirWing.cs` (`SortieIsPackage` ~:576 and its self-checks ~:1855).
Widen `SortieIsPackage` in place into `internal static bool SortieFormsUp(CommanderSortieKind kind, int casWanted, int capWanted, bool groundContact, bool aradGoneIn)` — one definition, more callers, NOT a copy. False for the radar aircraft by kind; false for a suppression sortie that has gone in; false while the objective is in ground contact; false for a single-airframe sortie, which has nobody to form up with; true otherwise, for a fighter patrol, a ground-attack sortie and an escort alike. Rewrite the five existing self-checks onto the new name and add: a CAP-only pair of fighters forms up (the widening, which used to be false); a lone fighter does not; a ground-attack sortie with an escort does; the radar aircraft never does; a suppression sortie that has gone in never does; a sortie in ground contact never does; the same sortie NOT in ground contact does.
Verify: `dotnet build` clean.

### Task 6 [x]: The decision is taken once, when the sortie opens
**Files:** `Operations/CommanderOperationsAirAwacs.cs` (`AddDemand` ~:519, `ReconcileSorties` ~:631), `Operations/CommanderOperationsAirPackages.cs` (`SortieHoldsAtFormUp` ~:807).
`AddDemand` sets `GoneIn = !SortieFormsUp(Objective, cas, cap, groundContact, aradGoneIn: false)`, where `groundContact` is `immediate || (mission is an Attack whose platoons have launched)` — the two facts already computed there for `SortieIsInContact`. Every other creator keeps the field default and therefore never forms up, which is exactly the exemption list: the radar watch, suppression, lift cover and platoon contact cover all set `NoCapWait = true` today. `SortieHoldsAtFormUp` becomes `sortie.HasFormUp && !sortie.GoneIn` — the stored decision, not a re-derivation. In the reconcile, `wanted.GoneIn = live.GoneIn || wanted.GoneIn;` so a forming sortie goes in the moment this review's decision says it should not be forming, which is what ends the hold when the objective comes into ground contact. Comment says so.
Verify: `dotnet build` clean. In-game: not observed, game not running.

### Task 7 [x]: The form-up counts both elements
**Files:** `Operations/CommanderOperationsAirPackages.cs` (`UpdatePackages` ~:299), `Operations/CommanderOperationsAirIdle.cs` (`CountAtFormUp` ~:230), `Operations/CommanderOperationsAirMarkers.cs` (~:838, ~:879).
`UpdatePackages`'s first gate reads `sortie.GoneIn` rather than re-deriving the rule. Add `CountAtFormUp(CommanderAirSortie)` beside the list overload as the one definition of "aircraft of this sortie that have arrived", summing both elements, and have the forming log line and the marker read it, so a fighter patrol does not report `forming 0/0`. The go-in test itself is untouched: `PackageGoesIn` already takes both elements and both wanted counts.
Verify: `dotnet build` clean.

### Task 8 [x]: One sizing envelope, four callers
**Files:** `Operations/CommanderOperationsAirWing.cs` (`CasWanted` ~:500, `CapWanted` ~:528), `Operations/CommanderOperationsAirPackages.cs` (`StrikeEscortWanted` ~:94).
Add `internal static int SortieElementWanted(int floor, int grown, int cap)` — never below the element's own floor, never below what its own growth term asked for, never above its own cap, and the cap never cuts below the floor; a cap of zero or less means the element has none. Generalised from `StrikeEscortWanted` (Reuse rule 5) and retrofitted behaviour-neutrally: the escort keeps its ceiling clamp on top, the patrol ladder passes baseline/baseline-plus-tracked/`CapPerObjectiveCap`, the ground-attack ladder passes zero/ladder/`CasPerObjectiveCap`. Self-checks at every boundary including the floor-beats-cap case, plus a loop pinning that the retrofitted `CapWanted` and `CasWanted` still answer exactly what they answer today for every input 0–12.
Verify: `dotnet build` clean; all existing escort self-checks still pass unchanged.

### Task 9 [x]: A sortie in trouble asks for a number the wing could produce
**Files:** `Operations/CommanderOperationsAirPosture.cs` (`CallForFighters` ~:672, `CheckAirPosture`).
Class constant `FallbackFightersCap` with a `<summary>` saying what it is and why. `CallForFighters` reads `SortieElementWanted(floor: sortie.CapsWanted, grown: ReinforcementWanted(hostiles, margin), cap: FallbackFightersCap)` — the existing "never lower a demand" becomes the floor, which is why a demand already above the cap is not cut. `ReinforcementWanted` is untouched and stays the growth term. Self-checks at each boundary: below the cap is unchanged; above it is capped; a floor above the cap survives; the cap is above the routine per-objective cap, or a sortie under attack could ask for no more than quiet cover.
Verify: `dotnet build` clean.

### Task 10 [x]: Prove the new checks actually fail
**Files:** a harness under the scratchpad only; no repo file changes.
Build a console harness referencing the built mod, `UnityEngine` and `Assembly-CSharp`, and call the new check blocks alone by reflection — the whole operations self-check cannot run outside the game because `CheckAirSuperiorityRefusal` builds a Unity object. Plant one defect per new gate, build, record which NAMED check fails, restore, confirm the file is byte-identical by sha256. At minimum: the hold-state independence check, the no-flap sweep, the form-up rule's ground-contact exemption, the sizing envelope's floor-beats-cap case, and the fallback cap.
Verify: every planted defect fails a NAMED check and nothing else; every restore is byte-identical.

### Task 11 [x]: CHANGELOG, decision log, track files
**Files:** `CHANGELOG.md` (into `## Unreleased`, re-read immediately before editing — other agents are writing to it), `conductor/decision-log.md` (append DECISION-045), `conductor/tracks.md` (register the row), `conductor/tracks/sortie-commitment_20260916/metadata.json`.
One entry covering all four parts in the file's own voice, naming the log lines a reader will look for and stating the three-minute form-up exposure a patrol now has.

### Task 12 [x]: Build and install
`dotnet build` must end `0 Warning(s)` / `0 Error(s)`, then one `.\build-and-install.ps1 -Dev`. There is no game to reload into, so every in-game item is reported "not observed: game not running; verify at next launch", with the exact log lines to look for at the next launch.

## DAG
1 → 2. 3 → 4. 5 → 6 → 7. 8 → 9. 1–9 → 10 → 11 → 12.
