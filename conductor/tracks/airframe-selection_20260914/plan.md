# Plan: Airframe selection by fitness tier

**Track**: airframe-selection_20260914 · **Design**: design.md (approved 2026-09-14, DECISION-011)
**Depends on**: smarter-air-wing_20260914 (landed first; both edit the role buyer)
**Expected agent runs**: 1 executor (this session) + 1 review + evaluation, per Track shape.
**Build after every task**: `NUCLEAR_OPTION_DIR="I:\SteamLibrary\steamapps\common\Nuclear Option" dotnet build GroundControlRts.csproj -c Release` — must end `0 Warning(s)` / `0 Error(s)`.

**Roster facts this plan is written against** (from the game's own `Air roster` lines, 2026-09-14):
`FS-12 Revoker [Fighter1] 65`, `FS-20 Vortex [SmallFighter1] 90`, `KR-67 Ifrit [Multirole1] 126`,
`A-19 Brawler [CAS1] 36`, `SAH-46 Chicane [AttackHelo1] 31`, `Alkyon AB-4 [FastBomber1] 390`,
`SFB-81 Darkreach [Darkreach] 225`, `EW-25 Medusa [EW1] 145`, `T/A-30 Compass [trainer] 22`,
`VT-7 Vagrant [VTOLTrainer1] 29`, `CI-22 Cricket [COIN] 12`, plus the two transports.

- [x] T1 **Tier table (design Section 1).** `Ai/CommanderEnemyCommanderAir.cs`: `internal enum
  AirframeTier { Fighter, Multirole, Strike, LastResort }`, class-level `FighterRatio` 1.5 with a
  `<summary>` saying why (a rating half again its opposite is a specialist; anything closer is a
  multirole), `IsTrainerAirframe` keyed on the game's own `jsonKey` containing `trainer` (Compass
  `trainer`, Vagrant `VTOLTrainer1` — the same data-key shape `IsLastResortAirframe` already uses),
  and the pure `AirframeTier ForRole(AircraftDefinition definition, AirRole role)`. SelfCheck in
  `CheckAirBuyRules`: exactly ×1.5 is Fighter, just under is Multirole, the mirror for Strike, the
  Cricket and the Compass land in `LastResort` for both roles, and the CAS order is the CAP order
  reversed except the bottom.

- [x] T2 **Tier order and highest launchable tier (design Section 2).** Pure
  `TierOrder(AirRole role)` (CAP: Fighter, Multirole, Strike, LastResort; CAS: Strike, Multirole,
  Fighter, LastResort) and `AirframeTier? HighestLaunchableTier(IEnumerable<AircraftDefinition>,
  AirRole, Func<AircraftDefinition, bool> canLaunch)` returning the first tier in that order holding
  a launchable candidate. SelfCheck: an empty tier is skipped; a strike-tier airframe is never the
  CAP answer while a fighter-tier one is launchable; nothing launchable returns null.

- [x] T3 **Threat read and within-tier selection (design Section 2).** Constants with `<summary>`:
  `CapThreatAircraft` 2, `CasThreatObserved` 3. Pure `bool ThreatHigh(AirRole role, int capAircraft,
  int casObserved)` and `AircraftDefinition? SelectInTier(IReadOnlyList<AircraftDefinition>
  candidates, bool threatHigh, float allocation)` — best-rated for the role when the threat is high,
  cheapest when quiet, never a candidate priced above `allocation`, and `IsLastResortAirframe`
  ranked below every non-last-resort candidate in the same tier so the Cricket still flies only when
  the Compass cannot. Returns null when the tier is unaffordable, which is the **wait**: the caller
  never drops a tier for price. SelfCheck: the flip at 1→2 tracked aircraft and 2→3 observed, a
  candidate above the allocation is never chosen, an unaffordable tier returns null rather than the
  next tier down.

