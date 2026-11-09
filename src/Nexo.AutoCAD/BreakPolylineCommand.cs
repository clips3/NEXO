using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(Nexo.AutoCAD.BreakPolylineCommand))]

namespace Nexo.AutoCAD
{
    public sealed class BreakPolylineCommand
    {
        [NexoRibbonCommand(
            "QB",
            "Conversão",
            "Quebrar Polyline",
            "Divide cada polyline selecionada em segmentos separados, mantendo arcos, larguras e propriedades gráficas.",
            "icon_break_polyline.png")]
        [CommandMethod("QB", CommandFlags.UsePickSet)]
        public void BreakSelectedPolylines()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                ObjectId[] polylineIds = GetPolylineSelection(editor);
                if (polylineIds.Length == 0)
                {
                    editor.WriteMessage("\n[NEXO] Nenhuma polyline selecionada.");
                    return;
                }

                int processed = 0;
                int createdSegments = 0;
                Database database = document.Database;
                ObjectId currentSpaceId = database.CurrentSpaceId;

                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in polylineIds)
                    {
                        if (id.IsNull || !id.IsValid || id.IsErased)
                            continue;

                        Polyline source = transaction.GetObject(id, OpenMode.ForWrite, false) as Polyline;
                        if (source == null || source.OwnerId != currentSpaceId || source.NumberOfVertices < 2)
                            continue;

                        int segmentCount = source.Closed ? source.NumberOfVertices : source.NumberOfVertices - 1;
                        BlockTableRecord owner = transaction.GetObject(source.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                        if (owner == null)
                            continue;

                        for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
                        {
                            int nextIndex = (segmentIndex + 1) % source.NumberOfVertices;
                            Polyline segment = CreateSegment(database, source, segmentIndex, nextIndex);
                            owner.AppendEntity(segment);
                            transaction.AddNewlyCreatedDBObject(segment, true);
                            createdSegments++;
                        }

                        source.Erase();
                        processed++;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(processed == 0
                    ? "\n[NEXO] Nenhuma polyline leve pôde ser processada no espaço atual."
                    : "\n[NEXO] Processadas " + processed + " polyline(s) em " + createdSegments + " segmento(s) separado(s).");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n[NEXO] Falha em QB: " + exception.Message);
            }
        }

        private static ObjectId[] GetPolylineSelection(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            List<ObjectId> polylines = new List<ObjectId>();
            if (implied.Status == PromptStatus.OK && implied.Value != null)
            {
                foreach (ObjectId id in implied.Value.GetObjectIds())
                {
                    if (IsLightweightPolyline(id))
                        polylines.Add(id);
                }

                if (polylines.Count > 0)
                    return polylines.ToArray();
            }

            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });
            PromptSelectionResult selection = editor.GetSelection(filter);
            return selection.Status == PromptStatus.OK && selection.Value != null
                ? selection.Value.GetObjectIds()
                : Array.Empty<ObjectId>();
        }

        private static bool IsLightweightPolyline(ObjectId id)
        {
            return !id.IsNull && id.IsValid && !id.IsErased && id.ObjectClass != null &&
                   string.Equals(id.ObjectClass.DxfName, "LWPOLYLINE", StringComparison.OrdinalIgnoreCase);
        }

        private static Polyline CreateSegment(Database database, Polyline source, int startIndex, int endIndex)
        {
            Polyline segment = new Polyline(2);
            segment.SetDatabaseDefaults(database);
            segment.SetPropertiesFrom(source);
            segment.Normal = source.Normal;
            segment.Elevation = source.Elevation;
            segment.Thickness = source.Thickness;
            segment.ConstantWidth = source.ConstantWidth;
            segment.AddVertexAt(
                0,
                source.GetPoint2dAt(startIndex),
                source.GetBulgeAt(startIndex),
                source.GetStartWidthAt(startIndex),
                source.GetEndWidthAt(startIndex));
            segment.AddVertexAt(1, source.GetPoint2dAt(endIndex), 0.0, 0.0, 0.0);
            segment.Closed = false;
            return segment;
        }
    }
}
