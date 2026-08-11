using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Dispeller.Windows;

/// <summary>
/// The palette and the gradient title band, shared by every window, so the settings window and
/// the results window read as one plugin rather than as a themed window plus a bare ImGui form.
/// </summary>
internal static class UiStyle
{
    // The base palette: the gradient band, and the text drawn on and around it.
    internal static readonly Vector4 DarkerPurple = new(0.28f, 0.20f, 0.45f, 1.00f);
    internal static readonly Vector4 SoftMagenta  = new(0.78f, 0.37f, 0.64f, 1.00f);
    internal static readonly Vector4 BrightWhite  = new(1.00f, 1.00f, 1.00f, 1.00f);

    /// <summary>Section header text, which is drawn on the slot's own colour rather than on the background.</summary>
    internal static readonly Vector4 AshBlack = new(0.10f, 0.10f, 0.10f, 1.00f);

    // One colour per family of equipment slots, so a section is identifiable before its label is
    // read: main gear, accessories, weapons.
    internal static readonly Vector4 LightMagenta   = new(0.88f, 0.47f, 0.74f, 1.00f);
    internal static readonly Vector4 LightPurple    = new(0.65f, 0.60f, 0.80f, 1.00f);
    internal static readonly Vector4 LightMintGreen = new(0.50f, 0.85f, 0.75f, 1.00f);

    /// <summary>
    /// Explanatory text under a setting, and a row that is only on screen because "show hidden
    /// items" is on.
    /// </summary>
    internal static readonly Vector4 MutedText = new(0.62f, 0.60f, 0.68f, 1.00f);

    /// <summary>
    /// A section header with nothing left to show. Neutral rather than the slot's own colour, so
    /// it reads as switched off instead of as a dimmer version of an active section.
    /// </summary>
    internal static readonly Vector4 InertHeader = new(0.26f, 0.25f, 0.30f, 1.00f);

    /// <summary>
    /// Hover wash behind a result row. Deliberately faint: the row's job is to be readable, and
    /// the highlight only has to say which row a right-click will act on.
    /// </summary>
    internal static readonly Vector4 RowHover = new(0.78f, 0.37f, 0.64f, 0.22f);

    /// <summary>
    /// The Outfit section. Clear of the colours the equipment slots use, because an outfit is not
    /// another slot - it is a grouping the dresser makes over the others.
    /// </summary>
    internal static readonly Vector4 OutfitAccent = new(0.56f, 0.68f, 0.92f, 1.00f);

    /// <summary>
    /// The <c>[Outfit]</c> row tag, dimmer than the section colour: it appears on a large share of
    /// the rows in a full result, and a bright tag on most of the list is decoration rather than a
    /// signal. It has to be legible when skimmed for, not when skimmed past.
    /// </summary>
    internal static readonly Vector4 OutfitTag = new(0.50f, 0.58f, 0.74f, 1.00f);

    /// <summary>
    /// A dresser slot spent on something already held. Warm gold rather than another shade of the
    /// plugin's pink, so it does not read as decoration.
    /// </summary>
    internal static readonly Vector4 Attention = new(0.96f, 0.78f, 0.36f, 1.00f);

    /// <summary>
    /// The notices saying the results are not fully live - a store read from disk, or never read
    /// at all. Red because these qualify everything below them: whatever the rows claim, they
    /// claim it about data the plugin has not confirmed. One colour for all of them, so they read
    /// as one class of message rather than as warnings of varying seriousness.
    /// </summary>
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

        // Anchored to the window rather than the cursor, so the band sits flush with both edges
        // instead of inset by the padding on the left and overhanging by the same amount right.
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

        // Measured at the scale it will be drawn at - SetWindowFontScale feeds CalcTextSize.
        ImGui.SetWindowFontScale(TitleFontScale);
        var titleSize = ImGui.CalcTextSize(title);

        ImGui.SetCursorPosX((width - titleSize.X) / 2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (HeaderHeight - titleSize.Y) / 2);

        ImGui.PushStyleColor(ImGuiCol.Text, BrightWhite);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        ImGui.SetWindowFontScale(1.0f);

        // Land exactly on the bottom edge of the band. Letting the text's own advance stand and
        // then nudging it with spacings leaves dead space inside and below the band.
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + HeaderHeight));
    }

    /// <summary>
    /// Everything <see cref="DrawPinnedFooter"/> occupies: the separator, its manual 10px offset,
    /// and one line of text. A window has to leave this much room for its body.
    /// </summary>
    internal static float FooterHeight
        => 1 + ImGui.GetStyle().ItemSpacing.Y + 10 + ImGui.GetTextLineHeight();

    /// <summary>
    /// One centred line pinned to the bottom edge of the window, above a separator. Both windows
    /// are NoScrollbar so the gradient band stays flush with their edges, which leaves a footer
    /// nowhere to go if the body overflows - without the pin it is pushed past the bottom and,
    /// with no scrollbar, only reachable with the mouse wheel.
    ///
    /// The caller is responsible for having sized its body to leave <see cref="FooterHeight"/>
    /// free; this only positions itself.
    /// </summary>
    internal static void DrawPinnedFooter(string message, Vector4 color)
    {
        ImGui.SetCursorPosY(ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - FooterHeight);

        ImGui.Separator();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 10);

        // Clamped at zero: a message wider than the window would otherwise centre to a negative
        // offset, pushing its start off the left edge rather than merely clipping its end.
        var centre = Math.Max(0, (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(message).X) / 2);
        ImGui.SetCursorPosX(centre);

        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(message);
        ImGui.PopStyleColor();
    }
}