- [x] T4 **Sortie buys through the tier rule.** `TryBuyRole`: replace the cheapest/escalate price
  walk with `HighestLaunchableTier` over the capability-passing, strip-accepted candidates, then
  `SelectInTier` inside it against the rung's remaining allocation and the threat read for the
  objective (`CountTrackedEnemyAircraftNearBases` / hostile-air-in-ring for CAP,
  `EffectiveObserved(CountObserved(...))` for CAS). Covers CAS packages, escorts and pre-emptive
  cover, which all arrive here. The `allowLastResort` second pass folds into T3's sub-rank and is
  removed rather than left dead.

- [x] T5 **Home CAP through the tier rule.** `BuyHomeCapFighter`: `BetterCapCandidate`'s
  fighter-identity ordering is replaced by the same `HighestLaunchableTier` + `SelectInTier` pair on
  `AirRole.Fighter`, with its self-check retired in favour of T1–T3's. Tracked hostile aircraft near
  the commander's bases are the threat read.

- [x] T6 **The smarter-air-wing picks.** Platoon-requested CAP (CAP order), ARAD (CAS order among
  anti-radiation-capable candidates only), AWACS (radar-capable candidates only, the radar tier),
  each through the same pair — one definition, every caller. Transports and the radar/EW special
  airframe are excluded from the Fighter and Strike orders so a Medusa is never a CAP buy.

- [x] T7 **Owned unbound airframes.** `Operations/CommanderOperationsAir.cs` `TakeUnboundOwned`
  returns the best-tier owned airframe for the role rather than the first match, so an owned Brawler
  is not bound to a CAP sortie while an owned fighter-tier airframe is unbound.

- [x] T8 **Idle sweep routing.** The sweep the smarter-air-wing track added: a strike-tier idle
  airframe goes RTB when a fighter-tier airframe already holds the home CAP, otherwise home CAP.

- [x] T9 **Diagnostics (design Section 4).** `Air roster` lines gain `CAP tier <t> / CAS tier <t>`;
  launch lines gain `(Strike tier, best affordable — 5 observed)` / `(Fighter tier, cheapest —
  quiet)`; denials become `no Fighter-tier airframe can launch from <bases>; Multirole flies CAP`
  and `Fighter tier unaffordable this review (cheapest 65, allocation 40)` through the existing
  `ReportAirDenial` cadence, with the stable-string rule kept (the price pair is the one exception,
  called out in a comment).

- [x] T10 **CHANGELOG + final rebuild.** Unreleased entry naming the tier rule and the three
  constants; `-t:Rebuild` ending `0 Warning(s)` / `0 Error(s)`; hot install with
  `.\build-and-install.ps1 -Dev` and read the log for `self-check FAILED`, exceptions and the first
  `Air roster` tier lines.

## Readings the design leaves open

1. **The Cricket ranks below the Compass inside the bottom tier.** The design puts both in
   `LastResort` but its own verification says "the Cricket flies only when the Compass cannot".
   Cheapest-when-quiet would buy the Cricket at 12 over the Compass at 22, so `IsLastResortAirframe`
   stays a hard sub-rank inside the tier. This preserves the existing LAST RESORT rule rather than
   forking it.
2. **Trainer identity is the `jsonKey` test**, matching how `IsLastResortAirframe` keys on `COIN`.
   It catches the Vagrant (`VTOLTrainer1`) as well as the Compass, which is the same class of
   airframe and the same intent.
3. **"Never drop a tier for price" is implemented as a wait**, not as a fallback: `SelectInTier`
   returning null ends the buy with a denial line naming the tier and the shortfall.

## Departures from the design, with the evidence

1. **The design's tier EXAMPLES do not match the game's own ratings; the RULE does.** The roster
   line now prints the two ratings it tiers on. `KR-67 Ifrit` is rated A/A 1.00 / A/G 0.46 —
   identical to the `FS-12 Revoker` and the `FS-20 Vortex` — so it is Fighter tier, not the
   Multirole the design predicted. `Alkyon AB-4` is 0.70 / 1.00, inside the ratio either way, so it
   is Multirole, not the Strike the design predicted. The ratio and the comparisons are exactly as
   specified; the design's example column was a guess about asset data. Nothing was tuned to force
   the examples, because hand-fitting the constant to two aircraft is the brittle per-airframe list
   the design set out to avoid.

