# Ground Control (RTS)

A BepInEx mod for **Nuclear Option** that adds RTS-style command gameplay on top of the base
game: a free camera, unit selection, orders, group control, ground/naval production and an
air-tasking layer. Intended mainly for the **Escalation** and **Terminal Control** game modes.
## Requirements

- Nuclear Option
- BepInEx 5
- BepInEx Configuration Manager (optional, for editing settings outside the game)

## Installation

1. Download `GroundControlRts.zip` from the latest release.
2. Steam → right-click Nuclear Option → Manage → Browse local files.
3. If there is no `BepInEx` folder, install BepInEx 5 first.
   If you ran the mod under its old name, delete the `NuclearOptionCommander` folder in
   `BepInEx\plugins` first -- otherwise both copies load and every patch runs twice.
4. Copy the `GroundControlRts` folder from the zip into:

```
Nuclear Option\BepInEx\plugins
```

---

## Quick start

| Action | Default |
| --- | --- |
| Enter/leave RTS mode | `CMD` button on the left edge (while outside an aircraft) |
| Move camera | `W` `A` `S` `D` `Q` `E`, hold `Shift` to boost |
| Look around | Hold `MMB` |
| Select a unit | `LMB` |
| Add to selection | `Shift` + `LMB` |
| Remove one unit from the selection | `Shift` + `LMB` on a selected unit |
| Select every unit of that type on screen | Double-click a unit |
| Select every unit of that type, anywhere | `Ctrl` + `LMB` |
| Box select (3D view) | Drag `LMB` |
| Box select (map) | `Ctrl` + drag `LMB` — a plain drag pans the map |
| Travel point | `RMB` |
| Multi-point route | Hold `Shift` and `RMB` each point in order |
| Attack order | `RMB` on a hostile unit, structure or objective |
| Guard order | `RMB` on a friendly unit |
| Stop and hold the selection | `X` |
| Jump to the next unit with no orders | `.` |
| Recall control group | `1` – `9` |
| Store control group | `Ctrl` + `1` – `9` |
| Recall camera view | `F1` – `F4` |
| Store camera view | `Ctrl` + `F1` – `F4` |
| Centre camera on selection | Tap `Space` (hold to centre and follow) |
| Fullscreen map | `M` |
| Cycle UI visibility | `H` |

Every binding is remappable in **CMD → Settings → Controls**, and they only apply while
RTS mode is active — aircraft controls are never touched.

**CMD → Settings → Shortcuts** lists every shortcut in the mod in one scrollable reference,
including the ones that are not remappable (control groups, camera bookmarks, double-click).
It reads the live bindings, so it shows your keys, not the defaults.

---

## Command and control

### Selection

- Click units in the 3D world, on the tactical map, or in the **Order of Battle** window.
- **Box select** by dragging the left mouse button in the camera view. On the map a plain drag
  pans, so hold `Ctrl` while dragging to draw a box there.
  A box that catches any friendly unit selects only friendlies.
- **Order of Battle** (`CMD → ORDER OF BATTLE`) lists every unit the faction owns, filtered by
  ground / air / naval / structures, plus tracked hostiles. Select one, select all, or recall a
  control group from the same window. Clicking a row also puts the camera on that unit.
- **Select by type**: double-clicking a unit selects every unit of that type currently on
  screen; `Ctrl` + click selects every one the faction owns, wherever it is.
- **Shift-click a unit that is already selected** to drop it, instead of redrawing the whole box.
- **Type chips**: a mixed selection shows one chip per unit type in the selection bar. Click a
  chip to narrow the selection to that type, or `Shift` + click it to drop that type.
- **`X` stops** the selection where it stands, without going to the selection bar.
- **`.` cycles idle units**: it selects and jumps to the next friendly ground or naval unit
  that holds no RTS order, so vehicles left at a depot or parked at the end of an old
  route are easy to find.

### Control groups

- Nine groups. `Ctrl` + `1`–`9` stores the current selection, `1`–`9` recalls it, `Shift` + a
  number adds the group to the current selection.
- A unit belongs to exactly one group, so recalling a group is unambiguous.
- Any order given to a selection applies to every unit in it, so a group is ordered as one.
- The current group is shown in the selection bar and as a `[n]` badge in the Order of Battle.

### Travel routes

- A single `RMB` replaces the route with one travel point.
- Holding the queue key (`Shift` by default) appends points, so you can plan a full path:
  the unit drives to point 1, then 2, then 3.
- Routes are drawn as numbered markers and lines in the 3D view **and** on the tactical map.
- Multiple selected units spread into a formation around each point instead of stacking.
- A unit that cannot reach a point (blocked, bad terrain) gives up after 60 s and moves on to
  the next one rather than stalling the whole route.
