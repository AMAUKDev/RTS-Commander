# Transport delivery survey — why cargo flights fail

Read-only survey, 2026-09-16. Evidence: `BepInEx\LogOutput.log` (one game launch, 38,388 lines,
seven hot reloads). Three play blocks are measured: the long one (log lines 5,037–33,152, called
**Session A** below), the short block after it (33,174–34,438, **Session B**), and the current
session (34,439–end, **Session C**).

## Headline

Across all three blocks, 81 cargo flights left the ground and 16 put a vehicle on the ground.
The dominant failure is not hovering and it is not crashing. It is **enemy fighters shooting the
transports down**, followed by **the mod's own threat watch recalling the load before it gets
there**. Hovering is real but rare, and the existing stall clock catches it.

## Part 1 — the measurements

### Every cargo flight, by outcome

A cargo flight is one `insertion flight <aircraft> bound for <point>` line. That line is written for
every kind of cargo sortie — picket insertions, forward-base construction loads and platoon lift
loads all pass through it.

| Outcome | Session A | Session B | Session C | Total |
|---|---|---|---|---|
| Flights launched | 32 | 14 | 34 | 80 |
| Vehicle on the ground | 8 | 1 | 7 | 16 |
| Transport destroyed with the load | 8 | 3 | 7 | 18 |
| Recalled by the route watch, load intact | 8 | 3 | 11 | 22 |
| Abandoned on the deck before take-off | 6 | 2 | 4 | 12 |
| Still airborne at the block's end, or unaccounted | 2 | 5 | 5 | 12 |

Success counts the two lines that prove a vehicle arrived: `settled at <point> after N s; shield off`
(the insertion shield lifting) and `delivered (N/M)` (a forward-base load credited).

Session C splits by side: the player's commander launched 14 and landed 5; the enemy commander
launched 21 and landed 2.

### Hovering

A transport that sits over its landing zone without unloading is caught by the stall clock in
`Operations/CommanderOperationsInsertion.cs` and its twin in `Operations/CommanderOperationsFob.cs`.
Both fire. They are not the problem the developer thinks they are.

| Stall-clock event | Session A | Session B | Session C |
|---|---|---|---|
| Turned into a parachute drop after 120 s | 4 | 3 | 2 |
| Still could not deliver, recalled | 0 | 1 | 1 |

Nine flights in three blocks hovered long enough to trip the clock. In Session C both conversions
were followed by a successful landing of the load at the same point, so the rescue path works. The
clock bounds the hover at about four minutes: 120 seconds trying to land, then 120 seconds on the
parachute run, then a recall.

Two reasons the clock can miss a hover. First, it only runs while the transport is inside 500 m of
the landing zone the mod recorded, and only while the mod still holds a flight record for it.
Second, a transport flying an ordinary supply run rather than an insertion or a lift has no record
at all and therefore no clock. Both are worth closing, but neither explains the bulk of the losses.

### Losses, by cause and by airframe

The line that names the killer only exists for the enemy commander's aircraft. The service that
writes it (`Ai/CommanderEnemyCommanderAirTasking.cs`) skips the local faction, so **the player's own
transports have no fate telemetry at all** — across all three blocks there are zero such lines for
the player side. This is the single biggest gap in the evidence and the cheapest thing to fix.

Enemy transports only:

| Airframe | Lost | Shot down | Flew into the ground |
|---|---|---|---|
| UH-90 Ibis (helicopter) | 32 | 29 | 3 |
| VL-49 Tarantula (tiltwing) | 24 | 17 | 7 |

The tiltwing is about three times as likely to end a flight in the ground as the helicopter: 29
percent of its losses against 9 percent. In Session C every one of the fourteen helicopter losses
was enemy fire and none was a crash, while four of eleven tiltwing losses were crashes.

Those four crashes cluster tightly. All were 45 to 62 km from the nearest base the enemy held, all
at 0 to 33 m above the ground, all after 360 to 420 seconds in the air — the longest flights in the
set. Two of them are logged two lines apart at 51.1 km and 51.2 km, 9 m and 0 m above ground: a pair
of tiltwings from the same wave putting themselves into the dirt at the same place within seconds.
That is a terminal-phase flight failure at the landing zone, not an interception.

