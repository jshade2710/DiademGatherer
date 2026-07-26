using FFXIVClientStructs.FFXIV.Client.Game;
using LuminaItem   = Lumina.Excel.Sheets.Item;
using LuminaRecipe = Lumina.Excel.Sheets.Recipe;

namespace DiademGatherer;

// Item-name → id resolution (Lumina Item sheet, cached) and inventory counts.
public static class ItemHelper
{
    // (recipeId, crafter jobId 8..15) resolved by result-item name; (0,0) if unknown.
    private static readonly Dictionary<string, (ushort Recipe, uint Job)> RecipeCache
        = new(StringComparer.OrdinalIgnoreCase);

    // The eight "Grade 4 Artisanal Skybuilders'" collectables (one per crafter),
    // discovered from the Item sheet and ordered by crafter job (CRP..CUL).
    // Cached after first build.
    private static List<(string Name, uint Job)>? _artisanalCache;

    public static IReadOnlyList<(string Name, uint Job)> ArtisanalCollectables()
    {
        if (_artisanalCache != null) return _artisanalCache;

        const string prefix = "Grade 4 Artisanal Skybuilders'";
        var list = new List<(string Name, uint Job)>();
        foreach (var item in Plugin.DataManager.GetExcelSheet<LuminaItem>())
        {
            var name = item.Name.ExtractText();
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var (_, job) = ResolveRecipe(name);
            if (job != 0) list.Add((name, job));
        }

        _artisanalCache = list.OrderBy(x => x.Job).ToList();
        return _artisanalCache;
    }

    public static (ushort RecipeId, uint JobId) ResolveRecipe(string resultItemName)
    {
        if (RecipeCache.TryGetValue(resultItemName, out var cached)) return cached;

        (ushort, uint) found = (0, 0);
        foreach (var row in Plugin.DataManager.GetExcelSheet<LuminaRecipe>())
        {
            var result = row.ItemResult.ValueNullable;
            if (result == null) continue;
            if (!string.Equals(result.Value.Name.ExtractText(), resultItemName,
                               StringComparison.OrdinalIgnoreCase)) continue;

            found = ((ushort)row.RowId, 8 + row.CraftType.RowId); // CraftType 0..7 → jobs 8..15
            break;
        }

        if (found.Item1 == 0)
            Plugin.Log.Warning($"[DiademGatherer] No recipe found for \"{resultItemName}\".");

        RecipeCache[resultItemName] = found;
        return found;
    }

    private static readonly Dictionary<string, uint> IdCache = new(StringComparer.OrdinalIgnoreCase);

    public static uint ResolveItemId(string name)
    {
        if (IdCache.TryGetValue(name, out var cached)) return cached;

        uint found = 0;
        foreach (var row in Plugin.DataManager.GetExcelSheet<LuminaItem>())
        {
            if (string.Equals(row.Name.ExtractText(), name, StringComparison.OrdinalIgnoreCase))
            {
                found = row.RowId;
                break;
            }
        }

        if (found == 0)
            Plugin.Log.Warning($"[DiademGatherer] Could not resolve item \"{name}\" from the Item sheet.");

        IdCache[name] = found;
        return found;
    }

    private static readonly InventoryType[] PlayerBags =
    {
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    };

    // Skybuilders' Scrips are a CURRENCY (not in the bags), so the bag-scan
    // CountInInventory returns 0 for them — read the count via the currency
    // container instead. -1 if unknown.
    public static unsafe int ScripCount()
    {
        var id = ResolveItemId("Skybuilders' Scrip");
        if (id == 0) return -1;
        var im = InventoryManager.Instance();
        return im == null ? -1 : im->GetInventoryItemCount(id);
    }

    // Condition of the most worn EQUIPPED item, as a percentage (-1 if unknown).
    // The game stores condition as 0..30000 (GBR reads it the same way, /300).
    public static unsafe int LowestEquippedCondition() => LowestEquippedCondition(out _);

    public static unsafe int LowestEquippedCondition(out uint worstItemId)
    {
        worstItemId = 0;
        var im = InventoryManager.Instance();
        if (im == null) return -1;

        var gear = im->GetInventoryContainer(InventoryType.EquippedItems);
        if (gear == null || !gear->IsLoaded) return -1;

        var worst = -1;
        for (var i = 0; i < gear->Size; i++)
        {
            var slot = gear->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0) continue;
            var pct = slot->Condition / 300; // 30000 → 100%
            if (worst < 0 || pct < worst) { worst = pct; worstItemId = slot->ItemId; }
        }
        return worst;
    }

    // Do we hold dark matter good enough to repair this item? Mirrors GBR's
    // HasDarkMatter: walk the ItemRepairResource sheet and accept any grade at or
    // above the one the item calls for. Without this the repair window opens and
    // then does nothing, which just burns the timeout.
    public static unsafe bool HasRepairMaterial(uint itemId)
    {
        if (itemId == 0) return false;
        var im = InventoryManager.Instance();
        if (im == null) return false;

        try
        {
            var item   = Plugin.DataManager.GetExcelSheet<LuminaItem>().GetRow(itemId);
            var needed = item.ItemRepair.Value.Item.RowId;
            foreach (var res in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ItemRepairResource>())
            {
                if (res.Item.RowId < needed) continue;
                if (im->GetInventoryItemCount(res.Item.RowId) > 0) return true;
            }
        }
        catch { /* sheet shape changed — let the repair attempt decide */ return true; }

        return false;
    }

    // How many of this item the player currently holds (-1 if the name is
    // unknown). Iterates the bag slots directly — GetInventoryItemCount does
    // NOT count COLLECTABLE items (the whole point of the crafting loop), which
    // is why crafted collectables read as 0.
    public static unsafe int CountInInventory(string name)
    {
        var id = ResolveItemId(name);
        if (id == 0) return -1;

        var im = InventoryManager.Instance();
        if (im == null) return -1;

        var total = 0;
        foreach (var bag in PlayerBags)
        {
            var container = im->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == id)
                    total += slot->Quantity;
            }
        }

        // Cross-check with the game's own counter. It doesn't see collectables
        // (why we scan the bags above), but it does count normal items the bag
        // scan can miss — take whichever is larger.
        var builtin = im->GetInventoryItemCount(id);
        return Math.Max(total, builtin);
    }
}
