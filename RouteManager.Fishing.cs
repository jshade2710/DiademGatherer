using System.Numerics;
using Dalamud.Game.ClientState.Conditions;

namespace DiademGatherer;

// Diadem fishing. Structurally the opposite of the gathering routes: there is
// one spot, we never leave it, and we don't drive the rod at all — AutoHook
// does. So this file is mostly about getting the character to stand in exactly
// the right place, handing over, and noticing when the handover stopped working.
//
// What we keep ownership of:
//   • flying to the hole and landing in it
//   • selecting the AutoHook preset and switching AutoHook on/off
//   • the instance clock, certification, repair and shopping (shared with the
//     gathering routes — all of that already existed)
//   • noticing that no line is in the water and re-kicking the session
//
// What AutoHook owns: every cast, tug read, hookset, Double/Triple Hook, GP
// spend and bait swap. We never send a fishing action ourselves.
public sealed partial class RouteManager
{
    // Cast. Hard-coded rather than resolved by name because "Cast" is a short
    // and not-necessarily-unique string in the Action sheet, and getting the
    // wrong row here would mean silently mis-answering "are we standing in a
    // fishing hole" — the one question this state machine turns on.
    private const uint CastActionId = 289;

    // Standing tolerance. A fishing hole is a disc a few yalms across, so this
    // is deliberately tighter than the route's ArrivalRadius: "close to the
    // hole" and "in the hole" are different things, and only the second casts.
    private const float FishArriveH = 2.5f;
    private const float FishArriveV = 3f;

    // How far we may drift from the hole before we go back and stand in it.
    // Generous relative to FishArriveH: once a line is out we're committed, and
    // the cost of a needless re-approach (AutoHook off, fly, land, restart) is
    // far higher than a yalm of slop.
    private const float FishDriftLimit = 8f;

    private static readonly TimeSpan FishApproachTimeout  = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FishLandPressSpacing = TimeSpan.FromMilliseconds(500);

    // How long the rod may be out of the water before we assume AutoHook has
    // stopped driving and re-kick it. A full Sweatfish cycle (cast, wait past
    // the 14s window, hook, reel) runs well under this; a stalled session shows
    // up as the flag simply never coming back on.
    private static readonly TimeSpan FishIdleBeforeKick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FishKickSpacing    = TimeSpan.FromSeconds(10);
    private const int MaxFishKicks = 3;

    private DiademData.FishingHole _fishHole = DiademData.Sweatfish;
    private Vector3? _fishSpot;
    private uint     _fishItemId;
    private int      _fishCountAtStart;
    private int      _fishCaught;

    private DateTime _fishApproachSince = DateTime.MinValue;
    private bool     _fishNavIssued;
    private DateTime _fishLandPressAt   = DateTime.MinValue;
    private DateTime _fishLogAt         = DateTime.MinValue;

    private bool     _autoHookOn;
    private DateTime _fishLineLastOut = DateTime.MinValue;
    private DateTime _fishKickAt      = DateTime.MinValue;
    private int      _fishKicks;

    private DateTime _fishCountAt = DateTime.MinValue;
    private static readonly TimeSpan FishCountInterval = TimeSpan.FromSeconds(1);

    public bool IsFishing  => _state is BotState.FishingApproach or BotState.FishingActive;
    public int  FishCaught => _fishCaught;

    // The hole this run is actually working while running, and the configured
    // one otherwise — so changing the dropdown mid-run can't make the status
    // panel claim we're somewhere we aren't.
    public DiademData.FishingHole FishingHole
        => _running && IsFishing ? _fishHole : DiademData.FishingHoleByLabel(_config.FishingHoleLabel);

    // ── Entry ──────────────────────────────────────────────────────────────────

