using ImGuiNET;
using T3.Core.Operator;
using T3.Editor.Gui.Dialogs;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel.Commands;
using T3.Editor.UiModel.Commands.Graph;
using T3.Editor.UiModel.Modification;

namespace T3.Editor.Gui.Graph.Dialogs;

internal sealed class RenameInputDialog : ModalDialog
{
    public void Draw()
    {
        if (BeginDialog("Rename input"))
        {
            DrawContent();
            EndDialogContent();
        }

        EndDialog();
    }

    private static void DrawContent()
    {
        var symbol = _symbol;
        if (symbol == null)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        switch (_renameGate.Update("Renaming input…", "Rebuilding symbol package. This can take a few seconds."))
        {
            case BlockingActionGate.Phase.ShowMessage:
                return;

            case BlockingActionGate.Phase.Run:
                var inputDef = symbol.InputDefinitions.FirstOrDefault(i => i.Id == _inputId);
                if (inputDef == null)
                {
                    ImGui.TextUnformatted("invalid input");
                    return;
                }

                // Blocking (~3s): rebuild + assembly reload. User already saw the wait message.
                UndoRedoStack.AddAndExecute(
                    new RenameSlotCommand(symbol.Id, _inputId, inputDef.Name,
                                          _pendingInputName, isInput: true));
                ImGui.CloseCurrentPopup();
                return;
        }

        var isWindowAppearing = ImGui.IsWindowAppearing();

        FormInputs.SetIndentToLeft();
        FormInputs.AddHint($"Careful! This operation will modify the definition of {symbol.Name}.");
        if (symbol.Namespace.StartsWith("Lib"))
        {
            FormInputs.AddHint("This is library Operator. Modifying it might prevent migrating your projects to future versions of Tooll");
        }

        FormInputs.SetIndentToParameters();

        if (isWindowAppearing)
        {
            var inputDef = symbol.InputDefinitions.FirstOrDefault(i => i.Id == _inputId);
            if (inputDef != null)
                _newInputName = inputDef.Name;
        }

        SymbolModificationInputs.DrawFieldNameInput(symbol, "New Input name", "Input",
                                                    ref _newInputName, out var isValid);

        if (isWindowAppearing)
        {
            ImGui.SetKeyboardFocusHere();
        }

        FormInputs.ApplyIndent();

        if (CustomComponents.DrawCtaButton("Rename input", isValid, enableTriggerWithReturn: true))
        {
            if (InputsAndOutputs.RenameInput(symbol, _inputId, _newInputName, dryRun: true, out _))
            {
                _pendingInputName = _newInputName;
                _renameGate.Arm();
            }
        }

        ImGui.SameLine();
        if (CustomComponents.DrawCtaButton("Cancel", Icon.None, CustomComponents.ButtonStates.Emphasized))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    public void ShowNextFrame(Symbol symbol, Guid inputId)
    {
        ShowNextFrame();
        _symbol = symbol;
        _inputId = inputId;
    }

    private static readonly BlockingActionGate _renameGate = new();
    private static Symbol _symbol;
    private static Guid _inputId;
    private static string _newInputName = string.Empty;
    private static string _pendingInputName = string.Empty;
}