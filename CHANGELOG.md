# Changelog

## Unreleased

### Fixed

- **Gold mine income never showed up in the faction balance — it was being paid out to the personal
  account.** The game hands every pilot a share of the faction's money every 30 seconds: their
  mission income, plus a quarter of everything the faction is holding *above the balance the mission
  started with*. Under a commander that second part is a hole in the treasury — the mines paid in,
  and the next payout took a slice straight back out to the personal wallet, so a balance that
  should have been climbing sat still. The faction's treasury is no longer counted as spare cash, so
  mine income stays where you earned it. Your own flying allowance is untouched.

- **Mine income read as a bare number.** `+20/min` looked like twenty dollars. Income is now written
  in the same money units as everything else — `+$20.0m/min` — in the build window, the mine's
  upgrade card, and the economy readout. The amount has not changed.

- **The economy, base capture and the enemy commander all kept running while the game was paused.**
  Every periodic job in the mod was on the wall clock instead of the game clock, so pausing froze the
  battlefield and nothing else: mines kept paying, bases kept falling, the enemy kept shopping. Mod
  logic now runs on game time — it stops dead when you pause, and it runs at 2x/4x with the CMD
  panel's speed buttons. Panels and markers still refresh while paused, so you can still look around
  and click things.

- **Newly bought ground vehicles drove off at the enemy on their own.** A vehicle that has never had
  a *player* order steers itself at the nearest objective or tracked enemy, and the depot's own
  "roll off the ramp" nudge does not count as one. Units bought from a depot now form up in a
  staging block beside it and wait for orders. Setting a rally point still overrides this; clearing
  one puts staging back rather than turning it off.

- **Air Command let you place a mission area you could not pay for.** The affordability check ran
  after the target was picked, so the map opened, the area went down, and nothing happened. The
  REQUEST MISSION button now refuses up front and says the price and your balance.

- **Warships arrived on the far side of the map from the dock that paid for them.** A hull enters
  the map along a sea lane, and the lane was picked from a band around the map edge and scored by
  how close it was to your nearest *airbase*. On a map whose coast runs away from your bases that
  put a fresh patrol boat an hour's sailing from the harbour. Ships now enter at the sea lane
  nearest your naval dock, from anywhere on the map rather than only the edge band. A faction with
  no dock cannot buy ships at all, so nothing else changes.

- **The enemy commander built a gold mine on the landing strip.** Runways and taxiways are terrain,
  not structures, so nothing was stopping a building being dropped straight onto one — and the
  road check could not see them either, because an airfield's taxiways are its own network and not
  part of the map's roads. Neither commander can now build on a runway or a taxiway, at any airbase
  on the map, held or not. The build ghost turns red and says *that is a runway or taxiway*.

- **Aircraft told to take a base flew over it and went home.** A travel point is a place to be, not
  a place to land, so an aircraft handed the ground squad's hold point did exactly what it was
  told. Ordering aircraft onto a capturable base is now a real landing order — see *Aircraft can
  take a base* below.

- **The enemy commander bought two ground-attack jets at the start of the match and then nothing
  else, all game.** It always bought the most expensive airframe its strip would accept, so the
  answer was always the same aeroplane; once its air fund could not clear that price again it
  bought nothing at all and said nothing about it. It now composes a wing by role and starts
  cheap — see *The enemy flies a mixed wing* below. **It also now writes a line to the log every
  time it declines to buy an aircraft, saying why** (at its ceiling, no airbase, cannot afford the
  cheapest thing its strips accept, nothing with an AI flight model), once per reason rather than
  twice a minute. An air force that silently stops was indistinguishable from one that was broken.

