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

    // Greyed-out explanatory text under a setting.
    internal static readonly Vector4 MutedText = new(0.62f, 0.60f, 0.68f, 1.00f);

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
}
