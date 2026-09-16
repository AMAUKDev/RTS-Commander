using System.Collections.Generic;
using UnityEngine;

namespace GroundControlRts;

/// <summary>
/// The insertion shield (design, insertion-shield_20260915; user, 2026-09-15: air-delivered pickets
/// "have a habit of being dropped from a too-high height and dying on contact with the ground, or
/// parachuting in but landing upside down"). Every vehicle a cargo run puts out of an aircraft is
/// shielded from all damage from the moment it activates until it has stood on the ground, upright
/// and still for <see cref="InsertionSettleSeconds"/>, or until <see cref="InsertionShieldMaxSeconds"/>
/// has passed whatever its state. A vehicle that comes to rest on its side or roof is set upright
/// where it lies. The damage gate is one Harmony prefix on the game's part damage entry
/// (<c>UnitPart.TakeDamage</c>, every damage kind passes through it); the sweep runs every tick over
/// the handful of vehicles in the set. Part of the supply helicopter service because the cargo
/// activation hook already lives here.
/// </summary>
internal sealed partial class CommanderSupplyHeliService
{
    /// <summary>Seconds a delivered vehicle must stand on the ground, upright and still before its
    /// shield lifts: 3. A parachute landing bounces for a second or two; three seconds of stillness is
    /// a vehicle that has finished arriving, and short enough that the picket is fighting within the
    /// same review it landed in.</summary>
    internal const float InsertionSettleSeconds = 3f;

    /// <summary>Longest a shield stays on whatever the vehicle is doing: 90 s. A parachute drop from
    /// 200 m takes well under a minute; a vehicle still not settled after a minute and a half is
    /// wedged or driving, and an immortal vehicle in the fight is a cheat.</summary>
    internal const float InsertionShieldMaxSeconds = 90f;

    /// <summary>How close to vertical a vehicle's up vector must be to count as upright: a dot product
    /// of 0.7 with straight up, about 45 degrees. Steeper than any slope a vehicle parks on; a vehicle
    /// on its side reads near 0 and on its roof near -1.</summary>
    internal const float UprightDotThreshold = 0.7f;

    /// <summary>Speed below which a vehicle counts as still: 0.5 m/s, the twitch of a body settling
    /// on its suspension rather than a vehicle moving.</summary>
    internal const float StillSpeedMetersPerSecond = 0.5f;

    /// <summary>How far above the terrain a vehicle may sit and still count as on the ground: 2.5 m,
    /// a tall hull on its wheels plus a little suspension; a vehicle under a parachute is tens of
    /// metres up.</summary>
    internal const float GroundContactMeters = 2.5f;

    /// <summary>How far above the snapped terrain a righted vehicle is placed: 1.5 m, so a hull set
    /// upright drops onto its wheels rather than being spawned into the slope.</summary>
    internal const float RightingLiftMeters = 1.5f;

    private sealed class ShieldRecord
    {
        internal float Since;

        /// <summary>Ground this vehicle is to be set down on, or null when it is left where it fell
        /// (delivery-bypass_20260916). Set by the unload-in-place path and cleared by the first sweep
        /// that acts on it, so a vehicle is placed once and then behaves like any other delivery.
        /// The placement rides on the sweep rather than on the activation hook because the sweep
        /// already moves delivered vehicles every tick and is proven; moving one inside the frame the
        /// engine is still activating it in would be new risk in the delivery path with the worst
        /// incident history in this repository.</summary>
        internal GlobalPosition? PlaceAt;
        internal float StillSince = -1f;
        internal string Label = string.Empty;
        internal string Where = string.Empty;
        internal FactionHQ? Hq;
    }

    private readonly Dictionary<Unit, ShieldRecord> shielded = new();
    private readonly List<Unit> shieldPrune = new();

    /// <summary>Whether a damaged part belongs to a shielded vehicle; the Harmony prefix's answer.</summary>
    internal static bool ShieldsDamage(UnitPart? part)
    {
        return Instance != null
            && part != null
            && part.parentUnit != null
            && Instance.shielded.ContainsKey(part.parentUnit);
    }