- **The enemy's aircraft crashed a few metres from the hangar, over and over.** They were being
  spawned in a hangar and left to taxi and take off on their own, and the game's AI pilot cannot do
  that on this map: a highway strip has no taxiways, so the pilot drives a straight line at the
  runway and the taxi and takeoff states answer *any* trouble at all — a stuck moment, a scrape, a
  wing that touches something — by ejecting the pilot and abandoning the aeroplane. You never see
  this yourself because you fly your own aircraft off the strip by hand. Commander-launched AI
  aircraft now enter the map already airborne over their own base, pointed at the enemy and at
  flying speed, which is exactly how a mission spawns aircraft that start in the air. **This
  applies to the aircraft your own AIR window buys too** — same hangar, same problem. Which
  airframes a base offers has not changed.

- **The naval dock could not be placed anywhere, however close to the water you stood.** The water
  test compared a world height against a *camera-relative* sea level, and the game slides that
  reference around as the camera moves — so the moment the RTS camera gained any altitude, every
  probe decided there was no water anywhere on the map. Dry land and open sea now read correctly
  regardless of where the camera is. The same mistake was quietly breaking the enemy commander's
  dock siting and the capture squad's hold point at a coastal airfield.

- **The enemy commander only ever built gold mines.** Two commanders spend the one faction balance
  — one buys units, one buys buildings — and the unit spender took a fixed share of the balance
  every review, so the balance never once climbed to a factory's or a dock's price after the
  opening minutes. Whatever the economy is saving for is now held back from the unit spender until
  it is bought, the same way airframes and warships are already saved for. The enemy now works
  through mines, then factories, then a naval dock and its upgrades.

- **The expansion priority self-check was failing at load.** An empty base at the very edge of the
  enemy commander's reach only tied with a defended base underfoot instead of beating it, so the
  commander could throw its capture squad at your airbase rather than walk onto a free one.

- **The enemy's aircraft took off and flew straight into the ground.** It was buying VTOLs — a
  Tarantula is the most expensive thing a highway strip will accept, and the buy loop always took
  the dearest airframe the strip allowed. Everything the mod does to steer a commanded aircraft is
  built on the game's *fixed-wing* pilot AI; helicopters and VTOLs run a completely different one,
  so the mission told a Tarantula what to attack and then nothing flew it there. It nosed over
  shortly after takeoff every time. No rotary or VTOL airframe can be given an Air Command mission
  from either side any more — your AIR window already worked this way, the enemy's buy loop did
  not. (The enemy was also stopped from buying helicopters outright at the time; that half has
  since been reversed — see *The enemy flies helicopters now* below.)

- **A capture squad drove into the terminal building over and over.** Capture orders aimed at the
  airbase's centre point, which on a real airfield sits on a building, so the units rammed it,
  reversed, and rammed it again forever. Standing anywhere inside the capture ring takes the base,
  so the squad is now sent to open ground inside the ring instead.

- **Units sent to capture a base could not be ordered anywhere else.** Any order given within the
  capture ring (plus a bit) counted as another capture order, so a squad standing on a base had
  every attempt to move it snapped straight back to where it was. Ordering a squad that is already
  taking a base is now read as a redirect and obeyed literally, with a LEAVING <BASE> toast so you
  can see the click landed. Adding fresh units to the selection still reads as reinforcement.

- **The enemy commander parked buildings on its own roads.** Two things were wrong. The road check
  measured the building by the size written on its data sheet, which for most structures is far
  smaller than the building or not filled in at all — a refinery was being treated as ten metres
  across. And the cheap "is this site anywhere near this road" test used the road's own bounding
  box with no margin, which for a straight road is a line: every site beside it skipped the check
  entirely. Buildings are now measured off the actual model, and the road test reaches out by the
  clearance being asked for.

- **The enemy commander stopped flying after the first few minutes.** It set aside a fixed share of
  each review's balance for aircraft, and then spent the rest on ground vehicles — which kept the
  balance low enough that the air share never once added up to an airframe's price again. Twenty
  minutes in you were fighting an enemy with excellent convoys and an empty sky. The air share is
  now *saved* between reviews instead of expiring with them, so it buys an aircraft as soon as it
  can afford one. Ships are bought out of a second saved fund the same way. Either fund hands its
  surplus back to the ground spender after a few reviews, so a faction that can never put anything
  up does not quietly withhold money from its convoys forever.