    // Begin (or resume, after a reinstance) the fishing loop. Safe to call with
    // the spot already resolved — the sheet lookup is cached in _fishSpot.
    private void BeginFishing()
    {
        _fishHole = DiademData.FishingHoleByLabel(_config.FishingHoleLabel);

        if (_fishItemId == 0)
            _fishItemId = ItemHelper.ResolveItemId(_fishHole.TargetFish);

        // The catch count is a DELTA against inventory, so it has to be
        // rebaselined every time we re-enter: certification empties the fish
        // out, and counting from a stale baseline would read as a big negative.
        _fishCountAtStart = _fishItemId == 0 ? 0 : ItemHelper.CountInInventory(_fishHole.TargetFish);
        _fishCaught       = 0;

        _fishApproachSince = DateTime.UtcNow;
        _fishNavIssued     = false;
        _fishLandPressAt   = DateTime.MinValue;
        _fishKicks         = 0;
        _fishKickAt        = DateTime.MinValue;
        _fishLineLastOut   = DateTime.MinValue;
        _jumpAttempts      = 0;
        _jumpRetryAt       = DateTime.MinValue;
        _mountRetryAt      = DateTime.MinValue;
        _retryAt           = DateTime.MinValue;

        // Force AutoHook off for the approach, unconditionally — not via
        // StopAutoHook, which no-ops when we don't think we switched it on.
        // AutoHook is very often already enabled from the user's own session, and
        // then it would start casting the moment we land, on whatever preset
        // happened to be selected. Off now, on in EnterFishingActive, means the
        // preset is always chosen before the first cast.
        _autoHook.Disable();
        _autoHookOn = false;

        SetState(BotState.FishingApproach);
    }

    // ── Spot resolution ────────────────────────────────────────────────────────

    // A fishing hole's world position, from the game's own FishingSpot sheet.
    //
    // This is why no coordinates are hard-coded for fishing: the sheet carries
    // real world X/Z for every hole, which is both exact and robust to the
    // Diadem being re-laid out. The sheet gives no Y — holes sit on cloud
    // shelves anywhere from Y -4 to Y +34 — so the altitude is snapped off the
    // navmesh with a deliberately tall vertical extent, the same trick
    // CombinedPath uses for nodes sunk into terrain.
    private Vector3? ResolveFishingSpot(DiademData.FishingHole hole)
    {
        // A recorded spot is an explicit override and wins: it exists precisely
        // for the cases where the sheet lookup can't work.
        if (_config.TryGetFishingSpot(hole.Label, out var recorded))
        {
            Plugin.Log.Information($"[DiademGatherer] Using the recorded spot for {hole.SpotName}: " +
                                   $"<{recorded.X:F1}, {recorded.Y:F1}, {recorded.Z:F1}>.");
            return recorded;
        }

        float x = 0, z = 0;
        var found = false;
        try
        {
            foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.FishingSpot>())
            {
                if (row.TerritoryType.RowId != DiademData.DiademTerritoryId) continue;
                var name = row.PlaceName.ValueNullable?.Name.ExtractText();
                if (!string.Equals(name, hole.SpotName, StringComparison.OrdinalIgnoreCase)) continue;
                x = row.X; z = row.Z; found = true;
                break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[DiademGatherer] Reading the FishingSpot sheet failed");
        }

        if (!found)
        {
            Plugin.ChatGui.PrintError(
                $"[DiademGatherer] Couldn't find \"{hole.SpotName}\" in the game's fishing-spot data " +
                "(non-English client?). Fly there yourself, stand in the hole, and press " +
                "\"Record this spot\" in the Fishing panel.");
            return null;
        }

        // Snap onto the mesh. halfExtentY is huge on purpose: we may be issuing
        // this from the spawn platform hundreds of yalms below the hole, and the
        // query is for "the mesh at this X/Z", not "the mesh near my altitude".
        var here    = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var flat    = new Vector3(x, here.Y, z);
        var snapped = _nav.NearestPoint(flat, 15f, 200f) ?? _nav.PointOnFloor(flat, 15f);

        if (snapped == null)
        {
            Plugin.ChatGui.PrintError(
                $"[DiademGatherer] {hole.SpotName} is at X {x:F0}, Z {z:F0} per the game's data, but " +
                "vnavmesh has no mesh there. Wait for the mesh to finish building, or record the " +
                "spot manually in the Fishing panel.");
            return null;
        }

        Plugin.Log.Information($"[DiademGatherer] {hole.SpotName} resolved from the FishingSpot sheet " +
                               $"to <{snapped.Value.X:F1}, {snapped.Value.Y:F1}, {snapped.Value.Z:F1}> " +
                               $"(sheet X {x:F0}, Z {z:F0}).");
        return snapped;
    }

