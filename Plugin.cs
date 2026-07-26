using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using DiademGatherer.IPC;
using DiademGatherer.Windows;

namespace DiademGatherer;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager        CommandManager  { get; private set; } = null!;
    [PluginService] internal static IClientState           ClientState     { get; private set; } = null!;
    [PluginService] internal static IObjectTable           ObjectTable     { get; private set; } = null!;
    [PluginService] internal static ICondition             Condition       { get; private set; } = null!;
    [PluginService] internal static IDataManager           DataManager     { get; private set; } = null!;
    [PluginService] internal static IGameGui               GameGui         { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle        AddonLifecycle  { get; private set; } = null!;
    [PluginService] internal static IFramework             Framework       { get; private set; } = null!;
    [PluginService] internal static IPluginLog             Log             { get; private set; } = null!;
    [PluginService] internal static IChatGui               ChatGui         { get; private set; } = null!;
    [PluginService] internal static IToastGui              ToastGui        { get; private set; } = null!;

    public Configuration   Configuration   { get; private set; }
    public RouteManager    RouteManager    { get; private set; }
    public CraftingManager CraftingManager { get; private set; }

    private readonly NavmeshIPC    _navmesh;
    private readonly ArtisanIPC    _artisan;

    private readonly WindowSystem _windowSystem = new("DiademGatherer");
    private readonly MainWindow   _mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        _navmesh = new NavmeshIPC(PluginInterface);
        _artisan = new ArtisanIPC(PluginInterface);

        RouteManager    = new RouteManager(_navmesh, Configuration, new SkillCaster());
        CraftingManager = new CraftingManager(_artisan, _navmesh, Configuration);

        _mainWindow = new MainWindow(this);
        _windowSystem.AddWindow(_mainWindow);

        PluginInterface.UiBuilder.Draw        += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += () => _mainWindow.IsOpen = true;
        Framework.Update                       += RouteManager.OnFrameworkUpdate;
        Framework.Update                       += CraftingManager.OnFrameworkUpdate;

        CommandManager.AddHandler("/diadem", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Diadem Gatherer window."
        });
    }

    // Everything is driven from the window's own controls; the command just
    // opens it.
    private void OnCommand(string _, string __) => _mainWindow.IsOpen = true;

    public void Dispose()
    {
        RouteManager.Dispose();
        CraftingManager.Dispose();
        Framework.Update -= RouteManager.OnFrameworkUpdate;
        Framework.Update -= CraftingManager.OnFrameworkUpdate;
        CommandManager.RemoveHandler("/diadem");
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _windowSystem.RemoveAllWindows();
        _navmesh.Dispose();
    }
}
