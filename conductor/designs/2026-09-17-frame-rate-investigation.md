# Frame-rate decay investigation (2026-09-17)

Investigation plus one small, additive change: a health line that measures what reading the code can
no longer settle. Evidence is file:line from the working tree, from the decompiled game at
`/tmp/no-decomp/`, and from `BepInEx\LogOutput.log`.

Fourth revision. Three theories have been tested and all three are dead. They are kept here as
disproved rather than deleted, because knowing where the problem is not is worth as much as a guess
about where it is.

---

## Where this stands

**Reading the code has run out of answers, so the deliverable is now a measurement.** A health line
is built, installed and off by default. Section 5 is the three-step instruction for switching it on
and what to send back.

Three candidates that fitted the symptom have each been killed by a fact:

| Theory | Killed by | Status |
|---|---|---|
| The commander overlay's drawing, immediate-mode half | Developer hid the overlay; frame rate did not move | Dead |
| The per-unit on-screen images, which hiding does not stop | Developer left commander mode entirely; frame rate still did not move | Dead |
| Permanent wrecks acting as steering obstacles | Both missions set a one-minute wreck decay, and it works | Dead |
| The commander log's storage | Benchmarked at 105 nanoseconds against 3.4, at one or two lines a second | Dead |

That second row matters most, because it closes the gap I flagged in the previous revision. Hiding
the overlay only stops the drawing; leaving commander mode also stops the per-frame service work,
including the one on-screen image per tracked unit whose position, scale, sprite and colour were
being rewritten every frame. **Neither changed anything, so the whole of the mod's per-frame drawing
and marker work is innocent.**

What is left is a fork nobody has measured:

- **Either the frame rate tracks the number of units that are alive**, in which case the mod simply
  fields more than the game is tuned for and the fix is caps, not leak-hunting.
- **Or it tracks elapsed time with unit count flat**, in which case something accumulates, and the
  best remaining candidate is memory.

The two look identical in play, because both commanders buy all match and the live unit count rises
roughly with match age. The health line reports both figures side by side, which is the whole point
of it.

---

## 1. What was built

**A new service, `Core/CommanderHealthDiagnostics.cs`**, plus one `Register` line. It writes one
line to the BepInEx log every thirty seconds.

```
Health t=24:30 frameMsAvg=41.2 frameMsWorst=180.4 frames=728 units=214 aircraft=11 ground=168
buildings=35 everSpawned=903 wrecks=6 tracked=Boscali:74/Primeva:81
strategicTargets=Boscali:12/Primeva:9 markerImages=192 airMissions=9 airSurvivalSeen=57
cargoMissions=2 cargoAutopilots=2 shielded=0 opsPool=12 opsPlatoons=10 opsMissions=94
opsSorties=6 opsAirframes=14 opsAirIssued=14 econMines=4 econFactories=5 econDocks=3
econBuilt=48 heapMB=412 gc0=1841
```

(One line in the log; wrapped here to fit the page.)

**Why each figure is there:**

- **`units` against `everSpawned`** is the fork, in two numbers. One is what is alive; the other is
  the game's own registry of every unit ever spawned, which is cleared only on mission teardown.
  Plot frame time against each. Whichever it follows is the answer.
- **`frameMsAvg` and `frameMsWorst`** together, because an average alone hides hitches. A rising
  average is steady cost. A rising worst with a flat average is a periodic stall, such as a garbage
  collection or a blocking console write. `frames` confirms the window really covered thirty
  seconds.
- **`heapMB` and `gc0`** test the memory theory in section 2.
- **`aircraft`, `ground`, `buildings`** split the live count, because it is ground vehicles and
  buildings that a commander's spending adds.
- **`wrecks`** should sit in single figures. Hundreds would mean the wreck decay is not being
  applied after all and section 3 comes back from the dead.
- **`markerImages`** is the per-unit image count, now known innocent, kept because it is free and
  because it says how many there were when the conclusion was drawn.
- **`airSurvivalSeen`, `econFactories`, `econDocks`** are confirmed small leaks in the mod. They
  should climb. If they do not, the line is not being read correctly, so they double as a check on
  the instrument itself.
- **`cargoAutopilots` against `cargoMissions`** is the one mod leak that could not be settled by
  reading. The two should track each other; a widening gap is the leak.
- **`strategicTargets`** is a confirmed game-side grower (section 2).

**What it costs.** Reasoned from the code, not measured in a match:

- **Setting off:** one boolean field test and one float comparison per frame. No allocation. Every
  thirty seconds it additionally reads the setting once and zeroes three fields.
- **Setting on:** adds one clock read, one add, one increment and one comparison per frame. Still no
  allocation.
- **Per report, once every thirty seconds:** one pass over the live unit list with a type test per
  entry, about thirty constant-time count reads, a pass over three headquarters, and the line built
  into two reused buffers. At a few hundred units that pass is tens of microseconds, so well under
  one frame's budget, once in roughly 1,800 frames.
