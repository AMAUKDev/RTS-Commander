using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// Which recipe slot a vehicle definition fills in a platoon. One definition shared by the recipe
/// fill (<see cref="CommanderOperationsService.FillRecipe"/>), the order book
/// (<c>Operations/CommanderOperationsRequisitions.cs</c>) and the buyer's role purchase
/// (<c>Ai/CommanderEnemyCommanderService.ChooseForRole</c>), so retuning what counts as armour
/// cannot drift between what a platoon wants and what the buyer fetches for it.
/// </summary>
internal enum CommanderPlatoonRole
{
    /// <summary>MBT or AFV. The platoon's weight — three slots in the default recipe.</summary>
    Armour,

    /// <summary>APC/LCV, preferring a definition with <c>captureStrength &gt; 0</c> (the same test
    /// <c>ChooseCaptureUnit</c> uses) — the one vehicle in the platoon that can actually take a
    /// point. One slot in the default recipe.</summary>
    Carrier,

    /// <summary>AAA, IR_SAM or R_SAM. A platoon in the open with no umbrella is a target — two
    /// slots in the default recipe.</summary>
    AirDefence,

    /// <summary>A munitions truck, identified by what its prefab carries rather than its vehicle
    /// type (departure 3, <see cref="CommanderGameAccess.IsMunitionsTruckDefinition"/>). Never
    /// fills a combat slot; requisitioned separately per forward base.</summary>
    Truck,

    /// <summary>Anything else the mod claims — <c>VehicleType.ART</c> and any combat vehicle that
    /// is not one of the three recipe roles. Fills an unfilled slot like any other combat vehicle
    /// (design SS1: "Unfillable slot -&gt; any combat vehicle"), but is never itself a recipe target.</summary>
    Other,
}

/// <summary>The one mapping from a vehicle definition to its platoon role.</summary>
internal static class CommanderPlatoonRoles
{
    /// <summary>Classifies <paramref name="definition"/> — the one role mapping the recipe, the
    /// order book and the buyer all read.</summary>
    internal static CommanderPlatoonRole Of(VehicleDefinition? definition)
    {
        if (definition == null)
        {
            return CommanderPlatoonRole.Other;
        }

        if (CommanderGameAccess.IsMunitionsTruckDefinition(definition))
        {
            return CommanderPlatoonRole.Truck;
        }

        // No faction the mod has met fields a VehicleType.LCV at all (Boscali and Primeva rosters,
        // 2026-09-13), so "carrier" on LCV alone left every carrier slot to the armour fallback and
        // every platoon reading A4/C0/D2. A troop-carrying AFV — one with capture strength, the APC
        // and IFV — is the light vehicle the recipe means; the tanks are the armour.
        if (definition.vehicleType == VehicleType.AFV && definition.captureStrength > 0f)
        {
            return CommanderPlatoonRole.Carrier;
        }

        // A light unmanned vehicle whose name says it is a missile launcher is air defence too
        // (2026-09-14: the Hexhound SAM the transports carry is a UGV by type, so it read as Other
        // and the air-defence-first cargo rule never saw it; every inserted picket flew without
        // its umbrella). The type table stays the first word; the name is only asked for UGVs.
        if (definition.vehicleType == VehicleType.UGV && IsAirDefenceByName(definition.unitName))
        {
            return CommanderPlatoonRole.AirDefence;
        }

        return definition.vehicleType switch
        {
            VehicleType.MBT or VehicleType.AFV => CommanderPlatoonRole.Armour,
            VehicleType.LCV => CommanderPlatoonRole.Carrier,
            VehicleType.AAA or VehicleType.IR_SAM or VehicleType.R_SAM => CommanderPlatoonRole.AirDefence,
            _ => CommanderPlatoonRole.Other,
        };
    }

