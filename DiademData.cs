using System.Numerics;

namespace DiademGatherer;

// Serializable Vector3 (System.Numerics.Vector3 uses fields; this keeps config
// JSON clean and explicit). Still used for user-recorded routes.
public class SavedVector3
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public SavedVector3() { }
    public SavedVector3(Vector3 v) { X = v.X; Y = v.Y; Z = v.Z; }

    public Vector3 ToVector3() => new(X, Y, Z);

    public override string ToString() => $"{X:F1}, {Y:F1}, {Z:F1}";
}

// A route is a sequence of node LABELS. Positions come from BuiltInNodes when
// the route ships with them (locked, not user-editable), otherwise from
// positions recorded in-game and stored in Configuration.
//
// Play model: LeadIn plays ONCE, then Cycle repeats.
public record RouteDefinition(
    string        Name,
    GatheringMode Category,
    IReadOnlyList<string> RequiredNodes,   // every distinct label the route uses
    IReadOnlyList<string> LeadIn,          // played once
    IReadOnlyList<string> Cycle,           // repeated
    IReadOnlyList<string> BurstNodes,      // nodes that get the full GP skill priority
    IReadOnlyList<string> DetourNodes,     // after these nodes, detour to DetourMonster
    string?       DetourMonster,           // monster worth flying off-route for
    IReadOnlyList<uint>   WantedItemIds,   // the only item slots we gather (from the GBR list)
    IReadOnlyDictionary<string, Vector3>? BuiltInNodes = null // locked positions
)
{
    public bool HasBuiltInNodes => BuiltInNodes != null;

    public bool IsReady(Configuration config)
        => BuiltInNodes != null
            ? RequiredNodes.All(BuiltInNodes.ContainsKey)
            : RequiredNodes.All(l => config.IsRecorded(Name, l));

    public bool TryGetNode(Configuration config, string label, out Vector3 pos)
    {
        if (BuiltInNodes != null && BuiltInNodes.TryGetValue(label, out pos))
            return true;
        pos = default;
        return BuiltInNodes == null && config.TryGetNode(Name, label, out pos);
    }
}

public static class DiademData
{
    public const uint DiademTerritoryId    = 939;
    public const uint FirmamentTerritoryId = 886;

    // Firmament plaza NPCs (found by ObjectTable name scan; no coords needed):
    public const string AurvaelName     = "Aurvael";     // Mission Commander — enters the Diadem
    public const string FlotpassantName = "Flotpassant"; // appraiser — certifies loot
    public const string EnieName        = "Enie";        // Scrip Exchange, east plaza

    // Aetheromatic Auger duty action (id from GBR's DiademAether implementation).
    public const uint  AugerActionId        = 19700;
    public const float AugerScanRange       = 25f;  // in-place / in-flight firing range
    public const float AugerDetourRange     = 160f; // how far off-route a detour monster may be
    public const float DetourApproachRange  = 18f;  // fly to within this of the detour target
    public const int   AugerGaugeReady      = 200;  // fire only at/above this charge

    // Monsters worth an auger charge, in priority order (botany drops
    // preferred, Icetraps first). Matched by name against live ObjectTable
    // spawns. Anything not listed is never shot.
    public static readonly string[] AugerPriority =
    {
        "Diadem Icetrap",
        "Diadem Melia",
        "Diadem Werewood",
        "Diadem Bloated Bulb",
        "Diadem Proto-noctilucale",
    };

    private static string[] Labels(char prefix, int count)
        => Enumerable.Range(1, count).Select(i => $"{prefix}{i}").ToArray();

    private static Dictionary<string, Vector3> Nodes(params (string L, float X, float Y, float Z)[] pts)
        => pts.ToDictionary(p => p.L, p => new Vector3(p.X, p.Y, p.Z));

