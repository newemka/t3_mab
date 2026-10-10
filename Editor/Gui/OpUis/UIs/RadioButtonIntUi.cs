#nullable enable
using ImGuiNET;
using T3.Core.Operator;
using T3.Core.Operator.Slots;
using T3.Core.Utils;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using Vector2 = System.Numerics.Vector2;

namespace T3.Editor.Gui.OpUis.UIs;

/// <summary>
/// Draws the button grid for picking an integer value.
/// </summary>
/// <remarks>
/// All labels use <see cref="Fonts.FontLarge"/> and never a font picked by the height of the buttons. Both their size
/// and the padding of the grid are derived from the zoom of the <see cref="ScalableCanvas"/> (see
/// <see cref="GetNormalizedZoom"/>), so the whole UI scales with the graph. On top of that, the labels are capped to
/// the width of their button, so that even two digit numbers are not clipped in wide grids.
/// The labels are drawn as text with an explicit font size (<c>drawList.AddText(font, fontSize, ...)</c>) instead of
/// letting <see cref="ImGui.Button"/> draw them: ImGui centers and clips its label against a font size and frame
/// padding that are independent of the canvas zoom, which makes labels drift out of their buttons when zooming.
/// </remarks>
internal static class RadioButtonIntUi
{
    private const int MaxCols = 8;
    private const int MaxRows = 2;

    /// <summary>Gap between buttons and inset of the grid at a normalized canvas zoom of 1.</summary>
    private const float ButtonSpacing = 2;

    /// <summary>Left inset of the value text at a normalized canvas zoom of 1.</summary>
    private const float ValueTextInset = 10;

    /// <summary>The operator name above the buttons is a caption, so it is drawn smaller than the button labels.</summary>
    private const float CaptionFontFactor = 0.5f;

    /// <summary>
    /// Limits of the canvas derived font scale: keeps labels legible when zoomed far out and limits how much the
    /// pre-rasterized <see cref="Fonts.FontLarge"/> gets blurred when zoomed in.
    /// </summary>
    private const float MinFontScale = 0.35f;

    private const float MaxFontScale = 1.5f;

    // Preallocated so the per-frame draw doesn't build strings
    private static readonly string[] _buttonLabels =
        {
            "0", "1", "2", "3", "4", "5", "6", "7",
            "8", "9", "10", "11", "12", "13", "14", "15"
        };

    private sealed class Binding : OpUiBinding
    {
        internal Binding(Instance instance)
        {
            IsValid = AutoBind(instance);
        }

        [BindInput("2d0e4b7d-3e27-4b1b-8f95-5b4625174f93")]
        internal readonly InputSlot<int> Int = null!;

        [BindInput("50fd8244-7d0d-4d14-91e5-0eada9968b44")]
        internal readonly InputSlot<T3.Core.DataTypes.Vector.Int2> ColsRows = null!;
    }

    internal static OpUi.CustomUiResult DrawChildUi(Instance instance,
                                                    ImDrawListPtr drawList,
                                                    ImRect selectableScreenRect,
                                                    ScalableCanvas canvas,
                                                    ref OpUiBinding? data1)
    {
        data1 ??= new Binding(instance);
        var data = (Binding)data1;

        if (!data.IsValid)
            return OpUi.CustomUiResult.None;

        var isAnimated = instance.Parent?.Symbol.Animator.IsInputSlotAnimated(data.Int) ?? false;
        var isConnected = data.Int.HasInputConnections;
        var canEdit = !isAnimated && !isConnected;

        var colsRows = data.ColsRows.GetCurrentValue();
        var cols = Math.Clamp(colsRows.X, 0, MaxCols);
        var rows = Math.Clamp(colsRows.Y, 0, MaxRows);
        var showButtons = canEdit && cols > 0 && rows > 0;

        var value = (isAnimated || isConnected)
                        ? data.Int.Value
                        : data.Int.TypedInputValue.Value;

        // Everything in this UI is scaled by the zoom of the canvas: the one font, the padding of the grid and
        // the insets. Nothing may depend on the screen size of the buttons, because that would mix the zoom of
        // the graph with the layout of the grid.
        var normalizedZoom = GetNormalizedZoom(canvas);
        var fontScale = normalizedZoom.Clamp(MinFontScale, MaxFontScale);
        var spacing = ButtonSpacing * normalizedZoom;

        if (showButtons)
            DrawNameAbove(instance, drawList, selectableScreenRect, fontScale * CaptionFontFactor);

        ImGui.PushClipRect(selectableScreenRect.Min, selectableScreenRect.Max, true);

        if (showButtons)
        {
            DrawButtonGrid(instance, data, drawList, selectableScreenRect, cols, rows, value, fontScale, spacing);
        }
        else
        {
            DrawValueText(instance, drawList, selectableScreenRect, value, fontScale, normalizedZoom);
        }

        ImGui.PopClipRect();

        return OpUi.CustomUiResult.Rendered
               | OpUi.CustomUiResult.PreventOpenSubGraph
               | OpUi.CustomUiResult.PreventInputLabels
               | OpUi.CustomUiResult.PreventTooltip;
    }

