using Dalamud.Configuration;
using System;
using System.Collections.Generic;
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
    public bool OpenWithGlamourDresser { get; set; } = true;

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

    /// <summary>
    /// Tag rows whose item is a piece of an outfit held in the dresser. Defaults on: knowing a
    /// garment is part of a set changes what discarding it costs, and nothing else on the row
    /// says so.
    ///
    /// A toggle rather than a fixture because it is a busy tag by nature - about half the items
    /// in a full dresser are part of some outfit - and whether that reads as useful or as noise
    /// is a matter of taste. Switching it off leaves the tooltip's "Part of ..." line, so the
    /// answer is still one hover away.
    /// </summary>
    public bool TagOutfitComponents { get; set; } = true;

    /// <summary>
    /// Show hidden items anyway, tagged as hidden, so they can be right-clicked back into the
    /// results. This does not merely append them: it switches hiding off wholesale, including
    /// for the shared-model test, so what you see is exactly the unfiltered picture.
    ///
    /// The master switch, and the one piece of this that is account-wide: it is a preference
    /// about how results are displayed, not a record of what any character has hidden.
    /// <see cref="CharacterHides.RevealedSlots"/> does the same thing one section at a time.
    /// </summary>
    public bool ShowHiddenItems { get; set; } = false;

    private Dictionary<ulong, CharacterHides> hidesByCharacter = [];

    /// <summary>
    /// What each character has hidden, keyed on their content id - the same identity the
    /// dresser cache files are named for.
    ///
    /// Per character, not per account, for the same reason the dresser cache is: the Glamour
    /// Dresser belongs to the character, so an item one character has no room for is not a
    /// statement about anyone else's dresser. Kept inside the config rather than in its own
    /// file because it is a handful of ids, and because Save() is what bumps
    /// <see cref="Revision"/> and so what makes the results rebuild on a hide.
    ///
    /// The setter tolerates a null out of the deserialiser: an older config has no such key
    /// and keeps the initialiser, but a hand-edited or truncated one could hand us null, and a
    /// null here would throw on every draw.
    /// </summary>
    public Dictionary<ulong, CharacterHides> HidesByCharacter
    {
        get => hidesByCharacter;
        set => hidesByCharacter = value ?? [];
    }

    /// <summary>
    /// 0 while logged out, exactly as DresserScanner reads it. Everything below treats 0 as
    /// "no character", which is the honest answer: there is no dresser on screen to hide from.
    /// </summary>
    private static ulong CurrentContentId
        => Plugin.ClientState.IsLoggedIn ? Plugin.PlayerState.ContentId : 0;

    /// <summary>The logged-in character's record, or null if there is none. Never creates one.</summary>
    private CharacterHides? Current
    {
        get
        {
            var contentId = CurrentContentId;
            return contentId != 0 && hidesByCharacter.TryGetValue(contentId, out var hides) ? hides : null;
        }
    }

    /// <summary>
    /// The logged-in character's record, created if this is their first hide. Null while
    /// logged out - there is nothing to key a record on, and nothing on screen to hide.
    /// </summary>
    private CharacterHides? CurrentForWrite()
    {
        var contentId = CurrentContentId;
        if (contentId == 0)
            return null;

        if (!hidesByCharacter.TryGetValue(contentId, out var hides))
            hidesByCharacter[contentId] = hides = new CharacterHides();

        return hides;
    }

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

    /// <summary>How many items the logged-in character has hidden. 0 while logged out.</summary>
    public int HiddenCount => Current?.HiddenItemIds.Count ?? 0;

    /// <summary>How many of their sections are revealing them. 0 while logged out.</summary>
    public int RevealedSlotCount => Current?.RevealedSlots.Count ?? 0;

    public bool IsHidden(uint itemId) => Current?.HiddenItemIds.Contains(itemId) ?? false;

    /// <summary>
    /// Hides or unhides one item for the logged-in character and persists it. Saving here is
    /// what bumps <see cref="Revision"/>, which is how the results rebuild themselves the
    /// moment the context menu closes - the row has to disappear, and so does whatever it was
    /// the only remaining match for.
    /// </summary>
    public void SetHidden(uint itemId, bool hidden)
    {
        var hides = CurrentForWrite();
        if (hides == null)
            return;

        var changed = hidden ? hides.HiddenItemIds.Add(itemId) : hides.HiddenItemIds.Remove(itemId);
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
        var hides = CurrentForWrite();
        if (hides == null)
            return;

        var changed = revealed ? hides.RevealedSlots.Add(slotCategory) : hides.RevealedSlots.Remove(slotCategory);
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
        var hides = Current;
        if (hides == null || (hides.HiddenItemIds.Count == 0 && hides.RevealedSlots.Count == 0))
            return;

        hides.HiddenItemIds.Clear();

        // The per-section reveals go with them. A reveal is an instruction about hidden items,
        // and leaving one armed would silently change what a section shows the next time
        // something in it is hidden.
        hides.RevealedSlots.Clear();

        Save();
    }
}

/// <summary>
/// One character's hidden items and the sections currently showing them. Both are per
/// character for the same reason: they describe that character's dresser.
/// </summary>
[Serializable]
public class CharacterHides
{
    /// <summary>
    /// Hidden dresser entries, keyed on the <b>raw</b> ItemId - so an HQ entry (stored at
    /// ItemId + 1,000,000) hides independently of its NQ twin, which is what the dresser shows
    /// and therefore what the user right-clicked.
    /// </summary>
    public HashSet<uint> HiddenItemIds { get; set; } = [];

    /// <summary>
    /// Slot categories ("Feet", "Body", ...) showing their hidden items, set by right-clicking
    /// the section header. Persisted rather than session state: it changes what is on screen,
    /// and state that quietly resets on a relog is state the user has to notice has gone.
    ///
    /// Keyed on the display name because that is the only identity a slot category has - it is
    /// produced by MainWindow.GetSlotName and never stored anywhere else.
    /// </summary>
    public HashSet<string> RevealedSlots { get; set; } = [];
}
