# Air Ceiling Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use orchestrator-supaconductor:executing-plans to implement this plan task-by-task.

**Goal:** Cut live aircraft from about 66 to about 36 by retuning the air ceiling that already
exists, taking transports out of its count, and reserving a block of it that standing patrols may
not touch.

**Architecture:** No new service, no new Harmony patch, no new scheduler. The income-scaled ceiling
at `Operations/CommanderOperationsAirRadarWatch.cs:1089` and its gate at
`Ai/CommanderEnemyCommanderAirBuy.cs:176` stay exactly where they are; this track retunes their
defaults, narrows what the count counts, and adds ONE pure predicate beside the ground ceiling's
plus one second refusal for standing patrols only.

**Tech Stack:** BepInEx 5 plugin, `net472`, Unity, Harmony. No unit-test project — the automated
checks are `SelfCheck()` methods run at plugin load, plus an offline reflection harness for pure
rules. Everything else is verified in the running game.

---

## Conventions for this plan, which override the generic skill template

- **There is no `pytest` and no test project.** "Write the failing test" means "add the named case to
  the relevant `SelfCheck` block and watch that NAMED case fail". Only pure rules get one; wiring is
  proved in the running game (CLAUDE.md testing rule 4 and 5).
- **Do not commit.** The developer commits when they ask for it (CLAUDE.md). Tasks end at a clean
  build, not at a commit.
- **Build with `.\build-and-install.ps1 -Dev`.** Close nothing; the developer's game reloads on F6.
  Never leave a copy in both `BepInEx\plugins\` and `BepInEx\scripts\`.
- **Read before writing.** Each task names the code to read first (Reuse rule 6).

---

### Task 1: Retune the ceiling's two defaults, with key renames

**Files:**
- Modify: `Core/CommanderSettings.cs:390` and `:397`

**Read first:** the comment block at `Core/CommanderSettings.cs:379-397`. It records why the floor
went 20 to 30 on 2026-09-14 and why the maximum is 60. Both reasons are now outdated by measurement
and the new comment must say so rather than deleting the history.

**Step 1: Change the two defaults and rename both keys.** The rename is the repo's standing
convention for reissuing a default BepInEx would otherwise never deliver to an existing config file
(the same move `Points/GarrisonPerPoint` made).

```csharp
    // Aircraft a commander's faction may have in the world at once. Was 20, then 30 (user,
    // 2026-09-14: "raise that ceiling to 30+"), on the belief that aircraft were cheap: the
    // frame-rate investigation of 2026-09-17 measured 5 to 7 live aircraft and wrote air out of
    // scope on that basis. That belief did not survive measurement. On 2026-09-18 the health line
    // read 66 live aircraft with both commanders sitting on this FLOOR, not on the income scaling
    // and not on the maximum below — so this number, not the money, was what sized the wing.
    // Sixteen, with the maximum at 24, puts the map near 36 aircraft (air-ceiling_20260918 §4).
    // Key renamed with the retune so BepInEx delivers the new default to an existing config.
    internal static int AirborneCeiling { get => Get("Operations", "AirFloorPerCommander", 16); set => Set("Operations", "AirFloorPerCommander", value); }
```

```csharp
    internal static int AirborneCeilingMax { get => Get("Operations", "AirCeilingMax", 24); set => Set("Operations", "AirCeilingMax", value); }
