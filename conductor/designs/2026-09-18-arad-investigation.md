# Why anti-radiation sorties never fire (2026-09-18)

Investigation only. No code was changed. Prompted by the developer: "i've seen a few ARAD equipped
aircraft but never seen them fire at anything — maybe we swap them out for something else!"

Evidence is the 81-minute match logged on 2026-09-18 plus the roster lines the mod prints at load.

---

## The short answer

**It is not a weapon bug and there is nothing to swap to.** The mod identifies anti-radiation
weapons correctly. The problem is that only one airframe per faction can carry one, and in both
cases that airframe is wanted more urgently somewhere else.

## What the log shows

| Measure | Count |
|---|---|
| Anti-radiation sorties opened | 31 |
| Aircraft ever tasked with one | **9** |
| Strike packages whose anti-radiation slot was filled | **0** (every package reads `A 0`) |

So roughly two thirds of the sorties never got an aircraft at all, and the slot inside a strike
package was never filled once in eighty-one minutes.

## Why: exactly one carrier per faction

The roster lines name the store each airframe can carry. Only **three** carry `ARAD-116`:

| Airframe | Carries ARAD-116 | Available for the job? |
|---|---|---|
| EW-25 Medusa | yes | **No** — reserved to the radar station by `RadarAirframeMayFill` |
| KR-67 Ifrit | yes | Primeva only — and it is Primeva's MAIN FIGHTER |
| Alkyon AB-4 | yes | Boscali only — at 390, the dearest aircraft either side flies |

Per faction that is one option each:

- **Boscali** can only use the **Alkyon AB-4**. It is the most expensive airframe in the match and
  Boscali lost 12 of 18 of them, so the commander rarely has one spare.
- **Primeva** can only use the **KR-67 Ifrit**, which is the aeroplane its entire patrol effort is
  built on. An anti-radiation sortie competes head-on with air superiority and loses.

That is the whole explanation for both numbers above. The capability test
(`CommanderAirCommandScoring.IsAradWeapon`) keys on the game's own `ARMSeeker` component on the
weapon prefab, which is the right test and is working — the AB-4's launch lines show it being bought
WITH an anti-radiation loadout and tasked with an anti-radiation strike.

## What was NOT established

**Whether a tasked aircraft actually fires its anti-radiation missiles.** Nine were tasked; the log
does not record weapon release, and the developer reports never seeing it happen. That question is
still open and needs either a watched sortie in game or a look at what the game's own AI does with
an `ARAD` mission mode. It is a SEPARATE question from the scarcity above, and the scarcity is
enough on its own to explain why the feature looks dead.

## Options, for the developer to choose between

1. **Do nothing.** The feature works when the aircraft exists; it rarely exists. Cheapest, and the
   suppression it was meant to add is small.
2. **Stop opening anti-radiation sorties.** Removes 31 sorties' worth of demand that mostly cannot
   be met, freeing the buy for strike and patrol work. Loses a capability that on this evidence was
   never delivering.
3. **Reserve the carrier.** Hold one AB-4 (Boscali) or one Ifrit (Primeva) back for suppression the
   way the radar aeroplane is reserved. Makes the feature real, at the cost of one fighter on the
   side that can least afford it.
4. **Watch one sortie in game first**, to settle whether a tasked aircraft fires at all. If it does
   not, options 1-3 are all moot and the feature should simply be removed.

**Recommendation: option 4 then 2.** Establish whether the weapon is ever released before spending
anything on making the sortie more common — the open question above is the one that decides whether
this feature can work at all.

## Related change made the same day

The Alkyon AB-4 was made ground-attack only on 2026-09-18 (developer decision: it is "a large, fast,
high-altitude large-payload delivery system"). That does **not** affect its anti-radiation work — the
suppression role sits on the ground-attack side of the airframe rules, not the fighter side — but it
does mean the AB-4 is no longer competing for patrol slots, so it should be marginally MORE available
for suppression than it was.
