# Save and load for a long match — feasibility study

Date: 2026-09-17. Read-only study, no code changed. Revised twice as the scope narrowed.

## Correction, read this first

**An earlier framing of this work assumed the game has its own in-match save that preserves the
world, leaving the mod only to remember what the commander knows. That is wrong, and nothing in
this document rests on it.**

The game has no save of a running match. `MissionSaveLoad` writes a mission *definition* — the
authored starting layout an editor produces and a mission loader reads. The evidence:

- The only caller of `SaveMissionTemp` is `CustomizeMissionMenu.CloseCustomizeMenu`, the pre-launch
  customise screen (`Assembly-CSharp.decompiled.cs:29060-29078`).
- Every autosave belongs to the mission editor: `AutoSave_Timed`, `AutoSave_OnPlay`, `AutoSave_OnExit`,
  with a retention-days cleanup (`:136589-136700`).
- `Mission.BeforeSave()` only de-duplicates names and flattens airbase strings (`:137662-137695`).
  It never reads a live unit's position, health, fuel or faction.
- Units spawned during a match never enter the mission at all. Neither `Spawner.SpawnVehicle` nor
  `Spawner.SpawnUnit` creates a saved record (`Spawner.cs:285-308`, `:607-634`), and a depot builds
  its vehicles with no name at all (`:74002`).

A related finding, which the original plan also depended on: **unit identifiers do not survive a
load.** The game's identifier is a bare counter handed out at spawn and reset to zero on clear
(`Unit.cs:528`, `:16638-16661`), and the saved unit record has no identifier field — only a type, a
faction, a name string and a position (`:142455-142500`).

So a mission restart returns the designer's opening map and nothing else. Anything that resumes a
match must have the mod record the strategic situation itself and re-establish it.

## Current scope

Set by the developer, and much narrower than the first framing:

- **Save** records who holds which control point, which forward bases exist and where, each
  faction's money, plus the cash value of every living unit added to that faction's treasury.
- **Load** starts a fresh mission, applies the saved ownership, rebuilds the forward bases where
  they were, and sets each faction's funds to the saved total. Both commanders then rebuild their
  forces from that war chest.
- Units are **not** recreated. No positions, damage, ammunition, loadouts or anything in flight.
- Forward bases **are** rebuilt where they were, not cashed in. A base costs a delivery flight and
  roughly ten minutes, so refunding it would undo real progress.

## The one thing that breaks this plan

**Refunding every unit and restoring who holds what are, as specified, mutually exclusive.**

In this mod, holding a control point is not a stored fact. It is a continuously re-derived
consequence of having vehicles standing in the ring. The hold machine runs every five seconds
(`Points/CommanderStrategicPointService.cs:184-243`), counts the ground vehicles each faction has
inside the point's radius, and then:

```
if (qualifying < 0)
{
    state.OwnerIndex = -1;
    state.CandidateIndex = -1;
    state.Progress = 0f;
    return;
}
```

`Points/CommanderStrategicPointService.cs:448-454`. `QualifyingFaction` returns -1 when no faction
has the minimum garrison in the ring (`:325-348`).

Refund every vehicle and there is no garrison anywhere. **The whole map goes neutral about five
seconds after the load finishes**, silently, and the restore looks like it simply did not work.
This is not a corner case; it is the main path.

Three ways out, and I recommend the second:

1. **Add a forced-owner state the reset path respects.** A real change to how capture behaves, it
   makes ownership sticky in a way it has never been, and it needs a conductor track of its own.
2. **Re-raise a minimum garrison at each held point instead of refunding it.** Refund everything
   except the vehicles that justify ownership, then spawn the minimum garrison at each point the
   save says was held. This fits the developer's own framing — exact composition is irrelevant — and
   costs nothing new: the buy path, the price rule and the spawn already exist. The garrison is
   bounded at the minimum garrison setting multiplied by the number of points held.
3. **Let the map go neutral and have both commanders re-take it.** Cheapest, and not a resume.

Bases and mined resource sites are exempt from this problem. A base's owner is the game's own
`Airbase.CurrentHQ`, and a site's owner follows the mine standing on it, so both can be
re-established directly.

## Part 2 — The machinery that already exists

This is the part that stands regardless of route, and under the narrowed scope it is a large share
of the work already done.

| Piece | Where | What it does |
|---|---|---|
| The interface | `Core/ICommanderService.cs:49` | `ICommanderPersistState` with snapshot and restore, fanned out by the registry (`Core/CommanderServiceRegistry.cs:31`, `:62`) |
| The file | `Core/CommanderStateStore.cs` | Writes JSON to the profile folder every 20 seconds and on shutdown, reads once per run, refuses a stale file |
| The records | `Core/CommanderStateModels.cs` | Air missions, relaunch queue, mine, factory and dock levels |
| Control points | `Points/CommanderStrategicPointPersist.cs` | Every discovered point, its owner stored as a faction *name*, and the road polylines |
| Air Command | `AirCommand/CommanderAirCommandPersist.cs` | Live missions and their recipes |
| Economy | `Economy/CommanderEconomyServicePersist.cs` | Upgrade levels, re-attaching a mine to a site by nearest position within 100 m |

