using System.Numerics;
using DiademGatherer.IPC;

namespace DiademGatherer;

// Shared "spend scrips at Enie" runner: walk to Enie, work her three scrip
// shops, and buy toward the Shop-tab goals until each goal's remaining count
// hits 0 or scrips run out. Used by BOTH the reinstance flow and the crafting
// loop so the buy logic lives in one place. Call Begin() once, then Tick() each
// frame until it returns true.
//
// A goal's Target is a remaining-to-buy counter: every confirmed purchase
// decrements it (persisted), so the runner never reads inventory to decide what
// to buy — stashing bought items in a retainer can't make it re-buy.
public sealed class ShopRunner
{
    private readonly Configuration _config;
    private readonly NavmeshIPC    _nav;

    private DateTime _startedAt;
    private DateTime _step;
    private DateTime _buyAt;
    private bool     _awaitingConfirm;
    private int      _current = -1;          // open shop index (-1 = none)
    private readonly HashSet<string> _skip        = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int>    _unavailable = new();

    // Current buy attempt.
    private string _buyGoal = "";
    private int    _buyQty;
    private Configuration.PurchaseGoal? _buyGoalRef; // decremented once the buy confirms

    // Gear/Furnishings is tabbed; next category tab to try when the wanted item
    // isn't in the currently-shown list (reset per item).
    private int _gearTab;

    private const float InteractRange = 6f;
    // A valid buy pops its "Exchange N scrips?" dialog within a frame or two;
    // if none shows in this window the quantity was rejected.
    private static readonly TimeSpan RejectTimeout = TimeSpan.FromSeconds(1.2);
    // Below the cheapest scrip item there's nothing left to buy — bail fast.
    private const int MinScripPrice = 500;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public ShopRunner(Configuration config, NavmeshIPC nav)
    {
        _config = config;
        _nav    = nav;
    }

    public void Begin()
    {
        _startedAt       = DateTime.UtcNow;
        _step            = DateTime.MinValue;
        _awaitingConfirm = false;
        _current         = -1;
        _buyGoal         = "";
        _buyQty          = 0;
        _buyGoalRef      = null;
        _skip.Clear();
        _unavailable.Clear();
        _gearTab = 0;
    }

    // True once shopping is finished (all goals bought out, out of scrips, or timeout).
    public bool Tick()
    {
        if (DateTime.UtcNow - _startedAt > Timeout) { CloseShop(); return true; }
        if (DateTime.UtcNow < _step) return false;

        if (GameUiHelper.TryGetSelectYesnoText(out var confirmText))
        {
            HandleConfirm(confirmText);
            return false;
        }

        if (GameUiHelper.IsVisible("ShopExchangeCurrency"))
            return TickShopOpen();

        // No shop open: pick the next shop with pending goals, else done.
        var nextShop = ShopGoals.NextShopWithPending(_config, _skip, _unavailable);
        if (nextShop < 0) { CloseShop(); return true; }

        if (GameUiHelper.IsVisible("SelectString"))
        {
            _current = nextShop;
            if (!GameUiHelper.SelectStringEntryWhere(t => ShopGoals.EntryMatches(nextShop, t)))
            {
                Plugin.ChatGui.PrintError(
                    $"[DiademGatherer] Enie's \"{Configuration.ShopNames[nextShop]}\" menu entry wasn't found — " +
                    "skipping that shop (its goals won't be bought).");
                _unavailable.Add(nextShop);
                _current = -1;
            }
            _step = DateTime.UtcNow + TimeSpan.FromSeconds(1.0);
            return false;
        }
        if (GameUiHelper.ClickTalk()) { _step = DateTime.UtcNow + TimeSpan.FromSeconds(0.8); return false; }

        // Walk to / interact with Enie to open her menu.
        var enie = GameUiHelper.FindNpc(DiademData.EnieName);
        var player = Plugin.ObjectTable.LocalPlayer;
        if (enie == null || player == null) { _step = DateTime.UtcNow + TimeSpan.FromSeconds(1); return false; }

        if (Vector3.Distance(player.Position, enie.Position) > InteractRange)
        {
            _nav.MoveCloseTo(enie.Position, fly: false, range: 4f);
            _step = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            return false;
        }

        _nav.Stop();
        GameUiHelper.InteractWith(enie.Address);
        _step = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
        return false;
    }