    /// <summary>Whether a unit name marks an air-defence launcher, pure: a whole word "SAM" or
    /// "AA" (so "Hexhound SAM" and "LCV25 AA" qualify, "Samson" and "Jackknife" do not).</summary>
    internal static bool IsAirDefenceByName(string? unitName)
    {
        if (string.IsNullOrEmpty(unitName))
        {
            return false;
        }

        string[] words = unitName!.Split(' ', '-', '_');
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i] == "SAM" || words[i] == "AA")
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// How a platoon is standing on the ground right now, inside whatever
/// <see cref="CommanderPlatoonState"/> it is in (design.md, ground-tactics_20260914). The state
/// says what the platoon is FOR; the posture says how it is deployed while doing it, and it is the
/// posture the marker text and the movement tick's branches read.
/// </summary>
internal enum CommanderGroundPosture
{
    /// <summary>Spread over its point's ring on role-assigned posts, or marching in formation —
    /// the ordinary posture, and what every platoon falls back to.</summary>
    Ring,

    /// <summary>Armour and carriers forward on an arc facing the threat while the point it holds is
    /// in contact (design §2).</summary>
    DefenceArc,

    /// <summary>Crossing the ground to its objective in cross-country bounds rather than following
    /// a road (design §3).</summary>
    Bounding,

    /// <summary>A reinforcement swinging wide of a point that is already held, to go in on the
    /// attacker's flank (design §4).</summary>
    CounterAttack,

    /// <summary>A reinforcement sitting out on the most threatened approach to a point that is
    /// already held, with nothing tracked to attack (design §4).</summary>
    Screen,
}

/// <summary>What a platoon is doing right now (design SS1).</summary>
internal enum CommanderPlatoonState
{
    /// <summary>Below establishment and waiting on its requisitions; may still hold a mission.</summary>
    Forming,

    /// <summary>Under way to its objective in formation.</summary>
    Moving,

    /// <summary>Arrived: members are on their hold posts (a forward base or a picket-scale
    /// detachment does not use this state — see <see cref="CommanderMissionKind.Picket"/>).</summary>
    Holding,

    /// <summary>Part of a live offensive, en route to or engaging its target.</summary>
    Attacking,

    /// <summary>Below half establishment (<see cref="CommanderOperationsService.ShouldWithdraw"/>),
    /// falling back on the nearest friendly forward base or held base.</summary>
    Withdrawing,
}

/// <summary>What kind of mission a platoon (or a picket detachment) is carrying out (design SS2/SS3).</summary>
internal enum CommanderMissionKind
{
    /// <summary>A platoon holding a front control point as a forward operating base, with a
    /// munitions truck parked in the ring.</summary>
    ForwardBase,

    /// <summary>A two-vehicle detachment holding a rear control point so it keeps paying. Not a
    /// platoon: <see cref="CommanderOperationsMission.WantedPlatoons"/> is always zero for one.</summary>
    Picket,

    /// <summary>A two- or three-axis push at an enemy point or base.</summary>
    Attack,

    /// <summary>The base's own reserve platoon(s) — what the home guard used to be (design SS1:
    /// "the home guard becomes the base's reserve platoon(s)").</summary>
    Reserve,
}

/// <summary>
/// A named group of up to <see cref="CommanderSettings.OperationsPlatoonSize"/> vehicles with one
/// objective and one state. The mod's unit of command for every AI-owned ground vehicle once the
/// operations service manages an HQ (design SS1).
/// </summary>
internal sealed class CommanderPlatoon
{
    /// <summary>Display and log name, e.g. <c>"1ST PLATOON"</c>.</summary>
    internal string Name = string.Empty;

    /// <summary>Every vehicle currently in the platoon.</summary>
    internal readonly List<Unit> Members = new();

    /// <summary>The unit routed and drawn as the platoon's position; <c>Members[0]</c> while it is
    /// alive, re-picked as the first live member once it is not (movement tick, T5).</summary>
    internal Unit? Leader = null;

    internal CommanderPlatoonState State = CommanderPlatoonState.Forming;

    /// <summary>Scaled <c>Time.time</c> until which the platoon is "in contact" and deployed in a
    /// firing line instead of marching (<c>CommanderOperationsService.ContactDrill</c>). Negative
    /// when it has never been in contact.</summary>
    internal float InContactUntil = -1f;

    /// <summary>Where the contact last was, so the line keeps facing it after tracking lapses.</summary>
    internal GlobalPosition ContactBearingAnchor;