- **The enemy commander never flew an airstrike.** Even when it did buy an aircraft, nothing told
  the aircraft what to do — and the game's own pilot AI lands after fifteen ticks with no target
  found, which is most of the way to the player's base. Every airframe the enemy owns on the duel
  now gets a real Air Command mission out of the same machinery your own aircraft use: a strategic
  strike box over your territory, with one in three flying air superiority instead once you have
  aircraft of your own up. This is duel-only. Every other mission launches its own AI aircraft and
  may script what they do, and overriding that is not a bug fix.

### Added

- **Aircraft can take a base.** Put a travel point on a yellow capture marker with aircraft
  selected and they fly to that airfield, land on it, and sit in the ring until it falls — then
  take off again on their own. Give them any other order and they take off immediately. An
  aircraft parked inside a ring is worth about a light vehicle to the capture (tunable:
  Gameplay/AircraftCaptureStrength), because in the base game an aeroplane contributes nothing to a
  capture at all unless it happens to be carrying a troop pod. The base game will also not land an
  AI aircraft anywhere except a field its own faction already holds, and it ejects the pilot of
  anything left standing still on a strange airfield — both are worked around, so an aircraft
  ordered onto a neutral field actually arrives and actually stays.

- **RESUPPLY, on the selection bar.** Select aircraft and press it and they fly to the nearest
  airbase your faction holds and land. Landing is how the game recovers an airframe: it goes back
  into stock with its cost refunded, ready to relaunch fully armed and fuelled. The route to the
  field it has chosen is drawn as the same yellow travel line every other order gets, so you can
  see where each one is going. Right-clicking aircraft onto a base you already own does the same
  thing — an order dropped on your own airfield is read as a rearm run.

- **The enemy flies a mixed wing.** Instead of one airframe repeated, the commander picks what the
  wing is short of: air superiority the moment you put an aircraft up and it has no fighter,
  a couple of transports while you have an army on the ground, ground attack the rest of the time.
  Within a role it buys the *cheapest* airframe that fits until it is running two of them and only
  then starts spending up — so the opening minutes are cheap light aircraft and the expensive
  ground-attack jets arrive once its economy can carry them. Roles are read off the game's own
  role data, not a list of aircraft names, so a patch that adds an aeroplane files it correctly.
  The airborne ceiling went from four to eight, since four is one of each role and no depth.
  Helicopters and tiltwings count, so the transports are real ones.

- **The enemy flies helicopters now.** They were banned outright, which was aimed at the right
  problem and hit the wrong target: what breaks a rotary airframe is being given an Air Command
  mission (it gets the target half and nothing that flies it there), and that is already refused
  for anything that is not an aeroplane. Left alone, the game's own helicopter AI is complete — it
  finds targets, flies to them, and hands itself over to fly a transport run whenever it is
  carrying cargo, which is the game placing troops for the enemy with no help from the mod. The one
  thing still refused is an airframe whose pilot the base game gives no AI flight state to at all,
  which would simply fall out of the sky. Which aircraft that covers is checked against the actual
  aircraft at runtime rather than assumed from its name.

- **The whole airframe list, in the log, once per mission.** Pilot type, role and price for every
  aircraft each faction can buy, with anything the commander refuses to buy marked and the reason
  given. All three are in the game's asset files rather than its code, so this is the only way to
  see what the AI is actually choosing between — and the only way to catch the mod excluding an
  aircraft it should not.

- **A toast when any base changes hands.** CAPTURED / LOST / <FACTION> TOOK / NEUTRAL, raised the
  moment the airfield flips, for every base on the map and both sides of the fight. It used to be a
  line in the battle log you were not looking at.

- **A capture progress bar.** A base being taken now shows how far along it is right on its marker,
  in the 3D view and on the tactical map — `CAPTURING MARIS AIRPORT [####------] 40%` in green when
  it is going your way, `CONTESTED` in red when it is not. The base game shows this nowhere outside
  its debug overlay, so a squad standing in the ring used to look like a squad doing nothing.

