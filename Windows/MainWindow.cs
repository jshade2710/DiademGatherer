using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace DiademGatherer.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin _plugin;

    // Text colours
    private static readonly Vector4 Green  = new(0.35f, 0.90f, 0.45f, 1f);
    private static readonly Vector4 Red    = new(0.95f, 0.40f, 0.35f, 1f);
    private static readonly Vector4 Amber  = new(0.98f, 0.80f, 0.30f, 1f);
    private static readonly Vector4 Gray   = new(0.60f, 0.62f, 0.66f, 1f);
    private static readonly Vector4 Accent = new(0.98f, 0.78f, 0.35f, 1f); // section headers

    // Button fills
    private static readonly Vector4 GoBtn     = new(0.14f, 0.52f, 0.24f, 1f);
    private static readonly Vector4 StopBtn   = new(0.58f, 0.16f, 0.16f, 1f);
    private static readonly Vector4 WarnBtn   = new(0.55f, 0.42f, 0.10f, 1f);
    private static readonly Vector4 ActionBtn = new(0.12f, 0.42f, 0.52f, 1f);

    public MainWindow(Plugin plugin)
        : base("Diadem Gatherer##main")
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 380),
            MaximumSize = new Vector2(780, 1000),
        };
    }

    private Configuration Cfg => _plugin.Configuration;
    private RouteManager  Mgr => _plugin.RouteManager;

    // ── Shared UI helpers ────────────────────────────────────────────────────

    private static Vector4 Shift(Vector4 c, float d)
        => new(Math.Clamp(c.X + d, 0, 1), Math.Clamp(c.Y + d, 0, 1), Math.Clamp(c.Z + d, 0, 1), c.W);

    private static bool AccentButton(string label, Vector4 fill, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        fill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Shift(fill, 0.10f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Shift(fill, -0.06f));
        var r = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return r;
    }

    private static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(Accent, title);
        ImGui.Separator();
        ImGui.Spacing();
    }

    // A coloured "● STATUS" badge + trailing dim text.
    private static void StatusBadge(string state, Vector4 color, string trailing)
    {
        ImGui.TextColored(color, "●");
        ImGui.SameLine();
        ImGui.TextColored(color, state);
        if (!string.IsNullOrEmpty(trailing))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(trailing);
        }
    }

    // ── Window ───────────────────────────────────────────────────────────────

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##tabs")) return;
        if (ImGui.BeginTabItem("Routes##tabbot"))     { DrawBotTab();      ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Crafting##tabcraft")) { DrawCraftingTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Shop##tabshop"))      { DrawShopTab();     ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Settings##tabset"))   { DrawSettingsTab(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ═════════════════════════════ Routes tab ══════════════════════════════════

    private void DrawBotTab()
    {
        ImGui.Spacing();

        if (Mgr.IsRunning)
        {
            DrawRunningPanel();
            ImGui.Spacing();
            DrawRunControls();
            return;
        }

        // Idle: status + config + start.
        var inDiadem = Plugin.ClientState.TerritoryType == DiademData.DiademTerritoryId;
        StatusBadge("READY", Gray, inDiadem ? "in the Diadem" : "not in the Diadem");

        Section("Job");
        DrawModeSelector();

        Section("Quick Options");
        Toggle("Certify loot at Flotpassant", () => Cfg.EnableCertification, v => Cfg.EnableCertification = v,
            "During each reinstance: Auto-submit + Request Inspection on both tabs before shopping / re-entering.");
        Toggle("Loop route", () => Cfg.LoopRoute, v => Cfg.LoopRoute = v, "Repeat the cycle indefinitely.");
        Toggle("Auger priority monsters", () => Cfg.UseAuger, v => Cfg.UseAuger = v,
            "Shoot the Aetheromatic Auger at priority monsters (Icetrap first) after gathers and on the R6 detour.");
        Toggle("Auto-reinstance", () => Cfg.AutoReinstance, v => Cfg.AutoReinstance = v,
            "Leave and re-enter the Diadem via Aurvael when the instance timer hits the limit.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawStartButton();
    }

    private void DrawModeSelector()
    {
        if (Cfg.Mode is not (GatheringMode.Mining or GatheringMode.Botany))
        { Cfg.Mode = GatheringMode.Mining; Cfg.Save(); }

        bool changed = false;
        changed |= ModeRadio("Mining##m", GatheringMode.Mining);
        ImGui.SameLine();
        changed |= ModeRadio("Botany##b", GatheringMode.Botany);
        if (changed)
        {
            var first = DiademData.RoutesFor(Cfg.Mode).FirstOrDefault();
            if (first != null) Cfg.SelectedRouteName = first.Name;
            Cfg.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"  →  {Mgr.SelectedRoute()?.Name ?? "—"}");
    }

    private bool ModeRadio(string label, GatheringMode mode)
    {
        if (ImGui.RadioButton(label, Cfg.Mode == mode)) { Cfg.Mode = mode; return true; }
        return false;
    }

    private void DrawAdvanced()
    {
        ImGui.Spacing();

        int radius = Cfg.NodeSearchRadius;
        if (ImGui.SliderInt("Node search radius (y)##nsr", ref radius, 5, 60)) Cfg.NodeSearchRadius = radius;
        if (ImGui.IsItemDeactivatedAfterEdit()) Cfg.Save();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "How far from a recorded waypoint to look for the real node "
            + "(a node can spawn at one of a few spots near the recorded position).");

        int gp = Cfg.BountifulMinGp;
        if (ImGui.SliderInt("Bountiful GP##gp", ref gp, 100, 1000)) Cfg.BountifulMinGp = gp;
        if (ImGui.IsItemDeactivatedAfterEdit()) Cfg.Save();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "Cast Bountiful only above this GP — banks the rest for the burst nodes.");

        if (Cfg.AutoReinstance)
        {
            int mins = Cfg.ReinstanceMinutes;
            if (ImGui.SliderInt("Reinstance After (min)##remins", ref mins, 30, 175)) Cfg.ReinstanceMinutes = mins;
            if (ImGui.IsItemDeactivatedAfterEdit()) Cfg.Save();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Diadem cap is 3h — 150 min leaves margin.");
        }

        ImGui.Spacing();
        ImGui.TextColored(Gray, "Prerequisites");
        IpcRow("vnavmesh", Mgr.NavmeshAvailable);
    }

    private void DrawRunningPanel()
    {
        var route = Mgr.SelectedRoute();
        StatusBadge(Mgr.IsPaused ? "PAUSED" : "RUNNING",
                    Mgr.IsPaused ? Amber : Green,
                    $"{route?.Name}");

        ImGui.Spacing();
        ImGui.TextColored(Gray, "Now");
        ImGui.SameLine(90);
        ImGui.TextUnformatted(Mgr.PlaybackInfo);

        if (Mgr.CurrentState == BotState.FlyingToNode && !Mgr.NavReady)
        {
            var prog = Mgr.NavBuildProgress;
            ImGui.TextColored(Amber, prog >= 0
                ? $"Waiting for navmesh build… {prog * 100f:F0}%"
                : "Waiting for navmesh — is vnavmesh loaded?");
        }

        ImGui.TextColored(Gray, "Instance");
        ImGui.SameLine(90);
        DrawInstanceTimer();
    }

    // Inline timer (no leading label — caller places it).
    private void DrawInstanceTimer()
    {
        var elapsed = Mgr.InstanceElapsed;
        if (elapsed.HasValue)
        {
            var limit = TimeSpan.FromMinutes(Cfg.ReinstanceMinutes);
            var e     = elapsed.Value;
            var color = e >= limit ? Red : (e >= limit - TimeSpan.FromMinutes(15) ? Amber : Green);
            ImGui.TextColored(color,
                $"{(int)e.TotalHours:D1}:{e.Minutes:D2}:{e.Seconds:D2} / {(int)limit.TotalHours:D1}:{limit.Minutes:D2}:00" +
                (Cfg.AutoReinstance ? "  (auto)" : ""));
        }
        else
        {
            ImGui.TextDisabled("— (enter the Diadem to start)");
        }
    }

    private void DrawRunControls()
    {
        ImGui.Separator();
        ImGui.Spacing();
        var half = new Vector2(ImGui.GetContentRegionAvail().X * 0.5f - 4, 0);

        if (Mgr.IsPaused) { if (AccentButton("Resume##res", GoBtn, half))   Mgr.Resume(); }
        else              { if (AccentButton("Pause##pau",  WarnBtn, half)) Mgr.Pause();  }
        ImGui.SameLine();
        if (AccentButton("Skip Node##skip", ActionBtn, half)) Mgr.SkipCurrentNode();

        if (AccentButton("Force Reinstance##fri", WarnBtn, new Vector2(-1, 0))) Mgr.TriggerReinstance();
        if (AccentButton("Stop##stop", StopBtn, new Vector2(-1, 0)))          Mgr.Stop();
    }

    private void DrawStartButton()
    {
        var route = Mgr.SelectedRoute();
        bool canStart = route != null && route.IsReady(Cfg) && Mgr.NavmeshAvailable;

        if (!canStart) ImGui.BeginDisabled();
        if (AccentButton("Start Gathering##start", GoBtn, new Vector2(-1, 34))) Mgr.Start();
        if (!canStart) ImGui.EndDisabled();

        if (!Mgr.NavmeshAvailable) ImGui.TextColored(Red, "vnavmesh not connected.");
        else if (route != null && !route.IsReady(Cfg)) ImGui.TextColored(Amber, "Record all nodes first.");
    }

    // ═══════════════════════════ Crafting tab ══════════════════════════════════

    private void DrawCraftingTab()
    {
        var cm = _plugin.CraftingManager;
        ImGui.Spacing();

        // Status card
        if (cm.IsRunning) StatusBadge("RUNNING", Green, $"{cm.CurrentState}");
        else              StatusBadge("STOPPED", Gray, "");

        ImGui.TextColored(Gray, "Goal");
        ImGui.SameLine(90); ImGui.TextUnformatted(cm.CurrentGoalName);

        ImGui.TextColored(Gray, "Turn-ins");
        ImGui.SameLine(90); ImGui.TextUnformatted($"{cm.TurnInsTotal} total · {cm.TurnInsSinceKupo}/{Cfg.KupoEveryTurnIns} to kupo");

        Section("Collectables");
        DrawCraftGoals(cm);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawCraftControls(cm);
    }

    private void DrawCraftGoals(CraftingManager cm)
    {
        if (ImGui.BeginTable("##craftgoals", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("On",          ImGuiTableColumnFlags.WidthFixed, 28);
            ImGui.TableSetupColumn("Collectable", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Batch",       ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Have",        ImGuiTableColumnFlags.WidthFixed, 46);
            ImGui.TableSetupColumn("",            ImGuiTableColumnFlags.WidthFixed, 26);
            ImGui.TableHeadersRow();

            int removeAt = -1;
            var artisanal = ItemHelper.ArtisanalCollectables();
            for (int i = 0; i < Cfg.CraftGoals.Count; i++)
            {
                var g = Cfg.CraftGoals[i];
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                CenterNext(CheckboxWidth);
                bool on = g.Enabled;
                if (ImGui.Checkbox($"##con{i}", ref on)) { g.Enabled = on; Cfg.Save(); }

                ImGui.TableSetColumnIndex(1);
                ImGui.SetNextItemWidth(-1);
                var preview = string.IsNullOrWhiteSpace(g.ItemName)
                    ? "(pick a collectable)"
                    : $"{ClassName(ItemHelper.ResolveRecipe(g.ItemName).JobId)}: {Short(g.ItemName)}";
                if (ImGui.BeginCombo($"##cname{i}", preview))
                {
                    foreach (var (aName, aJob) in artisanal)
                        if (ImGui.Selectable($"{ClassName(aJob)}: {Short(aName)}", aName == g.ItemName))
                        { g.ItemName = aName; Cfg.Save(); }
                    ImGui.EndCombo();
                }

                ImGui.TableSetColumnIndex(2);
                ImGui.SetNextItemWidth(-1);
                int batch = g.Batch;
                if (ImGui.InputInt($"##cbatch{i}", ref batch, 0)) g.Batch = Math.Max(1, batch);
                if (ImGui.IsItemDeactivatedAfterEdit()) Cfg.Save();

                ImGui.TableSetColumnIndex(3);
                var have = string.IsNullOrWhiteSpace(g.ItemName) ? -1 : ItemHelper.CountInInventory(g.ItemName);
                ImGui.TextColored(have >= g.Batch && g.Batch > 0 ? Green : Gray, have >= 0 ? have.ToString() : "—");
                if (!string.IsNullOrWhiteSpace(g.ItemName) && ImGui.IsItemHovered())
                {
                    var (_, gj) = ItemHelper.ResolveRecipe(g.ItemName);
                    var sc = cm.LastScore(gj);
                    if (sc >= 0) ImGui.SetTooltip($"{ClassName(gj)} score: {sc:N0} / {Cfg.MaxAccumulatedScore:N0}");
                }

                ImGui.TableSetColumnIndex(4);
                CenterNext(SmallButtonWidth("—"));
                if (ImGui.SmallButton($"—##cdel{i}")) removeAt = i;
            }
            ImGui.EndTable();
            if (removeAt >= 0) { Cfg.CraftGoals.RemoveAt(removeAt); Cfg.Save(); }
        }

        ImGui.Spacing();
        if (AccentButton("+ Add##addcraft", ActionBtn, new Vector2(90, 0)))
        { Cfg.CraftGoals.Add(new Configuration.CraftGoal()); Cfg.Save(); }
        ImGui.SameLine();
        if (AccentButton("Add All Classes##addall", ActionBtn, new Vector2(150, 0)))
        {
            foreach (var (aName, _) in ItemHelper.ArtisanalCollectables())
                if (!Cfg.CraftGoals.Any(g => g.ItemName == aName))
                    Cfg.CraftGoals.Add(new Configuration.CraftGoal { ItemName = aName });
            Cfg.Save();
        }
    }

    private void DrawCraftSettings()
    {
        ImGui.Spacing();
        int kEvery = Cfg.KupoEveryTurnIns;
        if (ImGui.SliderInt("Kupo after N turn-ins##kupon", ref kEvery, 5, 50)) Cfg.KupoEveryTurnIns = kEvery;
        if (ImGui.IsItemDeactivatedAfterEdit()) Cfg.Save();

        int kFloor = Cfg.KupoVoucherFloor;
        if (ImGui.SliderInt("Keep N vouchers##kupofloor", ref kFloor, 0, 9)) Cfg.KupoVoucherFloor = kFloor;
        if (ImGui.IsItemDeactivatedAfterEdit()) Cfg.Save();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stop playing kupo cards once this many vouchers remain (0 = spend all).");

        Toggle("Stop at a class score cap", () => Cfg.EnableScoreCap, v => Cfg.EnableScoreCap = v,
            "Skip turn-ins for a class once its accumulated score reaches the cap below. "
            + "Turn this off to keep handing in regardless.");

        if (!Cfg.EnableScoreCap) return;

        int max = Cfg.MaxAccumulatedScore;
        if (ImGui.SliderInt("Stop at class score##maxscore", ref max, 100_000, 1_000_000)) Cfg.MaxAccumulatedScore = max;
        if (ImGui.IsItemDeactivatedAfterEdit()) Cfg.Save();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Retire a class once its accumulated score reaches this (cap 500,000).");

        ImGui.Spacing();
        IpcRow("Artisan", _plugin.CraftingManager.ArtisanAvailable);
    }

    private void DrawCraftControls(CraftingManager cm)
    {
        if (cm.IsRunning)
        {
            if (cm.CurrentState == CraftState.AwaitKupo &&
                AccentButton("Continue (skip kupo)##kupocont", ActionBtn, new Vector2(-1, 0)))
                cm.ContinueFromKupo();

            if (AccentButton("Stop Crafting##craftstop", StopBtn, new Vector2(-1, 34))) cm.Stop();
        }
        else
        {
            bool can = cm.ArtisanAvailable;
            if (!can) ImGui.BeginDisabled();
            if (AccentButton("Start Crafting##craftstart", GoBtn, new Vector2(-1, 34))) cm.Start();
            if (!can) ImGui.EndDisabled();
            if (!can) ImGui.TextColored(Red, "Artisan not connected.");

            if (AccentButton("Play Kupo Now##kuponow", ActionBtn, new Vector2(-1, 0))) cm.ForceKupo();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Walk to Lizbeth and play kupo cards once, then stop.");
        }
    }

    // ═══════════════════════════ Settings tab ══════════════════════════════════
    // Everything tunable in one place, split by what it affects.

    private void DrawSettingsTab()
    {
        ImGui.Spacing();

        Section("Routes");
        DrawAdvanced();

        ImGui.Spacing();
        Section("Crafting");
        DrawCraftSettings();

        ImGui.Spacing();
        Section("Repair");
        DrawRepairSettings();

        ImGui.Spacing();
        Section("Troubleshooting");
        DrawDiagnostics();
    }

    // One-click report to attach to a bug report — Dalamud's own log interleaves
    // every plugin, which is too noisy to send.
    private void DrawDiagnostics()
    {
        ImGui.TextWrapped("If something goes wrong, save a report and attach it to the issue. "
                        + "It holds this plugin's recent log lines and your settings — no account details.");
        ImGui.Spacing();

        // The most useful lines are logged at Debug, and Dalamud's log level is a
        // global setting — so warn before someone sends a report missing the very
        // detail that would explain their problem.
        if (!DiagnosticsReport.DebugCaptured)
        {
            var lvl = DiagnosticsReport.LogLevelName(DiagnosticsReport.DalamudLogLevel());
            ImGui.TextColored(Amber, $"Dalamud's log level is {lvl}.");
            ImGui.TextWrapped("Reports will be missing the approach and gathering detail. "
                            + "For a useful report set it to Debug (Dalamud Settings → Log Level), "
                            + "reproduce the problem, then save.");
            ImGui.Spacing();
        }

        if (AccentButton("Save Log Report##savelog", ActionBtn, new Vector2(190, 0)))
        {
            try
            {
                _lastReportPath = DiagnosticsReport.Write(_plugin);
                ImGui.SetClipboardText(_lastReportPath);
                Plugin.ChatGui.Print($"[DiademGatherer] Report saved to {_lastReportPath} (path copied).");
            }
            catch (Exception ex)
            {
                _lastReportPath = null;
                Plugin.ChatGui.PrintError($"[DiademGatherer] Couldn't write the report: {ex.Message}");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Writes the report next to the plugin config and copies its path to the clipboard.");

        if (_lastReportPath == null) return;

        ImGui.SameLine();
        if (AccentButton("Open Folder##openlog", ActionBtn, new Vector2(130, 0)))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = Path.GetDirectoryName(_lastReportPath)!,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Plugin.ChatGui.PrintError($"[DiademGatherer] Couldn't open the folder: {ex.Message}");
            }
        }

        ImGui.TextColored(Green, "Saved — path copied to clipboard.");
    }

    private string? _lastReportPath;

    private void DrawRepairSettings()
    {
        Toggle("Repair gear between instances", () => Cfg.EnableRepair, v => Cfg.EnableRepair = v,
            "On the way back into the Diadem, repair with dark matter if any equipped piece is worn.");

        if (!Cfg.EnableRepair) return;

        ImGui.SetNextItemWidth(220);
        int pct = Cfg.RepairThreshold;
        if (ImGui.SliderInt("Repair below (%)##repairpct", ref pct, 10, 99)) Cfg.RepairThreshold = pct;
        if (ImGui.IsItemDeactivatedAfterEdit()) Cfg.Save();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "Repair when the most worn equipped item drops below this. Gear breaks at 0%, "
            + "and loses its stat bonus at 0% — 50% is a safe margin.");

        var worst = ItemHelper.LowestEquippedCondition();
        ImGui.TextColored(Gray, "Condition");
        ImGui.SameLine(110);
        if (worst >= 0) ImGui.TextColored(worst < Cfg.RepairThreshold ? Red : Green, $"{worst}%");
        else            ImGui.TextDisabled("—");
    }

    // ═════════════════════════════ Shop tab ════════════════════════════════════

    private void DrawShopTab()
    {
        ImGui.Spacing();

        // Scrip balance lives here, with the setting that spends it.
        var scrips = ItemHelper.ScripCount();
        ImGui.TextColored(Gray, "Scrips");
        ImGui.SameLine(90);
        if (scrips >= 0) ImGui.TextColored(scrips >= Cfg.ScripDumpAt ? Red : Green, $"{scrips:N0} / 10,000");
        else             ImGui.TextDisabled("—");
        ImGui.Spacing();

        Toggle("Spend scrips on these goals", () => Cfg.EnableShopping, v => Cfg.EnableShopping = v,
            "After gathering turn-ins and crafting turn-ins, buy the goals below at Enie.");

        ImGui.SetNextItemWidth(220);
        int dump = Cfg.ScripDumpAt;
        if (ImGui.SliderInt("Spend scrips at##dumpat", ref dump, 5000, 9800))
            Cfg.ScripDumpAt = (dump / 100) * 100;   // snap to 100s
        if (ImGui.IsItemDeactivatedAfterEdit()) Cfg.Save();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(
            "Only walk to Enie once scrips reach this. Applies to both the crafting turn-ins and the "
            + "gathering (certification) turn-ins. Scrips cap at 10,000.");

        Section("Purchase Goals");

        if (ImGui.BeginTable("##goals", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("On",    ImGuiTableColumnFlags.WidthFixed, 28);
            ImGui.TableSetupColumn("Item",  ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("To Buy", ImGuiTableColumnFlags.WidthFixed, 64);
            ImGui.TableSetupColumn("Keep Buying", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Remove",      ImGuiTableColumnFlags.WidthFixed, 58);
            ImGui.TableHeadersRow();

            int removeAt = -1;
            for (int i = 0; i < Cfg.PurchaseGoals.Count; i++)
            {
                var g = Cfg.PurchaseGoals[i];
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                CenterNext(CheckboxWidth);
                bool on = g.Enabled;
                if (ImGui.Checkbox($"##on{i}", ref on)) { g.Enabled = on; Cfg.Save(); }

                // Item: one dropdown of every shop's items, with a dashed shop
                // header between groups — the shop is set from the pick, so
                // there's no separate shop selector.
                ImGui.TableSetColumnIndex(1);
                ImGui.SetNextItemWidth(-1);
                var itemPreview = string.IsNullOrWhiteSpace(g.ItemName) ? "(pick an item)" : g.ItemName;
                if (ImGui.BeginCombo($"##name{i}", itemPreview))
                {
                    for (int shop = 0; shop < Configuration.ShopNames.Length; shop++)
                    {
                        ImGui.TextDisabled($"------ {Configuration.ShopNames[shop]} ------");
                        foreach (var e in ShopCatalog.ForShop(shop))
                            if (ImGui.Selectable(e.Name, e.Name == g.ItemName))
                            { g.ItemName = e.Name; g.Shop = e.Shop; Cfg.Save(); }
                    }
                    ImGui.EndCombo();
                }
                if (!string.IsNullOrWhiteSpace(g.ItemName) && ImGui.IsItemHovered())
                {
                    var price    = ShopCatalog.PriceOf(g.ItemName);
                    var bulk     = ShopCatalog.IsBulk(g.ItemName);
                    var shopName = g.Shop >= 0 && g.Shop < Configuration.ShopNames.Length
                        ? Configuration.ShopNames[g.Shop] : "?";
                    ImGui.SetTooltip($"{shopName}  ·  "
                        + (price > 0 ? $"{price:N0} scrips each" : "price not recorded yet")
                        + (bulk ? "  ·  buy in bulk" : "  ·  one at a time"));
                }

                // To Buy: how many are left to purchase. The runner counts this
                // down to 0 as it buys (inventory-independent), so it's safe to
                // stash bought items in a retainer.
                ImGui.TableSetColumnIndex(2);
                if (g.KeepBuying)
                {
                    ImGui.TextColored(Green, "always");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Buying forever — the count is ignored.");
                }
                else
                {
                    ImGui.SetNextItemWidth(-1);
                    int tgt = g.Target;
                    if (ImGui.InputInt($"##tgt{i}", ref tgt, 0)) g.Target = Math.Max(0, tgt);
                    if (ImGui.IsItemDeactivatedAfterEdit()) Cfg.Save();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(g.Target > 0 ? $"{g.Target} left to buy — counts down as it buys."
                                                      : "Done. Set a number to buy more.");
                }

                // Keep: buy this forever as a scrip sink so the balance never caps.
                ImGui.TableSetColumnIndex(3);
                CenterNext(CheckboxWidth);
                bool keep = g.KeepBuying;
                if (ImGui.Checkbox($"##keep{i}", ref keep))
                {
                    g.KeepBuying = keep;
                    // Only ONE item is the scrip sink — turning this on turns it off
                    // everywhere else, so leftover scrips all go to one place instead
                    // of being split unpredictably between several.
                    if (keep)
                        foreach (var other in Cfg.PurchaseGoals)
                            if (!ReferenceEquals(other, g)) other.KeepBuying = false;
                    Cfg.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Keep buying this forever (ignores the count) so leftover scrips "
                                   + "always have somewhere to go and never cap.\n"
                                   + "Only one item can be the sink — setting it here clears the others.");

                ImGui.TableSetColumnIndex(4);
                CenterNext(SmallButtonWidth("—"));
                if (ImGui.SmallButton($"—##del{i}")) removeAt = i;
            }
            ImGui.EndTable();
            if (removeAt >= 0) { Cfg.PurchaseGoals.RemoveAt(removeAt); Cfg.Save(); }
        }

        ImGui.Spacing();
        if (AccentButton("+ Add Goal##addgoal", ActionBtn, new Vector2(120, 0)))
        { Cfg.PurchaseGoals.Add(new Configuration.PurchaseGoal()); Cfg.Save(); }
        ImGui.SameLine();
        ImGui.TextDisabled("Pick an item — its shop is set automatically. Hover for price.");
    }

    // ── small helpers ────────────────────────────────────────────────────────

    // Centre the next widget of `width` horizontally within the current cell.
    private static void CenterNext(float width)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > width) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - width) * 0.5f);
    }

    private static float CheckboxWidth => ImGui.GetFrameHeight();

    private static float SmallButtonWidth(string label)
        => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;

    private void Toggle(string label, Func<bool> get, Action<bool> set, string tip = "")
    {
        bool v = get();
        if (ImGui.Checkbox(label, ref v)) { set(v); Cfg.Save(); }
        if (tip.Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    private static string Short(string itemName)
        => itemName.Replace("Grade 4 Artisanal Skybuilders' ", "").Replace("Grade 4 Skybuilders' ", "");

    private static readonly string[] CrafterNames =
        { "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL" };

    private static string ClassName(uint job)
        => job is >= 8 and <= 15 ? CrafterNames[job - 8] : "?";

    private static void IpcRow(string name, bool ok)
    {
        ImGui.TextColored(ok ? Green : Red, ok ? "●" : "○");
        ImGui.SameLine();
        ImGui.TextUnformatted(name);
        if (!ok) { ImGui.SameLine(); ImGui.TextColored(Red, "not connected"); }
    }
}
