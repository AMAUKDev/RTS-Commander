using System.Collections.Generic;

namespace GroundControlRts;

/// <summary>
/// The PLAYER COMMANDER switch, and the two rules every commander loop in the mod now asks before
/// it touches an HQ: <b>is this HQ commanded</b>, and <b>who is its opponent</b>. The AI itself is
/// not here — it stays in <see cref="CommanderEnemyCommanderService"/> and its partials, which were
/// already per-HQ. All this service does is widen the set of HQs those loops are allowed to run
/// for, from "every faction except mine" to "every faction except mine, plus mine when the switch
/// is on", and answer who each of them is playing against.
/// </summary>
/// <remarks>
/// The switch is co-command, not autopilot: the player's own clicks, orders and purchases keep
/// working while it is on, and the AI is told to leave alone any unit the player has given an order
/// to (see <c>CommanderMoveService.HasPlayerOrder</c>). Turning it off leaves whatever the AI
/// bought and positioned exactly where it is.
/// <para>
/// The <c>Enemy*</c> names in the AI files are deliberately left alone so upstream merges stay
/// clean; nothing this track adds is visible upstream beyond this one new file.
/// </para>
/// </remarks>
internal sealed class CommanderPlayerCommanderService : ICommanderTickActive, ICommanderResetSession
{
    /// <summary>Candidate HQs and their balances, reused across calls to <see cref="ChooseOpponent"/>.
    /// The review runs every 30 s for every commanded HQ, so it does not allocate a list each time.</summary>
    private static readonly List<FactionHQ> OpponentCandidates = new();
    private static readonly List<float> OpponentFunds = new();

    internal CommanderPlayerCommanderService()
    {
        Instance = this;
    }

    /// <summary>The registered service, so the settings button can flip the switch through the same
    /// <see cref="Toggle"/> the hotkey uses rather than writing the setting itself.</summary>
    internal static CommanderPlayerCommanderService? Instance { get; private set; }

    /// <summary>
    /// True when at least one commander is running: the enemy commander in any mode, or the player
    /// commander switch. This is the outer gate on the review loops, which used to test the enemy
    /// mode alone — with the enemy commander OFF and this switch ON, that gate would stop the
    /// player's own HQ ever being reviewed.
    /// </summary>
    internal static bool AnyCommanderOn =>
        CommanderEnemyCommanderService.EffectiveMode != CommanderEnemyCommanderService.ModeOff
            || CommanderSettings.PlayerCommanderEnabled;

    /// <summary>
    /// Whether an HQ inside a review loop is run by a commander. Pure, so the self-check can drive
    /// it: the local HQ is commanded only by the player switch, and every other HQ only by the
    /// enemy commander. The player switch never commands a hostile faction, and the enemy setting
    /// never commands yours.
    /// </summary>
    internal static bool IsCommanded(bool isLocalHq, bool enemyCommanderOn, bool playerCommanderOn)
    {
        return isLocalHq ? playerCommanderOn : enemyCommanderOn;
    }

    /// <summary>The live form of the rule above, used by every review loop in place of the raw
    /// <c>ReferenceEquals(hq, localHq)</c> skip it replaced.</summary>
    internal static bool IsCommanded(FactionHQ hq, FactionHQ? localHq)
    {
        return IsCommanded(
            ReferenceEquals(hq, localHq),
            CommanderEnemyCommanderService.EffectiveMode != CommanderEnemyCommanderService.ModeOff,
            CommanderSettings.PlayerCommanderEnabled);
    }

