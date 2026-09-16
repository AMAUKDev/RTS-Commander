# Plan — cheap aircraft earn their keep

Nine tasks. Each is one behaviour with its verification; where a `SelfCheck` can cover it, the
failing check, the implementation and the passing check live inside the one task.

1. **Extract `AffordableAirframes`** out of `PackageElementBuys` and have it read the extracted
   helper. Behaviour-neutral: every existing `CheckPackageElements` case must still pass unchanged.

2. **Extract `PreferTier`** out of `EscortTier` and have `EscortTier` call it. Behaviour-neutral:
   the existing escort-tier checks must still pass unchanged.

3. **`CheapAirframesAdmissible`** — the shared veto both new rules stand behind: a sortie buy, a
   fighting role that is not suppression or radar, not bleeding, no suppression sortie waiting.
   Named checks at each of the four vetoes.

4. **`AirJobIsEasy`** — the setting, the shared veto, and the four job facts. Named checks at every
   boundary: zero and one tracked aircraft, zero and one air-defence vehicle, two and three observed
   hostiles, in contact and not.

5. **`CheapAirframeMaxObserved` = 2** as a class constant with a `<summary>` saying it is the top of
   the close-air-support ladder's own "a picket, one airframe" step, plus a check pinning it to that
   step so the two cannot drift apart.

6. **Two settings** in `Core/CommanderSettings.cs` with `Get`/`Set` pairs and warm-up touches:
   `CheapAirframeEasyJobs` and `CheapAirframePadding`, both defaulting on.

7. **Wire the easy-job tier override into `TryBuyRole`**: move the attrition read above the tier
   choice, apply `PreferTier` to the bottom tier after the escort override, suppress the
   tier-shortfall line for a tier that was chosen deliberately, and name the choice on the launch
   line.

8. **`ElementProperBuys` / `ElementPaddingSlots` / `CheapestPaddingIndex`** plus the padding loop in
   `TryBuyRole`, launching through the same `LaunchBoughtAirframe` tail so the ceiling, the charge,
   the claim and the attrition ledger are the existing ones. Named checks on the arithmetic and on
   the pick.

9. **Prove the gates**: plant a defect in each of the easy predicate, the padding arithmetic and the
   tier override; build; name the check that fails; restore; confirm byte-identical by sha256. Then
   `CHANGELOG.md` and DECISION-048.
