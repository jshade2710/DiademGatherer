using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using DiademGatherer.IPC;

namespace DiademGatherer;

public enum BotState
{
    Idle,
    WaitingForInstance,     // started outside the Diadem — waiting for zone-in
    FlyingToNode,           // vnavmesh move command issued
    WaitingForNavmesh,      // polling arrival
    GatheringNode,          // interact with the local node and work it
    AugerPhase,             // firing the Aetheromatic Auger at priority monsters

    // Reinstance flow:
    // leave duty → [certify at Flotpassant] → [shop at Enie] → Aurvael → re-enter
    ReinstanceLeaving,      // clicking out of the duty
    ReinstanceWaitFirmament,// waiting for the Firmament zone-in
    ReinstanceCertifying,   // Flotpassant: auto-submit + inspect, both tabs
    ReinstanceShopping,     // Enie: buy toward purchase goals with scrips
    ReinstanceRepairing,    // dark-matter repair before heading back in
    ReinstanceDialogue,     // Aurvael → Travel to the Diadem → Yes → Commence
    ReinstanceWaitDiadem,   // waiting for the Diadem zone-in
}

public sealed partial class RouteManager : IDisposable
{
    private readonly NavmeshIPC     _nav;
    private readonly Configuration  _config;

    private BotState _state        = BotState.Idle;
    private DateTime _stateEntered = DateTime.UtcNow;
    private bool     _running      = false;
    private bool     _paused       = false;

    // Playback position
    private RouteDefinition? _route;
    private bool _inLeadIn = true;     // playing LeadIn vs Cycle
    private int  _seqIndex = 0;        // index into the current sequence

    // Move-retry bookkeeping. All movement is issued from the framework tick —
    // NEVER recurse between AdvanceNode and the fly logic, that caused a
    // stack-overflow crash when vnavmesh rejected every node in one frame.
    private int      _moveAttempts     = 0;
    private int      _consecutiveSkips = 0;
    private DateTime _retryAt          = DateTime.MinValue;

    // Buff-casting bookkeeping (one cast attempt per BuffCastSpacing, each
    // skill at most once per node).
    private readonly SkillCaster    _skills;
    private readonly HashSet<string> _castThisNode = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextCastAt   = DateTime.MinValue;
    private DateTime _mountRetryAt = DateTime.MinValue;

    // Take-off bookkeeping: after mounting we jump to get airborne so every
    // leg is flown; if flight never engages we proceed anyway.
    // Persistent enough to clear the spawn platform's launch whirlwind — the
    // leg must start AIRBORNE or the ground path can clip the vortex.
    private int      _jumpAttempts = 0;
    private DateTime _jumpRetryAt  = DateTime.MinValue;
    private const int MaxJumpAttempts = 12;
    private static readonly TimeSpan JumpRetryDelay = TimeSpan.FromSeconds(1.2);

    // Consecutive nodes left without gathering anything — for ANY reason
    // (node not up here, unreachable, window never opened).
    private int _emptyNodeStreak = 0;

    // Productivity watchdog: if nothing is gathered for a whole StallWindow,
    // the instance is unproductive — reinstance and start fresh. Two barren
    // instances in a row means the problem isn't the instance; stop.
    private DateTime _lastGatherAt        = DateTime.MinValue;
    private int      _gathersThisInstance = 0;
    private int      _barrenInstances     = 0;
    private static readonly TimeSpan StallWindow = TimeSpan.FromMinutes(5);

    // Stuck escalation: repeated failed repaths mean the character is wedged in
    // geometry — reinstance rather than grind against it.
    private int _stuckRepaths = 0;
    private const int MaxStuckRepaths = 2;

    // Burst-node pre-cast: when the node window opens at R8/B8, we run the whole
    // skill priority before the first gather, so every swing is buffed.
    private bool     _precastDone    = false;
    private DateTime _lastBuffCastAt = DateTime.MinValue;
    private static readonly TimeSpan BuffStallWindow = TimeSpan.FromSeconds(3);

    // Auger phase: after each gather, spend charge on priority monsters.
    private int      _augerAttempts = 0;
    private DateTime _augerNextTry  = DateTime.MinValue;
    private static readonly TimeSpan AugerPhaseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AugerShotSpacing  = TimeSpan.FromSeconds(2.5);

    // Collision/stuck detection while following a path.
    private Vector3  _lastPos      = Vector3.Zero;
    private DateTime _lastMoveAt   = DateTime.MinValue;
    private int      _stuckActions = 0;
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromSeconds(3);

    // Off-route detours (e.g. R6 → Icetrap).
    private bool     _inDetour     = false;
    // The detour target is tracked by GameObjectId and re-resolved every use —
    // object-table SLOTS get reused when mobs die, so a cached wrapper can
    // silently start pointing at a different monster.
    private ulong    _detourTargetId;
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? _detourTarget;

