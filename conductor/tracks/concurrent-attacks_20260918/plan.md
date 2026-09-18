# Concurrent Attacks Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use orchestrator-supaconductor:executing-plans to implement this plan task-by-task.

**Goal:** Let a commander run several ground attacks at once, each with its own strike package ahead
of it, drawing platoons from forward bases the enemy is not at — so the player sees combined arms
instead of fighters circling control points.

**Architecture:** No new service, no new Harmony patch, no new scheduler. Three changes to existing
code: the one-attack gate becomes an allowance shaped like the existing forward-base allowance; the
single strike-package FIELD is split so an attack's package lives on the attack and the deliberate
strike keeps the old slot; and an attack may call up a platoon from an unthreatened forward base
through the mod's existing definition of "the enemy is at this point".

**Tech Stack:** BepInEx 5 plugin, `net472`, Unity, Harmony. No unit-test project — automated checks
are `SelfCheck()` methods run at plugin load, plus an offline reflection harness for pure rules.

---

## Conventions for this plan, which override the generic skill template

- **No `pytest`, no test project.** "Write the failing test" means "add the named case to the
  relevant `SelfCheck` block and watch that NAMED case fail". Only pure rules get one; wiring is
  proved in the running game (CLAUDE.md testing rules 4 and 5).
- **Do not commit.** The developer commits when they ask.
- **Build with `.\build-and-install.ps1 -Dev`.** Never leave a copy in both `BepInEx\plugins\` and
  `BepInEx\scripts\`.
- **Read before writing.** Each task names the code to read first (Reuse rule 6).
- **Task 5 is the big one.** It is a refactor of ten call sites. Do not start it before tasks 1-4
  are building clean.

---

### Task 1: The attack allowance, failing check first

**Files:**
- Modify: `Operations/CommanderOperationsOffensive.cs` (beside `CountSparePlatoons` at `:99`)
- Modify: the operations self-check block

**Read first:** `MaxForwardBases` at `Operations/CommanderOperationsFront.cs:2186` and its
`MinForwardBases = 2` at `:2170`. The new rule is written to the SAME shape — a floor, plus a share
of the platoon count, take the larger — so the mod has one way of saying "how many of these am I
allowed" (Reuse rule 4). Read its `<summary>` too: it explains why demand is independent of the
platoon count.

**Step 1: Write the failing checks.** Add to the operations self-check, after the existing cases:

```csharp
        // The attack allowance (concurrent-attacks_20260918 §6). Same shape as MaxForwardBases: a
        // floor, plus a share of the platoon count, whichever is larger.
        Expect(failures, "a small army still gets the floor of attacks", MaxConcurrentAttacks(4, 0.2f, 2), 2);
        Expect(failures, "a ten-platoon army gets one attack per five platoons", MaxConcurrentAttacks(10, 0.2f, 2), 2);
        Expect(failures, "a twenty-platoon army gets four attacks", MaxConcurrentAttacks(20, 0.2f, 2), 4);
        Expect(failures, "an army with no platoons still gets the floor", MaxConcurrentAttacks(0, 0.2f, 2), 2);
        Expect(failures, "a floor of zero means one attack, the old behaviour", MaxConcurrentAttacks(20, 0f, 0), 1);
        Expect(failures, "a negative floor means one attack, the old behaviour", MaxConcurrentAttacks(20, 0.2f, -1), 1);
        Expect(failures, "a share above one is clamped rather than multiplying the army", MaxConcurrentAttacks(10, 5f, 2), 10);
```

**Step 2: Run it to make sure it fails.** `.\build-and-install.ps1 -Dev`
Expected: `error CS0103: The name 'MaxConcurrentAttacks' does not exist in the current context`.

**Step 3: Write the implementation**, immediately after `CountSparePlatoons`:

```csharp
    /// <summary>
    /// How many attacks this commander may have open at once, pure. Deliberately the same shape as
    /// <c>MaxForwardBases</c> (Reuse rule 4): a floor, plus a share of the platoon count, take the
    /// larger — so a small army still pushes somewhere and a large one pushes in proportion.
    /// <para>
    /// A non-positive floor answers ONE, which is the behaviour before this track rather than "no
    /// attacks at all". That is a deliberate departure from the repo's usual "zero switches the rule
    /// off": switching this rule off has to mean the old single attack, because a commander that
    /// never attacks is not a commander.
    /// </para>
    /// <para>
    /// The share is clamped to 0..1 for the same reason <c>MaxForwardBases</c> clamps it — a
    /// mis-typed slider should thin the attacks, never multiply the army.
    /// </para>
    /// </summary>
    internal static int MaxConcurrentAttacks(int platoonCount, float attacksPerPlatoon, int floor)
    {
        if (floor <= 0)
        {
            return 1;
        }

        int share = Mathf.FloorToInt(Mathf.Max(0, platoonCount) * Mathf.Clamp01(attacksPerPlatoon));
        return Mathf.Max(floor, share);
    }