    /// <summary>Scaled <c>Time.time</c> until which this platoon still calls for pre-emptive air
    /// support — refreshed every review it is under way inside the pre-emptive range of the enemy,
    /// and the hysteresis that keeps its sortie alive for
    /// <c>CommanderOperationsService.PreemptiveAirHoldSeconds</c> after it leaves that range (user
    /// decision 2026-09-14). Negative when it has never been near the enemy, and reset the moment
    /// it stops marching. The air mirror of <see cref="InContactUntil"/>.</summary>
    internal float PreemptiveAirUntil = -1f;

    /// <summary>
    /// What this platoon's pre-emptive sortie WOULD have asked for, while
    /// <c>CommanderSettings.OperationsMaxPreemptiveAirObjectives</c> is holding it back (user
    /// request, 2026-09-14). Both zero when it is not queued — it was served, or it never asked.
    /// The marker reads them so a platoon waiting its turn says <c>Requesting CAS (0/2 · queued)</c>
    /// rather than going silent, which is how the cap stays legible to the player instead of
    /// looking like the wing ignoring them.
    /// </summary>
    internal int QueuedCasWanted;
    internal int QueuedCapWanted;

    /// <summary>
    /// Scaled <c>Time.time</c> of the most recent member this platoon lost to death, capture by
    /// another faction or removal from the game — stamped by the review sweep, negative when it
    /// has never lost one (addendum 2026-09-14 §2). A loss this fresh is contact evidence for a
    /// platoon standing on its posts even when nothing is tracked, read through
    /// <c>CommanderOperationsService.LossIsRecent</c>.
    /// </summary>
    internal float LastLossAt = -1f;

    /// <summary>
    /// The label of the mission whose open reinforcement request this platoon is answering
    /// (addendum 2026-09-14 §3), empty otherwise. Set when it is attached to a mission with an
    /// open request, cleared when it leaves that mission or the request closes; the marker's
    /// <c>Reinforcing &lt;label&gt;</c> flag and the <c>reinforces</c> log line both read it, so
    /// a reinforcement is reported once when it is sent and not again every review.
    /// </summary>
    internal string ReinforcesLabel = string.Empty;

    /// <summary>
    /// How this platoon is deployed on the ground (ground-tactics design §5). Written once per
    /// movement tick by the posture branches, read by the marker text and the review diagnostics.
    /// </summary>
    internal CommanderGroundPosture Posture = CommanderGroundPosture.Ring;

    /// <summary>The bearing the current posture is aimed on: the defence arc's threat bearing, or
    /// the bearing a screen faces out along. Meaningless in <see cref="CommanderGroundPosture.Ring"/>.</summary>
    internal float PostureBearing = 0f;

    /// <summary>Where the current cross-country bound ends (design §3), or null between bounds.</summary>
    internal GlobalPosition? BoundTarget = null;

    /// <summary>The objective the current bound was cut toward. When the review moves the
    /// objective the bound is re-cut from where the platoon now stands rather than carrying it on
    /// toward somewhere it is no longer going.</summary>
    internal GlobalPosition BoundFor = default;

    /// <summary>Scaled <c>Time.time</c> the current bound was issued — what the 90 s bound timeout
    /// is measured from.</summary>
    internal float BoundIssuedAt = -1f;

    /// <summary>True once the "leaves the road … bounding" line has been written for this advance,
    /// so an attack says it once and not every bound (design §3).</summary>
    internal bool BoundLogged = false;

    /// <summary>Where a counter-attacking reinforcement swings out to before it goes in (design
    /// §4), or null while it is not counter-attacking.</summary>
    internal GlobalPosition? FlankPosition = null;

    /// <summary>True once the counter-attack has reached its flank position and is going in on the
    /// enemy rather than still swinging out to the side.</summary>
    internal bool FlankReached = false;

    /// <summary>
    /// Drops every posture the ground tactics keep between ticks, back to a platoon standing in its
    /// ring. Called wherever a platoon's job changes under it — a new mission, a new bound line, a
    /// contact that has lapsed — so no branch inherits another branch's half-finished manoeuvre.
    /// </summary>
    internal void ClearGroundPosture()
    {
        Posture = CommanderGroundPosture.Ring;
        BoundTarget = null;
        BoundFor = default;
        BoundIssuedAt = -1f;
        BoundLogged = false;
        FlankPosition = null;
        FlankReached = false;
    }

