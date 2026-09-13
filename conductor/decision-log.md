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
