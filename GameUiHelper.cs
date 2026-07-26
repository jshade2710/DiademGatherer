using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using RaptureAtkUnitManager = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DiademGatherer;

// Low-level game UI interaction for the reinstance flow:
//   leave duty → talk to Aurvael → "Travel to the Diadem" → Yes → Commence.
// The duty-exit sequence (agent Show + callbacks 0 / -2) mirrors AutoDuty's
// proven ExitDutyHelper.
public static unsafe class GameUiHelper
{
    public static AtkUnitBase* GetAddon(string name)
    {
        var ptr = Plugin.GameGui.GetAddonByName(name, 1).Address;
        return ptr == nint.Zero ? null : (AtkUnitBase*)ptr;
    }

    public static bool IsVisible(string name)
    {
        var addon = GetAddon(name);
        return addon != null && addon->IsVisible;
    }

    public static bool FireCallback(string addonName, bool updateState, params int[] ints)
    {
        var addon = GetAddon(addonName);
        if (addon == null || !addon->IsVisible) return false;

        var count  = ints.Length == 0 ? 1 : ints.Length;
        var values = stackalloc AtkValue[count];
        for (var i = 0; i < ints.Length; i++)
        {
            values[i].Type = AtkValueType.Int;
            values[i].Int  = ints[i];
        }

        addon->FireCallback((uint)ints.Length, values, updateState);
        Plugin.Log.Debug($"[DiademGatherer] Callback {addonName} [{string.Join(",", ints)}]");
        return true;
    }

    // Advance a Talk dialogue box. Recording a manual click showed the box only
    // advances on the FULL gesture — MouseDown → MouseUp → MouseClick — not a
    // lone MouseClick (that was a no-op that looped forever). The event MUST
    // carry a real Listener + Target or the native handler crashes on a null.
    public static bool ClickTalk()
    {
        var addon = GetAddon("Talk");
        if (addon == null || !addon->IsVisible) return false;

        FireAddonEvent(addon, AtkEventType.MouseDown);
        FireAddonEvent(addon, AtkEventType.MouseUp);
        FireAddonEvent(addon, AtkEventType.MouseClick);
        return true;
    }

    private static void FireAddonEvent(AtkUnitBase* addon, AtkEventType type)
    {
        var evt = stackalloc AtkEvent[1];
        evt[0].Listener = (AtkEventListener*)addon;
        evt[0].Target   = &AtkStage.Instance()->AtkEventTarget;
        var data = stackalloc AtkEventData[1];
        addon->ReceiveEvent(type, 0, evt, data);
    }

    // Click the SelectString menu entry whose text contains `contains`.
    public static bool SelectStringEntry(string contains)
        => SelectStringEntryWhere(t => t.Contains(contains, StringComparison.OrdinalIgnoreCase), contains);

    // Click the first SelectString entry matching an arbitrary predicate —
    // needed when substrings overlap (Enie's three "Skybuilders' Scrips…" shops).
    public static bool SelectStringEntryWhere(Func<string, bool> predicate, string describe = "(predicate)")
    {
        var addon = (AddonSelectString*)GetAddon("SelectString");
        if (addon == null || !addon->AtkUnitBase.IsVisible) return false;

        var menu = &addon->PopupMenu.PopupMenu;
        for (var i = 0; i < menu->EntryCount; i++)
        {
            // Entry pointers can be null mid-teardown — never dereference blind.
            if (menu->EntryNames == null || menu->EntryNames[i].Value == null) continue;
            string text;
            try { text = MemoryHelper.ReadStringNullTerminated((nint)menu->EntryNames[i].Value); }
            catch { continue; }
            if (predicate(text))
                return FireCallback("SelectString", true, i);
        }
        return SelectStringNoMatch(menu, describe);
    }

    private static bool SelectStringNoMatch(PopupMenu* menu, string contains)
    {

        // No match — dump the entries so the mismatch is diagnosable.
        ProbeLog($"SelectString had no entry containing \"{contains}\"; entries were:");
        for (var i = 0; i < menu->EntryCount; i++)
        {
            try { ProbeLog($"  [{i}] {MemoryHelper.ReadStringNullTerminated((nint)menu->EntryNames[i].Value)}"); }
            catch { /* ignore */ }
        }
        Plugin.Log.Warning($"[DiademGatherer] SelectString open but no entry contains \"{contains}\".");
        return false;
    }