    /// <summary>Where the line was formed. Fixed on first contact so the slots do not creep with the
    /// leader every movement tick; reset when the platoon marches again.</summary>
    private GlobalPosition? lineAnchor;

    /// <summary>The deploy point for this contact: the leader's position at first contact.</summary>
    internal GlobalPosition LineAnchor(GlobalPosition leaderNow)
    {
        if (InContactUntil < Time.time)
        {
            lineAnchor = null;
        }

        lineAnchor ??= leaderNow;
        return lineAnchor.Value;
    }

    /// <summary>The mission this platoon currently serves, or null between missions (just
    /// dissolved, or waiting to be matched by <c>AssignPlatoons</c>).</summary>
    internal CommanderOperationsMission? Mission = null;

    /// <summary>Where the platoon is currently routed.</summary>
    internal GlobalPosition Objective = default;

    /// <summary>What a full platoon is, fixed at formation time. Strength fractions
    /// (<see cref="CommanderOperationsService.ShouldWithdraw"/>) are read against this rather than
    /// the live <c>PlatoonSize</c> setting, so a mid-match recipe retune does not silently change
    /// what "half strength" means for a platoon already in the field.</summary>
    internal int Establishment = 0;

    /// <summary>
    /// Armour slots the recipe this platoon was formed from wanted, fixed at formation time beside
    /// <see cref="Establishment"/> and for the same reason. The teeth rule
    /// (<see cref="CommanderOperationsService.ShouldWithdraw(int, int, int, int)"/>) reads it rather
    /// than the live setting, so an air-mobile platoon — six light vehicles the transports can carry,
    /// no tanks by design — is not read as a platoon that has lost all its armour and sent home the
    /// moment it lands.
    /// </summary>
    internal int ArmourWanted = 0;

    /// <summary>Game time this platoon last began forming up (creation, or re-entry from
    /// Withdrawing). Read by <see cref="CommanderOperationsService.IsReadyToMoveOut"/>: a platoon
    /// moves out when full, or when it has waited long enough that waiting is costing more than
    /// the missing vehicles would add.</summary>
    internal float FormingSince = 0f;

    /// <summary>This platoon's re-raise bookkeeping — see <see cref="CommanderReseatRecord"/>. One
    /// record type, two holders: a platoon and a picket detachment are both groups of ground
    /// vehicles that can be taken off the road and handed back to a nearer depot.</summary>
    internal readonly CommanderReseatRecord Reseat = new();

    /// <summary>Per-member last-issued destination, owned by this platoon and handed to
    /// <see cref="CommanderMoveService.IssuePlatoonMove"/> so a member that has arrived at its
    /// formation slot is not re-issued every movement tick.</summary>
    internal readonly Dictionary<Unit, GlobalPosition> Issued = new();
}

/// <summary>
/// What one group of ground vehicles' last re-raise from a nearer depot did, and when (user
/// instruction 2026-09-16: a platoon or a ground-driven picket that a forward base has overtaken is
/// despawned and raised again from that base). One definition, two holders —
/// <see cref="CommanderPlatoon.Reseat"/> and <see cref="CommanderOperationsMission.Reseat"/> — because
/// the rule, the churn guard and the "these vehicles are already paid for" accounting are identical
/// for a marching platoon and a driving picket detachment; only what is being moved differs.
/// </summary>
internal sealed class CommanderReseatRecord
{
    /// <summary>Scaled <c>Time.time</c> of the last re-raise, negative when there has never been
    /// one. The churn guard reads it: a group is re-raised at most once every
    /// <c>CommanderOperationsService.ReseatCooldownSeconds</c>, so a second forward base coming
    /// online cannot dissolve the same group again while the first re-raise is still landing.</summary>
    internal float At = -1f;

    /// <summary>How many vehicles that re-raise handed back to the new depot; read against how many
    /// have since rejoined, through <c>CommanderOperationsService.ReseatVehiclesInTransit</c>, so the
    /// order books and the fills count a vehicle on its way back as one the commander already owns
    /// rather than one it has to buy again.</summary>
    internal int Banked = 0;