**What survives a mod hot reload today:** control points and roads with their owners, air missions
and their recipes, and mine, factory and dock upgrade levels. **What does not:** platoons, forward
bases, and the enemy commander's plan and savings.

Two disciplines in that code must be copied exactly by anything new. Owners are stored as faction
*names*, never as an index, because an index is only meaningful against the faction order it was
taken with (`Persist.cs:247`, `:290-306`). And the faction order is refreshed *before* owners are
resolved (`:128-132`), because refreshing it afterwards resets every point to neutral.

**One blocker in the existing store.** A snapshot is only read on a run that loaded from memory
rather than disk, and only if its recorded level time is earlier than the current level time and
within ten minutes of it (`Core/CommanderStateStore.cs:60`, and the session guard). After a mission
restart the level clock starts at zero, so both conditions fail. Widening this to cover a mission
restart means replacing that guard with an explicit named save, not a time window.

## Part 3 — Answers to the open questions

### Valuing the units

**There is one rule and it is the game's own worth rating**, `UnitDefinition.value`, carried by
aircraft, vehicle, ship and building definitions alike. The belief that vehicles price against a
faction catalogue is half right: the catalogue is a shopping list of what a faction may buy, but the
price on each entry is still that same rating.

There is no single function today that takes a living unit and returns its worth, but there are two
working precedents that do exactly this inline, and they are identical in shape:

- Ground, including the refund itself: `Operations/CommanderOperationsService.cs:801` reads the
  vehicle's value, and `:816` pays it back with `hq.AddFunds`.
- Air: `Operations/CommanderOperationsAirPlatoonCap.cs:142`.

So one helper is feasible and should be written once, per the project's reuse rule. Three things it
must special-case, because they were not bought at the rating: structures carry a cost multiplier
(`Economy/CommanderEconomyServiceCatalog.cs:37`); mines, factories and naval docks were bought at
flat configured prices (`Economy/CommanderEconomyService.cs:309`); and upgrade levels are sunk cost
on top, held in the level tables that are already saved.

Money is added with the game's own `AddFunds` on the faction. **This must be guarded by the server
check**, per the project rule, because a load runs off an event rather than a button; the nearest
existing model is `AirCommand/CommanderAirCommandGroundLoss.cs:68-82`.

Living units are enumerated by walking the faction's unit list and resolving each identifier, as at
`Economy/CommanderEconomyServiceEnemy.cs:592-615`. One caveat found in an earlier track: a building
that was knocked out when its base changed hands stays in the old faction's list, so a refund walk
that trusts that list alone can pay the wrong treasury.

### Setting control-point ownership on load

| Kind | Can it be forced? | How |
|---|---|---|
| Base | **Yes, and it sticks** | `airbase.capture.ForceCapture(hq)`, resolving the base by display name with the lookup that already exists (`Points/CommanderStrategicPointPersist.cs:210-228`). Public, server-only, and **not called anywhere in the mod today**, so unproven in play |
| Site with a mine | **Yes, indirectly** | Re-spawn the mine for the owning faction; ownership then follows the mine |
| Village, hilltop, outpost, crossroads, roadside | **Written easily, held only with a garrison** | The field is writable and the hot-reload restore already writes it, but the hold machine reverts it within five seconds unless vehicles are standing there |

Forcing a base's ownership also reassigns every building on it, which can leave knocked-out
buildings attributed to the wrong faction. Worth a log line rather than a guard.

### Rebuilding the forward bases

**The good news is substantial: a forward base can be created complete, standalone, with no delivery
flight and no delivery bookkeeping.** `CommanderEconomyService.TryBuildFob(hq, position, pointLabel,
built, out airbase)` at `Economy/CommanderFobBuilder.cs:804-834` reads no order record, no cargo, no
strategic point and no service state. It creates the base record, registers it with the mission,
spawns it, and puts down a vehicle depot, a radar and two helipads. Its only preconditions are a
server and a live mission. It charges nothing — the price is taken at order time — so a rebuild is
free by construction, which is what the decision requires.

Three things the caller must get right:

1. **Build the base before the structures.** The function already does this internally, and the
   ordering is load-bearing: the structures are refused outside a captured base's build radius, and
   creating the base first is what makes the position legal (`:962-965`, recorded from a live run).
2. **The name counter restarts at one.** It is not saved and is zeroed on reset
   (`Economy/CommanderFobBuilder.cs:115`, `Economy/CommanderEconomyService.cs:615`), so a rebuilt
   base can collide with an existing name. It must be seeded past the highest restored number.
