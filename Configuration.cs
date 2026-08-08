using Dalamud.Configuration;
using System;
using System.Threading;

namespace Dispeller;

[Serializable]
public class Configuration : IPluginConfiguration
{
    // 0 was the upstream shape, carrying ShowOnlyWeapons/ShowOnlyClothing. Those were never
    // read by anything and are gone; Dalamud's deserialiser ignores the leftover keys, so an
    // existing config file still loads.
    public int Version { get; set; } = 1;

    /// <summary>
    /// Hide the window on a zone change. Only hides it - <c>/dispeller</c> brings it back.
    /// </summary>
    public bool HideOnZoneChange { get; set; } = false;

    /// <summary>
    /// Open the window when the Glamour Dresser opens. Edge-triggered on the addon appearing,
    /// never level-triggered, so closing the window by hand while the dresser is still open
    /// leaves it closed.
    /// </summary>
    public bool OpenWithGlamourDresser { get; set; } = false;

    /// <summary>
    /// Hide the window again when the Glamour Dresser closes. A sub-option of
    /// <see cref="OpenWithGlamourDresser"/> - it does nothing on its own, so that switching
    /// the parent off cannot leave a stray behaviour running.
    /// </summary>
    public bool HideWhenLeavingGlamourDresser { get; set; } = false;

    /// <summary>
    /// Match on the mesh alone, so a recolour of a garment counts as a redundant glamour.
    /// This is the plugin's whole point, so it defaults on; off compares the material/colour
    /// variant too and finds far fewer duplicates.
    /// </summary>
    public bool CountRecoloursAsDuplicates { get; set; } = true;

    private static int revision;

    /// <summary>
    /// Bumped on every save. MainWindow watches this the same way it watches
    /// <see cref="Services.DresserScanner.Generation"/>, so a setting that changes what the
    /// results should contain rebuilds them without either window knowing about the other.
    /// </summary>
    public static int Revision => Volatile.Read(ref revision);

    public void Save()
    {
        Interlocked.Increment(ref revision);
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