    /// <summary>
    /// Minutes of driving from the depot the last re-raise moved this group TO, to the objective it
    /// was moving toward — the strict-progress guard (user decision 2026-09-16). Every re-raise must
    /// come in strictly under this, so each one shortens the remaining journey and a group can never
    /// be sent back and forth between two depots that are each nearer than the other depending on
    /// where it happens to stand. A long cooldown slows such a loop; this makes it impossible.
    /// <para><see cref="float.MaxValue"/> means "no previous re-raise on this journey", which any
    /// depot beats. That is the value a fresh record carries and the value
    /// <see cref="ResetForNewObjective"/> restores.</para>
    /// </summary>
    internal float LastDepotMinutes = float.MaxValue;

    /// <summary>
    /// Forgets the strict-progress memory because this group is now going somewhere else, so the
    /// distance its last re-raise achieved no longer describes its journey. Called wherever a group
    /// is given a new objective — which is <c>CommanderOperationsService.AttachPlatoon</c>, the one
    /// choke point a platoon passes through when it takes a mission. A picket never needs it: its
    /// objective is its own control point and never moves.
    /// <para>Without this, a platoon that finished one journey and was then sent to a nearer base
    /// would be refused a perfectly good re-raise because of a number describing a journey it is no
    /// longer on.</para>
    /// </summary>
    internal void ResetForNewObjective()
    {
        LastDepotMinutes = float.MaxValue;
    }
}

/// <summary>
/// A live mission: a forward base, a picket, an offensive or the base reserve. Owns the platoons
/// assigned to it and, for an offensive, the axes those platoons are marching on.
/// </summary>
internal sealed class CommanderOperationsMission
{
    /// <summary>This mission's picket detachment's re-raise bookkeeping — see
    /// <see cref="CommanderReseatRecord"/>. Only ever used by a
    /// <see cref="CommanderMissionKind.Picket"/>, the one mission kind that drives vehicles of its
    /// own rather than through a platoon.</summary>
    internal readonly CommanderReseatRecord Reseat = new();

    internal CommanderMissionKind Kind = CommanderMissionKind.Reserve;

    /// <summary>The control point this mission holds or attacks, or null for a base attack.</summary>
    internal CommanderStrategicPoint? Point = null;

    /// <summary>The base this mission attacks, or null for anything but an <see cref="CommanderMissionKind.Attack"/>
    /// on an airbase.</summary>
    internal Airbase? TargetAirbase = null;

    /// <summary>How many platoons this mission wants. Always 0 for a <see cref="CommanderMissionKind.Picket"/>
    /// — a picket is a pool detachment, never a platoon, so it never competes for the forward-base share.</summary>
    internal int WantedPlatoons = 0;

    /// <summary>Platoons currently serving this mission.</summary>
    internal readonly List<CommanderPlatoon> Assigned = new();

    /// <summary>
    /// A picket's detachment (design SS2: "a picket is a detachment from the pool, not a platoon,
    /// so it never competes for the forward-base share"). Only ever populated for
    /// <see cref="CommanderMissionKind.Picket"/> — an addition beyond the plan's original field
    /// list (T8), since a picket has nowhere else to keep the vehicles holding its ring.
    /// </summary>
    internal readonly List<Unit> PicketMembers = new();

    /// <summary>The munitions truck parked at a <see cref="CommanderMissionKind.ForwardBase"/>'s
    /// ring, or null while none has been requisitioned yet (design SS2). An addition beyond the
    /// plan's original field list (T17), needed to know whether this mission already has one.</summary>
    internal Unit? Truck = null;

    /// <summary>
    /// The strike package flying ahead of this attack, or null while it has none. Only ever
    /// populated for <see cref="CommanderMissionKind.Attack"/>.
    /// <para>
    /// Added by concurrent-attacks_20260918. Before it, a commander had ONE package, in a single
    /// field on the state (<c>OperationsState.StrikeSortie</c>) — so allowing several attacks would
    /// have given the first one air and sent the rest in naked, which is the opposite of what that
    /// track is for. The package lives on the mission because it should die with the attack, and a
    /// field here gives that for free.
    /// </para>
    /// <para>
    /// <c>OperationsState.StrikeSortie</c> still exists and still holds the DELIBERATE strike — the
    /// one the strike clock opens when no attack is running. That slot keeps its one-at-a-time rule
    /// and its own clock; this track did not change it, and an attack's package closing must never
    /// reset it.
    /// </para>
    /// </summary>
    internal CommanderOperationsService.CommanderAirSortie? StrikeSortie = null;