- Routes keep running when you leave RTS mode, so a convoy still arrives while you fly.
- Every order flashes a marker at the point that was ordered, in the 3D view and on the map, so
  a swallowed click is obvious. Toggle in **Settings → Gameplay**.
- **PATROL** turns a multi-point route into a loop. The unit keeps walking it while you are
  away flying, instead of parking at the last point.
- **Formations** are picked from the selection bar: ring, line, column or wedge, oriented along
  the direction of travel so the group arrives facing the right way. Column is the one that
  matters on roads.
- **Arrive together**: a unit that gets more than the cohesion distance ahead of the rearmost
  member of its order waits for it, so a group does not string out along the route.
- **Waypoint actions**: the `WP` button attaches an action to the *next* travel point you place
  — hold for a set time, radar off, or radar on. EMCON at a waypoint lets a battery drive to its
  firing position dark and only light up where you tell it to.

### Stances

Cycled from the selection bar, per unit, and they stick whether or not the unit has an order.

- **Free Fire** (default): shoots freely, and **attack-moves** — while travelling it breaks off
  to engage a hostile that comes inside its own weapon range, then resumes the route once the
  target is dead or has broken contact. Toggle the attack-move part in **Settings → Gameplay**.
- **Hold Fire**: turrets acquire nothing, so the unit stays quiet near a SAM belt. Radar-guided
  batteries fed by a fire-control truck also need the radar switched off in Unit Systems.
- **Hold Pos**: holds the ground it stands on and ignores travel orders, but still shoots.

### Guard and retreat

- `RMB` on a friendly unit — in the 3D view or on the map — tells the selection to escort it in
  formation and engage whatever shoots at it. Toggle in **Settings → Gameplay**.
- **RETREAT** sends the selection to the nearest friendly vehicle that can repair or rearm it.
  Turn on automatic retreat in **Settings → Gameplay** to have damaged units pull back on their
  own once they drop below the condition threshold.

### Attack orders

- `RMB` on a hostile unit, building or objective issues an attack order. Anything hostile can
  be targeted, including structures that cannot normally be selected.
- If the last point of a queued route lands on a hostile, the whole route becomes an approach
  and the unit attacks after walking it.
- Ordered units drive to roughly 70 % of their own weapon range and hold there, instead of
  driving onto the target. This can be turned off in **Settings → Gameplay**.
- Turrets on an ordered unit keep the commanded target as long as their own fire control
  considers it engageable, so a group focuses fire instead of scattering.
- The order re-tracks: if the target drives away, the attackers follow.
- When the target dies, the order does not: the attackers pick the nearest hostile the faction
  can see inside their own weapon range and keep firing. If nothing is in reach they hold the
  ground they took instead of reverting to the base-game AI and driving off. Toggle in
  **Settings → Gameplay**.

### Alerts and the battle log

- **Combat alerts** appear as clickable toasts in an `ALERTS` window in the top-right corner:
  `GROUP 3 UNDER ATTACK`, units lost, kills scored, reinforcements ready. Drag its title bar to
  move it anywhere (**Settings → Reset UI layout** puts it back). They are drawn **while you are
  flying too**, which is the point — otherwise you never find out you are losing units. Clicking
  one selects the unit and jumps to it. Toggle in **Settings → Gameplay**.
- **Battle log**: the `LOG` tab in the Order of Battle keeps the last events with mission
  timestamps. Click an entry to jump to the unit.
- **Condition and ammo**: the selection bar shows condition, ammo and the current order for the
  selection, and every Order of Battle row carries condition and ammo, so the supply layer is
  visible instead of invisible. Aircraft add a fuel reading.
- **Loadout**: with exactly one unit selected the bar lists what it is carrying - every weapon by
  name with rounds remaining, `R-27ER   2 / 4` - merged to one row per weapon type rather than
  one per pylon. Fuel only exists on aircraft in this game, so ground units and ships show no
  fuel reading.

### Aircraft

- Selected friendly AI aircraft accept the same travel points and attack orders. Aircraft are
  not directly commandable in the base game, so orders are executed through the Air Command
  mission layer, which drives the AI pilot.
- Ordering an untasked aircraft tasks it automatically: an attack order on an aircraft or
  missile becomes an Air Superiority mission, anything else becomes CAS, and a plain travel
  point uses the mission type currently selected in Air Command.
- Aircraft that are already in the air can be given a mission from **Air Command → AIR
  MISSIONS → IDLE**: pick the mission type, press `TASK`, and place the mission area on the
  map exactly as if you had just spawned it.
- An aircraft only acts on a travel point while it has no target of its own, and only once its
  AI pilot is in its combat state (not while taking off, taxiing or landing).

---

## Camera