```

**Step 4: Build and run the offline harness.** Expected: 0 warnings, 0 errors, 0 failures.

---

### Task 2: The two settings

**Files:**
- Modify: `Core/CommanderSettings.cs` (beside `OperationsFobShare` at `:358`)

**Read first:** `OperationsFobShare` and `GroundUnitCeiling`'s `<summary>` — the shape a new setting
follows, including saying WHY the number is what it is (CLAUDE.md).

**Step 1: Add both settings.**

```csharp
    /// <summary>
    /// Fewest attacks a commander runs at once (concurrent-attacks_20260918 §6). Two, because one
    /// was what the 2026-09-18 match measured and it produced a mission board of 48 pickets, 11
    /// forward bases and ONE attack — a static picket line with a single push crawling across it.
    /// Zero or less means one attack, the behaviour before that track.
    /// </summary>
    internal static int MaxAttacks { get => Get("Operations", "MaxAttacks", 2); set => Set("Operations", "MaxAttacks", value); }

    /// <summary>
    /// How many more attacks a commander earns per platoon it fields (concurrent-attacks_20260918
    /// §6). 0.2 is one more attack per five platoons, so the ten-platoon commanders of the measured
    /// match get two and a twenty-platoon one gets four. Clamped to 0..1 by the rule that reads it.
    /// </summary>
    internal static float AttacksPerPlatoon { get => Get("Operations", "AttacksPerPlatoon", 0.2f); set => Set("Operations", "AttacksPerPlatoon", value); }
```

**Step 2: Touch both in the warm-up list** beside `_ = OperationsFobShare;`.

**Step 3: Build.** Expected: 0 warnings, 0 errors.

---

### Task 3: Lift the one-attack gate

**Files:**
- Modify: `Operations/CommanderOperationsOffensive.cs:602-608`

**Read first:** `TryOpenAttack` in full (`:600-660`). Note three things that must NOT change: it
resets `state.Pressure` on a successful open; it returns false when no target qualifies; and it calls
`OpenStrikeSortie` at `:652` after adding the mission.

**Step 1: Replace the first-attack refusal with the allowance.**

```csharp
        // The allowance, not the first attack found (concurrent-attacks_20260918 §4 decision A).
        // This gate used to return false the moment ANY attack existed, which is why the 2026-09-18
        // match showed one attack against 48 pickets and 11 forward bases however large the army
        // grew.
        int openAttacks = 0;
        for (int i = 0; i < state.Missions.Count; i++)
        {
            if (state.Missions[i].Kind == CommanderMissionKind.Attack)
            {
                openAttacks++;
            }
        }

        if (openAttacks >= MaxConcurrentAttacks(
                state.Platoons.Count, CommanderSettings.AttacksPerPlatoon, CommanderSettings.MaxAttacks))
        {
            return false;
        }
```

**Step 2: Build.** Expected: 0 warnings, 0 errors.

**Step 3: Confirm nothing else was touched.** `git diff Operations/CommanderOperationsOffensive.cs`
Expected: the allowance and Task 1's rule only.

**Note:** at this point the commander opens several attacks but only the FIRST gets a package, because
`OpenStrikeSortie` still refuses when `state.StrikeSortie` is set. Task 5 is what fixes that. Do not
verify in game between here and Task 5 — the intermediate state is worse than either end.

---

### Task 4: Give an attack mission its own package slot

**Files:**
- Modify: `Operations/CommanderPlatoon.cs` (the `CommanderOperationsMission` record, beside `Truck`)

**Read first:** the `Truck` and `PicketMembers` fields and their summaries — both are "an addition
beyond the plan's original field list", and both say why the mission is the right owner.

**Step 1: Add the field.**

```csharp
    /// <summary>
    /// The strike package flying ahead of this attack, or null while it has none. Only ever
    /// populated for <see cref="CommanderMissionKind.Attack"/>.
    /// <para>
    /// Added by concurrent-attacks_20260918. Before it, a commander had ONE package in a single
    /// field on the state (<c>OperationsState.StrikeSortie</c>), so allowing several attacks would
    /// have given the first one air and sent the rest in naked — the opposite of what that track is
    /// for. The package lives on the mission because it should die with the attack, which a field
    /// here gives for free.
    /// </para>
    /// <para>
    /// <c>OperationsState.StrikeSortie</c> still exists and still holds the DELIBERATE strike — the
    /// one the strike clock opens when no attack is running. That slot keeps its one-at-a-time rule
    /// and its clock; this track did not change it.
    /// </para>
    /// </summary>
    internal CommanderAirSortie? StrikeSortie = null;
