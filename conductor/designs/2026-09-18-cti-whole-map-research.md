# How the Arma whole-map war mods stayed affordable (2026-09-18)

Research only. No code was changed and nothing was built. Every claim below is labelled by the
strength of its evidence:

- **[code]** — read directly from the mod's published source this morning. Strongest.
- **[docs]** — stated by the mod's own manual or wiki.
- **[report]** — a forum post, issue thread or search summary. One person's belief, treated as such.

Note on one source class: the Bohemia Interactive community wiki refused every automated request
(HTTP 403), so the two engine commands are cited from a third-party mirror and from search-engine
summaries of those wiki pages, never from the pages themselves. They are marked **[docs, second
hand]** and should be re-read by hand before anything is built on them.

---

## 1. The techniques, and what each one actually costs

| Technique | What it does | What it costs | What it breaks |
|---|---|---|---|
| **Freeze in place** (`enableSimulation false`) | The object stays in the world and stays visible, but stops being updated and stops sending network updates. **[docs, second hand]** | Nearly nothing per frozen object, but the object still exists, still occupies memory and is still drawn. | A frozen vehicle is a statue. Players can see it and it will not react. |
| **Distance-based freeze** (`enableDynamicSimulation`) | The same freeze, applied automatically to whole groups beyond a configured distance from a player. Works on groups, and a group containing a player is excluded. **[docs, second hand]** | One distance test per group on a timer. | Frozen units are, per the mirror, "totally unable to do anything even move" and "cant even be killed in this state". **[report]** |
| **Delete and remember** (caching, or "virtualising") | The units are destroyed outright. What survives is a record: composition, strength, position, orders. Fresh units are created from that record when someone comes close. | One record per formation instead of N vehicles. Cheapest of all. | Everything that lived on the deleted object is lost unless it was copied into the record: damage, ammunition, crew, cargo. |
| **Garrison as a number** | A town's or base's defenders are stored as counts of unit types, never as objects, until an enemy or a player approaches. | A few integers per settlement. | The defenders materialise. They were not there a moment ago. |
| **Teleport or spawn-at-destination** | A force that is meant to arrive somewhere is simply created near where it will fight, rather than created at a depot and driven. | The drive disappears entirely, along with all the frames it would have cost. | There is nothing on the roads to intercept, and nothing to watch arrive. |
| **Resolve the fight by dice** | Two formations that nobody is watching trade casualties on a timer by a formula, with no bullets fired. | One arithmetic tick per pair. | The battle did not happen. Only its result exists. |
| **Headless client** | A second copy of the game, with no screen, connects to the server and takes ownership of the AI so their pathfinding and behaviour run on a different processor core. **[report]** | A whole extra process and machine core. | Nothing about fidelity; it is pure capacity. Requires a dedicated server and mission support. |

---

## 2. Who did what

**Warfare 2, the official Arma 2 mode.** Its own manual exposes town garrisons as a tuning knob:
town defence range "determines the range at which units in town will spawn when enemies are near,
with higher values improving immersion but also lowering performance", and town defence time
controls how long those defenders survive after the enemy leaves, with the same trade-off stated in
the same words. **[docs, second hand]** So the official mode shipped garrison materialisation, and
its own documentation named the cost in the same sentence as the benefit.

**Benny's Warfare and the Capture The Island variants.** Nothing solid was found. Searches returned
only gameplay complaints and a 2013 forum remark that both major variants were "not really optimised
yet". **[report]** No technical documentation survives that is worth citing. I am not going to
invent a mechanism for them.

**Antistasi (Arma 3).** The closest relative to this project, and the best documented.

- Garrisons are held on the server as counts of unit types, with a separate set of functions for
  adding and removing types and counts, and a matching set for spawning and despawning the real
  thing locally. **[code]**
- Despawning deletes. The despawn routine destroys every soldier, group, vehicle, civilian and
  building of the garrison outright. Under a comment reading "Pretty dumb recovery logic", a wounded
  soldier is killed on a coin flip and otherwise quietly deleted. **[code]**