- `GC.GetTotalMemory(false)` is the non-forcing overload and does not provoke a collection. The
  strings go into reused buffers on purpose: a diagnostic that adds garbage to a
  garbage-collection investigation measures itself.

**Where the numbers come from.** Each service answers for its own tables through a new partial file
(`Operations/CommanderOperationsHealth.cs` and three siblings), which is purely additive and cannot
conflict with the work another agent is doing in those directories. A service that is not running
reports -1, which reads as "not asked" and can never be mistaken for an empty table.

**Verification.** Build and install clean, 0 warnings and 0 errors, into the scripts folder with no
copy left in the plugins folder. Seven named self-check cases over the two pure helpers, driven
against the built assembly offline. Five defects were planted one at a time, each failing a named
check, the file restored byte-identical after every one. **Nothing has run in the game: the
developer's game was closed throughout, so no line has been written and no figure has been read.**

---

## 2. What survives, ranked

Neither is proven. Both are what the health line is for.

### 1. It may simply be the number of live units

"Full-map combined arms warfare" is by definition the state with the most units. The mod fields far
more ground vehicles than any stock mission, and its own review log shows that growing: platoons run
0 to 6 early in the session and 8 to 12 late, with pooled vehicles reaching 25. Unit count rises
roughly with match age under the mod, **so unit-count cost and time-decay look identical unless you
plot them against each other.** The developer doubts this, but it has never been tested.

### 2. Memory growth making every garbage collection slower

`UnitRegistry.persistentUnitLookup` (decompiled line 16580) keeps a hard reference to every unit ever
spawned. The only removal anywhere in the game is a wipe on mission teardown (`:16651`); the sibling
lists of live units are cleaned properly on death (`:16594`), and this one is deliberately kept so
the game can answer "did this id used to be a unit".

Unity's collector does not compact and does not separate young objects from old, so **every
collection walks everything still alive**. A live set that only grows means each collection costs
more at unchanged frequency. That is a textbook decay curve, and it is the best remaining explanation
for a frame rate that worsens while unit count is flat. Under the mod it bites far harder than the
game expects: hundreds of units an hour instead of a few dozen.

**The mod cannot fix this.** Clearing those entries would break the lookup the whole game depends on.
What it can do is allocate less, which lowers how often collections happen. Two cheap places are
listed in section 4.

**A third, smaller, confirmed game-side grower:** `FactionHQ.strategicTargets` never shrinks. At
`FactionHQ.cs:2043` the game builds two separate tracking records and files one in the contact
database and the other in this list; both removal paths look up the database's object and test the
list for it, and with no equality override that test compares object identity and can never succeed.
Nothing reads the list, so it is memory only. Worth reporting upstream. It is on the health line
because this is the chance to watch it.

---

## 3. Disproved, with the evidence

**All of the mod's drawing and per-frame marker work.** Two separate developer tests. Hiding the
overlay stops the immediate-mode drawing and changed nothing; leaving commander mode entirely also
stops the active-mode tick, including the per-unit on-screen images, and changed nothing either.

The costs below are real and I stand by the readings. They are not what is being felt, and they
should not be optimised on frame-rate grounds.

- Label stacking is quadratic in labels on screen (`UI/CommanderUiTheme.cs:422`), and each label costs
  a text measurement plus five text draws (`:455`). Order of 150 to 200 labels a frame.
- Aircraft label text is built before the on-screen test, and building it scans every strategic point
  and every airbase (`Operations/CommanderOperationsAirMarkers.cs:194`).
- Every unit of every faction is walked every frame to find the aircraft (`:104`).
- One on-screen image per tracked unit, with position, scale, sprite and colour rewritten every frame
  (`Units/CommanderMarkerService.cs:47`, `Units/CommanderMarkerView.cs:61-66`).

**Permanent wrecks.** The mechanism is real: `GroundVehicle.SpawnWreckage` (decompiled line 91438)
registers every dead vehicle as an obstacle with an effectively infinite lifetime, and every living
vehicle re-scans that list every four seconds. But both missions in this repository set
`wrecksDecayTime: 1.0`, and `Wreckage.Start` (line 85324) reads that as **minutes**:
`(int)(1f + wrecksDecayTime * 60f) * 1000` milliseconds, so 61 seconds. `Disintegrate` (line 85368)
removes the obstacle entry directly **and** destroys the object, which also makes the self-cleaning
branch in `UpdateObstacles` fire. At roughly eight deaths a minute the steady state is under ten
wrecks on the whole map, where the theory needed hundreds.

The count cap of ten is global, one list trimmed across the whole map every sixty seconds, and it is
probably inert: the sweep is scheduled in `Awake` only if the number is already positive
(`MissionManager.cs:447`), while the mission's value is not copied in until mission start (`:396`).
That does not matter, because the decay alone bounds it.

**The tracking database of spotted contacts.** The game removes each entry when the unit dies
(`FactionHQ.cs:1461`), driven by an event that fires on both the disable path (`Unit.cs:945`) and the
destroy path (`:1594`), so a unit killed far away is covered too. The mod's twenty walk sites peak at
about 360 full passes per thirty seconds, from the loop over control points
(`Operations/CommanderOperationsFront.cs:280-306`), which is under a millisecond.

