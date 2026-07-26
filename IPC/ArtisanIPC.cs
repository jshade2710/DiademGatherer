using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace DiademGatherer.IPC;

// Artisan IPC — labels verified against PunishXIV/Artisan IPC.cs:
//   Artisan.CraftItem(ushort recipeId, int amount)  (action)
//   Artisan.IsBusy() → bool
//   Artisan.SetStopRequest(bool) (action)
public sealed class ArtisanIPC : IDisposable
{
    private readonly ICallGateSubscriber<ushort, int, object>? _craftItem;
    private readonly ICallGateSubscriber<bool>? _isBusy;
    private readonly ICallGateSubscriber<bool, object>? _setStopRequest;

    public ArtisanIPC(IDalamudPluginInterface pi)
    {
        _craftItem      = pi.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem");
        _isBusy         = pi.GetIpcSubscriber<bool>("Artisan.IsBusy");
        _setStopRequest = pi.GetIpcSubscriber<bool, object>("Artisan.SetStopRequest");
    }

    // Live probe — subscribers always construct; only invoking proves Artisan is there.
    public bool IsAvailable
    {
        get
        {
            try { _isBusy!.InvokeFunc(); return true; }
            catch { return false; }
        }
    }

    public bool IsBusy()
    {
        try { return _isBusy?.InvokeFunc() ?? false; }
        catch { return false; }
    }

    public bool CraftItem(ushort recipeId, int amount)
    {
        try { _craftItem?.InvokeAction(recipeId, amount); return true; }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[DiademGatherer] Artisan.CraftItem failed");
            return false;
        }
    }

    public void StopCrafting()
    {
        try { _setStopRequest?.InvokeAction(true); }
        catch { /* ignore */ }
    }

    public void Dispose() { }
}
