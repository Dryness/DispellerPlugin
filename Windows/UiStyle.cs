using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Dispeller.Windows;

/// <summary>
/// The palette and the gradient title band, shared by every window. Pulled out of MainWindow
/// when the settings window arrived, so the two read as one plugin rather than as a themed
/// window plus a bare ImGui form.
/// </summary>
internal static class UiStyle
{
    // Darker purple + soft magenta + text colors
    internal static readonly Vector4 DarkerPurple = new(0.28f, 0.20f, 0.45f, 1.00f);  // deep purple
    internal static readonly Vector4 SoftMagenta  = new(0.78f, 0.37f, 0.64f, 1.00f);  // soft magenta
    internal static readonly Vector4 BrightWhite  = new(1.00f, 1.00f, 1.00f, 1.00f);  // white for most text
    internal static readonly Vector4 AshBlack     = new(0.10f, 0.10f, 0.10f, 1.00f);  // ash black for dropdown header text only

    // Light variants for UI elements
    internal static readonly Vector4 LightMagenta   = new(0.88f, 0.47f, 0.74f, 1.00f);  // lighter magenta (pink) for main gear
    internal static readonly Vector4 LightPurple    = new(0.65f, 0.60f, 0.80f, 1.00f);  // pastel purple for accessories (lighter, softer)
    internal static readonly Vector4 LightMintGreen = new(0.50f, 0.85f, 0.75f, 1.00f);  // minty green for weapons

    // Greyed-out explanatory text under a setting, and for a row that is only on screen
    // because "show hidden items" is on.
    internal static readonly Vector4 MutedText = new(0.62f, 0.60f, 0.68f, 1.00f);

    // A section header with nothing left to show. Neutral rather than the slot's own colour,
    // so it reads as switched off instead of as a dimmer version of an active section.
    internal static readonly Vector4 InertHeader = new(0.26f, 0.25f, 0.30f, 1.00f);

    // Hover wash behind a result row. Deliberately faint: the row's job is to be readable,
    // and the highlight only has to say "this is the one the right-click will act on".
    internal static readonly Vector4 RowHover = new(0.78f, 0.37f, 0.64f, 0.22f);

    // The Outfit section. A cool blue, clear of the magenta/purple/mint the equipment slots use,
    // because an outfit is not another slot - it is a grouping the dresser makes over the others.
    internal static readonly Vector4 OutfitAccent = new(0.56f, 0.68f, 0.92f, 1.00f);

    // The [Outfit] row tag. Deliberately dimmer than the section colour: over half the rows in a
    // full result carry it, and a bright tag on most of the list is decoration rather than a
    // signal. It has to be legible when skimmed for, not when skimmed past.
    internal static readonly Vector4 OutfitTag = new(0.50f, 0.58f, 0.74f, 1.00f);

    // An item held in both the Glamour Dresser and the Armoire - the one row on screen that is
    // pure waste, since the Armoire copy costs nothing and the dresser copy costs a slot. Warm
    // gold rather than another shade of the plugin's pink, so it does not read as decoration.
    internal static readonly Vector4 Attention = new(0.96f, 0.78f, 0.36f, 1.00f);

    // The notices that say the results are not fully live - a store read from disk rather than
    // from the game, or one never read at all. Red because these qualify everything below them:
    // whatever the rows claim, they are claiming it about data the plugin has not confirmed. The
    // three share one colour so they read as one class of message rather than three warnings of
    // varying seriousness.
    internal static readonly Vector4 StaleNotice = new(0.93f, 0.44f, 0.46f, 1.00f);

    internal const float HeaderHeight = 60f;
    private const float TitleFontScale = 1.6f;

    /// <summary>
    /// Draws the gradient band across the top of a window and leaves the cursor exactly on its
    /// bottom edge. The caller supplies the whole title string, glyphs included.
    /// </summary>
    internal static void DrawHeaderBand(string title)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();

        // Span the full window width. Anchoring to the window rather than the cursor keeps
        // the band flush with both edges instead of inset by the padding on the left and
        // overhanging by the same amount on the right.
        var left = ImGui.GetWindowPos().X;
        var width = ImGui.GetWindowSize().X;

        drawList.AddRectFilledMultiColor(
            new Vector2(left, origin.Y),
            new Vector2(left + width, origin.Y + HeaderHeight),
            ImGui.ColorConvertFloat4ToU32(SoftMagenta),
            ImGui.ColorConvertFloat4ToU32(DarkerPurple),
            ImGui.ColorConvertFloat4ToU32(DarkerPurple),
            ImGui.ColorConvertFloat4ToU32(SoftMagenta)
        );

        // Measure at the scale it will be drawn at - SetWindowFontScale feeds CalcTextSize.
        ImGui.SetWindowFontScale(TitleFontScale);
        var titleSize = ImGui.CalcTextSize(title);

        ImGui.SetCursorPosX((width - titleSize.X) / 2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (HeaderHeight - titleSize.Y) / 2);

        ImGui.PushStyleColor(ImGuiCol.Text, BrightWhite);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        ImGui.SetWindowFontScale(1.0f);

        // Land exactly on the bottom edge of the band. Letting the text's own advance stand,
        // then nudging it with spacings, was what left dead space inside and below the band.
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + HeaderHeight));
    }

    /// <summary>
    /// Everything <see cref="DrawPinnedFooter"/> occupies: the separator, its manual 10px
    /// offset, and one line of text. A window has to leave this much room for its body.
    /// </summary>
    internal static float FooterHeight
        => 1 + ImGui.GetStyle().ItemSpacing.Y + 10 + ImGui.GetTextLineHeight();

    /// <summary>
    /// One centred line pinned to the bottom edge of the window, above a separator. Both
    /// windows are NoScrollbar so the gradient band stays flush with their edges, which leaves
    /// a footer nowhere to go if the body overflows - without the pin it is pushed past the
    /// bottom and, with no scrollbar, only reachable with the mouse wheel.
    ///
    /// The caller is responsible for having sized its body to leave <see cref="FooterHeight"/>
    /// free; this only positions itself.
    /// </summary>
    internal static void DrawPinnedFooter(string message, Vector4 color)
    {
        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - FooterHeight);

        ImGui.Separator();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 10);

        // Clamped at zero: a message wider than the window would otherwise be centred to a
        // negative offset, pushing its start off the left edge rather than merely clipping.
        var centre = Math.Max(0, (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(message).X) / 2);
        ImGui.SetCursorPosX(centre);

        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(message);
        ImGui.PopStyleColor();
    }
}
