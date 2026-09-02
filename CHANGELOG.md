# Changelog

## Unreleased

### Fixed

- **World markers no longer cover what they mark.** Every marker drawn over the 3D view - the
  attack marker most of all - was a filled dark plate centred on the point, so ordering a unit
  to attack put a black box on top of the enemy you were attacking. Markers are now open corner
  brackets that frame the point with the label floating above them: the attack bracket is sized
  to frame the target, travel points get a small one, and the middle is left clear. The order
  flash uses the same bracket, so the click, the route and the target all read as one thing.
- **Marker and route colours are the colours they were meant to be.** Lines and markers were
  painted with the green accent texture and tinted on top of it, so an orange attack route came
  out olive and every colour was pulled toward green.

- **3D-view route lines join the travel points they belong to.** The lines were rotated around
  the wrong pivot whenever the UI scale was not exactly 1, which is every resolution preset
  except one, so they hung in the air well away from the numbered points at either end.
- **Travel point numbers are visible again.** The numbered chips rendered as empty plates: the
  panel style's padding squeezed the glyph out of an 18px chip, and the number was tinted the
  same colour as the plate behind it.

- **Combat alert toasts no longer cover the funds readout.** The toast stack was pinned to the
  top-centre of the screen, on top of the faction funds display. Alerts now live in their own
  `ALERTS` window in the top-right corner that you can drag anywhere; **Settings → Reset UI
  layout** returns it to the corner.

- **Route lines are drawn on the map, not over it.** A multi-point route showed as lines painted
  on top of the tactical map from the 3D camera's point of view, so they ran nowhere near the
  waypoints and swung across the map as the camera turned, panned or zoomed. Route legs are now
  real map objects in the game's own icon layer: they sit on the terrain they belong to, pan and
  zoom with the map, keep a constant line width, and clip at the map edge. The 3D-view route
  lines are hidden while the map is up.

- **Multi-point routes actually get driven.** A unit that reached the first travel point of a
  queued route had its whole order thrown away and stopped there. It now carries on to the next
  point, and the one after that, until the route is finished (or loops, if it is a patrol).
  This was the single bug behind "the unit just moves to point 1 and stops".
- **Route lines no longer break apart.** A leg with one end behind the camera used to be
  dropped entirely, so a route looked like scattered unconnected markers. Legs are now clipped
  against the camera instead of discarded, travel points draw as small numbered chips instead
  of full marker plates, and the numbering matches the points you placed.
- **Attack orders work on anything you can see.** Ordering an attack used to need a physics
  raycast to land on the target, which almost never happened for aircraft or distant contacts —
  the click looked like it did nothing. The order now resolves against the world marker under
  the cursor first, so right-clicking an enemy marker (aircraft included) issues the attack.
- **Dragging the map no longer draws a selection box.** On the map a plain left drag pans, the
  same as the base game; hold the new **map box-select** key (Ctrl by default) to drag a
  selection box instead. Map icons now select on release, so grabbing the map to pan it does
  not also select whatever was under the cursor. The 3D view is unchanged: a plain left drag
  still boxes there.
- **RTS windows no longer leak clicks into the map.** Clicking a row in Order of Battle while
  the fullscreen map was open panned the map underneath the window.
- **Clicking a unit in a list shows you the unit.** Order of Battle rows, Air Command rows and
  battle-log rows now snap the camera onto the unit and follow it, instead of leaving you to
  press CENTER afterwards.

### Added

- **The selection bar shows a selected unit’s loadout.** Select one unit and the bar lists every
  weapon it carries by name with the rounds remaining - `R-27ER   2 / 4` - one row per weapon
  type, two rows across, and the bar grows to fit. Aircraft also get a `FUEL %` reading next to
  condition and ammo. A multi-unit selection still shows the type chips instead, and its
  condition, ammo and fuel readings are the average across the selection.
- `Project_plan.md` — design notes for work that has not been built yet, starting with
  **ballistic strike calls** (call for fire on a map point).

### Changed

- **New UI look.** Flat near-black translucent plates, one accent hairline instead of neon fill
  everywhere, and much lighter text — the old green-on-green buttons were hard to read. Panel
  edges now fade out into the scene rather than ending on a hard rectangle.

- **Service lifecycle is now a registry instead of six hand-written lists.**
  `CommanderModeController` used to repeat every service by name in its fields, its
  constructor, its tick, its activate, its deactivate and its scene reset — six places to keep
  in sync, and some services were already missing from one of them. Services now implement the
  small interfaces in `Core/ICommanderService.cs` and are registered once, so adding a feature
  is a single `services.Register(...)` line and registration order is the whole per-frame
  schedule. What is left in the controller is the camera/cursor takeover and the draw pass.
  `CommanderPersistentOperations` — a class whose only job was forwarding one method to seven
  services — is deleted.
- `CommanderServiceRegistryCheck` runs at plugin load and logs to the BepInEx console if the
  service ordering or the core/advanced gating ever breaks.