```

`AirborneIncomePerAirframe` is NOT touched — 15 a minute stays, so a rich commander still climbs from
16 toward 24.

**Step 2: Update both keys in the warm-up list.** They are already there at `:1077` and `:1079` as
`_ = AirborneCeiling;` and `_ = AirborneCeilingMax;` — the property names are unchanged, so no edit
is needed. Confirm by reading, do not edit blindly.

**Step 3: Build.** `.\build-and-install.ps1 -Dev`. Expected: 0 warnings, 0 errors.

---

### Task 2: Add the reserved-block setting

**Files:**
- Modify: `Core/CommanderSettings.cs` (beside `GroundUnitCeiling` at `:438`)

**Read first:** `GroundUnitCeiling`'s `<summary>` at `:432-438` — the shape every new setting in this
area follows, and the reason a ceiling beats a spending limit.

**Step 1: Add the setting with a `<summary>` saying what the number means and why it is that value**
(CLAUDE.md: constants carry their reasoning).

```csharp
    /// <summary>
    /// How many slots under the air ceiling a standing patrol may NOT take, so that transport
    /// escorts, strike packages, the radar aeroplane, anti-radiation sorties and air support over a
    /// ground fight always have room (air-ceiling_20260918 §4 decision C).
    /// <para>
    /// Six because that is two escorted lifts of two fighters each plus a two-aeroplane package, the
    /// largest set of protected work the commander has been observed running at once. The reason it
    /// is needed at all: one commander's log of 2026-09-18 carried 33 standing air requests and 27
    /// of them were patrols, so without a reservation the patrols take the whole sky and every lift
    /// holds at its form-up point waiting for an escort that will never be bought.
    /// </para>
    /// <para>Zero switches the reservation off and puts patrols on the same line as everything else,
    /// the convention every other rule in the mod uses.</para>
    /// </summary>
    internal static int AirPatrolReserve { get => Get("Operations", "AirPatrolReserve", 6); set => Set("Operations", "AirPatrolReserve", value); }
```

**Step 2: Touch it in the warm-up list** beside `_ = GroundUnitCeiling;` (`Core/CommanderSettings.cs:1083`):

```csharp
        _ = AirPatrolReserve;
```

**Step 3: Build.** Expected: 0 warnings, 0 errors.

---

### Task 3: The pure predicate, failing check first

**Files:**
- Modify: `Operations/CommanderOperationsUnitEconomy.cs` (beside `GroundBuyAllowed` at `:76`, and
  its check block `CheckUnitEconomy` at `:606`)

**Read first:** `GroundBuyAllowed` at `:76` and its eight named cases at `:609-616`. The new
predicate is written to match it exactly — inclusive at the ceiling, "zero never binds", tested
against the LIVE count so a loss opens exactly one replacement.

**Step 1: Write the failing checks.** Add to `CheckUnitEconomy(List<string> failures)`, after the
ground ceiling's cases:

```csharp
        // The air ceiling's two lines (air-ceiling_20260918 §4 decision C). Everything protected
        // buys up to the ceiling; a standing patrol stops a reserved block short of it.
        Expect(failures, "a commander under the air ceiling may buy a protected airframe", AirBuyAllowed(15, 16, 6, false), true);
        Expect(failures, "a commander exactly at the air ceiling may not buy a protected airframe", AirBuyAllowed(16, 16, 6, false), false);
        Expect(failures, "a loss at the air ceiling opens exactly one replacement", AirBuyAllowed(15, 16, 6, false), true);
        Expect(failures, "a standing patrol stops at the reserved line", AirBuyAllowed(10, 16, 6, true), false);
        Expect(failures, "a standing patrol one below the reserved line may still buy", AirBuyAllowed(9, 16, 6, true), true);
        Expect(failures, "an escort may still buy where a standing patrol may not", AirBuyAllowed(10, 16, 6, false), true);
        Expect(failures, "an air ceiling of zero never binds", AirBuyAllowed(500, 0, 6, true), true);
        Expect(failures, "a reserve of zero puts patrols on the same line as everything else", AirBuyAllowed(15, 16, 0, true), true);
        Expect(failures, "a reserve at the ceiling grounds every standing patrol", AirBuyAllowed(0, 16, 16, true), false);
