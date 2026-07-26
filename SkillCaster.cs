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
    public static unsafe bool TryUseActionOnTarget(uint actionId, ulong targetId)
    {
        var am = ActionManager.Instance();
        if (am == null || am->GetActionStatus(ActionType.Action, actionId) != 0) return false;
        return am->UseAction(ActionType.Action, actionId, targetId);
    }
}