- Spawn distance is a mission parameter defaulting to 1,000 metres, selectable from 600 to 1,200.
  **[code]**
- Fast travel exists for the player and for commanded squads, and is refused when an enemy is within
  500 metres. **[docs]**
- Convoys are a mission type rather than a background simulation: ammunition, armour, money,
  prisoner, reinforcement and supply convoys are generated as things for the player to ambush.
  **[docs]**

**Liberation and KP Liberation (Arma 3).** Sector-based rather than unit-based.

- A sector activates when friendly units come within 1,000 metres of it. That single number,
  `GRLIB_sector_size`, is commented in the configuration file as "Range to activate a sector".
  **[code]**
- Activation is deliberately delayed by up to about half a minute, in steps, and the delay is
  shorter when more friendly units are present. A lone scout waits longest. **[code]**
- Deactivation is delayed too, and the delay grows the longer the sector has been active, up to a
  configurable five extra minutes. **[report]**
- Enemy attack forces are not driven across the map. They are created between one and three
  kilometres from the marker they are attacking, capped at sixteen groups, scaled by a readiness
  figure. **[code]** The journey is abstracted away completely by choosing where to start it.

**ALiVE (Arma 3).** The most ambitious, and the only one that simulates the unseen war rather than
skipping it.

- Every formation is a "profile" — a record that can be a group of soldiers, a vehicle, or a single
  player. Profiles are despawned from the world but keep interacting with the rest of the mod, so
  "the virtual battle" continues in the background. **[docs]**
- Default spawn radius is 1,500 metres for players and drones, 1,500 for helicopters, and **zero for
  planes**. **[docs]**
- An "active limiter", default 144, caps how many profiles may be spawned at once. Groups are
  spawned first-come first-served by proximity, and any group over the limit "will be moved outside
  of spawn range". **[docs]** That last clause is worth reading twice: when the budget is exhausted,
  ALiVE moves the war away from the player rather than showing it.
- The dice roll is real code, not a rumour. A function called "get damage output" returns "the total
  damage that a profile can inflict on another", per second, from a table keyed on attacker type and
  victim type, with a hit chance and a critical chance. Infantry against a tank get a hit chance of
  0.90, damage 0.005 per soldier per second and a 20 percent chance of a 2.0 critical. Infantry
  against a plane get a hit chance of 0.10 and **zero** ordinary damage. **[code]** A separate combat
  handler runs the exchange at a combat range of 225 metres on a rate of one. **[code]**

---

## 3. The abstraction boundary, and the hand-off

This is the part that matters, and the two mods answer it differently.

**ALiVE simulates.** Virtual profiles move, meet, and grind each other down by the damage table
above. The AI commander gives orders to profiles whether or not they are spawned, and treats the two
states identically. Hand-off is by proximity: come within the spawn radius and the profile is built
into real units at the position the record says it reached. The player sees a formation at plausible
strength in a plausible place, because the record tracked both. The failure mode is that it can stop:
several posters report the commander going quiet after two to four hours, with virtual units no
longer moving and objectives no longer updating. **[report]**

**Antistasi rolls dice, but only when nobody can see.** Its remote-battle routine is thirty lines and
worth describing exactly, because it is the cleanest statement of the boundary I found. Every ten
seconds it looks for live enemies within half the spawn distance. If it finds any, it computes a
chance to kill equal to 50 multiplied by the ratio of its own count to the enemy count, rolls once,
and kills one unit on the losing side outright. It repeats until one side is gone. And the first
thing it checks, before any of that, is whether a **player** is within twice the spawn distance — if
one is, the routine exits immediately and never runs again for that group. **[code]**

So the hand-off rule, stated plainly, is: dice are allowed only at more than twice the distance at
which real units appear. There is a deliberate buffer of one whole spawn radius between "resolved by
arithmetic" and "resolved by bullets", so that a player approaching at walking or driving pace can
never cross the boundary faster than the system can react.

**Liberation ducks the question.** Nothing fights when unspawned, because nothing exists when
unspawned. A sector is a marker with an owner and a garrison strength; there is no battle to resolve
because there are no units to resolve it between.