    /// <summary>Puts a freshly activated cargo vehicle under the shield. <paramref name="placeAt"/>
    /// is the ground the next sweep is to set it down on, or null to leave it where it fell — only
    /// the delivery bypass passes one.</summary>
    private void ShieldDeliveredCargo(Unit cargoUnit, CargoMission mission, GlobalPosition? placeAt = null)
    {
        if (cargoUnit == null || cargoUnit.disabled || shielded.ContainsKey(cargoUnit))
        {
            return;
        }

        shielded[cargoUnit] = new ShieldRecord
        {
            Since = Time.time,
            Label = CommanderGameAccess.GetUnitLabel(cargoUnit),
            Where = mission.InsertionPoint != null ? mission.InsertionPoint.Label : mission.CargoLabel,
            Hq = mission.Hq,
            PlaceAt = placeAt,
        };
    }

    /// <summary>Whether a shielded vehicle has finished arriving, pure: on the ground, upright, and
    /// still for at least <paramref name="settleSeconds"/>.</summary>
    internal static bool InsertionSettled(bool onGround, bool upright, float stillSeconds, float settleSeconds)
    {
        return onGround && upright && stillSeconds >= settleSeconds;
    }

    /// <summary>Whether a vehicle at rest needs setting upright, pure: on the ground and still, but
    /// not upright. A vehicle still moving is left to finish falling first.</summary>
    internal static bool NeedsRighting(bool onGround, bool upright, bool still)
    {
        return onGround && still && !upright;
    }

    /// <summary>The shield's hard stop, pure: at or past the maximum.</summary>
    internal static bool ShieldExpired(float secondsShielded, float maxSeconds)
    {
        return secondsShielded >= maxSeconds;
    }

    /// <summary>Upright when the up vector's vertical component is at least the threshold, pure.</summary>
    internal static bool IsUpright(float upDotY, float threshold)
    {
        return upDotY >= threshold;
    }

    /// <summary>Every tick: lift shields that have settled or expired, right what lies on its side.</summary>
    private void SweepShields()
    {
        if (shielded.Count == 0)
        {
            return;
        }

        shieldPrune.Clear();
        foreach (KeyValuePair<Unit, ShieldRecord> entry in shielded)
        {
            Unit unit = entry.Key;
            ShieldRecord record = entry.Value;
            if (unit == null || unit.disabled)
            {
                shieldPrune.Add(unit!);
                continue;
            }

            float shieldedFor = Time.time - record.Since;
            if (record.PlaceAt.HasValue)
            {
                // The delivery bypass asked for this vehicle to be set down on chosen ground. Done
                // once, before anything is measured, because every measurement below is about where
                // the vehicle has ended up and it has not ended up anywhere yet.
                GlobalPosition placeAt = record.PlaceAt.Value;
                record.PlaceAt = null;
                record.StillSince = -1f;
                PlaceVehicleUpright(unit, placeAt);
                Note(record, $"{record.Label} set down at {record.Where}: unloaded in place, not landed.");
                continue;
            }

            GlobalPosition ground = CommanderGameAccess.SnapToTerrain(unit.transform.GlobalPosition());
            bool onGround = unit.transform.GlobalPosition().y - ground.y <= GroundContactMeters;
            bool upright = IsUpright(Vector3.Dot(unit.transform.up, Vector3.up), UprightDotThreshold);
            float speed = unit.rb != null ? unit.rb.velocity.magnitude : 0f;
            bool still = speed < StillSpeedMetersPerSecond;
            if (still)
            {
                if (record.StillSince < 0f)
                {
                    record.StillSince = Time.time;
                }
            }
            else
            {
                record.StillSince = -1f;
            }

            if (NeedsRighting(onGround, upright, still))
            {
                RightVehicle(unit, ground, record);
                record.StillSince = -1f;
                continue;
            }

            float stillSeconds = record.StillSince < 0f ? 0f : Time.time - record.StillSince;
            if (InsertionSettled(onGround, upright, stillSeconds, InsertionSettleSeconds))
            {
                Note(record, $"{record.Label} settled at {record.Where} after {shieldedFor:0} s; shield off.");
                shieldPrune.Add(unit);
            }
            else if (ShieldExpired(shieldedFor, InsertionShieldMaxSeconds))
            {
                Note(record, $"{record.Label} at {record.Where}: shield timed out after {shieldedFor:0} s "
                    + $"(on ground {onGround}, upright {upright}, speed {speed:0.0} m/s).");
                shieldPrune.Add(unit);
            }
        }

        for (int i = 0; i < shieldPrune.Count; i++)
        {
            shielded.Remove(shieldPrune[i]);
        }

        shieldPrune.Clear();
    }

