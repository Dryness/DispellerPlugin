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

    /// <summary>
    /// Slot category to force open on the next draw, set when a section's hidden items are
    /// revealed from its context menu. One-shot: claimed by the first draw that sees it.
    /// </summary>
    private string? expandOnNextDraw;

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

        // Pin the footer to the bottom edge of the window. Without this, the footer is pushed
        // past the bottom, and because the window is NoScrollbar is only reachable with the mouse wheel.
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
    /// Indicates when the results were built from the copy saved on disk rather than from a
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

        // Empty groups are drawn too - a slot whose every match is hidden keeps an inert
        // header rather than vanishing. See DrawSharedGroup.
        foreach (var group in sharedGroups)
        {
            DrawSharedGroup(group);
            ImGui.Spacing();
        }

        // Cleared only once headers have actually been drawn, so a login with the window
        // shut - or with no results yet - still collapses them when they next appear.
        collapseAllOnNextDraw = false;
    }

    /// <summary>
    /// The header's counts: what is on screen, how many distinct models those rows cover, and
    /// how much of the slot is being held back. The hidden figure is always shown, including
    /// as a zero - a number that only appears once something is hidden is a number nobody
    /// thinks to look for.
    /// </summary>
    private static string FormatGroupCounts(SharedModelGroup group)
    {
        var items = group.Items.Count;
        var models = group.ModelCount;

        return $"{items} item{(items == 1 ? "" : "s")} | {models} model{(models == 1 ? "" : "s")} | {group.HiddenCount} hidden";
    }

    private void DrawSharedGroup(SharedModelGroup group)
    {
        // Keyed on the slot category alone. Including the item count made the ID change
        // whenever the dresser did, so ImGui saw a new widget and collapsed it.
        using var id = ImRaii.PushId(group.SlotCategory);

        // Everything after ### is the ID, everything before it is drawn - so the counts can
        // change in the label without ImGui treating it as a different header and losing
        // whether the user had it expanded.
        var headerText = $"{group.SlotCategory} ({FormatGroupCounts(group)})###header";

        // Claimed here rather than in the branch that uses it, so a reveal that fails to
        // produce a live section cannot leave the request armed for the next slot to trip on.
        var expand = expandOnNextDraw == group.SlotCategory;
        if (expand)
            expandOnNextDraw = null;

        // A slot can end up with nothing left to show - hiding one of a pair takes both out.
        // Dropping the section entirely would make it look as though the slot had never had
        // any duplicates, so the header stays, greyed, still carrying its counts.
        if (group.Items.Count == 0)
        {
            DrawInertGroupHeader(group, headerText);
            return;
        }

        // Get color based on slot category
        var groupColor = GetColorForSlot(group.SlotCategory);
        ImGui.PushStyleColor(ImGuiCol.Header, groupColor);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, groupColor);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, groupColor);
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.AshBlack);

        if (expand)
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        else if (collapseAllOnNextDraw)
            ImGui.SetNextItemOpen(false, ImGuiCond.Always);

        var open = ImGui.CollapsingHeader(headerText);
        ImGui.PopStyleColor(4);

        // Both read ImGui's "last item" state, so they have to come before anything the body
        // draws - and the tooltip before the popup, which starts a window and replaces it.
        DrawGroupTooltip(group);
        DrawGroupContextMenu(group);

        if (open)
            DrawModelRuns(group, groupColor);
    }

    /// <summary>
    /// A section with nothing left to show. Greyed and forced shut, but still right-clickable:
    /// revealing what it is holding back is the only way to get the section back, so this is
    /// the header that needs the menu most. ImRaii.Disabled would have been the tidier way to
    /// make it inert and would have blocked exactly that.
    /// </summary>
    private void DrawInertGroupHeader(SharedModelGroup group, string headerText)
    {
        ImGui.PushStyleColor(ImGuiCol.Header, UiStyle.InertHeader);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UiStyle.InertHeader);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, UiStyle.InertHeader);
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.MutedText);

        // Shut every frame, not just once: left-clicking it can toggle the stored state all it
        // likes, and it will never open onto the nothing that is behind it.
        ImGui.SetNextItemOpen(false, ImGuiCond.Always);
        ImGui.CollapsingHeader(headerText);

        ImGui.PopStyleColor(4);

        DrawGroupTooltip(group);
        DrawGroupContextMenu(group);
    }

    /// <summary>
    /// Only speaks up when the section is holding something back - a tooltip on every header
    /// saying nothing in particular is a tooltip people learn to ignore.
    /// </summary>
    private void DrawGroupTooltip(SharedModelGroup group)
    {
        if (group.HiddenCount == 0 || !ImGui.IsItemHovered())
            return;

        var plural = group.HiddenCount == 1 ? "" : "s";

        ImGui.BeginTooltip();
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.MutedText);

        if (plugin.Configuration.ShowHiddenItems)
            ImGui.TextUnformatted($"{group.HiddenCount} hidden item{plural}, shown by the settings toggle");
        else if (plugin.Configuration.IsSlotRevealed(group.SlotCategory))
            ImGui.TextUnformatted($"Right-click to put {group.HiddenCount} item{plural} back out of sight");
        else
            ImGui.TextUnformatted($"Right-click to show {group.HiddenCount} hidden item{plural}");

        ImGui.PopStyleColor();
        ImGui.EndTooltip();
    }

    /// <summary>
    /// Reveals one section's hidden items without a trip to the settings window. The settings
    /// toggle is still there and still wins - it is the same switch thrown for every section
    /// at once - so while it is on, this menu has nothing to offer.
    /// </summary>
    private void DrawGroupContextMenu(SharedModelGroup group)
    {
        if (!ImGui.BeginPopupContextItem("##groupctx"))
            return;

        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.LightMagenta);
        ImGui.TextUnformatted(group.SlotCategory);
        ImGui.PopStyleColor();
        ImGui.Separator();

        if (plugin.Configuration.ShowHiddenItems)
        {
            using var disabled = ImRaii.Disabled(true);
            ImGui.MenuItem("Shown by the \"show hidden items\" setting");
        }
        else if (group.HiddenCount == 0)
        {
            using var disabled = ImRaii.Disabled(true);
            ImGui.MenuItem("Nothing hidden here");
        }
        else if (plugin.Configuration.IsSlotRevealed(group.SlotCategory))
        {
            if (ImGui.MenuItem($"Hide {group.HiddenCount} item{(group.HiddenCount == 1 ? "" : "s")} again"))
                plugin.Configuration.SetSlotRevealed(group.SlotCategory, false);
        }
        else
        {
            if (ImGui.MenuItem($"Show {group.HiddenCount} hidden item{(group.HiddenCount == 1 ? "" : "s")}"))
            {
                plugin.Configuration.SetSlotRevealed(group.SlotCategory, true);

                // Otherwise the section comes back collapsed and the reveal looks like it did
                // nothing but change a count - and a section that was inert has never been
                // expanded, so there is no remembered state to fall back on.
                expandOnNextDraw = group.SlotCategory;
            }
        }

        ImGui.EndPopup();
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

    /// <summary>
    /// Height of one result row - the icon's size, which is what set the row height before the
    /// Selectable existed too.
    /// </summary>
    private const float RowHeight = 32f;

    private void DrawItem(SharedModelItem item, List<SharedModelItem> allItemsInSlot)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20);

        var rowStart = ImGui.GetCursorPos();

        // The row is laid down as a single full-width Selectable first, and the icon, name,
        // dye circles and tags are then drawn back over it from the same cursor position.
        //
        // This is what the context menu needs: BeginPopupContextItem binds to the *last item
        // drawn*, and the row used to be four separate widgets - so a menu would have bound
        // to the [Armoire] tag alone, and only on the rows that happen to have one. One item
        // for the whole row also means one hover target, hence one tooltip below instead of
        // the three that used to hang off the individual pieces.
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UiStyle.RowHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, UiStyle.RowHover);
        ImGui.Selectable(
            $"##row{item.ItemId}",
            false,
            ImGuiSelectableFlags.None,
            new Vector2(ImGui.GetContentRegionAvail().X, RowHeight));
        ImGui.PopStyleColor(2);

        // Where the next row belongs. Drawing the contents rewinds the cursor, so it has to
        // be put back deliberately rather than left wherever the last tag ended up.
        var afterRow = ImGui.GetCursorPos();

        // Tooltip before the popup: both read ImGui's "last item" state, and beginning the
        // popup starts a window, which replaces it.
        DrawItemTooltip(item, allItemsInSlot);
        DrawItemContextMenu(item);

        ImGui.SetCursorPos(rowStart);
        DrawItemContents(item);
        ImGui.SetCursorPos(afterRow);
    }

    /// <summary>
    /// The visible part of a row. Drawn over the Selectable, so nothing in here may be
    /// interactive - an interactive widget would steal the hover from the row and take the
    /// context menu with it.
    /// </summary>
    private void DrawItemContents(SharedModelItem item)
    {
        // Try to get icon. The dresser's own IconId can be one the game cannot resolve for
        // HQ entries, so fall back to the icon the Item sheet gives for the base item.
        var icon = GetIcon((uint)item.IconId) ?? GetIcon(GetItemIconFromLumina(item.ItemId));
        if (icon != null)
            ImGui.Image(icon.Handle, new Vector2(RowHeight, RowHeight));
        else
            ImGui.Dummy(new Vector2(RowHeight, RowHeight)); // placeholder, so names stay aligned

        ImGui.SameLine();

        // Get display name - fallback if empty. HQ entries carry the game's own HQ glyph,
        // since the Item sheet name is identical for both qualities.
        var displayName = string.IsNullOrWhiteSpace(item.Name) ? $"Item #{item.ItemId}" : item.Name;
        if (item.IsHq)
            displayName = $"{displayName} {(char)SeIconChar.HighQuality}";

        // No per-row "shared model" marker: the scan only keeps items that already share a
        // model, so every row would carry one. The vertical bar down each run is what shows
        // which rows group together.
        //
        // A hidden row is only on screen because "show hidden items" is on. Muting the name
        // says so at a glance, rather than leaving it to the tag at the end of the line.
        ImGui.PushStyleColor(ImGuiCol.Text, item.IsHidden ? UiStyle.MutedText : UiStyle.BrightWhite);
        ImGui.TextUnformatted(displayName);
        ImGui.PopStyleColor();

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

            // A Dummy, not an InvisibleButton: this only has to reserve the circles' width on
            // the line now that the row's Selectable owns the hover. Do not advance the cursor
            // past it by hand - ImGui has already wrapped to the next row, so any SetCursorPosX
            // here indents the *following* item instead of this one.
            var circlesWidth = (item.DyeCount * circleSpacing) + 4;
            ImGui.Dummy(new Vector2(circlesWidth, circleRadius * 2 + 4));
        }

        // Draw Armoire marker if item can be stored in Armoire
        if (item.CanGoInArmoire)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5);

            ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.SoftMagenta);
            ImGui.TextUnformatted("[Armoire]");
            ImGui.PopStyleColor();
        }

        if (item.IsHidden)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5);

            ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.MutedText);
            ImGui.TextUnformatted("[Hidden]");
            ImGui.PopStyleColor();
        }
    }

    /// <summary>
    /// One tooltip for the whole row. The dye-slot and Armoire notes used to hang off their
    /// own widgets; with the row a single item they have nowhere else to live, and gathering
    /// them means the row explains itself in one hover rather than three.
    /// </summary>
    private void DrawItemTooltip(SharedModelItem item, List<SharedModelItem> allItemsInSlot)
    {
        if (!ImGui.IsItemHovered())
            return;

        var matchingModelCount = allItemsInSlot.Count(i => i.ModelId == item.ModelId);

        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"{matchingModelCount} items match model: {item.ModelId}");

        if (item.DyeCount > 0)
            ImGui.TextUnformatted($"{item.DyeCount} dye slot{(item.DyeCount > 1 ? "s" : "")} available");

        if (item.CanGoInArmoire)
            ImGui.TextUnformatted("This item can be stored in your Armoire instead of the Glamour Dresser!");

        // The only advertisement the feature gets. Right-click on a list row is not something
        // anyone tries unprompted.
        ImGui.Separator();
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.MutedText);
        ImGui.TextUnformatted(item.IsHidden
            ? "Right-click to show this item again"
            : "Right-click to hide this item");
        ImGui.PopStyleColor();

        ImGui.EndTooltip();
    }

    private void DrawItemContextMenu(SharedModelItem item)
    {
        // Raw ImGui rather than ImRaii: EndPopup must be called only when Begin returned
        // true, which is the opposite of the child/table rule ImRaii exists to handle.
        if (!ImGui.BeginPopupContextItem($"##rowctx{item.ItemId}"))
            return;

        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.LightMagenta);
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(item.Name) ? $"Item #{item.ItemId}" : item.Name);
        ImGui.PopStyleColor();
        ImGui.Separator();

        if (item.IsHidden)
        {
            if (ImGui.MenuItem("Show this item again"))
                plugin.Configuration.SetHidden(item.ItemId, false);
        }
        else
        {
            if (ImGui.MenuItem("Hide this item"))
                plugin.Configuration.SetHidden(item.ItemId, true);
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// GetFromGameIcon throws IconNotFoundException for an icon the game does not have, so
    /// GetWrapOrDefault never gets the chance to return null. An unresolvable icon must not
    /// take the whole window's Draw() down with it - DrawItem falls back to a blank space.
    ///
    /// Takes a uint, deliberately. The id used to be cast to ushort at the call site, which
    /// meant an out-of-range value wrapped into a valid but unrelated icon instead of
    /// failing.
    /// </summary>
    private IDalamudTextureWrap? GetIcon(uint id)
    {
        if (id == 0)
            return null;

        // The dresser offsets an HQ entry's icon by 1,000,000, exactly as it offsets its item
        // id. Asking for the HQ variant of the base icon gets the game's own HQ treatment 
        // rather than a near-miss.
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

            // Hidden items come out BEFORE the shared-model test below, not after it. Hiding
            // one half of a pair has to take the other half with it: the survivor is no longer
            // redundant with anything, and leaving it on screen as a run of one would be a lie
            // about what the plugin found.
            //
            // "Show hidden items" switches the filter off wholesale rather than appending the
            // hidden rows back on, so what is on screen is exactly the unfiltered picture with
            // the hidden ones tagged - and unhiding one changes nothing but its tag.
            // The filter is per slot, not global: a section header's right-click reveals just
            // that section, and the settings toggle is the same switch thrown for all of them
            // at once. ShowsHiddenIn answers both.
            var hiddenCount = validItems.Count(item => plugin.Configuration.IsHidden(item.ItemId));
            var visibleItems = validItems
                .Where(item => !plugin.Configuration.IsHidden(item.ItemId)
                               || plugin.Configuration.ShowsHiddenIn(GetSlotName(item.ItemId)))
                .ToList();

            // Per slot, so each section header can report what it is holding back - and so a
            // section that has nothing left to show can still say why.
            var hiddenBySlot = validItems
                .Where(item => plugin.Configuration.IsHidden(item.ItemId))
                .GroupBy(item => GetSlotName(item.ItemId))
                .ToDictionary(g => g.Key, g => g.Count());

            Plugin.Log.Information($"Scan: {dresserItems.Count} raw, {uniqueItems.Count} unique, {validItems.Count} equippable, {outfitCount} outfits, {hiddenCount} hidden");
            foreach (var g in uniqueItems.GroupBy(i => GetSlotName(i.ItemId)).OrderBy(g => GetSlotOrder(g.Key)))
                Plugin.Log.Debug($"Scan category {g.Key}: {g.Count()}");

            // First, identify items with shared models by grouping by slot + model
            var itemsWithSharedModels = visibleItems
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
                                IsHq = item.ItemId >= 1_000_000,
                                // Only ever true while ShowHiddenItems is on - otherwise a
                                // hidden item never reaches this far.
                                IsHidden = plugin.Configuration.IsHidden(item.ItemId)
                            };
                        })
                        .ToList();
                    
                    return new SharedModelGroup
                    {
                        ModelId = "", // Not used for slot-based grouping
                        SlotCategory = g.Key,
                        Items = sortedItems,
                        ModelCount = sortedItems.Select(i => i.ModelId).Distinct().Count(),
                        HiddenCount = hiddenBySlot.GetValueOrDefault(g.Key)
                    };
                })
                .OrderBy(g => GetSlotOrder(g.SlotCategory)) // Sort slots in logical order
                .ToList();

            // A slot can hide its way down to nothing - hiding one of a pair drops both, and
            // that was the slot's only match. It still gets a header, so the section reports
            // what became of it instead of disappearing. DrawSharedGroup draws these inert.
            var emptied = hiddenBySlot
                .Where(entry => !grouped.Any(g => g.SlotCategory == entry.Key))
                .Select(entry => new SharedModelGroup
                {
                    SlotCategory = entry.Key,
                    HiddenCount = entry.Value
                });

            grouped = grouped
                .Concat(emptied)
                .OrderBy(g => GetSlotOrder(g.SlotCategory))
                .ToList();

            sharedGroups = grouped;
            var totalItems = grouped.Sum(g => g.Items.Count);
            // Only the categories that actually have something in them. The emptied ones above
            // are on screen to explain themselves, not because they are a result.
            var categoryCount = grouped.Count(g => g.Items.Count > 0);
            statusMessage = $"Found {totalItems} items with shared models across {categoryCount} slot categories!";

            // Hidden items are silent by design, which is exactly why the count has to be
            // stated somewhere - otherwise a result that shrank months ago has no explanation
            // on screen at all. Whether any of them are currently revealed is a per-section
            // question, and the section headers answer it.
            if (hiddenCount > 0)
                statusMessage += $" ({hiddenCount} hidden)";
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

    /// <summary>Distinct models across <see cref="Items"/> - how many runs the section has.</summary>
    public int ModelCount { get; set; }

    /// <summary>Hidden items in this slot, whether or not any of them are on screen.</summary>
    public int HiddenCount { get; set; }
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
    public bool IsHidden { get; set; }
}