- The three largest files are split into `partial class` files named after the concern they
  own: `CommanderSamSiteAnalyzerService` (2890 lines → 7 files), `CommanderOverlayUi`
  (2001 → 5), `CommanderAirCommandService` (1949 → 6). No behaviour changed; nothing in the
  repo is over 1800 lines now.

No gameplay changes in any of the above.

- **The mod is now called Ground Control (RTS).** It was NOCommander / RTS-Commander. The
  plugin DLL is `GroundControlRts.dll` and it lives in
  `BepInEx/plugins/GroundControlRts/`. **Delete the old `NuclearOptionCommander` plugin folder**
  or BepInEx will load both copies and every Harmony patch will run twice.
- The BepInEx plugin id changed to `com.groundcontrol.rts`, so settings start from defaults.
  Old settings are still in `BepInEx/config/com.nuclearoption.commander.cfg` if you want to
  copy keybinds across by hand.

## 0.4.0.0 — Real orders, and someone to use them against

### Added

- **Attack-move.** A unit on the Free Fire stance that passes within its own weapon range of a
  hostile while travelling breaks off, engages it, and resumes the route when the target is dead
  or has broken contact. This is on by default and can be turned off in **Settings → Gameplay**.
- **Patrol routes.** `PATROL` in the selection bar turns a multi-point route into a loop. The
  unit keeps walking it while you are away flying, instead of parking at the last point.
- **Guard / escort orders.** `RMB` on a friendly unit, in the 3D view or on the map, tells the
  selection to escort it in formation and engage whatever shoots at it.
- **Stances.** The selection bar cycles Free Fire, **Hold Fire** (turrets acquire nothing, so a
  unit can sit dark near a SAM belt) and **Hold Pos** (holds its ground, still shoots).
- **Retreat to repair / rearm.** `RETREAT` sends the selection to the nearest friendly vehicle
  that can repair or rearm it. Optionally automatic below a condition threshold.
- **Real formations.** Ring, line, column and wedge, oriented along the direction of travel, so
  a group arrives facing the right way. Column matters on roads. Cycled from the selection bar.
- **Arrive together.** A unit more than the cohesion distance ahead of the rearmost member of
  its order waits for it, so a group no longer strings out along the route.
- **Waypoint actions.** The `WP` button attaches an action to the next travel point you place:
  hold for a set time, radar off, or radar on. EMCON at a waypoint means a battery can drive to
  its firing position dark and only light up where you tell it to.
- **Combat alerts.** `GROUP 3 UNDER ATTACK`, losses, kills and arrivals appear as clickable
  toasts, **including while you are flying**. Clicking one selects the unit and jumps to it.
- **Battle log.** A `LOG` tab in the Order of Battle listing kills, losses and arrivals with
  mission timestamps. Click an entry to jump to the unit.
- **Unit condition and ammo.** The selection bar shows condition, ammo and the current order for
  the selection; the Order of Battle shows condition and ammo per row.
- **Reinforce a control group.** A depot can be set to put every unit it builds straight into a
  control group, so a battlegroup rebuilds itself without re-boxing it.
- **Enemy commander AI.** An opposing commander that spends its faction's funds on
  reinforcements and shapes the buy against what you field — the base game only ever deploys the
  fixed reserve a mission was authored with and never buys anything. Off by default; cycle
  Cautious / Standard / Aggressive in **Settings → Gameplay**. Aggressive also gives the enemy
  faction a small income stipend, deliberately.

### Changed

- The selection bar has a second row of order buttons and a condition / ammo / order readout.
- Attack orders, guard orders and attack-move all share the same re-tracking and focus fire.
- Order of Battle rows carry condition and ammo, and the window is slightly wider to fit them.
- **New UI theme.** Every RTS window, panel, button, toggle, scrollbar and slider now uses
  a dark plate with a hairline neon-green edge and a subtle gradient, and windows have their own
  title bar band. Scrollbars lost their arrow buttons and are now thin rails. Layout is unchanged.

### Known limits

- Alerts for units taking fire come from a server-side code path, so they work in singleplayer
  and when hosting. A pure multiplayer client still gets loss and arrival entries.
- Automatic retreat only triggers for units that currently hold a RTS order; the
  `RETREAT` button works on anything.
- The enemy commander only runs where advanced features are enabled (the large strategic
  missions, or after unlocking them manually).

## 0.3.0.0 — Command quality of life

### Added

- **Stop hotkey** (`X` by default). STOP was previously only a button in the selection bar.
- **Select by type.** Double-clicking a unit selects every unit of that type currently on
  screen; the same-type key (`Ctrl` by default) plus a click selects every one the faction
  owns. Holding the add-selection key extends the current selection instead of replacing it.
- **Shift-click removes a unit from the selection** instead of doing nothing, so one wrong
  unit in a box no longer means starting the box again.
- **Selection type chips.** A selection of more than one unit lists one chip per unit type in
  the selection bar. Click a chip to narrow the selection to that type, or hold the
  add-selection key and click to drop that type.
