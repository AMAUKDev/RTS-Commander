# Backlog: Preserve mod state across hot reload (dev quality of life)

**Status**: investigated 2026-09-13 (read-only memo by a helper agent), not designed with the user.
Developer-only feature: every hot reload today wipes Air Command missions and AUTO flags,
mine/factory/dock upgrade levels, point ownership and platoon state, so a long test match has to be
restarted after each rebuild.

## Recommended shape (from the memo)

- **Where**: a JSON snapshot in `Application.persistentDataPath/CommanderState/<mission>.json`,
  never beside the DLL (`Assembly.Location` is empty under ScriptEngine; see
  `Core/CommanderMissionInstaller.cs`).
- **When**: written on `CommanderPlugin.OnDestroy` (verify ScriptEngine actually destroys the old
  plugin object; `CommanderModeController.OnDestroy` already assumes so) plus a periodic 15–30 s
  safety snapshot on the scaled scheduler. Read on the first tick after the mission name is known,
  only if mission name and a per-play-session id match, and only when `Assembly.Location` is empty
  (a hot reload), never on a normal `plugins\` load.
- **Air Command payload**: per mission the aircraft `persistentID`, mode, area centre, radius,
  target altitude, ordnance/saturation flags, purchased flag + cost, AUTO flag, and the recipe
  (aircraft `jsonKey`, origin airbase name, loadout as one mount `jsonKey` per hardpoint, mode,
  area, radius). Drop `Route`, `ForcedTarget` and the landing flags: the pilot's live AI state
  survives the reload and can be re-read.
- **Reattach**: resolve each `persistentID` via `PersistentID.TryGetUnit`; rebuild `AirMission`,
  re-pin in the mission list. Missing aircraft with AUTO on → push the recipe onto `relaunchQueue`.
  Needs a weapon-mount-by-`jsonKey` lookup beside `BuildWeaponOptions`.
- **Economy**: mine/factory/dock levels by `persistentID` — low risk, worth doing.
- **Points**: hold ownership is stored as an index into a per-tick HQ order (unstable by design);
  would need re-keying by faction name and (kind, rounded position). Stretch goal.
- **Operations**: rebuilds itself from the live roster within one 30 s review. Skip.
- **Gate**: `Developer/PersistStateAcrossReload`, default off.

## Effort

About 7 tasks for Air Command (interface `ICommanderPersistState` fanned out by the registry,
setting, writer/reader + OnDestroy spike, periodic tick, Air Command Snapshot/Restore, round-trip
self-check, in-game verification), +2 for economy. One executor, one review.
