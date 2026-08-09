using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Dispeller.Windows;

/// <summary>
/// The plugin's settings. Every toggle writes straight through to disk on change - there is
/// no OK/Apply, so nothing can be lost by closing the window.
/// </summary>
public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin)
        : base("Dispeller Continued - Settings", ImGuiWindowFlags.NoScrollbar)
    {
        // Distinct from the original plugin's windows for the same reason the main window is:
        // the title doubles as the ImGui window ID, so anyone running both would get a clash.
        Size = new Vector2(520, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        configuration = plugin.Configuration;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        UiStyle.DrawHeaderBand($"{(char)SeIconChar.Hyadelyn} Settings {(char)SeIconChar.Hyadelyn}");

        ImGui.Spacing();

        // The window is NoScrollbar so the gradient band stays flush with its edges, which
        // leaves the body with nowhere to go if it overflows a small window. Scrolling the
        // body inside a child gives it somewhere.
        //
        // ImRaii.Child ends the child unconditionally - ImGui requires EndChild() even when
        // BeginChild() returns false.
        //
        // Scoped rather than left to the end of Draw(), because the footer has to be drawn
        // outside the child to pin to the window rather than to the scrolling body. A negative
        // height means "content region avail minus this much", keeping the body clear of it.
        using (var child = ImRaii.Child(
            "SettingsBody",
            new Vector2(0, -(UiStyle.FooterHeight + ImGui.GetStyle().ItemSpacing.Y)),
            false))
        {
            if (child)
                DrawSettings();
        }

        UiStyle.DrawPinnedFooter("Dispeller originally by pupwife. Continued by Dryness.", UiStyle.MutedText);
    }

    private void DrawSettings()
    {
        DrawSectionTitle("Window behaviour");

        DrawSetting(
            "Hide the window when you change zone",
            configuration.HideOnZoneChange,
            value => configuration.HideOnZoneChange = value);

        DrawSetting(
            "Open Dispeller with the Glamour Dresser",
            configuration.OpenWithGlamourDresser,
            value => configuration.OpenWithGlamourDresser = value);

        // Only offered while its parent is on, and MainWindow gates the behaviour on the
        // parent too - a sub-option that is out of sight should not still be acting.
        if (configuration.OpenWithGlamourDresser)
        {
            ImGui.Indent();

            DrawSetting(
                "Hide Dispeller when leaving the Glamour Dresser",
                configuration.HideWhenLeavingGlamourDresser,
                value => configuration.HideWhenLeavingGlamourDresser = value);

            ImGui.Unindent();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSectionTitle("Recolours");

        DrawSetting(
            "Count recolours as duplicates",
            "An item's model is a mesh plus a colour/material variant. With this on, only the "
            + "mesh has to match, so a recolour is flagged as a duplicate. Turn it off to require "
            + "the recolour base to match too.",
            configuration.CountRecoloursAsDuplicates,
            value => configuration.CountRecoloursAsDuplicates = value);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSectionTitle("Outfits");

        DrawSetting(
            "Tag items that belong to an outfit",
            "Show or hide the [Outfit] tag on items.",
            configuration.TagOutfitComponents,
            value => configuration.TagOutfitComponents = value);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSectionTitle("Hidden items");

        DrawHiddenItems();
    }

    /// <summary>
    /// The other half of the main window's right-click menu. Hiding is silent by design, so
    /// this is where the count lives and where a hide made months ago can be undone without
    /// having to remember which item it was.
    /// </summary>
    private void DrawHiddenItems()
    {
        var hiddenCount = configuration.HiddenCount;
        var revealed = configuration.RevealedSlotCount;

        DrawSetting(
            "Show hidden items in every category",
            "Right-clicking an item in the results hides it, and a hidden item is left out of "
            + "the duplicate check entirely - so whatever it matched goes with it. Turn this on "
            + "to list them all again with a [Hidden] tag.",
            configuration.ShowHiddenItems,
            value => configuration.ShowHiddenItems = value);

        // "on this character" is not padding: hides follow the character, like the dresser
        // they describe, and this is the only place that says so.
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.MutedText);
        ImGui.TextUnformatted(hiddenCount switch
        {
            0 => "Nothing is hidden on this character.",
            1 => "1 item is hidden on this character.",
            _ => $"{hiddenCount} items are hidden on this character."
        });

        // Only worth mentioning when it is doing something the toggle above is not.
        if (revealed > 0 && !configuration.ShowHiddenItems)
            ImGui.TextUnformatted($"{revealed} section{(revealed == 1 ? " is" : "s are")} showing hidden items.");

        ImGui.PopStyleColor();

        if (hiddenCount == 0 && revealed == 0)
            return;

        ImGui.Spacing();

        // Ctrl-held, the Dalamud convention for a button that cannot be undone. There is no
        // record of what was hidden once this runs, so a stray click would cost real work.
        var ctrl = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!ctrl))
        {
            if (ImGui.Button("Unhide everything on this character"))
                configuration.UnhideAll();
        }

        if (!ctrl)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.MutedText);
            ImGui.TextUnformatted("Hold Ctrl - this cannot be undone.");
            ImGui.PopStyleColor();
        }
    }

    private static void DrawSectionTitle(string title)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.LightMagenta);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();

        ImGui.Spacing();
    }

    /// <summary>
    /// A checkbox whose label says the whole thing, for the settings that need no explaining.
    /// </summary>
    private void DrawSetting(string label, bool current, Action<bool> set)
    {
        DrawCheckbox(label, current, set);

        ImGui.Spacing();
    }

    /// <summary>
    /// One checkbox with its explanation underneath, for the settings whose consequences are
    /// not obvious from the label.
    /// </summary>
    private void DrawSetting(string label, string help, bool current, Action<bool> set)
    {
        DrawCheckbox(label, current, set);

        // Indent the help to clear the checkbox, so it reads as belonging to it rather than
        // as a paragraph between two settings.
        var indent = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;

        ImGui.Indent(indent);
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.MutedText);
        ImGui.TextWrapped(help);
        ImGui.PopStyleColor();
        ImGui.Unindent(indent);

        ImGui.Spacing();
    }

    /// <summary>
    /// Saving on change is what gives Configuration.Save() its first caller, and bumping the
    /// revision is what makes the results rebuild for settings that change them.
    /// </summary>
    private void DrawCheckbox(string label, bool current, Action<bool> set)
    {
        var value = current;
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            configuration.Save();
        }
    }
}
