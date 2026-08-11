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

namespace Dispeller.Windows;

/// <summary>
/// The results window: the merged dresser and Armoire contents, grouped by equipment slot and
/// then by shared model.
/// </summary>
public class MainWindow : Window, IDisposable
{
    /// <summary>
    /// The slot category outfit bundles are filed under. Not an equipment slot - the game gives
    /// a bundle EquipSlotCategory 0 - so it is matched and counted on its own terms throughout.
    /// </summary>
    private const string OutfitCategory = "Outfit";

    private readonly Plugin plugin;
    private List<SharedModelGroup>? sharedGroups;
    private string statusMessage = "Open your Glamour Dresser to get started!";

    /// <summary>
    /// Carried alongside the message rather than derived from it. The status line says two kinds
    /// of thing - a prompt for something the plugin still needs, which belongs with the
    /// stale-cache notices, and a summary of what it found, which does not - and deriving the
    /// colour would mean matching on the text.
    /// </summary>
    private Vector4 statusColor = UiStyle.StaleNotice;
    private int lastGeneration = DresserScanner.Generation;
    private int lastArmoireGeneration = ArmoireScanner.Generation;
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

    /// <summary>
    /// Driven by the scanner's edge-triggered events, so the window only moves at the moment the
    /// dresser opens. Reacting to "the dresser is open" every frame would make the window
    /// impossible to close while standing at one.
    /// </summary>
    private void OnDresserOpened()
    {
        if (plugin.Configuration.OpenWithGlamourDresser)
            IsOpen = true;
    }

    /// <summary>
    /// Gated on the parent setting as well as its own. Hiding on the way out is a sub-option of
    /// opening on the way in, and the settings window only offers it while the parent is on - so
    /// it must not keep acting once the parent is switched off.
    /// </summary>
    private void OnDresserClosed()
    {
        if (plugin.Configuration.OpenWithGlamourDresser && plugin.Configuration.HideWhenLeavingGlamourDresser)
            IsOpen = false;
    }

    /// <summary>
    /// Expanded sections are session state and should not carry across a login. ImGui keeps header
    /// state in the window's storage for as long as the game runs, so it has to be collapsed
    /// deliberately - it will not lapse on its own.
    ///
    /// The caches are not touched here: each scanner watches the logged-in character and swaps in
    /// that character's saved copy on its own.
    /// </summary>
    private void OnLogin()
    {
        collapseAllOnNextDraw = true;
    }

    public override void Draw()
    {
        // Rebuild whenever either store's contents have actually changed, so the results never
        // quietly describe an older read. A settings change rebuilds too: cheaper than working out
        // which settings the results depend on, and it cannot leave stale results on screen.
        if (DresserScanner.Generation != lastGeneration
            || ArmoireScanner.Generation != lastArmoireGeneration
            || Configuration.Revision != lastConfigRevision)
            BuildGroups();

        DrawHeader();

        ImGui.Spacing();

        DrawStatus();
        DrawCachedNotice();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawResults(UiStyle.FooterHeight);

        UiStyle.DrawPinnedFooter("Find shared models in your glamour dresser!", UiStyle.BrightWhite);
    }

    /// <summary>
    /// The game font has no emoji, so <c>SeIconChar</c> glyphs are used instead - they are drawn
    /// from the game's own font and scale with it.
    /// </summary>
    private void DrawHeader()
    {
        UiStyle.DrawHeaderBand($"{(char)SeIconChar.Hyadelyn} Dispeller {(char)SeIconChar.Hyadelyn}");
    }

    /// <summary>
    /// Routed through the same helper as the notices, so the status line gets the clamped centring
    /// too - it is the longest string the window draws, and the one most able to walk off the
    /// left edge.
    /// </summary>
    private void DrawStatus() => DrawCentredNotice((statusMessage, statusColor));

    /// <summary>
    /// Indicates when the results were built from a copy saved on disk rather than from a live
    /// read. Evaluated every frame rather than at scan time, so a line clears the moment its
    /// store is opened and confirmed.
    ///
    /// The two stores get a line each. They go stale independently - one can be opened and
    /// confirmed in a session the other is never touched in - so a single notice covering both
    /// would be unable to say which of them needs opening.
    /// </summary>
    private void DrawCachedNotice()
    {
        if (DresserScanner.IsFromSavedCache)
            DrawStaleNotice("Dresser", DresserScanner.SavedAt);

        // "Never read" is not the same as "empty", and the difference matters here: without it,
        // every row would be claiming its item is not in the Armoire on the strength of never
        // having looked.
        if (!ArmoireScanner.HasData)
            DrawCentredNotice(("Open your Armoire at least once so it can be read!", UiStyle.StaleNotice));
        else if (ArmoireScanner.IsFromSavedCache)
            DrawStaleNotice("Armoire", ArmoireScanner.SavedAt);
    }

    /// <summary>
    /// One store's "this came off disk" line. Both stores go through here so they cannot drift
    /// apart in wording or colour - the only thing that differs is which store is named.
    ///
    /// The timestamp is its own colour run: it is the one part of the line that is a fact rather
    /// than an instruction, and it is what the reader is actually looking for.
    /// </summary>
    private static void DrawStaleNotice(string store, DateTimeOffset savedAt)
        => DrawCentredNotice(
            ($"{store} cached from ", UiStyle.StaleNotice),
            ($"{savedAt.ToLocalTime():d MMM yyyy, HH:mm}", UiStyle.BrightWhite),
            (". Open it to refresh.", UiStyle.StaleNotice));

