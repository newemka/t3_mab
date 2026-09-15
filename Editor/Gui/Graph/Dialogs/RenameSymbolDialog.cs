#nullable enable

using ImGuiNET;
using T3.Editor.Compilation;
using T3.Editor.Gui.Graph.Dialogs;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel;

namespace T3.Editor.Gui.Dialogs;


internal sealed class RenameSymbolDialog : ModalDialog
{
    internal void Draw(IEnumerable<SymbolUi.Child> selectedChildUis2, ref string name)
    {
        if (BeginDialog("Rename symbol"))
        {
            DrawContent(selectedChildUis2, ref name);
            EndDialogContent();
        }
        EndDialog();
    }

    // Split out so early returns below only exit this method — Draw() always
    // reaches EndDialogContent()/EndDialog(), keeping ImGui's group/popup
    // stack balanced. Returning early straight out of Draw() previously left
    // BeginDialog()'s group unclosed on those frames, which is what caused
    // the "group_data.WindowID == window->ID" assertion.
    private void DrawContent(IEnumerable<SymbolUi.Child> selectedChildUis2, ref string name)
    {
        var selectedChildUis = selectedChildUis2.ToList();

        if (selectedChildUis.Count != 1)
        {
            Log.Warning("Can't use RenameSymbolDialog without selected operator");
            ImGui.CloseCurrentPopup();
            return;
        }

        var symbolUi = selectedChildUis[0];
        var symbolChild = symbolUi.SymbolChild;
        if (symbolChild == null || symbolChild.Symbol.SymbolPackage.IsReadOnly)
        {
            Log.Warning("Can't use RenameSymbolDialog without selected operator");
            ImGui.CloseCurrentPopup();
            return;
        }

        var symbol = symbolChild.Symbol;

        switch (_renameGate.Update("Renaming symbol…", "Rebuilding symbol package. This can take a few seconds."))
        {
            case BlockingActionGate.Phase.ShowMessage:
                return;

            case BlockingActionGate.Phase.Run:
                // Blocking: rebuild + assembly reload. User already saw the wait message.
                SymbolNaming.RenameSymbol(symbol, _pendingName);
                ImGui.CloseCurrentPopup();
                return;
        }

        ImGui.PushFont(Fonts.FontSmall);
        ImGui.TextUnformatted("Name");
        ImGui.PopFont();

        ImGui.SetNextItemWidth(150);

        _ = SymbolModificationInputs.DrawSymbolNameInput(ref name,
                                                         symbol.Namespace,
                                                         symbol.SymbolPackage,
                                                         ImGui.IsWindowAppearing(),
                                                         out var isNameValid);

        ImGui.Spacing();

        if (CustomComponents.DrawCtaButton("Rename", isNameValid, enableTriggerWithReturn: true))
        {
            _pendingName = name;
            _renameGate.Arm();
        }

        ImGui.SameLine();
        if (CustomComponents.DrawCtaButton("Cancel", Icon.None, CustomComponents.ButtonStates.Emphasized))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private readonly BlockingActionGate _renameGate = new();
    private string _pendingName = string.Empty;
}