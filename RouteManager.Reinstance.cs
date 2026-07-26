using System.Numerics;
using Dalamud.Game.ClientState.Conditions;

namespace DiademGatherer;

// Reinstance pipeline: leave duty -> certify (Flotpassant) -> shop (Enie) ->
// re-enter via Aurvael -> restart the route from the top.
public sealed partial class RouteManager
{
    // ── Reinstancing ───────────────────────────────────────────────────────────

    public TimeSpan? InstanceElapsed
        => _instanceEnteredAt == null ? null : DateTime.UtcNow - _instanceEnteredAt;

    private bool IsReinstanceDue
        => _instanceEnteredAt != null
        && Plugin.ClientState.TerritoryType == DiademData.DiademTerritoryId
        && DateTime.UtcNow - _instanceEnteredAt.Value >= TimeSpan.FromMinutes(_config.ReinstanceMinutes);

    // Called by the Force Reinstance button / command.
    public void TriggerReinstance()
    {
        if (!_running)
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] Start the bot first — reinstance runs as part of the loop.");
            return;
        }
        if (InReinstanceFlow)
            return; // already reinstancing (any stage, incl. certify/shop)

        BeginReinstance("manual");
    }

    private void BeginReinstance(string reason)
    {
        Plugin.ChatGui.Print($"[DiademGatherer] Reinstancing ({reason}) — leaving the duty…");
        _nav.Stop();
        _inDetour       = false;
        _detourTarget   = null;
        _reinstanceStartedAt = DateTime.UtcNow;
        _uiActionAt          = DateTime.MinValue;
        _selectClicks        = 0;
        _selectLockoutUntil  = DateTime.MinValue;
        _certInterrupted     = false;
        SetState(BotState.ReinstanceLeaving);
    }

    // Enter the Diadem from the Firmament via Aurvael, reusing the reinstance
    // dialogue machinery (talk → Travel to the Diadem → Yes → Commence).
    private void BeginDiademEntry()
    {
        Plugin.ChatGui.Print("[DiademGatherer] Entering the Diadem via Aurvael…");
        _reinstanceStartedAt = DateTime.UtcNow;
        _uiActionAt          = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        _selectClicks        = 0;
        _selectLockoutUntil  = DateTime.MinValue;
        SetState(BotState.ReinstanceDialogue);
    }

    private bool ReinstanceTimedOut()
    {
        if (DateTime.UtcNow - _reinstanceStartedAt <= ReinstanceTimeout) return false;
        Plugin.ChatGui.PrintError(
            "[DiademGatherer] Reinstance flow timed out (8 min). Stopping — check where you ended up.");
        Stop();
        return true;
    }

    private bool IsZoning
        => Plugin.Condition[ConditionFlag.BetweenAreas]
        || Plugin.Condition[ConditionFlag.BetweenAreas51]
        || Plugin.ObjectTable.LocalPlayer == null;

    private void PaceUi() => _uiActionAt = DateTime.UtcNow + UiClickSpacing;

    private void HandleReinstanceLeaving()
    {
        if (ReinstanceTimedOut()) return;

        if (Plugin.ClientState.TerritoryType != DiademData.DiademTerritoryId)
        {
            _leaveAttempts = 0;
            SetState(BotState.ReinstanceWaitFirmament);
            return;
        }

        if (IsZoning || DateTime.UtcNow < _uiActionAt) return;

        // Leave-duty confirmation may pop a Yes/No.
        if (GameUiHelper.IsVisible("SelectYesno")) { GameUiHelper.ClickYes(); PaceUi(); return; }

        if (GameUiHelper.ClickLeaveDuty())
        {
            _leaveAttempts = 0;
            PaceUi();
            return;
        }

        // The menu wasn't clickable. Open it and try again next tick — but log
        // WHY, because a silent loop here just re-opens the window forever.
        _leaveAttempts++;
        Plugin.Log.Debug(
            $"[DiademGatherer] Leave-duty attempt {_leaveAttempts}: " +
            $"ContentsFinderMenu visible={GameUiHelper.IsVisible("ContentsFinderMenu")}");

        // After ~8s of failing, dump the visible windows so the real addon name
        // is identifiable instead of guessed.
        if (_leaveAttempts == 10)
        {
            Plugin.ChatGui.PrintError(
                "[DiademGatherer] Can't click Leave Duty — dumping visible addons to probe.log. " +
                "Please share it; leaving the duty manually will let the bot continue.");
            GameUiHelper.ListVisibleAddons();
        }

        GameUiHelper.ShowDutyMenu(); // opens ContentsFinderMenu; clicked next tick
        PaceUi();
    }

    private int  _leaveAttempts;
    private bool _certInterrupted;   // broke off certifying to spend scrips → resume after

    // Accumulated Firmament score per appraisal tab (0 = miner, 1 = botanist),
    // learned while certifying. -1/absent = not read yet.
    private readonly Dictionary<int, int> _gatherScoreByTab = new();

    private static string CertTabName(int tab) => tab switch
    {
        0 => "Miner", 1 => "Botanist", 2 => "Fisher", _ => $"tab {tab}",
    };

    // Score for a gathering job, or -1 if we haven't certified with it yet.
    public int GatherScore(uint job) => job switch
    {
        SkillCaster.MinerJobId    => _gatherScoreByTab.TryGetValue(0, out var m) ? m : -1,
        SkillCaster.BotanistJobId => _gatherScoreByTab.TryGetValue(1, out var b) ? b : -1,
        _                         => -1,
    };

    // True once this job has hit the per-class cap — more gathering is wasted.
    private bool JobCapped(uint job)
    {
        var s = GatherScore(job);
        return s >= 0 && s >= _config.MaxAccumulatedScore;
    }

    // Skybuilders' scrips cap at 10k and a single inspection can pay ~1.8k, so
    // dump at the lower of the user's threshold and (cap − headroom). That way
    // certification never overflows into "you cannot carry any more".
    private const int ScripCap          = 10_000;
    private const int CertScripHeadroom = 2_000;

    private bool NeedsScripDumpBeforeCert()
    {
        if (!ShopGoals.AnyPending(_config)) return false;   // nothing to spend them on
        var scrips = ItemHelper.ScripCount();
        if (scrips < 0) return false;
        return scrips >= Math.Min(_config.ScripDumpAt, ScripCap - CertScripHeadroom);
    }

    private void HandleReinstanceWaitFirmament()
    {
        if (ReinstanceTimedOut() || IsZoning) return;

        if (Plugin.ClientState.TerritoryType == DiademData.FirmamentTerritoryId)
        {
            _uiActionAt = DateTime.UtcNow + TimeSpan.FromSeconds(2); // let NPCs load

            if (_config.EnableCertification)
            {
                Plugin.Log.Debug("[DiademGatherer] In the Firmament — certifying at Flotpassant first.");
                _reinstanceStartedAt = DateTime.UtcNow;
                _certTab = 0; _certPhase = 0; _certRadioTry = 0; _certCycles = 0;
                SetState(BotState.ReinstanceCertifying);
            }
            else
            {
                ProceedToShoppingOrEntry();
            }
        }
    }

    // Firmament stops share this exit: shop only once scrips have reached the
    // Shop-tab threshold (same rule as the crafting loop — don't walk to Enie
    // after every certification for a handful of scrips), else on to Aurvael.
    private void ProceedToShoppingOrEntry()
    {
        _reinstanceStartedAt = DateTime.UtcNow;
        _uiActionAt          = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);

        var scrips = ItemHelper.ScripCount();
        if (scrips >= _config.ScripDumpAt && ShopGoals.AnyPending(_config))
        {
            Plugin.ChatGui.Print($"[DiademGatherer] {scrips:N0} scrips — spending at Enie before heading back in.");
            _shop.Begin();
            SetState(BotState.ReinstanceShopping);
        }
        else
        {
            ProceedToRepairOrEntry();
        }
    }

    // Last stop before heading back in: repair if anything is worn past the
    // threshold, otherwise straight to Aurvael. Gear loses its stat bonus at 0%,
    // so it's worth the detour rather than gathering with degraded stats.
    private void ProceedToRepairOrEntry()
    {
        _reinstanceStartedAt = DateTime.UtcNow;
        _uiActionAt          = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);

        // This job has hit the 500k cap — everything gathered from here on is
        // worth nothing, so don't head back in.
        var job = _route?.Category switch
        {
            GatheringMode.Mining => SkillCaster.MinerJobId,
            GatheringMode.Botany => SkillCaster.BotanistJobId,
            _                    => 0u,
        };
        if (job != 0 && JobCapped(job))
        {
            Plugin.ChatGui.Print(
                $"[DiademGatherer] {CertTabName(job == SkillCaster.MinerJobId ? 0 : 1)} has reached " +
                $"{_config.MaxAccumulatedScore:N0} points — nothing more to earn here. Stopping.");
            Stop();
            return;
        }

        if (_config.EnableRepair)
        {
            var worst = ItemHelper.LowestEquippedCondition(out var wornItem);
            if (worst >= 0 && worst < _config.RepairThreshold)
            {
                // GBR checks for usable dark matter up front and aborts with a
                // message rather than opening a repair window that can't do
                // anything — otherwise we'd just burn the timeout every lap.
                if (!ItemHelper.HasRepairMaterial(wornItem))
                {
                    Plugin.ChatGui.PrintError(
                        $"[DiademGatherer] Gear at {worst}% but no dark matter to repair with — " +
                        "restock it, or turn repair off in Settings.");
                }
                else
                {
                    Plugin.ChatGui.Print($"[DiademGatherer] Gear at {worst}% — repairing before heading back in.");
                    _repairPhase = 0;
                    SetState(BotState.ReinstanceRepairing);
                    return;
                }
            }
        }

        Plugin.Log.Debug("[DiademGatherer] Heading to Aurvael.");
        _selectClicks       = 0;
        _selectLockoutUntil = DateTime.MinValue;
        SetState(BotState.ReinstanceDialogue);
    }

    // Dark-matter self-repair: fire the Repair general action, then Repair All in
    // the window it opens, confirming the dark-matter prompt.
    private int _repairPhase;

    private void HandleReinstanceRepairing()
    {
        if (IsZoning) return;

        // Never let repair hold the whole loop up — head in regardless.
        if (DateTime.UtcNow - _reinstanceStartedAt > RepairTimeout)
        {
            Plugin.ChatGui.PrintError(
                "[DiademGatherer] Repair didn't complete (out of dark matter?) — continuing without it.");
            GameUiHelper.CloseAddon("Repair");
            _selectClicks       = 0;
            _selectLockoutUntil = DateTime.MinValue;
            SetState(BotState.ReinstanceDialogue);
            return;
        }

        // Repairs are refused while mounted — GBR bails outright on this, and in
        // the Firmament we usually are mounted.
        if (Plugin.Condition[ConditionFlag.Mounted])
        {
            SkillCaster.ForceDismount();
            return;
        }

        // Repair in progress — let it finish before touching anything else.
        if (Plugin.Condition[ConditionFlag.Occupied39]) return;

        if (DateTime.UtcNow < _uiActionAt) return;
        _uiActionAt = DateTime.UtcNow + UiClickSpacing;

        // Confirm the "repair with dark matter?" prompt whenever it appears.
        if (GameUiHelper.TryGetSelectYesnoText(out _)) { GameUiHelper.ClickYes(); return; }

        var worst = ItemHelper.LowestEquippedCondition();

        if (!GameUiHelper.IsVisible("Repair"))
        {
            // Repaired enough → tidy up and go.
            if (worst < 0 || worst >= _config.RepairThreshold)
            {
                // The RepairAuto window can linger after repairing and would sit
                // on top of the Aurvael dialogue — GBR force-closes it too.
                if (GameUiHelper.IsVisible("RepairAuto")) { GameUiHelper.CloseAddon("RepairAuto"); return; }

                Plugin.ChatGui.Print($"[DiademGatherer] Gear repaired ({worst}%) — heading back in.");
                _selectClicks       = 0;
                _selectLockoutUntil = DateTime.MinValue;
                SetState(BotState.ReinstanceDialogue);
                return;
            }

            SkillCaster.TryUseGeneralAction(SkillCaster.GeneralActionRepair);
            return;
        }

        // Window is open — Repair All, then let the confirm above handle the rest.
        // Once everything is mended, close it so the check above can finish.
        if (worst >= _config.RepairThreshold) { GameUiHelper.CloseAddon("Repair"); return; }

        GameUiHelper.RepairAll();
        _repairPhase++;
        if (_repairPhase > 6) GameUiHelper.CloseAddon("Repair"); // stop poking a stuck window
    }

    // ── Certification at Flotpassant ───────────────────────────────────────────
    // Per tab (miner, then botany): Auto-submit (node 8) → Request Inspection
    // (node 10), repeating while Auto-submit stays enabled, then next tab.
    // Tab radios are identified empirically: click a candidate, verify via the
    // window's active-tab value, try the next on mismatch.
    private void HandleReinstanceCertifying()
    {
        if (ReinstanceTimedOut() || IsZoning) return;

        // Pulled out of the Firmament somehow? Re-sync instead of hunting for
        // the NPC in the wrong zone until the timeout.
        if (Plugin.ClientState.TerritoryType != DiademData.FirmamentTerritoryId)
        {
            SetState(BotState.ReinstanceWaitFirmament);
            return;
        }

        if (DateTime.UtcNow < _uiActionAt) return;

        // Submission confirms are fine to accept during certification.
        if (GameUiHelper.IsVisible("SelectYesno")) { GameUiHelper.ClickYes(); PaceUi(); return; }

        if (GameUiHelper.IsVisible("HWDGathereInspect"))
        {
            const string win = "HWDGathereInspect";

            switch (_certPhase)
            {
                case 0: // make sure the desired tab is active
                    if (GameUiHelper.InspectActiveTab() == _certTab)
                    {
                        _certPhase = 1;
                        return;
                    }
                    if (_certRadioTry >= GameUiHelper.InspectTabRadioNodes.Length)
                    {
                        Plugin.Log.Warning($"[DiademGatherer] Couldn't switch to inspect tab {_certTab}; skipping it.");
                        AdvanceCertTab();
                        return;
                    }
                    GameUiHelper.ClickButton(win, GameUiHelper.InspectTabRadioNodes[_certRadioTry]);
                    _certRadioTry++;
                    _uiActionAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(900);
                    return;

                case 1: // auto-submit, if there is anything to certify
                    // Per-class Firmament cap (500,000). Points past it are wasted,
                    // so stop certifying for that class. A score of -1 means we
                    // couldn't read it — treat that as "no cap" rather than
                    // stopping the run on a bad read.
                    var tabScore = GameUiHelper.InspectAccumulatedScore();
                    if (tabScore >= 0)
                    {
                        _gatherScoreByTab[_certTab] = tabScore;
                        if (tabScore >= _config.MaxAccumulatedScore)
                        {
                            Plugin.ChatGui.Print(
                                $"[DiademGatherer] {CertTabName(_certTab)} is at {tabScore:N0} / " +
                                $"{_config.MaxAccumulatedScore:N0} — capped, skipping its turn-ins.");
                            AdvanceCertTab();
                            return;
                        }
                    }

                    // Never cap: one inspection pays ~1.8k scrips, so break off
                    // and spend at Enie before starting a batch that would
                    // overflow, then come back and finish certifying.
                    if (NeedsScripDumpBeforeCert())
                    {
                        Plugin.ChatGui.Print(
                            $"[DiademGatherer] {ItemHelper.ScripCount():N0} scrips — spending at Enie " +
                            "before certifying more.");
                        GameUiHelper.CloseAddon(win);
                        _certInterrupted     = true;
                        _reinstanceStartedAt = DateTime.UtcNow;
                        _shop.Begin();
                        SetState(BotState.ReinstanceShopping);
                        return;
                    }

                    if (!GameUiHelper.IsButtonEnabled(win, GameUiHelper.InspectAutoSubmitNode))
                    {
                        Plugin.Log.Debug($"[DiademGatherer] Nothing to certify on tab {_certTab}.");
                        AdvanceCertTab();
                        return;
                    }
                    GameUiHelper.ClickButton(win, GameUiHelper.InspectAutoSubmitNode);
                    _certPhase  = 2;
                    _uiActionAt = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
                    return;

                case 2: // request inspection, then loop for another batch
                    GameUiHelper.ClickButton(win, GameUiHelper.InspectInspectionNode);
                    _certCycles++;
                    _certPhase  = 1;
                    _uiActionAt = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
                    if (_certCycles > 6) AdvanceCertTab(); // safety cap
                    return;
            }
            return;
        }

        // Window not open yet — work Flotpassant's dialogue.
        if (GameUiHelper.IsVisible("SelectString"))
        {
            if (!GameUiHelper.SelectStringEntry("inspect")
                && !GameUiHelper.SelectStringEntry("resource"))
            {
                Plugin.ChatGui.PrintError(
                    "[DiademGatherer] Couldn't find the inspection entry in Flotpassant's menu " +
                    "(entries dumped to probe.log) — skipping certification.");
                ProceedToShoppingOrEntry();
            }
            PaceUi();
            return;
        }
        if (GameUiHelper.ClickTalk()) { PaceUi(); return; }

        var flotpassant = FindNpc(DiademData.FlotpassantName);
        if (flotpassant == null) return; // NPCs still loading

        var player = Plugin.ObjectTable.LocalPlayer!;
        if (Vector3.Distance(player.Position, flotpassant.Position) > AurvaelInteractRange)
        {
            _nav.MoveCloseTo(flotpassant.Position, fly: false, range: 4f);
            _uiActionAt = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            return;
        }

        _nav.Stop();
        GameUiHelper.InteractWith(flotpassant.Address);
        _uiActionAt = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
    }

    private void AdvanceCertTab()
    {
        _certTab++;
        _certPhase    = 0;
        _certRadioTry = 0;
        _certCycles   = 0;

        if (_certTab > 1) // miner + botany done
        {
            Plugin.Log.Debug("[DiademGatherer] Certification complete.");
            GameUiHelper.CloseAddon("HWDGathereInspect");
            ProceedToShoppingOrEntry();
        }
    }

    // ── Shopping at Enie (delegated to the shared ShopRunner) ───────────────────

    private void HandleReinstanceShopping()
    {
        if (ReinstanceTimedOut() || IsZoning) return;

        // Wrong-zone re-sync (something pulled us out of the Firmament).
        if (Plugin.ClientState.TerritoryType != DiademData.FirmamentTerritoryId)
        {
            SetState(BotState.ReinstanceWaitFirmament);
            return;
        }

        if (!_shop.Tick()) return;

        // We broke off certifying to avoid capping — go finish it first.
        if (_certInterrupted)
        {
            _certInterrupted     = false;
            _reinstanceStartedAt = DateTime.UtcNow;
            _uiActionAt          = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
            _certPhase = 0; _certRadioTry = 0; _certCycles = 0; // reopen + reselect the tab
            Plugin.ChatGui.Print("[DiademGatherer] Scrips spent — back to Flotpassant to finish certifying.");
            SetState(BotState.ReinstanceCertifying);
            return;
        }

        // Done shopping → repair if needed, then Aurvael.
        ProceedToRepairOrEntry();
    }

    private void HandleReinstanceDialogue()
    {
        if (ReinstanceTimedOut()) return;

        // Commence accepted → we're loading into the Diadem.
        if (Plugin.ClientState.TerritoryType == DiademData.DiademTerritoryId)
        {
            SetState(BotState.ReinstanceWaitDiadem);
            return;
        }

        if (IsZoning || DateTime.UtcNow < _uiActionAt) return;

        // Confirmation windows are always safe to answer.
        if (GameUiHelper.IsVisible("ContentsFinderConfirm")) { GameUiHelper.ClickCommence(); PaceUi(); return; }
        if (GameUiHelper.IsVisible("SelectYesno"))           { GameUiHelper.ClickYes();      PaceUi(); return; }

        if (GameUiHelper.IsVisible("SelectString"))
        {
            // One click per lockout window — each click queues a duty
            // registration, and stacked registrations bounce the player
            // between instances after entry.
            if (DateTime.UtcNow < _selectLockoutUntil) return;

            if (_selectClicks >= 3)
            {
                Plugin.ChatGui.PrintError(
                    "[DiademGatherer] Selected 'Travel to the Diadem' 3× without zoning in — " +
                    "something is eating the confirmation (YesAlready?). Stopping to avoid " +
                    "queueing duplicate registrations.");
                Stop();
                return;
            }

            if (!GameUiHelper.SelectStringEntry("Diadem"))
            {
                Plugin.ChatGui.PrintError(
                    "[DiademGatherer] Aurvael's menu has no 'Diadem' entry — stopping.");
                Stop();
                return;
            }

            _selectClicks++;
            _selectLockoutUntil = DateTime.UtcNow + SelectLockout;
            PaceUi();
            return;
        }

        if (GameUiHelper.ClickTalk()) { PaceUi(); return; }

        // Nothing open. If a registration was just made, WAIT — the confirm /
        // loading screen is coming. Re-interacting now would queue another.
        if (DateTime.UtcNow < _selectLockoutUntil) return;

        // Find Aurvael and talk to him.
        var aurvael = FindAurvael();
        if (aurvael == null) return; // NPCs may still be streaming in

        var player = Plugin.ObjectTable.LocalPlayer!;
        if (Vector3.Distance(player.Position, aurvael.Position) > AurvaelInteractRange)
        {
            _nav.MoveCloseTo(aurvael.Position, fly: false, range: 4f);
            _uiActionAt = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            return;
        }

        _nav.Stop();
        GameUiHelper.InteractWith(aurvael.Address);
        _uiActionAt = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
    }

    private void HandleReinstanceWaitDiadem()
    {
        if (ReinstanceTimedOut() || IsZoning) return;
        if (Plugin.ClientState.TerritoryType != DiademData.DiademTerritoryId) return;

        // Barren-instance guard: if the instance we just left produced nothing,
        // and the one before it didn't either, a fresh instance won't help —
        // the item list is wrong. Stop instead of reinstancing forever.
        if (_gathersThisInstance == 0) _barrenInstances++;
        else                           _barrenInstances = 0;
        _gathersThisInstance = 0;

        if (_barrenInstances >= 2)
        {
            Plugin.ChatGui.PrintError(
                "[DiademGatherer] Two instances in a row with zero gathers — reinstancing isn't the " +
                "problem. Check the route's recorded positions and that you're on the right job. Stopping.");
            Stop();
            return;
        }

        // We're in. Restart the route from the very top, as a fresh run.
        _instanceEnteredAt = DateTime.UtcNow;
        ResetRouteToStart();
        Plugin.ChatGui.Print("[DiademGatherer] Reinstanced — restarting route from the top.");
        SetState(BotState.FlyingToNode);
    }

    private void ResetRouteToStart()
    {
        _inLeadIn         = _route!.LeadIn.Count > 0;
        _seqIndex         = 0;
        _moveAttempts     = 0;
        _consecutiveSkips = 0;
        _emptyNodeStreak  = 0;
        _retryAt          = DateTime.MinValue;
        _jumpAttempts     = 0;
        _jumpRetryAt      = DateTime.MinValue;
        _inDetour         = false;
        _detourTarget     = null;
        _detourTargetId   = 0;
        _stuckRepaths     = 0;
        _lastGatherAt     = DateTime.UtcNow; // fresh productivity window
        _castThisNode.Clear();
    }

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNpc(string name)
        => GameUiHelper.FindNpc(name);

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindAurvael()
        => FindNpc(DiademData.AurvaelName);
}
