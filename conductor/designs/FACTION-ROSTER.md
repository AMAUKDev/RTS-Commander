# Faction rosters — edit this file and hand it back

This is the full unit split between the two sides, as the mod currently applies it. **Edit the
tables, save, and give the file back.** The mod's roster lists will be rebuilt from exactly what you
write here.

Boscali is the Boscali Defence Force (BDF). Primeva is the Primeva People's Army (PALA).

---

## 1. How to edit

- Move a unit between the **Boscali**, **Primeva** and **Shared** columns by editing the tables.
- A unit may appear in Boscali *or* Primeva *or* Shared. Never two of them.
- Do not invent units. Only the ones listed here exist in the game.
- Aircraft carry a bracketed key, for example `FS-12 Revoker [Fighter1]`. The key is the game's own
  internal name; the readable name is for you. **If the two ever disagree, the readable NAME wins**
  and the bracket gets corrected — that is what happened to your 2026-09-16 edit, where four aircraft
  ended up carrying `[Multirole1]`. The brackets in this file are now the verified ones.
- Leave the **Can do** column alone unless it is actually wrong. It says what the unit is good for,
  and the safety checks are built from it.
- If you want a change that breaks one of the rules in section 6, write it anyway and add a note. I
  will tell you what it costs rather than silently refusing.

---

## 2. What the units can do

| Capability | Meaning |
|---|---|
| Capture | Can move a capture bar and take ground |
| Hold | Can sit on ground and shoot back at infantry and light vehicles |
| Anti-armour | Can kill tanks |
| Anti-air | Can kill aircraft |
| Suppress radar | Carries an anti-radar missile, so it can clear an air-defence belt |
| Ground attack | Can attack ground targets from the air |
| Cargo lift | Can carry a ground vehicle as cargo |
| Scout | Sees further than it shoots — a radar aeroplane or a radar truck |
| Repair | Repairs buildings and builds air-defence sites |

---

## 3. Aircraft

All thirteen aircraft in the game. Prices are the game's own worth ratings, which the mod uses as
the cost. This is your 2026-09-16 split, applied as written, with the bracketed keys corrected
against the game's own roster log.

### Boscali

| Aircraft | Price | Can do |
|---|---|---|
| FS-20 Vortex [SmallFighter1] | 90 | Anti-air, ground attack, suppress radar |
| FS-12 Revoker [Fighter1] | 65 | Anti-air, ground attack, suppress radar |
| VT-7 Vagrant [VTOLTrainer1] | 29 | Anti-air, ground attack |
| Alkyon AB-4 [FastBomber1] | 390 | Anti-air, ground attack, suppress radar |

### Primeva

| Aircraft | Price | Can do |
|---|---|---|
| KR-67 Ifrit [Multirole1] | 126 | Anti-air, ground attack, suppress radar |
| A-19 Brawler [CAS1] | 36 | Ground attack |
| SFB-81 Darkreach [Darkreach] | 225 | Ground attack |
| T/A-30 Compass [trainer] | 22 | Anti-air, ground attack |

> The VT-7 Vagrant and the T/A-30 Compass are both marked anti-air on your instruction, and the
> game's data agrees: it rates each of them 0.62 air-to-air, which is a real gun-and-missile
> capability even though the mod still ranks both in its bottom airframe tier and flies them only
> when nothing better can launch. Neither carries an anti-radar missile, so neither may claim
> suppress radar.

### Shared — cannot currently be split

| Aircraft | Can do | Why it is shared |
|---|---|---|
| SAH-46 Chicane [AttackHelo1] | Ground attack | The only aircraft with both a helicopter and an aeroplane pilot, which is what the mod's helicopter close-support role needs |
| EW-25 Medusa [EW1] | Scout | The only aircraft carrying the radar pod the early-warning role needs |
| UH-90 Ibis [UtilityHelo1] | Cargo lift | One of only two transports |
| VL-49 Tarantula [QuadVTOL1] | Cargo lift | The other transport, and the only one carrying a vehicle that can take ground |

> Splitting the two transports one each would leave whichever side lost the Tarantula unable to
> air-land anything that can capture, because the Ibis carries no capture-capable vehicle at all.
> That is why both are shared. If you want them split, say so and I will tell you what else has to
> change first.

### Excluded — on no side's list, deliberately

| Aircraft | Price | Can do |
|---|---|---|
| CI-22 Cricket [COIN] | 12 | Ground attack |

> You left the Cricket off both sides in your 2026-09-16 edit and confirmed that was deliberate, so
> it is now on no list at all. Neither side will ever buy it or launch it, and no rule in section 6
> depends on it. The mod's `Air roster` line in the log still prints the Cricket every mission and
> marks it `NOT ON THIS FACTION'S ROSTER, never bought or launched` for both sides, so an exclusion
> that had gone wrong would look different from an aircraft the scan simply never found. Put it back
> on a side's table if you change your mind; nothing else has to change.

---

## 4. Vehicles carried by aircraft

These exist **only** as aircraft cargo. They are in no convoy group, so this split is entirely the
mod's own and you can move anything freely.

### Boscali