```

**Step 2: Run it to make sure it fails.** The build will not compile — `AirBuyAllowed` does not
exist. That is the failing state for a repo with no test runner: the check cannot pass because the
rule it names is not there.

Run: `.\build-and-install.ps1 -Dev`
Expected: `error CS0103: The name 'AirBuyAllowed' does not exist in the current context`.

**Step 3: Write the minimal implementation**, immediately after `GroundBuyAllowed(int, int)`:

```csharp
    /// <summary>
    /// Whether one more aircraft may be bought, pure — the air mirror of
    /// <see cref="GroundBuyAllowed(int, int)"/>, and deliberately the same shape: inclusive at the
    /// ceiling, tested against the LIVE count so a loss opens exactly one replacement, and a
    /// non-positive ceiling is off.
    /// <para>
    /// The one thing it adds is a SECOND line. A standing patrol — fighters circling a point or a
    /// platoon because hostile aircraft were seen near it, and nothing else — may buy only while the
    /// faction is <paramref name="patrolReserve"/> aircraft below the ceiling. Everything else, which
    /// is transport escorts, strike packages, the radar aeroplane, anti-radiation sorties, air
    /// support over a ground fight and home defence, buys right up to it. Without the second line the
    /// twenty-seven patrol requests one commander carried on 2026-09-18 would take every slot and
    /// every lift would hold at its form-up point waiting for an escort nobody could buy — the same
    /// outage as a hard stop, reached by a different road.
    /// </para>
    /// <para>A reserve at or above the ceiling grounds standing patrols entirely, which is a
    /// legitimate setting and not an error; the clamp is there so it cannot go negative.</para>
    /// </summary>
    internal static bool AirBuyAllowed(int liveAircraft, int ceiling, int patrolReserve, bool standingPatrol)
    {
        if (ceiling <= 0)
        {
            return true;
        }

        int line = standingPatrol ? ceiling - Mathf.Max(0, patrolReserve) : ceiling;
        return liveAircraft < Mathf.Max(0, line);
    }
```

**Step 4: Build and run the checks.** `.\build-and-install.ps1 -Dev` (0 warnings, 0 errors), then run
the offline harness from Task 11. Expected: 0 failures.

---

### Task 4: Take transports out of the count

**Files:**
- Modify: `Ai/CommanderEnemyCommanderAirTasking.cs:317` (`CountAirborne`)

**Read first:** `CountAirborne`'s `<summary>` at `:313-316`. It states that the ceiling and the count
must be the same pair the buy loop reads, or a package would be sized against a different sky (Reuse
rule 4). That is why this change is made HERE and not by subtracting transports at the buy: the
escort sizing at `Operations/CommanderOperationsAirIdle.cs:433` and
`Operations/CommanderOperationsAirPlatoonCap.cs:288` must see the same change, and they do because
they call this method.

Also read `GetAirRole` at `Ai/CommanderEnemyCommanderAir.cs:175`. It is the mod's ONE definition of
"this aircraft is a transport" and must not be paraphrased.

**Step 1: Add the memo and the test.**

```csharp
    /// <summary>
    /// Whether one aircraft definition is a transport, memoised. <see cref="GetAirRole"/> is the
    /// mod's one definition of the question (Reuse rule 4) but it reaches into the prefab through
    /// <c>CommanderAirCommandService.HasPlanePilot</c>, which is a Unity component lookup; this is
    /// asked once per live aircraft per air buy and again for every escort sizing, so the answer is
    /// kept. Vehicle and aircraft definitions are shared assets that live for the process, so the
    /// memo needs no sweep and cannot go stale.
    /// </summary>
    private static readonly Dictionary<AircraftDefinition, bool> transportDefinitions = new();

    /// <summary>The memoised transport test, over a live unit.</summary>
    private static bool IsTransportAircraft(Unit unit)
    {
        if (unit.definition is not AircraftDefinition definition)
        {
            return false;
        }

        if (!transportDefinitions.TryGetValue(definition, out bool transport))
        {
            transport = GetAirRole(definition) == AirRole.Transport;
            transportDefinitions[definition] = transport;
        }

        return transport;
    }
