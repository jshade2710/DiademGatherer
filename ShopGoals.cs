namespace DiademGatherer;

// Pure selection logic for the Enie scrip shops, shared by the reinstance flow
// and the crafting loop. Stateless — the caller owns the skip/unavailable sets.
public static class ShopGoals
{
    // Enie's three scrip shops, matched by their SelectString entry text (as
    // seen live):
    //   "Skybuilders' Scrips"                            → 0 Scrips
    //   "Skybuilders' Scrips (Gear/Furnishings)"         → 1 Gear and Furniture
    //   "Skybuilders' Scrips (Materials/Materia/Items)"  → 2 Materials
    // (The menu also has "Fête Tokens"/"Cancel", which match nothing.)
    public static bool EntryMatches(int shop, string text) => shop switch
    {
        1 => text.Contains("Gear", StringComparison.OrdinalIgnoreCase)
             || text.Contains("Furnishing", StringComparison.OrdinalIgnoreCase),
        2 => text.Contains("Materia", StringComparison.OrdinalIgnoreCase),
        // Plain Scrips shop: the entry with no parenthetical. Matched on "Scrip"
        // (no apostrophe) — the game's apostrophe may not be the ASCII one.
        0 => text.Contains("Scrip", StringComparison.OrdinalIgnoreCase)
             && !text.Contains('('),
        _ => false,
    };

    // First goal (optionally for one shop) that still has some left to buy.
    // Target is a remaining-to-buy counter that the runner decrements as it
    // buys, so "pending" is simply Target > 0 — no inventory lookup, which is
    // what lets stashing items in a retainer not trigger re-buying.
    public static Configuration.PurchaseGoal? NextPending(
        Configuration cfg, int? shop,
        HashSet<string> skip, HashSet<int> unavailable)
    {
        // Pass 1 — real goals with a count still outstanding.
        foreach (var g in cfg.PurchaseGoals)
            if (IsCandidate(g, shop, skip, unavailable) && !g.KeepBuying && g.Target > 0)
                return g;

        // Pass 2 — keep-buying sinks are a LAST resort: they only soak up what's
        // left once every finite goal (in ANY shop) is met, so they can never
        // spend scrips that a real goal still needs.
        if (AnyFiniteOutstanding(cfg, skip, unavailable)) return null;

        foreach (var g in cfg.PurchaseGoals)
            if (IsCandidate(g, shop, skip, unavailable) && g.KeepBuying)
                return g;

        return null;
    }

    private static bool IsCandidate(
        Configuration.PurchaseGoal g, int? shop,
        HashSet<string> skip, HashSet<int> unavailable)
    {
        if (!g.Enabled || string.IsNullOrWhiteSpace(g.ItemName)) return false;
        if (shop != null && g.Shop != shop.Value) return false;
        return !skip.Contains(g.ItemName) && !unavailable.Contains(g.Shop);
    }

    // Any finite goal anywhere still needing items? Checked across all shops so
    // a sink in one shop can't jump ahead of a real goal in another.
    private static bool AnyFiniteOutstanding(
        Configuration cfg, HashSet<string> skip, HashSet<int> unavailable)
    {
        foreach (var g in cfg.PurchaseGoals)
            if (IsCandidate(g, null, skip, unavailable) && !g.KeepBuying && g.Target > 0)
                return true;
        return false;
    }

    // Lowest-numbered shop that still has pending goals; -1 if none.
    public static int NextShopWithPending(
        Configuration cfg, HashSet<string> skip, HashSet<int> unavailable)
    {
        for (var s = 0; s <= 2; s++)
            if (!unavailable.Contains(s) && NextPending(cfg, s, skip, unavailable) != null)
                return s;
        return -1;
    }

    // Gate only: is there anything left to buy?
    public static bool AnyPending(Configuration cfg)
        => cfg.EnableShopping
        && NextPending(cfg, null, new HashSet<string>(), new HashSet<int>()) != null;
}