    /// <summary>
    /// Index of the largest value in <paramref name="funds"/> other than <paramref name="selfIndex"/>,
    /// or -1 when there is no other entry. First wins a tie, so the answer is stable across reviews
    /// rather than flipping between two equally rich factions. Pure, for the self-check.
    /// </summary>
    internal static int PickRichestIndex(IReadOnlyList<float> funds, int selfIndex)
    {
        int best = -1;
        for (int i = 0; i < funds.Count; i++)
        {
            if (i == selfIndex)
            {
                continue;
            }

            if (best < 0 || funds[i] > funds[best])
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Who this commander is playing against. For every HQ that is not the local one the answer is
    /// the local HQ — exactly the value every call site passed before this track, so the switch-OFF
    /// behaviour is untouched even on a three-faction mission. Only the player's own commander has
    /// to look the opponent up, and it takes the richest other faction.
    /// </summary>
    /// <remarks>
    /// "Hostile" means "another HQ" throughout the mod — <c>CommanderGameAccess.IsFriendlyUnit</c>
    /// compares HQs and nothing else — so every registered HQ with a faction is a candidate.
    /// </remarks>
    internal static FactionHQ ChooseOpponent(FactionHQ hq, FactionHQ localHq)
    {
        if (!ReferenceEquals(hq, localHq))
        {
            return localHq;
        }

        OpponentCandidates.Clear();
        OpponentFunds.Clear();
        int selfIndex = -1;
        foreach (FactionHQ candidate in FactionRegistry.GetAllHQs())
        {
            if (candidate == null || candidate.faction == null)
            {
                continue;
            }

            if (ReferenceEquals(candidate, hq))
            {
                selfIndex = OpponentCandidates.Count;
            }

            OpponentCandidates.Add(candidate);
            OpponentFunds.Add(candidate.factionFunds);
        }

        int richest = PickRichestIndex(OpponentFunds, selfIndex);
        // -1 is a solo mission: nobody else is registered. Falling back to the HQ itself keeps the
        // callers total — GetStrikeTarget and GetTerritoryCenter on one's own HQ answer with one's
        // own ground, which is harmless because there is nothing else to fly at.
        return richest < 0 ? hq : OpponentCandidates[richest];
    }

    /// <summary>
    /// How a commander names itself in the log. One definition so that every line the AI writes
    /// says which commander wrote it, rather than calling the player's own commander "Enemy".
    /// </summary>
    /// <param name="hq">The commander writing the line.</param>
    /// <param name="detail">Extra text inside the parentheses, after the faction name. Used by the
    /// unit purchase line, which has always printed the plan it bought under.</param>
    internal static string CommanderLabel(FactionHQ hq, string? detail = null)
    {
        string prefix = ReferenceEquals(hq, CommanderGameAccess.GetLocalHq())
            ? "Player commander"
            : "Enemy commander";
        return detail == null
            ? $"{prefix} ({hq.faction.name})"
            : $"{prefix} ({hq.faction.name}, {detail})";
    }

    /// <summary>
    /// One runnable check, run once from <see cref="CommanderPlugin"/> at load and logged to the
    /// BepInEx console. It covers the two pure rules this service exists for. Both are one-line
    /// expressions that a later edit can invert without anything visibly breaking: a wrong
    /// <see cref="IsCommanded"/> either hands the player's faction to the AI with the switch off or
    /// stops the enemy commander running at all, and a wrong <see cref="PickRichestIndex"/> points
    /// the player's commander at its own base.
    /// </summary>
    internal static void SelfCheck()
    {
        List<string> failures = new();

        Expect(failures, "local, both off", IsCommanded(true, false, false), false);
        // The switch-OFF invariant: the enemy commander running must never reach the player's HQ.
        Expect(failures, "local, enemy only", IsCommanded(true, true, false), false);
        Expect(failures, "local, player only", IsCommanded(true, false, true), true);
        Expect(failures, "enemy, enemy on", IsCommanded(false, true, false), true);
        // And the mirror of it: the player switch never commands a hostile HQ.
        Expect(failures, "enemy, player only", IsCommanded(false, false, true), false);

        Expect(failures, "richest hostile wins", PickRichestIndex(new[] { 10f, 30f, 20f }, 0), 1);
        Expect(failures, "never itself", PickRichestIndex(new[] { 30f, 10f, 20f }, 0), 2);
        Expect(failures, "tie goes to the first", PickRichestIndex(new[] { 10f, 20f, 20f }, 0), 1);
        Expect(failures, "nobody else", PickRichestIndex(new[] { 10f }, 0), -1);

        if (failures.Count == 0)
        {
            CommanderPlugin.Log.LogDebug("Player commander self-check passed.");
            return;
        }

        for (int i = 0; i < failures.Count; i++)
        {
            CommanderPlugin.Log.LogError($"Player commander self-check FAILED: {failures[i]}");
        }
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    private static void Expect(List<string> failures, string name, int actual, int expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    /// <summary>Flips the switch, says so on screen and in the log.</summary>
    internal void Toggle()
    {
        bool on = !CommanderSettings.PlayerCommanderEnabled;
        CommanderSettings.PlayerCommanderEnabled = on;
        // NotifyCapture is the mod's one "a match event happened, with no unit to anchor it to"
        // toast, and it deliberately ignores the combat-alerts setting. Handing your faction to
        // the AI is exactly that kind of event.
        CommanderAlertService.Instance?.NotifyCapture(on ? "PLAYER COMMANDER ON" : "PLAYER COMMANDER OFF");
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        string faction = localHq?.faction != null ? localHq.faction.name : "no faction";
        CommanderPlugin.Log.LogInfo($"Player commander switched {(on ? "ON" : "OFF")} for {faction}.");
    }

    public void TickActive()
    {
        if (CommanderShortcutInput.IsDown(CommanderSettings.TogglePlayerCommander))
        {
            Toggle();
        }
    }

    public void ResetSession()
    {
        // Nothing to clear: the switch is a plain setting and stays where the player left it across
        // missions on purpose, and the per-HQ AI state it unlocks lives in the enemy commander's
        // own states dictionary, which that service already clears.
    }
}
