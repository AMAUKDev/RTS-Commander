using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Self-checks for the air buy. Split out of <c>CommanderEnemyCommanderAir.cs</c> on 2026-09-15 purely for size (that file
/// had grown past three thousand lines); the code is moved, not rewritten, and the class doc on the
/// parent file still describes the whole. One partial class, several files.
/// </summary>
internal sealed partial class CommanderEnemyCommanderService
{
    /// <summary>
    /// Asserts the role rule still reads the way the buy loop assumes. It is two comparisons on
    /// asset data, so a game patch that retunes an airframe's role identity, or an edit that flips
    /// the comparison, silently turns the mixed wing back into a monoculture with nothing to notice
    /// it. Run once at plugin load beside the plan self-check.
    /// </summary>
    private static void CheckAirRoles()
    {
        CheckAirRole("interceptor", antiAir: 0.9f, antiSurface: 0.1f, captureCapacity: 0, AirRole.Fighter);
        CheckAirRole("attack jet", antiAir: 0.1f, antiSurface: 0.9f, captureCapacity: 0, AirRole.Strike);
        CheckAirRole("transport", antiAir: 0f, antiSurface: 0f, captureCapacity: 8, AirRole.Transport);
    }

    private static void CheckAirRole(
        string name, float antiAir, float antiSurface, int captureCapacity, AirRole expected)
    {
        // No prefab, so HasPlanePilot is false and the transport branch is reachable, which is the
        // branch worth checking: a plane that carries troops must still count as what it shoots at.
        AircraftDefinition definition = ScriptableObject.CreateInstance<AircraftDefinition>();
        definition.roleIdentity.antiAir = antiAir;
        definition.roleIdentity.antiSurface = antiSurface;
        definition.captureCapacity = captureCapacity;

        AirRole actual = GetAirRole(definition);
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError(
                $"Enemy air role self-check FAILED ({name}): expected {expected}, got {actual}.");
        }

        Object.Destroy(definition);
    }

    /// <summary>
    /// The air buy rules that live in this file, run once at plugin load beside the role rule:
    /// the demand priority (operations tasking outranks the standing rules — user decision
    /// 2026-09-13) and the Cricket LAST RESORT classification. An edit that reorders
    /// <see cref="ChooseAirRole"/> or retypes the Cricket silently changes what every commander
    /// fields; this is what says so in the BepInEx console.
    /// </summary>
    private static void CheckAirBuyRules()
    {
        ForceRead flyingOpponent = new() { Aircraft = 2, Ground = 6 };
        ForceRead groundOpponent = new() { Ground = 6 };

        // The air savings ceiling (fix, 2026-09-14). A wing may bank the price of the dearest thing
        // an open demand wants, floored at one dearest fighter, and never a multiple of either.
        Expect("a quiet wing saves no more than one dearest fighter", AirFundCeiling(0f, 65f), 65f);
        Expect("an open strike demand raises the ceiling to that airframe", AirFundCeiling(145f, 65f), 145f);
        Expect("a demand cheaper than a fighter never lowers the floor", AirFundCeiling(12f, 65f), 65f);
        Expect("a commander whose strips launch nothing banks nothing", AirFundCeiling(0f, 0f), 0f);
        Expect("a negative price is never a ceiling", AirFundCeiling(-50f, 0f), 0f);
        Expect("a review short of four cheap airframes banks for all four", AirFundCeiling(145f, 65f, 4, 10, 31f), 145f);
        Expect("a review short of ten cheap airframes banks past the dearest one", AirFundCeiling(145f, 65f, 10, 10, 31f), 310f);
        Expect("the demand-sized ceiling never exceeds the room under the airborne ceiling", AirFundCeiling(145f, 65f, 10, 2, 31f), 145f);
        Expect("no room under the airborne ceiling leaves the one-airframe rule", AirFundCeiling(145f, 65f, 10, 0, 31f), 145f);
        Expect("an unlaunchable demand adds nothing to the ceiling", AirFundCeiling(145f, 65f, 10, 10, float.MaxValue), 145f);
        Expect(
            "the ceiling is the price of one airframe, never several",
            AirFundCeiling(145f, 65f) < 145f * 2f,
            true);

        Expect("CAP demand buys a fighter even with transports short and the opponent on the ground",
            ChooseAirRole(CommanderAirDemandKind.Cap, fighters: 2, flyingOpponent), AirRole.Fighter);
        Expect("CAS demand buys a strike airframe even with transports short",
            ChooseAirRole(CommanderAirDemandKind.Cas, fighters: 2, groundOpponent), AirRole.Strike);
        Expect("no demand and a flying opponent with no fighters buys the air-superiority counter",
            ChooseAirRole(CommanderAirDemandKind.None, fighters: 0, flyingOpponent), AirRole.Fighter);
        // The wing buys no transports of its own (user decision, 2026-09-14): the supply and
        // insertion path buys those, with cargo already aboard. NOTHING may yield a transport buy —
        // asserted over every demand kind, both opponent shapes and an empty and a stocked wing,
        // because ChooseAirRole is the only thing that names the role a buy will shop for.
        foreach (CommanderAirDemandKind kind in System.Enum.GetValues(typeof(CommanderAirDemandKind)))
        {
            foreach (bool flying in new[] { true, false })
            {
                for (int fighters = 0; fighters <= 2; fighters++)
                {
                    Expect(
                        $"no buy ever asks for a transport ({kind}, "
                            + $"{(flying ? "flying" : "ground")} opponent, {fighters} fighters up)",
                        ChooseAirRole(kind, fighters, flying ? flyingOpponent : groundOpponent) == AirRole.Transport,
                        false);
                }
            }
        }

        Expect("a ground opponent no longer makes the wing buy a transport of its own",
            ChooseAirRole(CommanderAirDemandKind.None, fighters: 1, groundOpponent), null);
        Expect("nor does an empty wing facing a ground opponent buy one",
            ChooseAirRole(CommanderAirDemandKind.None, fighters: 0, groundOpponent), null);
        Expect("no demand buys nothing", ChooseAirRole(CommanderAirDemandKind.None, fighters: 1, groundOpponent), null);
        Expect("AWACS demand buys the radar airframe",
            ChooseAirRole(CommanderAirDemandKind.Awacs, fighters: 2, flyingOpponent), AirRole.Awacs);
        Expect("ARAD demand buys the anti-radiation airframe",
            ChooseAirRole(CommanderAirDemandKind.Arad, fighters: 2, groundOpponent), AirRole.Arad);
        Expect("a CAS sortie that wants helicopters buys a helicopter",
            ChooseAirRole(CommanderAirDemandKind.Cas, fighters: 2, groundOpponent, wantsRotary: true), AirRole.RotaryCas);
        Expect("a CAS sortie that wants jets buys a jet",
            ChooseAirRole(CommanderAirDemandKind.Cas, fighters: 2, groundOpponent, wantsRotary: false), AirRole.Strike);
        Expect("the rotary preference never changes what a CAP demand buys",
            ChooseAirRole(CommanderAirDemandKind.Cap, fighters: 2, groundOpponent, wantsRotary: true), AirRole.Fighter);

        AircraftDefinition cricket = ScriptableObject.CreateInstance<AircraftDefinition>();
        cricket.jsonKey = "COIN";
        AircraftDefinition trainer = ScriptableObject.CreateInstance<AircraftDefinition>();
        trainer.jsonKey = "trainer";
        AircraftDefinition fighter = ScriptableObject.CreateInstance<AircraftDefinition>();
        fighter.jsonKey = "Fighter1";
        Expect("the Cricket's COIN data key is last resort", IsLastResortAirframe(cricket), true);
        Expect("the Compass trainer is NOT last resort (the user's correction)", IsLastResortAirframe(trainer), false);
        Expect("a normal airframe is not last resort", IsLastResortAirframe(fighter), false);
        Object.Destroy(cricket);
        Object.Destroy(trainer);
        Object.Destroy(fighter);

        // The home-CAP ARH rule (user decision 2026-09-14): the classification keys on the game's own
        // ARHSeeker component on the missile prefab, exactly the way the ARM test keys on ARMSeeker.
        // A GameObject with the seeker stands in for the missile prefab the real mounts carry.
        GameObject seekerPrefab = new("ArhSeekerProbe");
        seekerPrefab.AddComponent<ARHSeeker>();
        WeaponInfo arhMissile = ScriptableObject.CreateInstance<WeaponInfo>();
        arhMissile.missile = true;
        arhMissile.effectiveness.antiAir = 1f;
        arhMissile.weaponPrefab = seekerPrefab;
        WeaponInfo noSeeker = ScriptableObject.CreateInstance<WeaponInfo>();
        noSeeker.missile = true;
        noSeeker.effectiveness.antiAir = 1f;
        WeaponInfo groundMissile = ScriptableObject.CreateInstance<WeaponInfo>();
        groundMissile.missile = true;
        groundMissile.weaponPrefab = seekerPrefab;
        WeaponInfo arhBomb = ScriptableObject.CreateInstance<WeaponInfo>();
        arhBomb.missile = false;
        arhBomb.effectiveness.antiAir = 1f;
        arhBomb.weaponPrefab = seekerPrefab;
        Expect("a missile carrying the game's ARH seeker and scored against air is an ARH air-to-air missile",
            CommanderAirCommandService.IsArhAirToAirMissile(arhMissile), true);
        Expect("a missile with no seeker on its prefab is not ARH, however anti-air it is",
            CommanderAirCommandService.IsArhAirToAirMissile(noSeeker), false);
        Expect("a seeker on a weapon with no anti-air score is not an air-to-air missile",
            CommanderAirCommandService.IsArhAirToAirMissile(groundMissile), false);
        Expect("a bomb carrying a seeker is not a missile at all",
            CommanderAirCommandService.IsArhAirToAirMissile(arhBomb), false);
        Object.Destroy(seekerPrefab);
        Object.Destroy(arhMissile);
        Object.Destroy(noSeeker);
        Object.Destroy(groundMissile);
        Object.Destroy(arhBomb);

        CheckCasOrdnancePreference();
        CheckAradOrdnancePreference();
        CheckLastResortRule();
        CheckAirframeTiers();
        CheckInternalCannonRule();
        CheckPackageElements();
        CheckCheapAirframeUse();
        CheckLaunchBaseSpread();
    }

    /// <summary>The launch-base ranking at its boundaries (user report 2026-09-14, taxi queues).</summary>
    private static void CheckLaunchBaseSpread()
    {
        Expect("an empty base 10 km off beats a nearer base with one aircraft waiting",
            LaunchBaseScore(10_000f, 0, LaunchQueuePenaltyMeters) < LaunchBaseScore(1_000f, 1, LaunchQueuePenaltyMeters), true);
        Expect("a nearer empty base still beats a farther empty base",
            LaunchBaseScore(1_000f, 0, LaunchQueuePenaltyMeters) < LaunchBaseScore(10_000f, 0, LaunchQueuePenaltyMeters), true);
        Expect("a base 16 km off with nobody waiting loses to one next door with one waiting",
            LaunchBaseScore(16_000f, 0, LaunchQueuePenaltyMeters) > LaunchBaseScore(0f, 1, LaunchQueuePenaltyMeters), true);
        Expect("a negative distance never scores below zero", LaunchBaseScore(-500f, 0, LaunchQueuePenaltyMeters), 0f);
    }

    /// <summary>
    /// The internal-cannon rule (user, 2026-09-14): no loadout the commander flies carries a gun
    /// mount while INTERNAL CANNONS is off. Driven end to end over the real
    /// <c>WithoutInternalCannons</c> with probe mounts, because the failure it guards against is
    /// silent — an AI airframe with rounds left keeps making gun runs in defended airspace instead
    /// of going home, and nothing in the log says why.
    /// <para>
    /// The setting is read live, so the check runs its cases only when the setting is OFF (the
    /// state that has a rule to break); with the setting on it asserts the opposite, that the gun
    /// survives. Either way the console says which case failed.
    /// </para>
    /// </summary>
    private static void CheckInternalCannonRule()
    {
        WeaponMount cannon = MakeGunMount("30mm cannon", ammo: 450);
        WeaponMount missile = MakeCasMount("AGM-68", antiSurface: 0.9f, ammo: 2);

        Expect("a gun mount reads as a gun", CommanderAirCommandService.IsGunMount(cannon), true);
        Expect("a missile mount is never a gun", CommanderAirCommandService.IsGunMount(missile), false);
        Expect("an empty pylon is never a gun", CommanderAirCommandService.IsGunMount(null), false);

        Loadout mixed = new();
        mixed.weapons.Add(cannon);
        mixed.weapons.Add(missile);
        Loadout strippedMixed = CommanderAirCommandService.WithoutInternalCannons(mixed);
        Expect(
            CommanderSettings.AirIncludeInternalCannons
                ? "the gun stays in the loadout while INTERNAL CANNONS is on"
                : "the role loadout builder never returns a gun mount while INTERNAL CANNONS is off",
            CommanderAirCommandService.LoadoutHasGunMount(strippedMixed),
            CommanderSettings.AirIncludeInternalCannons);
        Expect(
            "stripping the gun leaves every other store on the aircraft",
            CommanderAirCommandService.LoadoutHasGunMount(strippedMixed)
                || ReferenceEquals(strippedMixed.weapons[1], missile),
            true);

        // The gun-only airframe: stripping would leave it with nothing to find a target with, so the
        // original stands. An unarmed aeroplane never comes home either.
        Loadout gunOnly = new();
        gunOnly.weapons.Add(cannon);
        Expect(
            "an airframe whose only store is its gun keeps it rather than flying unarmed",
            CommanderAirCommandService.LoadoutHasGunMount(
                CommanderAirCommandService.WithoutInternalCannons(gunOnly)),
            true);

        Expect("an empty loadout carries no gun", CommanderAirCommandService.LoadoutHasGunMount(new Loadout()), false);
        Expect("no loadout at all carries no gun", CommanderAirCommandService.LoadoutHasGunMount(null), false);

        Object.Destroy(cannon.info);
        Object.Destroy(cannon);
        Object.Destroy(missile.info);
        Object.Destroy(missile);
    }

    /// <summary>A probe mount for the cannon check: the game's own <c>WeaponInfo.gun</c> flag with a
    /// rack size, which is everything the strip reads.</summary>
    private static WeaponMount MakeGunMount(string weaponName, int ammo)
    {
        WeaponInfo info = ScriptableObject.CreateInstance<WeaponInfo>();
        info.weaponName = weaponName;
        info.gun = true;
        WeaponMount mount = ScriptableObject.CreateInstance<WeaponMount>();
        mount.mountName = weaponName;
        mount.info = info;
        mount.ammo = ammo;
        mount.GunAmmo = true;
        return mount;
    }

    /// <summary>
    /// The CAS ordnance preference (design.md, smarter-air-wing_20260914 Section 1): AGM-68 beats
    /// AGM-48, either beats any other air-to-ground store however heavy its rack, and a store with
    /// no ground effectiveness is never preferred whatever it is called. The bonus is what decides
    /// which store every commander-built CAS hardpoint carries, so a retune that shrinks it below
    /// an ordinary rack's score silently ends the preference.
    /// </summary>
    private static void CheckCasOrdnancePreference()
    {
        WeaponMount heavy = MakeCasMount("AGM-68", antiSurface: 0.9f, ammo: 2);
        // The real AGM-48's shape, as the installed build's roster line reported it: the game's
        // catalog declares NO damage for it, and it kills in play regardless (Departure 16). The
        // probe therefore carries no warhead on purpose — a rule that refused it would fail here.
        WeaponMount light = MakeCasMount("AGM-48 ", antiSurface: 0.64f, ammo: 4, warhead: false);
        WeaponMount rockets = MakeCasMount("S-24 Rocket Pod", antiSurface: 0.9f, ammo: 20);
        // The Eyeball Mk.II's own shape: the AGM-48's designation and rack with the sensor
        // variant's name on it, rated high against ground, no warhead. Everything except the name
        // says "ground attack", which is the whole reason the exclusion is keyed on the name.
        WeaponMount recon = MakeCasMount("AGM-48 Eyeball Mk.II", antiSurface: 0.9f, ammo: 4, warhead: false);
        WeaponMount nothing = MakeCasMount("R-73 Archer", antiSurface: 0f, ammo: 4);

        Expect("the AGM-68 is the first-tier CAS store", CommanderAirCommandService.PreferredCasOrdnanceRank(heavy), 0);
        Expect("the AGM-48 is the second-tier CAS store even though it declares no damage", CommanderAirCommandService.PreferredCasOrdnanceRank(light), 1);
        Expect("an ordinary rocket pod is not preferred CAS ordnance", CommanderAirCommandService.PreferredCasOrdnanceRank(rockets), -1);
        Expect("the Eyeball is a recon round, so it is not preferred CAS ordnance", CommanderAirCommandService.PreferredCasOrdnanceRank(recon), -1);
        Expect("an air-to-air missile is not preferred CAS ordnance", CommanderAirCommandService.PreferredCasOrdnanceRank(nothing), -1);

        float heavyScore = CommanderAirCommandService.ScoreCasMountForCommander(heavy);
        float lightScore = CommanderAirCommandService.ScoreCasMountForCommander(light);
        float rocketScore = CommanderAirCommandService.ScoreCasMountForCommander(rockets);
        float noneScore = CommanderAirCommandService.ScoreCasMountForCommander(nothing);
        Expect("the AGM-68 outranks the AGM-48", heavyScore > lightScore, true);
        Expect("the AGM-48 outranks a twenty-store rocket pod", lightScore > rocketScore, true);
        Expect("an ordinary ground store still outranks a weapon that cannot hit the ground", rocketScore > noneScore, true);
        Expect("a weapon with no ground effectiveness scores nothing for CAS", noneScore, 0f);

        // The recon exclusion itself (Departure 16): a sensor round rated 0.9 against ground,
        // carrying the AGM-48's own designation and a four-round rack, must still score nothing —
        // and must score BELOW the rocket pod it would otherwise have outranked outright.
        float reconScore = CommanderAirCommandService.ScoreCasMountForCommander(recon);
        Expect("a recon round scores nothing for CAS however it is rated", reconScore, 0f);
        Expect("a recon round never outranks a real ground store", rocketScore > reconScore, true);

        Object.Destroy(heavy);
        Object.Destroy(light);
        Object.Destroy(rockets);
        Object.Destroy(recon);
        Object.Destroy(nothing);
    }

    /// <summary>
    /// The suppression ordnance rule (user decision 2026-09-14: "ARADs are not using the correct
    /// ordinance, should be AGM-99 or AGM-68 etc"). A real anti-radiation missile wins its pylon
    /// against anything else the group offers; below it the named standoff stores go AGM-99 first,
    /// then AGM-68, then every other ground store on its own merits; and an air-to-air missile never
    /// reaches a suppression pylon at all, which is the whole of the bug this rule exists for.
    /// </summary>
    private static void CheckAradOrdnancePreference()
    {
        // The game's own anti-radiation missile, keyed the way the live rule keys it: an ARMSeeker
        // on the missile prefab. Named ARAD-116 because that is what the roster line prints.
        GameObject armSeekerPrefab = new("ArmSeekerProbe");
        armSeekerPrefab.AddComponent<ARMSeeker>();
        WeaponMount antiRadiation = MakeCasMount("ARAD-116", antiSurface: 0.68f, ammo: 4);
        antiRadiation.info.weaponPrefab = armSeekerPrefab;
        WeaponMount antiShip = MakeCasMount("AGM-99", antiSurface: 0.78f, ammo: 4);
        WeaponMount heavyStandoff = MakeCasMount("AGM-68", antiSurface: 0.9f, ammo: 2);
        WeaponMount ordinary = MakeCasMount("S-24 Rocket Pod", antiSurface: 0.9f, ammo: 20);
        WeaponMount airToAir = MakeCasMount("R-73 Archer", antiSurface: 0f, ammo: 4);

        Expect("the AGM-99 is the first-choice standoff store on a suppression sortie",
            CommanderAirCommandService.PreferredAradSecondaryRank(antiShip), 0);
        Expect("the AGM-68 is the second-choice standoff store",
            CommanderAirCommandService.PreferredAradSecondaryRank(heavyStandoff), 1);
        Expect("an ordinary rocket pod is not named standoff ordnance",
            CommanderAirCommandService.PreferredAradSecondaryRank(ordinary), -1);
        Expect("an air-to-air missile is never named standoff ordnance",
            CommanderAirCommandService.PreferredAradSecondaryRank(airToAir), -1);

        float armScore = CommanderAirCommandService.ScoreAradMountForCommander(antiRadiation);
        float shipScore = CommanderAirCommandService.ScoreAradMountForCommander(antiShip);
        float heavyScore = CommanderAirCommandService.ScoreAradMountForCommander(heavyStandoff);
        float ordinaryScore = CommanderAirCommandService.ScoreAradMountForCommander(ordinary);
        float airScore = CommanderAirCommandService.ScoreAradMountForCommander(airToAir);
        Expect("a real anti-radiation missile outranks every standoff store beside it", armScore > shipScore, true);
        Expect("the AGM-99 outranks the AGM-68 on a suppression sortie", shipScore > heavyScore, true);
        Expect("the AGM-68 outranks a twenty-store rocket pod", heavyScore > ordinaryScore, true);
        Expect("an ordinary ground store still scores something on a spare pylon", ordinaryScore > 0f, true);
        Expect("an air-to-air missile scores nothing on a suppression pylon", airScore, 0f);
        Expect("an empty pylon scores nothing", CommanderAirCommandService.ScoreAradMountForCommander(null), 0f);

        // The one that matters most: the loadout the launch line reports is the ARM, named and
        // counted, not the standoff store that happens to sit beside it.
        Loadout suppression = new();
        suppression.weapons.Add(antiRadiation);
        suppression.weapons.Add(antiShip);
        Expect(
            "a suppression loadout reports its anti-radiation stores with their rack size",
            CommanderAirCommandService.DescribeAradWeapons(suppression, withCounts: true),
            "ARAD-116 x4");
        Loadout standoffOnly = new();
        standoffOnly.weapons.Add(antiShip);
        standoffOnly.weapons.Add(heavyStandoff);
        Expect(
            "a loadout of standoff stores alone carries no anti-radiation ordnance at all",
            CommanderAirCommandService.DescribeAradWeapons(standoffOnly, withCounts: true),
            string.Empty);

        Object.Destroy(armSeekerPrefab);
        Object.Destroy(antiRadiation);
        Object.Destroy(antiShip);
        Object.Destroy(heavyStandoff);
        Object.Destroy(ordinary);
        Object.Destroy(airToAir);
    }

    /// <summary>
    /// The LAST RESORT admissibility rule (addendum 2026-09-14). The observed failure is the case
    /// worth naming: a Compass was on the roster and launchable, the review's remaining slice could
    /// not cover a second one, and the Cricket went up instead. The rule now asks only whether an
    /// ordinary airframe EXISTS, so that case buys nothing and saves.
    /// </summary>
    /// <summary>
    /// How many airframes of the element's chosen type this review orders (user decision
    /// 2026-09-14: "if there's 2x CAS aircraft, both should be the same… and ordered at the same
    /// time from the same location"). The whole element or none of it: buying one now and a
    /// different type next review is exactly the mixed package the decision exists to stop, and the
    /// allocation the element could not cover is worth more saved than spent.
    /// <para>
    /// The one exception is a sortie IN CONTACT, where one aeroplane now beats none: a platoon being
    /// shot at cannot wait two reviews for a matched pair, and the element's pinned type means the
    /// second one still matches the first when it is affordable.
    /// </para>
    /// A free airframe (a price of zero, which no real definition has) orders the whole element
    /// rather than dividing by zero. Pure, for the self-check.
    /// </summary>
    internal static int PackageElementBuys(int elementShort, float unitPrice, float budget, bool inContact)
    {
        int wanted = Mathf.Max(0, elementShort);
        int affordable = AffordableAirframes(wanted, unitPrice, budget);
        if (affordable >= wanted)
        {
            return wanted;
        }

        return inContact ? affordable : 0;
    }

    /// <summary>
    /// How many airframes of one type the allocation actually covers, never more than the element
    /// still wants. Extracted from <see cref="PackageElementBuys"/> on 2026-09-16 (Reuse rule 5)
    /// because the padding rule needs exactly the number that rule computes and then throws away
    /// whenever it is short of the whole element. A free airframe (a price of zero, which no real
    /// definition has) covers the whole element rather than dividing by zero. Pure, for the
    /// self-check.
    /// </summary>
    internal static int AffordableAirframes(int wanted, float unitPrice, float budget)
    {
        int asked = Mathf.Max(0, wanted);
        if (asked <= 0)
        {
            return 0;
        }

        float price = Mathf.Max(0f, unitPrice);
        if (price <= 0f)
        {
            return asked;
        }

        return Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0f, budget) / price), 0, asked);
    }

    /// <summary>
    /// How many airframes of the element's OWN chosen type this review orders once padding is
    /// allowed (user instruction 2026-09-16: cheap aircraft "pad out sorties that cannot afford full
    /// strength"). With padding off this is <see cref="PackageElementBuys"/> exactly as it was — the
    /// whole element or none of it. With padding on it is as many of the proper type as the
    /// allocation covers, because the slots it cannot cover are about to be filled by something
    /// cheaper rather than left empty, and "rather than left empty" is the only reason the
    /// whole-or-nothing rule ever existed.
    /// <para>
    /// The proper count is taken FIRST and is the most the allocation covers, so padding can never
    /// displace an airframe the sortie could otherwise have afforded: it only ever spends what is
    /// left over.
    /// </para>
    /// Pure, for the self-check.
    /// </summary>
    internal static int ElementProperBuys(
        int elementShort, float unitPrice, float budget, bool inContact, bool padsWithCheap)
    {
        int whole = PackageElementBuys(elementShort, unitPrice, budget, inContact);
        if (whole > 0 || !padsWithCheap)
        {
            return whole;
        }

        return AffordableAirframes(elementShort, unitPrice, budget);
    }

    /// <summary>
    /// How many slots of the element are left for a cheap airframe to fill. Padding needs something
    /// to pad: with no airframe of the proper type bought there is no element to pad out, and that
    /// case stays the 2026-09-13 rule that a commander SAVES for the aeroplane it wants rather than
    /// settling for the cheap one — which is the rule the easy-job test
    /// (<see cref="AirJobIsEasy"/>) answers honestly for the sorties where settling is right.
    /// Pure, for the self-check.
    /// </summary>
    internal static int ElementPaddingSlots(int elementShort, int properBought, bool padsWithCheap)
    {
        if (!padsWithCheap || properBought < 1)
        {
            return 0;
        }

        return Mathf.Max(0, Mathf.Max(0, elementShort) - properBought);
    }

    /// <summary>
    /// Whether an element keeps the type and base its first order picked (user decision
    /// 2026-09-14). It keeps them while all three hold: something was pinned, that airframe can
    /// still fill the role from a strip this commander holds, and the pinned base itself still
    /// accepts it. A base lost to the enemy, or a hangar list that no longer takes the type, releases
    /// the pin and the element re-picks — both fields together, never one of them. Pure, for the
    /// self-check.
    /// </summary>
    internal static bool KeepsPackageChoice(
        bool hasPinnedType, bool pinnedTypeStillLaunchable, bool pinnedBaseStillAccepts)
    {
        return hasPinnedType && pinnedTypeStillLaunchable && pinnedBaseStillAccepts;
    }

    /// <summary>
    /// The homogeneous-package rules (user decision 2026-09-14). Both are retunable into nonsense —
    /// an element that buys one airframe at a time is the mixed package the decision forbids, and a
    /// pin that never releases strands an element on a base the commander has lost — and neither
    /// failure says anything in the running game until a playtest shows a package of three different
    /// aeroplanes.
    /// </summary>
    private static void CheckPackageElements()
    {
        // Element sizing: the whole element or nothing, unless the sortie is in contact.
        Expect("a pair the allocation covers is ordered together", PackageElementBuys(2, 36f, 100f, false), 2);
        Expect("a pair the allocation exactly covers is ordered together", PackageElementBuys(2, 50f, 100f, false), 2);
        Expect("a pair the allocation cannot cover is not half-bought", PackageElementBuys(2, 36f, 50f, false), 0);
        Expect("a pair a sortie in contact cannot cover buys what it can now", PackageElementBuys(2, 36f, 50f, true), 1);
        Expect("a sortie in contact with nothing affordable still buys nothing", PackageElementBuys(2, 36f, 10f, true), 0);
        Expect("a single-airframe element the allocation covers is bought", PackageElementBuys(1, 36f, 50f, false), 1);
        Expect("a single-airframe element the allocation misses is not", PackageElementBuys(1, 36f, 10f, false), 0);
        Expect("a full element orders nothing more", PackageElementBuys(0, 36f, 500f, false), 0);
        Expect("a negative shortfall orders nothing", PackageElementBuys(-2, 36f, 500f, true), 0);
        Expect("a four-ship element is ordered whole when it fits", PackageElementBuys(4, 25f, 100f, false), 4);
        Expect("a four-ship element one airframe short of affordable saves", PackageElementBuys(4, 25f, 99f, false), 0);
        Expect("a priceless airframe never divides by zero", PackageElementBuys(3, 0f, 0f, false), 3);

        // Type and base persistence on a top-up after a loss.
        Expect("an element with nothing pinned picks fresh", KeepsPackageChoice(false, true, true), false);
        Expect("a pinned type still launchable from its pinned base is reused",
            KeepsPackageChoice(true, true, true), true);
        Expect("a pinned type the roster can no longer launch releases the pin",
            KeepsPackageChoice(true, false, true), false);
        Expect("a pinned base that no longer accepts the type releases the pin",
            KeepsPackageChoice(true, true, false), false);
        Expect("a base lost with the type still available still releases the pin",
            KeepsPackageChoice(true, false, false), false);
    }

    /// <summary>
    /// The cheap-airframe rules (user instruction 2026-09-16, "i also want to see increased use of
    /// the cheap aircraft"). Every one of them is retunable into nonsense with nothing in the running
    /// game to notice: an easy-job test that never says no puts trainers over a defended objective, a
    /// padding rule that runs before the proper airframes are counted spends the money they needed,
    /// and either of them firing while the side is bleeding or while a suppression sortie is waiting
    /// is a rule the user explicitly asked to be protected from.
    /// </summary>
    private static void CheckCheapAirframeUse()
    {
        // The observed-hostile limit is the top of the close-air-support ladder's own picket step.
        // Pinned to the ladder rather than restated, so retuning one and not the other fails here.
        Expect(
            "the easy-job observed limit is still the top of the close-air-support ladder's picket step",
            CommanderOperationsService.CasWanted(CheapAirframeMaxObserved),
            1);
        Expect(
            "one hostile past the easy-job limit already asks the ladder for a second airframe",
            CommanderOperationsService.CasWanted(CheapAirframeMaxObserved + 1),
            2);

        // The shared veto, one case per refusal.
        Expect("a quiet ground-attack sortie may consider the cheap airframe",
            CheapAirframesAdmissible(true, AirRole.Strike, bleeding: false, suppressionSortieWaiting: false), true);
        Expect("a fighter sortie may consider it too",
            CheapAirframesAdmissible(true, AirRole.Fighter, false, false), true);
        Expect("a rotary close-air-support sortie may consider it too",
            CheapAirframesAdmissible(true, AirRole.RotaryCas, false, false), true);
        Expect("a buy no sortie asked for never considers it (the home patrol and the threat fighter)",
            CheapAirframesAdmissible(false, AirRole.Fighter, false, false), false);
        Expect("a suppression buy never considers it: no cheap airframe carries an anti-radiation missile",
            CheapAirframesAdmissible(true, AirRole.Arad, false, false), false);
        Expect("a radar buy never considers it: no cheap airframe carries the radar pod",
            CheapAirframesAdmissible(true, AirRole.Awacs, false, false), false);
        Expect("a transport buy never considers it",
            CheapAirframesAdmissible(true, AirRole.Transport, false, false), false);
        Expect("a BLEEDING side never considers it; escalation outranks the cheap airframe",
            CheapAirframesAdmissible(true, AirRole.Strike, bleeding: true, suppressionSortieWaiting: false), false);
        Expect("a side with a suppression sortie still waiting never considers it",
            CheapAirframesAdmissible(true, AirRole.Strike, bleeding: false, suppressionSortieWaiting: true), false);

        // Where the line between an easy job and a real one is drawn, at every boundary.
        Expect("an objective with nothing over it and nothing on it is an easy job",
            AirJobIsEasy(true, true, 0, 0, 0, false), true);
        Expect("a picket exactly at the observed limit is still an easy job",
            AirJobIsEasy(true, true, 0, CheapAirframeMaxObserved, 0, false), true);
        Expect("one hostile past the limit is not an easy job",
            AirJobIsEasy(true, true, 0, CheapAirframeMaxObserved + 1, 0, false), false);
        Expect("a single tracked hostile aircraft makes it a real job",
            AirJobIsEasy(true, true, 1, 0, 0, false), false);
        Expect("a single observed air-defence vehicle makes it a real job",
            AirJobIsEasy(true, true, 0, 0, 1, false), false);
        Expect("an objective in contact is never an easy job",
            AirJobIsEasy(true, true, 0, 0, 0, true), false);
        Expect("the setting switched off leaves the bottom tier where it was",
            AirJobIsEasy(false, true, 0, 0, 0, false), false);
        Expect("a buy the shared veto refused is never an easy job either",
            AirJobIsEasy(true, false, 0, 0, 0, false), false);
        Expect("a negative tracking read never makes a job harder than nothing",
            AirJobIsEasy(true, true, -3, -3, -3, false), true);

        // The two precedences the user asked to be protected, written end to end rather than as
        // separate facts about the veto: bleeding wins, and the suppression budget is untouchable.
        Expect("a bleeding side's quiet objective still draws the proper aircraft",
            AirJobIsEasy(true, CheapAirframesAdmissible(true, AirRole.Strike, true, false), 0, 0, 0, false), false);
        Expect("a bleeding side pads nothing either",
            MayPadWithCheapAirframes(true, CheapAirframesAdmissible(true, AirRole.Strike, true, false)), false);
        Expect("a waiting suppression sortie keeps the cheap airframe off a quiet objective",
            AirJobIsEasy(true, CheapAirframesAdmissible(true, AirRole.Fighter, false, true), 0, 0, 0, false), false);
        Expect("a waiting suppression sortie stops the padding spending its budget",
            MayPadWithCheapAirframes(true, CheapAirframesAdmissible(true, AirRole.Strike, false, true)), false);
        Expect("padding is allowed for a HARD sortie, which is the one that must not fly short-handed",
            MayPadWithCheapAirframes(true, CheapAirframesAdmissible(true, AirRole.Strike, false, false)), true);
        Expect("the padding setting switched off stops it",
            MayPadWithCheapAirframes(false, CheapAirframesAdmissible(true, AirRole.Strike, false, false)), false);

        // The shared tier override, and that the escort still reads through it unchanged.
        Expect("a preferred tier that can launch and be paid for wins",
            PreferTier(true, true, true, AirframeTier.LastResort, AirframeTier.Fighter),
            AirframeTier.LastResort);
        Expect("nothing to prefer leaves the ordinary tier alone",
            PreferTier(false, true, true, AirframeTier.LastResort, AirframeTier.Fighter),
            AirframeTier.Fighter);
        Expect("a preferred tier nothing can launch leaves the ordinary tier alone",
            PreferTier(true, false, true, AirframeTier.LastResort, AirframeTier.Fighter),
            AirframeTier.Fighter);
        Expect("a preferred tier the element cannot pay for leaves the ordinary tier alone",
            PreferTier(true, true, false, AirframeTier.LastResort, AirframeTier.Fighter),
            AirframeTier.Fighter);
        Expect("a preferred tier still wins when the ordinary walk found no tier at all",
            PreferTier(true, true, true, AirframeTier.LastResort, null),
            AirframeTier.LastResort);
        Expect("the escort's multirole preference still reads through the shared override",
            EscortTier(ElementKind.Escort, true, true, AirframeTier.Fighter),
            AirframeTier.Multirole);

        // The padding arithmetic. The load-bearing case is the last one: the proper count is the
        // most the allocation covers, so padding can only ever spend what is left over.
        Expect("padding off leaves the whole-element rule exactly as it was",
            ElementProperBuys(2, 65f, 100f, false, false), 0);
        Expect("padding on buys the one proper airframe the allocation covers",
            ElementProperBuys(2, 65f, 100f, false, true), 1);
        Expect("and leaves one slot for a cheap airframe",
            ElementPaddingSlots(2, 1, true), 1);
        Expect("an allocation that covers the whole element pads nothing",
            ElementProperBuys(2, 40f, 100f, false, true), 2);
        Expect("an element bought whole has no slots left to pad",
            ElementPaddingSlots(2, 2, true), 0);
        Expect("an allocation that covers no proper airframe buys none",
            ElementProperBuys(2, 65f, 60f, false, true), 0);
        Expect("and pads nothing, because padding pads an element rather than replacing one",
            ElementPaddingSlots(2, 0, true), 0);
        Expect("a four-ship element one airframe short pads the three slots it could not cover",
            ElementPaddingSlots(4, 1, true), 3);
        Expect("padding switched off leaves no slots to fill",
            ElementPaddingSlots(2, 1, false), 0);
        Expect("a sortie in contact keeps its own partial-buy rule untouched",
            ElementProperBuys(2, 36f, 50f, true, false), 1);
        Expect("padding never displaces an airframe the allocation could have covered",
            ElementProperBuys(3, 40f, 100f, false, true), AffordableAirframes(3, 40f, 100f));
        Expect("the affordable count never exceeds what the element still wants",
            AffordableAirframes(2, 25f, 500f), 2);
        Expect("the affordable count of an empty element is nothing",
            AffordableAirframes(0, 25f, 500f), 0);
        Expect("a negative allocation covers nothing",
            AffordableAirframes(2, 25f, -5f), 0);
        Expect("a priceless airframe still never divides by zero",
            AffordableAirframes(3, 0f, 0f), 3);

        CheckPaddingPick();
    }

    /// <summary>The padding pick at its boundaries: cheapest wins, the last-resort airframe is still
    /// ranked below every ordinary one, and the diversity cap skips a type only while another is
    /// affordable.</summary>
    private static void CheckPaddingPick()
    {
        AirframeCandidate[] roster =
        {
            new(0, AirframeTier.Fighter, price: 65f, rating: 1f, lastResort: false),
            new(1, AirframeTier.LastResort, price: 22f, rating: 0.62f, lastResort: false),
            new(2, AirframeTier.LastResort, price: 12f, rating: 0.44f, lastResort: true),
        };
        Expect("padding takes the cheapest ORDINARY airframe, never the last-resort one",
            CheapestPaddingIndex(roster, 100f), 1);
        Expect("a leftover that reaches only the last-resort airframe takes it",
            CheapestPaddingIndex(roster, 20f), 2);
        Expect("a leftover that reaches nothing pads nothing",
            CheapestPaddingIndex(roster, 5f), -1);

        AirframeCandidate[] crowded =
        {
            new(0, AirframeTier.Fighter, price: 65f, rating: 1f, lastResort: false),
            new(1, AirframeTier.LastResort, price: 22f, rating: 0.62f, lastResort: false, shareExceeded: true),
        };
        Expect("a padding type already past the diversity cap is skipped while anything else fits",
            CheapestPaddingIndex(crowded, 100f), 0);

        AirframeCandidate[] allCrowded =
        {
            new(0, AirframeTier.Fighter, price: 65f, rating: 1f, lastResort: false, shareExceeded: true),
            new(1, AirframeTier.LastResort, price: 22f, rating: 0.62f, lastResort: false, shareExceeded: true),
        };
        Expect("a sky where every type is past the cap still pads rather than leaving the slot empty",
            CheapestPaddingIndex(allCrowded, 100f), 1);

        AirframeCandidate[] tied =
        {
            new(0, AirframeTier.LastResort, price: 22f, rating: 0.5f, lastResort: false),
            new(1, AirframeTier.LastResort, price: 22f, rating: 0.64f, lastResort: false),
        };
        Expect("two padding candidates at one price are separated by the better rating",
            CheapestPaddingIndex(tied, 100f), 1);

        AirframeCandidate[] excluded =
        {
            new(0, AirframeTier.Excluded, price: 5f, rating: 0.9f, lastResort: false),
            new(1, AirframeTier.LastResort, price: 22f, rating: 0.62f, lastResort: false),
        };
        Expect("a transport is never a padding airframe, however cheap it is",
            CheapestPaddingIndex(excluded, 100f), 1);
    }

    private static void CheckLastResortRule()
    {
        Expect("the last-resort airframe flies when the roster holds nothing else for the role",
            LastResortAllowed(ordinaryCandidateAtAnyPrice: false), true);
        Expect("an ordinary airframe on the roster keeps the last-resort airframe on the ground",
            LastResortAllowed(ordinaryCandidateAtAnyPrice: true), false);
        Expect("an ordinary airframe this review cannot afford still keeps the last-resort airframe grounded "
                + "(the T/A-30 Compass / CI-22 Cricket bug, 2026-09-14)",
            LastResortAllowed(ordinaryCandidateAtAnyPrice: true), false);
    }

    /// <summary>A probe mount for the ordnance checks: a named, non-nuclear conventional missile
    /// with a rack size, which is everything <c>PreferredCasOrdnanceRank</c> and the CAS scorer
    /// read.</summary>
    /// <param name="warhead">Whether the probe carries a warhead — the two damage numbers a real
    /// store declares. Default true, because a real air-to-ground missile has one: leaving them at
    /// zero made every probe read as a warhead-less sensor round the moment the warhead rule landed
    /// (fix, 2026-09-14), which collapsed all four CAS scores to zero and failed three ordnance
    /// checks that had nothing wrong with them. Pass false to model the Eyeball Mk.II.</param>
    private static WeaponMount MakeCasMount(string weaponName, float antiSurface, int ammo, bool warhead = true)
    {
        WeaponInfo info = ScriptableObject.CreateInstance<WeaponInfo>();
        info.weaponName = weaponName;
        info.missile = true;
        info.effectiveness.antiSurface = antiSurface;
        if (warhead)
        {
            // The AGM-68's shape: damage declared on the store itself. Nothing decides anything on
            // these two numbers any more (Departure 16) — they are kept so the probes model the
            // real catalog, where the AGM-68 declares 700/120 and the AGM-48 declares nothing at
            // all and kills anyway.
            info.pierceDamage = 60f;
            info.blastDamage = 200f;
        }

        WeaponMount mount = ScriptableObject.CreateInstance<WeaponMount>();
        mount.mountName = weaponName;
        mount.info = info;
        mount.ammo = ammo;
        return mount;
    }

    /// <summary>
    /// The airframe fitness tiers and the rule that picks inside one (design.md,
    /// airframe-selection_20260914, DECISION-011). Every number here can be retuned into nonsense —
    /// a <see cref="FighterRatio"/> nudged down puts the Ifrit ahead of the Revoker on CAP, a
    /// threshold nudged up buys Crickets into a raid — and nothing in the running game would say so
    /// until a playtest went badly. This is what says so at plugin load.
    /// </summary>
    private static void CheckAirframeTiers()
    {
        // Section 1: the tier table, read at the ratio boundary in both directions.
        Expect("an airframe rated exactly the ratio above its other role is a fighter",
            TierOf(antiAir: 1.5f, antiSurface: 1f, AirRole.Fighter), AirframeTier.Fighter);
        Expect("just under the ratio is a multirole, not a fighter",
            TierOf(antiAir: 1.49f, antiSurface: 1f, AirRole.Fighter), AirframeTier.Multirole);
        Expect("the mirror of the ratio is a strike airframe",
            TierOf(antiAir: 1f, antiSurface: 1.5f, AirRole.Fighter), AirframeTier.Strike);
        Expect("just under the mirrored ratio is a multirole too",
            TierOf(antiAir: 1f, antiSurface: 1.49f, AirRole.Fighter), AirframeTier.Multirole);
        Expect("an airframe the game rates at nothing either way is a multirole, never a fighter",
            TierOf(antiAir: 0f, antiSurface: 0f, AirRole.Fighter), AirframeTier.Multirole);
        Expect("a tier is the same airframe fact whichever role is asking",
            TierOf(antiAir: 1f, antiSurface: 1.5f, AirRole.Strike), AirframeTier.Strike);

        // The bottom tier: the last resort and the trainers, whatever their ratings say.
        AircraftDefinition cricket = ScriptableObject.CreateInstance<AircraftDefinition>();
        cricket.jsonKey = "COIN";
        cricket.roleIdentity.antiSurface = 1f;
        AircraftDefinition compass = ScriptableObject.CreateInstance<AircraftDefinition>();
        compass.jsonKey = "trainer";
        compass.roleIdentity.antiSurface = 1f;
        AircraftDefinition vagrant = ScriptableObject.CreateInstance<AircraftDefinition>();
        vagrant.jsonKey = "VTOLTrainer1";
        vagrant.roleIdentity.antiSurface = 1f;
        AircraftDefinition revoker = ScriptableObject.CreateInstance<AircraftDefinition>();
        revoker.jsonKey = "Fighter1";
        revoker.roleIdentity.antiAir = 1f;
        Expect("the CI-22 Cricket is bottom tier for CAP", ForRole(cricket, AirRole.Fighter), AirframeTier.LastResort);
        Expect("the CI-22 Cricket is bottom tier for CAS too", ForRole(cricket, AirRole.Strike), AirframeTier.LastResort);
        Expect("the T/A-30 Compass trainer is bottom tier however it is rated",
            ForRole(compass, AirRole.Fighter), AirframeTier.LastResort);
        Expect("the VT-7 Vagrant's trainer data key puts it in the bottom tier as well",
            ForRole(vagrant, AirRole.Strike), AirframeTier.LastResort);
        Expect("the T/A-30 Compass is a trainer", IsTrainerAirframe(compass), true);
        Expect("the FS-12 Revoker is not a trainer", IsTrainerAirframe(revoker), false);
        Expect("every AWACS candidate sits in one tier, because the radar pod is the qualification",
            ForRole(revoker, AirRole.Awacs), AirframeTier.Multirole);
        Object.Destroy(cricket);
        Object.Destroy(compass);
        Object.Destroy(vagrant);
        Object.Destroy(revoker);

        // Section 2: the CAS order is the CAP order reversed, except that the bottom stays bottom.
        AirframeTier[] cap = TierOrder(AirRole.Fighter);
        AirframeTier[] cas = TierOrder(AirRole.Strike);
        Expect("CAP shops for a fighter first", cap[0], AirframeTier.Fighter);
        Expect("CAS shops for a strike airframe first", cas[0], AirframeTier.Strike);
        Expect("the CAS order is the CAP order reversed in the middle", cas[1], cap[1]);
        Expect("the CAS order ends on the CAP order's first tier", cas[2], cap[0]);
        Expect("the bottom tier is the bottom of both orders", cas[3], cap[3]);
        Expect("the bottom of the CAP order is the last resort", cap[3], AirframeTier.LastResort);
        Expect("ARAD walks the CAS order", TierOrder(AirRole.Arad)[0], AirframeTier.Strike);
        Expect("rotary CAS walks the CAS order", TierOrder(AirRole.RotaryCas)[0], AirframeTier.Strike);

        // The highest launchable tier, including the empty-tier skip the design asks for.
        AirframeTier[] noFighterOnTheStrips = { AirframeTier.Strike, AirframeTier.LastResort };
        Expect("a CAP buy with no fighter and no multirole launchable falls to the strike tier",
            HighestLaunchableTier(noFighterOnTheStrips, AirRole.Fighter), AirframeTier.Strike);
        AirframeTier[] fullRoster =
        {
            AirframeTier.Strike, AirframeTier.LastResort, AirframeTier.Fighter, AirframeTier.Multirole,
        };
        Expect("a strike airframe is never the CAP answer while a fighter can launch",
            HighestLaunchableTier(fullRoster, AirRole.Fighter), AirframeTier.Fighter);
        Expect("a fighter is never the CAS answer while a strike airframe can launch",
            HighestLaunchableTier(fullRoster, AirRole.Strike), AirframeTier.Strike);
        AirframeTier[] trainersOnly = { AirframeTier.LastResort };
        Expect("a roster of trainers still launches something",
            HighestLaunchableTier(trainersOnly, AirRole.Fighter), AirframeTier.LastResort);
        Expect("nothing launchable is nothing chosen",
            HighestLaunchableTier(System.Array.Empty<AirframeTier>(), AirRole.Fighter), null);

        // The air-superiority REFUSAL (user report, 2026-09-14: "seeing a lot of air superiority
        // brawlers - SHOULDN'T BE, they're CAS aircraft"). Every path that can hand an airframe a
        // patrol, an escort or a sortie CAP slot reads this through one of the two capability
        // gates, so these cases stand for all of them.
        AircraftDefinition brawler = ScriptableObject.CreateInstance<AircraftDefinition>();
        brawler.jsonKey = "CAS1";
        brawler.roleIdentity.antiAir = 0.30f;
        brawler.roleIdentity.antiSurface = 0.80f;
        AircraftDefinition chicane = ScriptableObject.CreateInstance<AircraftDefinition>();
        chicane.jsonKey = "AttackHelo1";
        chicane.roleIdentity.antiAir = 0.27f;
        chicane.roleIdentity.antiSurface = 0.90f;
        AircraftDefinition revoker2 = ScriptableObject.CreateInstance<AircraftDefinition>();
        revoker2.jsonKey = "Fighter1";
        revoker2.roleIdentity.antiAir = 1.00f;
        revoker2.roleIdentity.antiSurface = 0.46f;
        AircraftDefinition alkyon = ScriptableObject.CreateInstance<AircraftDefinition>();
        alkyon.jsonKey = "FastBomber1";
        alkyon.roleIdentity.antiAir = 0.70f;
        alkyon.roleIdentity.antiSurface = 1.00f;
        AircraftDefinition compass2 = ScriptableObject.CreateInstance<AircraftDefinition>();
        compass2.jsonKey = "trainer";
        compass2.roleIdentity.antiAir = 0.62f;
        compass2.roleIdentity.antiSurface = 0.64f;
        Expect("the A-19 Brawler is refused every air-superiority task, at the game's own ratings",
            MayFlyAirSuperiority(brawler), false);
        Expect("the SAH-46 Chicane is refused every air-superiority task",
            MayFlyAirSuperiority(chicane), false);
        Expect("the FS-12 Revoker flies air superiority", MayFlyAirSuperiority(revoker2), true);
        // Was "a multirole bomber is still allowed to escort" and expected true until 2026-09-18.
        // The user watched a match and decided the Alkyon AB-4 is ground-attack only, so the whole
        // multirole tier is now refused every fighter task — patrol and escort alike.
        Expect("the Alkyon AB-4 is ground-attack only and flies no fighter task", MayFlyAirSuperiority(alkyon), false);
        Expect("the multirole refusal does not touch the job the AB-4 IS for",
            ForRole(alkyon, AirRole.Strike), AirframeTier.Multirole);
        Expect("the T/A-30 Compass may hold the patrol when nothing better can launch",
            MayFlyAirSuperiority(compass2), true);
        Expect("the A-19 Brawler is exactly the strike tier the refusal keys on",
            ForRole(brawler, AirRole.Fighter), AirframeTier.Strike);
        Expect("refusing the patrol does not refuse the job it IS for",
            ForRole(brawler, AirRole.Strike), AirframeTier.Strike);

        // The structural exclusions (user report, 2026-09-14: "it's spawned a UH-90 transport for
        // CAP for a platoon"). A transport carries real role ratings — the Ibis is 0.27 / 0.90,
        // which is the strike tier by arithmetic — so nothing but an explicit exclusion keeps it
        // out of the combat tiers.
        AircraftDefinition ibis = ScriptableObject.CreateInstance<AircraftDefinition>();
        ibis.jsonKey = "UtilityHelo1";
        ibis.captureCapacity = 8;
        ibis.roleIdentity.antiAir = 0.27f;
        ibis.roleIdentity.antiSurface = 0.90f;
        Expect("a UH-90 Ibis troop helicopter is no CAP candidate, whatever its ratings say",
            ForRole(ibis, AirRole.Fighter), AirframeTier.Excluded);
        Expect("a transport is no CAS candidate either", ForRole(ibis, AirRole.Strike), AirframeTier.Excluded);
        Expect("a transport is no ARAD candidate", ForRole(ibis, AirRole.Arad), AirframeTier.Excluded);
        Expect("a radar airframe may fly the AWACS station", RadarAirframeMayFill(AirRole.Awacs), true);
        Expect("a radar airframe never flies a suppression sortie", RadarAirframeMayFill(AirRole.Arad), false);
        Expect("a radar airframe never flies a patrol", RadarAirframeMayFill(AirRole.Fighter), false);
        Expect("a radar airframe never flies a strike", RadarAirframeMayFill(AirRole.Strike), false);
        Expect("a transport is no AWACS candidate", ForRole(ibis, AirRole.Awacs), AirframeTier.Excluded);
        Expect("a transport is still the candidate for the transport role",
            ForRole(ibis, AirRole.Transport), AirframeTier.Multirole);
        Expect("a transport is refused every combat role through the shared gate",
            MayFillRole(ibis, AirRole.Fighter), false);
        Expect("a transport is refused air superiority", MayFlyAirSuperiority(ibis), false);
        Expect("the roster line says WHY a transport has no tier",
            DescribeTier(hq: null!, state: null, ibis, AirRole.Fighter), "excluded (transport)");
        Expect("a combat airframe is not offered for the transport role",
            ForRole(brawler, AirRole.Transport), AirframeTier.Excluded);
        Object.Destroy(ibis);

        // No plane pilot, no tasking. These probe definitions carry no prefab at all, so
        // HasPlanePilot is false for every one of them — which is the condition being asserted, and
        // also why the tier table itself must NOT read the prefab: ForRole has to stay answerable
        // from the two role ratings alone, and the prefab refusals live in MayFillRole beside it.
        Expect("an airframe nothing can task is refused every combat role, however it is rated",
            MayFillRole(revoker2, AirRole.Fighter), false);
        Expect("the tier table still answers for it, because a tier is a rating fact",
            ForRole(revoker2, AirRole.Fighter), AirframeTier.Fighter);
        Expect("the roster line names the reason rather than printing a tier it can never be picked from",
            DescribeTier(hq: null!, state: null, revoker2, AirRole.Fighter), "excluded (no plane pilot)");
        Expect("the tier half of the refusal is separate, so a real fighter still passes it",
            MayFlyAirSuperiority(revoker2), true);
        Object.Destroy(brawler);
        Object.Destroy(chicane);
        Object.Destroy(revoker2);
        Object.Destroy(alkyon);
        Object.Destroy(compass2);

        // The same order, asked one pair at a time — what the sortie fill and the idle sweep read
        // when they are choosing between airframes the commander already owns.
        Expect("an owned fighter is preferred to an owned strike airframe for a CAP slot",
            TierBeats(AirframeTier.Fighter, AirframeTier.Strike, AirRole.Fighter), true);
        Expect("an owned strike airframe never displaces an owned fighter on CAP",
            TierBeats(AirframeTier.Strike, AirframeTier.Fighter, AirRole.Fighter), false);
        Expect("an owned strike airframe is preferred for a CAS slot",
            TierBeats(AirframeTier.Strike, AirframeTier.Fighter, AirRole.Strike), true);
        Expect("a multirole beats a trainer for either job",
            TierBeats(AirframeTier.Multirole, AirframeTier.LastResort, AirRole.Strike), true);
        Expect("a tier never beats itself", TierBeats(AirframeTier.Fighter, AirframeTier.Fighter, AirRole.Fighter), false);

        // The threat read, checked either side of both thresholds.
        Expect("one tracked hostile aircraft is a quiet sky for a CAP buy",
            ThreatHigh(AirRole.Fighter, trackedAircraft: 1, observedHostiles: 0), false);
        Expect("two tracked hostile aircraft buy the best fighter the tier holds",
            ThreatHigh(AirRole.Fighter, trackedAircraft: 2, observedHostiles: 0), true);
        Expect("two observed hostiles are a quiet objective for a CAS buy",
            ThreatHigh(AirRole.Strike, trackedAircraft: 0, observedHostiles: 2), false);
        Expect("three observed hostiles buy the best strike airframe the tier holds",
            ThreatHigh(AirRole.Strike, trackedAircraft: 0, observedHostiles: 3), true);
        Expect("a CAS buy reads the ground, not the sky",
            ThreatHigh(AirRole.Strike, trackedAircraft: 9, observedHostiles: 0), false);
        Expect("a CAP buy reads the sky, not the ground",
            ThreatHigh(AirRole.Fighter, trackedAircraft: 0, observedHostiles: 9), false);
        Expect("a transport is bought on price whatever is happening, in the sky or on the ground",
            ThreatHigh(AirRole.Transport, trackedAircraft: 9, observedHostiles: 9), false);

        // Choosing inside a tier: the FS-12 Revoker (65, rated 1.0) against the FS-20 Vortex
        // (90, rated 1.2), both fighter tier, with the CI-22 Cricket sitting in the bottom tier.
        AirframeCandidate[] roster =
        {
            new(0, AirframeTier.Fighter, price: 65f, rating: 1.0f, lastResort: false),
            new(1, AirframeTier.Fighter, price: 90f, rating: 1.2f, lastResort: false),
            new(2, AirframeTier.LastResort, price: 12f, rating: 0.3f, lastResort: true),
            new(3, AirframeTier.LastResort, price: 22f, rating: 0.4f, lastResort: false),
        };
        Expect("a quiet sky buys the cheapest fighter in the tier",
            SelectInTier(roster, AirframeTier.Fighter, threatHigh: false, allocation: 500f), 0);
        Expect("a threatened sky buys the best fighter the allocation covers",
            SelectInTier(roster, AirframeTier.Fighter, threatHigh: true, allocation: 500f), 1);
        Expect("a candidate priced above the allocation is never chosen",
            SelectInTier(roster, AirframeTier.Fighter, threatHigh: true, allocation: 80f), 0);
        Expect("a tier the allocation cannot reach waits rather than dropping a tier",
            SelectInTier(roster, AirframeTier.Fighter, threatHigh: true, allocation: 40f), -1);
        Expect("the CI-22 Cricket stays on the ground while the T/A-30 Compass can fly, however much cheaper it is",
            SelectInTier(roster, AirframeTier.LastResort, threatHigh: false, allocation: 500f), 3);
        Expect("the CI-22 Cricket flies when the Compass is out of reach",
            SelectInTier(roster, AirframeTier.LastResort, threatHigh: false, allocation: 15f), 2);
        Expect("an empty tier chooses nothing",
            SelectInTier(roster, AirframeTier.Multirole, threatHigh: false, allocation: 500f), -1);

        // Role fit per element and the diversity cap (design.md, strike-packages_20260915
        // Section 3, user decision 2026-09-15).
        CheckElementFit();

        // The attrition brake (user decision 2026-09-15), in its own file.
        CheckAirAttrition();
    }

    /// <summary>
    /// The per-element airframe preferences and the diversity cap (design.md,
    /// strike-packages_20260915 Section 3). Every one of these can be retuned or edited into the
    /// monoculture the whole track exists to break — a preference that reads the wrong rating, a cap
    /// that fires on the first airframe of a match and grounds the wing — and none of them says
    /// anything in the running game until a playtest shows twenty of the same aeroplane.
    /// </summary>
    private static void CheckElementFit()
    {
        Expect("a strike element excludes helicopters", RotaryExcludedForElement(ElementKind.Strike), true);
        Expect("a bomber element excludes helicopters", RotaryExcludedForElement(ElementKind.Bomber), true);
        Expect("an escort element may still be whatever the role rules allow", RotaryExcludedForElement(ElementKind.Escort), false);
        Expect("an ordinary buy keeps its rotary rules", RotaryExcludedForElement(ElementKind.Other), false);
        // The bomber classification, on the game's own data key (the Alkyon AB-4 is FastBomber1).
        AircraftDefinition alkyon = ScriptableObject.CreateInstance<AircraftDefinition>();
        alkyon.jsonKey = "FastBomber1";
        AircraftDefinition heavyBomber = ScriptableObject.CreateInstance<AircraftDefinition>();
        heavyBomber.jsonKey = "Bomber2";
        AircraftDefinition brawler2 = ScriptableObject.CreateInstance<AircraftDefinition>();
        brawler2.jsonKey = "CAS1";
        Expect("a fast bomber's data key names it a bomber", IsBomberAirframe(alkyon), true);
        Expect("a plain bomber's data key names it one too", IsBomberAirframe(heavyBomber), true);
        Expect("a ground-attack jet is not a bomber", IsBomberAirframe(brawler2), false);
        Object.Destroy(alkyon);
        Object.Destroy(heavyBomber);
        Object.Destroy(brawler2);

        // Two fighter-tier candidates: a cheap good one (65, A/A 1.0) and a dear better one
        // (130, A/A 1.2). Value for money picks the first; "best affordable" picks the second.
        AirframeCandidate[] fighters =
        {
            new(0, AirframeTier.Fighter, price: 65f, rating: 1.0f, lastResort: false, antiAir: 1.0f),
            new(1, AirframeTier.Fighter, price: 130f, rating: 1.2f, lastResort: false, antiAir: 1.2f),
        };
        Expect(
            "the home patrol buys the fighter that gives the most air-to-air per credit",
            SelectInTier(fighters, AirframeTier.Fighter, threatHigh: true, allocation: 500f, ElementKind.HighCap),
            0);
        Expect(
            "a patrol that cannot afford the dear one still buys the cheap one",
            SelectInTier(fighters, AirframeTier.Fighter, threatHigh: true, allocation: 100f, ElementKind.HighCap),
            0);
        Expect(
            "naming no element leaves the old best-affordable rule exactly as it was",
            SelectInTier(fighters, AirframeTier.Fighter, threatHigh: true, allocation: 500f),
            1);

        // The escort's tier choice: the multirole tier when one can launch and be paid for.
        Expect(
            "an escort shops the multirole tier when one is launchable and affordable",
            EscortTier(ElementKind.Escort, true, true, AirframeTier.Fighter),
            AirframeTier.Multirole);
        Expect(
            "an escort with no multirole on the strips keeps the ordinary answer",
            EscortTier(ElementKind.Escort, false, true, AirframeTier.Fighter),
            AirframeTier.Fighter);
        Expect(
            "an escort that cannot afford the multirole keeps the ordinary answer",
            EscortTier(ElementKind.Escort, true, false, AirframeTier.Fighter),
            AirframeTier.Fighter);
        Expect(
            "the home patrol is not an escort and never has its tier changed",
            EscortTier(ElementKind.HighCap, true, true, AirframeTier.Fighter),
            AirframeTier.Fighter);
        Expect(
            "an ordinary buy is not an escort either",
            EscortTier(ElementKind.Other, true, true, AirframeTier.Strike),
            AirframeTier.Strike);
        Expect(
            "an escort with nothing launchable at all still has nothing to buy",
            EscortTier(ElementKind.Escort, false, false, null),
            null);

        // The strike and bomber elements, in the strike tier: a cheap weak jet, a dear strong one,
        // and a bomber-class airframe that is neither the cheapest nor the best rated.
        AirframeCandidate[] attackers =
        {
            new(0, AirframeTier.Strike, price: 40f, rating: 0.6f, lastResort: false, antiSurface: 0.6f),
            new(1, AirframeTier.Strike, price: 145f, rating: 1.0f, lastResort: false, antiSurface: 1.0f),
            new(2, AirframeTier.Strike, price: 90f, rating: 0.8f, lastResort: false, antiSurface: 0.8f, bomberClass: true),
        };
        Expect(
            "a strike element buys the most anti-surface it can afford, quiet or not",
            SelectInTier(attackers, AirframeTier.Strike, threatHigh: false, allocation: 500f, ElementKind.Strike),
            1);
        Expect(
            "a strike element the allocation limits still buys the best it can reach",
            SelectInTier(attackers, AirframeTier.Strike, threatHigh: false, allocation: 100f, ElementKind.Strike),
            2);
        Expect(
            "a bomber element buys the bomber-class airframe over the better-rated jet",
            SelectInTier(attackers, AirframeTier.Strike, threatHigh: true, allocation: 500f, ElementKind.Bomber),
            2);
        Expect(
            "a bomber element with no bomber it can afford falls back to anti-surface",
            SelectInTier(attackers, AirframeTier.Strike, threatHigh: true, allocation: 50f, ElementKind.Bomber),
            0);
        Expect(
            "naming no element leaves a quiet CAS buy on the cheapest, exactly as before",
            SelectInTier(attackers, AirframeTier.Strike, threatHigh: false, allocation: 500f),
            0);

        // The diversity cap itself.
        Expect("three of four airframes of one type is past a sixty percent cap", TypeShareExceeded(3, 4, 0.6f), true);
        Expect("two of four is not", TypeShareExceeded(2, 4, 0.6f), false);
        Expect("the only airframe of a one-aircraft side is never capped", TypeShareExceeded(1, 1, 0.6f), false);
        Expect("two of two is too small a sample to cap", TypeShareExceeded(2, 2, 0.6f), false);
        Expect("three of three is a sample, and it is past the cap", TypeShareExceeded(3, 3, 0.6f), true);
        Expect("a type nothing of which is flying is never capped", TypeShareExceeded(0, 20, 0.6f), false);
        Expect("exactly sixty percent of a big side is not past the cap", TypeShareExceeded(6, 10, 0.6f), false);
        Expect("one more than sixty percent is", TypeShareExceeded(7, 10, 0.6f), true);
        Expect(
            "the sample floor is more than a pair, or the first buy of a match would be refused",
            TypeShareSampleFloor >= 3,
            true);

        // The cap SKIPS, it never grounds: a tier in which everything is over-represented still buys.
        AirframeCandidate[] crowded =
        {
            new(0, AirframeTier.Fighter, price: 65f, rating: 1.0f, lastResort: false, antiAir: 1.0f, shareExceeded: true),
            new(1, AirframeTier.Fighter, price: 130f, rating: 1.2f, lastResort: false, antiAir: 1.2f),
        };
        Expect(
            "an over-represented type is skipped while another is available",
            SelectInTier(crowded, AirframeTier.Fighter, threatHigh: false, allocation: 500f),
            1);
        AirframeCandidate[] allCrowded =
        {
            new(0, AirframeTier.Fighter, price: 65f, rating: 1.0f, lastResort: false, antiAir: 1.0f, shareExceeded: true),
            new(1, AirframeTier.Fighter, price: 130f, rating: 1.2f, lastResort: false, antiAir: 1.2f, shareExceeded: true),
        };
        Expect(
            "a tier in which every type is over-represented still buys rather than grounding the wing",
            SelectInTier(allCrowded, AirframeTier.Fighter, threatHigh: false, allocation: 500f),
            0);
        Expect(
            "an over-represented type the allocation cannot reach does not change the answer",
            SelectInTier(crowded, AirframeTier.Fighter, threatHigh: false, allocation: 70f),
            0);

        // The last-resort rule outranks every element preference, as it always did.
        AirframeCandidate[] withCricket =
        {
            new(0, AirframeTier.LastResort, price: 12f, rating: 0.3f, lastResort: true, antiSurface: 0.3f),
            new(1, AirframeTier.LastResort, price: 22f, rating: 0.2f, lastResort: false, antiSurface: 0.2f),
        };
        Expect(
            "a strike element still leaves the last-resort airframe on the ground",
            SelectInTier(withCricket, AirframeTier.LastResort, threatHigh: true, allocation: 500f, ElementKind.Strike),
            1);
        Expect(
            "a home patrol still leaves it on the ground too",
            SelectInTier(withCricket, AirframeTier.LastResort, threatHigh: true, allocation: 500f, ElementKind.HighCap),
            1);

        float cap = CommanderSettings.TypeShareCap;
        Expect("the diversity cap allows a majority; check the Operations section of the config", cap > 0.5f, true);
        Expect("the diversity cap is below the whole sky; check the Operations section of the config", cap < 1f, true);
    }

    /// <summary>Builds a throwaway definition with the two ratings the tier table reads and asks
    /// which tier it lands in — the self-check's own probe, so the boundary cases are written as
    /// numbers rather than as aircraft names.</summary>
    private static AirframeTier TierOf(float antiAir, float antiSurface, AirRole role)
    {
        AircraftDefinition definition = ScriptableObject.CreateInstance<AircraftDefinition>();
        definition.jsonKey = "TierProbe";
        definition.roleIdentity.antiAir = antiAir;
        definition.roleIdentity.antiSurface = antiSurface;
        AirframeTier tier = ForRole(definition, role);
        Object.Destroy(definition);
        return tier;
    }

    private static void Expect(string name, string actual, string expected)
    {
        if (!string.Equals(actual, expected, System.StringComparison.Ordinal))
        {
            CommanderPlugin.Log.LogError($"Enemy air buy self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    private static void Expect(string name, AirframeTier? actual, AirframeTier? expected)
    {
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError($"Enemy air buy self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    private static void Expect(string name, AirRole? actual, AirRole? expected)
    {
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError($"Enemy air buy self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }

    private static void Expect(string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            CommanderPlugin.Log.LogError($"Enemy air buy self-check FAILED ({name}): expected {expected}, got {actual}.");
        }
    }
}
