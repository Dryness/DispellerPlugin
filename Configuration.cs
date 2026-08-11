using Dalamud.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Dispeller;

[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>
    /// 1 kept every setting except the hides account-wide; 2 moved all of them per character.
    /// See <see cref="Migrate"/>, which carries an existing config's answers across.
    /// </summary>
    public int Version { get; set; } = 2;

    // Defaults for a character the plugin has not seen before.
    //
    // These keep the JSON names version 1 wrote, so an existing config loads into them - which is
    // what stops the move to per-character settings resetting anybody. A new character inherits
    // whatever was configured account-wide, and on a fresh install these are simply the shipped
    // defaults, so one path covers both. Nothing reads them directly except record creation and
    // Migrate; every live read goes through the accessors further down.

    [JsonProperty("HideOnZoneChange")] public bool DefaultHideOnZoneChange { get; set; } = false;
    [JsonProperty("OpenWithGlamourDresser")] public bool DefaultOpenWithGlamourDresser { get; set; } = true;
    [JsonProperty("HideWhenLeavingGlamourDresser")] public bool DefaultHideWhenLeavingGlamourDresser { get; set; } = false;
    [JsonProperty("CountRecoloursAsDuplicates")] public bool DefaultCountRecoloursAsDuplicates { get; set; } = true;
    [JsonProperty("TagOutfitComponents")] public bool DefaultTagOutfitComponents { get; set; } = true;
    [JsonProperty("ShowHiddenItems")] public bool DefaultShowHiddenItems { get; set; } = false;

    private Dictionary<ulong, CharacterSettings> settingsByCharacter = [];

    /// <summary>
    /// Every setting and every hide, per character, keyed on content id - the same identity the
    /// dresser cache files are named for. Per character because the Glamour Dresser is: how one
    /// character's results are matched, filtered and displayed says nothing about anyone else's.
    ///
    /// Kept inside the config rather than in its own file because <see cref="Save"/> is what
    /// bumps <see cref="Revision"/>, and that is what rebuilds the results when a setting changes.
    ///
    /// Still written under the old <c>HidesByCharacter</c> key: the record gained fields rather
    /// than changing shape, so an older file deserialises into it untouched, and renaming the key
    /// would throw every existing hide away for cosmetics.
    ///
    /// The setter tolerates a null out of the deserialiser, which a hand-edited or truncated file
    /// could produce; a null here would throw on every draw.
    /// </summary>
    [JsonProperty("HidesByCharacter")]
    public Dictionary<ulong, CharacterSettings> SettingsByCharacter
    {
        get => settingsByCharacter;
        set => settingsByCharacter = value ?? [];
    }

    /// <summary>
    /// Brings an older config forward by copying the account-wide answers into every character
    /// already on file. Without it they would keep their hides but silently pick up the shipped
    /// defaults for everything else.
    ///
    /// Called from the plugin's constructor rather than from a property, because it has to happen
    /// after Dalamud has finished deserialising and exactly once.
    /// </summary>
    public void Migrate()
    {
        if (Version >= 2)
            return;

        foreach (var settings in settingsByCharacter.Values)
            ApplyDefaults(settings);

        Version = 2;
        Save();
        Plugin.Log.Information(
            $"Config migrated to per-character settings for {settingsByCharacter.Count} character(s)");
    }

    private void ApplyDefaults(CharacterSettings settings)
    {
        settings.HideOnZoneChange = DefaultHideOnZoneChange;
        settings.OpenWithGlamourDresser = DefaultOpenWithGlamourDresser;
        settings.HideWhenLeavingGlamourDresser = DefaultHideWhenLeavingGlamourDresser;
        settings.CountRecoloursAsDuplicates = DefaultCountRecoloursAsDuplicates;
        settings.TagOutfitComponents = DefaultTagOutfitComponents;
        settings.ShowHiddenItems = DefaultShowHiddenItems;
    }

    // The live settings. Every call site reads and writes the logged-in character's answer
    // without knowing that is what it is doing.
    //
    // JsonIgnore on all of them: the stored copy lives in the character's record, and letting
    // these serialise would write a second, account-wide copy of every setting back into the file.

    /// <summary>Hide the window on a zone change. Only hides it - <c>/dispeller</c> brings it back.</summary>
    [JsonIgnore]
    public bool HideOnZoneChange
    {
        get => Read(s => s.HideOnZoneChange, DefaultHideOnZoneChange);
        set => Write((s, v) => s.HideOnZoneChange = v, value);
    }

    /// <summary>
    /// Open the window when the Glamour Dresser opens. Edge-triggered on the addon appearing,
    /// never level-triggered, so closing the window by hand while the dresser is still open
    /// leaves it closed.
    /// </summary>
    [JsonIgnore]
    public bool OpenWithGlamourDresser
    {
        get => Read(s => s.OpenWithGlamourDresser, DefaultOpenWithGlamourDresser);
        set => Write((s, v) => s.OpenWithGlamourDresser = v, value);
    }

    /// <summary>
    /// Hide the window again when the Glamour Dresser closes. A sub-option of
    /// <see cref="OpenWithGlamourDresser"/> - it does nothing on its own, so that switching
    /// the parent off cannot leave a stray behaviour running.
    /// </summary>
    [JsonIgnore]
    public bool HideWhenLeavingGlamourDresser
    {
        get => Read(s => s.HideWhenLeavingGlamourDresser, DefaultHideWhenLeavingGlamourDresser);
        set => Write((s, v) => s.HideWhenLeavingGlamourDresser = v, value);
    }

    /// <summary>
    /// Match on the mesh alone, so a recolour of a garment counts as a redundant glamour.
    /// This is the plugin's whole point, so it defaults on; off compares the material/colour
    /// variant too and finds far fewer duplicates.
    /// </summary>
    [JsonIgnore]
    public bool CountRecoloursAsDuplicates
    {
        get => Read(s => s.CountRecoloursAsDuplicates, DefaultCountRecoloursAsDuplicates);
        set => Write((s, v) => s.CountRecoloursAsDuplicates = v, value);
    }

    /// <summary>
    /// Tag rows whose item is a piece of an outfit held in the dresser. Defaults on: knowing a
    /// garment is part of a set changes what discarding it costs, and nothing else on the row
    /// says so.
    ///
    /// A toggle rather than a fixture because it is a busy tag by nature - a large share of a
    /// full dresser belongs to some outfit - and whether that reads as useful or as noise is a
    /// matter of taste. Switching it off leaves the tooltip's "Part of ..." line, so the answer
    /// is still one hover away.
    /// </summary>
    [JsonIgnore]
    public bool TagOutfitComponents
    {
        get => Read(s => s.TagOutfitComponents, DefaultTagOutfitComponents);
        set => Write((s, v) => s.TagOutfitComponents = v, value);
    }

    /// <summary>
    /// Show hidden items anyway, tagged as hidden, so they can be right-clicked back into the
    /// results. This does not merely append them: it switches hiding off wholesale, including
    /// for the shared-model test, so what you see is exactly the unfiltered picture.
    ///
    /// The master switch. <see cref="CharacterSettings.RevealedSlots"/> does the same thing one
    /// section at a time, and this wins over every one of them.
    /// </summary>
    [JsonIgnore]
    public bool ShowHiddenItems
    {
        get => Read(s => s.ShowHiddenItems, DefaultShowHiddenItems);
        set => Write((s, v) => s.ShowHiddenItems = v, value);
    }

    /// <summary>
    /// 0 while logged out, exactly as <see cref="Services.DresserScanner"/> reads it. Everything
    /// below treats 0 as "no character" - there is no dresser on screen to describe.
    /// </summary>
    private static ulong CurrentContentId
        => Plugin.ClientState.IsLoggedIn ? Plugin.PlayerState.ContentId : 0;

    /// <summary>
    /// True when there is a character to read settings for. The settings window asks, because a
    /// toggle that silently refuses to stick is worse than one that is not offered.
    /// </summary>
    [JsonIgnore]
    public static bool HasCharacter => CurrentContentId != 0;

    /// <summary>The logged-in character's record, or null if there is none. Never creates one.</summary>
    private CharacterSettings? Current
    {
        get
        {
            var contentId = CurrentContentId;
            return contentId != 0 && settingsByCharacter.TryGetValue(contentId, out var s) ? s : null;
        }
    }

    /// <summary>
    /// The logged-in character's record, created on first use and seeded from the defaults above.
    /// Null while logged out - there is nothing to key a record on.
    /// </summary>
    private CharacterSettings? CurrentForWrite()
    {
        var contentId = CurrentContentId;
        if (contentId == 0)
            return null;

        if (!settingsByCharacter.TryGetValue(contentId, out var settings))
        {
            settingsByCharacter[contentId] = settings = new CharacterSettings();
            ApplyDefaults(settings);
        }

        return settings;
    }

    /// <summary>Reads one setting, falling back to the default while logged out or before a first write.</summary>
    private T Read<T>(Func<CharacterSettings, T> get, T fallback)
    {
        var settings = Current;
        return settings == null ? fallback : get(settings);
    }

    /// <summary>
    /// Writes one setting and persists it. A write while logged out is dropped rather than
    /// applied to the defaults: those describe a character we have not met, and quietly editing
    /// them from the title screen would change what every future character starts with.
    /// </summary>
    /// <remarks>
    /// Every caller is a property setter, so <see cref="CallerMemberNameAttribute"/> names the
    /// setting rather than this method, and the line <see cref="Save"/> writes names it too.
    /// </remarks>
    private void Write<T>(Action<CharacterSettings, T> set, T value, [CallerMemberName] string setting = "")
    {
        var settings = CurrentForWrite();
        if (settings == null)
            return;

        set(settings, value);
        Save(setting);
    }

    private static int revision;

    /// <summary>
    /// Bumped on every save. MainWindow watches this the same way it watches
    /// <see cref="Services.DresserScanner.Generation"/>, so a setting that changes what the
    /// results should contain rebuilds them without either window knowing about the other.
    ///
    /// A character switch needs no equivalent: the dresser scanner already bumps its own
    /// generation, and that rebuild picks up the new character's settings along the way.
    /// </summary>
    public static int Revision => Volatile.Read(ref revision);

    /// <summary>
    /// Persists the config and bumps the revision. The caller is logged because a setting saved
    /// twice for one click is otherwise invisible - both writes succeed and agree.
    /// </summary>
    public void Save([CallerMemberName] string caller = "")
    {
        Interlocked.Increment(ref revision);
        Plugin.PluginInterface.SavePluginConfig(this);
        Plugin.Log.Debug($"Config saved from {caller} (revision {Revision})");
    }

    /// <summary>How many items the logged-in character has hidden. 0 while logged out.</summary>
    [JsonIgnore]
    public int HiddenCount => Current?.HiddenItemIds.Count ?? 0;

    /// <summary>How many of their sections are revealing them. 0 while logged out.</summary>
    [JsonIgnore]
    public int RevealedSlotCount => Current?.RevealedSlots.Count ?? 0;

    public bool IsHidden(uint itemId) => Current?.HiddenItemIds.Contains(itemId) ?? false;

    /// <summary>
    /// Hides or unhides one item for the logged-in character and persists it. Saving is what
    /// bumps <see cref="Revision"/>, which rebuilds the results the moment the context menu
    /// closes - the row has to disappear, and so does whatever it was the only match for.
    /// </summary>
    public void SetHidden(uint itemId, bool hidden)
    {
        var settings = CurrentForWrite();
        if (settings == null)
            return;

        var changed = hidden ? settings.HiddenItemIds.Add(itemId) : settings.HiddenItemIds.Remove(itemId);
        if (changed)
            Save();
    }

    /// <summary>
    /// Whether one slot's hidden items are on screen - the master switch, or this section's
    /// own. The single question every caller actually wants answered.
    /// </summary>
    public bool ShowsHiddenIn(string slotCategory)
        => ShowHiddenItems || IsSlotRevealed(slotCategory);

    /// <summary>
    /// This section's own reveal, ignoring the master switch - what the section's context menu
    /// is actually able to turn off.
    /// </summary>
    public bool IsSlotRevealed(string slotCategory)
        => Current?.RevealedSlots.Contains(slotCategory) ?? false;

    public void SetSlotRevealed(string slotCategory, bool revealed)
    {
        var settings = CurrentForWrite();
        if (settings == null)
            return;

        var changed = revealed ? settings.RevealedSlots.Add(slotCategory) : settings.RevealedSlots.Remove(slotCategory);
        if (changed)
            Save();
    }

    /// <summary>
    /// Clears the logged-in character's hides only. Another character's are their own dresser's
    /// business, and a button in a window showing this character's results must not reach past
    /// them.
    /// </summary>
    public void UnhideAll()
    {
        var settings = Current;
        if (settings == null || (settings.HiddenItemIds.Count == 0 && settings.RevealedSlots.Count == 0))
            return;

        settings.HiddenItemIds.Clear();

        // The per-section reveals go with them. A reveal is an instruction about hidden items,
        // and leaving one armed would silently change what a section shows the next time
        // something in it is hidden.
        settings.RevealedSlots.Clear();

        Save();
    }
}

