#nullable enable
using ImGuiNET;
using T3.Core.Operator;
using T3.Core.Operator.Slots;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using Vector2 = System.Numerics.Vector2;

namespace T3.Editor.Gui.OpUis.UIs;

internal static class DirectIntValueUi
{
    private const int MaxCols = 8;
    private const int MaxRows = 2;
    private const float ButtonSpacing = 2;

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

        ImGui.PushClipRect(selectableScreenRect.Min, selectableScreenRect.Max, true);

        if (showButtons)
        {
            DrawButtonGrid(instance, data, selectableScreenRect, cols, rows, value);
        }
        else
        {
            DrawValueText(instance, selectableScreenRect, value);
        }

        ImGui.PopClipRect();

        return OpUi.CustomUiResult.Rendered
               | OpUi.CustomUiResult.PreventOpenSubGraph
               | OpUi.CustomUiResult.PreventInputLabels
               | OpUi.CustomUiResult.PreventTooltip;
    }

    private static void DrawButtonGrid(Instance instance,
                                       Binding data,
                                       ImRect rect,
                                       int cols,
                                       int rows,
                                       int currentValue)
    {
        var padding = new Vector2(ButtonSpacing, ButtonSpacing);
        var min = rect.Min + padding;
        var size = rect.Max - rect.Min - padding * 2;

        var buttonW = (size.X - ButtonSpacing * (cols - 1)) / cols;
        var buttonH = (size.Y - ButtonSpacing * (rows - 1)) / rows;
        if (buttonW < 1 || buttonH < 1)
            return;

        var buttonSize = new Vector2(buttonW, buttonH);
        var font = buttonH > 40 ? Fonts.FontLarge : (buttonH > 22 ? Fonts.FontNormal : Fonts.FontSmall);

        ImGui.PushFont(font);
        ImGui.PushID(instance.SymbolChildId.GetHashCode());

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var index = row * cols + col;
                var isSelected = index == currentValue;

                ImGui.SetCursorScreenPos(min + new Vector2(col * (buttonW + ButtonSpacing),
                                                           row * (buttonH + ButtonSpacing)));

                ImGui.PushID(index);
                if (isSelected)
                    ImGui.PushStyleColor(ImGuiCol.Button, UiColors.BackgroundActive.Rgba);

                if (ImGui.Button(_buttonLabels[index], buttonSize))
                {
                    SetValue(instance, data, index);
                }

                if (isSelected)
                    ImGui.PopStyleColor();
                ImGui.PopID();
            }
        }

        ImGui.PopID();
        ImGui.PopFont();
    }

    private static void DrawValueText(Instance instance, ImRect rect, int value)
    {
        var h = rect.GetHeight();
        var font = h > 40 ? Fonts.FontLarge : (h > 25 ? Fonts.FontNormal : Fonts.FontSmall);

        ImGui.PushFont(font);
        ImGui.SetCursorScreenPos(rect.Min + new Vector2(10, 0));
        ImGui.BeginGroup();

        var name = instance.SymbolChild.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            ImGui.TextUnformatted(name + ":");
            ImGui.SameLine();
        }

        ImGui.TextUnformatted($"{value:0}");
        ImGui.EndGroup();
        ImGui.PopFont();
    }

    private static void SetValue(Instance instance, Binding data, int newValue)
    {
        // User-triggered, so allocations are fine here.
        data.Int.TypedInputValue.Value = newValue;
        data.Int.Input.IsDefault = false;
        data.Int.DirtyFlag.ForceInvalidate();
        // TODO: route through the undo system (ChangeInputValueCommand or whatever
        // the other op UIs use) so clicks are undoable and the symbol is marked modified.
    }
}