A separate ALiVE detail deserves its own line, because it is the only thing found anywhere in this
family that addresses fast-moving players directly. ALiVE ships an optional **air combat
activator**: while any living player is in a plane or helicopter, it scans aircraft and anti-air
vehicles globally, and ordinary vehicles around airborne players, using a plane radius of **7,000
metres** and a helicopter radius of **5,000 metres**, processing sixteen candidates per tick to
spread the cost. **[code]** In other words, ALiVE's answer to "the player is in a jet" was to raise
the activation radius by a factor of nearly five and to pay for the larger scan by batching it.

---

## 4. What it costs in fidelity

Honest version, because the tidy version would be useless.

- **Things materialise.** Warfare 2's own manual frames the town defence range as a straight trade of
  immersion against performance. **[docs, second hand]** Nobody solved this. They tuned it.
- **Things vanish.** Antistasi's captured vehicles despawning while the player travelled was filed as
  a bug. **[report]** A player reported enemy troops spawning "right behind us and right where we had
  just been just moments before". **[report]**
- **Wounded men are deleted.** Antistasi's own comment calls its despawn recovery "pretty dumb", and
  a wounded soldier survives a despawn on a coin flip. **[code]**
- **Aircraft break the model.** The clearest complaint found: flying a jet at speed, enemy
  helicopters do not appear at distance but "appear too close, suddenly appearing in front with no
  time to lock on missiles or aim guns". One mission's fix was to force aircraft to spawn at least
  2,000 metres away. **[report]** This is precisely the failure this project would hit, reported by
  people who hit it first.
- **Whole categories stop existing.** An ALiVE discussion notes that because units are virtualised
  out of range, "a large majority of the artillery units don't actually exist" in a campaign — they
  are only profiles, so they cannot shell anything the normal way. **[report]**
- **The abstract war can silently stop.** The ALiVE commander timing out after a few hours is
  reported repeatedly. **[report]** An abstraction layer is a second simulation, and it can fail
  quietly in ways a real one cannot.

## 5. Scale numbers

Published figures are scarce, and I found no before-and-after frame-time measurement anywhere in
this family. What exists:

| Figure | Value | Source class |
|---|---|---|
| ALiVE profiles spawned at once (default cap) | 144 | **[docs]** |
| ALiVE total profiles supported | "thousands", no number given | **[docs]** |
| Antistasi spawn distance | 1,000 m default, 600–1,200 m range | **[code]** |
| Liberation sector activation range | 1,000 m | **[code]** |
| Liberation enemy attack force | up to 16 groups, created 1–3 km from target | **[code]** |
| ALiVE spawn radius | 1,500 m ground, 1,500 m helicopter, 0 m plane | **[docs]** |
| ALiVE air-combat radius, when a player is airborne | 7,000 m plane, 5,000 m helicopter | **[code]** |

The one ratio worth carrying away: ALiVE's default allows 144 spawned formations against an
unbounded number of virtual ones. Everything else in this family lives at roughly one to two
kilometres of activation radius.

---

## 6. Applicability here

Read for this section: the frame-rate investigation of 2026-09-17, the operations service and the
platoon definition, and the decompiled game's unit, spawner and battlefield-grid classes.

**The jet constraint rules out distance-based activation, and I will say so plainly.** A jet covering
300 metres a second crosses ALiVE's entire default spawn radius in five seconds and Antistasi's in
three. Worse, distance is not even the right test here: a commander at altitude can see and target
ground vehicles far beyond any radius these mods use, and this mod paints markers on them. ALiVE's
own workaround, a seven-kilometre radius for airborne players, would on a Nuclear Option map cover
so much of the battlefield that almost nothing would stay virtual. Distance-based activation is
dead. Do not propose it.