/// <summary>
/// One character's settings, hidden items, and the sections currently showing them.
///
/// The field initialisers here are only reached by a record built outside
/// <see cref="Configuration.CurrentForWrite"/> - an older record being deserialised, which
/// <see cref="Configuration.Migrate"/> then overwrites. A record created for a new character is
/// seeded from the config's defaults instead, so the two paths cannot disagree about what a fresh
/// character starts with.
/// </summary>
[Serializable]
public class CharacterSettings
{
    public bool HideOnZoneChange { get; set; } = false;
    public bool OpenWithGlamourDresser { get; set; } = true;
    public bool HideWhenLeavingGlamourDresser { get; set; } = false;
    public bool CountRecoloursAsDuplicates { get; set; } = true;
    public bool TagOutfitComponents { get; set; } = true;
    public bool ShowHiddenItems { get; set; } = false;

    /// <summary>
    /// Hidden dresser entries, keyed on the <b>raw</b> ItemId - so an HQ entry (stored at
    /// ItemId + 1,000,000) hides independently of its NQ twin, which is what the dresser shows
    /// and therefore what the user right-clicked.
    /// </summary>
    public HashSet<uint> HiddenItemIds { get; set; } = [];

    /// <summary>
    /// Slot categories ("Feet", "Body", ...) showing their hidden items, set by right-clicking the
    /// section header. Persisted rather than session state: it changes what is on screen, and
    /// state that quietly resets on a relog is state the user has to notice has gone.
    ///
    /// Keyed on the display name because that is the only identity a slot category has.
    /// </summary>
    public HashSet<string> RevealedSlots { get; set; } = [];
}