    // ── Mining Route 1 ──────────────────────────────────────────────────────────
    // Red (R) + Blue (B) spawn chains. First loop skips B8 (not spawned yet);
    // later loops slot it after R1. R8/B8 are the GP-dump burst nodes; after R6
    // we detour to an Icetrap if one is up and the auger is charged.
    public static readonly RouteDefinition MiningRoute1 = new(
        Name:          "Mining Route 1",
        Category:      GatheringMode.Mining,
        RequiredNodes: Labels('R', 8).Concat(Labels('B', 8)).ToArray(),
        LeadIn:        new[]
        {
            "R1", "R2", "B1", "B2", "B3", "R3", "R4", "R5",
            "B4", "R6", "B5", "B6", "B7", "R7", "R8",
        },
        Cycle:         new[]
        {
            "R1", "B8", "R2", "B1", "B2", "B3", "R3", "R4",
            "R5", "B4", "R6", "B5", "B6", "B7", "R7", "R8",
        },
        BurstNodes:    new[] { "R8", "B8" },
        DetourNodes:   new[] { "R6" },
        DetourMonster: "Diadem Icetrap",
        // From the GBR "Diadem Mining" list — the only slots we gather.
        WantedItemIds: new uint[] { 32042, 32041, 32044, 32043, 32040 },
        BuiltInNodes:  Nodes(
            ("R1", -161.22f,  -3.61f, -385.46f),
            ("R2", -163.48f,  -6.88f, -521.60f),
            ("B1", -160.01f, -15.88f, -570.54f),
            ("B2", -127.46f, -19.08f, -641.31f),
            ("B3",  -59.55f, -19.08f, -649.13f),
            ("R3",  -74.03f, -19.08f, -601.82f),
            ("R4",  -48.77f, -47.61f, -520.69f),
            ("R5",  -18.24f, -27.26f, -541.73f),
            ("B4",    8.58f, -20.54f, -605.64f),
            ("R6", -347.56f,  -3.13f, -322.32f),
            ("B5", -360.31f,  -6.24f, -367.64f),
            ("B6", -333.13f,  -4.50f, -443.10f),
            ("B7", -277.16f,  -2.51f, -411.89f),
            ("R7", -266.75f,  -3.04f, -345.53f),
            ("R8", -211.00f,  -3.84f, -356.80f),
            ("B8", -226.38f,  -3.89f, -498.79f))
    );

    // ── Botany Route 1 ──────────────────────────────────────────────────────────
    // Green (G) + Pink (P) spawn chains, same structure. P8/G8 burst.
    public static readonly RouteDefinition BotanyRoute1 = new(
        Name:          "Botany Route 1",
        Category:      GatheringMode.Botany,
        RequiredNodes: Labels('G', 8).Concat(Labels('P', 8)).ToArray(),
        LeadIn:        new[]
        {
            "P1", "G1", "G2", "G3", "P2", "P3", "G4", "P4",
            "G5", "G6", "P5", "P6", "P7", "G7", "P8",
        },
        Cycle:         new[]
        {
            "P1", "G8", "G1", "G2", "G3", "P2", "P3", "G4",
            "P4", "G5", "G6", "P5", "P6", "P7", "G7", "P8",
        },
        BurstNodes:    new[] { "P8", "G8" },
        // Same auger detour as mining: G3 sits in the Icetrap area (R6's spot),
        // so after gathering G3 fly to a charged Icetrap if one is up. The
        // near-node auger phase (any priority mob within range) already runs on
        // both routes; this adds the off-route Icetrap run botany was missing.
        DetourNodes:   new[] { "G3" },
        DetourMonster: "Diadem Icetrap",
        // From the GBR "Diadem Botany" list — the only slots we gather.
        WantedItemIds: new uint[] { 32037, 32036, 32035, 32038, 32039 },
        BuiltInNodes:  Nodes(
            ("P1", -251.60f,  -3.59f, -475.88f),
            ("G1", -195.88f,  -1.45f, -312.59f),
            ("G2", -261.23f,  -2.55f, -346.75f),
            ("G3", -321.71f,  -5.04f, -322.18f),
            ("P2", -356.22f,  -5.22f, -410.71f),
            ("P3", -370.49f,  -3.60f, -343.68f),
            ("G4", -376.62f,  15.51f, -291.58f),
            ("P4", -432.23f,  25.61f, -251.39f),
            ("G5", -420.88f,  22.68f, -202.51f),
            ("G6", -468.88f,  27.82f, -193.32f),
            ("P5", -486.54f,  25.16f, -245.16f),
            ("P6", -543.81f,  30.42f, -258.63f),
            ("P7", -577.00f,  34.21f, -232.93f),
            ("G7", -553.59f,  29.54f, -214.58f),
            ("P8", -205.38f,  -3.71f, -504.49f),
            ("G8", -148.64f,  -5.08f, -391.94f))
    );

    // ── Registry ────────────────────────────────────────────────────────────────
    public static readonly IReadOnlyList<RouteDefinition> AllRoutes = new[]
    {
        MiningRoute1,
        BotanyRoute1,
    };

    public static IEnumerable<RouteDefinition> RoutesFor(GatheringMode mode)
        => AllRoutes.Where(r => r.Category == mode);

    public static RouteDefinition? FindByName(string name)
        => AllRoutes.FirstOrDefault(r => r.Name == name);
}