    // ── Approach ───────────────────────────────────────────────────────────────

    private void HandleFishingApproach()
    {
        if (_config.AutoReinstance && IsReinstanceDue) { BeginReinstance("instance timer"); return; }
        if (DateTime.UtcNow < _retryAt) return;

        if (!_nav.IsReady())
        {
            if (TimeSinceEntered > NavReadyTimeout)
            {
                Plugin.ChatGui.PrintError(
                    "[DiademGatherer] vnavmesh mesh never became ready. Check /vnav and start again.");
                Stop();
            }
            return;
        }

        _fishSpot ??= ResolveFishingSpot(_fishHole);
        if (_fishSpot == null) { Stop(); return; }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var spot = _fishSpot.Value;
        var hSep = Vector2.Distance(new Vector2(spot.X, spot.Z),
                                    new Vector2(player.Position.X, player.Position.Z));
        var vSep = MathF.Abs(spot.Y - player.Position.Y);

        var mounted  = Plugin.Condition[ConditionFlag.Mounted];
        var inFlight = Plugin.Condition[ConditionFlag.InFlight];

        if (DateTime.UtcNow >= _fishLogAt)
        {
            Plugin.Log.Information($"[DiademGatherer] {_fishHole.SpotName} approach: " +
                                   $"h={hSep:F1} v={vSep:F1} mounted={mounted} inFlight={inFlight} " +
                                   $"castable={CanCastLine()}");
            _fishLogAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        }

        // The authoritative "am I in the hole" test is whether the game will let
        // us Cast — position is just how we get there.
        //
        // "...or a line is already out" matters more than it looks: AutoHook may
        // well be enabled globally when we arrive, in which case it starts
        // fishing by itself the instant we touch down. Cast is then refused for
        // the obvious reason, and testing castability alone would strand us here
        // until the two-minute timeout while the character fished happily.
        if (!mounted && !inFlight && (CanCastLine() || Plugin.Condition[ConditionFlag.Fishing]))
        {
            EnterFishingActive();
            return;
        }

        if (DateTime.UtcNow - _fishApproachSince > FishApproachTimeout)
        {
            _nav.Stop();
            Plugin.ChatGui.PrintError(
                $"[DiademGatherer] Couldn't get into {_fishHole.SpotName} in two minutes " +
                $"(h={hSep:F1} v={vSep:F1}). Stopping — if the position looks wrong, stand in the " +
                "hole and press \"Record this spot\".");
            Stop();
            return;
        }

        // Far away → fly. Mount and get airborne first, exactly as the route
        // legs do, so we never ground-run across the Diadem.
        if (hSep > FishArriveH || vSep > FishArriveV)
        {
            if (!EnsureAirborne()) return;

            if (!_fishNavIssued)
            {
                if (_nav.MoveCloseTo(spot, fly: true, range: 1.5f))
                {
                    _fishNavIssued = true;
                    Plugin.Log.Debug($"[DiademGatherer] Flying to {_fishHole.SpotName} @ {spot}.");
                }
                else
                {
                    _retryAt = DateTime.UtcNow + MoveRetryDelay;
                }
                return;
            }

            // vnavmesh finished but we're still short — re-path rather than hover.
            if (!_nav.IsBusy()) _fishNavIssued = false;
            return;
        }

        // In position but airborne: land. Unlike a gathering node there is
        // nothing here to interact with, so nothing dismounts us automatically —
        // we have to press it ourselves.
        if (mounted || inFlight)
        {
            _nav.Stop();
            _fishNavIssued = false;
            if (DateTime.UtcNow >= _fishLandPressAt)
            {
                SkillCaster.ForceDismount();
                _fishLandPressAt = DateTime.UtcNow + FishLandPressSpacing;
            }
            return;
        }

        // Standing in the right place, but the game still won't let us cast —
        // we're on the rim rather than in the disc. Walk the last stretch.
        if (!_nav.IsBusy()) _nav.MoveCloseTo(spot, fly: false, range: 0.5f);
    }

