using ImGuiNET;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using T3.Core.Resource;
using T3.Core.Utils;
using T3.Editor.App;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Gui.Windows.Layouts;
using T3.Editor.UiModel;
using Icon = T3.Editor.Gui.Styling.Icon;
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

        DrawInnerContent();
    }

    internal override IReadOnlyList<Window> GetInstances()
    {
        throw new NotImplementedException();
    }

    private static void DrawInnerContent()
    {
        ImGui.Indent(10);
        /*ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5, 5));
        ImGui.AlignTextToFramePadding();*/
       /* ImGui.Text("Display Layout: ");
        ImGui.SameLine();*/
       
       // ImGui.PopStyleVar();
        FormInputs.AddVerticalSpace(10);

        var screens = Screen.AllScreens;

        // Draw visual screen layout
      

       /* var secondOutput = WindowManager.ShowSecondaryRenderWindow;
        if (ImGui.Checkbox("Enable Output Window", ref secondOutput))
        {
            WindowManager.ShowSecondaryRenderWindow = secondOutput;
            if (secondOutput)
            {
                // When enabling, apply current spanning settings
                ProgramWindows.UpdateViewerWindowState();
            }
        }*/

     
        ImGui.Unindent(10);
        DrawScreenLayout(screens);
        ImGui.Indent(10);


        // Add some space after the visualization
        FormInputs.AddVerticalSpace(20);
        var windowWidth = ImGui.GetWindowWidth() - 12;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5);
        if (ImGui.Button("Windows Display settings"+new string(' ',5) ))
        {
            OpenWindowsDisplaySettings();
        }
       // ImGui.SameLine();
        Icons.DrawIconOnLastItem(Icon.OpenExternally, UiColors.Text,.99f);
        CustomComponents.TooltipForLastItem("Open Windows display settings to configure screen arrangement, resolution, etc.");
        FormInputs.AddVerticalSpace(20);
        if (ImGui.CollapsingHeader("Extra details and dev stuff", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            ImGui.Text("Select screens for Output Window:");
            CustomComponents.TooltipForLastItem("The red rectangle represents the spanning area for the Output Window (Viewer).");

            foreach (var screen in screens)
            {
                var screenIndex = Array.IndexOf(screens, screen);
                ImGui.PushID($"textscreen_span_{screenIndex}");

                // Check if this screen is currently in the spanning area
                var isPartOfSpanning = IsScreenInSpanningArea(screen, UserSettings.Config.OutputArea);
                var wasPartOfSpanning = isPartOfSpanning; // Store original value

                if (ImGui.Checkbox("", ref isPartOfSpanning))
                {
                    // Checkbox was toggled
                    if (isPartOfSpanning && !wasPartOfSpanning)
                    {
                        // Checkbox was checked - check if main window overlaps before adding
                        if (WouldOverlapWithMainWindow(screen, screens))
                        {
                            _pendingScreenToAdd = screen;
                            _showOverlapWarning = true;
                        }
                        else
                        {
                            // No overlap, add immediately
                            AddScreenToSpanning(screen, screens);
                            ApplySpanningChanges();
                        }
                    }
                    else if (!isPartOfSpanning && wasPartOfSpanning)
                    {
                        // Checkbox was unchecked - remove screen from spanning
                        RemoveScreenFromSpanning(screen, screens);
                        ApplySpanningChanges();
                    }
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Include Screen {screenIndex + 1} in Output Window spanning area");
                }
                ImGui.SameLine();
                ImGui.Text($" Screen {screenIndex + 1}: {screen.Bounds.Width}x{screen.Bounds.Height} @ ({screen.Bounds.X},{screen.Bounds.Y})");
                ImGui.PopID();
            }

            // Show overlap warning dialog
            /* if (_showOverlapWarning)
             {
                 ImGui.OpenPopup("Overlap Warning");
             }

             if (ImGui.BeginPopupModal("Overlap Warning", ref _showOverlapWarning, ImGuiWindowFlags.AlwaysAutoResize))
             {
                 ImGui.Text("Warning: The selected screen(s) overlap with the Main Editor window.");
                 ImGui.Text("This may cause the Editor interface to be covered by the Output Window.");
                 ImGui.Spacing();
                 ImGui.Text("Do you want to continue?");
                 ImGui.Spacing();

                 if (ImGui.Button("Yes", new Vector2(120, 0)))
                 {
                     if (_pendingScreenToAdd != null)
                     {
                         AddScreenToSpanning(_pendingScreenToAdd, screens);
                         ApplySpanningChanges();
                         _pendingScreenToAdd = null;
                     }
                     _showOverlapWarning = false;
                     ImGui.CloseCurrentPopup();
                 }
                 ImGui.SameLine();
                 if (ImGui.Button("No", new Vector2(120, 0)))
                 {
                     _pendingScreenToAdd = null;
                     _showOverlapWarning = false;
                     ImGui.CloseCurrentPopup();
                 }

                 ImGui.EndPopup();
             }*/


            FormInputs.AddVerticalSpace(10);

            // Display current selection
            ImGui.Text($"Selected for UI fullscreen: Screen {UserSettings.Config.FullScreenIndexMain + 1}");

            ImGui.Checkbox("Enable UI fullscreen", ref UserSettings.Config.FullScreen);

            // Display spanning information
            var spanningBounds = UserSettings.Config.OutputArea;
            //ImGui.Text($"Output Window spanning area:/n X={spanningBounds.X:0} Y={spanningBounds.Y:0} " +
            // $"Width={spanningBounds.Z:0} Height={spanningBounds.W:0}");
            ImGui.TextWrapped($"Output Window spanning area:\n X={spanningBounds.X:0} Y={spanningBounds.Y:0} " +
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
                ApplySpanningChanges();
            }
        }
        
        ImGui.Unindent(10);
    }

    private static void ShowAvailableScreensInformation(Screen[] screens)
    {
        if (ImGui.CollapsingHeader("Available Screens details", ImGuiTreeNodeFlags.CollapsingHeader))
        {
            // Display screen information in tree nodes
            for (var i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];

                if (ImGui.TreeNode($"Screen {i + 1}"))
                {
                    ImGui.BulletText($"Device Name: {screen.DeviceName}");
                    ImGui.BulletText($"Primary: {screen.Primary}");
                    ImGui.BulletText($"Bounds: {screen.Bounds}");
                    ImGui.BulletText($"Bits Per Pixel: {screen.BitsPerPixel}");

                    ImGui.TreePop();
                }

                ImGui.Separator();
            }

            ImGui.Text($"Total screens detected: {screens.Length}");
        }
    }

    private static void DrawScreenLayout(Screen[] screens)
    {
        
        // This is all what we have to do in oder to make the screen layout responsive
        var windowWidth = ImGui.GetWindowWidth() - 12; // A bit of a hack to avoid scrollbar issues, 12 pixels is the width of the scrollbar. 
        var baseScale = 0.1f * T3Ui.UiScaleFactor;
        var drawList = ImGui.GetWindowDrawList();
        var canvasPos = ImGui.GetCursorScreenPos();

        // Find the overall bounds of all screens to center the visualization
        var overallBounds = GetOverallScreenBounds(screens);

        // Calculate the needed area for the visualization at base scale
        var neededArea = new Vector2(overallBounds.Width, overallBounds.Height) * baseScale;

        // Define desired margins (in pixels)
        var horizontalMargin = 10f;
        var availableWidth = windowWidth - (horizontalMargin * 2);

        // Calculate scale factor to fit within available width (with margins)
        var scaleFactorX = availableWidth / neededArea.X;
        var finalScale = baseScale * MathF.Min(1, scaleFactorX);

        // Recalculate needed area with final scale
        neededArea = new Vector2(overallBounds.Width, overallBounds.Height) * finalScale;

        // Calculate center offset - this should be based on the final scaled size
        var centerOffsetX = (windowWidth - neededArea.X) * 0.5f;

        // Ensure the offset respects our minimum margin
        centerOffsetX = Math.Max(horizontalMargin, centerOffsetX);

        // Reserve space for the canvas first
        ImGui.InvisibleButton("screen_layout_canvas", neededArea);
        var canvasEndPos = ImGui.GetCursorScreenPos();

        ImGui.SetCursorScreenPos(canvasPos + new Vector2(centerOffsetX, 0));

        // Create a child window for the interactive elements to ensure proper hit testing
        ImGui.BeginChild("Editor screen selection", neededArea,false,ImGuiWindowFlags.NoScrollbar);
        {
            //Draw all the screen rectangles and labels
            foreach (var screen in screens)
            {
                var bounds = screen.Bounds;
                var screenIndex = Array.IndexOf(screens, screen);

                // Calculate scaled position and size relative to overall bounds
                var x = canvasPos.X + centerOffsetX + (bounds.X - overallBounds.X) * finalScale;
                var y = canvasPos.Y + (bounds.Y - overallBounds.Y) * finalScale;
                var width = bounds.Width * finalScale;
                var height = bounds.Height * finalScale;
                var min = new Vector2(x, y);

                // Draw screen rectangle
                //var color = screen.Primary ? UiColors.BackgroundButton.Rgba : new Vector4(0.2f, 0.5f, 0.8f, 1.0f);
                var color = UiColors.BackgroundButton.Rgba;
                drawList.AddRectFilled(min, new Vector2(x + width, y + height), ImGui.ColorConvertFloat4ToU32(color));

                // Draw border
                drawList.AddRect(min, new Vector2(x + width, y + height), ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 1)));
                //ImGui.Text($" Screen {screenIndex + 1}: {screen.Bounds.Width}x{screen.Bounds.Height} @ ({screen.Bounds.X},{screen.Bounds.Y})");
                // Draw screen label

                var label = $"{screenIndex + 1}";
                if (screen.Primary)
                    label += $" (Primary)";
                var resolutionLabel = $"{bounds.Width}x{bounds.Height}";

                var textSize = ImGui.CalcTextSize(label);
                var textPos = new Vector2(x + (width - textSize.X) * 0.5f, y + (height - textSize.Y) * 0.25f);
                drawList.AddText(textPos, UiColors.Text, label);
                
                var font = Fonts.FontSmall;
                ImGui.PushFont(font);
                var resolutionTextSize = ImGui.CalcTextSize(resolutionLabel);
                var resolutionTextPos = new Vector2(x + (width - resolutionTextSize.X) * 0.5f, resolutionTextSize.Y+5+ textPos.Y);// new Vector2(x + (width - resolutionTextSize.X) * 0.5f, y + (height - resolutionTextSize.Y) * 0.45f);
                drawList.AddText(font, font.FontSize, resolutionTextPos, UiColors.TextMuted, resolutionLabel);
                ImGui.PopFont();

                // Show an icon for the Screen where the UI will be Fullscreen
                /*if (UserSettings.Config.FullScreenIndexMain == screenIndex)
                {
                    // Icons.DrawIconAtScreenPosition(Icon.PopUp, iconMax, drawList, UiColors.Text);
                    //ImGui.SetCursorPos(min );
                    //ImGui.SameLine();
                    ImGui.SetCursorScreenPos(new Vector2(x - 92 * T3Ui.UiScaleFactor + width * .5f, y + height - 64 * T3Ui.UiScaleFactor));
                    ImGui.Image((IntPtr)SharedResources.t3logoAlphaTextureImageSrv, new Vector2(64, 64) * T3Ui.UiScaleFactor);
                }*/
            }


            // Draw interactive elements (radio buttons and checkboxes) on top
            foreach (var screen in screens)
            {
                var bounds = screen.Bounds;
                var screenIndex = Array.IndexOf(screens, screen);

                // Calculate scaled position and size relative to overall bounds
                var x = (bounds.X - overallBounds.X) * finalScale;
                var y = (bounds.Y - overallBounds.Y) * finalScale;
                var width = bounds.Width * finalScale;
                var height = bounds.Height * finalScale;

                // Position the t3 logo button 
                ImGui.SetCursorPos(new Vector2(x + width*.25f, y + height * .65f));

                ImGui.PushID($"screen_radio_{screenIndex}");
                var isSelected = UserSettings.Config.FullScreenIndexMain == screenIndex;
                /*if (ImGui.RadioButton("", isSelected))
                {
                    UserSettings.Config.FullScreenIndexMain = screenIndex;
                }*/
                var buttonSize = new Vector2(16, 16)*T3Ui.UiScaleFactor;
                if (CustomComponents.ToggleIconButton(ref isSelected, Icon.TixlLogo, buttonSize))
                {
                    UserSettings.Config.FullScreenIndexMain = screenIndex;
                }
                CustomComponents.TooltipForLastItem($"Set Screen {screenIndex + 1} as fullscreen display for Main window");
                /*if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Set Screen {screenIndex + 1} as fullscreen display for Main window");
                }*/
                ImGui.PopID();

                // Position the checkbox in top-right corner relative to the child window
                ImGui.SetCursorPos(new Vector2(x + width - width * .25f - buttonSize.X, y + height*.65f));

                ImGui.PushID($"screen_span_{screenIndex}");

                // Check if this screen is currently in the spanning area
                var isPartOfSpanning = IsScreenInSpanningArea(screen, UserSettings.Config.OutputArea);
                var wasPartOfSpanning = isPartOfSpanning; // Store original value
                
                if (CustomComponents.ToggleIconButton(ref isPartOfSpanning, Icon.PlayOutput, buttonSize))
                {
                    // Checkbox was toggled
                    if (isPartOfSpanning && !wasPartOfSpanning)
                    {
                        // Checkbox was checked - check if main window overlaps before adding
                        if (WouldOverlapWithMainWindow(screen, screens))
                        {
                            _pendingScreenToAdd = screen;
                            _showOverlapWarning = true;
                        }
                        else
                        {
                            // No overlap, add immediately
                            AddScreenToSpanning(screen, screens);
                            ApplySpanningChanges();
                        }
                    }
                    else if (!isPartOfSpanning && wasPartOfSpanning)
                    {
                        // Checkbox was unchecked - remove screen from spanning
                        RemoveScreenFromSpanning(screen, screens);
                        ApplySpanningChanges();
                    }
                }
                CustomComponents.TooltipForLastItem($"Include Screen {screenIndex + 1} in spanning area");
               /* if (ImGui.IsItemHovered())
                {
                    
                    ImGui.SetTooltip($"Include Screen {screenIndex + 1} in spanning area",);
                }*/
                ImGui.PopID();

                
            }
            
        }
        ImGui.EndChild();

        // Calculate the scaled spanning area relative to overall bounds
        var scaledSpanning = new Vector4(
            (UserSettings.Config.OutputArea.X - overallBounds.X) * finalScale,
            (UserSettings.Config.OutputArea.Y - overallBounds.Y) * finalScale,
            UserSettings.Config.OutputArea.Z * finalScale,
            UserSettings.Config.OutputArea.W * finalScale
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
        // Draw spanning area if defined
        // Count how many screens are currently selected for spanning
        var screensInSpanning = screens.Count(screen => IsScreenInSpanningArea(screen, UserSettings.Config.OutputArea));

        // Check if spanning area has gaps
        var hasGaps = IsSpanningAreaLargerThanScreens(screens, UserSettings.Config.OutputArea);

        // Draw spanning area only if more than one screen is selected AND the area is defined
        if (screensInSpanning > 1 && scaledSpanning.Z > 0 && scaledSpanning.W > 0)
        {
            // Draw the spanning area border
            drawList.AddRect(rectMin, rectMax, UiColors.BackgroundActive.Fade(0.5f), 0, ImDrawFlags.RoundCornersNone, 2);

            // Add a filled rectangle at the bottom for the label
            var labelHeight = 20 * T3Ui.UiScaleFactor; // Height of the label area
            var labelRectMin = new Vector2(rectMin.X, rectMax.Y);
            var labelRectMax = new Vector2(rectMax.X, rectMax.Y + labelHeight);
            drawList.AddRectFilled(labelRectMin, labelRectMax, UiColors.BackgroundActive.Fade(0.3f));

            // Add text - show warning if there are gaps
            ImGui.PushFont(Fonts.FontSmall);
            string text;
            if (hasGaps)
            {
                text = "Spanning (with gaps)";
                var textSize = ImGui.CalcTextSize(text);
                var textPos = new Vector2(
                    labelRectMin.X + (scaledSpanning.Z - textSize.X) * 0.5f,
                    labelRectMin.Y + (labelHeight - textSize.Y) * 0.5f
                );

                drawList.AddText(textPos, UiColors.Text, text);
                CustomComponents.TooltipForLastItem("The spanning area includes regions not covered by any selected screen. Open Windows Display settings to arrange your screen layout.");
            }
            else
            {
                text = "Spanning";
                var textSize = ImGui.CalcTextSize(text);
                var textPos = new Vector2(
                    labelRectMin.X + (scaledSpanning.Z - textSize.X) * 0.5f,
                    labelRectMin.Y + (labelHeight - textSize.Y) * 0.5f
                );

                drawList.AddText(textPos, UiColors.Text, text);
            }
            ImGui.PopFont();

        }

        // Set cursor position to continue after the visualization
        ImGui.SetCursorScreenPos(canvasEndPos);

    }

    private static bool WouldOverlapWithMainWindow(Screen screenToAdd, Screen[] allScreens)
    {
        // Get the main window's screen
        var mainScreenIndex = UserSettings.Config.FullScreenIndexMain;
        if (mainScreenIndex < 0 || mainScreenIndex >= allScreens.Length)
            return false;

        var mainScreen = allScreens[mainScreenIndex];

        // Check if the screen to add overlaps with the main screen
        return screenToAdd.Bounds.IntersectsWith(mainScreen.Bounds);
    }

    private static void ApplySpanningChanges()
    {
        var spanningBounds = UserSettings.Config.OutputArea;

        // Check if spanning area is defined
        if (spanningBounds.Z > 0 && spanningBounds.W > 0)
        {
            // Enable the output window if not already enabled
            if (!WindowManager.ShowSecondaryRenderWindow)
            {
                WindowManager.ShowSecondaryRenderWindow = true;
            }

            // Update the viewer window with the new spanning area
            // This will be picked up by the main update loop
            ProgramWindows.UpdateViewerSpanning(spanningBounds);
        }
        else
        {
            // No spanning area defined, back to windowed mode
            ProgramWindows.Viewer.SetSizeable();
        }
    }

    private static bool IsScreenInSpanningArea(Screen screen, Vector4 spanningArea)
    {
        if (spanningArea.Z == 0 || spanningArea.W == 0) // No spanning area defined
            return false;

        var screenBounds = screen.Bounds;

        // Check if the screen's bounds are completely within the spanning area
        return screenBounds.Left >= spanningArea.X &&
               screenBounds.Right <= spanningArea.X + spanningArea.Z &&
               screenBounds.Top >= spanningArea.Y &&
               screenBounds.Bottom <= spanningArea.Y + spanningArea.W;
    }

    private static void AddScreenToSpanning(Screen screen, Screen[] screens)
    {
        var currentBounds = UserSettings.Config.OutputArea;
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
        var currentBounds = UserSettings.Config.OutputArea;

        // Get all screens that are currently in the spanning area, excluding the one to remove
        var remainingScreens = screens.Where(s => IsScreenInSpanningArea(s, currentBounds) && s != screen).ToArray();

        // Update bounds with remaining screens
        UpdateSpanningBounds(remainingScreens);
    }

    private static void UpdateSpanningBounds(Screen[] selectedScreens)
    {
        if (selectedScreens.Length == 0)
        {
            UserSettings.Config.OutputArea = new Vector4(0, 0, 0, 0);
            return;
        }

        var minX = selectedScreens.Min(s => s.Bounds.X);
        var minY = selectedScreens.Min(s => s.Bounds.Y);
        var maxX = selectedScreens.Max(s => s.Bounds.Right);
        var maxY = selectedScreens.Max(s => s.Bounds.Bottom);

        UserSettings.Config.OutputArea = new Vector4(
            minX, minY,
            maxX - minX, maxY - minY
        );
    }

    private static void ClearSpanningSelection()
    {
        UserSettings.Config.OutputArea = new Vector4(0, 0, 0, 0);
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

    private static void OpenWindowsDisplaySettings()
    {
        try
        {
            // Modern Windows 10/11 way - opens directly to display settings
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:display",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // Fallback methods if the modern way fails
            try
            {
                // Alternative method 1 - Control panel display settings
                Process.Start("control", "desk.cpl,,3");
            }
            catch
            {
                // Alternative method 2 - Direct display properties
                try
                {
                    Process.Start("desk.cpl");
                }
                catch (Exception fallbackEx)
                {
                    // Log the error or show a message to the user
                    Debug.WriteLine($"Failed to open display settings: {ex.Message}");
                    Debug.WriteLine($"Fallback also failed: {fallbackEx.Message}");
                }
            }
        }
    }

    private static bool IsSpanningAreaLargerThanScreens(Screen[] screens, Vector4 spanningArea)
    {
        if (spanningArea.Z == 0 || spanningArea.W == 0)
            return false;

        // Get all screens that are currently in the spanning area
        var screensInSpanning = screens.Where(s => IsScreenInSpanningArea(s, spanningArea)).ToArray();

        if (screensInSpanning.Length == 0)
            return false;

        // Calculate the total area of all screens in the spanning area
        var totalScreenArea = screensInSpanning.Sum(s => s.Bounds.Width * s.Bounds.Height);

        // Calculate the spanning area
        var spanningAreaTotal = spanningArea.Z * spanningArea.W;

        // If spanning area is larger than the sum of screen areas, there are gaps
        return spanningAreaTotal > totalScreenArea;
    }

    private static bool _showOverlapWarning = false;
    private static Screen _pendingScreenToAdd = null;
}