- **Idle unit cycling** (`.` by default): selects and jumps the camera to the next friendly
  ground or naval unit that holds no RTS order. Units held by STOP are skipped.
- **Camera bookmarks.** `Ctrl` + `F1`-`F4` stores the current viewpoint, `F1`-`F4` jumps back
  to it. Bookmarks are cleared on a mission change.
- **Order feedback.** Every travel point and attack order flashes a contracting marker at the
  ordered point, in the 3D view and on the tactical map, so a swallowed click is visible.
- **Shortcut reference** (`CMD → Settings → SHORTCUTS`): a scrollable, read-only list of every
  shortcut in the mod, grouped by camera / selection / control groups / orders / production /
  interface. Keys are read live, so a rebound key shows its new value, and the list also covers
  the shortcuts that are not remappable: control groups `1`-`9`, camera bookmarks `F1`-`F4`,
  double-click select-same-type and the selection-bar type chips.
- New settings: camera bookmarks, order feedback, keep attacking after the target dies. New
  bindings: stop order, same type, cycle idle.

### Changed

- **Attack orders survive their target.** When the commanded target is destroyed, the attackers
  now pick the nearest hostile the faction can currently see inside their own weapon range and
  keep the order. If nothing is in reach they hold the ground they took instead of reverting to
  Basegame tasking and driving away. Toggleable in Settings > Gameplay.
- The selection bar grows to fit the type chips when more than one unit is selected.
- Commander panel and selection bar help text updated for the new gestures and hotkeys.

## 0.2.0.0 — Command overhaul

### Added

- **Order of Battle window** (`CMD → ORDER OF BATTLE`): every unit the faction owns in one
  list, filtered by ground / air / naval / structures, plus tracked hostiles. Select one,
  select all, or recall a control group from the same window.
- **Control groups 1-9.** `Ctrl` + number stores the selection, number recalls it, `Shift` +
  number adds it to the current selection. Group membership is shown in the selection bar and
  as a badge in the Order of Battle. Any order given to a selection applies to the whole group.
- **Multi-point travel routes.** Holding the queue key (`Shift`) while right-clicking appends
  travel points, and units walk them in order. Routes are drawn as numbered markers and lines
  in the 3D view and on the tactical map, and keep running after you leave RTS mode.
- **Box selection**, by dragging the left mouse button in the 3D view and on the map. A box
  that catches any friendly unit selects only friendlies.
- **Attack orders.** Right-clicking a hostile unit, building or objective orders the selection
  to attack it, including targets that cannot normally be selected. A queued route whose last
  point lands on a hostile becomes an approach followed by an attack. Attackers stop at roughly
  70 % of their own weapon range (toggleable) and their turrets keep the commanded target
  instead of scattering, and the order re-tracks a target that moves.
- **Aircraft orders.** Selected friendly AI aircraft take the same travel points and attack
  orders, executed through the Air Command mission layer. Ordering an untasked aircraft tasks
  it automatically, choosing Air Superiority for air targets and CAS for surface targets.
- **Tasking aircraft that are already in the air**: `Air Command → AIR MISSIONS → IDLE` lists
  every untasked friendly AI aircraft with a `TASK` button that places a mission area for it,
  exactly like a freshly spawned mission.
- **Resizable tactical map**: drag the grip in the bottom-right corner. The size is saved.
- **Camera auto centre and follow on selection.** Selecting several units frames the whole
  group so a convoy fits on screen. Toggleable in Settings → Gameplay → Command.
- New settings: auto follow, control-group hotkeys, attack-order standoff, Order of Battle
  visibility, saved tactical map size. New bindings: queue travel point, assign control group.

### Changed

- RTS mode now owns tactical map input. Left button clicks icons and drags selection
  boxes, **middle button drags the map**, right button issues orders. Zoom, keyboard panning
  and jump-to-map keep their base-game bindings.
- Right-click orders replace the base game's map order, which only ever sent every selected
  unit to the single last waypoint.
- Selection resolves on mouse release rather than press, so a drag can become a box.
- Multiple selected units still spread into a formation, now around every point of a route.
- A unit that makes no progress toward a travel point for 60 s skips it instead of stalling
  the rest of the route.
- Order re-issues are rate limited to destination changes over 40 m, cutting networked command
  traffic for chasing and formation orders.
- Help text in the Commander panel, selection bar and tactical map updated for the new orders.

### Documentation

- `README.md` rewritten around the command features, with a quick-start binding table.
- `CLAUDE.md` added: architecture, base-game API facts, patch conventions, build instructions
  and a pre-commit checklist for future work.
- This changelog added.

## 0.1.2.0 and earlier

Free camera and 3D unit selection, single-destination move orders, unit pinning, depot
spawning with the faction reserve and rally points, supply helicopter missions, Air Command
mission types and loadout editor, naval purchases, radar and repair unit systems, the
experimental SAM site analyzer and builder, and the movable tactical minimap.
