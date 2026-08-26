using System.Numerics;
using Dalamud.Plugin.Services;
using DiademGatherer.IPC;

namespace DiademGatherer;

// Firmament crafting loop, run OUTSIDE the Diadem:
//   Artisan crafts a batch → walk to Potkin (Collectable Appraiser) and turn
//   the collectables in (HWDSupply) → every N turn-ins, Lizbeth plays kupo
//   cards (HWDLottery) → when scrips near cap, dump them into the Shop-tab
//   goals at Enie → repeat. Fully automated once the windows were probed.
public enum CraftState
{
    Idle,
    StartBatch,     // resolve goal + kick Artisan
    WaitArtisan,    // Artisan crafting; wait until batch done
    GoToPotkin,     // walk to the appraiser
    AwaitTurnIn,    // hand in collectables via HWDSupply
    GoToLizbeth,    // walk to Kupo of Fortune
    AwaitKupo,      // scratch kupo cards until vouchers hit the floor
    KupoWrapUp,     // dismiss Lizbeth's closing dialogue so crafting can resume
    Shopping,       // spend scrips at Enie (ShopRunner)
    ReturnHome,     // walk back to the crafting spot, then next batch
}

public sealed class CraftingManager : IDisposable
{
    private readonly ArtisanIPC    _artisan;
    private readonly NavmeshIPC    _nav;
    private readonly Configuration _config;

    private CraftState _state = CraftState.Idle;
    private DateTime   _stateEntered = DateTime.UtcNow;
    private bool       _running;

    private Configuration.CraftGoal? _goal;
    private int      _countAtTurnInStart;
    private DateTime _actionAt = DateTime.MinValue;   // pacing for walks/interacts
    private DateTime _artisanGraceUntil;              // time for Artisan to get going
    private int      _artisanKicks;                   // kicks for the current batch
    private bool     _rekick;                          // next StartBatch is a re-kick, not a fresh goal
    private readonly HashSet<string> _skippedGoals = new(StringComparer.OrdinalIgnoreCase); // ran out of materials this run
    private uint     _goalJob;                        // crafter job the goal belongs to
    private DateTime _turnInStep = DateTime.MinValue; // pacing for turn-in clicks
    private int      _turnInPrev;                     // last observed bag count (incremental counting)
    private bool     _turnInInterrupted;              // paused mid-turn-in to spend scrips → resume at Potkin
    private DateTime _kupoStep   = DateTime.MinValue; // pacing for kupo clicks
    private int      _kupoPlays;                      // safety cap on cards per visit
    private DateTime _kupoProgress;                   // last meaningful kupo action
    private int      _kupoReinteracts;                // Lizbeth re-interact attempts
    private DateTime _kupoWrapStep;                   // pacing for closing-dialogue clicks
    private DateTime _kupoWrapUntil;                  // keep clearing until nothing shows this long
    private static readonly Random Rng = new();

    // The crafting spot to return to after the Potkin/Lizbeth/Enie excursion.
    private Vector3? _homePos;

    // Kupo-only test run (Play Kupo Now): after the kupo session, stop instead
    // of continuing into shop/next-batch.
    private bool _kupoOnly;

    // Set when a turn-in was declined because kupo vouchers were capped, so the
    // kupo session returns to Potkin instead of moving on to shopping/crafting.
    private bool _resumeTurnInAfterKupo;

    private readonly ShopRunner _shop;

    public int TurnInsTotal     { get; private set; }
    public int TurnInsSinceKupo { get; private set; }

    // Last accumulated score read per crafter job (8..15), used to skip capped
    // classes without opening the window.
    private readonly Dictionary<uint, int> _scoreByJob = new();
    public int LastScore(uint job) => _scoreByJob.TryGetValue(job, out var s) ? s : -1;

    private const string PotkinName  = "Potkin";
    private const string LizbethName = "Lizbeth";

    private static readonly TimeSpan TurnInTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan KupoTimeout   = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan CraftTimeout  = TimeSpan.FromMinutes(45);

    public CraftState CurrentState => _state;
    public bool       IsRunning    => _running;
    public bool       ArtisanAvailable => _artisan.IsAvailable;
    public string     CurrentGoalName  => _goal?.ItemName ?? "—";

