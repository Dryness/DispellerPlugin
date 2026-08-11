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

        // Runs before anything reads a setting. A version 1 config kept everything but the hides
        // account-wide, and without this each character on file would silently pick up the
        // shipped defaults instead of what the user had chosen.
        Configuration.Migrate();

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
    /// Shuts both windows on logout. Everything they draw belongs to the character being left:
    /// the caches are cleared on the way out, so a window left open at the title screen shows an
    /// empty result and a prompt to open a dresser that is no longer reachable.
    ///
    /// Closed rather than hidden, and not reopened on the next login. A window that reappears by
    /// itself is a surprise, and "open with the Glamour Dresser" already covers the case where
    /// someone wants it back without asking.
    ///
    /// The settings window goes too, and now has to: every setting belongs to the character being
    /// left, so once they are gone there is nothing for it to edit. It says as much if reopened
    /// from the installer's cog at the title screen.
    /// </summary>
    private void OnLogout(int type, int code)
    {
        MainWindow.IsOpen = false;
        ConfigWindow.IsOpen = false;
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