    private bool InReinstanceFlow
        => _state is BotState.ReinstanceLeaving or BotState.ReinstanceWaitFirmament
                  or BotState.ReinstanceCertifying or BotState.ReinstanceShopping
                  or BotState.ReinstanceRepairing
                  or BotState.ReinstanceDialogue or BotState.ReinstanceWaitDiadem;

    // Certification bookkeeping.
    private int _certTab;      // 0 = miner tab, 1 = botany tab
    private int _certPhase;    // 0 = ensure tab, 1 = auto-submit, 2 = request inspection
    private int _certRadioTry; // which tab-radio candidate we're testing
    private int _certCycles;   // submit+inspect rounds on the current tab (safety cap)

    // Enie shopping is delegated to the shared ShopRunner.
    private readonly ShopRunner _shop;

    // Reinstance bookkeeping.
    private DateTime? _instanceEnteredAt;                 // when we last zoned into the Diadem
    private DateTime  _reinstanceStartedAt;               // start of current reinstance flow
    private DateTime  _uiActionAt = DateTime.MinValue;    // spacing between UI clicks
    private uint      _lastTerritory;

    // Guard against queueing multiple duty registrations: clicking "Travel to
    // the Diadem" repeatedly (e.g. when YesAlready races us on the confirm)
    // stacks registrations that later bounce the player between instances.
    private DateTime _selectLockoutUntil = DateTime.MinValue;
    private int      _selectClicks       = 0;
    private static readonly TimeSpan SelectLockout = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan NavTimeout         = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NavReadyTimeout    = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MoveRetryDelay     = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MountTimeout       = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MountRetryDelay    = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan BuffCastSpacing    = TimeSpan.FromSeconds(1.4);
    private static readonly TimeSpan ReinstanceTimeout  = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan RepairTimeout      = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan UiClickSpacing     = TimeSpan.FromMilliseconds(800);
    private const float AurvaelInteractRange = 6f;
    private const int   MaxMoveAttempts = 3;
    private const float ArrivalRadius   = 8f;
    // Switch to the node approach this far out, so the new path takes over while
    // we're still moving rather than after a full stop at the waypoint.
    private const float HandoffRadius   = 40f;

    public BotState CurrentState        => _state;
    public bool     IsRunning           => _running;
    public bool     IsPaused            => _paused;
    public bool     NavmeshAvailable    => _nav.IsAvailable;
    public bool     NavReady            => _nav.IsReady();
    public float    NavBuildProgress    => _nav.BuildProgress();

    public string CurrentLabel
    {
        get
        {
            var seq = CurrentSequence();
            if (seq != null && _seqIndex >= 0 && _seqIndex < seq.Count)
                return seq[_seqIndex];
            return "—";
        }
    }

    // For UI: which phase + position-in-sequence
    public string PlaybackInfo
    {
        get
        {
            if (!_running || _route == null) return "—";
            var phase = _inLeadIn ? "Lead-in" : "Cycle";
            var seq   = CurrentSequence();
            return $"{phase} {_seqIndex + 1}/{seq?.Count ?? 0}  →  {CurrentLabel}";
        }
    }

    public RouteManager(NavmeshIPC nav, Configuration config, SkillCaster skills)
    {
        _nav    = nav;
        _config = config;
        _skills = skills;
        _shop   = new ShopRunner(config, nav);

        // Node-interaction refusals arrive as error toasts, not return values —
        // this is how GBR learns it must force a landing instead of retrying.
        Plugin.ToastGui.ErrorToast += OnErrorToast;
    }

    // ── Controls ────────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_running) return;

