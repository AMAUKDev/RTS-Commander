# Addendum (2026-09-14): descriptive platoon markers, reactive CAS, reinforcement requests

**User decisions, verbatim intent**: "we need to have more descriptive Platoon markers - they should
say 'Requesting CAS', 'Requesting Reinforcements', 'Holding point X' (rather than just holding) etc.
And they do need to be able to request another platoon (or several) as reinforcements too."
And: "we also need reactive air-tasking. a quiet platoon holding a point comes under attack and can
request CAS".

## 1. Marker text

`Operations/CommanderOperationsMarkers.cs` label becomes `<NAME> n/m — <situation>[ · flags]`:

| State | Situation text |
|---|---|
| Forming | `Forming at <nearest point or base>` |
| Moving | `Moving to <objective label>` |
| Holding | `Holding <point label>` (Reserve: `Reserve at <base>`) |
| Attacking | `Attacking <target label>` |
| Withdrawing | `Withdrawing to <rally label>` |

Flags, appended in this order when true: `In contact`, `Requesting CAS` (a sortie is open for this
platoon with unfilled airframes), `CAS overhead` (filled), `Requesting reinforcements` (an open
reinforcement request, see §3), `Reinforcing <label>` (this platoon is answering one). Labels reuse
`CommanderOperationsMission.Label` / point labels; no new strings are invented for points.

## 2. Reactive contact for holding platoons and pickets

`ContactDrill` runs only for Moving/Attacking. Add a detection-only pass for Holding platoons,
Reserve platoons and picket detachments: a tracked hostile ground unit inside `ContactRangeMeters`
(2.5 km) of the leader, **or** a member lost within `LossContactSeconds` (60 s), sets
`InContactUntil` (same hold, 20 s past last evidence) **without** moving the platoon off its posts.
The existing "platoon in contact" sortie source then opens CAS + escort at contact priority.
Pickets are not platoons: give `CommanderOperationsMission` a `ContactUntil` for Picket/ForwardBase
missions so the same sortie source covers a point under attack with no platoon on it.

## 3. Reinforcement requests

A holding or attacking platoon in contact whose observed hostiles (`CountObserved` around the
leader, with the `ObservedFloors` memory) exceed its own live strength × `ReinforceOddsRatio`
(1.0 — equal numbers is already a fair fight for the defender; below it the platoon is
outnumbered) opens a **reinforcement request** on its mission: `WantedPlatoons` rises by
`ceil((observed − strength) / PlatoonSize)`, capped at `MaxReinforcementPlatoons` (3). The existing
assignment pass (`AssignPlatoons`) fills it from Reserve platoons first, then Holding platoons on
rear points, never from platoons already attacking; unfillable remainder goes to the order book as
today, so the buyer builds the platoons. When observed hostiles fall below the platoon's strength
for `ReinforceReleaseSeconds` (120 s) the request closes and the reinforcing platoons return to
reserve. Log: `<platoon> requests <n> platoon(s) of reinforcements at <label> (<observed> observed vs
<strength>)`, `<platoon> reinforces <label>`, `<label>: reinforcement request closed`.

Self-checks: label table covers every state; contact-by-loss window boundaries; odds rule at
equal/greater/less; reinforcement count arithmetic and cap; release timer boundary.

## 4. Picket and forward-base truck markers (added 2026-09-14)

Pickets and munitions trucks are not platoons, so §1's walk drew neither. One marker per picket
detachment at its first live member — or at the point itself while an insertion request or flight is
bound for it with nothing on the ground yet — reading `PICKET <point label> n/2 — <situation>[ ·
flags]`, situations `Awaiting insertion`, `Dropped, taking posts` (within `PointsHoldSeconds`, 60 s,
of the last delivery, stamped on `CommanderOperationsMission.LastDropAt`), `Holding <point>` (every
live member inside the point's radius) and `Moving to <point>`; flags `In contact` (the mission's own
`ContactUntil`, §2), `Requesting CAS` (a sortie open for this mission with unfilled airframes) and
`Under strength` (n < `PointsMinGarrison`), appended by §1's `MarkerFlags` with one added parameter
rather than a forked picket copy. A picket with no member and no flight bound to it gets no marker.
One marker per forward-base truck at the truck: `TRUCK <point label> — <situation>`, situations
`Moving to <point>`, `Supplying <point>` (inside the radius) and `Returning` (the point is gone or
has changed hands). Same dot, label style, colours, world-vs-map split, fullscreen-map hide and 8 s
tracking test on anything not ours. Self-checks: every situation and flag renders, the n/m
arithmetic against the garrison setting, and the empty picket returning no label at all.