| Vehicle | Can do |
|---|---|
| AFV6 APC | Capture, hold |
| AFV6 IFV | Capture, hold, anti-armour |
| AFV6 AT | Capture, hold, anti-armour |
| AFV6 AA | Anti-air |

### Primeva

| Vehicle | Can do |
|---|---|
| LCV25 AT | Capture, hold, anti-armour |
| LCV25 AA | Anti-air |
| Hexhound SAM | Anti-air |
| Hexhound GMG | Hold |

### Shared

| Vehicle | Can do | Why it is shared |
|---|---|---|
| HLT Radar Truck | Scout | The only ground radar any transport carries, so splitting it leaves one side with no air-landable eyes |

---

## 5. Vehicles bought at a depot

**These come from the game's own convoy groups, not from the mod, so the split below is the game's
and is already almost total.** You can still move a unit here, but doing so means the mod overriding
the game's own faction data, which is a bigger change than moving anything in section 4. Flag it if
you want it.

### Boscali

| Vehicle | Can do |
|---|---|
| Type-12 MBT | Hold, anti-armour |
| AFV8 IFV | Capture, hold, anti-armour |
| AFV8 APC | Capture, hold |
| AFV8 Mobile Air Defense | Anti-air |
| FGA-57 Anvil | Hold, anti-armour |
| HLT Munitions Truck | Hold |

### Primeva

| Vehicle | Can do |
|---|---|
| Spearhead MBT | Hold, anti-armour |
| Linebreaker IFV | Capture, hold, anti-armour |
| Linebreaker APC | Capture, hold |
| Linebreaker SAM | Anti-air |
| AeroSentry SPAAG | Anti-air |
| MSV Munitions | Hold |

### Shared

| Vehicle | Can do | Why it is shared |
|---|---|---|
| M12 Jackknife | Repair | The only repair vehicle in the game, and it is in neither faction's convoy groups, so without sharing it neither side can repair buildings or build air-defence sites |
| T9K41 Boltstrike | Anti-air | The long-range launcher both sides field in the game's own data |

---

## 6. Rules that must hold after your edits

Each of these is checked when the mod loads. If an edit breaks one, the mod will say so by name
rather than failing quietly in play.

1. **Each side can take ground.** At least one capture-capable vehicle, by depot and by air.
2. **Each side can clear an air-defence belt.** At least one aircraft that can suppress radar.
   This matters more than it used to: a sortie now waits for a sweep rather than flying the belt.
   **This one now passes only just for Primeva.** Your split leaves Primeva exactly one aircraft
   that carries an anti-radar missile, the KR-67 Ifrit at 126, against three for Boscali. Nothing
   is broken and the check passes, but Primeva's whole ability to open an air-defence belt now rests
   on a single airframe that it also has to be able to afford — and 126 is the dearest entry price
   for suppression either side has ever had. If Primeva is poor, or that one airframe is grounded,
   its sorties will sit and wait.
3. **Each side can lift cargo.** Otherwise flown-in forward bases and air-mobile platoons stop
   working for that side entirely.
4. **Each side can repair.** Otherwise building repair and air-defence site construction stop.
5. **Each side can kill armour, kill aircraft, attack ground from the air, and scout.**
6. **No unit appears on both sides' own lists**, and nothing shared also appears on a side's list.
7. **Neither side is priced out of a capability the other has cheaply.** This is the rule your
   2026-09-16 split moves the most, so here is where it stands. The figure is the cheapest aircraft
   on that side that can do the job at all.

   | Capability | Boscali | Primeva | Gap |
   |---|---|---|---|
   | Air superiority, anything that can fight | VT-7 Vagrant 29 | T/A-30 Compass 22 | Primeva 7 cheaper |
   | Air superiority, a proper fighter | FS-12 Revoker 65 | KR-67 Ifrit 126 | Boscali 61 cheaper |
   | Radar suppression | FS-12 Revoker 65 | KR-67 Ifrit 126 | Boscali 61 cheaper |
   | Ground attack | VT-7 Vagrant 29 | A-19 Brawler 36 | Boscali 7 cheaper |
   | How many aircraft can suppress radar | 3 of 4 | 1 of 4 | Boscali 2 more |

   Read plainly: **Boscali is now cheaper or equal on every one of the four capabilities except the
   bottom-tier air-to-air entry, where Primeva is ahead by 7.** The clean one-advantage-each balance
   the previous split had is gone. Primeva pays 61 more than Boscali both for its first real fighter
   and for its only way into an air-defence belt, and its cheap ground-attack advantage over Boscali
   has narrowed from 189 to 7, because the VT-7 Vagrant now sits on Boscali's list and attacks ground
   for 29. Primeva's remaining edges are the heavy SFB-81 Darkreach and a 22-cost aircraft that can
   fight in the air. **Nothing here is a rule break and nothing was rebalanced** — rule 7 is a
   judgement, not a check the mod runs, and this is your call to make. Flagging it so it is a choice
   rather than a surprise.

---

## 7. Current counts

| | Boscali only | Primeva only | Shared | Excluded |
|---|---|---|---|---|
| Aircraft | 4 | 4 | 4 | 1 |
| Air-landed vehicles | 4 | 4 | 1 | 0 |
| Depot vehicles | 6 | 6 | 2 | 0 |