**What survives the constraint is abstraction of things the player structurally cannot watch**, and
there is exactly one large category: units in transit. A platoon driving from a depot to the front
costs frames for minutes and contributes nothing, and it is the one part of the war with no fixed
place for the player to look. Liberation solved this by creating attack forces one to three
kilometres from their target rather than driving them there, and Antistasi gives the player the same
privilege as fast travel. **[code]** Here, the equivalent is to let a platoon exist as its recipe and
its orders while it is nominally moving, and create real vehicles only at the forming-up point. The
mod already holds everything such a record needs: the platoon has an establishment, an armour count,
a role recipe, an objective and a forming-up radius of 150 to 400 metres.

The second candidate is **rear-area garrisons as numbers**, following Warfare 2 and Antistasi: a
strategic point deep behind our own line keeps a token vehicle or two and holds the rest as counts,
materialising them when the front or a hostile approaches. The player will occasionally fly over a
thinly held rear town. That is a smaller lie than a battle that did not happen.

**Abstract combat resolution does not fit.** Antistasi's dice roll is permitted only when no player
is within twice the spawn distance, and here no such guarantee can ever be made — the commander can
be over any fight in under a minute. Building a damage table like ALiVE's and then having the player
arrive mid-roll would produce exactly the "that battle did not really happen" complaint, with no
buffer distance to hide behind. Skip it.

**Headless clients do not apply.** There is no second process to offload to and a mod cannot create
one.

Three engine facts that change what is buildable, all read from the decompiled game today and
**none of them verified in a running match**:

1. **Creating units anywhere is available.** `Spawner.SpawnVehicle` takes a prefab, a world position,
   a rotation, a velocity, a headquarters and a skill. Teleporting a platoon to the front, or
   materialising a garrison, is therefore mechanically possible. It is a server call and must be
   guarded accordingly.
2. **There is no cheap freeze.** `Unit.DisableUnit` exists, but the flag it sets is a networked
   "out of action" state that the rest of the game reads for the head-up display, rearming and
   depots — it is closer to knocking a unit out than to pausing it. The one genuine freeze available
   to a mod is the Unity route of switching off the component that drives a vehicle's per-frame
   work, which needs the frame-rate measurement to say which component that is before it is worth
   trying.
3. **Deleting a unit may be made silent, and deleting it costs memory forever.** A unit's teardown
   raises the "unit lost" event only when the disabled flag is not already set, so setting that flag
   first appears to suppress the loss bookkeeping. Against that: the game's registry keeps a
   permanent reference to every unit ever spawned and clears it only when the mission ends, which is
   the second surviving theory in the frame-rate investigation. **Repeatedly deleting and recreating
   platoons would therefore feed the exact leak that is already under suspicion.** In Arma, caching
   is free; here it is paid for in memory that is never returned. This is the single biggest
   difference between this project and everything above, and it argues for keeping units virtual for
   long uninterrupted stretches rather than churning them.

One measurement worth taking before any of this. The game keeps a spatial grid of units and a unit
removes itself from it when disabled or destroyed, so per-unit neighbour queries should already be
bounded by local density rather than by the total count. A squared curve against the global count is
therefore a little surprising, and it may really be a squared curve against **crowding** — platoons
and their reinforcements stacked into the same few grid squares around an objective. If that is what
it is, then spreading or merging formations is a cheaper fix than any abstraction layer, and it
should be checked first. This is a hypothesis from reading, not a finding.

---

## Sources

All fetched 2026-09-18. Publication dates are given where the source carries one.

**Source code, read directly**

