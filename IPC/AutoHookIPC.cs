namespace DiademGatherer.IPC;

// AutoHook control surface.
//
// Unlike vnavmesh and Artisan, AutoHook exposes NO IPC. It references ECommons'
// EzIpcManager but registers no [EzIPC] methods, and the shipped DLL carries no
// "AutoHook.*" call-gate labels at all — so there is nothing to subscribe to.
// What it does register is slash commands, and Dalamud will dispatch those
// plugin-to-plugin through ICommandManager.ProcessCommand.
//
// Commands used, verified against PunishXIV/AutoHook's AutoHook.cs:
//   /ahpreset <name>  selects a preset by EXACT, CASE-SENSITIVE PresetName.
//                     The argument is NOT trimmed — see PresetName below.
//   /ahon  /ahoff     set Configuration.C.PluginEnabled.
//   /ahstart          FishingSessionManager.Get().ManualStartFishing().
//
// Deliberately one-way: AutoHook reports no "am I running" state by any route,
// so the fishing state machine infers progress from the game's own condition
// flags rather than asking. See RouteManager.Fishing.cs.
public sealed class AutoHookIPC
{
    // AutoHook registers its handlers at load, so the presence of its commands
    // is the availability probe. There is no InvokeFunc to try here, which is
    // how the other two IPC wrappers answer this question.
    public bool IsAvailable => Plugin.CommandManager.Commands.ContainsKey("/ahon");

    public bool Enable()      => Send("/ahon");
    public bool Disable()     => Send("/ahoff");
    public bool StartFishing() => Send("/ahstart");

    // `name` must match the preset's PresetName byte for byte. AutoHook compares
    // with an exact, case-sensitive equality and does not trim, so a preset named
    // with stray double or trailing spaces can only be selected by reproducing
    // them — which is why the config field warns about it rather than tidying the
    // string up here. Silently trimming would turn "preset not found" into a run
    // that fishes on whatever preset happened to be selected.
    public bool SelectPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return Send($"/ahpreset {name}");
    }

    // Does AutoHook actually have a preset by this name?
    //
    // Worth the trouble because a mismatch is otherwise SILENT and expensive:
    // /ahpreset prints "not found" in the user's chat and leaves whatever preset
    // was already selected, so the run would fish happily for a full instance on
    // the wrong rotation — a line stays in the water, so no watchdog here would
    // ever notice. Checking up front turns that into one clear message.
    //
    // Read-only, and tolerant: any failure returns null, meaning "couldn't tell",
    // and the caller proceeds rather than blocking on our inability to parse
    // someone else's config.
    public static bool? PresetExists(string name)
    {
        try
        {
            // pluginConfigs/DiademGatherer → pluginConfigs → AutoHook.json.
            // Derived rather than hard-coded so it follows a moved XIVLauncher.
            var ours = Plugin.PluginInterface.GetPluginConfigDirectory();
            var dir  = Path.GetDirectoryName(ours);
            if (dir == null) return null;
            var path = Path.Combine(dir, "AutoHook.json");
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            // Matched textually rather than deserialized: the file runs to
            // several megabytes and we only need one field out of it.
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(json, "\"PresetName\"\\s*:\\s*\"(.*?)\""))
            {
                var found = System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value);
                if (found == name) return true;   // ordinal: AutoHook is case-sensitive
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[DiademGatherer] Couldn't check AutoHook's presets: {ex.Message}");
            return null;
        }
    }

    private static bool Send(string command)
    {
        try
        {
            var ok = Plugin.CommandManager.ProcessCommand(command);
            if (!ok)
                Plugin.Log.Warning($"[DiademGatherer] AutoHook did not accept \"{command}\" " +
                                   "(is AutoHook installed and enabled?).");
            else
                Plugin.Log.Debug($"[DiademGatherer] → AutoHook: {command}");
            return ok;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"[DiademGatherer] Sending \"{command}\" to AutoHook failed");
            return false;
        }
    }
}