3. **Only one site rule can realistically refuse a previously valid position** — a physical overlap
   with something parked on one of the four structure spots (`Economy/CommanderBuildPreview.cs:416-432`).
   Spacing and enemy-proximity rules gate site *picking*, not building, and there is no cap on how
   many bases may exist.

### The recognition problem, which is the one most likely to bite

Better than feared, and worse in a specific place.

**The commander will not order a second base on the same ground.** The decision reads the world, not
the order list, for both distance gates: it measures to the nearest base whose name begins with the
forward-base prefix, across every faction (`Economy/CommanderFobBuilder.cs:146-187`, used at
`Operations/CommanderOperationsFob.cs:776`). Rebuild through the normal path and the prefix is
there automatically, so the rebuilt base refuses its own ground for five kilometres. The depot
re-registers itself through a poll every five seconds with no restore needed.

**But everything on the online side is driven solely off the mod's own order record**, and the code
says so itself: *"A reload leaves the runtime airbase standing and working but forgets the order
that built it, so the teardown watch stops watching it"* (`Operations/CommanderOperationsFob.cs:28-30`).
Without a restored record: a base whose buildings all die is never torn down and stands forever as
an empty registered base; it can never be abandoned to advance the line; and the quiet and in-use
clocks never run.

So the record must be synthesised, and it can be. The base, the point, the phase and the building
list are all recoverable from the world. Two clocks default to a value read as *"quiet, never used"*,
which would make a freshly restored base immediately eligible for abandonment — both must be seeded
to the current time on restore. That is a named rule, not an accident, and belongs in the task.

### Half-delivered bases

**Rule: a base that was not yet online at save time is cancelled, not restored.** Its cost is not
refunded.

That matches what the code already does and says. There is no refund anywhere on the delivery path —
*"No refund — the structures were charged at order time, which is the stake"*
(`Operations/CommanderOperationsFob.cs:3005-3006`), and the same stance at abandonment and teardown.
A partly built order is already destroyed unconditionally on the next review, because construction
is synchronous and a phase left there means the build failed (`:1558-1562`). Restoring a delivering
order would strand it: the transport it was waiting on no longer exists, and the cargo handler walks
an empty flight list, so it would sit until the fifteen-minute stall timeout cancelled it anyway.

Cancelling at save time is therefore the same outcome, ten minutes sooner and visible in a log line
rather than as an unexplained wait. Refunding it instead would be a change to a rule the mod applies
consistently everywhere else, and should not be smuggled in here.

### Where the save goes and what triggers it

Put it beside the existing snapshot, in the mod's folder under the game's profile directory, but as
a *named* file that does not expire and is not deleted on read — the hot-reload snapshot is
deliberately read-once and time-guarded, and those properties are wrong here.

For the trigger, recommend a button in the settings window rather than a key. The window already has
buttons and the helpers to draw them (`UI/CommanderOverlayUiSettings.cs`), saving is a deliberate
act performed once an hour rather than something to fire by accident, and a key binding would need a
new setting and a new shortcut for no gain. A configurable shortcut can follow later if the
developer wants one; the mechanism exists (`Core/CommanderShortcutInput.cs`).

## Part 4 — Honest verdict

**This is not a day's work. It is closer to a week, and two parts of it are unproven in play.**

The narrowing helped a great deal. Not recreating units removes the hardest problem entirely, and
rebuilding a forward base turns out to be a single standalone call. But the remaining work is still
six or seven distinct behaviours, each needing its own verification in the running game:

- An explicit named save and load that survives a mission restart, replacing the guard that
  currently refuses exactly this.
- A single valuation helper with three documented exceptions, and a refund walk guarded for the server.
- Base ownership forced through an interface the mod has never called.
- Presence-held points re-garrisoned, which is a new behaviour, not a restore.
- Forward bases rebuilt, with a name counter seeded and an order record synthesised.
- Mines, factories and docks rebuilt with their levels.
- The enemy commander's savings and its opening-balance flag, which if missed hands every computer
  faction a second opening balance, silently and unwinnably.

**If the frame-rate decay is the reason for building this, fix the frame rate instead.** That is the
more useful of the two answers. A save and load that exists only to work around a performance
problem buys a restart, costs a week, and adds a permanent surface where a bad restore quietly
corrupts a match rather than failing loudly. The other agent's investigation may remove the need
entirely.

If long matches are the goal in their own right, which the developer has said they are, then this is
worth building — but afterwards, and in a narrower first slice. The slice I would ship first is:
money, control-point ownership with re-garrisoning, and forward bases. That is the strategic picture
the developer actually described. The enemy commander's internal state and the economy's upgrade
levels can follow once the first slice has survived a real match.

The first task either way is the same, and it is not the file format. It is to make a saved file
survive a mission restart at all: an explicit save, a file that does not expire, and a load that
runs after the session reset on the new mission, with the hot-reload path left exactly as it is.
Until that works, nothing else can be tested.