2. **A strike-tier airframe is REFUSED every air-superiority task, not merely ranked below one.**
   Preference alone was not enough: it only chooses between candidates that are all admissible, so
   whenever no fighter-tier airframe was owned or launchable — a commander on highway strips, or a
   wing that had lost its fighters — an `A-19 Brawler` was still the best remaining CAP candidate and
   flew the patrol. The user confirmed this from a live match on 2026-09-14: "seeing a lot of air
   superiority brawlers - SHOULDN'T BE, they're CAS aircraft". `MayFlyAirSuperiority` is now a hard
   refusal, applied at the two capability gates — `PassesRoleCapability` for the buy and
   `FillsAirRole` for the claim, the sortie fill, the retask and the role counts — so the patrol, the
   escort, a sortie's CAP slot, home-CAP lending and the idle sweep all honour it from one rule.

3. **Rung 1 does not deadlock on the refusal; it is skipped.** The ladder's existing valve reads the
   same gate, so a commander whose strips launch nothing but ground-attack airframes now logs
   `home CAP impossible — no air-to-air-capable airframe can launch from highwaystrip1` and
   `the rung is skipped and the lower rungs proceed`. That is the path the valve was built for. The
   strike airframes are not wasted: they fill CAS sorties, and the idle sweep sends them home.

4. **The package form-up hold no longer puts CAS aircraft on an air-superiority task.** This was the
   dominant source of the user's "a lot of" — every CAS airframe on every package spent its whole
   form-up wait carrying `AirCommandMode.AirGuard`, which the interface labels `AIR SUPERIORITY`,
   because that was the only mode that orbits a point without hunting ground targets. `HoldingMode`
   now keeps a CAS airframe on its own CAS (or ARAD) mode throughout and leaves the escort on
   AIR SUPERIORITY, which is its job. The hold's position, radius, bounded wait and go-in test are
   untouched.

5. **`no Fighter-tier airframe can launch from its strips; LastResort flies CAP` still appears**, and
   is correct: it is the once-per-pair note saying the strips cannot launch the tier the role wanted.

6. **Transports, helicopters and the radar/EW airframe are excluded structurally, not ranked low.**
   A transport carries real combat ratings — the `UH-90 Ibis` is A/A 0.27 / A/G 0.90, the strike tier
   by arithmetic — so the first build printed `CAP tier Strike` for a troop helicopter and one was
   seen bound to a platoon's CAP (user report, 2026-09-14). `AirframeTier.Excluded` appears in no
   tier order, so it can never be chosen; `MayFillRole` adds the two refusals that need the airframe's
   prefab (no plane pilot, and no helicopters on air superiority). The tier table itself stays a pure
   function of the two role ratings, which is what keeps every self-check probe answerable without
   the game loaded.

## Verified in the running game, 2026-09-14

Zero `self-check FAILED` and zero exceptions after the reload. No `A-19 Brawler`, `SAH-46 Chicane` or
`SFB-81 Darkreach` appears on any `home CAP`, `escort` or idle-sweep line. Both commanders patrol with
`T/A-30 Compass` and log `(home CAP, LastResort tier, cheapest — quiet, active-radar loadout)`.

The roster line now names every exclusion:

```
UH-90 Ibis [UtilityHelo1] pilot Helo, role Transport, CAP excluded (transport) / CAS excluded (transport) (A/A 0.27, A/G 0.90)
SAH-46 Chicane [AttackHelo1] pilot Helo+Plane, role Strike, CAP excluded (rotary) / CAS tier Strike (A/A 0.27, A/G 0.90)
VL-49 Tarantula [QuadVTOL1] pilot Tiltwing, role Transport, CAP excluded (transport) / CAS excluded (transport)
EW-25 Medusa [EW1] pilot Plane, role Fighter, CAP excluded (radar/EW) / CAS excluded (radar/EW) (A/A 0.36, A/G 0.31)
```