- **A countdown on factories.** Selecting a factory now says how long until its next batch and how
  long a production run takes — `NEXT 2 x AGM IN 3:12 (EVERY 4:00)` — beside the upgrade button.
  The old readout said `1/cycle` without ever saying how long a cycle was.

- **Naval docks, and a naval gate to go with them.** Nobody buys ships any more without one — you
  or the enemy. A dock is built from the BUILD window, has to stand on dry land at the water's
  edge, and may sit further from your bases than anything else you build (its own radius, 12 km by
  default, because the coast usually is). It upgrades three times and each level opens a heavier
  class of hull: patrol boats and landing craft, then corvettes and frigates, then destroyers,
  carriers and assault ships. Locked hulls stay visible in the naval window with the dock level
  they need, so the ladder reads as something to build toward.

- **The enemy commander goes to sea.** It builds its own dock on the nearest coast to a base it
  holds, upgrades it, and buys hulls under exactly the same level gate you are on, entering them
  from the map's sea lanes the way your purchases do. It never put a boat in the water before.

- **A radar screen instead of a blind enemy.** The enemy has always been handed the location of
  your *buildings* — without that it has nothing to attack — but nothing about your army. It now
  buys radar vehicles and drives them out to standing overwatch posts on the approaches from your
  territory, picking the highest ground near each post, and it is short of a radar before it is
  short of anything else in its plan. Everything it sees that way, it sees because a truck is
  parked somewhere you can shoot it.

- **Game speed in the commander panel: 1x, 2x, 4x.** An RTS spends a lot of its time watching a
  convoy cross a map. Host only — on a multiplayer client the clock belongs to the server — and it
  drops back to 1x when you leave commander mode, so nothing carries a fast-forward into flying or
  into the next mission.

- **Build radius and naval dock radius are sliders** in Settings > Gameplay, not just config file
  entries. Both are map-dependent: how tight a base perimeter feels, and whether a faction can
  reach the coast at all, are answers you only get by looking at the map you are on.

### Changed

- **The enemy commander strikes your main base, and keeps fighters over its own.** Its strike target
  was the *average* position of every airbase you hold — fine while you hold one, useless the moment
  you capture a second, because the target slides off into open ground between them and the strike
  package finds nothing to bomb. It now remembers the base you started the mission holding and works
  that, from the first minute. One airframe in three is also held back on a combat air patrol over
  its own ground instead of being sent to your base, so its mines and factories are defended and you
  are met on the way in.

- **The enemy's aircraft losses are logged.** Every airframe that leaves the world writes a line
  saying how long it lasted. Twenty-six launches and no airstrike looked identical in the log to
  twenty-six aeroplanes shot down on the way in; now it does not.

- **The CAPTURE button is gone from the commander panel.** Capturing is an ordinary order: drop a
  travel point on the yellow capture marker, in the 3D view or on the map, and the selection goes
  and takes the base — as the last point of a route if you like. The button only ever did the same
  thing to the nearest target, and having it there hid the fact that any order can be a capture.

- **The build radius is 2.5 km, down from 7 km.** Bases are compact now; industry sits inside the
  perimeter you are actually defending instead of sprawling most of the way to the enemy. The
  naval dock is the one exception and keeps its own, larger radius.

### Fixed

- **The CAPTURE button did nothing and did not say why.** It refused outright when nothing in the
  selection carried troops, and the refusal was written to a status line that is not drawn
  anywhere — so pressing it with an ordinary vehicle selected looked like a dead button. It now
  always issues the order and tells you on screen how many of the selected units can actually take
  ground, rather than silently deciding for you. Which vehicles those are is also named once per
  mission in the BepInEx console, because that fact lives in the game's asset files and cannot be
  read any other way.