```

**Step 2: Build.** Expected: 0 warnings, 0 errors (the field is unread for now).

---

### Task 5: One helper, ten call sites — THE REFACTOR

**Files:**
- Modify: `Operations/CommanderOperationsAirPackages.cs` (`:477` `FindStrikeFor`, `:511`
  `UpdateStrikePackage`, `:556` `ForgetStrikeSortie`, `:570` `CloseFinishedStrike`)
- Modify: `Operations/CommanderOperationsAirIdle.cs:404` (`AddStrikeDemand`)
- Modify: `Operations/CommanderOperationsAirPosture.cs:837` (the stand-down)
- Modify: `Operations/CommanderOperationsOffensive.cs:1271`, `:1288`, `:1317`, `:1385`

**Read first, all of it, before editing anything:** every one of those ten sites. They are listed in
the design §3. Two behaviours must survive untouched:

1. **`ForgetStrikeSortie` resets the DELIBERATE strike's clock** (`state.LastStrikeAt`,
   `state.StrikeClockMinutes`, `state.StrikeClockReported`). An ATTACK's package closing must NOT
   touch that clock — otherwise every attack that finishes delays the next deliberate strike.
2. **`FindStrikeFor(state, mission)`** already matches a package to a mission by point or airbase.
   With the package on the mission it becomes `mission.StrikeSortie` — simpler, and it removes a
   lookup that could match the wrong attack once several are open on nearby points.

**Step 1: Add the enumerator**, in `CommanderOperationsAirPackages.cs` beside `ForgetStrikeSortie`:

```csharp
    /// <summary>
    /// Every strike package this commander currently has flying: the deliberate one in
    /// <c>state.StrikeSortie</c>, plus one per open attack. Filled into a caller-owned buffer rather
    /// than returned, so the per-review walks allocate nothing.
    /// <para>
    /// One definition, read by the demand walk, the go-in update and the close sweep (Reuse rule 4).
    /// Before concurrent-attacks_20260918 each of those read the single field directly; with
    /// packages in two places that would have been three chances to forget one.
    /// </para>
    /// </summary>
    private static void CollectStrikeSorties(OperationsState state, List<CommanderAirSortie> into)
    {
        into.Clear();
        if (state.StrikeSortie != null)
        {
            into.Add(state.StrikeSortie);
        }

        for (int i = 0; i < state.Missions.Count; i++)
        {
            CommanderOperationsMission mission = state.Missions[i];
            if (mission.Kind == CommanderMissionKind.Attack && mission.StrikeSortie != null)
            {
                into.Add(mission.StrikeSortie);
            }
        }
    }
```

Add a reused scratch list beside the file's other scratch fields.

**Step 2: Split the forget.** `ForgetStrikeSortie` keeps its name and its clock reset for the
deliberate strike only; add a sibling that clears an attack's package without touching the clock:

```csharp
    /// <summary>Drops one attack's package. Deliberately does NOT touch the deliberate strike's
    /// clock — see ForgetStrikeSortie, whose clock reset belongs to the deliberate strike alone.</summary>
    private static void ForgetAttackStrike(OperationsState state, CommanderAirSortie sortie)
    {
        for (int i = 0; i < state.Missions.Count; i++)
        {
            if (ReferenceEquals(state.Missions[i].StrikeSortie, sortie))
            {
                state.Missions[i].StrikeSortie = null;
                return;
            }
        }
    }
