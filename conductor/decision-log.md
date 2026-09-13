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