- **Aircraft nobody bought no longer show up.** The Ground Control Duel handed each faction a
  free AI air force — the mission's own `AIAircraftLimit`, which the game tops up automatically —
  so two aircraft were already flying before you had spent anything, and the enemy's were picked
  at random from the whole aircraft list regardless of whether the only airbase on its side could
  handle them. That is where the aircraft that "crashed" in the first minute of a round came from.
  Both sides now start with an empty sky: every aircraft in the duel is one a commander paid for.
  Yours come from the AIR window; the enemy's are bought and launched one at a time, from an
  airbase picked first so it never buys an airframe its strip cannot take. The duel's authored
  aircraft stock is zero on both sides for the same reason — an AI aircraft either side puts up
  now costs money, which is what makes it an economy duel. Aircraft **you** fly yourself are
  untouched: those come out of your own allocation, as always.

- **Aircraft fly the order you gave them.** Telling an aircraft to go somewhere and watching it
  turn round and land at home with most of a tank left was the game's own idle timer: an AI pilot
  that goes fifteen ticks without a target lands, and the mod was writing the commanded
  destination *after* that decision had already been taken. A commanded aircraft is no longer
  counted as idle, and running its racks dry no longer ends the order either — it finishes the
  travel points first. Genuinely low fuel still sends it home, as it should.

### Added

- **Capturing bases, for both commanders.** Taking an airbase in Nuclear Option just means
  standing a unit that carries troops inside the base's capture ring — but nobody was ever telling
  units to go and do it. Now:
  - **Capturable bases are marked on the map once you have found one.** The base game draws no map
    icon at all for an airbase you do not own, so there was nothing to aim at. Now any capturable
    base a unit of yours has been near is marked `CAPTURABLE <name>` — on the tactical map while it
    is open, in the 3D view while it is not — in yellow when nobody holds it and orange when
    somebody does. Finding one is announced in the battle log.

    Finding it is the condition: fly or drive within range and it appears, and then it **stays**
    marked for the rest of the mission whether or not anything of yours is still nearby, because an
    airfield does not move. Aircraft find bases from 12 km, ground units from 4 km. Bases you have
    not found behave like ordinary ground, so you cannot capture-order something you have not seen.
  - **Right-click a base you do not own and the selected units go and take it** — in the 3D view or
    on the tactical map. It is an ordinary order, so it composes with everything else: queue travel
    points across the map and make the last one a base, and the route ends in a capture. The order
    snaps to the middle of the ring, so units stop somewhere that actually captures instead of
    wherever the cursor happened to land, which on a zoomed-out map can be a kilometre out.
  - The main CMD panel also has a **CAPTURE** button naming the nearest base you could take and how
    far away it is, as a shortcut for the common case.
  - **The enemy commander expands.** Every twenty seconds it picks the nearest base nobody holds,
    commits up to three of its capture-capable units, and keeps them pointed at the ring until the
    base is its. If it owns nothing that can take ground, buying one jumps the queue ahead of
    whatever its plan wanted — an expansion with no troops is an expansion that never happens.
    Empty bases always outrank defended ones, however far away they are.
  - Captures by either side land in the battle log.
  - **The duel map now has bases to take.** Maris Airport, Sandrift Airbase and South Boscali
    General Aviation are switched on as neutral, capturable ground between the two strips. Taking
    one gives you a new place to launch from and a new 7 km circle to build in — and, with the new
    lose condition, one more base the other side has to take off you before you are out.

    The map's stock airbases are not laid out symmetrically, so this is a compromise rather than a
    mirror: Maris is 9 km from the Boscali strip while Primeva's nearest two are 18 and 23 km. Say
    if it plays lopsided and the set is one line to change.

- **Win and lose conditions.** A faction left holding no airbase loses the match outright, and
  everyone else wins it. This runs on every mission, not just the duel, and does not depend on
  the mission author having written a capture objective for each base.

- **Buildings must be built near a base you hold.** Both commanders can only place structures
  within 7 km of an airbase their faction owns, so capturing ground is what opens up new places
  to build. The ghost turns red and says so outside the radius. The distance is
  `Economy / BuildRadiusKm` in the config file.

