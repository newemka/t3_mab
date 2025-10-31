using ImGuiNET;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms; // Add this namespace
using T3.Core.Animation;
using T3.Core.Audio;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Vector;
using T3.Core.UserData;
using T3.Core.Utils;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel.ProjectHandling;
using Vector2 = System.Numerics.Vector2;

namespace T3.Editor.Gui.Windows;

internal sealed class ScreenManager : Window
{
    internal ScreenManager()
    {
        Config.Title = "Screen manager";
    }

    protected override void DrawContent()
    {
        FormInputs.AddVerticalSpace(15);

        ImGui.Indent(5);
        DrawInnerContent();
    }

    internal override IReadOnlyList<Window> GetInstances()
    {
        throw new NotImplementedException();
    }

    private void DrawInnerContent()
    {
        if (ImGui.CollapsingHeader("Available Screens", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var screens = Screen.AllScreens;

            // Display screen information in tree nodes
            for (var i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];

                if (ImGui.TreeNode($"Screen {i + 1}"))
                {
                    ImGui.BulletText($"Device Name: {screen.DeviceName}");
                    ImGui.BulletText($"Primary: {screen.Primary}");
                    ImGui.BulletText($"Bounds: {screen.Bounds}");
                    ImGui.BulletText($"Working Area: {screen.WorkingArea}");
                    ImGui.BulletText($"Bits Per Pixel: {screen.BitsPerPixel}");

                    ImGui.TreePop();
                }

                ImGui.Separator();
            }

            ImGui.Text($"Total screens detected: {screens.Length}");

            // Draw visual screen layout
            FormInputs.AddVerticalSpace(10);
            if (ImGui.CollapsingHeader("Screen Layout Visualization", ImGuiTreeNodeFlags.DefaultOpen))
            {
                DrawScreenLayout(screens);
            }
        }
    }

    private static void DrawScreenLayout(Screen[] screens)
    {
        var scale = 0.1f * T3Ui.UiScaleFactor;
        var drawList = ImGui.GetWindowDrawList();
        var canvasPos = ImGui.GetCursorScreenPos();

        // Find the overall bounds of all screens to center the visualization
        var overallBounds = GetOverallScreenBounds(screens);

        // Calculate offset to center the visualization
        var centerOffsetX = (ImGui.GetContentRegionAvail().X - (overallBounds.Width * scale)) * 0.5f;

        // Reserve space for the canvas first
        ImGui.InvisibleButton("screen_layout_canvas", new Vector2(overallBounds.Width * scale, overallBounds.Height * scale));
        var canvasEndPos = ImGui.GetCursorScreenPos();

        // First draw all the screen rectangles and labels
        foreach (var screen in screens)
        {
            var bounds = screen.Bounds;
            var screenIndex = Array.IndexOf(screens, screen);

            // Calculate scaled position and size relative to overall bounds
            var x = canvasPos.X + centerOffsetX + (bounds.X - overallBounds.X) * scale;
            var y = canvasPos.Y + (bounds.Y - overallBounds.Y) * scale;
            var width = bounds.Width * scale;
            var height = bounds.Height * scale;

            // Draw screen rectangle
            var color = screen.Primary ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f) : new Vector4(0.2f, 0.5f, 0.8f, 1.0f);
            drawList.AddRectFilled(new Vector2(x, y), new Vector2(x + width, y + height), ImGui.ColorConvertFloat4ToU32(color));

            // Draw border
            drawList.AddRect(new Vector2(x, y), new Vector2(x + width, y + height), ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)));

            // Draw screen label
            var label = $"Screen {screenIndex + 1}";
            if (screen.Primary)
                label += " (Primary)";

            var textSize = ImGui.CalcTextSize(label);
            var textPos = new Vector2(x + (width - textSize.X) * 0.5f, y + (height - textSize.Y) * 0.5f);
            drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), label);
        }

        
        ImGui.SetCursorScreenPos(canvasPos + new Vector2(centerOffsetX,0));

        // Create a child window for the interactive elements to ensure proper hit testing
        ImGui.BeginChild("Editor screen selection", new Vector2(overallBounds.Width * scale, overallBounds.Height * scale));
        {
            foreach (var screen in screens)
            {
                var bounds = screen.Bounds;
                var screenIndex = Array.IndexOf(screens, screen);

                // Calculate scaled position and size relative to overall bounds
                var x = (bounds.X - overallBounds.X) * scale;
                var y = (bounds.Y - overallBounds.Y) * scale;
                var width = bounds.Width * scale;
                var height = bounds.Height * scale;

                // Position the radio button in top-left corner relative to the child window
                ImGui.SetCursorPos(new Vector2(x + 5, y + 5));

                ImGui.PushID($"screen_radio_{screenIndex}");
                var isSelected = UserSettings.Config.FullScreenIndexMain == screenIndex;
                if (ImGui.RadioButton("", isSelected))
                {
                    UserSettings.Config.FullScreenIndexMain = screenIndex;
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Set Screen {screenIndex + 1} as fullscreen display for Main window");
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        // Set cursor position to continue after the visualization
        ImGui.SetCursorScreenPos(canvasEndPos);

        // Add some space after the visualization
        FormInputs.AddVerticalSpace(10);

        // Display current selection
        ImGui.Text($"Selected for fullscreen: Screen {UserSettings.Config.FullScreenIndexMain + 1}");

        ImGui.Checkbox("Enable fullscreen", ref UserSettings.Config.FullScreen);

        // Add a button to reset to primary screen
        if (ImGui.Button("Reset to Primary Screen"))
        {
            var primaryScreenIndex = Array.FindIndex(screens, s => s.Primary);
            if (primaryScreenIndex >= 0)
            {
                UserSettings.Config.FullScreenIndexMain = primaryScreenIndex;
            }
        }
    }

    private static Rectangle GetOverallScreenBounds(Screen[] screens)
    {
        if (screens.Length == 0)
            return new Rectangle(0, 0, 0, 0);

        var minX = screens.Min(s => s.Bounds.X);
        var minY = screens.Min(s => s.Bounds.Y);
        var maxX = screens.Max(s => s.Bounds.Right);
        var maxY = screens.Max(s => s.Bounds.Bottom);

        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }
}