```

**Step 2: Narrow the count and say why in the summary.**

```csharp
    /// <summary>Aircraft this faction currently has in the world, pilots and AI alike, EXCEPT its
    /// transports. Internal (one-word widening): the strike package's escort is capped by the room
    /// left under the airborne ceiling, and the ceiling and the count have to be the same pair the
    /// buy loop reads or the package would be sized against a different sky (Reuse rule 4).
    /// <para>
    /// Transports came out on 2026-09-18 by user decision (air-ceiling_20260918 §4 decision B): a
    /// lift must never be blocked by a full sky. It still may not fly into contested air without its
    /// escort — that gate is <c>Operations/CommanderOperationsFob.cs:2320</c> and is untouched — so
    /// what this buys is that the LIFT is free, not that it is unprotected.
    /// </para></summary>
    internal static int CountAirborne(FactionHQ hq)
    {
        if (hq.factionUnits == null)
        {
            return 0;
        }

        int count = 0;
        foreach (PersistentID id in hq.factionUnits)
        {
            if (id.TryGetUnit(out Unit unit) && unit is Aircraft && !unit.disabled && !IsTransportAircraft(unit))
            {
                count++;
            }
        }

        return count;
    }
```

**Step 3: Confirm `System.Collections.Generic` is imported** at the top of the file; add it if not.

**Step 4: Build.** Expected: 0 warnings, 0 errors.

**Note for the reviewer:** this is wiring and asset data, not arithmetic, so it gets no self-check.
It is proved in the running game by Task 13's transport test.

---

### Task 5: The live wrapper the buy calls

**Files:**
- Modify: `Operations/CommanderOperationsUnitEconomy.cs` (beside `GroundBuyAllowed(FactionHQ)` at
  `:202`)

**Read first:** `GroundBuyAllowed(FactionHQ hq)` at `:202` — the shape of a live wrapper in this
file, and its summary's habit of naming every caller.

**Step 1: Add the wrapper.**

```csharp
    /// <summary>
    /// The air ceiling as the buyer asks it: true when this faction may buy another aircraft for the
    /// work described by <paramref name="standingPatrol"/>. One definition, read by the AI
    /// commander's air buy (<c>Ai/CommanderEnemyCommanderAirBuy.cs</c>) — the one path by which a
    /// COMMANDER adds an aircraft to the world. The ceiling it reads is the income-scaled one
    /// (<see cref="EffectiveAirborneCeiling"/>), not a second number.
    /// </summary>
    internal static bool AirBuyAllowed(FactionHQ hq, bool standingPatrol)
    {
        return AirBuyAllowed(
            CommanderEnemyCommanderService.CountAirborne(hq),
            EffectiveAirborneCeiling(hq),
            CommanderSettings.AirPatrolReserve,
            standingPatrol);
    }
```

**Step 2: Build.** Expected: 0 warnings, 0 errors.

---

### Task 6: Mark a standing patrol as one

**Files:**
- Modify: `Operations/CommanderOperationsAirWing.cs:186` (the sortie record)
- Modify: `Operations/CommanderOperationsAirPlatoonCap.cs:487` (`AddCapDemand`)

**Read first:** all three sites that create a `CommanderSortieKind.Cap` sortie, because the whole
point of this task is that the kind alone cannot tell them apart:

- `Operations/CommanderOperationsAirPlatoonCap.cs:286` `AddLiftCoverDemand` — a transport's escort.
  Leave it false.
- `Operations/CommanderOperationsAirPlatoonCap.cs:461` `AddEscortDemand` — a platoon's escort. Leave
  it false.
- `Operations/CommanderOperationsAirPlatoonCap.cs:487` `AddCapDemand` — fighters over a point or a
  platoon because hostile aircraft were tracked near it, called from `:41` and `:60`. This one, and
  only this one, is true.

**Step 1: Add the field** next to `Kind` on `CommanderAirSortie`:

```csharp
        /// <summary>
        /// True only for fighters circling a point or a platoon because hostile aircraft were
        /// tracked near it, with nothing else making it an objective — what
        /// <c>AddCapDemand</c> posts. These, and only these, are rationed by
        /// <c>CommanderSettings.AirPatrolReserve</c> (air-ceiling_20260918 §4 decisions C and D).
        /// <para>
        /// A marker rather than a test on <see cref="Kind"/>, because a lift's escort, a platoon's
        /// escort and a standing patrol are ALL <see cref="CommanderSortieKind.Cap"/> and were told
        /// apart only by their printed label. Comparing labels to decide what a commander may buy is
        /// exactly the kind of rule that breaks the day a label is reworded.
        /// </para>
        /// </summary>
        internal bool IsStandingPatrol;