    private bool TickShopOpen()
    {
        // A buy is out: HandleConfirm resolves it from the confirm dialog. If no
        // dialog appears at all, the game rejected the request outright (a stack
        // qty the shop won't take) — fall back to one-at-a-time, safety only.
        if (_awaitingConfirm)
        {
            if (DateTime.UtcNow - _buyAt < RejectTimeout) return false;
            _awaitingConfirm = false;

            if (_buyQty > 1) { _buyQty = 1; return false; } // no confirm for a stack → buy singly

            if (ItemHelper.ScripCount() < MinScripPrice) { CloseShop(); return true; }
            Plugin.Log.Information($"[DiademGatherer] \"{_buyGoal}\" wouldn't buy — skipping.");
            _skip.Add(_buyGoal);
            _buyQty = 0;
            return false;
        }

        var goal = _current >= 0 ? ShopGoals.NextPending(_config, _current, _skip, _unavailable) : null;
        if (goal == null)
        {
            // This shop's goals are done — close it; the outer loop re-talks to
            // Enie if another shop still has pending goals.
            GameUiHelper.CloseAddon("ShopExchangeCurrency");
            _current = -1;
            _step = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
            return false;
        }

        var idx = GameUiHelper.FindShopItemIndex(goal.ItemName);
        if (idx < 0)
        {
            // Gear and Furniture is tabbed — the item may be on another category
            // tab (Armor = gear, Others = coffers/furnishings). Cycle the tabs
            // until it shows up; only then give up on it.
            var tabs = _current == 1 ? GameUiHelper.ShopCategoryCount() : 0;
            if (_gearTab < tabs)
            {
                GameUiHelper.ShopSelectCategory(_gearTab);
                _gearTab++;
                _step = DateTime.UtcNow + TimeSpan.FromSeconds(0.6);
                return false;
            }
            Plugin.ChatGui.PrintError(
                $"[DiademGatherer] \"{goal.ItemName}\" isn't listed in the open shop — skipping it.");
            _skip.Add(goal.ItemName);
            _gearTab = 0;
            return false;
        }
        _gearTab = 0; // found on the current tab; reset for the next item

        // Quantity, from real data so we never fire a buy the shop rejects:
        //   • one-at-a-time items (parasols, gear, …) buy 1;
        //   • bulk items buy the whole remaining count, capped by the scrips on
        //     hand (known price) and the shop's 99-per-transaction limit.
        // Keep-buying goals have no remaining count — take as many as the scrips
        // (and the 99-per-transaction cap) allow.
        var need   = goal.KeepBuying ? 99 : goal.Target;
        var price  = ShopCatalog.PriceOf(goal.ItemName);
        var scrips = ItemHelper.ScripCount();
        int qty;
        if (!ShopCatalog.IsBulk(goal.ItemName))
        {
            if (price > 0 && scrips >= 0 && scrips < price)
            {
                Plugin.ChatGui.Print(
                    $"[DiademGatherer] Can't afford \"{goal.ItemName}\" ({price:N0} needed, have {scrips:N0}) " +
                    "— skipping it this trip.");
                _skip.Add(goal.ItemName);
                return false;
            }
            qty = 1;
        }
        else if (price > 0 && scrips >= 0)
        {
            var affordable = scrips / price;
            if (affordable < 1)
            {
                Plugin.ChatGui.Print(
                    $"[DiademGatherer] Can't afford \"{goal.ItemName}\" ({price:N0} each, have {scrips:N0}) " +
                    "— skipping it this trip.");
                _skip.Add(goal.ItemName);
                return false;
            }
            qty = Math.Clamp(Math.Min(need, affordable), 1, 99);
        }
        else
            qty = Math.Clamp(need, 1, 99);                // unknown price → confirm-dialog path sorts it out

        _buyGoal    = goal.ItemName;
        _buyGoalRef = goal;
        _buyQty     = qty;
        GameUiHelper.ShopBuy(idx, qty);
        _awaitingConfirm = true;
        _buyAt = DateTime.UtcNow;
        _step  = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
        return false;
    }