### En route versus at the landing zone

The distinction matters and the log answers it only partly. Being shot down is overwhelmingly an
en-route event: helicopter losses in Session C sit at 60 to 260 m above the ground and 10 to 30 km
out, which is transit altitude, and the named killers are fighters (VT-7 Vagrant, FS-12 Revoker) and
one human player. The crashes are the opposite: ground level, at the far end of the route, late in
the flight. So:

- **21 of 25 transport losses in Session C were shot down in transit.** A delivery bypass does
  nothing for these.
- **4 of 25 flew into the ground, all tiltwings, all at or near the destination.** A bypass could
  help these, if it shortens the time spent in the hover.

## Part 2 — how a delivery actually works today

Numbered, with the owner of each step marked. **Game** means the stock `AIHeloTransportState`;
**Mod** means this repository.

1. **Mod.** The operations side decides a point needs vehicles and opens a flight record with the
   landing post, the expected number of vehicles and a request clock.
2. **Mod.** The supply side buys the transport, loads the vehicles onto cargo mounts, spawns it at
   an airbase and binds the live aircraft back to the flight record.
3. **Game.** The pilot enters the transport state. Its own landing-spot search would pick a target.
4. **Mod.** A Harmony prefix on that search suppresses it entirely for an assigned transport, and a
   prefix on the state's physics tick writes the destination fields directly every cycle: the
   landing zone, the touchdown point, the slope, the drop-conditions flag and the private
   parachute-drop flag. The mod also walks the transport along its own approach waypoints, and can
   raise the terrain-following floor (it only ever raises it, so it cannot fly the aircraft down).
5. **Game.** The transport flies to the destination. Inside 1,500 m it drops its minimum radar
   altitude to a third; inside 200 m it stops following terrain altogether.
6. **Game.** Inside 300 m of the touchdown point the game switches on auto-hover and **switches
   flight assist off**, then commands a hover at 20 m. Only once the transport is within 20 m
   horizontally does the commanded height start falling, at 2 m per second.
7. **Game — this is the gate that matters.** Cargo is released only when `radarAlt < 2` **and**
   `speed < 10`. In plain words: the aircraft must be physically on the ground and stopped. If it
   holds a hover at 5 m, or drifts more than 20 m from the touchdown point, nothing is ever
   released. That is exactly the "hovers forever" the developer describes, and the rule is the
   game's, not the mod's.
8. **Mod.** A prefix replaces the game's release routine. It opens the cargo doors, waits out an
   unload delay for ammunition runs, and fires one cargo mount at a time with a 2.5-second gap.
   Firing a mount is what creates the vehicle in the world.
9. **Mod.** A postfix on the game's cargo-activation call puts the new vehicle under the insertion
   shield, tells it to hold position, and credits the delivery to the picket or the forward base.
10. **Mod.** If a second vehicle is still aboard, a ramp-clear handshake drives the first one clear
    before the next is released, and the landing timer is pinned at zero so the game cannot take off
    mid-unload.
11. **Mod.** Once the last vehicle is out and the ramp is clear, the mod issues the flight home
    itself, using the rotary landing state with the origin airbase pinned.
12. **Mod.** The shield sweep runs every tick. A vehicle that comes to rest on its side is set
    upright where it lies. The shield lifts once the vehicle has been on the ground, upright and
    still for three seconds, or after 90 seconds whatever it is doing.

The parachute path already skips steps 6 to 10 entirely. With the drop flag set the game holds
200 m, releases within about four seconds' flying time of the drop point and switches straight to
the combat state; the mod then sends the transport home. **The mod already has a bypass of the
landing gate. It is the parachute conversion.**

## Part 3 — the proposal, judged

The proposal: once the transport is approximately stationary at the landing zone, spawn the
vehicles, empty its cargo, send it home.

**Can "approximately stationary at the landing zone" be detected?** Yes, cleanly, and the mod
already does this test. The elevated-platform approach in the delivery code reads horizontal
distance to the target, height above the target and `aircraft.speed`, and uses thresholds of 50 m,
10 m and 25 m per second. The same three readings plus `radarAlt` give the condition wanted here,
and the 500 m ring and the 120-second clock are already built and self-checked.