        if (!_nav.IsAvailable)
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] vnavmesh is not available. Cannot start.");
            return;
        }

        var route = SelectedRoute();
        if (route == null)
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] No route selected.");
            return;
        }

        if (!route.IsReady(_config))
        {
            Plugin.ChatGui.PrintError(
                $"[DiademGatherer] {route.Name} has unrecorded nodes — " +
                "record them in-game first (Recorder panel or /diadem rec).");
            return;
        }

        if (Plugin.ClientState.TerritoryType != DiademData.DiademTerritoryId)
            Plugin.ChatGui.Print("[DiademGatherer] Note: you don't appear to be in the Diadem.");

        _route            = route;
        _running          = true;
        _inLeadIn         = route.LeadIn.Count > 0;
        _seqIndex         = 0;
        _moveAttempts     = 0;
        _consecutiveSkips = 0;
        _retryAt          = DateTime.MinValue;
        _jumpAttempts     = 0;
        _jumpRetryAt      = DateTime.MinValue;
        _emptyNodeStreak  = 0;
        _stuckRepaths     = 0;
        _lastGatherAt        = DateTime.UtcNow;
        _gathersThisInstance = 0;
        _barrenInstances     = 0;

        // If we were already inside when the plugin loaded, there was no
        // territory transition to observe — assume the instance clock starts now.
        if (Plugin.ClientState.TerritoryType == DiademData.DiademTerritoryId && _instanceEnteredAt == null)
            _instanceEnteredAt = DateTime.UtcNow;

        // Outside the instance? In the Firmament we can enter ourselves via
        // Aurvael; anywhere else, hold until the player zones in.
        if (Plugin.ClientState.TerritoryType != DiademData.DiademTerritoryId)
        {
            if (Plugin.ClientState.TerritoryType == DiademData.FirmamentTerritoryId)
            {
                BeginDiademEntry();
                return;
            }

            Plugin.ChatGui.Print(
                "[DiademGatherer] Not in the Diadem yet — the route will begin automatically on zone-in.");
            SetState(BotState.WaitingForInstance);
            return;
        }

        // Wrong-job guard: the route's category dictates the job. Warn loudly
        // but don't block.
        var job = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        var wantJob = route.Category switch
        {
            GatheringMode.Mining => SkillCaster.MinerJobId,
            GatheringMode.Botany => SkillCaster.BotanistJobId,
            _                    => 0u,
        };
        if (wantJob != 0 && job != wantJob)
            Plugin.ChatGui.PrintError(
                $"[DiademGatherer] {route.Name} expects " +
                $"{(wantJob == SkillCaster.MinerJobId ? "MINER" : "BOTANIST")} — switch jobs or " +
                "this run will misbehave.");

        Plugin.ChatGui.Print(
            $"[DiademGatherer] Starting {route.Name} — " +
            $"lead-in {route.LeadIn.Count}, cycle {route.Cycle.Count} nodes.");

        // The actual fly command is issued on the next framework tick
        // (HandleFlyingToNode) — never from the UI thread.
        SetState(BotState.FlyingToNode);
    }

    // Freeze in place: stop moving, stop gathering, hold all state. Resume
    // re-approaches the current node; SkipCurrentNode moves on to the next.
    private DateTime _pausedAt = DateTime.MinValue;

    public void Pause()
    {
        if (!_running || _paused) return;
        _paused   = true;
        _pausedAt = DateTime.UtcNow;
        _nav.Stop();
        Plugin.ChatGui.Print("[DiademGatherer] Paused. Resume re-approaches the current node; Skip moves to the next.");
    }

    public void Resume()
    {
        if (!_running || !_paused) return;
        _paused = false;

        // Credit the paused duration to running timers so a long pause doesn't
        // trip the reinstance-flow timeout the instant we resume.
        if (_pausedAt != DateTime.MinValue)
        {
            var pausedFor = DateTime.UtcNow - _pausedAt;
            _reinstanceStartedAt += pausedFor;
            _stateEntered        += pausedFor;
            _pausedAt = DateTime.MinValue;
        }

        // Reinstance / waiting states can just continue where they were;
        // route states re-approach the current node cleanly.
        if (!InReinstanceFlow && _state != BotState.WaitingForInstance)
        {
            _inDetour       = false;
            _detourTarget   = null;
            _moveAttempts  = 0;
            _retryAt       = DateTime.MinValue;
            _jumpAttempts  = 0;
            _jumpRetryAt   = DateTime.MinValue;
            SetState(BotState.FlyingToNode);
        }
        Plugin.ChatGui.Print($"[DiademGatherer] Resumed — heading to {CurrentLabel}.");
    }

    public void SkipCurrentNode()
    {
        if (!_running) return;
        _paused         = false;
        _inDetour       = false;
        _detourTarget   = null;
        _nav.Stop();
        Plugin.ChatGui.Print($"[DiademGatherer] Skipping {CurrentLabel}.");
        AdvanceNode();
    }

    public void Stop()
    {
        if (!_running) return;
        _running        = false;
        _paused         = false;
        _inDetour       = false;
        _detourTarget   = null;
        _nav.Stop();

        SetState(BotState.Idle);
        Plugin.ChatGui.Print("[DiademGatherer] Stopped.");
    }

    // ── Framework tick ─────────────────────────────────────────────────────────

    public void OnFrameworkUpdate(IFramework _)
    {
        // Track Diadem entry time even while idle so the reinstance timer is
        // accurate no matter when Start is pressed.
        var territory = Plugin.ClientState.TerritoryType;
        if (territory != _lastTerritory)
        {
            var previous   = _lastTerritory;
            _lastTerritory = territory;

            if (territory == DiademData.DiademTerritoryId)
                _instanceEnteredAt = DateTime.UtcNow;

            // If some OTHER plugin (AutoRetainer MultiMode, Lifestream, …) yanks
            // us out of the Diadem while we're mid-route, stop instead of
            // blindly pathing Diadem coordinates in whatever zone we landed in.
            if (_running && previous == DiademData.DiademTerritoryId && !InReinstanceFlow)
            {
                Plugin.ChatGui.PrintError(
                    "[DiademGatherer] Left the Diadem unexpectedly — stopping. " +
                    "(AutoRetainer MultiMode or another plugin may have teleported you.)");
                Stop();
                return;
            }
        }

        if (!_running || _paused) return;

        switch (_state)
        {
            case BotState.WaitingForInstance:
                if (IsZoning) break;
                if (Plugin.ClientState.TerritoryType == DiademData.DiademTerritoryId)
                {
                    Plugin.ChatGui.Print("[DiademGatherer] Entered the Diadem — starting route.");
                    ResetRouteToStart();
                    SetState(BotState.FlyingToNode);
                }
                else if (Plugin.ClientState.TerritoryType == DiademData.FirmamentTerritoryId)
                {
                    // Player made it to the Firmament — we can take it from here.
                    BeginDiademEntry();
                }
                break;

            case BotState.FlyingToNode:
                HandleFlyingToNode();
                break;
            case BotState.WaitingForNavmesh:
                CheckNavmeshArrival();
                break;
            case BotState.GatheringNode:
                HandleGatheringNode();
                break;
            case BotState.AugerPhase:
                HandleAugerPhase();
                break;

            case BotState.ReinstanceLeaving:      HandleReinstanceLeaving();      break;
            case BotState.ReinstanceWaitFirmament: HandleReinstanceWaitFirmament(); break;
            case BotState.ReinstanceCertifying:   HandleReinstanceCertifying();   break;
            case BotState.ReinstanceShopping:     HandleReinstanceShopping();     break;
            case BotState.ReinstanceRepairing:    HandleReinstanceRepairing();    break;
            case BotState.ReinstanceDialogue:     HandleReinstanceDialogue();     break;
            case BotState.ReinstanceWaitDiadem:   HandleReinstanceWaitDiadem();   break;
        }
    }

    // ── State handlers ─────────────────────────────────────────────────────────

    // Runs every framework tick while in FlyingToNode state. Issues at most one
    // vnavmesh command per tick; failures schedule a retry instead of recursing.
    private void HandleFlyingToNode()
    {
        // Between nodes is the safe moment to reinstance.
        if (_config.AutoReinstance && IsReinstanceDue)
        {
            BeginReinstance("instance timer");
            return;
        }

        // Productivity watchdog: flying the route but nothing has been gathered
        // for StallWindow → the instance is a dud (or we're wedged). Reset it.
        if (_config.AutoReinstance
            && _lastGatherAt != DateTime.MinValue
            && DateTime.UtcNow - _lastGatherAt > StallWindow
            && Plugin.ClientState.TerritoryType == DiademData.DiademTerritoryId)
        {
            BeginReinstance($"nothing gathered in {StallWindow.TotalMinutes:F0} min");
            return;
        }

        // Back-off between retries.
        if (DateTime.UtcNow < _retryAt) return;

        // Wait for the navmesh to finish building (it rebuilds on every zone-in,
        // and the Diadem mesh can take a while). This was the original crash
        // trigger: moves were being rejected because the mesh wasn't ready.
        if (!_nav.IsReady())
        {
            if (TimeSinceEntered > NavReadyTimeout)
            {
                Plugin.ChatGui.PrintError(
                    "[DiademGatherer] vnavmesh mesh never became ready. " +
                    "Check /vnav — wait for the build to hit 100% and start again.");
                Stop();
            }
            return; // keep waiting
        }

        // Mount up before every leg (mount roulette). Retries handle the brief
        // post-gather lockout; a hard timeout stops instead of hanging.
        if (!Plugin.Condition[ConditionFlag.Mounted])
        {
            // Combat blocks mounting and Diadem mobs leash on their own — pause
            // the clock rather than hard-stopping an unattended run mid-fight.
            if (Plugin.Condition[ConditionFlag.InCombat])
            {
                _stateEntered = DateTime.UtcNow;
                return;
            }

            // Still finishing a gather (window closing / animation) — don't fire
            // mount into it, just wait a beat for the state to clear.
            if (Plugin.Condition[ConditionFlag.Gathering])
                return;

            if (TimeSinceEntered > MountTimeout)
            {
                Plugin.ChatGui.PrintError(
                    "[DiademGatherer] Couldn't mount for 60s (in combat?). Stopping.");
                Stop();
                return;
            }

            if (DateTime.UtcNow >= _mountRetryAt)
            {
                SkillCaster.TryUseGeneralAction(SkillCaster.GeneralActionMountRoulette);
                _mountRetryAt = DateTime.UtcNow + MountRetryDelay;
            }
            return; // wait until mounted
        }

        // Mounted but grounded: jump to take off so the leg is ALWAYS flown —
        // never ground-run/sprint toward a node. Keep trying to get airborne; if
        // we somehow can't for a long time, stop rather than walk there.
        if (!Plugin.Condition[ConditionFlag.InFlight])
        {
            if (_jumpAttempts >= MaxJumpAttempts)
            {
                Plugin.ChatGui.PrintError(
                    "[DiademGatherer] Couldn't get airborne (obstructed take-off?). " +
                    "Stopping instead of running there on foot.");
                Stop();
                return;
            }
            if (DateTime.UtcNow >= _jumpRetryAt)
            {
                SkillCaster.TryUseGeneralAction(SkillCaster.GeneralActionJump);
                _jumpAttempts++;
                _jumpRetryAt = DateTime.UtcNow + JumpRetryDelay;
            }
            return; // wait until airborne
        }

        // Destination: the detour target if one is active, else the waypoint.
        Vector3 pos;
        string  label;
        float   range;
        if (_inDetour)
        {
            if (!DetourTargetValid()) { EndDetour(); return; } // despawned/died mid-approach
            pos   = _detourTarget!.Position;
            label = $"detour ({_detourTarget.Name.TextValue})";
            range = DiademData.DetourApproachRange;
        }
        else
        {
            if (!TryGetCurrentPosition(out pos, out label))
            {
                Plugin.Log.Warning($"[DiademGatherer] No recorded position for {label}; skipping.");
                SkipNode();
                return;
            }
            range = ArrivalRadius;
        }

        Plugin.Log.Debug($"[DiademGatherer] Flying to {label} @ {pos} (attempt {_moveAttempts + 1})");
        if (_nav.MoveCloseTo(pos, fly: true, range: range))
        {
            _lastPos      = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            _lastMoveAt   = DateTime.UtcNow;
            _stuckActions = 0;
            SetState(BotState.WaitingForNavmesh);
            return;
        }

        // Rejected — retry a couple of times with a delay, then give up on
        // this destination.
        _moveAttempts++;
        if (_moveAttempts >= MaxMoveAttempts)
        {
            Plugin.Log.Warning($"[DiademGatherer] vnavmesh rejected move to {label} " +
                               $"{MaxMoveAttempts}x; skipping.");
            if (_inDetour) EndDetour();
            else           SkipNode();
        }
        else
        {
            _retryAt = DateTime.UtcNow + MoveRetryDelay;
        }
    }

    // A node was skipped without being gathered. If we skip an entire lap's
    // worth of nodes in a row, something is systemically wrong — stop cleanly
    // instead of spinning forever.
    private void SkipNode()
    {
        _consecutiveSkips++;
        var routeLength = _route!.LeadIn.Count + _route.Cycle.Count;

        if (_consecutiveSkips >= routeLength)
        {
            Plugin.ChatGui.PrintError(
                "[DiademGatherer] Every node in the route was skipped — vnavmesh is " +
                "rejecting all movement. Are you in the Diadem with the mesh built? Stopping.");
            Stop();
            return;
        }

        AdvanceNode();
    }

    private void CheckNavmeshArrival()
    {
        // Collision/stuck detection: path active but we haven't moved for 3s →
        // jump to break the collision (twice), then recalculate the path.
        var curPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        if (Vector3.Distance(curPos, _lastPos) > 0.5f)
        {
            _lastPos      = curPos;
            _lastMoveAt   = DateTime.UtcNow;
            _stuckActions = 0;
        }
        else if (_nav.IsBusy() && DateTime.UtcNow - _lastMoveAt > StuckThreshold)
        {
            _stuckActions++;
            _lastMoveAt = DateTime.UtcNow; // rate-limit follow-ups
            if (_stuckActions <= 2)
            {
                Plugin.Log.Debug($"[DiademGatherer] Stuck for 3s ({_stuckActions}/2) — jumping.");
                SkillCaster.TryUseGeneralAction(SkillCaster.GeneralActionJump);
            }
            else
            {
                _stuckRepaths++;

                // Wedged in geometry: repathing isn't helping — reset the whole
                // instance rather than grinding here indefinitely.
                if (_stuckRepaths > MaxStuckRepaths)
                {
                    _nav.Stop();
                    if (_config.AutoReinstance
                        && Plugin.ClientState.TerritoryType == DiademData.DiademTerritoryId)
                    {
                        Plugin.ChatGui.Print(
                            $"[DiademGatherer] Stuck at {CurrentLabel} after {_stuckRepaths} repaths — reinstancing.");
                        BeginReinstance("stuck");
                    }
                    else
                    {
                        Plugin.ChatGui.PrintError(
                            $"[DiademGatherer] Stuck at {CurrentLabel} after {_stuckRepaths} repaths — " +
                            "skipping the node (enable Auto Reinstance to reset the instance instead).");
                        _stuckRepaths = 0;
                        SkipNode();
                    }
                    return;
                }

                Plugin.Log.Warning($"[DiademGatherer] Still stuck after jumps — recalculating path " +
                                   $"({_stuckRepaths}/{MaxStuckRepaths}).");
                _nav.Stop();
                _moveAttempts = 0;
                _retryAt      = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
                _stuckActions = 0;
                SetState(BotState.FlyingToNode); // re-issues the same destination
                return;
            }
        }

        // NOTE: no opportunistic mid-flight shots. Stopping mid-leg to fire cost
        // more time than it gained and interfered with the route; the auger now
        // fires only at nodes (post-gather) and on the Icetrap detour.

        // ── Detour approach: fly to the monster, shoot, resume the route ──────
        if (_inDetour)
        {
            if (!DetourTargetValid()) { _nav.Stop(); EndDetour(); return; } // killed in flight / despawned

            var me       = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            float toMob  = Vector3.Distance(me, _detourTarget!.Position);

            // Fly ALL THE WAY to the approach point before doing anything —
            // dismounting early while hovering high caused a fall that put the
            // target out of range and abandoned the detour. The path ends at
            // ground level next to the mob, so dismounting there is safe.
            bool arrived = !_nav.IsBusy() && TimeSinceEntered > TimeSpan.FromSeconds(3);

            if (arrived)
            {
                if (DateTime.UtcNow < _augerNextTry) return;

                // Path ended too far away (unreachable perch etc.) — give up cleanly.
                if (toMob > DiademData.AugerScanRange + 5f)
                {
                    Plugin.Log.Warning($"[DiademGatherer] Detour approach ended {toMob:F0}y from target; resuming route.");
                    EndDetour();
                    return;
                }

                // Can't fire mounted — we're grounded/hovering low now, hop off.
                if (Plugin.Condition[ConditionFlag.Mounted])
                {
                    SkillCaster.ForceDismount();
                    _augerNextTry = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
                    return;
                }

                var detGauge = GameUiHelper.AugerGaugeValue();
                if (SkillCaster.TryUseActionOnTarget(DiademData.AugerActionId, _detourTarget.GameObjectId))
                {
                    Plugin.ChatGui.Print($"[DiademGatherer] Auger fired at {_detourTarget.Name.TextValue} (detour, gauge={detGauge}).");
                    EndDetour();
                    return;
                }

                Plugin.Log.Information(
                    $"[DiademGatherer] Detour auger refused (gauge={detGauge}, attempt {_augerAttempts + 1}).");
                _augerAttempts++;
                if (_augerAttempts >= 3)
                {
                    Plugin.Log.Warning($"[DiademGatherer] Couldn't fire at detour target " +
                                       $"(dist={toMob:F0}); resuming route.");
                    EndDetour();
                    return;
                }
                _augerNextTry = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                return;
            }

            if (TimeSinceEntered > NavTimeout)
            {
                Plugin.Log.Warning("[DiademGatherer] Detour flight timed out; resuming route.");
                _nav.Stop();
                EndDetour();
            }
            return;
        }

        // ── Normal waypoint arrival ───────────────────────────────────────────
        if (!TryGetCurrentPosition(out var target, out var label))
        {
            AdvanceNode();
            return;
        }

        var pos     = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        float dist  = Vector3.Distance(pos, target);
        bool stopped = !_nav.IsBusy() && TimeSinceEntered > TimeSpan.FromSeconds(3);

        // Hand over to the node approach as soon as the node is actually visible,
        // WITHOUT waiting to finish the hop to the waypoint. The approach then
        // replaces the running path mid-flight, so we curve straight into the node
        // instead of flying to the waypoint, stopping dead, and setting off again —
        // that stop-and-restart at every node is what makes the route look jagged.
        if (dist <= HandoffRadius && FindGatheringNode(target, _config.NodeSearchRadius) != null)
        {
            Plugin.Log.Debug($"[DiademGatherer] Node visible near {label} (dist={dist:F1}) — approaching.");
            OnArrival();
            return;
        }

        if (dist <= ArrivalRadius || (stopped && dist < 20f))
        {
            Plugin.Log.Debug($"[DiademGatherer] Arrived at {label} (dist={dist:F1})");
            OnArrival();
            return;
        }

        if (TimeSinceEntered > NavTimeout)
        {
            Plugin.Log.Warning(
                $"[DiademGatherer] Nav timeout to {label} (dist={dist:F1}). " +
                "Is the vnavmesh mesh built for this area? Skipping.");
            _nav.Stop();
            SkipNode();
        }
    }

    private void OnArrival()
    {
        _consecutiveSkips = 0; // we actually reached a node — reset the skip guard
        _stuckRepaths     = 0;
        _castThisNode.Clear();
        _nextCastAt       = DateTime.MinValue;
        _precastDone      = false;

        // Work the node at this waypoint ourselves.
        _nativeNode           = null;
        _nativeNodeId         = 0;
        _gatherBuffed         = false;
        _gatherPending        = false;
        _lastGp               = 0;
        _gatherStarted        = false;
        _buffGpBefore         = 0;

        _navIssued            = false;
        _navTarget            = null;
        _spotLearned          = false;
        _navCandidate         = 0;
        _forceLanding         = false;
        _forceLandSpot        = null;
        _landPressAt          = DateTime.MinValue;
        _landAttemptSince     = DateTime.MinValue;
        _approachSince        = DateTime.UtcNow;
        _landedAt             = DateTime.MinValue;
        _wasInFlight          = false;
        _wasMounted           = false;
        _groundLegCount       = 0;
        _pathBuilder.Reset();
        _navBestH             = float.MaxValue;
        _navStallSince        = DateTime.MinValue;
        _navRetryAt           = DateTime.MinValue;
        _gatherCap            = DateTime.MinValue;
        _gatherStep           = DateTime.MinValue;
        SetState(BotState.GatheringNode);
    }

    // Left a waypoint without gathering, for ANY reason (node not up here,
    // unreachable, window never opened). Counting every such exit is what makes
    // the misconfiguration alarm fire instead of lapping the route in silence.
    private void NoGatherHere(string reason)
    {
        // Left a node without exhausting it — the chain's next node won't spawn,
        // so don't sit waiting for it at the following waypoints.
        _chainIntact = false;
        _emptyNodeStreak++;
        Plugin.Log.Warning($"[DiademGatherer] {reason} at {CurrentLabel} " +
                           $"(empty streak {_emptyNodeStreak}); advancing.");

        if (_emptyNodeStreak == 3)
            Plugin.ChatGui.PrintError(
                "[DiademGatherer] Nothing gathered at 3 nodes in a row — nodes may not be up on this " +
                "route, or the recorded waypoints are off. Check you're on the right job/route.");

        // A whole lap with zero gathers is conclusive: no instance will fix it.
        var lap = _route!.Cycle.Count;
        if (lap > 0 && _emptyNodeStreak >= lap)
        {
            Plugin.ChatGui.PrintError(
                "[DiademGatherer] A full lap with ZERO gathers — no nodes are being found at these " +
                "waypoints. Check the route's recorded positions and that you're on the right job. Stopping.");
            Stop();
            return;
        }

        FinishWaypoint();
    }

    private void FinishWaypoint()
    {

        // Detour nodes (e.g. R6): if the detour monster is up within range and
        // we have charge, fly off-route to it before continuing.
        if (_config.UseAuger
            && _route!.DetourNodes.Contains(CurrentLabel)
            && GameUiHelper.AugerGaugeValue() >= DiademData.AugerGaugeReady)
        {
            var target = FindDetourTarget();
            if (target != null)
            {
                Plugin.ChatGui.Print(
                    $"[DiademGatherer] {target.Name.TextValue} is up — detouring to shoot it.");
                _inDetour        = true;
                _detourTarget    = target;
                _detourTargetId  = target.GameObjectId;
                _augerAttempts = 0;
                _augerNextTry  = DateTime.MinValue;
                _moveAttempts  = 0;
                _retryAt       = DateTime.MinValue;
                _jumpAttempts  = 0;
                _jumpRetryAt   = DateTime.MinValue;
                SetState(BotState.FlyingToNode);
                return;
            }
        }

        AdvanceNode();
    }

    // Detour is finished (target shot, dead, despawned, or unreachable) —
    // resume the normal route.
    private void EndDetour()
    {
        _inDetour       = false;
        _detourTarget   = null;
        _detourTargetId = 0;
        AdvanceNode();
    }

    // The route's detour monster (highest priority tier only), scanned wider
    // than normal firing range.
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindDetourTarget()
    {
        if (_route?.DetourMonster == null) return null;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;

        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        float bestDist = DiademData.AugerDetourRange;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleChara chara) continue;
            if (chara.CurrentHp == 0 || !chara.IsTargetable) continue;
            if (!chara.Name.TextValue.Contains(_route.DetourMonster, StringComparison.OrdinalIgnoreCase)) continue;

            var dist = Vector3.Distance(player.Position, chara.Position);
            if (dist < bestDist)
            {
                best     = chara;
                bestDist = dist;
            }
        }

        return best;
    }

    // True while the detour target still exists and is worth shooting. Always
    // re-resolves by id — never trusts the cached slot wrapper.
    private bool DetourTargetValid()
    {
        _detourTarget = _detourTargetId == 0 ? null : Plugin.ObjectTable.SearchById(_detourTargetId);
        return _detourTarget is Dalamud.Game.ClientState.Objects.Types.IBattleChara { CurrentHp: > 0, IsTargetable: true };
    }


    // Casts the route's buff plan while the gathering window is open.
    //   Burst nodes  → priority list, each once, whatever GP affords right now.
    //   Normal nodes → Bountiful (once) only when GP is above the anti-cap
    //                  threshold, so GP is banked for the burst nodes.
    // Returns true if it actually cast a buff this call (so callers can wait out
    // the animation lock only when there was one).
    private bool CastNodeBuffs()
    {
        if (DateTime.UtcNow < _nextCastAt) return false;

        string? toCast = null;

        if (_route!.BurstNodes.Contains(CurrentLabel))
        {
            foreach (var skill in _skills.BurstList)
            {
                if (_castThisNode.Contains(skill)) continue;
                if (_skills.CanCast(skill)) { toCast = skill; break; }
            }
        }
        else
        {
            var bountiful = _skills.BountifulSkill;
            if (bountiful != null && !_castThisNode.Contains(bountiful))
            {
                var gp = Plugin.ObjectTable.LocalPlayer?.CurrentGp ?? 0;
                if (gp >= _config.BountifulMinGp && _skills.CanCast(bountiful))
                    toCast = bountiful;
            }
        }

        if (toCast != null && _skills.TryCast(toCast))
        {
            _castThisNode.Add(toCast);
            _nextCastAt     = DateTime.UtcNow + BuffCastSpacing;
            _lastBuffCastAt = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    // ── Auger ──────────────────────────────────────────────────────────────────

    // Fires the Aetheromatic Auger at the highest-priority monster in range
    // until the charge runs dry, nothing is in range, or the phase times out.
    private void HandleAugerPhase()
    {
        if (TimeSinceEntered > AugerPhaseTimeout) { EndAugerPhase(); return; }
        if (DateTime.UtcNow < _augerNextTry) return;

        // Charge spent (or gauge unreadable) → done shooting.
        if (GameUiHelper.AugerGaugeValue() < DiademData.AugerGaugeReady)
        {
            EndAugerPhase();
            return;
        }

        // The auger CANNOT be fired while mounted — this was why mid-flight
        // stops and detours "did nothing". Dismount (falling briefly is fine),
        // then fire. Dismount retries don't count as fire attempts; the phase
        // timeout bounds the whole thing.
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            SkillCaster.ForceDismount();
            _augerNextTry = DateTime.UtcNow + TimeSpan.FromSeconds(1.2); // land + settle
            return;
        }

        var target = FindAugerTarget();
        if (target == null) { EndAugerPhase(); return; } // nothing on the list nearby

        var gauge = GameUiHelper.AugerGaugeValue();
        if (SkillCaster.TryUseActionOnTarget(DiademData.AugerActionId, target.GameObjectId))
        {
            Plugin.Log.Information($"[DiademGatherer] Auger FIRED at {target.Name.TextValue} (gauge={gauge}).");
            _augerAttempts = 0;
            _augerNextTry  = DateTime.UtcNow + AugerShotSpacing; // let the cast resolve, then look again
            return;
        }

        // Unusable right now — one quick retry, then move on.
        Plugin.Log.Information(
            $"[DiademGatherer] Auger action refused at {target.Name.TextValue} (gauge={gauge}, attempt {_augerAttempts + 1}).");
        _augerAttempts++;
        if (_augerAttempts >= 2) { EndAugerPhase(); return; }
        _augerNextTry = DateTime.UtcNow + TimeSpan.FromSeconds(1);
    }

    // The auger phase only ever runs at a node now, so leaving it always
    // continues the route.
    private void EndAugerPhase() => FinishWaypoint();

    // Highest-priority live monster within auger range; distance breaks ties
    // within the same priority tier.
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindAugerTarget()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;

        foreach (var name in DiademData.AugerPriority)
        {
            Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
            float bestDist = DiademData.AugerScanRange;

            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleChara chara) continue;
                if (chara.CurrentHp == 0 || !chara.IsTargetable) continue;
                if (!chara.Name.TextValue.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;

                var dist = Vector3.Distance(player.Position, chara.Position);
                if (dist < bestDist)
                {
                    best     = chara;
                    bestDist = dist;
                }
            }

            if (best != null) return best;
        }

        return null;
    }

    // Only updates playback indices and re-enters FlyingToNode. The actual
    // move command is issued by HandleFlyingToNode on a subsequent framework
    // tick — this method must never call into vnavmesh directly.
    private void AdvanceNode()
    {
        var seq = CurrentSequence()!;
        _seqIndex++;

        if (_seqIndex >= seq.Count)
        {
            if (_inLeadIn)
            {
                // Lead-in finished → switch to the repeating cycle.
                _inLeadIn = false;
                _seqIndex = 0;
                if (_route!.Cycle.Count == 0) { Stop(); return; }
            }
            else
            {
                if (_config.LoopRoute)
                {
                    _seqIndex = 0;
                    Plugin.ChatGui.Print("[DiademGatherer] Cycle complete — looping.");
                }
                else
                {
                    Plugin.ChatGui.Print("[DiademGatherer] Route complete.");
                    Stop();
                    return;
                }
            }
        }

        _moveAttempts = 0;
        _retryAt      = DateTime.MinValue;
        _jumpAttempts = 0;
        _jumpRetryAt  = DateTime.MinValue;
        SetState(BotState.FlyingToNode);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    public RouteDefinition? SelectedRoute()
        => DiademData.FindByName(_config.SelectedRouteName)
        ?? DiademData.RoutesFor(_config.Mode).FirstOrDefault();

    private IReadOnlyList<string>? CurrentSequence()
        => _route == null ? null : (_inLeadIn ? _route.LeadIn : _route.Cycle);

    private bool TryGetCurrentPosition(out Vector3 pos, out string label)
    {
        label = CurrentLabel;
        return _route!.TryGetNode(_config, label, out pos);
    }

    private void SetState(BotState s)
    {
        _state        = s;
        _stateEntered = DateTime.UtcNow;
    }

    private TimeSpan TimeSinceEntered => DateTime.UtcNow - _stateEntered;

    public void Dispose()
    {
        Plugin.ToastGui.ErrorToast -= OnErrorToast;
        Stop();
    }
}
