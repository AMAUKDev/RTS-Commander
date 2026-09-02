# Project plan — Ground Control (RTS)

Forward-looking design notes. Anything already shipped lives in `README.md` / `CHANGELOG.md`;
this file is only for work that has not been built yet. Keep entries in gameplay terms first
and engine terms second, and delete an entry once it ships.

---

## Ballistic strike calls

### What the player does

Right now the commander can move units, task aircraft and buy reinforcements, but there is no
way to say **"put fire on that spot"**. Every weapon in the mod's control is bound to a unit
that has to drive somewhere and pick its own target. A strike call is the missing verb: pick a
point on the map, pick who shoots it, and the shot happens without micromanaging the shooter.

The flow, from the player's side:

1. Press the strike key (or the STRIKE button in the selection bar) — the cursor arms.
2. Click a point in the 3D view or on the tactical map. Holding the queue key stacks several
   aimpoints into one fire mission.
3. A **strike panel** lists every friendly asset that can range that point, sorted by time to
   first round: artillery, ballistic launchers, ships in range, and tasked aircraft.
4. Pick one, or press CALL for the cheapest asset that can do it.
5. A countdown marker sits on the aimpoint (SPLASH 0:38) in the world and on the map, and
   the battle log records the call, the shooter and the impact.

Cancelling before impact stands the shooter down. A shooter that dies, runs dry or is dragged
out of range hands the mission back with a "STRIKE ABORTED" alert rather than silently doing
nothing — the mod's existing failure mode of "nothing visibly happens" is what makes orders
feel broken, so every strike has to report an outcome.

### Three delivery types, because the base game has three

The game does not have one "call for fire" API, so this is three thin adapters behind one
player-facing verb. Verified against the decompiled `Assembly-CSharp.dll`:

| Type | Base-game hook | Notes |
| --- | --- | --- |
| Tube artillery | `MobileArtilleryAI` | Has a private `currentTargetPosition` (`GlobalPosition?`), a state machine (`TargetSearch` → `DestinationSearch` → `DrivingToDestination` → `Attack` → `Rearm`) and its own min/max range. It already knows how to reposition into range and shell a coordinate. |
| Ballistic / cruise launchers | `MissileLauncher : Weapon` | `Weapon.Fire(owner, target, inheritedVelocity, weaponStation, GlobalPosition aimpoint)` takes a **ground aimpoint**, not just a unit. `BallisticMissileGuidance` flies to a `GlobalPosition` with a circular error and an optional airburst height. |
| Naval and aircraft | `CommanderAirCommandService`, ship weapon stations | Already have a mission/area concept; a strike call is a one-shot mission area with a hard aimpoint. |

Artillery is the first one to build. It is the only path where the base game already does the
hard parts — ballistics, repositioning, reload, rearm — and the mod only has to hand it a
coordinate.

### How artillery calls should be implemented

`MobileArtilleryAI` picks its own target through `FactionHQ.TryGetNearestGroundEnemy`. The
commanded version overrides the *choice*, not the machinery:

- A Harmony patch on the AI's target-search step, guarded on a cheap
  `CommanderStrikeService.HasMissions` flag, substitutes the commanded aimpoint for the one the
  AI would have found. Same shape as `CommanderAirCommandPilotHooks`.
- `currentTargetPosition` and the state field are private, so reach them with
  `AccessTools.FieldRefAccess` and verify the names against a fresh decompile — they are
  string-matched and fail silently otherwise.
- Setting the aimpoint while the AI is in `Attack` is not enough: it has to be pushed back
  through `DestinationSearch` so it repositions when the new aimpoint is outside
  `minRange`/`maxRange`.
- Rounds are not free. A mission expires after N rounds or a fixed shoot time, then the AI
  returns to its own logic, so a called strike cannot permanently hijack a battery.

Ownership: this is a new `CommanderStrikeService` in a new `Strike/` folder, registered in
`CommanderModeController.Awake` under `CommanderTier.Advanced`, implementing
`ICommanderTickPersistent` (missions must keep running while the player is flying) and
`ICommanderResetSession`. It does **not** go into `CommanderMoveService`: move orders are about
where a unit is, strike missions are about where its rounds land, and merging the two is how
the order system got confusing the first time.

> ponytail: artillery only, one mission per battery, no counter-battery and no danger-close
> check on friendlies. Upgrade when the artillery path proves itself in play.

### Ballistic launchers, second

Once artillery works, the same service gains a second adapter that calls
`Weapon.Fire(..., aimpoint)` on a `MissileLauncher` whose missile carries
`BallisticMissileGuidance`. Two things to settle first, and both need testing in-engine rather
than reasoning:

- **Networking.** `Weapon.Fire` is a local call. `Unit.SingleRemoteFire` / `RpcSingleRemoteFire`
  and `Unit.CmdSetStationTargets` are the networked paths, and none of them carries a ground
  aimpoint. A client-side call may fire locally and desync, in which case the feature is
  host-only and must say so in the UI.
- **Which units even have one.** Launcher loadouts live in Unity assets, not in
  `Assembly-CSharp`, so the set of ballistic-capable units has to be discovered by walking
  `Unit.weaponStations` at runtime and logging what carries a `MissileLauncher` with ballistic
  guidance. Build that as a debug dump before writing any UI.

Cruise missiles are a bonus case: `Missile` implements `ICommandable`, so a launched cruise
missile can be given `SetDestination` mid-flight the same way ground units are — which is a
route, not a strike, and belongs to a later entry.

### Cost and balance

Strikes spend ammunition, not funds, so the limiting factor is rearm logistics, which the mod
already models (`RetreatConditionPercent`, `Rearmer`, the supply helicopters). No new economy.
The enemy commander does not call strikes in the first version.

### NO auto production of units
the only units that actaully gets spawned should be the ones that the played bought and not just automatically spawns in for free

### Settings this adds

- `StrikeCalls` (on/off, advanced tier only)
- `StrikeMaxRounds` — rounds per called mission before the battery reverts to its own AI
- `StrikeKey` — arms the aimpoint cursor; needs a row in `CommanderShortcutReference.Collect`,
  a `DrawControlSettings` entry and `GetBinding`/`SetBinding`/`ResetActionBindings` cases

### Done when

- Calling a strike on a visible enemy position produces impacts on that position, from a
  battery that repositioned itself if it had to.
- Cancelling, the shooter dying, and running out of ammo each produce a distinct alert.
- The countdown marker is visible in both the 3D view and the tactical map.
- The battery goes back to its own AI afterwards instead of standing idle.
