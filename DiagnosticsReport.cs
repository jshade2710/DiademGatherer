using System.Text;

namespace DiademGatherer;

// Builds a single shareable file when someone hits a problem: the plugin's own
// log lines plus the settings that shape its behaviour. Dalamud's log holds
// every plugin's output interleaved, which is far too noisy to send — this pulls
// out just ours and puts it somewhere easy to attach to a bug report.
public static class DiagnosticsReport
{
    // Ours are the only lines worth sending; everything else is other plugins.
    private const string Tag = "DiademGatherer";

    // Enough history to cover several laps and a reinstance without producing a
    // file too large to attach.
    private const int MaxLines = 4000;

    public static string FileName => "diadem-report.txt";

    private static string XivLauncherDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher");

    private static string DalamudLogPath => Path.Combine(XivLauncherDir, "dalamud.log");

    // Dalamud's log level is a global setting, not per-plugin, and it decides
    // whether our Debug lines reach the log at all. Serilog's LogEventLevel:
    // 0 Verbose, 1 Debug, 2 Information, 3 Warning, 4 Error, 5 Fatal.
    // Null when it can't be read.
    public static int? DalamudLogLevel()
    {
        try
        {
            var cfg = Path.Combine(XivLauncherDir, "dalamudConfig.json");
            if (!File.Exists(cfg)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
            return doc.RootElement.TryGetProperty("LogLevel", out var v) && v.TryGetInt32(out var i)
                ? i : null;
        }
        catch { return null; }
    }

    public static string LogLevelName(int? level) => level switch
    {
        0 => "Verbose", 1 => "Debug", 2 => "Information",
        3 => "Warning", 4 => "Error",  5 => "Fatal",
        _ => "unknown",
    };

    // True when the log is capturing at least Debug — i.e. a report will contain
    // the detail needed to diagnose an approach or gathering problem.
    public static bool DebugCaptured
    {
        get { var l = DalamudLogLevel(); return l is null or <= 1; }
    }

    // Writes the report next to the plugin's config and returns its full path.
    // Throws only on genuinely unexpected IO — callers report the message.
    public static string Write(Plugin plugin)
    {
        var sb = new StringBuilder();
        var cfg = plugin.Configuration;

        sb.AppendLine("=== DiademGatherer report ===");
        sb.AppendLine($"generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"plugin    : {typeof(Plugin).Assembly.GetName().Version}");
        sb.AppendLine($"territory : {Plugin.ClientState.TerritoryType}");

        var lvl = DalamudLogLevel();
        sb.AppendLine($"log level : {LogLevelName(lvl)}"
                    + (DebugCaptured
                        ? " (full detail)"
                        : " — Debug NOT captured, so the approach/gather detail is MISSING."
                          + " Set Dalamud's log level to Debug or Verbose, reproduce, and re-save."));
        sb.AppendLine($"job       : {Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId.ToString() ?? "—"}");
        sb.AppendLine();

        sb.AppendLine("--- state ---");
        sb.AppendLine($"route     : {cfg.SelectedRouteName} ({cfg.Mode})");
        sb.AppendLine($"bot       : {plugin.RouteManager.CurrentState}"
                    + $"{(plugin.RouteManager.IsRunning ? " (running)" : "")}"
                    + $"{(plugin.RouteManager.IsPaused ? " (paused)" : "")}");
        sb.AppendLine($"playback  : {plugin.RouteManager.PlaybackInfo}");
        sb.AppendLine($"crafting  : {plugin.CraftingManager.CurrentState}");
        sb.AppendLine($"vnavmesh  : available={plugin.RouteManager.NavmeshAvailable} ready={plugin.RouteManager.NavReady}");
        sb.AppendLine();

        sb.AppendLine("--- settings ---");
        sb.AppendLine($"node search radius : {cfg.NodeSearchRadius}");
        sb.AppendLine($"bountiful min GP   : {cfg.BountifulMinGp}");
        sb.AppendLine($"auger              : {cfg.UseAuger}");
        sb.AppendLine($"certification      : {cfg.EnableCertification}");
        sb.AppendLine($"shopping           : {cfg.EnableShopping} (spend at {cfg.ScripDumpAt})");
        sb.AppendLine($"repair             : {cfg.EnableRepair} (below {cfg.RepairThreshold}%)");
        sb.AppendLine($"auto-reinstance    : {cfg.AutoReinstance} ({cfg.ReinstanceMinutes} min)");
        sb.AppendLine($"loop route         : {cfg.LoopRoute}");
        sb.AppendLine($"score cap          : {cfg.MaxAccumulatedScore}");
        sb.AppendLine($"learned spots      : {cfg.LearnedNodeSpotSets.Count} nodes");
        sb.AppendLine();

        sb.AppendLine($"--- last {MaxLines} log lines ---");
        try
        {
            foreach (var line in TailPluginLog(MaxLines)) sb.AppendLine(line);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(couldn't read {DalamudLogPath}: {ex.Message})");
        }

        var path = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), FileName);
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    // Dalamud keeps the log open for writing, so it must be opened shared or the
    // read fails outright.
    private static List<string> TailPluginLog(int max)
    {
        var kept = new Queue<string>(max);
        using var fs = new FileStream(DalamudLogPath, FileMode.Open,
                                      FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);

        while (sr.ReadLine() is { } line)
        {
            if (!line.Contains(Tag, StringComparison.Ordinal)) continue;
            if (kept.Count == max) kept.Dequeue();
            kept.Enqueue(line);
        }
        return kept.ToList();
    }
}
