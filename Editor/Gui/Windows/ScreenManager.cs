using ImGuiNET;
using System.Drawing;
using System.Windows.Forms;
using T3.Core.Utils;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.UiHelpers;
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

    private static void DrawInnerContent()
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
                    //ImGui.BulletText($"Working Area: {screen.WorkingArea}");
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
     
        

        ImGui.SetCursorScreenPos(canvasPos + new Vector2(centerOffsetX, 0));

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

                // Position the checkbox in top-right corner relative to the child window
                ImGui.SetCursorPos(new Vector2(x + width - 30 * T3Ui.UiScaleFactor, y + 5));

                ImGui.PushID($"screen_span_{screenIndex}");

                // Check if this screen is currently in the spanning area
                var isPartOfSpanning = IsScreenInSpanningArea(screen, UserSettings.Config.Rectangle);
                var wasPartOfSpanning = isPartOfSpanning; // Store original value

                if (ImGui.Checkbox("", ref isPartOfSpanning))
                {
                    // Checkbox was toggled
                    if (isPartOfSpanning && !wasPartOfSpanning)
                    {
                        // Checkbox was checked - add screen to spanning
                        AddScreenToSpanning(screen, screens);
                    }
                    else if (!isPartOfSpanning && wasPartOfSpanning)
                    {
                        // Checkbox was unchecked - remove screen from spanning
                        RemoveScreenFromSpanning(screen, screens);
                    }
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Include Screen {screenIndex + 1} in spanning area");
                }
                ImGui.PopID();

                
            }
        }
        ImGui.EndChild();

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

        // Calculate the scaled spanning area relative to overall bounds
        var scaledSpanning = new Vector4(
            (UserSettings.Config.Rectangle.X - overallBounds.X) * scale,
            (UserSettings.Config.Rectangle.Y - overallBounds.Y) * scale,
            UserSettings.Config.Rectangle.Z * scale,
            UserSettings.Config.Rectangle.W * scale
        );

        // Calculate the position on the canvas
        var rectMin = new Vector2(
            canvasPos.X + centerOffsetX + scaledSpanning.X,
            canvasPos.Y + scaledSpanning.Y
        );

        var rectMax = new Vector2(
            rectMin.X + scaledSpanning.Z,
            rectMin.Y + scaledSpanning.W
        );

        // Draw the spanning area rectangle
        drawList.AddRect(rectMin, rectMax, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 0, 0, 1)), 8.0f);

        // Set cursor position to continue after the visualization
        ImGui.SetCursorScreenPos(canvasEndPos);

        // Add some space after the visualization
        FormInputs.AddVerticalSpace(10);

        // Display current selection
        ImGui.Text($"Selected for fullscreen: Screen {UserSettings.Config.FullScreenIndexMain + 1}");

        ImGui.Checkbox("Enable fullscreen", ref UserSettings.Config.FullScreen);

        // Display spanning information
        var spanningBounds = UserSettings.Config.Rectangle;
        ImGui.Text($"Spanning area: X={spanningBounds.X:0} Y={spanningBounds.Y:0} " +
                   $"Width={spanningBounds.Z:0} Height={spanningBounds.W:0}");

        // Add a button to reset to primary screen
        if (ImGui.Button("Reset to Primary Screen"))
        {
            var primaryScreenIndex = Array.FindIndex(screens, s => s.Primary);
            if (primaryScreenIndex >= 0)
            {
                UserSettings.Config.FullScreenIndexMain = primaryScreenIndex;
            }
        }

        // Add a button to clear all spanning selections
        ImGui.SameLine();
        if (ImGui.Button("Clear Spanning Selection"))
        {
            ClearSpanningSelection();
        }
    }

    private static bool IsScreenInSpanningArea(Screen screen, Vector4 spanningArea)
    {
        if (spanningArea.Z == 0 || spanningArea.W == 0) // No spanning area defined
            return false;

        var screenBounds = screen.Bounds;

        // Check if the screen's bounds are completely within the spanning area
        // or if they significantly overlap (you can adjust this logic as needed)
        return screenBounds.Left >= spanningArea.X &&
               screenBounds.Right <= spanningArea.X + spanningArea.Z &&
               screenBounds.Top >= spanningArea.Y &&
               screenBounds.Bottom <= spanningArea.Y + spanningArea.W;
    }

    private static void AddScreenToSpanning(Screen screen, Screen[] screens)
    {
        var currentBounds = UserSettings.Config.Rectangle;
        var screenBounds = screen.Bounds;

        // Get all screens that are currently in the spanning area
        var currentScreens = screens.Where(s => IsScreenInSpanningArea(s, currentBounds)).ToList();

        // Add the new screen
        if (!currentScreens.Contains(screen))
            currentScreens.Add(screen);

        // Calculate new combined bounds
        UpdateSpanningBounds(currentScreens.ToArray());
    }

    private static void RemoveScreenFromSpanning(Screen screen, Screen[] screens)
    {
        var currentBounds = UserSettings.Config.Rectangle;

        // Get all screens that are currently in the spanning area, excluding the one to remove
        var remainingScreens = screens.Where(s => IsScreenInSpanningArea(s, currentBounds) && s != screen).ToArray();

        // Update bounds with remaining screens
        UpdateSpanningBounds(remainingScreens);
    }

    private static void UpdateSpanningBounds(Screen[] selectedScreens)
    {
        if (selectedScreens.Length == 0)
        {
            UserSettings.Config.Rectangle = new Vector4(0, 0, 0, 0);
            return;
        }

        var minX = selectedScreens.Min(s => s.Bounds.X);
        var minY = selectedScreens.Min(s => s.Bounds.Y);
        var maxX = selectedScreens.Max(s => s.Bounds.Right);
        var maxY = selectedScreens.Max(s => s.Bounds.Bottom);

        UserSettings.Config.Rectangle = new Vector4(
            minX, minY,
            maxX - minX, maxY - minY
        );
    }

    private static void ClearSpanningSelection()
    {
        UserSettings.Config.Rectangle = new Vector4(1920, 0, 640, 360);
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