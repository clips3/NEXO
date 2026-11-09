using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(Nexo.AutoCAD.LayerSelectionCommands))]
[assembly: CommandClass(typeof(Nexo.AutoCAD.EllipseToPolylineCommand))]

namespace Nexo.AutoCAD
{
    public sealed class LayerSelectionCommands
    {
        private static PendingSelection _pendingSelection;

        [NexoRibbonCommand(
            "SL",
            "Seleção",
            "Selecionar layer",
            "Seleciona objetos das layers escolhidas, incluindo referências de blocos.",
            "icon_select_layer.png")]
        [CommandMethod("SL", CommandFlags.UsePickSet)]
        public void SelectLayersCommand()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                ObjectId[] impliedIds = GetImpliedSelection(editor);
                List<string> layers;

                if (impliedIds.Length > 0)
                {
                    layers = GetDistinctLayers(document.Database, impliedIds);
                    if (layers.Count == 0)
                    {
                        editor.WriteMessage("\n[NEXO] Nenhuma layer encontrada na seleção.");
                        return;
                    }
                }
                else
                {
                    List<string> drawingLayers = GetDrawingLayers(document.Database);
                    if (drawingLayers.Count == 0)
                    {
                        editor.WriteMessage("\n[NEXO] O desenho não possui layers disponíveis.");
                        return;
                    }

                    string chosenLayer = LayerPickerDialog.ChooseLayer(drawingLayers);
                    if (chosenLayer == null)
                        return;

                    layers = new List<string> { chosenLayer };
                }

                ObjectId[] selectedIds = SelectEntitiesOnLayers(document, layers);
                if (selectedIds.Length == 0)
                {
                    editor.WriteMessage("\n[NEXO] Nenhum objeto encontrado no espaço atual na(s) layer(s): " + string.Join(",", layers));
                    return;
                }

