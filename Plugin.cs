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

        // Before anything reads a setting: an existing config needs its account-wide answers
        // copied into each character's record, or they silently pick up the shipped defaults.
        Configuration.Migrate();

        // Started first and left to run. Nothing waits on it - a dresser opened before it
        // finishes just pays the paging cost in the build instead.
        SheetWarmup.Start();

        // Before the windows: MainWindow subscribes to its open/close events.
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

        ClientState.Logout += OnLogout;

        Log.Information($"===Dispeller Continued plugin loaded! Ready to find shared models!===");
    }

    public void Dispose()
    {
        // Asked to stop first, so an unload during a cold start doesn't leave it walking sheets.
        SheetWarmup.Stop();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        ClientState.Logout -= OnLogout;

        WindowSystem.RemoveAllWindows();

        MainWindow.Dispose();
        ConfigWindow.Dispose();
        DresserScanner.Dispose();
        ArmoireScanner.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    /// <summary>
    /// Shuts both windows on logout. Everything they draw belongs to the character being left -
    /// the results, and the settings too, since every setting is that character's.
    ///
    /// Closed rather than hidden, and not reopened on the next login: a window that reappears by
    /// itself is a surprise, and "open with the Glamour Dresser" already covers wanting it back.
    /// </summary>
    private void OnLogout(int type, int code)
    {
        MainWindow.IsOpen = false;
        ConfigWindow.IsOpen = false;
    }

    /// <summary>
    /// <c>/dispeller config</c> is the same door as the installer's cog and the window's own
    /// title-bar button - people reach for whichever they already know.
    /// </summary>
    private void OnCommand(string command, string args)
    {
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