- Antistasi, garrison despawn routine, copyright line dated 2025 — https://github.com/official-antistasi-community/A3-Antistasi/blob/master/A3A/addons/core/functions/GarrisonLocal/fn_garrisonLocal_despawn.sqf
- Antistasi, remote battle resolver — https://github.com/official-antistasi-community/A3-Antistasi/blob/master/A3A/addons/core/functions/CREATE/fn_remoteBattle.sqf
- Antistasi, mission parameters including the spawn distance default of 1,000 — https://github.com/official-antistasi-community/A3-Antistasi/blob/master/A3A/addons/core/Params.hpp
- Antistasi, server-side garrison functions holding unit counts and types — https://github.com/official-antistasi-community/A3-Antistasi/tree/master/A3A/addons/core/functions/GarrisonServer
- KP Liberation, sector activation range and configuration — https://github.com/KillahPotatoes/KP-Liberation/blob/master/Missionframework/kp_liberation_config.sqf
- KP Liberation, staged activation delay — https://github.com/KillahPotatoes/KP-Liberation/blob/master/Missionframework/scripts/server/sector/wait_to_spawn_sector.sqf
- KP Liberation, attack forces created near their target — https://github.com/KillahPotatoes/KP-Liberation/blob/master/Missionframework/scripts/server/battlegroup/spawn_battlegroup.sqf
- ALiVE, virtual damage table — https://github.com/ALiVEOS/ALiVE.OS/blob/master/addons/sys_profile/fnc_profileGetDamageOutput.sqf
- ALiVE, virtual combat handler — https://github.com/ALiVEOS/ALiVE.OS/blob/master/addons/sys_profile/fnc_profileCombatHandler.sqf
- ALiVE, air combat activator with the 7,000 and 5,000 metre radii — https://github.com/ALiVEOS/ALiVE.OS/blob/master/addons/sys_profile/fnc_profileActivatorAirCombat.sqf
- ALiVE, player proximity activator — https://github.com/ALiVEOS/ALiVE.OS/blob/master/addons/sys_profile/fnc_profileActivatorPlayerProximity.sqf

**Documentation**

- ALiVE wiki, Virtual AI System (spawn radii, active limiter) — https://alivewiki.com/wiki/Virtual_AI_System.html
- ALiVE wiki, Military AI Commander — https://alivewiki.com/wiki/Military_AI_Commander.html
- Antistasi beginners guide, version 3.0 (fast travel, convoys, spawn distance advice) — https://official-antistasi-community.github.io/A3-Antistasi-Docs/beginners_guide/raw_beginners_guide.html
- KP Liberation frequently asked questions — https://github.com/KillahPotatoes/KP-Liberation/wiki/EN_FAQ

**Documentation, second hand — the wiki refused automated access, so these are mirrors and search summaries**

- Dynamic simulation, third-party mirror — https://pmc.editing.wiki/doku.php?id=arma3%3Ascripting%3Adynamic-simulation
- Bohemia wiki, Dynamic Simulation (403; summarised only) — https://community.bistudio.com/wiki/Arma_3:_Dynamic_Simulation
- Bohemia wiki, `enableSimulation` (403; summarised only) — https://community.bistudio.com/wiki/enableSimulation
- Bohemia wiki, Warfare 2 manual, town defence range and time (403; summarised only) — https://community.bistudio.com/wiki/Warfare_2_Manual
- Bohemia wiki, headless client (403; summarised only) — https://community.bistudio.com/wiki/Arma_3:_Headless_Client

**Forum, issue and discussion threads — one person's belief**

- Antistasi issue 126, a proposal to estimate distant fights, by Sparker95. Note this is a feature request, not a description of shipped behaviour — https://github.com/A3Antistasi/A3-Antistasi/issues/126
- Antistasi issue 237, captured vehicles despawning while travelling — https://github.com/A3Antistasi/antistasi-1.x/issues/237
- KP Liberation issue 788, a sector failing to spawn enemies once a cap was reached — https://github.com/KillahPotatoes/KP-Liberation/issues/788
- GREUH Liberation mechanics — https://greuh-liberation.fandom.com/wiki/Mechanics
- ALiVE forum, virtualised artillery — https://alivemod.com/forum/2643-virtualized-artillery-let-s-chat/0
- ALiVE forum, the commander going quiet after a few hours — https://alivemod.com/forum/1324-opcom-seems-to-time-out/0
- Bohemia forums, AI caching discussion — https://forums.bohemia.net/forums/topic/201538-ai-caching/

**This project**

- `conductor/designs/2026-09-17-frame-rate-investigation.md`
- `Operations/CommanderOperationsService.cs`, `Operations/CommanderPlatoon.cs`
- Decompiled game at `/tmp/no-decomp/`: `Unit.cs`, `Spawner.cs`, and the battlefield grid in
  `Assembly-CSharp.decompiled.cs`
