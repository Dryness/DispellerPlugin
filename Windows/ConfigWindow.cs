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
        using var child = ImRaii.Child("SettingsBody", new Vector2(0, 0), false);
        if (!child)
            return;

        DrawSectionTitle("Window behaviour");

        DrawSetting(
            "Hide the window when you change zone",
            "Only hides it - /dispeller opens it again. Useful if you would rather not have it "
            + "follow you into a duty.",
            configuration.HideOnZoneChange,
            value => configuration.HideOnZoneChange = value);

        DrawSetting(
            "Open Dispeller with the Glamour Dresser",
            "Reacts to the dresser opening, not to it being open - so closing this window by "
            + "hand while the dresser is still up leaves it closed.",
            configuration.OpenWithGlamourDresser,
            value => configuration.OpenWithGlamourDresser = value);

        // Only offered while its parent is on, and MainWindow gates the behaviour on the
        // parent too - a sub-option that is out of sight should not still be acting.
        if (configuration.OpenWithGlamourDresser)
        {
            ImGui.Indent();

            DrawSetting(
                "Hide Dispeller when leaving the Glamour Dresser",
                "Puts the window away again when the dresser closes. Leave this off to keep "
                + "reading the results after you walk away.",
                configuration.HideWhenLeavingGlamourDresser,
                value => configuration.HideWhenLeavingGlamourDresser = value);

            ImGui.Unindent();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawSectionTitle("What counts as the same model");

        DrawSetting(
            "Count recolours as duplicates",
            "An item's model is a mesh plus a colour/material variant. With this on, only the "
            + "mesh has to match, so a recolour of a garment you already own is flagged as a "
            + "redundant glamour - which is the point of the plugin. Turn it off to require "
            + "the variant to match too; you will see far fewer results.",
            configuration.CountRecoloursAsDuplicates,
            value => configuration.CountRecoloursAsDuplicates = value);
    }

    private static void DrawSectionTitle(string title)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.LightMagenta);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();

        ImGui.Spacing();
    }

    /// <summary>
    /// One checkbox with its explanation underneath. Saving on change is what gives
    /// Configuration.Save() its first caller, and bumping the revision is what makes the
    /// results rebuild for settings that change them.
    /// </summary>
    private void DrawSetting(string label, string help, bool current, Action<bool> set)
    {
        var value = current;
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            configuration.Save();
        }

        ImGui.Indent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.PushStyleColor(ImGuiCol.Text, UiStyle.MutedText);
        ImGui.TextWrapped(help);
        ImGui.PopStyleColor();
        ImGui.Unindent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);

        ImGui.Spacing();
    }
}