```

**Step 3: Retrofit the three walks** — `AddStrikeDemand`, `UpdateStrikePackage`, `CloseFinishedStrike`
— to call `CollectStrikeSorties` and loop, instead of reading `state.StrikeSortie` and returning
early. Each keeps its existing body verbatim inside the loop; this is a MOVE, not a rewrite
(Reuse rule 3). `CloseFinishedStrike` calls `ForgetStrikeSortie` for the deliberate one and
`ForgetAttackStrike` for an attack's.

**Step 4: Retrofit `FindStrikeFor`** to `return mission.StrikeSortie;` and leave `StrikeDeliveredFor`
calling it unchanged.

**Step 5: Retrofit `OpenStrikeSortie`.** It takes the mission it is opening for (null for the
deliberate strike). Its guard becomes: refuse if THAT owner already has a package, not if any
package exists. `:1385` assigns to the owner's slot.

**Step 6: Retrofit the stand-down** at `CommanderOperationsAirPosture.cs:837` so it clears whichever
of the two owners holds the sortie.

**Step 7: Build.** Expected: 0 warnings, 0 errors.

**Step 8: Confirm the deliberate strike's gates still read the single slot.**
`Operations/CommanderOperationsOffensive.cs:1271` and `:1288` decide whether the CLOCK opens a
deliberate strike; both must still test `state.StrikeSortie` and the "is an attack open" flag, so a
deliberate strike is still one at a time and still suppressed while an attack is running.

---

### Task 6: Call up a platoon from an unthreatened forward base, failing check first

**Files:**
- Modify: `Operations/CommanderOperationsOffensive.cs`
- Modify: the operations self-check block

**Read first:** `IsThreatenedFrontPoint` at `Operations/CommanderOperationsFront.cs:2200` and its
summary — it is the mod's ONE definition of "the enemy is at this point", read by the forward-base
allowance, the pool order and the order book, "so all three can never disagree". This rule becomes
its fourth reader and must not invent a second idea of danger. Also read `DemoteForwardBaseToPicket`
(`:2305`), which is how a base gives up its platoons today, so the call-up matches its shape.

**Step 1: Write the failing checks.**

```csharp
        // The call-up (concurrent-attacks_20260918 §4 decision C, §5 decision E).
        Expect(failures, "a quiet forward base may send its platoon to an attack", PlatoonMayBeCalledUp(threatened: false, hasPlatoon: true, isForwardBase: true), true);
        Expect(failures, "a threatened forward base is never stripped", PlatoonMayBeCalledUp(threatened: true, hasPlatoon: true, isForwardBase: true), false);
        Expect(failures, "a forward base with no platoon has nothing to send", PlatoonMayBeCalledUp(threatened: false, hasPlatoon: false, isForwardBase: true), false);
        Expect(failures, "a picket is never called up, it is what holds the point", PlatoonMayBeCalledUp(threatened: false, hasPlatoon: true, isForwardBase: false), false);
```

**Step 2: Run it to make sure it fails.** Expected: `error CS0103: ... 'PlatoonMayBeCalledUp' ...`.

**Step 3: Write the pure rule.**

```csharp
    /// <summary>
    /// Whether one forward base may give its platoon to an attack, pure. Three conditions, and the
    /// first is the guarantee: a base the enemy is AT keeps everything it has. The second is that
    /// there is something to send, and the third is that only a forward base is ever asked — a
    /// picket is the detachment holding the point and is never called up
    /// (concurrent-attacks_20260918 §5 decision E).
    /// <para>
    /// What the caller must also honour and this rule cannot express: the base keeps its
    /// <c>PicketMembers</c> and its <c>Truck</c>. The platoon goes, the point is still held.
    /// </para>
    /// </summary>
    internal static bool PlatoonMayBeCalledUp(bool threatened, bool hasPlatoon, bool isForwardBase)
    {
        return isForwardBase && hasPlatoon && !threatened;
    }
```

**Step 4: Build and run the harness.** Expected: 0 failures.

---

### Task 7: Wire the call-up into the attack

**Files:**
- Modify: `Operations/CommanderOperationsOffensive.cs` (`TryOpenAttack`, around the
  `CountSparePlatoons` test at `:622`)

**Read first:** `CountSparePlatoons` at `:99` — it counts only platoons with no mission or a Reserve
mission, which is exactly why attacks are small.

**Step 1:** When `spare < minPlatoons`, walk the forward-base missions in WORST-ranked order (the
least valuable base gives up its platoon first), test each with `PlatoonMayBeCalledUp` and
`IsThreatenedFrontPoint`, and move one platoon to the attack — clearing its `Mission` and removing it
from the base's `Assigned`, leaving `PicketMembers` and `Truck` alone.

**Step 2: Log it**, once per call-up, naming the base and the attack:

```csharp
        CommanderAiLog.Note(
            hq, $"calls up {platoon.Name} from {baseMission.Label} for the attack on {label}: that base is quiet.");
```

**Step 3: Build.** Expected: 0 warnings, 0 errors.

**Note:** this is wiring and gets no self-check. Task 11 is how it is seen.

---

### Task 8: The sliders

**Files:**
- Modify: `UI/CommanderOverlayUiSettings.cs` (`OperationsBoxHeight` at `:593`, sliders at `:633`)

**Step 1:** Raise `OperationsBoxHeight` 494 → 570 (two more 38 px rows) and update its comment to say
thirteen rows and why.

**Step 2:** Add both sliders beside the ground ceiling's:

```csharp
        CommanderSettings.MaxAttacks = Mathf.RoundToInt(DrawPointsSlider(
            rowY, width, "Attacks at once", CommanderSettings.MaxAttacks, 0f, 8f, "0", " attacks"));
        rowY += 38f;

        CommanderSettings.AttacksPerPlatoon = DrawPointsSlider(
            rowY, width, "Attacks per platoon", CommanderSettings.AttacksPerPlatoon, 0f, 1f, "0.00", "");
        rowY += 38f;
