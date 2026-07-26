namespace DiademGatherer;

// Enie's three scrip shops, their items, and each item's scrip price — all
// captured from the live shop UI. Shop indices match Configuration.ShopNames:
//   0 Scrips, 1 Gear and Furniture, 2 Materials.
//
// NOTE: the "Gear and Furniture" shop is tabbed (Weapons/Armor/Accessories/
// Others). Only the active tab's items are exposed to the buy callback, so
// auto-buying from it needs a tab switch first (Armor = gear, Others = the
// coffers/furniture below). The Scrips and Materials shops are flat.
public static class ShopCatalog
{
    public readonly record struct Entry(int Shop, string Name, int Price);

    public static readonly Entry[] Items =
    {
        // ── 0: Scrips ────────────────────────────────────────────────────────
        new(0, "Albino Karakul Horn",   8400),
        new(0, "Ufiti Horn",            8400),
        new(0, "Megalotragus Horn",     8400),
        new(0, "Big Shell Whistle",     8400),
        new(0, "Antelope Doe Horn",     8400),
        new(0, "Pegasus Whistle",       4200),
        new(0, "Ballroom Etiquette - The Winsome Wallflower",   1800),
        new(0, "Ballroom Etiquette - Intelligent Impressions",  1800),
        new(0, "Ballroom Etiquette - Emphatic Elucidation",     1800),
        new(0, "Ballroom Etiquette - Uncouth Congratulations",  1800),
        new(0, "Ballroom Etiquette - Concealing Meals",         1800),
        new(0, "Ballroom Etiquette - Next, Godliness",          1800),
        new(0, "Ballroom Etiquette - Well Bread",                900),
        new(0, "Modern Aesthetics - Modern Legend",             1800),
        new(0, "Modern Aesthetics - Controlled Chaos",          1800),
        new(0, "Modern Aesthetics - Saintly Style",             1800),
        new(0, "Dress-up Estinien",      1200),
        new(0, "Miniature White Knight", 1200),
        new(0, "Cerberpup",              1200),
        new(0, "Paissa Brat",             800),
        new(0, "Hunting Hawk",            800),
        new(0, "Baby Brachiosaur",        800),
        new(0, "Pegasus Colt",            800),
        new(0, "Machinist Barding",      1200),
        new(0, "Safety in Numbers Orchestrion Roll",       1200),
        new(0, "The Mendicant's Relish Orchestrion Roll",  1200),
        new(0, "The Heavens' Ward Orchestrion Roll",       1200),
        new(0, "Hearthward Orchestrion Roll",              1200),
        new(0, "What Is Love? Orchestrion Roll",           1200),
        new(0, "Skyrise Orchestrion Roll",                 1200),
        new(0, "Unworthy Orchestrion Roll",                1200),
        new(0, "Jewel Orchestrion Roll",                    600),
        new(0, "Paradise Found Orchestrion Roll",           600),
        new(0, "Fealty Orchestrion Roll",                   600),
        new(0, "Stone and Steel Orchestrion Roll",          600),
        new(0, "Order Yet Undeciphered Orchestrion Roll",   600),
        new(0, "Freefall Orchestrion Roll",                 600),
        new(0, "Parasol",                1800),
        new(0, "Cheerful Checkered Parasol", 1800),
        new(0, "Pastoral Dot Parasol",   1800),
        new(0, "Sky Blue Parasol",        900),
        new(0, "Calming Checkered Parasol", 900),
        new(0, "Lizbeth Card",            500),
        new(0, "Ehll Tou Card",           500),

        // ── 1: Gear and Furniture — Armor tab (gear) ─────────────────────────
        new(1, "Craftsman's Coverall Top",     2200),
        new(1, "Craftsman's Singlet",          2200),
        new(1, "Craftsman's Apron",            2200),
        new(1, "Craftsman's Coverall Bottoms", 2000),
        new(1, "Craftsman's Leather Trousers", 2000),
        new(1, "Craftsman's Leather Shoes",    1200),
        new(1, "Skyworker's Helmet",           1200),
        new(1, "Skyworker's Singlet",          2200),
        new(1, "Skyworker's Gloves",           1200),
        new(1, "Skyworker's Bottoms",          2000),
        new(1, "Skyworker's Boots",            1200),
        // ── 1: Gear and Furniture — Others tab (coffers/furnishings) ─────────
        new(1, "Millfiend's Costume Coffer",   3000),
        new(1, "Forgefiend's Costume Coffer",  3000),
        new(1, "Hammerfiend's Costume Coffer", 3000),
        new(1, "Gemfiend's Costume Coffer",    3000),
        new(1, "Hidefiend's Costume Coffer",   3000),
        new(1, "Boltfiend's Costume Coffer",   3000),
        new(1, "Cauldronfiend's Costume Coffer", 3000),
        new(1, "Galleyfiend's Costume Coffer", 3000),
        new(1, "Minefiend's Costume Coffer",   3000),
        new(1, "Fieldfiend's Costume Coffer",  3000),
        new(1, "Tacklefiend's Costume Coffer", 3000),
        new(1, "Stuffed Hraesvelgr",            600),
        new(1, "Stuffed Estinien",              600),
        new(1, "Stuffed Cait Sith",             600),
        new(1, "Huggable Gaelicat",             250),
        new(1, "Lord Commander Portrait",       900),
        new(1, "Azure Dragoon Portrait",        900),
        new(1, "Garment Rack",                  350),
        new(1, "Fortemps Sofa",                 350),
        new(1, "Ishgardian Display Stand",      350),
        new(1, "Imposing Ishgardian Shelf",     900),
        new(1, "Ishgardian Stove",              350),
        new(1, "Apron Rack",                    350),
        new(1, "Modern Mogseat",                200),
        new(1, "Moogle Round Table",            250),
        new(1, "Mandragora Table Chronometer",  200),
        new(1, "Paissa Rug",                    200),

        // ── 2: Materials ─────────────────────────────────────────────────────
        new(2, "Skysteel Ingot",   200),
        new(2, "Skysteel Cloth",   200),
        new(2, "Skysteel Leather", 200),
        new(2, "Brass Sky Pirate Spoil", 40),
        new(2, "Steel Sky Pirate Spoil", 40),
        new(2, "Gatherer's Guerdon Materia VII",   240),
        new(2, "Gatherer's Guerdon Materia VIII",  300),
        new(2, "Gatherer's Guile Materia VII",     240),
        new(2, "Gatherer's Guile Materia VIII",    300),
        new(2, "Gatherer's Grasp Materia VII",     240),
        new(2, "Gatherer's Grasp Materia VIII",    300),
        new(2, "Craftsman's Competence Materia VII",  240),
        new(2, "Craftsman's Competence Materia VIII", 300),
        new(2, "Craftsman's Cunning Materia VII",     240),
        new(2, "Craftsman's Cunning Materia VIII",    300),
        new(2, "Craftsman's Command Materia VII",     240),
        new(2, "Craftsman's Command Materia VIII",    300),
        new(2, "Wide Spectrum #1 Dye",         100),
        new(2, "Firmament Aetheryte Ticket",   500),
        new(2, "Oddly Specific Cedar Log",       20),
        new(2, "Oddly Specific Coerthan Iron Ore", 20),
        new(2, "Oddly Specific Mythrite Sand",   20),
        new(2, "Oddly Specific Silver Ore",      20),
        new(2, "Oddly Specific Gagana Skin",     20),
        new(2, "Oddly Specific Fleece",          20),
        new(2, "Oddly Specific Sap",             20),
        new(2, "Oddly Specific Aloe",            20),
        new(2, "Oddly Delicate Pine Log",        30),
        new(2, "Oddly Delicate Silvergrace Ore", 30),
        new(2, "Oddly Delicate Scheelite",       30),
        new(2, "Oddly Delicate Raw Celestine",   30),
        new(2, "Oddly Delicate Gazelle Hide",    30),
        new(2, "Oddly Delicate Rhea",            30),
        new(2, "Oddly Delicate Mistletoe",       30),
        new(2, "Oddly Delicate Hammerhead Shark", 30),
    };

    public static IEnumerable<Entry> ForShop(int shop)
    {
        foreach (var e in Items)
            if (e.Shop == shop) yield return e;
    }

    // Scrip price for an item, 0 if unknown/not recorded.
    public static int PriceOf(string name)
    {
        foreach (var e in Items)
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e.Price;
        return 0;
    }

    // The shop an item belongs to, or -1 if it isn't catalogued.
    public static int ShopOf(string name)
    {
        foreach (var e in Items)
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e.Shop;
        return -1;
    }

    // Whether the shop lets this item be bought in bulk (has a quantity
    // stepper), from what the shop actually shows:
    //   • Materials — everything is stackable/bulk;
    //   • Scrips — only the emotes, hairstyles, and orchestrion rolls;
    //   • Gear and Furniture — one at a time.
    // Unknown items default to bulk (a rejected stack falls back cleanly, while
    // needlessly buying one-at-a-time does not).
    public static bool IsBulk(string name)
    {
        var shop = ShopOf(name);
        return shop switch
        {
            2 => true,
            0 => name.Contains("Ballroom Etiquette", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("Modern Aesthetics", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("Orchestrion Roll", StringComparison.OrdinalIgnoreCase),
            1 => false,
            _ => true, // uncatalogued
        };
    }
}