    // Handle a Yes/No during shopping, driven by its TEXT. Two prompts can occur
    // per purchase: the "Exchange N scrips?" buy confirm (Yes), and — for a
    // registerable item already claimed — a "you have already acquired the
    // action… Proceed?" warning (OK). Whichever comes first for our fired buy is
    // the one that commits the count; a second prompt for the same buy doesn't.
    private void HandleConfirm(string text)
    {
        var ours = _awaitingConfirm;   // first confirm for the buy we just fired
        _awaitingConfirm = false;

        // Registerable item already claimed: OK it (the buy still gives the item).
        if (text.Contains("already acquired", StringComparison.OrdinalIgnoreCase)
            || text.Contains("already obtained", StringComparison.OrdinalIgnoreCase))
        {
            GameUiHelper.ClickYes(); // "OK"
            if (ours) CommitBuy();
            _step = DateTime.UtcNow + TimeSpan.FromSeconds(0.8);
            return;
        }

        // "Exchange N skybuilders' scrips…" — the number is the total for the
        // quantity we asked to buy. With known prices we only ever fire an
        // affordable quantity, so this is normally a straight Yes.
        if (ours && _buyQty >= 1 && TryParsePrice(text, out var total))
        {
            var scrips = ItemHelper.ScripCount();
            if (total <= scrips)
            {
                GameUiHelper.ClickYes();
                CommitBuy();
                _step = DateTime.UtcNow + TimeSpan.FromSeconds(1.0);
                return;
            }

            // Safety (only reachable for an unknown-price item): can't afford
            // this many — decline and drop to what the scrips cover.
            var unit       = Math.Max(1, total / _buyQty);
            var affordable = scrips / unit;
            GameUiHelper.ClickNo();
            if (affordable >= 1)
            {
                _buyQty = affordable; // TickShopOpen re-fires at this ceiling
            }
            else
            {
                Plugin.Log.Information($"[DiademGatherer] Can't afford one \"{_buyGoal}\" ({unit}, have {scrips}) — skipping.");
                _skip.Add(_buyGoal);
                _buyQty = 0;
            }
            _step = DateTime.UtcNow + TimeSpan.FromSeconds(0.5);
            return;
        }

        // Anything else — accept, and commit if it was our buy so we can't loop.
        GameUiHelper.ClickYes();
        if (ours) CommitBuy();
        _step = DateTime.UtcNow + TimeSpan.FromSeconds(0.8);
    }

    // Count a confirmed purchase: subtract the bought quantity from the goal's
    // remaining-to-buy Target and persist it. Clearing the ref prevents a second
    // prompt for the same buy from decrementing twice.
    private void CommitBuy()
    {
        if (_buyGoalRef != null)
        {
            if (_buyGoalRef.KeepBuying)
            {
                Plugin.ChatGui.Print($"[DiademGatherer] Bought {_buyQty}x {_buyGoal} (keep buying).");
            }
            else
            {
                _buyGoalRef.Target = Math.Max(0, _buyGoalRef.Target - _buyQty);
                _config.Save();
                Plugin.ChatGui.Print(
                    $"[DiademGatherer] Bought {_buyQty}x {_buyGoal} — {_buyGoalRef.Target} left to buy.");
            }
            _buyGoalRef = null;
        }
    }

    // First number in the prompt ("Exchange 9,000 scrips…") as an int; the buy
    // total. false if there's no number to read.
    private static bool TryParsePrice(string text, out int price)
    {
        price = 0;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"[\d,]+");
        return m.Success && int.TryParse(m.Value.Replace(",", ""), out price) && price > 0;
    }

    private static void CloseShop()
    {
        if (GameUiHelper.IsVisible("ShopExchangeCurrency"))
            GameUiHelper.CloseAddon("ShopExchangeCurrency");
    }
}