```

**Step 3: Build**, then eyeball in game that the box does not clip and the STRATEGIC SAVE box below
does not overlap.

---

### Task 9: Prove the new checks still fail

**Files:** temporarily modify `Operations/CommanderOperationsOffensive.cs`.

CLAUDE.md testing rule 5. Record the sha256, plant ONE defect at a time, build (each MUST build
clean), run the harness, restore, confirm byte-identical.

| Defect | Change | Must fail this NAMED case |
|---|---|---|
| 1 | `Mathf.Max(floor, share)` → `Mathf.Min(floor, share)` | `a twenty-platoon army gets four attacks` |
| 2 | drop the `floor <= 0` branch | `a floor of zero means one attack, the old behaviour` |
| 3 | drop `Mathf.Clamp01` on the share | `a share above one is clamped rather than multiplying the army` |
| 4 | `!threatened` → `threatened` in `PlatoonMayBeCalledUp` | `a threatened forward base is never stripped` |
| 5 | drop `isForwardBase` from `PlatoonMayBeCalledUp` | `a picket is never called up, it is what holds the point` |

Record every result for Task 10.

---

### Task 10: Changelog and decision log

**Files:** `CHANGELOG.md` (top of `## Unreleased`), `conductor/decision-log.md` (DECISION-062).

**Read first:** the air-ceiling entry at the top of `## Unreleased` — the register to match. Plain
words, no identifiers in prose, says what the player will notice and what it costs.

**Changelog** must cover: the commander used to run exactly one attack however big its army, so the
map was a static picket line with one push crawling across it; it now runs several, each with its own
air ahead of it; and a forward base behind a quiet sector sends its platoon forward while keeping the
detachment that holds the point. State the cost: attacking in three places with a small army can mean
losing in three places, and forward bases are held more thinly while a push is on.

**Decision log** as DECISION-062, in the established shape, carrying the measured evidence (48
pickets / 11 forward bases / 1 attack; 1 of 10 platoons attacking; 382 fighters against 177
ground-attack), decisions A-E from the design, the full impact list, the verification, and a clearly
headed **NOT OBSERVED IN GAME** section carrying Task 11 verbatim.

---

### Task 11: In-game verification — the developer flies

**Files:** none. CLAUDE.md testing rule 2.

**At load.** Silence from `Operations self-check` means the eleven new cases passed.

**In a match, with the health line on.** What should be different:

1. **More than one `Attack` in the mission board** on the review line, and more than one platoon
   reading `Attacking@`. The measured baseline is exactly one of each.
2. **Each open attack has a package.** Look for more than one `STRIKE <point>` entry in the `air=[...]`
   list. Before this track there was never more than one.
3. **Call-up lines**: `calls up NTH PLATOON from <base> for the attack on <point>: that base is quiet.`
4. **`CAS` fill rising against `CAP` fill.** The measured split was 177 ground-attack launches against
   382 fighters (32/68). Anything moving toward parity is the goal of this track.
5. **Territorial events per hour above 26**, the measured rate.

**Regressions to watch for, likeliest first.** Attacks that open and sit `Forming` forever because
nothing can man them (lower `Operations/AttacksPerPlatoon`); forward bases falling to counter-attack
after being called up (this is the stated trade — if unacceptable, the call-up needs a "keep one
platoon per base" floor, which is deliberately NOT in this track); and the deliberate strike going
quiet because an attack's package closing wrongly reset its clock (Task 5 step 2 exists to prevent
exactly this — if it appears, that split is wrong).

---

## Task DAG

Tasks 1, 2, 4, 6 are independent. Task 3 depends on 1 and 2. Task 5 depends on 4. Task 7 depends on 6.
Task 8 depends on 2. Task 9 depends on 1 and 6. Task 10 depends on 9. Task 11 depends on all.

**Do not verify in game between Task 3 and Task 5.** That intermediate state opens several attacks
while only one can have air, which is worse than either end.

## Out of scope — do not drift

- The 48 pickets and the 60-point map.
- Any change to air demand weighting or the air ceiling.
- The sub-second frame stalls and the quiet-ground retirement that has never fired.
- A "keep one platoon per forward base" floor — mentioned in Task 11 as a possible follow-up only.