- **The enemy commander obeys the same siting rules you do.** It used to drop mines and factories
  wherever its dice landed, including across the highway — which is what left its own convoys
  stuck against a building and its taxiing aircraft driving into one. It now checks each candidate
  site against the same road and collision rules the player's build preview enforces, and tries
  another spot when one is blocked.

### Added

- **Ground Control Duel now has an opponent that plays.** The enemy commander used to be off
  until you found it in the settings, and on the duel map that meant nobody ever attacked you.
  It now runs on that mission whether or not the setting is on (the button reads `(MISSION)`),
  and it plays harder there than anywhere else:
  - **Starts the moment the match does.** Both enemy reviews used to be able to burn their first
    turn in the menu, so the opponent's first purchase and first gold mine could land half a
    minute into the match. They now wait for a mission instead of a clock.
  - Opens with half again its starting balance, and builds up to four gold mines and two
    factories instead of two and one.
  - Spends 45% of its pot every 30 seconds on up to five vehicles, so its depots keep pushing
    convoys out instead of trickling.
  - Buys and launches its own aircraft, one at a time and only types the airbase it is launching
    from can actually take, so the air raids keep coming without anything writing itself off on
    a highway strip.
  - **Knows where your base is.** Every building you own is on its map the moment you place it,
    which is what aims its convoys and its strike aircraft at you — the game's ground AI drives
    at the nearest enemy it knows about, and its pilots only ever shoot at what their faction has
    tracked. Your vehicles and aircraft stay unrevealed: it knows the address, not your army.

  Everything past the opening balance is still earned at your rates, so killing its convoys and
  bombing its mines stalls it exactly the way it would stall you.

- **A see-through preview while you site a building.** The building itself follows the cursor,
  green where the ground is clear and red where it is not, and the BUILD window says why it is
  red. A site is blocked when it sits on a road or overlaps another unit or building; trees,
  rocks and scenery are ignored, because clearing those to build is normal. A click on a red
  site is refused instead of taking your money. Your placements land unrotated so what you saw
  is what you get.
- **Buildings you build are named for what they are.** A gold mine reads as "Gold Mine" on the
  map, in its unit panel and in the repair list, instead of reporting the industrial prefab it is
  wearing ("Refinery Structure"); a built factory reads as "<UNIT> Factory". Mines built before
  this change keep the old name until the mission is restarted.
- **Buildings you put down can be selected.** Click one or drag a box over it like any vehicle
  and it opens the unit panel with its level, its upgrade button, and a **DESTROY BUILDING**
  button (which asks for a second click and gives no refund). Until now the game's rule that
  buildings are not selectable applied to your own gold mines and factories too, so a mine you
  had just built could not be clicked at all — the only way to upgrade one was the BUILD list.

- **Repair crews.** Buildings never healed on their own in this game, and until now there was
  nothing a commander could do about a bombed refinery. `CMD → BUILD → REPAIR` lists every
  damaged building you own with its condition, and **SEND CREW** hires one of your faction's
  repair trucks for a flat fee and drops it beside that building. It drives in, repairs it, and
  is yours afterwards — and it can be shelled on the way, so a crew is a bet, not a button. A
  building that already has a crew coming says so instead of letting you pay twice.
  - The enemy commander hires crews too, at the same price, and fixes its most valuable damaged
    building first. Bombing its economy now has to be kept up.
- **Every building in the game is buildable.** `CMD → BUILD → STRUCTURES` is the whole
  encyclopedia — radars, depots, hangars, bunkers, ammunition dumps, industry, civilian
  structures — grouped by the categories the game files them under. Each one does whatever its
  own prefab does: a radar you build sees for you, a depot you build supplies for you. Prices come
  from what the game itself values each building at, times the new `BuildingCostMultiplier`
  config knob, so nothing goes stale when the game adds a building.
