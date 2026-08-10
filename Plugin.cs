using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dispeller.Windows;
using Dispeller.Services;

namespace Dispeller;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/dispeller";

    public Configuration Configuration { get; init; }
    public DresserScanner DresserScanner { get; init; }
    public ArmoireScanner ArmoireScanner { get; init; }

    public readonly WindowSystem WindowSystem = new("DispellerContinued");
    private MainWindow MainWindow { get; init; }
    private ConfigWindow ConfigWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Started first and left to run: it pages the Excel sheets the results are built from into
        // memory on a background thread, which is what the first build of a session spends its time
        // on. Nothing waits on it - if the dresser is opened before it finishes, the build pays the
        // cost itself exactly as it used to.
        SheetWarmup.Start();

        // Built before the windows: MainWindow subscribes to its open/close events.
        DresserScanner = new DresserScanner();

        // The Armoire has no addon of its own to follow, so nothing subscribes to this - the
        // window watches its generation the same way it watches the dresser's.
        ArmoireScanner = new ArmoireScanner();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Dispeller Continued - Find shared models in your glamour dresser! Use \"/dispeller config\" for settings."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        Log.Information($"===Dispeller Continued plugin loaded! Ready to find shared models!===");
    }

    public void Dispose()
    {
        // Asked to stop first, so an unload during a cold start doesn't leave it walking sheets.
        SheetWarmup.Stop();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();

        MainWindow.Dispose();
        ConfigWindow.Dispose();
        DresserScanner.Dispose();
        ArmoireScanner.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // "/dispeller config" is the same door as the installer's cog and the window's own
        // title-bar button - people reach for whichever they already know.
        var argument = args.Trim();

        if (argument.Equals("config", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }

        MainWindow.Toggle();
    }

    public void ToggleMainUi() => MainWindow.Toggle();

    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
