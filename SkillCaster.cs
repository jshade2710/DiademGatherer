using FFXIVClientStructs.FFXIV.Client.Game;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace DiademGatherer;

// Casts gathering buff actions directly via the game's ActionManager, and
// handles mounting. Action IDs are resolved from the game's Action sheet by
// English name at runtime — no hard-coded IDs to go stale.
//
// Skill plan (per the user's Pteranodon route):
//   Normal nodes → Bountiful Yield II / Bountiful Harvest II when GP is above
//                  a threshold (dump GP so it never caps).
//   Burst nodes (R8/B8) → in priority order, whatever GP affords:
//     MIN: King's Yield II → Nald'thal's Tidings → Mountaineer's Gift II → Mountaineer's Gift I
//     BTN: Blessed Harvest II → Nophica's Tidings → Pioneer's Gift II → Pioneer's Gift I
public sealed class SkillCaster
{
    public const uint MinerJobId    = 16;
    public const uint BotanistJobId = 17;

    public const uint GeneralActionJump          = 2;
    public const uint GeneralActionRepair        = 6;   // dark-matter self-repair
    public const uint GeneralActionMountRoulette = 9;
    public const uint GeneralActionDismount      = 23;

    private static readonly string[] MinBurst =
        { "King's Yield II", "Nald'thal's Tidings", "Mountaineer's Gift II", "Mountaineer's Gift I" };
    private static readonly string[] BtnBurst =
        { "Blessed Harvest II", "Nophica's Tidings", "Pioneer's Gift II", "Pioneer's Gift I" };

    private const string MinBountiful = "Bountiful Yield II";
    private const string BtnBountiful = "Bountiful Harvest II";

    // name → action id (0 = looked up and not found)
    private readonly Dictionary<string, uint> _idCache = new(StringComparer.OrdinalIgnoreCase);

    // ── Job helpers ────────────────────────────────────────────────────────────

    private static uint CurrentJobId => Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;

    public IReadOnlyList<string> BurstList => CurrentJobId switch
    {
        MinerJobId    => MinBurst,
        BotanistJobId => BtnBurst,
        _             => Array.Empty<string>(),
    };

    public string? BountifulSkill => CurrentJobId switch
    {
        MinerJobId    => MinBountiful,
        BotanistJobId => BtnBountiful,
        _             => null,
    };

    // ── Action sheet lookup ────────────────────────────────────────────────────

    private uint ResolveId(string name)
    {
        if (_idCache.TryGetValue(name, out var cached))
            return cached;

        uint found = 0;
        foreach (var row in Plugin.DataManager.GetExcelSheet<LuminaAction>())
        {
            if (string.Equals(row.Name.ExtractText(), name, StringComparison.OrdinalIgnoreCase))
            {
                found = row.RowId;
                break;
            }
        }

        if (found == 0)
            Plugin.Log.Warning($"[DiademGatherer] Could not resolve action \"{name}\" from the Action sheet " +
                               "(non-English client?).");

        _idCache[name] = found;
        return found;
    }

    // ── Casting ────────────────────────────────────────────────────────────────

    // True if the action exists and the game says it's usable right now
    // (covers GP cost, correct job, gathering window open, etc.).
    public unsafe bool CanCast(string name)
    {
        var id = ResolveId(name);
        if (id == 0) return false;
        var am = ActionManager.Instance();
        return am != null && am->GetActionStatus(ActionType.Action, id) == 0;
    }

    public unsafe bool TryCast(string name)
    {
        var id = ResolveId(name);
        if (id == 0) return false;
        var am = ActionManager.Instance();
        if (am == null || am->GetActionStatus(ActionType.Action, id) != 0) return false;

        bool ok = am->UseAction(ActionType.Action, id);
        if (ok) Plugin.Log.Debug($"[DiademGatherer] Cast {name} ({id})");
        return ok;
    }

    public static unsafe bool TryUseGeneralAction(uint id)
    {
        var am = ActionManager.Instance();
        if (am == null || am->GetActionStatus(ActionType.GeneralAction, id) != 0) return false;
        return am->UseAction(ActionType.GeneralAction, id);
    }

    // Dismount WITHOUT consulting GetActionStatus — exactly what GBR does
    // ("Hotkey Z"). While airborne the game reports a non-zero status for the
    // dismount action, so a status-gated press is silently skipped and we just
    // hover forever; pressing it regardless is what actually starts the landing.
    public static unsafe void ForceDismount()
    {
        var am = ActionManager.Instance();
        if (am == null) return;
        am->UseAction(ActionType.GeneralAction, GeneralActionDismount);
    }

    // Fire an action at a specific target (e.g. the Aetheromatic Auger).
    // GetActionStatus covers charge/usability; UseAction enforces range/LoS.
    //
    // `why` carries the game's own reason for a refusal. Logging a bare "refused"
    // threw that away, which is how the auger spent an entire botany run failing
    // for a reason nobody could name.
    public static unsafe bool TryUseActionOnTarget(uint actionId, ulong targetId, out string why)
    {
        var am = ActionManager.Instance();
        if (am == null) { why = "ActionManager unavailable"; return false; }

        var status = am->GetActionStatus(ActionType.Action, actionId);
        if (status != 0)
        {
            why = $"GetActionStatus={status} ({ActionStatusText(status)})";
            return false;
        }

        if (!am->UseAction(ActionType.Action, actionId, targetId))
        {
            why = "UseAction refused (range, facing, or line of sight)";
            return false;
        }

        why = "";
        return true;
    }

    // The handful of status codes worth naming; the rest are rare enough that the
    // number itself is the useful thing to have in a log.
    private static string ActionStatusText(uint status) => status switch
    {
        562 => "not facing the target",
        565 => "target too far away",
        566 => "target out of range",
        571 => "target cannot be targeted",
        572 => "no line of sight",
        573 => "invalid target",
        _   => "unnamed — look it up in the LogMessage sheet",
    };

    // An action's real range in yalms, straight from the game's Action sheet, or
    // -1 when the sheet defers to weapon range. Our auger scan radius was a guess
    // (25, then widened to 32) that had never been checked against the action.
    private static readonly Dictionary<uint, float> RangeCache = new();

    public static float ActionRange(uint actionId)
    {
        if (RangeCache.TryGetValue(actionId, out var cached)) return cached;

        var range = -1f;
        try
        {
            var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(actionId);
            if (row.HasValue) range = row.Value.Range;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[DiademGatherer] Couldn't read action {actionId}'s range: {ex.Message}");
        }

        RangeCache[actionId] = range;
        Plugin.Log.Information($"[DiademGatherer] Action {actionId} range per the game's own sheet: "
            + (range < 0 ? "weapon range (sheet says -1)" : $"{range:0.#}y"));
        return range;
    }
}
