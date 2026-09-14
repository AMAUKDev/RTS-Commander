# Decision Log

Technical and business decisions made during development.

## Format

### DECISION-XXX: [Title]
- **Date**: YYYY-MM-DD
- **Track**: track-id
- **Decision**: What was decided
- **Rationale**: Why this decision was made
- **Alternatives**: What else was considered
- **Impact**: Effects on architecture/product/business

---

### DECISION-001: Fork upstream, keep changes mergeable
- **Date**: 2026-09-13
- **Track**: (setup)
- **Decision**: Develop on a fork of `simonsimme/RTS-Commander`; keep `upstream` remote and prefer new services/files over edits inside upstream services.
- **Rationale**: The original author is still active (Camera-fixes, FIX-and-FEATURE branches). Clean merges matter more than minimal diffs.
- **Alternatives**: Hard fork with no upstream tracking.
- **Impact**: Every track spec names which upstream files it touches and why.

### DECISION-002: Conductor mode human-in-the-loop
- **Date**: 2026-09-13
- **Track**: (setup)
- **Decision**: `mode: human-in-the-loop`, `max_fix_cycles: 2`.
- **Rationale**: Gameplay changes need the developer in the loop (no automated test harness; verification is in the running game). Matches the "Track shape" section of `.claude/CLAUDE.md`.
- **Alternatives**: `agentic`.
- **Impact**: The loop pauses on ambiguity and after two failed fix cycles.

### DECISION-003: Map-driven AI variety, points layer first
- **Date**: 2026-09-13
- **Track**: strategic-points_20260913
- **Decision**: AI variety comes from geography (resource sites, villages, hilltops, bases), not personalities or deeper adaptation. Resource sites = existing industrial buildings + generated fill; mines only at sites; control points pay only while garrisoned by ≥ MinGarrison ground vehicles; per-base flat income. Delivered as four tracks: points (this), platoons with objectives, truck logistics, air/naval support tasking.
- **Rationale**: User observation: enemy AI mine-spams at its base and convoy-rushes one road. KISS: one new concept ("point") that every later system reads. Take-and-hold at platoon scale is the desired feel.
- **Alternatives**: Spacing rule only (no objectives to fight over); points as destroyable buildings (bombing targets, not held ground); personalities.
- **Impact**: New `Points` settings section, new strategic-points service, two AI decisions changed, COMMANDER LOG window, point markers on map and world.

### DECISION-004: Later AI tracks recorded as backlog stubs
- **Date**: 2026-09-13
- **Track**: (backlog)
- **Decision**: The three follow-on tracks from DECISION-003 are written up as `conductor/backlog/*.md` with the user's verbatim intent, reuse pointers and open questions, so the design sessions start from the record rather than memory.
- **Rationale**: Only the first track had a design; the other three lived in one conversation.
- **Alternatives**: Create full tracks now (premature: each needs its own brainstorm).
- **Impact**: `/orchestrator-supaconductor:brainstorm` for each, in order, once the points track is verified in game.

### DECISION-005: Platoon operations doctrine
- **Date**: 2026-09-13
- **Track**: platoon-operations_20260913
- **Decision**: Missions-and-requisitions architecture (approach A). The mod claims every AI-bought vehicle at the depot; platoons of 6 (3 armour, 1 carrier, 2 AD) fill FOB, picket and attack missions; offensives run on 2–3 equidistant axes, sized to the commander's own tracked picture, with release points and a pressure clock (attack at least every 12 min). Buyer fills an order book of requisitions. AI-only this track.
- **Rationale**: Today's behaviour is a trickle to points plus one convoy at the nearest enemy. Explicit missions are legible in the COMMANDER LOG and give offensives a shape. Contact-weighted point value, axis-only manning gates and the pressure clock stop the sides expanding outward without meeting.
- **Alternatives**: Extend the garrison step and redirect game convoys (no scaling, still one stream); per-vehicle utility scoring (never forms up).
- **Impact**: New Operations/ service replacing the garrison partial and the home guard's recruit logic; buyer gains requisition mode; new markers and log block; new OPERATIONS settings.

### DECISION-006: Truck logistics shape
- **Date**: 2026-09-13
- **Track**: truck-logistics_20260913
- **Decision**: Mines and every control point pay only by truck; base income stays per tick. One persistent truck per paying point, round-trips to the nearest held base, pays on entering the ring, returns empty. A lost truck loses only its load and is replaced free on the next cadence. 3-minute loads.
- **Rationale**: User wants traffic both ways and a raidable economy without escort rules or a cost spiral. FOBs from the platoon track already sit on front-line points.
- **Alternatives**: Mines only (too little traffic); everything including base income (too much); truck consumed on arrival (one-way traffic); truck fee per spawn (can go net negative under pressure); escorts by rule (couples to platoons).
- **Impact**: New Logistics/ service, hopper accrual replacing direct payout for mines and control points, truck markers, IN TRANSIT readouts, Logistics settings section.