    // Mount + take off, the same shape HandleFlyingToNode uses. Returns true
    // once we're airborne and the caller may issue a move.
    private bool EnsureAirborne()
    {
        if (!Plugin.Condition[ConditionFlag.Mounted])
        {
            if (Plugin.Condition[ConditionFlag.InCombat]) { _stateEntered = DateTime.UtcNow; return false; }
            if (DateTime.UtcNow >= _mountRetryAt)
            {
                SkillCaster.TryUseGeneralAction(SkillCaster.GeneralActionMountRoulette);
                _mountRetryAt = DateTime.UtcNow + MountRetryDelay;
            }
            return false;
        }

        if (!Plugin.Condition[ConditionFlag.InFlight])
        {
            if (_jumpAttempts >= MaxJumpAttempts)
            {
                Plugin.ChatGui.PrintError(
                    "[DiademGatherer] Couldn't get airborne (obstructed take-off?). Stopping.");
                Stop();
                return false;
            }
            if (DateTime.UtcNow >= _jumpRetryAt)
            {
                SkillCaster.TryUseGeneralAction(SkillCaster.GeneralActionJump);
                _jumpAttempts++;
                _jumpRetryAt = DateTime.UtcNow + JumpRetryDelay;
            }
            return false;
        }

        return true;
    }

    private static unsafe bool CanCastLine()
    {
        var am = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (am == null) return false;
        return am->GetActionStatus(
            FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, CastActionId) == 0;
    }

    // ── Fishing ────────────────────────────────────────────────────────────────

    private void EnterFishingActive()
    {
        _nav.Stop();
        _fishNavIssued   = false;
        _fishLineLastOut = DateTime.UtcNow;   // grace period before the first kick
        _fishKicks       = 0;

        if (!_autoHook.IsAvailable)
        {
            Plugin.ChatGui.PrintError(
                "[DiademGatherer] AutoHook isn't installed or isn't loaded, so nothing would " +
                "actually fish. Stopping.");
            Stop();
            return;
        }

        Plugin.ChatGui.Print(
            $"[DiademGatherer] In {_fishHole.SpotName} — handing the rod to AutoHook " +
            $"(preset \"{_config.AutoHookPreset}\").");

        // Preset first, then enable: switching preset under a running session is
        // the one ordering that can leave AutoHook fishing the previous preset.
        _autoHook.SelectPreset(_config.AutoHookPreset);
        _autoHook.Enable();
        _autoHook.StartFishing();
        _autoHookOn = true;

        SetState(BotState.FishingActive);
    }