    public static bool ClickYes() => FireCallback("SelectYesno", true, 0);
    public static bool ClickNo()  => FireCallback("SelectYesno", true, 1);

    // The prompt text of the currently open SelectYesno (AtkValues[0]).
    public static bool TryGetSelectYesnoText(out string text)
    {
        text = string.Empty;
        var addon = GetAddon("SelectYesno");
        if (addon == null || !addon->IsVisible || addon->AtkValuesCount < 1) return false;
        try
        {
            var v = addon->AtkValues[0];
            if (v.Type != AtkValueType.String) return false;
            text = MemoryHelper.ReadStringNullTerminated((nint)v.String.Value);
            return true;
        }
        catch { return false; }
    }

    // "Commence" on the duty confirmation window.
    public static bool ClickCommence() => FireCallback("ContentsFinderConfirm", true, 8);

    // Open the in-duty ContentsFinderMenu window (needed before ClickLeaveDuty).
    public static void ShowDutyMenu()
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.ContentsFinderMenu);
        if (agent != null) agent->Show();
    }

    // Click "Leave Duty" on the ContentsFinderMenu (AutoDuty sequence).
    // The addon exists but reports IsVisible=false while it animates in, so
    // fire on existence rather than visibility — the callback is a no-op if the
    // window truly isn't there, whereas waiting on IsVisible can hang forever.
    public static bool ClickLeaveDuty()
    {
        var addon = GetAddon("ContentsFinderMenu");
        if (addon == null) return false;

        FireCallbackUnchecked(addon, "ContentsFinderMenu", true, 0);
        FireCallbackUnchecked(addon, "ContentsFinderMenu", false, -2);
        return true;
    }

    // FireCallback without the IsVisible gate, for addons that are present but
    // still animating. Only use where a stray callback is harmless.
    private static void FireCallbackUnchecked(AtkUnitBase* addon, string name, bool updateState, params int[] ints)
    {
        var count  = ints.Length == 0 ? 1 : ints.Length;
        var values = stackalloc AtkValue[count];
        for (var i = 0; i < ints.Length; i++)
        {
            values[i].Type = AtkValueType.Int;
            values[i].Int  = ints[i];
        }

        addon->FireCallback((uint)ints.Length, values, updateState);
        Plugin.Log.Debug($"[DiademGatherer] Callback {name} [{string.Join(",", ints)}] (unchecked)");
    }

    // Aetheromatic Auger gauge, read from the Diadem HUD element the same way
    // GBR does (HWDAetherGauge + 0x268). Returns -1 if the gauge isn't on
    // screen (not in the Diadem / HUD element hidden).
    private const int AetherGaugeOffset = 0x268;

    public static int AugerGaugeValue()
    {
        var addon = GetAddon("HWDAetherGauge");
        if (addon == null || !addon->IsVisible) return -1;

        // Raw struct-offset read (patch-fragile), so it's sanity-bounded — but
        // the bound has to be generous. It was 1000, and the gauge climbs well
        // past that: once it did, every read was rejected as garbage, the auger
        // phase stopped firing, and the gauge simply sat full. The point of the
        // check is only to reject a wrong offset (pointers read as huge or
        // negative), not to second-guess a plausible charge.
        var value = *(int*)((nint)addon + AetherGaugeOffset);
        if (value >= 0 && value <= GaugeSanityMax) return value;

        // Out of range means we're probably reading the wrong thing — say so
        // once in a while rather than silently never firing again.
        if (DateTime.UtcNow >= _gaugeWarnAt)
        {
            _gaugeWarnAt = DateTime.UtcNow + TimeSpan.FromMinutes(5);
            Plugin.Log.Warning($"[DiademGatherer] Auger gauge read {value} is outside 0..{GaugeSanityMax} — "
                             + "treating it as unreadable, so the auger won't fire.");
        }
        return -1;
    }

    private const int GaugeSanityMax = 100_000;
    private static DateTime _gaugeWarnAt = DateTime.MinValue;

    // Probe output goes to BOTH the Dalamud log and a dedicated file, because
    // dalamud.log stops persisting once it hits its 100 MB cap mid-session.
    private static string ProbeFilePath
        => Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "probe.log");

    private static void ProbeLog(string line)
    {
        Plugin.Log.Information($"[Probe] {line}");
        try { File.AppendAllText(ProbeFilePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
        catch { /* never let diagnostics break the game */ }
    }

    // Diagnostic: list every currently visible addon's internal name — used to
    // discover window names (e.g. Flotpassant's "Resource Control" window).
    public static void ListVisibleAddons()
    {
        var mgr = RaptureAtkUnitManager.Instance();
        if (mgr == null) return;

        ProbeLog("Visible addons:");
        ref var list = ref mgr->AllLoadedUnitsList;
        for (var i = 0; i < list.Count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit == null || !unit->IsVisible) continue;
            ProbeLog($"  {unit->NameString}");
        }
        Plugin.ChatGui.Print($"[DiademGatherer] Visible addons dumped to {ProbeFilePath}");
    }

    // Diagnostic: dump an addon's AtkValues to the plugin log. Used to reverse
    // the HWDSupply (Flotpassant appraisal) and scrip-shop windows so their
    // callbacks can be automated. Run /diadem probe <AddonName> with the
    // ── HWDGathereInspect (Flotpassant) ────────────────────────────────────────
    // Buttons (from live enumeration): 8 = Auto-submit, 10 = Request Inspection,
    // 3/4/5 = job-tab radio buttons. Active tab readable at AtkValues[1].

    public const uint InspectAutoSubmitNode = 8;
    public const uint InspectInspectionNode = 10;
    public static readonly uint[] InspectTabRadioNodes = { 3, 4, 5 };

    // Accumulated Firmament score for the tab currently selected in the appraisal
    // window (the per-class 500,000 cap). Index 3, confirmed live via
    // `/diadem score`: tab 0 (Miner) read 391,959 and tab 1 (Botanist) 214,816,
    // matching the on-screen values. Note this is NOT the same index as
    // HWDSupply's own score (24) — the two windows lay out differently.
    //
    // Returns -1 when it can't be read, and callers treat -1 as "no cap known", so
    // a bad read can never stop the bot by mistake — it just enforces no cap.
    private const int InspectScoreIdx = 3;

    public static int InspectAccumulatedScore()
    {
        var addon = GetAddon("HWDGathereInspect");
        if (addon == null || !addon->IsVisible || addon->AtkValuesCount <= InspectScoreIdx) return -1;
        try
        {
            var v = (int)addon->AtkValues[InspectScoreIdx].UInt;
            // A score is 0..500,000; anything else means we're reading the wrong
            // field, and we'd rather enforce nothing than enforce nonsense.
            return v >= 0 && v <= 500_000 ? v : -1;
        }
        catch { return -1; }
    }

    // Currently selected tab (0 = miner, 1 = botanist, 2 = fisher); -1 if closed.
    public static int InspectActiveTab()
    {
        var addon = GetAddon("HWDGathereInspect");
        if (addon == null || !addon->IsVisible || addon->AtkValuesCount < 2) return -1;
        return (int)addon->AtkValues[1].UInt;
    }

    // Whether a button component is currently enabled (e.g. Auto-submit greys
    // out when there is nothing to certify on the tab).
    public static bool IsButtonEnabled(string addonName, uint nodeId)
    {
        var addon = GetAddon(addonName);
        if (addon == null) return false;
        var node = addon->GetNodeById(nodeId);
        if (node == null || (ushort)node->Type < 1000) return false; // must be a component node
        var comp = ((AtkComponentNode*)node)->Component;
        if (comp == null) return false;
        return ((AtkComponentButton*)comp)->IsEnabled;
    }

    // Replay a button node's own click event.
    public static bool ClickButton(string addonName, uint nodeId)
    {
        var addon = GetAddon(addonName);
        if (addon == null) return false;

        var node = addon->GetNodeById(nodeId);
        if (node == null || (ushort)node->Type < 1000) return false; // must be a component node

        var evt = node->AtkEventManager.Event;
        if (evt == null) return false;

        var data = stackalloc AtkEventData[1];
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, data);
        Plugin.Log.Debug($"[DiademGatherer] Clicked node {nodeId} on {addonName} " +
                         $"(evt={evt->State.EventType}, param={evt->Param}).");
        return true;
    }

    // ── HWDSupply (Potkin — collectable turn-in) ───────────────────────────────
    // Verified live:
    //   tab switch  = FireCallback(true, 0, classIndex)   (0..7 = CRP..CUL)
    //   hand in row = FireCallback(true, 1, rowIndex)  → "Item Request" window
    // Item rows start at AtkValues[91], stride 19; row+18 = count-in-bag.
    private const int SupplyRowBase   = 91;
    private const int SupplyCountOff  = 18; // count-in-bag within a row

    private const int SupplyScoreIdx = 24; // accumulated score of the selected class tab

    public static void SupplySelectClass(int classIndex)
        => FireCallback("HWDSupply", true, 0, classIndex);

    // Live scrip total from HWDSupply AtkValues[2] ("805/10,000"); -1 if the
    // window isn't open. Authoritative during turn-in (updates as you hand in).
    public static int SupplyScripCount()
    {
        var addon = GetAddon("HWDSupply");
        if (addon == null || !addon->IsVisible || addon->AtkValuesCount <= 2) return -1;
        try
        {
            var v = addon->AtkValues[2];
            if (v.Type != AtkValueType.String) return -1;
            var s = MemoryHelper.ReadStringNullTerminated((nint)v.String.Value);
            var slash = s.IndexOf('/');
            var num = (slash > 0 ? s[..slash] : s).Replace(",", "").Trim();
            return int.TryParse(num, out var n) ? n : -1;
        }
        catch { return -1; }
    }

    // Accumulated score of the class tab currently shown; -1 if unreadable.
    public static int SupplyAccumulatedScore()
    {
        var addon = GetAddon("HWDSupply");
        if (addon == null || !addon->IsVisible || addon->AtkValuesCount <= SupplyScoreIdx) return -1;
        try { return (int)addon->AtkValues[SupplyScoreIdx].UInt; }
        catch { return -1; }
    }

    public static bool SupplyHandIn(int rowIndex)
        => FireCallback("HWDSupply", true, 1, rowIndex);

    // The "Item Request" / "Hand Over" confirmation window. Its internal name
    // varies by context, so probe the known candidates.
    private static readonly string[] HandInAddons = { "Request", "RequestHandIn" };

    public static string? OpenHandInAddon()
    {
        foreach (var n in HandInAddons)
            if (IsVisible(n)) return n;
        return null;
    }

    public static bool RequestHandInOpen() => OpenHandInAddon() != null;

    // "Hand Over" button. Node 3 is the standard confirm for this window;
    // fall back to a callback if the node isn't clickable.
    public static bool RequestHandInConfirm()
    {
        var name = OpenHandInAddon();
        if (name == null) return false;
        return FireCallback(name, true, 0);
    }

    // Does row 0 hold a collectable whose name contains `contains`? Guards
    // against handing in the wrong item after a tab switch.
    public static bool SupplyRow0NameContains(string contains)
    {
        var addon = GetAddon("HWDSupply");
        if (addon == null || !addon->IsVisible) return false;
        var idx = SupplyRowBase + 2; // name field
        if (addon->AtkValuesCount <= idx) return false;
        try
        {
            var v = addon->AtkValues[idx];
            if (v.Type != AtkValueType.String) return false;
            var name = MemoryHelper.ReadStringNullTerminated((nint)v.String.Value);
            return name.Contains(contains, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ── HWDLottery (Lizbeth — Kupo of Fortune) ─────────────────────────────────
    // The board's 4 scratch hexagons are type-1005 button components (node dump):
    // ids 17..20, ordered left-to-right by X — 17 (x25) 18 (x142) 19 (x201)
    // 20 (x210). A scratch = clicking the hexagon node (FireCallback ignored the
    // index and always hit the leftmost). Close is node 36. The "kupo voucher
    // required (N remaining)" prompt is a normal SelectYesno.
    public static bool LotteryOpen() => IsVisible("HWDLottery");

    // Hexagon node ids, left-to-right. Index 0 (node 17) is the leftmost cell
    // we never scratch.
    public static readonly uint[] LotteryCellNodes = { 17, 18, 19, 20 };

    // Scratch hexagon `cellIndex` (0 = leftmost). Each hexagon is a 1005 wrapper
    // whose OWN event is a timeline no-op; the real scratch is the child Button's
    // ButtonClick (params 20..23), which the addon acts on — so we find that
    // child and replay its ButtonClick specifically.
    public static bool LotteryScratch(int cellIndex)
    {
        if (cellIndex < 0 || cellIndex >= LotteryCellNodes.Length) return false;
        var addon = GetAddon("HWDLottery");
        if (addon == null) return false;
        var node = addon->GetNodeById(LotteryCellNodes[cellIndex]);
        if (node == null || (ushort)node->Type < 1000) return false;
        var comp = ((AtkComponentNode*)node)->Component;
        if (comp == null) return false;

        var uld = &comp->UldManager;
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var child = uld->NodeList[i];
            if (child == null) continue;
            var evt = child->AtkEventManager.Event;
            if (evt == null || evt->State.EventType != AtkEventType.ButtonClick) continue;
            var data = stackalloc AtkEventData[1];
            addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, data);
            return true;
        }
        return false;
    }

    // Close button = node 36; replay its own registered event (the recording
    // confirmed Close is a real ButtonClick, not an addon callback).
    public static bool LotteryClose() => ClickButton("HWDLottery", 36);

    // A card is "scratched" once any of the 5 cell values (AtkValues[32..36])
    // is non-zero — they are all 0 on a fresh, unscratched card.
    public static bool LotteryScratched()
    {
        var addon = GetAddon("HWDLottery");
        if (addon == null || !addon->IsVisible || addon->AtkValuesCount <= 36) return false;
        for (var i = 32; i <= 36; i++)
        {
            try { if (addon->AtkValues[i].UInt != 0) return true; }
            catch { /* ignore */ }
        }
        return false;
    }

    // ── ShopExchangeCurrency (Enie) ────────────────────────────────────────────
    // Layout (verified from probes + live buy test):
    //   AtkValues[3]   = item count
    //   AtkValues[90…] = contiguous run of item-name strings, one per item
    //   Buy            = FireCallback(true, 0, itemIndex, quantity) → SelectYesno
    // The item index for the callback is the item's position within that
    // name run (0-based), confirmed by a live purchase.

    // Finds the callback index for an item by name; -1 if not present.
    public static int FindShopItemIndex(string itemName)
    {
        var addon = GetAddon("ShopExchangeCurrency");
        if (addon == null || !addon->IsVisible) return -1;

        int count = addon->AtkValuesCount > 3 ? (int)addon->AtkValues[3].UInt : 0;
        if (count <= 0) return -1;

        // Locate the matching name string, then walk back to the start of the
        // contiguous string run it belongs to.
        int match = -1;
        for (var i = 4; i < addon->AtkValuesCount; i++)
        {
            var v = addon->AtkValues[i];
            if (v.Type != AtkValueType.String) continue;
            string text;
            try { text = MemoryHelper.ReadStringNullTerminated((nint)v.String.Value); }
            catch { continue; }
            if (text.Contains(itemName, StringComparison.OrdinalIgnoreCase)) { match = i; break; }
        }
        if (match < 0) return -1;

        var runStart = match;
        while (runStart > 0
               && addon->AtkValues[runStart - 1].Type == AtkValueType.String
               && match - (runStart - 1) < count)
            runStart--;

        return match - runStart;
    }

    public static bool ShopBuy(int itemIndex, int quantity)
        => FireCallback("ShopExchangeCurrency", true, 0, itemIndex, quantity);

    // The "Gear/Furnishings" scrip shop is tabbed (Weapons/Armor/Accessories/
    // Others) and only the active tab's items are exposed to ShopBuy. The tabs
    // are RadioButton components; collect their node IDs in list order.
    private static List<uint> ShopCategoryNodes()
    {
        var list = new List<uint>();
        var addon = GetAddon("ShopExchangeCurrency");
        if (addon == null) return list;

        var uld = &addon->UldManager;
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || (ushort)node->Type < 1000) continue;
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null) continue;
            var info = (AtkUldComponentInfo*)comp->UldManager.Objects;
            if (info == null) continue;
            if (info->ComponentType == ComponentType.RadioButton)
                list.Add(node->NodeId);
        }
        return list;
    }

    // How many category tabs the open shop has (0 if not tabbed / not open).
    public static int ShopCategoryCount() => ShopCategoryNodes().Count;

    // Switch to the index-th category tab by replaying its radio button's event.
    public static bool ShopSelectCategory(int index)
    {
        var nodes = ShopCategoryNodes();
        if (index < 0 || index >= nodes.Count) return false;
        return ClickButton("ShopExchangeCurrency", nodes[index]);
    }

    // Standard "close window" callback.
    public static void CloseAddon(string name) => FireCallback(name, true, -1);

    // ── Gathering (the node item window) ───────────────────────────────────────
    // Layout from GBR's GatheringReader: 8 item slots start at AtkValues[5],
    // stride 11; each slot's ItemId is at slotBase+1 (0 = empty). Integrity
    // remaining=[109], max=[110]. Gather a slot = FireCallback(true, slot, 0).
    private const int GatherSlotBase     = 5;
    private const int GatherSlotStride   = 11;
    private const int GatherSlotCount    = 8;
    private const int GatherIntegrityIdx = 109;

    // "Repair All" on the dark-matter repair window. Node id verified with
    // `/diadem buttons Repair` if the layout ever changes.
    public static bool RepairAll() => FireCallback("Repair", true, 0);

    public static bool GatheringOpen() => IsVisible("Gathering");

    // Integrity (gathering attempts) left on the open node; -1 if not open.
    public static int GatheringIntegrity()
    {
        var addon = GetAddon("Gathering");
        if (addon == null || !addon->IsVisible || addon->AtkValuesCount <= GatherIntegrityIdx) return -1;
        try { return (int)addon->AtkValues[GatherIntegrityIdx].UInt; }
        catch { return -1; }
    }

    // Slot indices (0..7) that currently hold a gatherable item.
    public static List<int> GatheringItemSlots()
    {
        var list = new List<int>();
        var addon = GetAddon("Gathering");
        if (addon == null || !addon->IsVisible) return list;
        for (var i = 0; i < GatherSlotCount; i++)
        {
            var idx = GatherSlotBase + i * GatherSlotStride + 1; // ItemId within the slot
            if (idx >= addon->AtkValuesCount) break;
            try { if (addon->AtkValues[idx].UInt != 0) list.Add(i); }
            catch { /* ignore */ }
        }
        return list;
    }

    // The item id in a slot (0 if empty/out of range).
    public static uint GatheringSlotItemId(int slot)
    {
        var addon = GetAddon("Gathering");
        if (addon == null || !addon->IsVisible) return 0;
        var idx = GatherSlotBase + slot * GatherSlotStride + 1;
        if (idx < 0 || idx >= addon->AtkValuesCount) return 0;
        try { return addon->AtkValues[idx].UInt; }
        catch { return 0; }
    }

    // Gather item slot `index` (0..7) from the open node.
    public static bool GatherSlot(int index)
        => FireCallback("Gathering", true, index, 0);

    // Finds the real service NPC by name: a targetable Event NPC, nearest to
    // the player. Service NPCs (Lizbeth, …) often have an INVISIBLE proxy of
    // the same name — filtering to targetable EventNpc avoids grabbing it.
    public static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNpc(string name)
    {
        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDist = float.MaxValue;
        var pos = Plugin.ObjectTable.LocalPlayer?.Position ?? default;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc) continue;
            if (!obj.IsTargetable) continue;
            if (!obj.Name.TextValue.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;

            var d = System.Numerics.Vector3.Distance(pos, obj.Position);
            if (d < bestDist) { best = obj; bestDist = d; }
        }
        return best;
    }

    // Gathering nodes specifically: GBR uses OpenObjectInteraction for these,
    // which is also what handles dismounting you as part of opening the node.
    public static void OpenNodeInteraction(nint objectAddress)
    {
        var ts = TargetSystem.Instance();
        if (ts == null || objectAddress == nint.Zero) return;
        ts->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)objectAddress);
    }

    public static void InteractWith(nint objectAddress)
    {
        var ts = TargetSystem.Instance();
        if (ts == null || objectAddress == nint.Zero) return;
        var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)objectAddress;
        // Target first, then interact — some NPCs (Lizbeth) open a flavour line
        // instead of their service if interacted with untargeted.
        ts->Target = obj;
        ts->InteractWithObject(obj, false);
    }
}