    /// <summary>True once the "no munitions truck available" line has been logged for this
    /// mission, so it logs once and not every review (design SS2).</summary>
    internal bool NoTruckLogged = false;

    /// <summary>Assault groups for an <see cref="CommanderMissionKind.Attack"/> mission — one per
    /// platoon committed to the attack, each pointing at the axis it was assigned.</summary>
    internal readonly List<CommanderAssaultGroup> Axes = new();

    /// <summary>
    /// Scaled <c>Time.time</c> the first assault group arrived at its release point, or -1 before
    /// then. <see cref="CommanderOperationsService.AssaultFormUpTimeoutSeconds"/> is measured from
    /// here (design SS3: "timeout 4 min, then go"). An addition beyond the plan's original field
    /// list (T14), since the timeout has to be measured from somewhere.
    /// </summary>
    internal float FirstGroupArrivedAt = -1f;

    /// <summary>True once every group has arrived (or the form-up timeout fired) and the assault is
    /// going in at the target's hold point instead of waiting at its release point. Attack missions
    /// only.</summary>
    internal bool Launched = false;

    /// <summary>
    /// True once the "waiting for CAS overhead" line has been written for this attack, so the go-in
    /// hold logs once and not every review it is in force (air-support design, Approval 1). An
    /// addition beyond the plan's original field list, like <see cref="FirstGroupArrivedAt"/>: the
    /// hold has to say itself somewhere.
    /// </summary>
    internal bool CasHoldLogged = false;

    /// <summary>
    /// <see cref="CasHoldLogged"/>'s twin for the strike ahead of the attack (design.md,
    /// strike-packages_20260915 Section 5): the "holding until the strike has gone in" line is
    /// written once per attack, not once per review it is in force.
    /// </summary>
    internal bool StrikeHoldLogged = false;

    /// <summary>
    /// Scaled <c>Time.time</c> until which this <see cref="CommanderMissionKind.Picket"/> or
    /// <see cref="CommanderMissionKind.ForwardBase"/> mission is "in contact" — a tracked hostile
    /// ground unit inside <c>CommanderOperationsService.ContactRangeMeters</c> of its point, or a
    /// detachment member lost inside <c>CommanderOperationsService.LossContactSeconds</c>, holds
    /// the same clock a platoon's <see cref="CommanderPlatoon.InContactUntil"/> carries (addendum
    /// 2026-09-14 §2). Pickets are not platoons, so the point itself — not any platoon — is what
    /// raises the contact-priority air demand when it is under attack with no platoon on it.
    /// Negative when the point has never been in contact.
    /// </summary>
    internal float ContactUntil = -1f;

    /// <summary>
    /// Scaled <c>Time.time</c> of the most recent picket member this mission lost
    /// (<see cref="CommanderOperationsMission.PicketMembers"/>), stamped by the picket sweep the
    /// same way <see cref="CommanderPlatoon.LastLossAt"/> is for a platoon (addendum 2026-09-14
    /// §2). Negative when it has never lost one.
    /// </summary>
    internal float LastLossAt = -1f;

    /// <summary>
    /// Scaled <c>Time.time</c> from which nobody has contested this point — the quiet-ground
    /// retirement's clock (design.md, <c>unit-economy_20260918</c> §2.4). Refreshed to now wherever
    /// contact is already detected (<c>DetectMissionContact</c>, <c>DetectHoldingContact</c>), so it
    /// is the EXISTING notion of contact that drives it and not a second one, and again every time
    /// the retirement thins the point, so a garrison is thinned once per timeout rather than once
    /// per review.
    /// <para>
    /// Negative until the retirement first sees the point quiet, which starts the clock. "Never been
    /// in contact" must not read as "quiet for ever": that would cash a picket in on the review it
    /// was formed, and at half price each time the commander would buy and sell the same vehicles
    /// for the whole match.
    /// </para>
    /// </summary>
    internal float QuietSince = -1f;

    /// <summary>
    /// Scaled <c>Time.time</c> the most recent air-inserted vehicle was set down on this picket, or
    /// negative when none ever was. The picket marker's <c>Dropped, taking posts</c> window reads
    /// it (picket/truck markers, 2026-09-14), and it lives on the mission rather than on the
    /// insertion record because that record is swept as soon as the transport is recovered — which
    /// is well inside the window the marker still wants to report.
    /// </summary>
    internal float LastDropAt = -1f;