**Is spawning the vehicle new work?** No. It is one line that already exists. Firing a cargo mount
is how every vehicle enters the world today, and the game's own release routine does the same thing.
The proposal's "spawn directly" half is a reuse, not a build.

**Can the aircraft be emptied so the game agrees?** Yes, for free. Firing a mount sets its fired flag
and its ammunition to zero, and the loaded-ammunition accessor then reports empty. No extra field is
needed. The game will not try to deliver again because the mod, not the game, decides when the
flight is over and issues the trip home.

**What is lost.** The bypass fires the cargo from wherever the transport is hovering instead of from
the ground.

| Thing | Under the bypass |
|---|---|
| The damage shield on a delivered vehicle | Kept. It hangs off cargo activation, which still happens. |
| Setting a vehicle upright that lands on its side | Kept, and needed more, since the vehicle now falls further. |
| The parachute fallback | Unnecessary for the stall case, since the bypass replaces it. Still wanted for genuinely blocked landing zones. |
| The ramp-clear handshake | Breaks, and must be replaced by the parachute path's fixed 1.5-second spacing. Two vehicles dropped onto the same spot is a new failure. |
| Delivery credit to the forward base and the picket | Kept. It also hangs off cargo activation. |
| The transport's trip home | Kept. The mod already issues it on the parachute path. |
| Vehicles surviving the fall | At risk. The user's own earlier complaint was vehicles "dropped from a too-high height and dying on contact". The shield covers this, but only if the drop height stays low. |

**Does it fix the crashes?** No, and this is the part to be blunt about. Of 25 enemy transport
losses in the current session, 21 were fighters shooting them down in transit and 4 were crashes.
The bypass does nothing for the 21. It helps the 4 only insofar as it shortens the hover, and the
crashes we can see happened during the approach and hover, so shortening that phase is plausibly
worth something — but the evidence is four events, not a trend. The bypass mainly fixes the
hovering, which by the log's count is nine events in three blocks.

### Options, ranked

**1. Log the player's own transports first, then decide.** The fate line exists only for the enemy
commander. The developer is describing what they see on screen, and the log cannot confirm or deny
it for their own side. Extending the airframe tracker to the local faction is small, self-contained
and turns this whole question from argument into measurement.
*Cost:* one service's faction filter. *Risk:* more log volume; nothing else.

**2. A narrow rescue on the existing stall clock.** Leave normal deliveries alone. When the 120-second
clock expires, instead of only converting to a parachute drop, first check whether the transport is
already low and slow over the post — inside the ring, under roughly 30 m of radar altitude, under
roughly 5 m per second. If it is, release the cargo where it sits with the parachute path's spacing,
mark the flight delivered and send it home. Fall back to the parachute drop when it is high or fast,
and keep the recall as the last step.
*Cost:* one branch inside a stall path that already exists, plus a self-check case per threshold.
*Risk:* vehicles dropped from up to 30 m; the shield already covers that fall, and the height cap is
the control. No change to the 90 percent of flights that never stall.

**3. Stop sending the tiltwing to land.** It crashes at three times the helicopter's rate and every
crash we can attribute is at the destination. Prefer the UH-90 Ibis for flights that must touch
down, and route the VL-49 Tarantula to parachute drops, which never enter the hover. This costs
nothing at runtime and uses the delivery choice the mod already makes.
*Cost:* a preference in the transport pick. *Risk:* fewer airframes available for landing
deliveries, so more flights wait; and the tiltwing's cargo must be parachute-capable.

**4. The bypass as proposed, for every delivery.** Do not do this yet. It replaces a working path
with an untested one to fix nine events, it breaks the ramp-clear handshake, and it does nothing
about the 21 transports the enemy shot down. If option 2 proves the low-and-slow release is safe,
widening it later is a small change.

### Not in scope here, but the larger number

Twenty-two flights across three blocks were recalled by the route watch with their load intact, and
eighteen were shot down. Together that is half of all cargo flights, against sixteen successes. The
delivery mechanism is not the biggest thing standing between these transports and the ground; the
escort is.