- Free camera with 3D unit selection.
- **Auto centre and follow**: selecting a unit snaps the camera to it and follows it. Selecting
  several units frames the whole group so a convoy fits on screen. Toggle in
  **Settings → Gameplay → Command**.
- `Space` centres on the selection; hold it to centre and follow.
- **Camera bookmarks**: `Ctrl` + `F1`–`F4` stores the current viewpoint, `F1`–`F4` jumps back to
  it. Useful for your front line, your airbase and your carrier. Toggle in
  **Settings → Gameplay**.
- POV camera attached to the selected unit, with snapping to the head position of available
  crew members.

## Map

- Movable tactical minimap, kept open alongside the RTS UI.
- **Resizable**: drag the grip in the bottom-right corner of the map. The size is saved.
- `LMB` clicks icons, drag `LMB` (or `MMB`) to pan the map, `Ctrl` + drag `LMB` draws a
  selection box, and the base-game zoom and keyboard-pan bindings still work.
- `RMB` sets travel points and attack orders, same rules as the 3D view.
- Selected units draw their full remaining route on the map, numbered in the order they will
  drive it.
- Radar coverage overlay generated from Unit Systems.

---

## Production and logistics

### Depot spawning

- Buy ground units with the faction money pool, or deploy vehicles from the faction reserve.
- Vehicles are grouped by their base-game categories.
- Optional rally points for spawned vehicles, plus a spawn queue.
- **Reinforce group**: set a depot to put every unit it builds straight into a control group,
  so a battlegroup rebuilds itself without re-boxing it every time.
- Faction reserve system that holds certain unit types back after a factory produces them.

### Supply heli

- Custom supply runs with cargo-capable helicopters and configurable cargo loadouts.
- Landing deliveries or parachute airdrops, with landing-zone selection in 3D.

### Naval

- Purchasable naval units and naval resupply missions for selected ships.

### Air Command

Dispatch aircraft with custom loadouts on a specific mission:

- **Air Superiority** and **AWACS / Jammer** stay inside their assigned area (blue circle) but
  engage anything in range.
- **CAS**, **ARAD** and **Strike** only attack targets inside their assigned area (red circle).
- ARAD supports saturation attacks; missions in progress can be edited, relocated or recalled.
- Aircraft come from the faction reserve when possible, otherwise they are purchased. Aircraft
  that return successfully restore the airframe or refund the money.

## Enemy commander

Off by default. Cycle it in **Settings → Gameplay**.

The base game only ever deploys the fixed vehicle reserve a mission was authored with — no
opposing faction ever spends money. Turning this on gives every hostile faction a commander that
reviews its funds every 30 seconds, buys reinforcements into its reserve, and shapes the buy
against what you are fielding: if you are flying a lot, it buys air defence.

- **Cautious** — one unit per review, a quarter of the surplus.
- **Standard** — two units per review.
- **Aggressive** — four units per review, half the surplus, plus a small income stipend for the
  enemy faction. This one is deliberately a cheat.

Bought units are deployed and driven by the base game's own depot and ground AI, so they behave
like any other enemy convoy. Requires advanced features, and only runs in singleplayer or when
hosting.

## Unit systems

- Toggle compatible radar systems on or off, and show radar coverage on the map at an
  adjustable target altitude.
- Force Jacknifes to repair the nearest valid target instead of the highest-priority one.
- Relocate containers using nearby tractors or flatbeds.

## Experimental

- Heavily WIP tool for automatically building SAM sites via Jacknifes, with landing pads for
  ammo deliveries. Intended to be used later by the enemy AI for balancing purposes.

---

## Notes and known limits

- Advanced features (production, Air Command, supply, naval, SAM analyzer) are gated to large
  strategic missions. Other missions start in **core mode**, which still has camera, selection,
  groups, routes and attack orders. `CMD → UNLOCK ALL FEATURES` overrides the gate, but it can
  break missions that were not built for it.
- The mod is client-side and issues the same networked orders a player would; it is not a
  server plugin.
- Combat alerts for units taking fire come from a server-side code path, so they work in
  singleplayer and when hosting. A pure multiplayer client still sees losses and arrivals.
- Only structures are repairable in the base game, so a retreating vehicle goes to a Jacknife or
  rearm truck to top up ammo rather than to heal.
- Automatic retreat only triggers for units that currently hold a RTS order. The RETREAT
  button works on anything.
- Nothing here is balanced yet.

## Building from source

Requires the .NET SDK and a Nuclear Option installation with BepInEx.

```bash
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option"
```

`GameDir` can also come from a `NUCLEAR_OPTION_DIR` environment variable. The output DLL lands
in `bin/Release/net472/` and goes into `BepInEx/plugins/GroundControlRts/`.

See [CLAUDE.md](CLAUDE.md) for the architecture notes and [CHANGELOG.md](CHANGELOG.md) for the
release history.
