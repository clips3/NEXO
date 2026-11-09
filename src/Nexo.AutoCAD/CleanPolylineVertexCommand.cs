using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(Nexo.AutoCAD.CleanPolylineVertexCommand))]

namespace Nexo.AutoCAD
{
    public sealed class CleanPolylineVertexCommand
    {
        [NexoRibbonCommand(
            "CLPV",
            "Conversão",
            "Clean Polyline Vertex",
            "Executa OVERKILL nos objetos selecionados, otimizando os segmentos das polylines.",
            "icon_clean_polyline_vertex.png")]
        [CommandMethod("CLPV", CommandFlags.UsePickSet)]
        public void CleanSelectedPolylineVertices()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                SelectionSet selection = GetSelection(editor);
                if (selection == null || selection.Count == 0)
                {
                    editor.WriteMessage("\n[NEXO] Nenhum objeto selecionado para limpeza.");
                    return;
                }

                editor.Command("_.-OVERKILL", selection, "", "_PL", "_Y", "");
                editor.WriteMessage("\n[NEXO] OVERKILL concluiu a limpeza dos objetos selecionados.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n[NEXO] Falha em CLPV: " + exception.Message);
            }
        }

        private static SelectionSet GetSelection(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
                return implied.Value;

            PromptSelectionResult selection = editor.GetSelection();
            return selection.Status == PromptStatus.OK ? selection.Value : null;
        }
    }
}