                ScheduleSelectionAfterCommand(document, selectedIds, layers);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n[NEXO] Falha no comando SL: " + exception.Message);
            }
        }

        private static ObjectId[] GetImpliedSelection(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status != PromptStatus.OK || implied.Value == null)
                return Array.Empty<ObjectId>();

            return implied.Value.GetObjectIds();
        }

        private static List<string> GetDrawingLayers(Database database)
        {
            List<string> layers = new List<string>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                LayerTable table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    LayerTableRecord layer = transaction.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                    if (layer != null && !string.IsNullOrWhiteSpace(layer.Name))
                        layers.Add(layer.Name);
                }
                transaction.Commit();
            }

            layers.Sort(StringComparer.OrdinalIgnoreCase);
            return layers;
        }

        private static List<string> GetDistinctLayers(Database database, ObjectId[] ids)
        {
            List<string> layers = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    if (id.IsNull || !id.IsValid || id.IsErased)
                        continue;

                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity != null && !string.IsNullOrWhiteSpace(entity.Layer) && seen.Add(entity.Layer))
                        layers.Add(entity.Layer);
                }

                transaction.Commit();
            }

            return layers;
        }

        private static int CountConfirmedSelection(Editor editor, ObjectId[] requestedIds)
        {
            PromptSelectionResult result = editor.SelectImplied();
            if (result.Status != PromptStatus.OK || result.Value == null)
                return 0;

            HashSet<ObjectId> requested = new HashSet<ObjectId>(requestedIds);
            HashSet<ObjectId> confirmed = new HashSet<ObjectId>();
            foreach (ObjectId id in result.Value.GetObjectIds())
            {
                if (requested.Contains(id))
                    confirmed.Add(id);
            }

            return confirmed.Count;
        }

        private static void ScheduleSelectionAfterCommand(
            Document document,
            ObjectId[] selectedIds,
            IList<string> layers)
        {
            Application.Idle -= ApplyPendingSelection;
            _pendingSelection = new PendingSelection(document, selectedIds, layers);
            Application.Idle += ApplyPendingSelection;
        }

        private static void ApplyPendingSelection(object sender, EventArgs eventArgs)
        {
            Application.Idle -= ApplyPendingSelection;
            PendingSelection pending = _pendingSelection;
            _pendingSelection = null;
            if (pending == null)
                return;

            Document activeDocument = Application.DocumentManager.MdiActiveDocument;
            if (!ReferenceEquals(activeDocument, pending.Document))
                return;

            Editor editor = pending.Document.Editor;
            try
            {
                editor.SetImpliedSelection(pending.ObjectIds);
                int confirmedCount = CountConfirmedSelection(editor, pending.ObjectIds);
                if (confirmedCount == 0)
                {
                    editor.WriteMessage(
                        "\n[NEXO] O AutoCAD não confirmou a seleção de objetos na(s) layer(s): " +
                        string.Join(",", pending.Layers) + ". Nenhum objeto foi informado como selecionado.");
                    return;
                }

                string countMessage = confirmedCount == pending.ObjectIds.Length
                    ? "\n[NEXO] Selecionados " + confirmedCount + " objeto(s)"
                    : "\n[NEXO] Selecionados " + confirmedCount + " de " + pending.ObjectIds.Length + " objeto(s) encontrados";
                editor.WriteMessage(countMessage + " na(s) layer(s): " + string.Join(",", pending.Layers));
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n[NEXO] Falha ao aplicar a seleção: " + exception.Message);
            }
        }

        private sealed class PendingSelection
        {
            internal PendingSelection(Document document, ObjectId[] objectIds, IList<string> layers)
            {
                Document = document;
                ObjectIds = (ObjectId[])objectIds.Clone();
                Layers = new List<string>(layers).ToArray();
            }

            internal Document Document { get; }
            internal ObjectId[] ObjectIds { get; }
            internal string[] Layers { get; }
        }

        private static ObjectId[] SelectEntitiesOnLayers(Document document, IList<string> layers)
        {
            Database database = document.Database;
            Editor editor = document.Editor;
            HashSet<string> targetLayers = new HashSet<string>(layers, StringComparer.OrdinalIgnoreCase);
            HashSet<ObjectId> selected = new HashSet<ObjectId>();
            ObjectId currentSpaceId = database.CurrentSpaceId;

            SelectionFilter layerFilter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.LayerName, string.Join(",", layers))
            });
            ObjectId[] layerEntityIds = GetAllSelectionIds(editor, layerFilter);

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Dictionary<ObjectId, bool> definitionMatches = new Dictionary<ObjectId, bool>();
                HashSet<ObjectId> definitionsBeingChecked = new HashSet<ObjectId>();

                foreach (ObjectId entityId in layerEntityIds)
                {
                    if (entityId.IsNull || !entityId.IsValid || entityId.IsErased)
                        continue;

                    Entity entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                    if (entity != null && entity.OwnerId == currentSpaceId)
                        selected.Add(entityId);
                }

                BlockTableRecord currentSpace = transaction.GetObject(currentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (currentSpace != null)
                {
                    foreach (ObjectId entityId in currentSpace)
                    {
                        if (entityId.IsNull || !entityId.IsValid || entityId.IsErased ||
                            entityId.ObjectClass == null || entityId.ObjectClass.DxfName != "INSERT")
                            continue;

                        BlockReference reference = transaction.GetObject(entityId, OpenMode.ForRead, false) as BlockReference;
                        if (reference != null && BlockReferenceContainsLayer(
                            transaction, reference, targetLayers, definitionMatches, definitionsBeingChecked))
                        {
                            selected.Add(entityId);
                        }
                    }
                }

                transaction.Commit();
            }

            ObjectId[] resultIds = new ObjectId[selected.Count];
            selected.CopyTo(resultIds);
            return resultIds;
        }
        private static ObjectId[] GetAllSelectionIds(Editor editor, SelectionFilter filter)
        {
            PromptSelectionResult result = editor.SelectAll(filter);
            return result.Status == PromptStatus.OK && result.Value != null
                ? result.Value.GetObjectIds()
                : Array.Empty<ObjectId>();
        }

        private static bool BlockReferenceContainsLayer(
            Transaction transaction,
            BlockReference reference,
            HashSet<string> targetLayers,
            Dictionary<ObjectId, bool> definitionMatches,
            HashSet<ObjectId> definitionsBeingChecked)
        {
            if (targetLayers.Contains(reference.Layer))
                return true;

            if (BlockDefinitionContainsLayer(
                transaction, reference.BlockTableRecord, targetLayers, definitionMatches, definitionsBeingChecked))
            {
                return true;
            }

            return AttributesContainLayer(transaction, reference, targetLayers);
        }

        private static bool AttributesContainLayer(Transaction transaction, BlockReference reference, HashSet<string> targetLayers)
        {
            foreach (ObjectId attributeId in reference.AttributeCollection)
            {
                AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                if (attribute != null && targetLayers.Contains(attribute.Layer))
                    return true;
            }

            return false;
        }

        private static bool BlockDefinitionContainsLayer(
            Transaction transaction,
            ObjectId definitionId,
            HashSet<string> targetLayers,
            Dictionary<ObjectId, bool> definitionMatches,
            HashSet<ObjectId> definitionsBeingChecked)
        {
            if (definitionId.IsNull || !definitionId.IsValid || definitionId.IsErased)
                return false;
            if (definitionMatches.TryGetValue(definitionId, out bool cached))
                return cached;
            if (!definitionsBeingChecked.Add(definitionId))
                return false;

            bool containsLayer = false;
            BlockTableRecord definition = transaction.GetObject(definitionId, OpenMode.ForRead, false) as BlockTableRecord;
            if (definition != null)
            {
                foreach (ObjectId entityId in definition)
                {
                    Entity entity = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                    if (entity == null)
                        continue;
                    if (targetLayers.Contains(entity.Layer))
                    {
                        containsLayer = true;
                        break;
                    }

                    BlockReference nestedReference = entity as BlockReference;
                    if (nestedReference != null && BlockReferenceContainsLayer(
                        transaction, nestedReference, targetLayers, definitionMatches, definitionsBeingChecked))
                    {
                        containsLayer = true;
                        break;
                    }
                }
            }

            definitionsBeingChecked.Remove(definitionId);
            definitionMatches[definitionId] = containsLayer;
            return containsLayer;
        }
    }
}