**The commander log's storage.** Benchmarked: 105 nanoseconds against a ring buffer's 3.4, at one or
two lines a second. Four orders of magnitude short of mattering.

**Everything else:** no exception storm (seven error lines in 4,650, none recurring); aircraft count
flat across the whole session at 5 to 7; the mod's collections essentially clean, with every aircraft
and vehicle table having a sweep that covers shoot-downs and not just landings; the damage hook that
fires on every hit is one dictionary lookup; no leaked marker objects, event subscriptions or
coroutines in the mod; the game's own display and audio managers all capped and balanced; the
heightmap does not accumulate.

---

## 4. Small things worth fixing regardless

**Two per-frame allocations that feed section 2's memory theory.** Both behaviour-neutral:

- The attack-mode patch reads a private field by reflection, which allocates, **before** it checks
  whether the aircraft is one of ours (`AirCommand/CommanderAirCommandPilotHooks.cs:250-259`). Its
  two sibling patches have that early exit; this one does not. Runs per AI aircraft. Fifteen minutes.
- The mission-aircraft list is rebuilt and sorted with string comparisons every frame even when its
  window is shut (`AirCommand/CommanderAirCommandUi.cs:132`). The line below it is correctly gated,
  with a comment saying why, so this is an oversight. Five minutes.

**The debug console is on.** `BepInEx\config\BepInEx.cfg` has `[Logging.Console] Enabled = true` with
info-level messages, and the mod wrote 4,618 lines in one session, each a blocking write to a Windows
console window. Not a decay, but a severe hitch source: if anything selects text in that window,
Windows suspends the game until the selection is cleared. Turning it off costs nothing and the file
log keeps everything.

**Six small leaks in the mod**, an hour in total, worth doing because they are wrong rather than
because they are slow: airframes that have logged a survival line, never forgotten
(`AirCommand/CommanderAirCommandSurvival.cs:90`); factory and naval-dock upgrade levels, dropped only
when the player demolishes and never when the enemy destroys, while the sibling mine table has the
sweep they need (`Economy/CommanderEconomyService.cs:113-114`, sweep at `:1044-1064`); unit ids seen
at each depot (`Depot/CommanderSpawnService.cs:1138`); units whose radar was switched off
(`Units/CommanderRadarService.cs:20`); two fields missing from the session reset so they survive a
mission restart (`Camera/CommanderCameraFollowService.cs:33`,
`Depot/CommanderFactionVehicleService.cs:116`); and two transport autopilot tables whose removal is
guarded by a component already destroyed with the aircraft (`Supply/CommanderSupplyHeliService.cs:71-72`).

---

## 5. What to do now

The build is already installed. Three steps, one match.

**1. Switch the line on, before launching.** Open `BepInEx\config\com.groundcontrol.rts.cfg`, find
the `[Developer]` section (it is already there, with `KeepStateAcrossHotReload` in it), and add this
line under it:

```
HealthDiagnosticLine = true
```

The key is not in the file yet, because the setting was only just added and the plugin writes its
defaults the first time it runs. Typing it in by hand works: the config is read before the defaults
are bound, so the value is picked up on the very next launch and there is no need to launch twice.

While you are in the config folder, in `BepInEx.cfg` set `[Logging.Console] Enabled = false`. That is
unrelated to the measurement, costs nothing, and removes a stutter source.

**2. Play one match into the bad state.** Nothing special is needed. Play as you normally would,
until the frame rate is clearly bad. The line writes itself every thirty seconds whether or not
commander mode is open.

**3. Send back two lines from `BepInEx\LogOutput.log`.** Search for `Health t=`. Send:

- one line from early in the match, around `t=2:00`, and
- one line from once the frame rate is bad.

That pair is the whole measurement. Paste them one above the other and whatever has grown between
them is where to look next.

**What each outcome means**, so the answer is readable without waiting for me:

- **`frameMsAvg` roughly doubles and `units` roughly doubles with it** → live unit count. The mod
  fields too many, the fix is caps and cheaper per-unit work, and there is no leak to find.
- **`frameMsAvg` climbs while `units` is flat, and `heapMB` climbs all match** → memory. The game
  roots every unit ever spawned and each collection walks more of them. Mostly not fixable by the
  mod; the answer is to allocate less and to accept a ceiling on match length.
- **`frameMsWorst` climbs while `frameMsAvg` stays flat** → periodic stalls, not steady cost. Look at
  `gc0` first, then the console setting above.
- **Any single named table climbing into the thousands** → that table is the leak, and it names
  itself.
- **`wrecks` in the hundreds** → the wreck decay is not being applied and section 3's second theory
  is back.

If nothing on the line grows and the frame rate still decays, that is also a result: it would mean
the cost is inside the game's own per-unit work, and the next step is a real profiler rather than
more log lines.