    /// <summary>
    /// How many of <see cref="WantedPlatoons"/> are the open reinforcement request (addendum
    /// 2026-09-14 §3): zero with no request, otherwise the capped count a platoon outnumbered on
    /// paper asked for. <see cref="WantedPlatoons"/> is always
    /// <c>baseline + ReinforcePlatoons</c> — the request adjusts both together, so the existing
    /// assignment, requisition and share passes need no changes to serve a request.
    /// </summary>
    internal int ReinforcePlatoons = 0;

    /// <summary>
    /// Scaled <c>Time.time</c> from which every assigned platoon's observed hostiles have stayed
    /// below its own strength — the release timer of an open request (addendum 2026-09-14 §3),
    /// which closes it after <c>CommanderOperationsService.ReinforceReleaseSeconds</c>. Negative
    /// while any platoon is still outnumbered on paper, or with no request open.
    /// </summary>
    internal float ReinforceBelowSince = -1f;

    /// <summary>What the COMMANDER LOG calls this mission, e.g. a point's label or an airbase's.</summary>
    internal string Label = string.Empty;

    // ---- Air-mobile platoons (design.md, air-mobile-platoons_20260915) ----

    /// <summary>
    /// True when this mission's platoon is flown in rather than driven: the objective was more than
    /// <c>CommanderSettings.AirMobileDriveMinutes</c> from the nearest usable depot when the mission
    /// would have raised a new platoon, and a vehicle-carrying transport could reach it. An
    /// air-mobile mission is skipped by the purpose count and the platoon matching — nothing is
    /// raised at a depot for it and no existing platoon is sent driving to it — and its carrier and
    /// air-defence lines are bought as cargo by the lift rather than at a depot.
    /// </summary>
    internal bool AirMobile = false;

    /// <summary>The open lift bringing this mission's platoon in, or null when none is flying.</summary>
    internal CommanderOperationsService.CommanderFobOrder? LiftOrder = null;

    /// <summary>
    /// The name the platoon this lift is raising will carry, reserved when the mission is marked
    /// air-mobile so the log line that announces the raise, the lift's own lines and the review
    /// line's <c>lift=</c> field all name the same platoon — which does not exist until its first
    /// load is on the ground. Empty on every other mission.
    /// </summary>
    internal string AirMobilePlatoonName = string.Empty;

    /// <summary>
    /// Minutes of driving from the nearest usable depot to this mission's point, refreshed once per
    /// review by the forward-base planner. The drive-time gate reads it to decide whether a new
    /// platoon flies, and the order book reads it to decide whether the armour and truck lines — the
    /// ones no transport can carry — may be posted yet. <c>float.MaxValue</c> with no depot at all.
    /// </summary>
    internal float DriveMinutesToPoint = float.MaxValue;
}

/// <summary>
/// One platoon's route on an offensive: the axis it forms up from, the release point it stops at
/// and waits, and whether it has arrived there yet (design SS3). Two or three assault groups can
/// share the same form-up/release pair when an attack fields more platoons than axes.
/// </summary>
internal sealed class CommanderAssaultGroup
{
    /// <summary>Where this axis forms up: a held forward base or base within
    /// <c>AxisCandidateRadiusMeters</c> of the target, or a flank point off the direct line when only
    /// one candidate exists (design SS3).</summary>
    internal GlobalPosition FormUpPoint = default;

    /// <summary>Where this group stops and waits, <c>ReleaseDistanceMeters</c> short of the target on
    /// its own side (design SS3), computed by <c>ReleasePointFor</c>.</summary>
    internal GlobalPosition ReleasePoint = default;

    /// <summary>The platoon marching this axis, or null while the mission has not yet been assigned
    /// enough platoons to fill every axis.</summary>
    internal CommanderPlatoon? Platoon = null;

    /// <summary>True once this group's platoon has closed up on its release point (or, after the
    /// mission has launched, is treated as no longer meaningful — <c>UpdateAttacks</c> only reads
    /// this before <c>Launched</c>).</summary>
    internal bool Arrived = false;
}