```

**Step 2: Set it in `AddCapDemand` only**, in the `demand.Add(new CommanderAirSortie { ... })` at
`:501`:

```csharp
            Kind = CommanderSortieKind.Cap,
            IsStandingPatrol = true,
```

**Step 3: Confirm the other two sites were not touched.** Run:
`git diff Operations/CommanderOperationsAirPlatoonCap.cs`
Expected: exactly one added line in the file, inside `AddCapDemand`.

**Step 4: Build.** Expected: 0 warnings, 0 errors.

---

### Task 7: The second refusal at the reserved line

**Files:**
- Modify: `Ai/CommanderEnemyCommanderAirBuy.cs`, after the demand is resolved (currently `:271`,
  just after `state.AirDenialContext = previousBuy;`)

**Read first:** `BuyAirframeForTurn` from `:172` to `:280`. Three things there matter and must not be
disturbed:

1. The absolute ceiling gate at `:176` runs BEFORE `TryGetAirDemand` and returns without advancing
   the CAP/CAS alternation. Leave it exactly as it is; it still refuses everything at the ceiling.
2. Home defence returns at `:253`, which is BEFORE where the new gate goes. Home defence is therefore
   never squeezed, which is decision C, and it falls out of the existing order for free — do not add
   a special case for it.
3. `TryGetAirDemand` is called with `advanceTurn: true` at `:264`. The new gate sits after it, so a
   refused patrol DOES advance the alternation. That is deliberate and worth its comment: it means a
   patrol refused this review hands the turn to the other half of the wing rather than blocking it.

**Step 1: Add the gate**, immediately after `state.AirDenialContext = previousBuy;`:

```csharp
        // The reserved block (air-ceiling_20260918 §4 decision C). The absolute ceiling above has
        // already been cleared; this is the SECOND, lower line that standing patrols alone stop at,
        // so escorts, packages, the radar aeroplane and air support over a ground fight keep their
        // slots when twenty-odd patrol requests are open. Home defence returned above and never
        // reaches here. The turn has already been advanced by the demand read, on purpose: a patrol
        // refused this review hands the alternation to the other half of the wing instead of
        // holding it.
        if (demandSortie?.IsStandingPatrol == true
            && !CommanderOperationsService.AirBuyAllowed(hq, standingPatrol: true))
        {
            ReportAirDenial(
                hq,
                state,
                $"a standing patrol stops {CommanderSettings.AirPatrolReserve} aircraft below its "
                    + $"{CommanderOperationsService.EffectiveAirborneCeiling(hq)}-aircraft ceiling, so its "
                    + "escorts, packages and radar aeroplane keep their slots");
            return 0f;
        }
```

**Step 2: Confirm the absolute gate is unchanged.** Run:
`git diff Ai/CommanderEnemyCommanderAirBuy.cs`
Expected: added lines only, nothing removed, and the block at `:176` untouched.

**Step 3: Build.** Expected: 0 warnings, 0 errors.

**Note:** the non-patrol case is deliberately not re-tested here — it would repeat the gate at `:176`
that has already passed on the same review.

---

### Task 8: The slider

**Files:**
- Modify: `UI/CommanderOverlayUiSettings.cs:593` (`OperationsBoxHeight`) and `:637` (beside the
  ground ceiling's slider)

**Read first:** `:588-593`, the comment that derives the box height from its contents, and the
`DrawPointsSlider` calls at `:633-643`. DECISION-059 made this height a named constant because it had
been written out twice and the two had drifted — do not reintroduce a literal.

**Step 1: Raise the box by one row.** The comment at `:588` says ten 38 px sliders; it becomes eleven.

```csharp
    private const float OperationsBoxHeight = 494f;
