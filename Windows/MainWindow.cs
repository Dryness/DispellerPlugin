using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Excel.Sheets;
using Dispeller.Services;

// yea i used emojis bc i wanted to be cute and funny so what 

namespace Dispeller.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private List<SharedModelGroup>? sharedGroups;
    private string statusMessage = "Open your Glamour Dresser to get started!";
    private int lastGeneration = DresserScanner.Generation;
    private int lastConfigRevision = Configuration.Revision;
    private bool collapseAllOnNextDraw = false;

    public MainWindow(Plugin plugin)
        : base("Dispeller Continued - Shared Model Analyzer", ImGuiWindowFlags.NoScrollbar)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(1.5f, 1),
            Click = _ => plugin.ToggleConfigUi(),
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("Settings");
                ImGui.EndTooltip();
            },
        });

        Plugin.ClientState.Login += OnLogin;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        plugin.DresserScanner.DresserOpened += OnDresserOpened;
        plugin.DresserScanner.DresserClosed += OnDresserClosed;
    }

    public void Dispose()
    {
        Plugin.ClientState.Login -= OnLogin;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        plugin.DresserScanner.DresserOpened -= OnDresserOpened;
        plugin.DresserScanner.DresserClosed -= OnDresserClosed;
    }

    /// <summary>
    /// Hides - never closes for good. <c>/dispeller</c> and the installer's button both bring
    /// it straight back, so this is safe to leave on.
    /// </summary>
    private void OnTerritoryChanged(uint territory)
    {
        if (plugin.Configuration.HideOnZoneChange)
            IsOpen = false;
    }

    // Both of these are driven by DresserScanner's edge-triggered events, so the window only
    // moves at the moment the dresser opens or closes. Reacting to "the dresser is open"
    // every frame instead would make the window impossible to close while standing at one.
    private void OnDresserOpened()
    {
        if (plugin.Configuration.OpenWithGlamourDresser)
            IsOpen = true;
    }

    private void OnDresserClosed()
    {
        // Gated on the parent as well as on its own toggle. Hiding on the way out is a
        // sub-option of opening on the way in, and the settings window only offers it while
        // the parent is on - so it must not keep acting once the parent is switched off.
        if (plugin.Configuration.OpenWithGlamourDresser && plugin.Configuration.HideWhenLeavingGlamourDresser)
            IsOpen = false;
    }

    /// <summary>
    /// Expanded sections are session state and shouldn't carry across a login. ImGui keeps
    /// header state in the window's storage for as long as the game runs, so it has to be
    /// collapsed deliberately - it won't lapse on its own.
    ///
    /// The cache itself is not touched here: DresserScanner watches the logged-in character
    /// and swaps in that character's saved copy on its own.
    /// </summary>
    private void OnLogin()
    {
        collapseAllOnNextDraw = true;
    }

    public override void Draw()
    {
        // The cache re-reads itself while the dresser is open. Rebuild when its contents
        // have actually changed - opening the dresser, changing its view, depositing or
        // retrieving - so the results never quietly describe an older read. A settings change
        // rebuilds too: cheaper than working out which settings the results depend on, and it
        // cannot leave stale results on screen.
        if (DresserScanner.Generation != lastGeneration || Configuration.Revision != lastConfigRevision)
            BuildGroups();

        // Pink gradient header
        DrawHeader();
        
        ImGui.Spacing();

        // Status message
        DrawStatus();

        // Cached-data notice
        DrawCachedNotice();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Results display, sized to leave room for the footer
        var footerHeight = GetFooterHeight();
        DrawResults(footerHeight);

        // Footer, pinned to the bottom edge of the window. Without this the
        // footer is pushed past the bottom and — because the window is
        // NoScrollbar — is only reachable with the mouse wheel.
        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - footerHeight);
        DrawFooter();
    }

    /// <summary>
    /// Height of everything DrawFooter draws: the separator, its manual 10px
    /// offset, and one line of text.
    /// </summary>
    private static float GetFooterHeight()
        => 1 + ImGui.GetStyle().ItemSpacing.Y + 10 + ImGui.GetTextLineHeight();

    private void DrawHeader()
    {
        // The game font has no emoji, so the original sparkles rendered as nothing at all.
        // SeIconChar glyphs are drawn from the game's own font and scale with it.
        UiStyle.DrawHeaderBand($"{(char)SeIconChar.Hyadelyn} Dispeller {(char)SeIconChar.Hyadelyn}");
    }

    private void DrawStatus()
    {
        var centerPos = (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(statusMessage).X) / 2;
        ImGui.SetCursorPosX(centerPos);
        
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.BrightWhite);
        ImGui.TextUnformatted(statusMessage);
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// Says so when the results were built from the copy saved on disk rather than from a
    /// live read. Evaluated every frame rather than at scan time, so it clears the moment the
    /// dresser is opened and the cache is confirmed.
    /// </summary>
    private void DrawCachedNotice()
    {
        if (!DresserScanner.IsFromSavedCache)
            return;

        var message = $"Cached from {DresserScanner.SavedAt.ToLocalTime():d MMM yyyy, HH:mm} - open your dresser to refresh";
        var centerPos = (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(message).X) / 2;
        ImGui.SetCursorPosX(centerPos);

        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.LightPurple);
        ImGui.TextUnformatted(message);
        ImGui.PopStyleColor();
    }

    private void DrawResults(float footerHeight)
    {
        // Nothing to show yet - DrawStatus already says what state the scan is in.
        if (sharedGroups == null || sharedGroups.Count == 0)
            return;

        // A negative height means "content region avail minus this much", which keeps
        // the scrolling results area clear of the footer below it.
        var height = -(footerHeight + ImGui.GetStyle().ItemSpacing.Y);

        // ImRaii.Child ends the child unconditionally - ImGui requires EndChild() even
        // when BeginChild() returns false (unlike Begin/End on popups and menus).
        using var child = ImRaii.Child("Results", new Vector2(0, height), false);
        if (!child)
            return;

        foreach (var group in sharedGroups.Where(g => g.Items.Count > 0))
        {
            DrawSharedGroup(group);
            ImGui.Spacing();
        }

        // Cleared only once headers have actually been drawn, so a login with the window
        // shut - or with no results yet - still collapses them when they next appear.
        collapseAllOnNextDraw = false;
    }

    private void DrawSharedGroup(SharedModelGroup group)
    {
        // Keyed on the slot category alone. Including the item count made the ID change
        // whenever the dresser did, so ImGui saw a new widget and collapsed it.
        using var id = ImRaii.PushId(group.SlotCategory);

        // Get color based on slot category
        var groupColor = GetColorForSlot(group.SlotCategory);
        ImGui.PushStyleColor(ImGuiCol.Header, groupColor);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, groupColor);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, groupColor);
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.AshBlack);

        // Everything after ### is the ID, everything before it is drawn - so the count can
        // change in the label without ImGui treating it as a different header and losing
        // whether the user had it expanded.
        var headerText = $"{group.SlotCategory} ({group.Items.Count} items)###header";

        if (collapseAllOnNextDraw)
            ImGui.SetNextItemOpen(false, ImGuiCond.Always);

        if (ImGui.CollapsingHeader(headerText))
        {
            ImGui.PopStyleColor(4);

            DrawModelRuns(group, groupColor);
        }
        else
        {
            ImGui.PopStyleColor(4);
        }
    }

    /// <summary>
    /// Draws a slot's items as runs of a shared model, bracketing each run with a thin
    /// vertical bar in the slot's own colour. Items are sorted by model ID when the scan
    /// builds the group, so a run is always a contiguous span.
    /// </summary>
    private void DrawModelRuns(SharedModelGroup group, Vector4 groupColor)
    {
        var drawList = ImGui.GetWindowDrawList();
        var barColor = ImGui.ColorConvertFloat4ToU32(new Vector4(groupColor.X, groupColor.Y, groupColor.Z, 0.55f));
        var spacing = ImGui.GetStyle().ItemSpacing.Y;

        for (var start = 0; start < group.Items.Count; )
        {
            var end = start;
            while (end < group.Items.Count && group.Items[end].ModelId == group.Items[start].ModelId)
                end++;

            var origin = ImGui.GetCursorScreenPos();

            for (var i = start; i < end; i++)
                DrawItem(group.Items[i], group.Items);

            // The cursor now sits at the start of the next row, one ItemSpacing below
            // the run's last row.
            var runBottom = ImGui.GetCursorScreenPos().Y - spacing;
            drawList.AddRectFilled(
                new Vector2(origin.X + 8, origin.Y),
                new Vector2(origin.X + 10, runBottom),
                barColor,
                1.0f);

            if (end < group.Items.Count)
                ImGui.Spacing();

            start = end;
        }
    }

    private void DrawItem(SharedModelItem item, List<SharedModelItem> allItemsInSlot)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20);

        var matchingModelCount = allItemsInSlot.Count(i => i.ModelId == item.ModelId);

        // Try to get icon. The dresser's own IconId can be one the game cannot resolve for
        // HQ entries, so fall back to the icon the Item sheet gives for the base item.
        var icon = GetIcon((uint)item.IconId) ?? GetIcon(GetItemIconFromLumina(item.ItemId));
        if (icon != null)
        {
            ImGui.Image(icon.Handle, new Vector2(32, 32));
            ImGui.SameLine();
        }
        else
        {
            // Draw a placeholder if icon is missing
            ImGui.Dummy(new Vector2(32, 32));
            ImGui.SameLine();
        }

        // Get display name - fallback if empty. HQ entries carry the game's own HQ glyph,
        // since the Item sheet name is identical for both qualities.
        var displayName = string.IsNullOrWhiteSpace(item.Name) ? $"Item #{item.ItemId}" : item.Name;
        if (item.IsHq)
            displayName = $"{displayName} {(char)SeIconChar.HighQuality}";

        // No per-row "shared model" marker: the scan only keeps items that already share a
        // model, so every row would carry one. The vertical bar down each run is what shows
        // which rows group together.
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.BrightWhite);
        ImGui.TextUnformatted(displayName);
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted($"{matchingModelCount} items match model: {item.ModelId}");
            ImGui.EndTooltip();
        }

        // Draw dye slot indicators (circles) - similar to Glamaholic
        if (item.DyeCount > 0)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5);
            
            var drawList = ImGui.GetWindowDrawList();
            var basePos = ImGui.GetCursorScreenPos();
            var circleRadius = 4.0f;
            var circleSpacing = 8.0f;
            // Use white/light gray for empty circles (visible on dark background)
            var circleColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.85f, 0.85f, 1.0f));
            
            // Draw circles for each dye slot (1 or 2)
            for (int i = 0; i < item.DyeCount; i++)
            {
                var circleCenter = basePos + new Vector2(circleRadius + 2, circleRadius + 2) + new Vector2(i * circleSpacing, 0);
                // Draw empty circle outline (similar to Glamaholic - empty circles indicate available dye slots)
                drawList.AddCircle(circleCenter, circleRadius + 1, circleColor);
            }
            
            // The invisible button both reserves the circles' width on this line and
            // gives them a hover target. Do not advance the cursor past it by hand:
            // ImGui has already wrapped to the next row, so any SetCursorPosX here
            // indents the *following* item instead of this one.
            var circlesWidth = (item.DyeCount * circleSpacing) + 4;
            ImGui.InvisibleButton($"dye_{item.ItemId}", new Vector2(circlesWidth, circleRadius * 2 + 4));

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"{item.DyeCount} dye slot{(item.DyeCount > 1 ? "s" : "")} available");
                ImGui.EndTooltip();
            }
        }

        // Draw Armoire marker if item can be stored in Armoire
        if (item.CanGoInArmoire)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5);
            
            ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.SoftMagenta);
            ImGui.TextUnformatted("[Armoire]");
            ImGui.PopStyleColor();
            
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("This item can be stored in your Armoire instead of the Glamour Dresser!");
                ImGui.EndTooltip();
            }
        }
    }

    /// <summary>
    /// GetFromGameIcon throws IconNotFoundException for an icon the game does not have, so
    /// GetWrapOrDefault never gets the chance to return null. An unresolvable icon must not
    /// take the whole window's Draw() down with it - DrawItem falls back to a blank space.
    ///
    /// Takes a uint, deliberately. The id used to be cast to ushort at the call site, which
    /// meant an out-of-range value wrapped into a valid but unrelated icon instead of
    /// failing: a cane rendered as a pair of boots, and because the lookup succeeded the
    /// fallback to the sheet's own icon never got a chance to run.
    /// </summary>
    private IDalamudTextureWrap? GetIcon(uint id)
    {
        if (id == 0)
            return null;

        // The dresser offsets an HQ entry's icon by 1,000,000, exactly as it offsets its item
        // id - measured in game on 2026-08-08: icons 1038223/1032676/1048001 against sheet
        // icons 38223/32676/48001. Asking for the HQ variant of the base icon gets the game's
        // own HQ treatment rather than a near-miss.
        var isHq = id >= 1_000_000;
        var iconId = isHq ? id - 1_000_000 : id;

        // Anything still out of range is not an icon this can resolve. Return null rather
        // than truncating, so the caller's fallback runs.
        if (iconId > ushort.MaxValue)
            return null;

        try
        {
            return Plugin.TextureProvider
                .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(iconId, itemHq: isHq))
                .GetWrapOrDefault();
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"No icon {id}: {ex.Message}");
            return null;
        }
    }

    private void DrawFooter()
    {
        ImGui.Separator();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 10);
        
        var message = "Find shared models in your glamour dresser!";
        var centerPos = (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(message).X) / 2;
        ImGui.SetCursorPosX(centerPos);
        
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.BrightWhite);
        ImGui.TextUnformatted(message);
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// Rebuilds the grouped results from the cache, whenever the cache changes underneath
    /// the window. Opening the dresser, changing its view, depositing or retrieving, and
    /// loading a character's saved copy all land here - there is nothing for a user to press.
    /// </summary>
    private void BuildGroups()
    {
        lastGeneration = DresserScanner.Generation;
        lastConfigRevision = Configuration.Revision;

        try
        {
            var dresserItems = DresserScanner.GetDresserItems();

            if (dresserItems.Count == 0)
            {
                statusMessage = "Open your Glamour Dresser at least once so it can be read!";
                sharedGroups = null;
                return;
            }

            // Deduplicate by ItemId. Do NOT key on Slot: Slot identifies an outfit set, not a
            // dresser position - an "Attire" bundle and all nine of its pieces share one Slot,
            // so grouping by it collapses whole outfits down to a single garment.
            var uniqueItems = dresserItems
                .GroupBy(item => item.ItemId)
                .Select(g => g.First())
                .ToList();

            // Outfit bundles ("... Attire") have no equipment slot of their own, so they can't
            // be model-matched against garments. Counted here, grouping deferred.
            var outfitCount = uniqueItems.Count(item => GetSlotName(item.ItemId) == "Outfit");

            // Filter out items with unknown slots
            var validItems = uniqueItems
                .Where(item => {
                    var slotName = GetSlotName(item.ItemId);
                    return !string.IsNullOrEmpty(slotName) && slotName != "Unknown Slot" && slotName != "Outfit";
                })
                .ToList();

            Plugin.Log.Information($"Scan: {dresserItems.Count} raw, {uniqueItems.Count} unique, {validItems.Count} equippable, {outfitCount} outfits");
            foreach (var g in uniqueItems.GroupBy(i => GetSlotName(i.ItemId)).OrderBy(g => GetSlotOrder(g.Key)))
                Plugin.Log.Debug($"Scan category {g.Key}: {g.Count()}");

            // First, identify items with shared models by grouping by slot + model
            var itemsWithSharedModels = validItems
                .GroupBy(item => {
                    var slotName = GetSlotName(item.ItemId);
                    var modelId = GetItemModel(item.ItemId);
                    return $"{slotName}-{modelId}";
                })
                .Where(g => g.Count() > 1) // Only groups with matching models
                .SelectMany(g => g) // Flatten back to individual items
                .ToList();

            // Now group by slot category only
            var grouped = itemsWithSharedModels
                .GroupBy(item => GetSlotName(item.ItemId))
                .Select(g => {
                    // Sort items within this slot by model ID so matching models are adjacent,
                    // and put the HQ entry at the head of its run.
                    var sortedItems = g
                        .OrderBy(item => GetItemModel(item.ItemId))
                        .ThenByDescending(item => item.ItemId >= 1_000_000)
                        .Select(item => {
                            // Always get item name from Lumina for accuracy
                            // Dresser name can be incorrect/outdated when dresser updates
                            var itemName = GetItemNameFromLumina(item.ItemId);
                            
                            // Get icon - use from Lumina if dresser icon is invalid
                            var iconId = item.IconId;
                            if (iconId == 0)
                            {
                                iconId = GetItemIconFromLumina(item.ItemId);
                            }
                            
                            // Get dye count from Lumina
                            var dyeCount = GetItemDyeCount(item.ItemId);
                            
                            // Check if item can be stored in Armoire
                            var canGoInArmoire = CanGoInArmoire(item.ItemId);
                            
                            return new SharedModelItem
                            {
                                Name = itemName,
                                ItemId = item.ItemId,
                                IconId = (int)iconId,
                                Slot = item.Slot,
                                ModelId = GetItemModel(item.ItemId),
                                DyeCount = dyeCount,
                                CanGoInArmoire = canGoInArmoire,
                                IsHq = item.ItemId >= 1_000_000
                            };
                        })
                        .ToList();
                    
                    return new SharedModelGroup
                    {
                        ModelId = "", // Not used for slot-based grouping
                        SlotCategory = g.Key,
                        Items = sortedItems
                    };
                })
                .OrderBy(g => GetSlotOrder(g.SlotCategory)) // Sort slots in logical order
                .ToList();

            sharedGroups = grouped;
            var totalItems = grouped.Sum(g => g.Items.Count);
            statusMessage = $"Found {totalItems} items with shared models across {grouped.Count} slot categories!";
        }
        catch (Exception ex)
        {
            statusMessage = $"Error: {ex.Message}";
            sharedGroups = null;
            Plugin.Log.Error(ex, "Error during dresser scan");
        }
    }

    private string GetItemModel(uint itemId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>()!;
        if (!sheet.TryGetRow(BaseItemId(itemId), out var item))
            return "Unknown";

        var model = ModelDetectionService.ExtractModelInfo(item.ModelMain, plugin.Configuration.CountRecoloursAsDuplicates);
        return ModelDetectionService.GetModelIdString(model);
    }

    /// <summary>
    /// The dresser stores HQ entries at ItemId + 1,000,000, but the Item sheet only holds the
    /// base row - so every HQ item failed to resolve and fell through to "Unknown Slot".
    /// Only sheet lookups are normalised; the raw ItemId stays the dedup identity, so an HQ
    /// item and its NQ twin remain two dresser entries, which is what the game shows.
    /// </summary>
    private static uint BaseItemId(uint itemId)
        => itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;

    private string GetSlotName(uint itemId)
    {
        if (itemId == 0)
            return "Unknown Slot";
            
        var sheet = Plugin.DataManager.GetExcelSheet<Item>()!;
        if (!sheet.TryGetRow(BaseItemId(itemId), out var item))
            return "Unknown Slot";

        if (!item.EquipSlotCategory.IsValid)
            return "Unknown Slot";

        // Outfit bundles point at EquipSlotCategory row 0, where every slot field is zero.
        // They are real dresser entries ("Bunny Attire", "The Emperor's New Attire"), not junk.
        if (item.EquipSlotCategory.RowId == 0)
            return "Outfit";

        var category = item.EquipSlotCategory.Value;

        // Check each slot category in priority order
        if (category.MainHand > 0) return "Main Hand";
        if (category.OffHand > 0) return "Off Hand";
        if (category.Head > 0) return "Head";
        if (category.Body > 0) return "Body";
        if (category.Gloves > 0) return "Gloves";
        if (category.Legs > 0) return "Legs";
        if (category.Feet > 0) return "Feet";
        if (category.Ears > 0) return "Ears";
        if (category.Neck > 0) return "Neck";
        if (category.Wrists > 0) return "Wrists";
        if (category.FingerR > 0 || category.FingerL > 0) return "Ring";

        return "Unknown Slot";
    }

    private string GetItemNameFromLumina(uint itemId)
    {
        if (itemId == 0)
            return "Unknown Item";
            
        var sheet = Plugin.DataManager.GetExcelSheet<Item>()!;
        if (!sheet.TryGetRow(BaseItemId(itemId), out var item))
            return "Unknown Item";
        
        return item.Name.ExtractText();
    }

    private uint GetItemIconFromLumina(uint itemId)
    {
        if (itemId == 0)
            return 0;
            
        var sheet = Plugin.DataManager.GetExcelSheet<Item>()!;
        if (!sheet.TryGetRow(BaseItemId(itemId), out var item))
            return 0;
        
        return item.Icon;
    }

    private byte GetItemDyeCount(uint itemId)
    {
        if (itemId == 0)
            return 0;
            
        var sheet = Plugin.DataManager.GetExcelSheet<Item>()!;
        if (!sheet.TryGetRow(BaseItemId(itemId), out var item))
            return 0;
        
        return item.DyeCount;
    }

    private static HashSet<uint>? armoireItemIds;

    private bool CanGoInArmoire(uint itemId)
    {
        if (itemId == 0)
            return false;

        // Built once. This runs per item, and a linear Any() over the Cabinet sheet made a
        // rebuild cost items x cabinet rows - tolerable when only the Scan button triggered
        // it, not when the results rebuild themselves as the dresser changes.
        armoireItemIds ??= Plugin.DataManager.GetExcelSheet<Cabinet>()!
            .Select(row => row.Item.RowId)
            .ToHashSet();

        return armoireItemIds.Contains(BaseItemId(itemId));
    }

    private int GetSlotOrder(string slotName)
    {
        // Return order value for slot sorting (lower = appears first)
        return slotName switch
        {
            "Main Hand" => 1,
            "Off Hand" => 2,
            "Head" => 3,
            "Body" => 4,
            "Gloves" => 5,
            "Legs" => 6,
            "Feet" => 7,
            "Ears" => 8,
            "Neck" => 9,
            "Wrists" => 10,
            "Ring" => 11,
            "Outfit" => 12,
            _ => 99
        };
    }

    private Vector4 GetColorForSlot(string slotName)
    {
        // Accessories - purple
        if (slotName == "Ears" || slotName == "Neck" || slotName == "Wrists" || slotName == "Ring")
        {
            return UiStyle.LightPurple;
        }
        
        // Main gear - pink/magenta
        if (slotName == "Head" || slotName == "Body" || slotName == "Gloves" || slotName == "Legs" || slotName == "Feet")
        {
            return UiStyle.LightMagenta;
        }
        
        // Weapons - minty green
        if (slotName == "Main Hand" || slotName == "Off Hand")
        {
            return UiStyle.LightMintGreen;
        }
        
        // Default to purple if unknown
        return UiStyle.LightPurple;
    }
}

public class SharedModelGroup
{
    public string ModelId { get; set; } = string.Empty;
    public string SlotCategory { get; set; } = string.Empty;
    public List<SharedModelItem> Items { get; set; } = [];
}

public class SharedModelItem
{
    public string Name { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public int IconId { get; set; }
    public uint Slot { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public byte DyeCount { get; set; }
    public bool CanGoInArmoire { get; set; }
    public bool IsHq { get; set; }
}