### DECISION-007: Air support tasking driven by the ground plan
- **Date**: 2026-09-13
- **Track**: air-support-tasking_20260913
- **Decision**: The operations mission list is the single source of air objectives — no separate air brain. CAS-only sorties over contested objectives first (platoon in contact or live attack), then front ForwardBase missions by threat; pickets and rear points get none. Magnitude scales with `CountObserved` + `ObservedFloors` (ladder 0→0, ≤2→1, ≤5→2, ≤9→3, ≥10→4 airframes; cap 4 CAS + 1 CAP escort per objective; wing ceiling 8 everywhere). CAP escort only when hostile air is tracked inside the objective ring (`ThreatMemorySeconds` freshness). The player-side AI tasks only airframes it bought itself; player AIR missions are never retasked. Launches ride the existing `BuyAirframe`/`TryLaunchAiAircraft` path on the existing `AirFund`; the duel-only gates on the air buy and task legs are removed so it runs on every commanded HQ, stock missions included. Losses re-request only while contested, behind a 2-min cooldown doubled to 4 when ≥2 hostile air-defence units are observed at the loss. Naval out of scope.
- **Rationale**: Today the enemy wing orbits the opponent's frozen opening airbase and only exists on the duel map. CAS bound to the platoon plan makes the air force fight over the same ground the platoons fight over, and the existing launch, recovery and budget machinery applies unchanged.
- **Alternatives**: A smarter fixed strike target in `TaskAirWing` (still a separate brain); commander launches through `TrySpawnFromRecipe` for recipe AUTO-relaunch (local-HQ + raw-funds path that bypasses the AirFund); scaling per platoon rather than per objective (more sorties than a wing of 8 can fly).
- **Impact**: New `Operations/CommanderOperationsAir.cs` partial on the operations service; a pending-launch claim notify on the existing `RegisterFactionUnit` postfix; `ChooseAirRole` reads sortie demand; duel gates deleted; 2 new config entries (Operations section); SelfCheck ladder cases; no new Harmony patches.

### DECISION-008: Picket insertion by transport helicopter rides the SAM supply machinery
- **Date**: 2026-09-13 (user-approved same day with four answers folded in)
- **Track**: heli-picket-insertion_20260913
- **Decision**: Rear picket points farther than 2 km from the nearest road (the user's gate, verbatim intent "more than 2km from road is fine") are filled by air instead of by road; a roadless map flies every picket. The operations service requests a transport heli, the two picket vehicles are bought as cargo mounts at heli spawn (charged their `cargo.value` each) — one air-defence plus the cheapest other mountable ground vehicle, per the user's "doctrine over bargains" answer — and the heli is rented (charged at spawn, refunded on recovery via the existing `PurchasedWithFunds` chain), flown to the point's hold-post ring by the existing `AIHeloTransportState` patches, landed and unloaded only (airdrop resolved out of scope), RTB'd and recovered. One insertion airborne per HQ, one request per review, 10-minute loss cooldown per point (confirmed); a lost heli loses its hull cost and the two vehicles aboard (engine behaviour: `MountedCargo.OnPartDetached` spawns cargo disabled). Funded from the ground pot, never the `AirFund`, `BuyAirframe` or `TaskAirWing` — the air-support wing (DECISION-007) and insertions buy and fly separate aircraft. Road distance is measured against the road polylines discovery already caches (`roadPointLists`, retained past discovery instead of cleared).
- **Rationale**: Pickets drive from the depot over the road network, so roadless rear points (hilltops, remote villages) pay nothing for most of the match and yield to platoons on the pool guard. The SAM supply feature already proved the whole buy-fly-land-unload-return chain in play; the only engine unknowns the capture ponytail flagged are asset data (which vehicles are mountable cargo), resolved by a once-per-mission roster log with drive-fill as the fallback.
- **Alternatives**: Gate on distance from the nearest held asset (design's first proposal, 10 km — the user replaced it with the road-distance gate: distance-to-road is what actually predicts a slow drive); physically loading pool vehicles aboard (no game API — loading is loadout at spawn, verified in the assembly); spawning the vehicles at the point when the heli arrives (teleports combat power, no delivery risk); sling loading (player-flown mechanic, no AI hook); airdrop variant (parachute-capable mounts exist but descent is unverified in play — user kept it out of scope).
- **Impact**: New `Operations/CommanderOperationsInsertion.cs` partial; road polylines retained past discovery (`Points/CommanderStrategicPointDiscovery.cs` — the `StepBases` clear removed) plus a `NearestRoadDistanceMeters` helper on the points service; an HQ-parameterised AI entry generalising `CommanderSupplyHeliService`'s local-HQ spawn gate (second caller after `RequestSamSiteFoundationDrop`); insertion branches in `HoldDeployedCargo`, the ejection suppression and `PruneFinishedMissions`; `PicketMembers` adoption of deployed vehicles; `heli=` on the `Ops … review:` line (the air-support track extends the same line with `air=[…]` — reconcile at implementation); 4 new Operations config entries, 1 new slider row, SelfCheck cases.