```

Update the comment above it to say eleven rows, and why the eleventh exists.

**Step 2: Add the slider** immediately after the ground ceiling's, so the two ceilings read together:

```csharp
        CommanderSettings.AirPatrolReserve = Mathf.RoundToInt(DrawPointsSlider(
            rowY, width, "Air patrol reserve", CommanderSettings.AirPatrolReserve, 0f, 20f, "0", " slots"));
        rowY += 38f;
```

**Step 3: Build.** Expected: 0 warnings, 0 errors.

**Step 4: Eyeball it in game.** The OPERATIONS box must not clip its last row and the STRATEGIC SAVE
box below it must not overlap. This is the one task with no check but the eye.

---

### Task 9: Prove the new checks still fail

**Files:**
- Temporarily modify: `Operations/CommanderOperationsUnitEconomy.cs`

This is CLAUDE.md testing rule 5 and it is not optional: a check that cannot fail is not a check.
Record the file's sha256 first, plant ONE defect at a time, build, run the harness, restore, and
confirm the file is byte-identical before planting the next.

**Step 1: Record the baseline.**

```bash
sha256sum Operations/CommanderOperationsUnitEconomy.cs
cp Operations/CommanderOperationsUnitEconomy.cs /tmp/orig-unit-economy.cs
```

**Step 2: Plant each defect, one at a time.** Each must BUILD CLEAN — that is what proves it was live
compiled code and not a comment.

| Defect | Change | Must fail this NAMED case |
|---|---|---|
| 1 | `liveAircraft < line` becomes `liveAircraft <= line` | `a commander exactly at the air ceiling may not buy a protected airframe` |
| 2 | drop the `standingPatrol ?` branch, always use `ceiling` | `a standing patrol stops at the reserved line` |
| 3 | drop `Mathf.Max(0, line)`, return `liveAircraft < line` | `a reserve at the ceiling grounds every standing patrol` |
| 4 | `ceiling <= 0` becomes `ceiling < 0` | `an air ceiling of zero never binds` |

For each: build (`.\build-and-install.ps1 -Dev`, expect 0 warnings / 0 errors), run the Task 11
harness, read the named failure, then restore and verify:

```bash
cp /tmp/orig-unit-economy.cs Operations/CommanderOperationsUnitEconomy.cs
sha256sum Operations/CommanderOperationsUnitEconomy.cs
```

Expected: the same hash recorded in Step 1, every time.

**Step 3: Record all four results** for the decision-log entry in Task 12 — defect, build outcome,
the named case that failed, and the restored hash.

---

### Task 10: Clean rebuild and install

**Files:** none.

**Step 1:** `.\build-and-install.ps1 -Dev`. Expected: 0 warnings, 0 errors, installed into
`BepInEx\scripts`.

**Step 2: Confirm there is no second copy.**

```powershell
Get-ChildItem "I:\SteamLibrary\steamapps\common\Nuclear Option\BepInEx\plugins\GroundControlRts" -ErrorAction SilentlyContinue
```

Expected: no output.

---

### Task 11: The offline harness

**Files:**
- Create: in the session scratchpad, NOT in the project.

**Read first:** DECISION-059's verification paragraph in `conductor/decision-log.md`. It records that
this harness runs under Windows PowerShell 5.1 (.NET Framework, matching `net472`) with an
`AssemblyResolve` handler onto `NuclearOption_Data\Managed` and `BepInEx\core`, and that
`Quaternion`, `Vector3.RotateTowards` and friends throw outside the player (DECISION-058). This
track's block is free of all of them, so it runs.

**Step 1: Write the harness.** It loads `bin\Release\net472\GroundControlRts.dll`, reflects the
private static `CheckUnitEconomy(List<string>)` on `GroundControlRts.CommanderOperationsService`,
invokes it with a fresh `List<string>`, and prints every entry.

**Step 2: Run it.**

```powershell
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File <scratchpad>\unit-economy-check.ps1
```

Expected on a clean tree: `0 failure(s)`.

---

### Task 12: Changelog and decision log

**Files:**
- Modify: `CHANGELOG.md` (top of `## Unreleased`)
- Modify: `conductor/decision-log.md` (append as DECISION-061)