    /// <summary>
    /// A single centred line assembled from differently coloured runs. Measured whole and
    /// positioned once, so the colour changes do not shift where the line sits.
    /// </summary>
    private static void DrawCentredNotice(params (string Text, Vector4 Color)[] runs)
    {
        var width = 0f;
        foreach (var (text, _) in runs)
            width += ImGui.CalcTextSize(text).X;

        // Clamped at zero, as UiStyle.DrawPinnedFooter clamps: a line wider than the window
        // centres to a negative offset, walking its start off the left edge instead of letting
        // the end clip.
        ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2));

        for (var i = 0; i < runs.Length; i++)
        {
            // Zero spacing, or ImGui inserts its item spacing between the runs and the line reads
            // as words pulled apart rather than as one sentence.
            if (i > 0)
                ImGui.SameLine(0, 0);

            ImGui.PushStyleColor(ImGuiCol.Text, runs[i].Color);
            ImGui.TextUnformatted(runs[i].Text);
            ImGui.PopStyleColor();
        }
    }

    private void DrawResults(float footerHeight)
    {
        // Nothing to show yet - DrawStatus already says what state the scan is in.
        if (sharedGroups == null || sharedGroups.Count == 0)
            return;

        // A negative height means "content region avail minus this much", which keeps the
        // scrolling results area clear of the footer below it.
        var height = -(footerHeight + ImGui.GetStyle().ItemSpacing.Y);

        // ImRaii.Child ends the child unconditionally - ImGui requires EndChild() even when
        // BeginChild() returns false, unlike Begin/End on popups and menus.
        using var child = ImRaii.Child("Results", new Vector2(0, height), false);
        if (!child)
            return;

        // Empty groups are drawn too - a slot whose every match is hidden keeps an inert header
        // rather than vanishing. See DrawInertGroupHeader.
        foreach (var group in sharedGroups)
        {
            DrawSharedGroup(group);
            ImGui.Spacing();
        }

        // Cleared only once headers have actually been drawn, so a login with the window shut, or
        // with no results yet, still collapses them when they next appear.
        collapseAllOnNextDraw = false;
    }

    /// <summary>
    /// The header's counts: what is on screen, how many distinct models those rows cover, and how
    /// much of the slot is being held back. The hidden figure shows even as a zero - a number that
    /// only appears once something is hidden is one nobody thinks to look for.
    /// </summary>
    private static string FormatGroupCounts(SharedModelGroup group)
    {
        var items = group.Items.Count;
        var models = group.ModelCount;

        // The Outfit section counts outfits, not models. Its rows are bundles, which have no
        // model of their own and are matched on the outfit's name - calling that a model would
        // describe the one section where the word does not apply.
        var unit = group.SlotCategory == OutfitCategory ? "outfit" : "model";

        return $"{items} item{(items == 1 ? "" : "s")} | {models} {unit}{(models == 1 ? "" : "s")} | {group.HiddenCount} hidden";
    }

    private void DrawSharedGroup(SharedModelGroup group)
    {
        // Keyed on the slot category alone. Including the item count would change the id whenever
        // the dresser did, so ImGui would see a new widget and collapse it.
        using var id = ImRaii.PushId(group.SlotCategory);

        // Everything after ### is the id and everything before it is drawn, so the counts can
        // change in the label without ImGui treating it as a different header and forgetting
        // whether the user had it expanded.
        var headerText = $"{group.SlotCategory} ({FormatGroupCounts(group)})###header";

        // Claimed here rather than in the branch that uses it, so a reveal that fails to produce
        // a live section cannot leave the request armed for the next slot to trip on.
        var expand = expandOnNextDraw == group.SlotCategory;
        if (expand)
            expandOnNextDraw = null;

        // A slot can end up with nothing left to show, since hiding one of a pair takes both out.
        // Dropping the section would read as "this slot never had duplicates", so the header
        // stays, greyed, still carrying its counts.
        if (group.Items.Count == 0)
        {
            DrawInertGroupHeader(group, headerText);
            return;
        }

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

        // Both read ImGui's "last item" state, so they come before anything the body draws - and
        // the tooltip before the popup, which starts a window and replaces that state.
        DrawGroupTooltip(group);
        DrawGroupContextMenu(group);

        if (open)
            DrawModelRuns(group, groupColor);
    }

    /// <summary>
    /// A section with nothing left to show. Greyed and forced shut, but still right-clickable:
    /// revealing what it is holding back is the only way to get the section back, so this is the
    /// header that needs its menu most. <c>ImRaii.Disabled</c> would be the tidier way to make it
    /// inert and would block exactly that.
    /// </summary>
    private void DrawInertGroupHeader(SharedModelGroup group, string headerText)
    {
        ImGui.PushStyleColor(ImGuiCol.Header, UiStyle.InertHeader);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UiStyle.InertHeader);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, UiStyle.InertHeader);
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.MutedText);

        // Shut every frame rather than once: left-clicking may toggle the stored state all it
        // likes, and it will never open onto the nothing behind it.
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

                // A section that was inert has never been expanded, so there is no remembered
                // state to fall back on - without this it comes back collapsed and the reveal
                // looks like it did nothing but change a count.
                expandOnNextDraw = group.SlotCategory;
            }
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// Draws a slot's items as runs of a shared model, bracketing each run with a thin vertical
    /// bar in the slot's own colour. Items are sorted by model id when the group is built, so a
    /// run is always a contiguous span.
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

            // The cursor now sits at the start of the next row, one ItemSpacing below the run's
            // last row.
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

    /// <summary>Height of one result row - the item icon's size.</summary>
    private const float RowHeight = 32f;

    // The game's own furnishing art for the two places an item can live, drawn left of the item
    // icon. Furnishings rather than font glyphs because the game font has no symbol for either
    // store, and these are the objects themselves.
    private const uint GlamourDresserIcon = 51710;
    private const uint ArmoireIcon = 52536;

    // Smaller than the item icon so the row still reads name-first, with a gap wide enough that
    // two adjacent glyphs do not merge into one shape at a glance.
    private const float LocationGlyphSize = 22f;
    private const float LocationGlyphGap = 4f;

    /// <summary>Width the location column occupies whether or not either glyph is drawn.</summary>
    private const float LocationColumnWidth = (LocationGlyphSize + LocationGlyphGap) * 2;

    private void DrawItem(SharedModelItem item, List<SharedModelItem> allItemsInSlot)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20);

        var rowStart = ImGui.GetCursorPos();

        // The row is one full-width Selectable, laid down first, with the glyphs, icon, name, dye
        // circles and tags drawn back over it from the same cursor position.
        //
        // This is what the context menu needs: BeginPopupContextItem binds to the last item drawn,
        // so a row made of several widgets binds its menu to the trailing tag alone, and only on
        // the rows that have one. One item for the whole row also means one hover target, hence
        // one tooltip rather than one per piece.
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UiStyle.RowHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, UiStyle.RowHover);

        // Keyed on the dresser Slot as well as the item id, because one item id can be on screen
        // twice: an item stored in two dresser groupings is two rows, and so is an outfit held
        // under two. Two widgets sharing an ImGui id share their hover and their popup.
        ImGui.Selectable(
            $"##row{item.ItemId}_{item.Slot}",
            false,
            ImGuiSelectableFlags.None,
            new Vector2(ImGui.GetContentRegionAvail().X, RowHeight));
        ImGui.PopStyleColor(2);

        // Where the next row belongs. Drawing the contents rewinds the cursor, so it has to be put
        // back deliberately rather than left wherever the last tag ended up.
        var afterRow = ImGui.GetCursorPos();

        // Tooltip before the popup: both read ImGui's "last item" state, and beginning the popup
        // starts a window, which replaces it.
        DrawItemTooltip(item, allItemsInSlot);
        DrawItemContextMenu(item);

        ImGui.SetCursorPos(rowStart);
        DrawItemContents(item);
        ImGui.SetCursorPos(afterRow);
    }

    /// <summary>
    /// The visible part of a row, drawn over the Selectable - so nothing in here may be
    /// interactive, or it would steal the hover from the row and take the context menu with it.
    /// </summary>
    private void DrawItemContents(SharedModelItem item)
    {
        DrawLocationGlyphs(item);

        // The dresser's own IconId can be one the game cannot resolve for an HQ entry, so fall
        // back to the icon the Item sheet gives for the base item.
        var icon = GetIcon((uint)item.IconId) ?? GetIcon(GetItemIconFromLumina(item.ItemId));
        if (icon != null)
            ImGui.Image(icon.Handle, new Vector2(RowHeight, RowHeight));
        else
            ImGui.Dummy(new Vector2(RowHeight, RowHeight)); // placeholder, so names stay aligned

        ImGui.SameLine();

        // HQ entries carry the game's own HQ glyph, since the Item sheet name is identical for
        // both qualities.
        var displayName = string.IsNullOrWhiteSpace(item.Name) ? $"Item #{item.ItemId}" : item.Name;
        if (item.IsHq)
            displayName = $"{displayName} {(char)SeIconChar.HighQuality}";

        // No per-row "shared model" marker: nearly every row is here because it shares a model, so
        // almost all of them would carry one. The vertical bar down each run is what shows which
        // rows group together.
        //
        // A hidden row is only on screen because "show hidden items" is on. Muting the name says
        // so at a glance rather than leaving it to the tag at the end of the line.
        ImGui.PushStyleColor(ImGuiCol.Text, item.IsHidden ? UiStyle.MutedText : UiStyle.BrightWhite);
        ImGui.TextUnformatted(displayName);
        ImGui.PopStyleColor();

        // One empty circle per dye slot the item has, as Glamaholic draws them.
        if (item.DyeCount > 0)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5);

            var drawList = ImGui.GetWindowDrawList();
            var basePos = ImGui.GetCursorScreenPos();
            var circleRadius = 4.0f;
            var circleSpacing = 8.0f;
            var circleColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.85f, 0.85f, 1.0f));

            for (int i = 0; i < item.DyeCount; i++)
            {
                var circleCenter = basePos + new Vector2(circleRadius + 2, circleRadius + 2) + new Vector2(i * circleSpacing, 0);
                drawList.AddCircle(circleCenter, circleRadius + 1, circleColor);
            }

            // A Dummy rather than an InvisibleButton: this only reserves the circles' width, now
            // that the row's Selectable owns the hover. Do not advance the cursor past it by hand
            // - ImGui has already wrapped to the next row, so a SetCursorPosX here would indent
            // the following item instead of this one.
            var circlesWidth = (item.DyeCount * circleSpacing) + 4;
            ImGui.Dummy(new Vector2(circlesWidth, circleRadius * 2 + 4));
        }

        // Where the item is now is already said by the glyphs on the left, so this line is only
        // about what to do next - and that changes completely once a second copy is confirmed,
        // whether it is in the Armoire or in the dresser itself. The two read the same on the row
        // and are told apart in the tooltip.
        if (item.IsPerfectDuplicate)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5);

            ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.Attention);
            ImGui.TextUnformatted("[Perfect duplicate]");
            ImGui.PopStyleColor();
        }
        else if (item.CanGoInArmoire && !item.InArmoire)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5);

            ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.SoftMagenta);
            ImGui.TextUnformatted("[Armoire Eligible]");
            ImGui.PopStyleColor();
        }

        // Independent of the two Armoire tags above and free to sit alongside one: where an item
        // is kept and what it is a piece of are different facts. This is the one tag that says
        // something about the item rather than about what to do with it - losing a piece breaks up
        // a set, and the row otherwise gives no sign it belongs to one.
        //
        // Checked at draw time rather than at build time: the tag is the only thing the setting
        // governs, so turning it off has nothing to rebuild and the tooltip keeps saying it.
        if (item.Outfits.Count > 0 && plugin.Configuration.TagOutfitComponents)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5);

            ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.OutfitTag);
            ImGui.TextUnformatted("[Outfit]");
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
    /// Where the item lives, as up to two pieces of the game's own furnishing art ahead of the
    /// item icon.
    ///
    /// Both slots sit at fixed positions and an absent one is left blank rather than closed up:
    /// left means Glamour Dresser, right means Armoire, both filled means both. Position is what
    /// carries the meaning - the two icons are similar enough at this size that a glyph shifting
    /// left when the other was missing would be unreadable.
    /// </summary>
    private void DrawLocationGlyphs(SharedModelItem item)
    {
        var start = ImGui.GetCursorPos();
        var top = start.Y + (RowHeight - LocationGlyphSize) / 2;

        DrawLocationGlyph(new Vector2(start.X, top), GlamourDresserIcon, item.InDresser);
        DrawLocationGlyph(new Vector2(start.X + LocationGlyphSize + LocationGlyphGap, top), ArmoireIcon, item.InArmoire);

        // Back to the row's top-left, advanced past the column. Each ImGui.Image above left the
        // cursor wherever it finished, and the item icon has to start from a position that does
        // not depend on which glyphs were drawn.
        ImGui.SetCursorPos(new Vector2(start.X + LocationColumnWidth, start.Y));
    }

    private void DrawLocationGlyph(Vector2 position, uint iconId, bool present)
    {
        if (!present)
            return;

        var icon = GetIcon(iconId);
        if (icon == null)
            return;

        ImGui.SetCursorPos(position);
        ImGui.Image(icon.Handle, new Vector2(LocationGlyphSize, LocationGlyphSize));
    }

    /// <summary>
    /// Where the item lives, in words. The glyph column says it at a glance; this is what the
    /// glance is checked against, and it is also the only place that can admit to not knowing.
    /// </summary>
    private static string DescribeLocation(SharedModelItem item)
    {
        if (item.InDresser && item.InArmoire)
            return "In your Glamour Dresser and your Armoire";

        if (item.InArmoire)
            return "In your Armoire";

        return ArmoireScanner.HasData
            ? "In your Glamour Dresser"
            : "In your Glamour Dresser - your Armoire has not been read yet";
    }

    /// <summary>
    /// One tooltip for the whole row. With the row a single ImGui item there is only one hover
    /// target, so everything the row has to explain is gathered here.
    /// </summary>
    private void DrawItemTooltip(SharedModelItem item, List<SharedModelItem> allItemsInSlot)
    {
        if (!ImGui.IsItemHovered())
            return;

        var matchingModelCount = allItemsInSlot.Count(i => i.ModelId == item.ModelId);

        ImGui.BeginTooltip();

        // An outfit row is not here for its model - a bundle has none - but because the dresser is
        // holding the same outfit under more than one grouping, which is what its run counts.
        if (item.IsOutfit)
        {
            ImGui.TextUnformatted($"Outfit: {item.ModelId}");
            ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.Attention);
            ImGui.TextUnformatted(matchingModelCount > 1
                ? $"Your dresser is holding this outfit under {matchingModelCount} separate groupings"
                : "Held under one grouping");
            ImGui.PopStyleColor();
        }
        else
        {
            // A run of one is an item redundant against its own second copy rather than against a
            // neighbour, so it does not claim a match.
            ImGui.TextUnformatted(matchingModelCount > 1
                ? $"{matchingModelCount} items match model: {item.ModelId}"
                : $"Model: {item.ModelId}");
        }

        ImGui.TextUnformatted(DescribeLocation(item));

        if (item.DresserCopies > 1)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.Attention);
            ImGui.TextUnformatted(
                $"Your dresser is holding {item.DresserCopies} copies of this item, one slot each.");
            ImGui.PopStyleColor();
        }

        // Named rather than merely flagged: which set this copy hangs off is the reason the tag is
        // worth having.
        if (item.Outfits.Count > 0)
            ImGui.TextUnformatted($"Part of {string.Join(", ", item.Outfits)}");

        if (item.DyeCount > 0)
            ImGui.TextUnformatted($"{item.DyeCount} dye slot{(item.DyeCount > 1 ? "s" : "")} available");

        // Two pieces of advice, and which applies turns on whether a copy is already in the
        // Armoire - so they are never both true at once.
        if (item.InDresser && item.InArmoire)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.Attention);
            ImGui.TextUnformatted("A copy is already in your Armoire - the dresser copy is costing you a slot for nothing.");
            ImGui.PopStyleColor();
        }
        else if (item.CanGoInArmoire && !item.InArmoire)
        {
            ImGui.TextUnformatted("This item can be stored in your Armoire instead of the Glamour Dresser!");
        }

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

    /// <summary>
    /// Raw ImGui rather than ImRaii: EndPopup must be called only when Begin returned true, which
    /// is the opposite of the child/table rule ImRaii exists to handle.
    /// </summary>
    private void DrawItemContextMenu(SharedModelItem item)
    {
        if (!ImGui.BeginPopupContextItem($"##rowctx{item.ItemId}_{item.Slot}"))
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
    /// One icon, or null if the game cannot resolve it. <c>GetFromGameIcon</c> throws
    /// <c>IconNotFoundException</c> for an icon the game does not have, so <c>GetWrapOrDefault</c>
    /// never gets the chance to return null - and an unresolvable icon must not take the window's
    /// whole Draw() down with it.
    ///
    /// Takes a uint deliberately: narrowing an id that came out of the dresser does not fail, it
    /// wraps, silently producing a valid but unrelated icon.
    /// </summary>
    private IDalamudTextureWrap? GetIcon(uint id)
    {
        if (id == 0)
            return null;

        // The dresser offsets an HQ entry's icon by 1,000,000, exactly as it offsets its item id.
        // Asking for the HQ variant of the base icon gets the game's own HQ treatment rather than
        // a near-miss.
        var isHq = id >= 1_000_000;
        var iconId = isHq ? id - 1_000_000 : id;

        // Anything still out of range is not an icon this can resolve. Null rather than truncated,
        // so the caller's fallback runs.
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

    /// <summary>
    /// Rebuilds the grouped results whenever a cache changes underneath the window. Opening the
    /// dresser, changing its view, depositing or retrieving, and loading a character's saved copy
    /// all land here - there is nothing for a user to press.
    /// </summary>
    private void BuildGroups()
    {
        lastGeneration = DresserScanner.Generation;
        lastArmoireGeneration = ArmoireScanner.Generation;
        lastConfigRevision = Configuration.Revision;

        // Dropped rather than carried between builds: a rebuild happens because something changed,
        // and one of those things is the recolour setting the model answer depends on.
        slotNameCache.Clear();
        modelIdCache.Clear();

        var buildTimer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var dresserItems = DresserScanner.GetDresserItems();
            var armoireItemIds = ArmoireScanner.GetStoredItemIds();
            var uniqueItems = MergeStores(dresserItems, armoireItemIds);

            if (uniqueItems.Count == 0)
            {
                statusMessage = "Open your Glamour Dresser at least once so it can be read!";
                statusColor = UiStyle.StaleNotice;
                sharedGroups = null;
                return;
            }

            // Outfit bundles ("... Attire") have no equipment slot of their own, so they cannot be
            // model-matched against garments. They get their own section, matched on the outfit's
            // name, and are taken from the raw dresser list rather than from uniqueItems: a
            // grouping is a dresser-only idea, and an Armoire row has no Slot to answer with.
            //
            // Deduplicated on (ItemId, Slot). Slot is the id of the dresser's own grouping, so one
            // outfit at one Slot is one grouping however many times the array lists it - removing
            // a piece from an outfit leaves the game repeating that outfit's entry, and counting
            // rows would report those repeats as duplicated outfits. Two genuine groupings are two
            // Slots, which survives this untouched.
            var outfitEntries = dresserItems
                .Where(item => GetSlotName(item.ItemId) == OutfitCategory)
                .DistinctBy(item => (item.ItemId, item.Slot))
                .ToList();

            // Which dresser grouping each copy is linked to, so a piece can be told from an
            // unattached second copy of the same item sitting in a grouping of its own.
            var outfitsBySlot = BuildOutfitsBySlot(outfitEntries);

            // Only items with a real equipment slot can be model-matched. Outfits are excluded
            // here because they are handled as their own section.
            var validItems = uniqueItems
                .Where(item => {
                    var slotName = GetSlotName(item.ItemId);
                    return !string.IsNullOrEmpty(slotName) && slotName != "Unknown Slot" && slotName != OutfitCategory;
                })
                .ToList();

            // Hidden items come out before the shared-model test below, never after it. Hiding one
            // half of a pair has to take the other half with it: the survivor is no longer
            // redundant with anything, and a run of one would be a lie about what was found.
            //
            // "Show hidden items" switches the filter off wholesale rather than appending the
            // hidden rows back on, so what is on screen is the unfiltered picture with the hidden
            // ones tagged. The filter is per slot rather than global - a section header's
            // right-click reveals just that section, and the settings toggle is the same switch
            // thrown for all of them at once. ShowsHiddenIn answers both.
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

            Plugin.Log.Information(
                $"Scan: {dresserItems.Count} raw dresser, {armoireItemIds.Count} armoire, {uniqueItems.Count} unique, "
                + $"{uniqueItems.Count(i => i.InDresser && i.InArmoire)} in both, {validItems.Count} equippable, "
                + $"{outfitEntries.Count} outfits, {hiddenCount} hidden");
            foreach (var g in uniqueItems.GroupBy(i => GetSlotName(i.ItemId)).OrderBy(g => GetSlotOrder(g.Key)))
                Plugin.Log.Debug($"Scan category {g.Key}: {g.Count()}");

            // The redundant items, grouped on slot plus model. Two ways in: more than one item
            // resolving to the same appearance, or an item held in the dresser and the Armoire at
            // once. The second needs nothing else to match - the dresser slot is being spent on
            // something already stored for free - and without the clause it would only ever
            // surface by coincidence, when an unrelated item happened to share its mesh.
            var itemsWithSharedModels = visibleItems
                .GroupBy(item => {
                    var slotName = GetSlotName(item.ItemId);
                    var modelId = GetItemModel(item.ItemId);
                    return $"{slotName}-{modelId}";
                })
                .Where(g => g.Count() > 1 || g.Any(item => item.InDresser && item.InArmoire))
                .SelectMany(g => g)
                .ToList();

            var grouped = itemsWithSharedModels
                .GroupBy(item => GetSlotName(item.ItemId))
                .Select(g => {
                    // Sorted by model id so matching models are adjacent, with the HQ entry at the
                    // head of its run.
                    var sortedItems = g
                        .OrderBy(item => GetItemModel(item.ItemId))
                        .ThenByDescending(item => item.ItemId >= 1_000_000)
                        .Select(item => {
                            // The name always comes from the Item sheet - the dresser's own copy
                            // can be stale.
                            var itemName = GetItemNameFromLumina(item.ItemId);

                            var iconId = item.IconId;
                            if (iconId == 0)
                            {
                                iconId = GetItemIconFromLumina(item.ItemId);
                            }

                            var dyeCount = GetItemDyeCount(item.ItemId);
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
                                InDresser = item.InDresser,
                                InArmoire = item.InArmoire,
                                IsHq = item.ItemId >= 1_000_000,
                                // Only ever true while hidden items are being shown - otherwise a
                                // hidden item never reaches this far.
                                IsHidden = plugin.Configuration.IsHidden(item.ItemId),
                                DresserCopies = item.DresserCopies,
                                Outfits = OutfitsFor(item, outfitsBySlot)
                            };
                        })
                        .ToList();
                    
                    return new SharedModelGroup
                    {
                        ModelId = "",
                        SlotCategory = g.Key,
                        Items = sortedItems,
                        ModelCount = sortedItems.Select(i => i.ModelId).Distinct().Count(),
                        HiddenCount = hiddenBySlot.GetValueOrDefault(g.Key)
                    };
                })
                .OrderBy(g => GetSlotOrder(g.SlotCategory))
                .ToList();

            // A slot can hide its way down to nothing, when hiding one of a pair drops both and
            // that was the slot's only match. It still gets a header, so the section reports what
            // became of it instead of disappearing; DrawInertGroupHeader draws these.
            var emptied = hiddenBySlot
                .Where(entry => !grouped.Any(g => g.SlotCategory == entry.Key))
                .Select(entry => new SharedModelGroup
                {
                    SlotCategory = entry.Key,
                    HiddenCount = entry.Value
                });

            // The Outfit section is built separately, off the raw entries, so it is concatenated
            // rather than falling out of the slot grouping above. GetSlotOrder puts it last.
            var outfitGroup = BuildOutfitGroup(outfitEntries, armoireItemIds);

            grouped = grouped
                .Concat(emptied)
                .Concat(outfitGroup == null ? [] : new[] { outfitGroup })
                .OrderBy(g => GetSlotOrder(g.SlotCategory))
                .ToList();

            sharedGroups = grouped;
            var totalItems = grouped.Sum(g => g.Items.Count);

            // Only the categories with something in them. The emptied ones are on screen to
            // explain themselves rather than because they are a result.
            var categoryCount = grouped.Count(g => g.Items.Count > 0);

            // "Redundant" rather than "with shared models": most are, but the ones held in both
            // stores are here on their own account and the headline should not misdescribe them.
            statusMessage = $"Found {totalItems} redundant items across {categoryCount} slot categories!";
            statusColor = UiStyle.BrightWhite;

            // Called out separately because it is the one number actionable without any comparing:
            // a second copy, in the Armoire or in the dresser itself, is a slot spent on nothing.
            // Counted off the same property the row tag is drawn from, so the headline and the
            // rows cannot disagree about what a perfect duplicate is.
            var perfectDuplicates = grouped.Sum(g => g.Items.Count(i => i.IsPerfectDuplicate));
            if (perfectDuplicates > 0)
                statusMessage += $" {perfectDuplicates} perfect duplicate{(perfectDuplicates == 1 ? "" : "s")}.";

            // Hiding is silent by design, which is why the count has to be stated somewhere -
            // otherwise a result that shrank has no explanation on screen at all. Whether any of
            // them are currently revealed is a per-section question the headers answer.
            //
            // Outfits are hidden through the same menu and belong in the same total, or hiding one
            // would take rows off the screen without changing a number on it.
            var totalHidden = hiddenCount + (outfitGroup?.HiddenCount ?? 0);
            if (totalHidden > 0)
                statusMessage += $" ({totalHidden} hidden)";

            // Logged because this runs on the draw path, which is an accepted compromise - the
            // figure to watch is the warm one, not the first build of a session.
            Plugin.Log.Debug($"Build took {buildTimer.Elapsed.TotalMilliseconds:F1} ms for {uniqueItems.Count} items");
        }
        catch (Exception ex)
        {
            statusMessage = $"Error: {ex.Message}";
            statusColor = UiStyle.StaleNotice;
            sharedGroups = null;
            Plugin.Log.Error(ex, "Error during dresser scan");
        }
    }

    /// <summary>
    /// The outfit grouping sitting at each dresser Slot, for the <c>[Outfit]</c> tag.
    ///
    /// An outfit is not a container: its pieces stay in their own equipment slots and are merely
    /// linked to it, so this adds no items to the pool. It answers a different question - whether
    /// the row in front of you is linked to a set you have - which nothing else on the row says.
    ///
    /// Keyed on Slot rather than on item id, and that is the point. Being linked to an outfit is a
    /// property of the <i>copy</i>: a piece can be part of a set while a second copy of the same
    /// item stands alone, and an id-keyed answer tags both. It also settles the pieces belonging
    /// to two sets, which an id-keyed answer has to name both owners of - the Slot says which
    /// grouping this copy actually hangs off.
    /// </summary>
    private Dictionary<uint, (uint SetId, string Name)> BuildOutfitsBySlot(List<PrismBoxItem> outfitEntries)
    {
        var bySlot = new Dictionary<uint, (uint SetId, string Name)>();

        foreach (var entry in outfitEntries)
        {
            var setId = BaseItemId(entry.ItemId);

            // TryAdd rather than an assignment: a Slot is not expected to hold two bundles, and if
            // one did, the first is as good an answer as the second.
            bySlot.TryAdd(entry.Slot, (setId, GetItemNameFromLumina(setId)));
        }

        return bySlot;
    }

    /// <summary>
    /// The outfit this particular copy is linked to, or empty. A list because the tooltip prints
    /// it as one, though a copy hangs off exactly one grouping.
    /// </summary>
    private static List<string> OutfitsFor(ItemEntry item, Dictionary<uint, (uint SetId, string Name)> outfitsBySlot)
    {
        // An Armoire-only entry carries Slot 0, which is a real dresser Slot - nothing links the
        // two, so it must not be looked up as though it did.
        if (!item.InDresser)
            return [];

        if (!outfitsBySlot.TryGetValue(item.Slot, out var outfit))
            return [];

        // The bundle row is the grouping, not a piece of it.
        var itemId = BaseItemId(item.ItemId);
        if (itemId == outfit.SetId)
            return [];

        return OutfitService.GetComponents(outfit.SetId).Contains(itemId) ? [outfit.Name] : [];
    }

    /// <summary>
    /// The Outfit section: outfits the dresser is holding under more than one grouping.
    ///
    /// Matched on the outfit's name, not on what is linked to it. Depositing a piece that could
    /// join an outfit you already have lets you start a fresh grouping instead, and taking that
    /// offer spends a second set of dresser slots on the same outfit - two groupings under one
    /// name is precisely the waste, whatever each of them currently holds.
    ///
    /// Fed the raw dresser entries rather than the merged pool: a grouping only exists in the
    /// dresser, and an Armoire row carries no Slot to be grouped by.
    /// </summary>
    private SharedModelGroup? BuildOutfitGroup(List<PrismBoxItem> outfitEntries, HashSet<uint> storedInArmoire)
    {
        // Same rule as every other section: hidden entries come out before the count test, so
        // hiding one half of a duplicated pair takes the other half with it rather than leaving a
        // run of one claiming to be a duplicate.
        var revealed = plugin.Configuration.ShowsHiddenIn(OutfitCategory);
        var hiddenCount = outfitEntries.Count(entry => plugin.Configuration.IsHidden(entry.ItemId));

        var items = outfitEntries
            .Where(entry => !plugin.Configuration.IsHidden(entry.ItemId) || revealed)
            .GroupBy(entry => GetItemNameFromLumina(entry.ItemId))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Select(entry => new SharedModelItem
            {
                Name = g.Key,
                ItemId = entry.ItemId,
                IconId = (int)entry.IconId,
                Slot = entry.Slot,
                // No model to carry, so the name goes here instead. It is what DrawModelRuns
                // brackets a run on and what the header counts as distinct.
                ModelId = g.Key,
                DyeCount = 0,
                CanGoInArmoire = CanGoInArmoire(entry.ItemId),
                InDresser = true,
                InArmoire = storedInArmoire.Contains(entry.ItemId),
                IsHq = entry.ItemId >= 1_000_000,
                IsHidden = plugin.Configuration.IsHidden(entry.ItemId),
                IsOutfit = true,
            }))
            .ToList();

        // No section at all unless there is something to say. One that has hidden its only match
        // still gets a header, greyed, so the reveal has something to right-click.
        if (items.Count == 0 && hiddenCount == 0)
            return null;

        return new SharedModelGroup
        {
            SlotCategory = OutfitCategory,
            Items = items,
            ModelCount = items.Select(i => i.ModelId).Distinct().Count(),
            HiddenCount = hiddenCount,
        };
    }

    /// <summary>One candidate for matching, and which of the two stores it came out of.</summary>
    private sealed record ItemEntry(
        uint ItemId,
        uint IconId,
        uint Slot,
        bool InDresser,
        bool InArmoire,
        int DresserCopies);

    /// <summary>
    /// Folds the Glamour Dresser and the Armoire into one pool, one entry per dresser copy.
    ///
    /// The stores are merged rather than matched separately because a redundant glamour is
    /// redundant wherever the two copies sit - an Armoire piece and a dresser piece sharing a mesh
    /// is exactly the case worth knowing about.
    ///
    /// An item held in both <i>stores</i> is one entry with both flags set rather than two rows:
    /// it is one item as far as the player is concerned, and splitting it would double-count it in
    /// the header counts and pad every run it appears in. An item held twice in the <i>dresser</i>
    /// is the opposite case and stays two rows, because there really are two of them.
    /// </summary>
    private static List<ItemEntry> MergeStores(List<PrismBoxItem> dresserItems, HashSet<uint> armoireItemIds)
    {
        // Keyed on the item and the dresser grouping it sits in, so one item stored twice stays
        // two entries. Keying on ItemId alone collapses them into a row that then matches nothing,
        // silently discarding the strongest finding the plugin can make.
        //
        // Slot is the id of the dresser's grouping rather than a storage position - a bundle and
        // its pieces all share one - so this is not "one entry per position". It is the narrowest
        // key that keeps two copies apart: the game lists a piece once even when it belongs to two
        // outfits the player owns, so a second row under a second Slot is a second copy. Repeats
        // of one (ItemId, Slot) are not, and still collapse here.
        var dresserEntries = new Dictionary<(uint ItemId, uint Slot), ItemEntry>();

        foreach (var item in dresserItems)
        {
            var key = (item.ItemId, item.Slot);
            if (dresserEntries.ContainsKey(key))
                continue;

            // The dresser holds HQ entries at ItemId + 1,000,000 while the Armoire only ever holds
            // base ids, so the membership test is on the raw id deliberately: an HQ dresser entry
            // never claims the NQ Armoire copy as its own, and the game shows them as two things.
            dresserEntries[key] = new ItemEntry(
                item.ItemId,
                item.IconId,
                item.Slot,
                InDresser: true,
                InArmoire: armoireItemIds.Contains(item.ItemId),
                DresserCopies: 1);
        }

        // Every copy carries the total, not its own ordinal, so any one of the rows can say the
        // item is stored more than once without the others having to be consulted.
        var copies = dresserEntries.Keys
            .GroupBy(key => key.ItemId)
            .ToDictionary(g => g.Key, g => g.Count());

        var merged = dresserEntries.Values
            .Select(entry => entry with { DresserCopies = copies[entry.ItemId] })
            .ToList();

        foreach (var itemId in armoireItemIds)
        {
            if (copies.ContainsKey(itemId))
                continue;

            // Nothing but the id comes out of the Armoire - no icon, no stains, no slot. The icon
            // is filled in from the Item sheet downstream, as it already is for a dresser entry
            // whose own IconId is unusable.
            merged.Add(new ItemEntry(itemId, 0, 0, InDresser: false, InArmoire: true, DresserCopies: 0));
        }

        return merged;
    }

    // Both are asked the same question about the same item several times during one build, and
    // each answer costs an Excel row fetch. Memoised for the duration of a build rather than
    // restructured, because the call sites are spread across a LINQ pipeline where threading a
    // precomputed value through would cost more clarity than it buys.
    //
    // Cleared at the top of BuildGroups, which is what keeps them honest: the model answer depends
    // on the recolour setting, and a setting change is itself what triggers a rebuild. Draw runs
    // on the framework thread only, so neither needs locking.
    private readonly Dictionary<uint, string> slotNameCache = [];
    private readonly Dictionary<uint, string> modelIdCache = [];

    private string GetItemModel(uint itemId)
    {
        if (modelIdCache.TryGetValue(itemId, out var cached))
            return cached;

        return modelIdCache[itemId] = ComputeItemModel(itemId);
    }

    private string ComputeItemModel(uint itemId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>()!;
        if (!sheet.TryGetRow(BaseItemId(itemId), out var item))
            return "Unknown";

        var model = ModelDetectionService.ExtractModelInfo(item.ModelMain, plugin.Configuration.CountRecoloursAsDuplicates);
        return ModelDetectionService.GetModelIdString(model);
    }

    /// <summary>
    /// The dresser stores HQ entries at ItemId + 1,000,000 while the Item sheet only holds the
    /// base row, so every sheet lookup has to be normalised or an HQ item resolves to nothing.
    ///
    /// Only sheet lookups: the raw ItemId stays the dedup identity, so an HQ item and its NQ twin
    /// remain two dresser entries, which is what the game shows.
    /// </summary>
    private static uint BaseItemId(uint itemId)
        => itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;

    private string GetSlotName(uint itemId)
    {
        if (slotNameCache.TryGetValue(itemId, out var cached))
            return cached;

        return slotNameCache[itemId] = ComputeSlotName(itemId);
    }

    private string ComputeSlotName(uint itemId)
    {
        if (itemId == 0)
            return "Unknown Slot";

        var sheet = Plugin.DataManager.GetExcelSheet<Item>()!;
        if (!sheet.TryGetRow(BaseItemId(itemId), out var item))
            return "Unknown Slot";

        if (!item.EquipSlotCategory.IsValid)
            return "Unknown Slot";

        // Outfit bundles point at EquipSlotCategory row 0, where every slot field is zero. They
        // are real dresser entries rather than junk.
        if (item.EquipSlotCategory.RowId == 0)
            return "Outfit";

        var category = item.EquipSlotCategory.Value;

        // First slot the category fills wins - a garment only ever fills one.
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

    /// <summary>Every item the Cabinet sheet says is Armoire-eligible. Built once.</summary>
    private static HashSet<uint>? armoireItemIds;

    /// <summary>
    /// Whether the Armoire will take this item at all - a sheet fact, independent of whether it
    /// is in there. A set rather than a linear scan because this runs per item, and the results
    /// rebuild themselves as the dresser changes.
    /// </summary>
    private bool CanGoInArmoire(uint itemId)
    {
        if (itemId == 0)
            return false;

        armoireItemIds ??= Plugin.DataManager.GetExcelSheet<Cabinet>()!
            .Select(row => row.Item.RowId)
            .ToHashSet();

        return armoireItemIds.Contains(BaseItemId(itemId));
    }

    /// <summary>Sort key for the sections, head to foot and then accessories, with Outfit last.</summary>
    private int GetSlotOrder(string slotName)
    {
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

    /// <summary>
    /// One colour per family of slots, so a section is identifiable before its label is read.
    /// Outfits sit outside the three, being a grouping the dresser lays over the slots rather
    /// than another slot.
    /// </summary>
    private Vector4 GetColorForSlot(string slotName)
    {
        if (slotName == OutfitCategory)
        {
            return UiStyle.OutfitAccent;
        }

        if (slotName == "Ears" || slotName == "Neck" || slotName == "Wrists" || slotName == "Ring")
        {
            return UiStyle.LightPurple;
        }

        if (slotName == "Head" || slotName == "Body" || slotName == "Gloves" || slotName == "Legs" || slotName == "Feet")
        {
            return UiStyle.LightMagenta;
        }

        if (slotName == "Main Hand" || slotName == "Off Hand")
        {
            return UiStyle.LightMintGreen;
        }

        return UiStyle.LightPurple;
    }
}

/// <summary>One section of the results: a slot category and the rows filed under it.</summary>
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

/// <summary>One row of the results.</summary>
public class SharedModelItem
{
    public string Name { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public int IconId { get; set; }
    public uint Slot { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public byte DyeCount { get; set; }

    /// <summary>Eligible for the Armoire - a Cabinet sheet fact, true whether or not it is in there.</summary>
    public bool CanGoInArmoire { get; set; }

    /// <summary>Held in the Glamour Dresser right now. Both this and <see cref="InArmoire"/> can be true.</summary>
    public bool InDresser { get; set; }

    /// <summary>
    /// Held in the Armoire right now. False while the Armoire has never been read, which is why
    /// <see cref="ArmoireScanner.HasData"/> has to be consulted before drawing any conclusion
    /// from it being false.
    /// </summary>
    public bool InArmoire { get; set; }

    public bool IsHq { get; set; }
    public bool IsHidden { get; set; }

    /// <summary>
    /// This row is an outfit bundle in the Outfit section, rather than a garment. Changes what
    /// its <see cref="ModelId"/> means - an outfit has no model of its own, so the field carries
    /// the outfit's name and the run brackets entries of the same outfit.
    /// </summary>
    public bool IsOutfit { get; set; }

    /// <summary>
    /// How many times the Glamour Dresser is holding this exact item. Normally 1; more than that
    /// is the same item stored twice over, each copy costing its own dresser slot. 0 for a row
    /// that came out of the Armoire alone.
    /// </summary>
    public int DresserCopies { get; set; }

    /// <summary>
    /// A dresser slot spent on something already held - true without comparing the item to
    /// anything else.
    ///
    /// Two ways to be one, deliberately under a single name: a copy in the Armoire, which stores
    /// it for free, or a second copy in the dresser itself. What the player does about it is the
    /// same either way, and telling them apart is what the row's tooltip is for.
    /// </summary>
    public bool IsPerfectDuplicate => (InDresser && InArmoire) || DresserCopies > 1;

    /// <summary>
    /// The outfit grouping this particular copy is linked to, by name. Empty for most rows, and
    /// always empty on an outfit row - a bundle is not a piece of itself. What the
    /// <c>[Outfit]</c> tag is drawn from.
    ///
    /// Per copy, not per item: a second copy of a piece, stored on its own, is linked to nothing
    /// even while the first is part of a set.
    /// </summary>
    public List<string> Outfits { get; set; } = [];
}