    /// <summary>Stands a vehicle upright on <paramref name="ground"/>: heading kept, pitch and roll
    /// zeroed, lifted a little above the terrain, velocities cleared so it drops onto its suspension.
    /// Server only; the game syncs the transform. Lifted out of <see cref="RightVehicle"/> on
    /// 2026-09-16 (Reuse rule 5) when the delivery bypass needed the same move to set a delivered
    /// vehicle down on chosen ground; the righting path is unchanged and still its only other
    /// caller.</summary>
    private static void PlaceVehicleUpright(Unit unit, GlobalPosition ground)
    {
        if (!unit.IsServer)
        {
            return;
        }

        float yaw = unit.transform.eulerAngles.y;
        Vector3 local = ground.ToLocalPosition() + Vector3.up * RightingLiftMeters;
        if (unit.rb != null)
        {
            unit.rb.velocity = Vector3.zero;
            unit.rb.angularVelocity = Vector3.zero;
            unit.rb.position = local;
            unit.rb.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        unit.transform.SetPositionAndRotation(local, Quaternion.Euler(0f, yaw, 0f));
    }

    /// <summary>Sets a vehicle that has come to rest on its side or roof back on its wheels where it
    /// lies.</summary>
    private static void RightVehicle(Unit unit, GlobalPosition ground, ShieldRecord record)
    {
        PlaceVehicleUpright(unit, ground);
        Note(record, $"righted {record.Label} at {record.Where}: it had come to rest on its side.");
    }

    private static void Note(ShieldRecord record, string text)
    {
        if (record.Hq != null)
        {
            CommanderAiLog.Note(record.Hq, text);
        }
        else
        {
            CommanderPlugin.Log.LogInfo($"Supply: {text}");
        }
    }

    /// <summary>The shield's rules at their boundaries, run at plugin load beside the other services'
    /// checks.</summary>
    internal static void SelfCheck()
    {
        // The delivery bypass's own named checks (delivery-bypass_20260916), registered through this
        // one entry so the supply service keeps a single self-check call at plugin load.
        SelfCheckUnload();

        bool ok = InsertionSettled(true, true, 3f, 3f)
            && !InsertionSettled(true, true, 2.9f, 3f)
            && !InsertionSettled(false, true, 10f, 3f)
            && !InsertionSettled(true, false, 10f, 3f)
            && NeedsRighting(true, false, true)
            && !NeedsRighting(true, false, false)
            && !NeedsRighting(false, false, true)
            && !NeedsRighting(true, true, true)
            && ShieldExpired(90f, 90f)
            && !ShieldExpired(89f, 90f)
            && IsUpright(1f, 0.7f)
            && IsUpright(0.7f, 0.7f)
            && !IsUpright(0.1f, 0.7f)
            && !IsUpright(-1f, 0.7f);
        if (!ok)
        {
            CommanderPlugin.Log.LogError(
                "Supply self-check FAILED: the insertion shield's settle, righting, expiry or upright rule no longer "
                    + "answers at its boundaries.");
        }
    }
}
