using T3.Editor.Gui.Styling;
using T3.Editor.Gui.Input;

namespace T3.Editor.Gui.Dialogs;



/// <summary>
/// Helper for modal dialogs that need to run a blocking, multi-second operation
/// (e.g. rebuilding a symbol package after a rename) without freezing the UI
/// before the "please wait" message has actually been presented to the screen.
///
/// ImGui draws and (if you call it directly) blocks in the same pass, so running
/// the blocking action on the very frame the user confirms means the hint text
/// gets built but never painted before the freeze hits. This gate inserts one
/// extra frame between "user confirmed" and "action runs", so the message has a
/// chance to actually render first.
///
/// State machine: Idle -> (Arm) -> ShowMessage -> (next frame) -> Run -> (next frame) -> Idle.
///
/// Usage:
///   private readonly BlockingActionGate _renameGate = new();
///
///   private void DrawContent()
///   {
///       switch (_renameGate.Update("Renaming input…", "This can take a few seconds."))
///       {
///           case BlockingActionGate.Phase.ShowMessage:
///               return; // message was drawn this frame, stop here
///
///           case BlockingActionGate.Phase.Run:
///               UndoRedoStack.AddAndExecute(...); // the actual blocking call
///               ImGui.CloseCurrentPopup();
///               return;
///       }
///
///       // ...normal form...
///       if (DrawCtaButton("Rename"))
///           _renameGate.Arm();
///   }
///
/// Note this is intentionally NOT generic over an Action: building/capturing a
/// delegate would allocate on every idle frame the dialog is open, which is
/// wasteful for something that runs every frame while visible. The caller runs
/// its own code inline in the Run case instead.
/// </summary>
internal sealed class BlockingActionGate
{
    public enum Phase
    {
        Idle,
        ShowMessage,
        Run
    }

    private Phase _phase = Phase.Idle;

    /// <summary>True while a wait message is showing or the action is about to run.</summary>
    public bool IsPending => _phase != Phase.Idle;

    /// <summary>Arms the gate. Call this from the button that confirms the operation.</summary>
    public void Arm() => _phase = Phase.ShowMessage;

    /// <summary>
    /// Call at the very top of the dialog's Draw/DrawContent, before drawing anything else.
    /// - <see cref="Phase.Idle"/>: nothing pending, draw the normal form.
    /// - <see cref="Phase.ShowMessage"/>: the two-line hint was just drawn for you; the
    ///   caller should stop drawing and return immediately.
    /// - <see cref="Phase.Run"/>: the caller must run the blocking action now and return.
    /// </summary>
    public Phase Update(string title, string detail)
    {
        switch (_phase)
        {
            case Phase.ShowMessage:
                FormInputs.AddHint(title);
                FormInputs.AddHint(detail);
                _phase = Phase.Run;
                return Phase.ShowMessage;

            case Phase.Run:
                _phase = Phase.Idle;
                return Phase.Run;

            default:
                return Phase.Idle;
        }
    }
}