    private static void DrawButtonGrid(Instance instance,
                                       Binding data,
                                       ImDrawListPtr drawList,
                                       ImRect rect,
                                       int cols,
                                       int rows,
                                       int currentValue,
                                       float fontScale,
                                       float spacing)
    {
        var padding = new Vector2(spacing, spacing);
        var min = rect.Min + padding;
        var size = rect.Max - rect.Min - padding * 2;

        var buttonW = (size.X - spacing * (cols - 1)) / cols;
        var buttonH = (size.Y - spacing * (rows - 1)) / rows;
        if (buttonW < 1 || buttonH < 1)
            return;

        var buttonSize = new Vector2(buttonW, buttonH);

        PushScaledFont(fontScale);

        // The zoom of the canvas defines the size of the labels, but two digit numbers in a wide grid would be
        // wider than their button. Cap the size so that they always fit; this can only shrink the labels.
        var availableLabelWidth = buttonW - 2 * spacing;
        var widestLabelWidth = 0f;
        for (var i = Math.Min(cols * rows, _buttonLabels.Length) - 1; i >= 0; i--)
        {
            widestLabelWidth = MathF.Max(widestLabelWidth, ImGui.CalcTextSize(_buttonLabels[i]).X);
        }

        if (widestLabelWidth > availableLabelWidth && widestLabelWidth > 0)
        {
            SetScaledFont(fontScale * MathF.Max(availableLabelWidth, 1f) / widestLabelWidth);
        }

        var fontSize = ImGui.GetFontSize();
        ImGui.PushID(instance.SymbolChildId.GetHashCode());

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var index = row * cols + col;
                var isSelected = index == currentValue;
                var buttonMin = min + new Vector2(col * (buttonW + spacing),
                                                  row * (buttonH + spacing));

                ImGui.SetCursorScreenPos(buttonMin);

                ImGui.PushID(index);
                if (isSelected)
                {
                    // The selected button keeps its background while hovering or pressing it.
                    var selectedColor = UiColors.BackgroundActive.Rgba;
                    ImGui.PushStyleColor(ImGuiCol.Button, selectedColor);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, selectedColor);
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, selectedColor);
                }

                // The button only draws its frame, the label is drawn below with an explicit size.
                if (ImGui.Button("##cell", buttonSize))
                {
                    SetValue(data, index);
                }

                if (isSelected)
                    ImGui.PopStyleColor(3);

                var label = _buttonLabels[index];
                var labelSize = ImGui.CalcTextSize(label);
                var labelPos = Vector2.Round(buttonMin + (buttonSize - labelSize) * 0.5f);

                drawList.PushClipRect(buttonMin, buttonMin + buttonSize, true);
                drawList.AddText(Fonts.FontLarge, fontSize, labelPos, UiColors.Text, label);
                drawList.PopClipRect();

                ImGui.PopID();
            }
        }

        ImGui.PopID();
        PopScaledFont();
    }

    private static void DrawValueText(Instance instance,
                                      ImDrawListPtr drawList,
                                      ImRect rect,
                                      int value,
                                      float fontScale,
                                      float normalizedZoom)
    {
        var name = instance.SymbolChild.Name;
        var text = string.IsNullOrWhiteSpace(name) ? $"{value:0}" : $"{name}: {value:0}";

        PushScaledFont(fontScale);
        drawList.AddText(Fonts.FontLarge,
                         ImGui.GetFontSize(),
                         rect.Min + new Vector2(ValueTextInset * normalizedZoom, 0),
                         UiColors.Text,
                         text);
        PopScaledFont();
    }

    private static void DrawNameAbove(Instance instance, ImDrawListPtr drawList, ImRect rect, float fontScale)
    {
        var name = instance.SymbolChild.Name;
        if (string.IsNullOrWhiteSpace(name))
            return;

        PushScaledFont(fontScale);
        var textSize = ImGui.CalcTextSize(name);
        var pos = new Vector2(rect.Min.X + 2, rect.Min.Y - textSize.Y - 2);

        // The caption is drawn outside of ImGui's item layout, so it gets the font size explicitly.
        drawList.AddText(Fonts.FontLarge, ImGui.GetFontSize(), pos, UiColors.Text, name);
        PopScaledFont();
    }

    /// <summary>
    /// Converts the zoom of the canvas into the scale of <see cref="Fonts.FontLarge"/> and the padding of this UI.
    /// </summary>
    /// <remarks>
    /// The font atlas is rasterized with <see cref="T3Ui.UiScaleFactor"/> applied (see UiContentUpdate), so dividing
    /// the zoom by it yields a factor of 1 for the canvas zoom the font was rasterized for. Hence the UI keeps a
    /// constant size on the canvas and none of this depends on the DPI of the display.
    /// </remarks>
    private static float GetNormalizedZoom(ScalableCanvas canvas)
    {
        var normalizedZoom = MathF.Abs(canvas.Scale.Y) / T3Ui.UiScaleFactor;
        return float.IsFinite(normalizedZoom) ? normalizedZoom : 1f;
    }

    /// <summary>
    /// Pushes <see cref="Fonts.FontLarge"/> at the given scale, so that <see cref="ImGui.CalcTextSize"/> measures
    /// the size the labels are drawn with. Must be restored with <see cref="PopScaledFont"/>.
    /// </summary>
    private static void PushScaledFont(float fontScale)
    {
        ImGui.PushFont(Fonts.FontLarge);
        SetScaledFont(fontScale);
    }

    /// <summary>Rescales the pushed font, see <see cref="PushScaledFont"/>.</summary>
    private static void SetScaledFont(float fontScale)
    {
        ImGui.SetWindowFontScale(fontScale.Clamp(0.01f, 10f));
    }

    private static void PopScaledFont()
    {
        ImGui.SetWindowFontScale(1);
        ImGui.PopFont();
    }

    private static void SetValue(Binding data, int newValue)
    {
        data.Int.TypedInputValue.Value = newValue;
        data.Int.Input.IsDefault = false;
        data.Int.DirtyFlag.ForceInvalidate();
    }
}
