# Design: Airframe selection by fitness tier

**Date**: 2026-09-14 · **Track**: airframe-selection_20260914 · **Approved by user**: 2026-09-14
**Depends on**: commander-priorities_20260914 (rung allocations, home CAP), smarter-air-wing_20260914
(packages, AWACS, ARAD, platoon CAP — implement this track after it lands; both edit the role buyer).

## Problem (user's words)

"We need to be smarter about air-frame selection for our various taskings. We should never have
Crickets or Brawlers flying CAP (they're CAS aircraft), or an air superiority platform like the
Revoker flying CAS when a Brawler would do it better!"

The 2026-09-14 capability change (`Ai/CommanderEnemyCommanderAir.cs` `IsAntiAirCapable` /
`IsAntiSurfaceCapable`, `PassesRoleCapability`) made "can hang the weapon" the only gate, with price
deciding among candidates. The game's own role ratings (`AircraftDefinition.roleIdentity.antiAir` /
`antiSurface`, already read by `GetAirRole`) are no longer ranked by, so a Cricket or Compass wins a
CAP buy on price and a Revoker can be sent to CAS.

## Decisions (user, 2026-09-14)

1. Rank by **fitness tier** first; the highest tier a held base can launch always wins.
2. Within the tier, **scale by enemy presence and funds**: strong preference for the best airframe
   when money is available; the cheapest in the tier when the sky or ground is quiet.
3. Never drop a tier for price; a lower tier flies only when nothing above it can launch.

## Reuse

- Role ratings: `roleIdentity.antiAir` / `antiSurface` (read today in `GetAirRole`).
- Candidate list and strip test: `airCatalog`, `FindAcceptingAirbase` (`IsCompatibleAirbase` +
  `CanSpawnAircraft`); capability tests and `BuildRoleLoadout` stay as the gate and the loadout.
- Buyers: `TryBuyRole`, `BuyHomeCapFighter`, the sortie fill in `Operations/CommanderOperationsAir.cs`
  (`TryClaimAircraft`, `TakeUnboundOwned`), the ARAD/AWACS/package candidate picks added by the
  smarter-air-wing track, the idle sweep.
- Threat reads: `CountTrackedEnemyAircraftNearBases` / hostile-air-in-ring counts (CAP),
  `CountObserved` + `EffectiveObserved` (CAS).
- Last resort: `IsLastResortAirframe` (Cricket) becomes the bottom tier rather than a separate pass.
- Not reused: hand-written per-airframe lists (brittle, faction-specific).

## Section 1 — Tiers from game data

`AirframeTier ForRole(AircraftDefinition, AirRole)`: pure, from the two ratings.

| Tier (CAP order) | Rule | Examples on today's rosters |
|---|---|---|
| Fighter | `antiAir ≥ antiSurface × FighterRatio` (1.5) | FS-12 Revoker, FS-20 Vortex |
| Multirole | ratings within the ratio either way | KR-67 Ifrit |
| Strike | `antiSurface ≥ antiAir × FighterRatio` | A-19 Brawler, SAH-46 Chicane, Alkyon |
| LastResort | `IsLastResortAirframe` (Cricket) or trainer identity (Compass) | CI-22 Cricket, T/A-30 Compass |

CAS order is Strike, Multirole, Fighter, LastResort. Transports (`captureCapacity > 0`, no plane
pilot) and radar/EW special-system airframes are excluded from both; AWACS uses the radar tier only;
ARAD tiers by the CAS order among anti-radiation-capable airframes. `FighterRatio` is a constant
with rationale; the `Air roster` line prints `CAP tier … / CAS tier …` per airframe so the table is
visible in the log.

## Section 2 — Selection rule

For a role and objective: `tier = highest tier (in that role's order) with ≥ 1 airframe that passes
the capability gate AND can launch from a held base`. Within the tier:

- **Threat high** (CAP: ≥ `CapThreatAircraft` (2) tracked hostile aircraft near the objective or
  bases; CAS: ≥ `CasThreatObserved` (3) effective observed hostiles): buy the **best-rated**
  airframe whose price fits the rung's remaining allocation this review.
- **Threat low**: buy the **cheapest** in the tier that fits.
- If nothing in the tier fits the allocation this review, **wait** (denial line names the tier and
  the shortfall); do not drop a tier for price.
- A lower tier is used only when no airframe in any higher tier can launch from any held base.

Pure: `SelectInTier(candidates, threatHigh, allocation)`; `HighestLaunchableTier(...)`.

## Section 3 — Where it applies

Home CAP, platoon-requested CAP, escorts, CAS packages, pre-emptive cover, ARAD, AWACS, and the
sortie fill's choice among already-owned unbound airframes (an owned Brawler is not bound to a CAP
sortie while an owned fighter-tier is unbound). Idle sweep: a strike-tier airframe left idle goes
RTB when a fighter-tier airframe is already on home CAP, else home CAP.

## Section 4 — Diagnostics and settings

- Launch lines: `launched a A-19 Brawler (Strike tier, best affordable — 5 observed)` /
  `(Strike tier, cheapest — quiet)`. Denials: `no Fighter-tier airframe can launch from <bases>;
  Multirole flies CAP` / `Fighter tier unaffordable this review (cheapest 65, allocation 40)`.
- Constants with `<summary>`: `FighterRatio` 1.5, `CapThreatAircraft` 2, `CasThreatObserved` 3.
  No new config entries.

## Out of scope

Per-airframe hand lists; the player's own AIR window picker; loadout scoring (unchanged).

## Verification

Self-checks: tier table at the ratio boundaries (exactly ×1.5 is Fighter; just under is Multirole);
last-resort and trainer land in the bottom tier; CAS order is the CAP order reversed except the
bottom; highest-launchable-tier picks skip an empty tier; best-vs-cheapest flips at 1→2 aircraft
and 2→3 observed; a candidate above the allocation is never chosen; a strike-tier airframe is never
a CAP candidate while a fighter tier is launchable. In game: `Air roster` shows tiers; with
airbase_desert1 held the enemy's CAP launches are Revokers/Vortexes only and its CAS launches
Brawlers/Chicanes; on highway strips CAP falls to Compass with a `no Fighter-tier … can launch`
line and the Cricket flies only when the Compass cannot; no `(Strike tier …)` launch ever carries
`home CAP`.