- **The BUILD window has tabs.** ECONOMY (mines, factories and their upgrades), STRUCTURES and
  REPAIR, with the damaged-building count on the REPAIR tab so you notice without looking.

- **Build menu and an economy to spend it on.** `CMD → BUILD` is a new window with two things in
  it, both paid for out of the faction money pool and both capped at three levels.
  - **Gold mines.** Buy one and click a spot on the ground to site it. It looks like an ordinary
    industrial building and pays your faction a standing income for as long as it stands. Upgrade
    it twice for more income.
  - **Factory upgrades.** A factory normally drops one unit into the faction reserve per
    production cycle. Upgrade it and it drops two, then three.
  - The enemy commander builds mines and buys upgrades under the same rules and the same prices,
    out of its own funds, whenever it is switched on — so its economy grows too, and its mines
    are targets worth striking.
  - Prices and the income rate are in the `Economy` section of the BepInEx config file.

- **A 1v1 mission that comes with the mod: Ground Control Duel.** It installs itself into your
  mission list the first time the plugin loads — no separate download, but copy the whole
  `GroundControlRts` folder into `BepInEx\plugins`, not just the DLL.
  - Base against base: each commander starts with one highway airstrip, two vehicle depots and
    a few AA mounts, about 20 km apart. Every other airbase on the map is shut down.
  - No pre-placed armies and no pre-placed industry. Both sides start with the same money, the
    same aircraft pool and the same buildings, and build everything else with `CMD → BUILD`.
  - Capturing the enemy airstrip wins the match. No nukes.
- **Factories can be built, not just upgraded.** `CMD → BUILD` has a `BUILD FACTORY` button
  and a `PRODUCES` picker listing your own faction's ground vehicles: choose the unit, buy the
  factory, click a site, and from then on it feeds that unit into the faction reserve for your
  depots to deploy. The product and the cycle time are fixed once it is built. The enemy
  commander builds its first factory the same way, at the same price.
  - New config values in the `Economy` section: `FactoryBuildCost` and
    `FactoryProductionSeconds`.

### Changed

- **Placement is less fiddly.** Hold the repeat key (Left Shift by default) while siting a
  building to stay in placement mode and put down another one, the way supply deployments already
  worked. Right-click now backs out of any armed placement — build, supply target, air mission
  area or trailer destination — and Escape cancels a build placement like it already cancelled
  the others.

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

- **The enemy commander is a fair opponent instead of a difficulty slider.** CAUTIOUS / STANDARD
  / AGGRESSIVE are gone, and so is the income stipend AGGRESSIVE handed the enemy faction. The
  setting is now **OFF / MATCHED / MISSION FUNDS**. In MATCHED the enemy is put on your economy
  the first time it reviews — your faction's authored starting balance, your kill reward, your
  tax rate — and from there both commanders buy ground units out of the same kind of pot, a
  quarter of it every 30 seconds, up to three vehicles. Neither side is handed anything. MISSION
  FUNDS is the same commander on whatever balance the mission author gave it, for missions that
  are meant to be lopsided.
- **The enemy commander now plays to a tactical plan, and the plan is what decides the game.**
  It reads what you are fielding every 30 seconds and commits to the counter: air power pulls it
  onto **AIR DEFENCE**, massed armour onto **FIRE SUPPORT** (artillery), a static line of guns
  and launchers onto **SPEARHEAD** (armour to run through it), and nothing dominant onto a cheap
  **RECON SCREEN**. Switching takes two reviews of the same read, so a counter you just paid for
  gets a minute to work before it answers — and shifting your own composition flips its plan
  back, which is the loop. It will still buy one launcher ahead of the plan if you are flying and
  it has no air defence at all.
- **The enemy's plan and balance are shown under your funds readout**, because a plan you cannot
  see is a plan you cannot answer. Hidden with the same **Faction funds** toggle.
- Settings written by an older build carry over except for the enemy commander, which is a new
  key (`EnemyCommanderMode`) and starts at OFF.

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