    public CraftingManager(ArtisanIPC artisan, NavmeshIPC nav, Configuration config)
    {
        _artisan = artisan;
        _nav     = nav;
        _config  = config;
        _shop    = new ShopRunner(config, nav);
    }

    // ── Controls ───────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_running) return;

        if (Plugin.ClientState.TerritoryType != DiademData.FirmamentTerritoryId)
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] Crafting runs in the Firmament — go there first.");
            return;
        }
        if (!_artisan.IsAvailable)
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] Artisan is not available (install/enable it).");
            return;
        }
        if (!_config.CraftGoals.Any(g => g.Enabled && !string.IsNullOrWhiteSpace(g.ItemName)))
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] No enabled craft goals — add some on the Crafting tab.");
            return;
        }

        _running           = true;
        TurnInsSinceKupo   = 0;
        _resumeTurnInAfterKupo = false;
        _artisanKicks      = 0;
        _rekick            = false;
        _turnInInterrupted = false;
        _skippedGoals.Clear();
        // Remember where crafting happens so we can return after each excursion.
        _homePos           = Plugin.ObjectTable.LocalPlayer?.Position;
        SetState(CraftState.StartBatch);
        Plugin.ChatGui.Print("[DiademGatherer] Crafting loop started.");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _artisan.StopCrafting();
        _nav.Stop();
        SetState(CraftState.Idle);
        Plugin.ChatGui.Print("[DiademGatherer] Crafting loop stopped.");
    }

    // Manual override: skip the current kupo session and move on.
    public void ContinueFromKupo()
    {
        if (_state == CraftState.AwaitKupo)
        {
            TurnInsSinceKupo = 0;
            EnterShopStage();
        }
    }

    // Test / on-demand: run just the kupo excursion (walk to Lizbeth, play,
    // return home), even if the main loop isn't running.
    public void ForceKupo()
    {
        if (_running && _state != CraftState.Idle)
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] Crafting loop is mid-step — let it finish or Stop first.");
            return;
        }
        if (Plugin.ClientState.TerritoryType != DiademData.FirmamentTerritoryId)
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] Kupo runs in the Firmament — go there first.");
            return;
        }

        _running   = true;
        _kupoOnly  = true;
        _kupoPlays = 0;
        _kupoStep  = DateTime.MinValue;
        _homePos   = Plugin.ObjectTable.LocalPlayer?.Position;
        Plugin.ChatGui.Print("[DiademGatherer] Play Kupo Now — heading to Lizbeth.");
        SetState(CraftState.GoToLizbeth);
    }

    // ── Tick ───────────────────────────────────────────────────────────────────

    public void OnFrameworkUpdate(IFramework _)
    {
        if (!_running) return;

        // Crafting is a Firmament activity; leaving it (manually queueing into
        // the Diadem, teleporting, …) suspends the loop.
        if (Plugin.ClientState.TerritoryType != DiademData.FirmamentTerritoryId)
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] Left the Firmament — crafting loop stopped.");
            Stop();
            return;
        }

        switch (_state)
        {
            case CraftState.StartBatch:  HandleStartBatch();  break;
            case CraftState.WaitArtisan: HandleWaitArtisan(); break;
            case CraftState.GoToPotkin:  HandleGoTo(PotkinName,  CraftState.AwaitTurnIn, OnReachedPotkin);  break;
            case CraftState.AwaitTurnIn: HandleAwaitTurnIn(); break;
            case CraftState.GoToLizbeth: HandleGoTo(LizbethName, CraftState.AwaitKupo,   OnReachedLizbeth); break;
            case CraftState.AwaitKupo:   HandleAwaitKupo();   break;
            case CraftState.KupoWrapUp:  HandleKupoWrapUp();  break;
            case CraftState.Shopping:
                if (Plugin.ClientState.TerritoryType != DiademData.FirmamentTerritoryId) { Stop(); break; }
                if (_shop.Tick()) EndShopping();
                break;
            case CraftState.ReturnHome:  HandleReturnHome();  break;
        }
    }

    // ── Handlers ───────────────────────────────────────────────────────────────

    // First enabled goal whose class is below the score cap (per last-known
    // score; unknown classes are assumed workable). null → all done/capped.
    private Configuration.CraftGoal? NextGoal()
    {
        foreach (var g in _config.CraftGoals)
        {
            if (!g.Enabled || string.IsNullOrWhiteSpace(g.ItemName)) continue;
            if (_skippedGoals.Contains(g.ItemName)) continue;   // out of materials this run
            var (_, job) = ItemHelper.ResolveRecipe(g.ItemName);
            if (job == 0) continue;
            var score = LastScore(job);
            if (score >= 0 && score >= _config.MaxAccumulatedScore) continue; // capped
            return g;
        }
        return null;
    }

    private void HandleStartBatch()
    {
        // Fresh goal gets its own kick budget; a re-kick of the same goal keeps
        // the running count.
        if (!_rekick) _artisanKicks = 0;
        _rekick = false;

        _goal = NextGoal();
        if (_goal == null)
        {
            Plugin.ChatGui.Print("[DiademGatherer] All craft goals done, capped, or out of materials — stopping.");
            Stop();
            return;
        }

        var (recipe, job) = ItemHelper.ResolveRecipe(_goal.ItemName);
        if (recipe == 0)
        {
            Plugin.ChatGui.PrintError($"[DiademGatherer] No recipe for \"{_goal.ItemName}\" — check the name. Stopping.");
            Stop();
            return;
        }
        _goalJob = job;

        var have = ItemHelper.CountInInventory(_goal.ItemName);
        if (have >= _goal.Batch)
        {
            SetState(CraftState.GoToPotkin); // batch already in the bag
            return;
        }

        var jobNow = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        if (jobNow != job)
            Plugin.ChatGui.PrintError(
                $"[DiademGatherer] \"{_goal.ItemName}\" is a different crafter's recipe than your " +
                "current job — switch class (or let Artisan's gearset settings handle it).");

        var need = _goal.Batch - Math.Max(have, 0);
        Plugin.ChatGui.Print($"[DiademGatherer] Crafting {need}x {_goal.ItemName} via Artisan.");
        if (!_artisan.CraftItem(recipe, need))
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] Artisan rejected the craft request. Stopping.");
            Stop();
            return;
        }

        _artisanKicks++;
        _artisanGraceUntil = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        SetState(CraftState.WaitArtisan);
    }

    private void HandleWaitArtisan()
    {
        if (TimeSinceEntered > CraftTimeout)
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] Crafting batch took too long — stopping.");
            Stop();
            return;
        }

        if (_artisan.IsBusy() || DateTime.UtcNow < _artisanGraceUntil) return;

        // Artisan idle: batch done, or it stopped early (out of materials).
        // Everything here turns on `have`, and a -1 (name didn't resolve) looks
        // exactly like "crafted nothing" — which would silently re-craft forever
        // instead of turning in. Log what we actually measured.
        var have = ItemHelper.CountInInventory(_goal!.ItemName);
        Plugin.Log.Information($"[DiademGatherer] Batch check \"{_goal.ItemName}\": "
            + $"have={have} (itemId={ItemHelper.ResolveItemId(_goal.ItemName)}) batch={_goal.Batch} "
            + $"→ {(have < 0 ? "UNRESOLVED — cannot count" : have >= _goal.Batch ? "turn in" : have > 0 ? "partial turn in" : "nothing crafted")}");

        if (have >= _goal.Batch)
        {
            SetState(CraftState.GoToPotkin);
            return;
        }

        if (have > 0)
        {
            // Partial batch (materials ran out?) — turn in what we have.
            Plugin.ChatGui.Print($"[DiademGatherer] Artisan stopped at {have}/{_goal.Batch} — turning in the partial batch.");
            SetState(CraftState.GoToPotkin);
            return;
        }

        if (have < 0)
        {
            Plugin.ChatGui.PrintError(
                $"[DiademGatherer] Can't count \"{_goal.ItemName}\" in your bags — the item name didn't "
                + "resolve, so the batch can never look complete. Re-pick the collectable in the Crafting tab. Stopping.");
            Stop();
            return;
        }

        if (_artisanKicks >= 2)
        {
            // Out of materials for this recipe — skip it and try the next goal
            // instead of stopping the whole loop (keeps the kupo/turn-in count).
            Plugin.ChatGui.PrintError(
                $"[DiademGatherer] \"{_goal.ItemName}\" crafted nothing (out of materials?) — skipping to the next goal.");
            _skippedGoals.Add(_goal.ItemName);
            SetState(CraftState.StartBatch);
            return;
        }

        _rekick = true;                  // one re-kick of the same goal, then give up
        SetState(CraftState.StartBatch);
    }

    private void HandleGoTo(string npcName, CraftState next, Action onArrive)
    {
        if (DateTime.UtcNow < _actionAt) return;

        var npc = FindNpc(npcName);
        if (npc == null)
        {
            if (TimeSinceEntered > TimeSpan.FromSeconds(30))
            {
                Plugin.ChatGui.PrintError($"[DiademGatherer] Can't find {npcName} nearby — stopping.");
                Stop();
            }
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        if (Vector3.Distance(player.Position, npc.Position) > 6f)
        {
            _nav.MoveCloseTo(npc.Position, fly: false, range: 4f);
            _actionAt = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            return;
        }

        _nav.Stop();
        GameUiHelper.InteractWith(npc.Address);
        onArrive();
        SetState(next);
        _actionAt = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
    }

    private void OnReachedPotkin()
    {
        _countAtTurnInStart = ItemHelper.CountInInventory(_goal!.ItemName);
        _turnInPrev = Math.Max(_countAtTurnInStart, 0); // baseline for incremental counting
        _turnInStep = DateTime.MinValue;
        Plugin.ChatGui.Print($"[DiademGatherer] At Potkin — handing in {_countAtTurnInStart}x {_goal.ItemName}.");
    }

    private void OnReachedLizbeth()
    {
        _kupoStep        = DateTime.MinValue;
        _kupoPlays       = 0;
        _kupoProgress    = DateTime.UtcNow;
        _kupoReinteracts = 0;
        Plugin.ChatGui.Print("[DiademGatherer] At Lizbeth — playing kupo cards.");
    }

    // Fully automated turn-in via HWDSupply:
    //   select the goal's class tab → confirm the "Item Request" (Hand Over)
    //   window if open → else hand in row 0 → repeat until the bag is empty.
    private void HandleAwaitTurnIn()
    {
        if (GameUiHelper.ClickTalk()) { _turnInStep = DateTime.UtcNow + TimeSpan.FromSeconds(0.8); return; }

        var have = ItemHelper.CountInInventory(_goal!.ItemName);

        // Incremental counting: every drop in the bag count is a completed
        // hand-in. Counting as we go (not at the end) survives the mid-turn-in
        // shopping interruption without losing progress.
        if (have >= 0 && have < _turnInPrev)
        {
            var delta = _turnInPrev - have;
            TurnInsTotal     += delta;
            TurnInsSinceKupo += delta;
            _turnInPrev = have;
        }

        if (have <= 0)
        {
            Plugin.ChatGui.Print(
                $"[DiademGatherer] Turn-in complete ({TurnInsSinceKupo}/{_config.KupoEveryTurnIns} toward kupo).");
            EnterKupoStage(); // kupo → shop → home → next batch
            return;
        }

        // Mid-turn-in scrip guard: near the 10k cap → pause here, go spend at
        // Enie, then walk back to Potkin and finish handing in.
        var scrips = GameUiHelper.SupplyScripCount();
        if (scrips >= 0 && scrips >= _config.ScripDumpAt && ShopGoals.AnyPending(_config))
        {
            Plugin.ChatGui.Print(
                $"[DiademGatherer] Scrips at {scrips:N0} mid-turn-in — pausing to spend before the cap.");
            if (GameUiHelper.IsVisible("HWDSupply")) GameUiHelper.CloseAddon("HWDSupply");
            _turnInInterrupted = true;
            GoShopping();
            return;
        }

        if (TimeSinceEntered > TurnInTimeout)
        {
            Plugin.ChatGui.PrintError("[DiademGatherer] Turn-in stalled — stopping. (Hand-in window name may differ.)");
            Stop();
            return;
        }

        if (DateTime.UtcNow < _turnInStep) return;

        // Vouchers are full: the game offers to complete the hand-in anyway, but
        // it grants NO stamps, so saying yes throws the collectable away for
        // nothing. Decline, go spend the vouchers at Lizbeth, then come back and
        // finish the batch. Previously this prompt was ignored entirely and the
        // loop just kept driving the hand-in behind it.
        if (GameUiHelper.TryGetSelectYesnoText(out var capPrompt)
            && capPrompt.Contains("kupo voucher", StringComparison.OrdinalIgnoreCase)
            && capPrompt.Contains("will not receive", StringComparison.OrdinalIgnoreCase))
        {
            Plugin.ChatGui.Print("[DiademGatherer] Kupo vouchers are full — turn-ins would earn nothing. "
                               + "Playing kupo first, then finishing the batch.");
            GameUiHelper.ClickNo();
            _resumeTurnInAfterKupo = true;
            if (GameUiHelper.IsVisible("HWDSupply")) GameUiHelper.CloseAddon("HWDSupply");
            _actionAt   = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            _turnInStep = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            SetState(CraftState.GoToLizbeth);
            return;
        }

        // Step 1: confirm an open Item Request.
        if (GameUiHelper.RequestHandInOpen())
        {
            GameUiHelper.RequestHandInConfirm();
            _turnInStep = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
            return;
        }

        // Step 2: the supply window must be open on our goal's class tab.
        if (!GameUiHelper.IsVisible("HWDSupply"))
        {
            // Re-interact with Potkin to reopen it (bag not yet empty).
            var potkin = FindNpc(PotkinName);
            if (potkin != null) GameUiHelper.InteractWith(potkin.Address);
            _turnInStep = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
            return;
        }

        // classIndex 0..7 = CRP..CUL, from jobId 8..15.
        var classIndex = (int)_goalJob - 8;
        GameUiHelper.SupplySelectClass(classIndex);

        // Read this class's accumulated score; stop turning in once it caps.
        var score = GameUiHelper.SupplyAccumulatedScore();
        if (score >= 0)
        {
            _scoreByJob[_goalJob] = score;
            if (score >= _config.MaxAccumulatedScore)
            {
                Plugin.ChatGui.Print(
                    $"[DiademGatherer] {_goal.ItemName}'s class hit the {_config.MaxAccumulatedScore:N0} " +
                    "score cap — moving on.");
                EnterKupoStage(); // incremental counting already tallied the hand-ins
                return;
            }
        }

        // Only hand in when row 0 is actually our collectable (post-tab-switch).
        var shortName = _goal.ItemName.Replace("Grade 4 Artisanal Skybuilders' ", "")
                                      .Replace("Grade 4 Skybuilders' ", "");
        if (GameUiHelper.SupplyRow0NameContains(shortName))
        {
            GameUiHelper.SupplyHandIn(0);
            _turnInStep = DateTime.UtcNow + TimeSpan.FromSeconds(1.0);
        }
        else
        {
            // Tab may still be switching — wait a beat and re-check.
            _turnInStep = DateTime.UtcNow + TimeSpan.FromSeconds(0.6);
        }
    }

    // Automated Kupo of Fortune:
    //   answer the "voucher required (N remaining)" prompt with Yes until N hits
    //   the floor, scratching a weighted-random hexagon each time the board is
    //   open with no prompt. Cell 0 is scratched at half weight (can't win top).
    private void HandleAwaitKupo()
    {
        void FinishKupo(string why)
        {
            Plugin.Log.Information($"[Kupo] done ({why}) — clearing closing dialogue.");
            if (GameUiHelper.LotteryOpen()) GameUiHelper.LotteryClose();
            TurnInsSinceKupo = 0;
            // After the last card, Lizbeth still has one closing Talk box open;
            // it must be dismissed before the player is free to craft again.
            _kupoWrapStep  = DateTime.MinValue;
            _kupoWrapUntil = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
            SetState(CraftState.KupoWrapUp);
        }

        if (TimeSinceEntered > KupoTimeout) { FinishKupo("timeout"); return; }
        if (_kupoPlays >= 60)               { FinishKupo("play cap"); return; }
        if (DateTime.UtcNow < _kupoStep) return;

        void Progress() { _kupoProgress = DateTime.UtcNow; _kupoReinteracts = 0; }

        // Board open: scratch exactly ONE cell per card, then Close to continue
        // the dialogue (a scratched card has non-zero cell values). After
        // scratching we wait KupoRevealDelay before closing so the scratch and
        // the prize are actually visible.
        if (GameUiHelper.LotteryOpen())
        {
            if (GameUiHelper.LotteryScratched())
            {
                Plugin.Log.Information("[Kupo] card scratched → Close");
                GameUiHelper.LotteryClose();
                Progress();
                _kupoStep = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
            }
            else
            {
                var cell = PickKupoCell();
                Plugin.Log.Information($"[Kupo] scratching cell {cell} → showing result for {KupoRevealDelay.TotalSeconds:0}s");
                GameUiHelper.LotteryScratch(cell);
                Progress();
                _kupoStep = DateTime.UtcNow + KupoRevealDelay;
            }
            return;
        }

        // Voucher prompt gates each play. Stop at the floor, else spend one.
        if (GameUiHelper.TryGetSelectYesnoText(out var prompt)
            && prompt.Contains("kupo voucher", StringComparison.OrdinalIgnoreCase))
        {
            var remaining = ParseFirstInt(prompt); // "...(N remaining)..."
            Plugin.Log.Information($"[Kupo] voucher prompt '{prompt}' → {remaining} left, floor {_config.KupoVoucherFloor}");
            if (remaining >= 0 && remaining <= _config.KupoVoucherFloor)
            {
                GameUiHelper.ClickNo();
                FinishKupo($"floor reached ({remaining} left)");
                return;
            }
            GameUiHelper.ClickYes();
            _kupoPlays++;
            Progress();
            _kupoStep = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
            return;
        }

        // Any other Yes/No (unexpected) — accept it to keep moving.
        if (GameUiHelper.TryGetSelectYesnoText(out var yn))
        {
            Plugin.Log.Information($"[Kupo] other SelectYesno '{yn}' → Yes");
            GameUiHelper.ClickYes();
            Progress();
            _kupoStep = DateTime.UtcNow + TimeSpan.FromSeconds(1.0);
            return;
        }

        // Lizbeth's dialogue: her intro and the "play again?" line are Talk boxes.
        if (GameUiHelper.ClickTalk())
        {
            Plugin.Log.Information("[Kupo] advanced a Talk box");
            Progress();
            _kupoStep = DateTime.UtcNow + TimeSpan.FromSeconds(0.8);
            return;
        }
        if (GameUiHelper.IsVisible("SelectString"))
        {
            Plugin.Log.Information("[Kupo] SelectString → 'Kupo'");
            if (!GameUiHelper.SelectStringEntry("Kupo")) FinishKupo("no Kupo menu entry");
            Progress();
            _kupoStep = DateTime.UtcNow + TimeSpan.FromSeconds(1.0);
            return;
        }

        // Nothing open. This is usually a brief gap between dialogue steps
        // (Talk closing → SelectYesno rendering) — WAIT, don't re-interact, or
        // we restart Lizbeth's intro and never reach the voucher prompt.
        if (DateTime.UtcNow - _kupoProgress < TimeSpan.FromSeconds(5)) return;

        // Genuinely stalled. Two failed re-interacts → no vouchers / done.
        if (_kupoReinteracts >= 2) { FinishKupo("no cards to play"); return; }
        var lizbeth = FindNpc(LizbethName);
        if (lizbeth == null) { FinishKupo("Lizbeth not found"); return; }

        Plugin.Log.Information($"[Kupo] stalled {5}s — re-interacting with Lizbeth (try {_kupoReinteracts + 1})");
        GameUiHelper.InteractWith(lizbeth.Address);
        _kupoReinteracts++;
        _kupoProgress = DateTime.UtcNow;
        _kupoStep     = DateTime.UtcNow + TimeSpan.FromSeconds(2);
    }

    // After the kupo session ends, Lizbeth leaves one closing Talk box on
    // screen. Nothing else can happen (crafting included) until it's dismissed,
    // so click through any remaining Talk/prompt until the screen settles.
    private void HandleKupoWrapUp()
    {
        if (DateTime.UtcNow < _kupoWrapStep) return;

        if (GameUiHelper.ClickTalk())
        {
            Plugin.Log.Information("[Kupo] cleared a closing Talk box");
            _kupoWrapStep  = DateTime.UtcNow + TimeSpan.FromSeconds(0.8);
            _kupoWrapUntil = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
            return;
        }
        if (GameUiHelper.TryGetSelectYesnoText(out _))
        {
            GameUiHelper.ClickNo(); // decline any stray "play again?"
            _kupoWrapStep  = DateTime.UtcNow + TimeSpan.FromSeconds(0.8);
            _kupoWrapUntil = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
            return;
        }

        // Give the closing box a moment to render; only finish once nothing has
        // shown for the settle window.
        if (DateTime.UtcNow < _kupoWrapUntil) return;

        if (_kupoOnly) { _kupoOnly = false; Stop(); return; }

        // We only came here because the turn-in was blocked by full vouchers —
        // vouchers are spent now, so finish handing the batch in.
        if (_resumeTurnInAfterKupo)
        {
            _resumeTurnInAfterKupo = false;
            Plugin.ChatGui.Print("[DiademGatherer] Vouchers spent — back to Potkin to finish the turn-in.");
            SetState(CraftState.GoToPotkin);
            return;
        }

        EnterShopStage();
    }

    // We only ever scratch ONE hexagon per card; this just picks WHICH one,
    // weighting the leftmost (cell 0) at half the others since it can't reach
    // the top prize. Fixed 4-cell board.
    // How long to linger on a scratched card before closing, so the reveal and
    // prize are visible.
    private static readonly TimeSpan KupoRevealDelay = TimeSpan.FromSeconds(5);

    // The board has 4 hexagons (cells 0..3). We never scratch cell 0 (the
    // "0,0" corner) — pick evenly among the other three.
    private const int KupoCells = 4;
    private int PickKupoCell() => 1 + Rng.Next(KupoCells - 1); // 1..3

    private static int ParseFirstInt(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"\d+");
        return m.Success && int.TryParse(m.Value, out var v) ? v : -1;
    }

    // ── Post-batch pipeline: kupo → shop → home → next batch ────────────────────

    // Kupo runs when the turn-in threshold is met (self-exits if no vouchers).
    private void EnterKupoStage()
    {
        // Close the turn-in window first — a lingering HWDSupply blocks the next
        // NPC interaction (Lizbeth) and makes it look like crafting starts too
        // early with the turn-in window still up. Settle a beat before walking.
        if (GameUiHelper.IsVisible("HWDSupply"))
        {
            GameUiHelper.CloseAddon("HWDSupply");
            _actionAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        }

        if (TurnInsSinceKupo >= _config.KupoEveryTurnIns)
            SetState(CraftState.GoToLizbeth);
        else
            EnterShopStage();
    }

    // Shop runs when scrips are near the cap and there are goals to buy.
    private void EnterShopStage()
    {
        var scrips = ItemHelper.ScripCount();
        if (scrips >= _config.ScripDumpAt && ShopGoals.AnyPending(_config))
        {
            Plugin.ChatGui.Print($"[DiademGatherer] {scrips:N0} scrips — spending at Enie before they cap.");
            GoShopping();
        }
        else
        {
            SetState(CraftState.ReturnHome);
        }
    }

    private void GoShopping()
    {
        _shop.Begin();
        SetState(CraftState.Shopping);
    }

    private void EndShopping()
    {
        // If we broke off a turn-in to shop, go back to Potkin and finish it;
        // otherwise return to the crafting spot for the next batch.
        if (_turnInInterrupted)
        {
            _turnInInterrupted = false;
            Plugin.ChatGui.Print("[DiademGatherer] Scrips spent — returning to Potkin to finish turning in.");
            SetState(CraftState.GoToPotkin);
        }
        else
        {
            SetState(CraftState.ReturnHome);
        }
    }

    // Walk back to the crafting spot, then start the next batch.
    private void HandleReturnHome()
    {
        if (_homePos == null) { SetState(CraftState.StartBatch); return; }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var dist = Vector3.Distance(player.Position, _homePos.Value);
        if (dist <= 3f || TimeSinceEntered > TimeSpan.FromSeconds(45))
        {
            _nav.Stop();
            SetState(CraftState.StartBatch);
            return;
        }

        if (!_nav.IsBusy())
            _nav.MoveCloseTo(_homePos.Value, fly: false, range: 2f);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNpc(string name)
        => GameUiHelper.FindNpc(name);

    private void SetState(CraftState s)
    {
        _state        = s;
        _stateEntered = DateTime.UtcNow;
    }

    private TimeSpan TimeSinceEntered => DateTime.UtcNow - _stateEntered;

    public void Dispose() => Stop();
}