**Read first:** the unit-economy entry at the top of `## Unreleased` — the register to match. Written
for a product owner: plain words, no identifiers in prose, says what the player will notice and what
it costs.

**Step 1: Changelog.** Cover, in plain words: the sky had grown to about 66 aircraft; the limit that
should have held it was sitting on a floor set back when aircraft were thought to be free; the floor
and maximum come down; transports no longer count against it so a lift is never grounded by a busy
sky, though it still waits for its escort; and a reserved block means circling patrols can never
crowd out escorts, packages, the radar aeroplane or air support over a fight. State the cost: the
commander will refuse its own patrol orders often and say so, until the next piece of work merges
nearby patrol requests into one.

**Step 2: Decision log** as DECISION-061, in the established shape: Date, Track, Problem (with the
health line and the 33-requests-27-patrols count), Decisions A to F from the design with their
reasoning, Impact (every file), Verification (build result, harness result, all four defect plants
with their named cases and the restored hash), and a clearly-headed **NOT OBSERVED IN GAME** section
carrying Task 13 verbatim.

---

### Task 13: In-game verification — the developer flies, the plan says what to look for

**Files:** none.

This is CLAUDE.md testing rule 2: nothing here is done until it has run in the game. Write this out
for the developer, in order.

**Before launching.** Confirm `BepInEx\config\com.groundcontrol.rts.cfg` has `HealthDiagnosticLine =
true` under `[Developer]`. The three renamed or new keys will appear with their new defaults on the
next run; the old `AirborneFloor` and `AirborneCeilingMax` lines can be left in place and ignored.

**At load.** Silence from `Operations self-check` means the nine new cases passed. Any
`Operations self-check FAILED: <name>` line names the one that did not.

**In a match — what should be different.**

1. `aircraft=` on the `Health t=` line settles near **36**, against 66 on 2026-09-18, and `missiles=`
   falls with it. `ground=`, `buildings=` and the pool figures should be unchanged.
2. `frameMsAvg=` should improve. It was 17 to 33 ms; it will not reach the old 8.7 ms and should not
   be expected to.
3. In the COMMANDER LOG, patrol refusals naming the RESERVED line, worded
   `a standing patrol stops 6 aircraft below its N-aircraft ceiling...`. These will be frequent. That
   is the known cost recorded in the design, not a fault.
4. A strike package and the radar aeroplane still get their airframes while those refusals are
   printing. This is the test that the reservation works — if packages are refused too, the marker is
   being set on the wrong sorties.
5. **The transport test.** With the sky at the ceiling, a forward base ordered by air must still
   launch. Watch for the lift holding at its form-up point `waiting for the escort` and then going —
   holding is correct, never going is the regression.

**Regressions to watch for, in likeliest order.** A wing too small to contest the air at all (raise
`Operations/AirFloorPerCommander`); lifts holding for an escort indefinitely, which would mean the
reserve is too small for the number of concurrent lifts (raise `Operations/AirPatrolReserve`); and
points changing hands to enemy air because standing patrols are too rare (lower
`Operations/AirPatrolReserve`, or accept it and bring forward the area-merge track). Each is one
slider in the OPERATIONS box.

---

## Task DAG

Tasks 1, 2, 4 and 6 are independent and may run in any order. Task 3 depends on 2. Task 5 depends on
3 and 4. Task 7 depends on 5 and 6. Task 8 depends on 2. Tasks 9, 10 and 11 depend on 3. Task 12
depends on 9. Task 13 depends on 10.

In practice one executor runs 1 to 13 in order; the dependency list exists so a re-dispatch after a
context exhaustion knows what is safe to resume.

## Out of scope — do not drift into these

- Merging nearby patrol requests into one. That is the next track and is what removes the refusal
  noise this one creates.
- Recalling, landing or selling airborne aircraft.
- Any change to ground vehicles, buildings, control point counts, or the idle reserve.
- The multi-second frame stalls (`frameMsWorst=7875`), which are a separate fault.
