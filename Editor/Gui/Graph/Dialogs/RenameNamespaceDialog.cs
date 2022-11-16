﻿using System.Text.RegularExpressions;
using Editor.Gui.Graph.Interaction;
using Editor.Gui.Styling;
using Editor.Gui.UiHelpers;
using Editor.Gui.Windows;
using ImGuiNET;
using T3.Editor.Gui.Windows;

namespace Editor.Gui.Graph.Dialogs
{
    public class RenameNamespaceDialog : ModalDialog
    {
        public void Draw(NamespaceTreeNode subtreeNodeToRename)
        {
            if (BeginDialog("Rename namespace"))
            {
                var dialogJustOpened = _node == null;
                if (dialogJustOpened)
                {
                    _node = subtreeNodeToRename;
                    _nameSpace = _node.GetAsString();
                }

                ImGui.PushFont(Fonts.FontSmall);
                ImGui.TextUnformatted("Namespace");
                ImGui.PopFont();

                ImGui.SetNextItemWidth(150);

                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();

                ImGui.InputText("##name", ref _nameSpace, 255);

                CustomComponents.HelpText("Careful now. This operator might affect a lot of operator definitions");
                ImGui.Spacing();

                if (CustomComponents.DisablableButton(
                                                      "Rename",
                                                      !string.IsNullOrEmpty(_nameSpace) 
                                                      && Regex.IsMatch(_nameSpace, @"^[\d\w_\.]+$")
                                                      ))
                {
                    NodeOperations.RenameNameSpaces(_node, _nameSpace);
                    Close();
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    Close();
                }

                EndDialogContent();
            }

            EndDialog();
        }

        private static void Close()
        {
            ImGui.CloseCurrentPopup();
            _node = null;
        }

        private static NamespaceTreeNode _node;
        private static string _nameSpace;
    }
}