    private void HandleFishingActive()
    {
        // Count catches first, so the watchdogs below see current numbers.
        // Throttled: CountInInventory walks every slot of every player bag, and
        // doing that 60 times a second for a two-and-a-half-hour instance is a
        // lot of work to notice a fish a second late.
        if (_fishItemId != 0 && DateTime.UtcNow >= _fishCountAt)
        {
            _fishCountAt = DateTime.UtcNow + FishCountInterval;
            var net = ItemHelper.CountInInventory(_fishHole.TargetFish) - _fishCountAtStart;
            if (net > _fishCaught)
            {
                var gained = net - _fishCaught;
                _fishCaught           = net;
                _lastGatherAt         = DateTime.UtcNow; // feeds the productivity watchdog
                _gathersThisInstance += gained;          // and the barren-instance guard

                // A fish in the bag is unambiguous proof the rotation is alive,
                // so it clears the idle watchdog outright. That matters because
                // the watchdog's other input is a condition flag: if that flag
                // ever means something narrower than "a line is out", this is
                // what stops us killing a run that was working fine.
                _fishLineLastOut = DateTime.UtcNow;
                _fishKicks       = 0;
                Plugin.Log.Information(
                    $"[DiademGatherer] +{gained} {_fishHole.TargetFish} " +
                    $"({_fishCaught} this instance, ~{_fishCaught * _fishHole.PointsEach:N0} pts).");
            }
        }

        if (_config.AutoReinstance && IsReinstanceDue)
        {
            StopAutoHook("reinstancing");
            BeginReinstance("instance timer");
            return;
        }

        // Did something move us out of the hole? Judge that by POSITION, not by
        // whether Cast is currently usable: the game refuses Cast while a line
        // is out, during the reel, and through the catch animation, so a
        // castability test here would bounce us out mid-cycle every single cast.
        // Distance from the spot can't false-positive that way.
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player != null && _fishSpot != null)
        {
            var spot = _fishSpot.Value;
            var drift = Vector2.Distance(new Vector2(spot.X, spot.Z),
                                         new Vector2(player.Position.X, player.Position.Z));
            if (drift > FishDriftLimit || Plugin.Condition[ConditionFlag.Mounted])
            {
                Plugin.Log.Warning($"[DiademGatherer] Moved out of {_fishHole.SpotName} " +
                                   $"(drift {drift:F1}y, mounted={Plugin.Condition[ConditionFlag.Mounted]}) " +
                                   "— re-approaching.");
                StopAutoHook("out of the hole");
                _fishApproachSince = DateTime.UtcNow;
                _fishNavIssued     = false;
                SetState(BotState.FishingApproach);
                return;
            }
        }

        // Rod in the water? That's our only progress signal — AutoHook reports
        // no state by any route, so a stalled session is invisible except as
        // this flag never coming back on.
        if (Plugin.Condition[ConditionFlag.Fishing])
        {
            _fishLineLastOut = DateTime.UtcNow;
            _fishKicks       = 0;
            return;
        }

        if (DateTime.UtcNow - _fishLineLastOut < FishIdleBeforeKick) return;
        if (DateTime.UtcNow < _fishKickAt) return;

        if (_fishKicks >= MaxFishKicks)
        {
            Plugin.ChatGui.PrintError(
                $"[DiademGatherer] AutoHook hasn't put a line in the water after {MaxFishKicks} " +
                "attempts. Check that the preset name matches AutoHook's exactly (it is " +
                "case-sensitive and untrimmed) and that AutoHook is enabled. Stopping.");
            Stop();
            return;
        }

        _fishKicks++;
        _fishKickAt = DateTime.UtcNow + FishKickSpacing;
        Plugin.Log.Warning($"[DiademGatherer] No line out for {FishIdleBeforeKick.TotalSeconds:F0}s — " +
                           $"re-kicking AutoHook (attempt {_fishKicks}/{MaxFishKicks}).");
        _autoHook.SelectPreset(_config.AutoHookPreset);
        _autoHook.Enable();
        _autoHook.StartFishing();
    }

    // Hand the rod back. Always paired with leaving FishingActive: leaving
    // AutoHook enabled while we fly to Flotpassant would have it casting at
    // whatever it finds on the way.
    private void StopAutoHook(string why)
    {
        if (!_autoHookOn) return;
        _autoHookOn = false;
        Plugin.Log.Information($"[DiademGatherer] Switching AutoHook off ({why}).");
        _autoHook.Disable();
    }
}
