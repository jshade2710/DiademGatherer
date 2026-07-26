using System.Numerics;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace DiademGatherer;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    // Category currently selected in the UI (filters which routes are shown).
    public GatheringMode Mode { get; set; } = GatheringMode.Mining;

    // Name of the route the user has selected to run / record.
    public string SelectedRouteName { get; set; } = "Mining Route 1";

    // Recorded world-space positions:  routeName  →  ( label → position ).
    public Dictionary<string, Dictionary<string, SavedVector3>> RecordedNodes { get; set; } = new();

    // Native gathering: a recorded waypoint is only one of the spots a node can
    // spawn, so at each waypoint we look for a real gathering node within this
    // many yalms of the recorded position. None found → the node isn't up here
    // this cycle, skip it.
    public int NodeSearchRadius { get; set; } = 30;

    // Learned approach spots, keyed by the node's own (rounded) world position:
    // wherever we were standing when a node actually opened is, by definition, a
    // spot that works. Reusing them beats pathing at the node itself, whose raw
    // position is often not a walkable point (nodes sunk in pits or terrain).
    // This is our equivalent of GBR's node-offset table, built from real runs.
    //
    // We keep SEVERAL spots per node and pick one at random each visit — the same
    // thing GBR's AutoOffsets.TryGetRandomOffset does. Standing on the identical
    // pixel every lap is what makes a route look automated.
    public Dictionary<string, List<SavedVector3>> LearnedNodeSpotSets { get; set; } = new();

    public static string NodeSpotKey(System.Numerics.Vector3 nodePos)
        => $"{nodePos.X:F0}:{nodePos.Y:F0}:{nodePos.Z:F0}";

    // Anti-cap: on normal nodes, cast Bountiful Yield II / Bountiful Harvest II
    // only when current GP is at or above this, banking the rest for burst nodes.
    public int BountifulMinGp { get; set; } = 550;

    // After each gather, fire the Aetheromatic Auger at priority monsters
    // (botany drops, Icetraps first — see DiademData.AugerPriority).
    public bool UseAuger { get; set; } = true;

    // ── Scrip shopping at Enie during reinstance ──────────────────────────────
    // Target is a remaining-to-buy counter: set how many to buy, and the shop
    // runner decrements it toward 0 as it purchases (persisted each buy). This
    // is inventory-independent, so stashing what you bought in a retainer never
    // makes it re-buy.
    [Serializable]
    public class PurchaseGoal
    {
        public string ItemName { get; set; } = "";
        public int    Target   { get; set; } = 0; // how many left to buy (counts down)
        public bool   Enabled  { get; set; } = true;

        // Buy this forever instead of counting down — a scrip sink so the
        // balance always has somewhere to go and never caps. Target is ignored.
        public bool KeepBuying { get; set; } = false;

        // Which of Enie's shops carries this item (set automatically from the
        // item dropdown): 0 = Scrips, 1 = Gear and Furniture, 2 = Materials.
        public int Shop { get; set; } = 2;
    }

    public static readonly string[] ShopNames = { "Scrips", "Gear and Furniture", "Materials" };

    public bool EnableShopping { get; set; } = false;
    public List<PurchaseGoal> PurchaseGoals { get; set; } = new();

    // During each reinstance, certify Diadem loot at Flotpassant first
    // (Auto-submit + Request Inspection on the miner and botany tabs).
    public bool EnableCertification { get; set; } = true;

    // Repair with dark matter on the way back in, once the most worn equipped
    // piece falls below RepairThreshold percent. Gear loses its stat bonus at 0%,
    // so this is about never gathering with degraded stats.
    public bool EnableRepair    { get; set; } = true;
    public int  RepairThreshold { get; set; } = 50;

    // ── Crafting loop (Artisan → Potkin turn-ins → Lizbeth kupo) ──────────────
    [Serializable]
    public class CraftGoal
    {
        public string ItemName { get; set; } = ""; // e.g. "Grade 4 Artisanal Skybuilders' Icebox"
        public int    Batch    { get; set; } = 10; // craft this many, then turn in
        public bool   Enabled  { get; set; } = true;
    }

    public List<CraftGoal> CraftGoals { get; set; } = new();

    // Visit Lizbeth (Kupo of Fortune) after this many turn-ins.
    public int KupoEveryTurnIns { get; set; } = 10;

    // Keep playing kupo cards until this many vouchers remain (0 = spend all).
    public int KupoVoucherFloor { get; set; } = 0;

    // When scrips reach this, run the Shop-tab purchase goals at Enie before
    // continuing to craft (cap is 10,000 — don't waste overflow).
    public int ScripDumpAt { get; set; } = 9000;

    // Stop crafting/turning in for a class once its accumulated score reaches
    // this (the per-class Firmament cap is 500,000).
    public int MaxAccumulatedScore { get; set; } = 500_000;

    // Automatically leave and re-enter the Diadem (via Aurvael) after
    // ReinstanceMinutes, then restart the route from the top.
    public bool AutoReinstance { get; set; } = true;
    public int  ReinstanceMinutes { get; set; } = 150; // 2.5 h (instance cap is 3 h)

    // Repeat the Cycle indefinitely. If false, the route runs LeadIn + one Cycle.
    public bool LoopRoute { get; set; } = true;

    // ── Recorded-node helpers ───────────────────────────────────────────────────

    public bool TryGetNode(string routeName, string label, out Vector3 pos)
    {
        pos = Vector3.Zero;
        if (RecordedNodes.TryGetValue(routeName, out var nodes)
            && nodes.TryGetValue(label, out var saved))
        {
            pos = saved.ToVector3();
            return true;
        }
        return false;
    }

    public void SetNode(string routeName, string label, Vector3 pos)
    {
        if (!RecordedNodes.TryGetValue(routeName, out var nodes))
        {
            nodes = new Dictionary<string, SavedVector3>();
            RecordedNodes[routeName] = nodes;
        }
        nodes[label] = new SavedVector3(pos);
    }

    public bool IsRecorded(string routeName, string label)
        => RecordedNodes.TryGetValue(routeName, out var nodes) && nodes.ContainsKey(label);

    // ── Persistence ─────────────────────────────────────────────────────────────

    [NonSerialized]
    private IDalamudPluginInterface? _pi;

    public void Initialize(IDalamudPluginInterface pi) => _pi = pi;
    public void Save() => _pi!.SavePluginConfig(this);
}
