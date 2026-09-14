# Design: Preserve mod state across hot reload

**Date**: 2026-09-13 · **Track**: persist-reload-state_20260913 · **Approved by user**: 2026-09-13
("write the design and get it implemented asap"). Developer quality-of-life; off by default.

## Problem

Every hot reload (BepInEx ScriptEngine swaps the mod assembly mid-mission) wipes the mod's memory
while the game world keeps running: Air Command missions and their AUTO flags, mine/factory/dock
upgrade levels, point ownership, platoons. A long test match has to be restarted after each
rebuild, which is the opposite of what hot reload is for.

## Decisions

1. **Scope**: Air Command missions (with AUTO and relaunch recipes) and economy upgrade levels.
   Point ownership is a stretch goal only if cheap after the first two; platoon state is skipped
   (it rebuilds itself from the live roster within one 30 s review).
2. **Gate**: `Developer/PersistStateAcrossReload` setting, default **false**. When on, a snapshot is
   written; a snapshot is **read only on a hot-reload load** (`Assembly.Location` empty, the same
   detector `Core/CommanderMissionInstaller.cs` uses), never on a normal `plugins\` launch.
3. **File**: `Application.persistentDataPath/CommanderState/<mission name>.json`. Not beside the
   DLL (unknown under hot reload). Hand-written minimal JSON (the game ships Newtonsoft; use it if
   referenced already, else write a tiny serializer for flat records — no new dependency).
4. **Session guard**: the snapshot carries the mission name and a session id. The id is
   `Application.persistentDataPath`-independent: a random GUID written once per game process into a
   `Developer` config-independent static… which does not survive reload. So instead use the game's
   own mission start signature: mission name + `Time.timeSinceLevelLoad` at snapshot time; on
   restore accept only if the mission name matches and the snapshot's level time is **less than**
   the current `Time.timeSinceLevelLoad` and within 10 minutes of it. A stale file from an earlier
   match on the same map fails the time window.

## Section 1 — Mechanism

- New lifecycle hook `ICommanderPersistState { void Snapshot(CommanderStateWriter w); void
  Restore(CommanderStateReader r); }` fanned out by `CommanderServiceRegistry` like the others.
- New `Core/CommanderStateStore.cs`: owns the file path, writes on `CommanderPlugin.OnDestroy`
  (first task: verify in the log that ScriptEngine calls OnDestroy; `CommanderModeController` already
  relies on it) and every `SnapshotIntervalSeconds` (20, scaled scheduler) while a mission runs;
  reads once, on the first `TickPersistent` after `CommanderFeatureGate.MissionName` is non-empty and
  the local HQ exists, then deletes the file so it cannot be re-applied.
- Record format: one JSON object with `mission`, `levelTime`, and per-service arrays.

## Section 2 — Air Command payload

Per mission: aircraft `persistentID` (as string), mode, area centre (x,y,z), radius, target
altitude, `TargetOrdnance`, `SaturationAttack`, `PurchasedWithFunds`, `PurchaseCost`, `AutoRecreate`,
and the recipe: aircraft definition `jsonKey`, origin airbase name, loadout as one mount `jsonKey`
(or empty) per hardpoint index, recipe mode, area, radius, target altitude, flags. Pending relaunch
queue entries are saved as recipes too.

Not saved (re-derived or dropped): `Route`, `RouteIndex`, `ForcedTarget`, landing flags, `Parked`,
`OnDeckSince`, `Issued`. The pilot's live AI state survives; `Returning`/`RtbIssued` are re-derived
from the pilot's current state being a landing state.

Restore: resolve each `persistentID` via `PersistentID.TryGetUnit`; if it is a live, non-disabled
`Aircraft` of the local HQ, rebuild `AirMission` (recipe rebuilt via `Encyclopedia` lookup by
`jsonKey`, airbase by name from `hq.GetAirbases()`, mounts by `jsonKey` via a new lookup beside
`BuildWeaponOptions`), insert into `missions`, re-pin in the mission list. If the aircraft is gone
and AUTO was on, push the recipe onto `relaunchQueue`. Queued relaunches restore straight into the
queue. Log one line: `Air Command restored N missions, M relaunches queued, K dropped`.

## Section 3 — Economy payload

`mineLevels`, `factoryLevels`, `dockLevels` as (persistentID, level). Restore by resolving IDs to
live, non-disabled units of any HQ; skip the rest. Also re-attach restored mines to their resource
sites (`CommanderStrategicPointService.AttachMine` by nearest site within 100 m) so the site reads
as taken again. Log one line with counts.

## Section 4 — Stretch: points

Only if Sections 1–3 land inside the task budget: control-point ownership as (kind, rounded
position, owner faction name, progress). Restore after discovery completes by matching kind and
position within 50 m, resolving the faction by name to an HQ index in the current `hqOrder`. If
anything does not match, leave the point neutral. Otherwise document as not done.

## Out of scope

Platoon/operations state; anything on a normal launch; multiplayer clients; persistence across a
mission restart (that is the mission's job).

## Verification

Self-checks: snapshot/restore round trip of an Air Command record and an economy record through
the writer/reader with synthetic values (pure, no game objects); the session guard's time window.
In game (developer): with the toggle on, launch two Air Command missions (one AUTO), upgrade a mine
to level 2, hot reload; both missions still listed with AUTO intact, the mine still level 2, log
shows the restore counts; with the toggle off nothing is written; on a normal launch nothing is read.
