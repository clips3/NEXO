using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(Nexo.AutoCAD.LayerDeletePlusCommand))]

namespace Nexo.AutoCAD
{
    public sealed class LayerDeletePlusCommand
    {
        [NexoRibbonCommand(
            "LDL",
            "Camadas",
            "Deletar Layer +",
            "Remove uma layer vazia, exclui seus objetos ou move os objetos para outra layer antes de removê-la.",
            "icon_delete_layer.png")]
        [CommandMethod("LDL")]
        public void DeleteLayerPlus()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                LayerSnapshot snapshot = ReadLayerSnapshot(document.Database);
                if (snapshot.Layers.Count == 0)
                {
                    editor.WriteMessage("\n[NEXO] Não há layers disponíveis para remover.");
                    return;
                }

                LayerDeleteRequest request = LayerDeleteDialog.Choose(
                    snapshot.Layers,
                    snapshot.UsageCounts,
                    snapshot.CurrentLayer);
                if (request == null)
                    return;

                int affectedObjects = ApplyLayerDelete(document.Database, request);
                editor.Regen();
                editor.WriteMessage("\n[NEXO] Layer " + request.LayerName + " removida. Objetos afetados: " + affectedObjects + ".");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n[NEXO] Falha em LDL: " + exception.Message);
            }
        }

        private static LayerSnapshot ReadLayerSnapshot(Database database)
        {
            LayerSnapshot snapshot = new LayerSnapshot();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                LayerTable layerTable = transaction.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (layerTable != null)
                {
                    foreach (ObjectId layerId in layerTable)
                    {
                        LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
                        if (layer == null || layer.IsDependent || string.IsNullOrWhiteSpace(layer.Name))
                            continue;

                        snapshot.Layers.Add(layer.Name);
                        snapshot.UsageCounts[layer.Name] = 0;
                        if (layerId == database.Clayer)
                            snapshot.CurrentLayer = layer.Name;
                    }
                }

                BlockTable blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (blockTable != null)
                {
                    foreach (ObjectId blockId in blockTable)
                    {
                        BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                        if (block == null || block.IsFromExternalReference)
                            continue;

                        foreach (ObjectId entityId in block)
                        {
                            Entity entity = TryGetEntity(transaction, entityId);
                            if (entity == null)
                                continue;

                            AddLayerUse(snapshot.UsageCounts, entity.Layer);
                            BlockReference reference = entity as BlockReference;
                            if (reference != null)
                            {
                                foreach (ObjectId attributeId in reference.AttributeCollection)
                                {
                                    AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                                    if (attribute != null)
                                        AddLayerUse(snapshot.UsageCounts, attribute.Layer);
                                }
                            }
                        }
                    }
                }

                transaction.Commit();
            }

            snapshot.Layers.Sort(StringComparer.OrdinalIgnoreCase);
            return snapshot;
        }

        private static Entity TryGetEntity(Transaction transaction, ObjectId id)
        {
            try
            {
                return transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
            }
            catch
            {
                return null;
            }
        }

        private static void AddLayerUse(IDictionary<string, int> usageCounts, string layerName)
        {
            if (usageCounts.TryGetValue(layerName, out int count))
                usageCounts[layerName] = count + 1;
        }

        private static int ApplyLayerDelete(Database database, LayerDeleteRequest request)
        {
            int affectedObjects = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                LayerTable layerTable = transaction.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
                if (layerTable == null || !layerTable.Has(request.LayerName))
                    throw new InvalidOperationException("A layer selecionada não existe mais no desenho.");

                LayerTableRecord sourceLayer = transaction.GetObject(layerTable[request.LayerName], OpenMode.ForWrite) as LayerTableRecord;
                if (sourceLayer == null || sourceLayer.IsDependent)
                    throw new InvalidOperationException("A layer selecionada é dependente de uma referência externa e não pode ser removida.");
                if (sourceLayer.ObjectId == database.Clayer)
                    throw new InvalidOperationException("Não é possível remover a layer atual.");

                LayerTableRecord targetLayer = null;
                if (request.Action == LayerDeleteAction.MoveObjects)
                {
                    if (string.IsNullOrWhiteSpace(request.TargetLayerName) ||
                        string.Equals(request.LayerName, request.TargetLayerName, StringComparison.OrdinalIgnoreCase) ||
                        !layerTable.Has(request.TargetLayerName))
                    {
                        throw new InvalidOperationException("A layer de destino não é válida.");
                    }

                    targetLayer = transaction.GetObject(layerTable[request.TargetLayerName], OpenMode.ForRead) as LayerTableRecord;
                    if (targetLayer == null || targetLayer.IsDependent)
                        throw new InvalidOperationException("A layer de destino não está disponível.");
                }

                sourceLayer.IsLocked = false;
                BlockTable blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (blockTable != null)
                {
                    foreach (ObjectId blockId in blockTable)
                    {
                        BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                        if (block == null || block.IsFromExternalReference)
                            continue;

                        List<ObjectId> entityIds = new List<ObjectId>();
                        foreach (ObjectId entityId in block)
                            entityIds.Add(entityId);

                        foreach (ObjectId entityId in entityIds)
                        {
                            Entity entity = TryGetEntity(transaction, entityId);
                            if (entity == null || entity.IsErased)
                                continue;

                            BlockReference reference = entity as BlockReference;
                            if (reference != null)
                            {
                                foreach (ObjectId attributeId in reference.AttributeCollection)
                                {
                                    AttributeReference attribute = transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                                    if (attribute != null && !attribute.IsErased &&
                                        string.Equals(attribute.Layer, request.LayerName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        ProcessEntity(transaction, attributeId, request, targetLayer);
                                        affectedObjects++;
                                    }
                                }
                            }

                            if (string.Equals(entity.Layer, request.LayerName, StringComparison.OrdinalIgnoreCase))
                            {
                                ProcessEntity(transaction, entityId, request, targetLayer);
                                affectedObjects++;
                            }
                        }
                    }
                }

                sourceLayer.Erase(true);
                transaction.Commit();
            }

            return affectedObjects;
        }

        private static void ProcessEntity(
            Transaction transaction,
            ObjectId entityId,
            LayerDeleteRequest request,
            LayerTableRecord targetLayer)
        {
            Entity entity = transaction.GetObject(entityId, OpenMode.ForWrite, false) as Entity;
            if (entity == null)
                return;

            if (request.Action == LayerDeleteAction.DeleteObjects)
                entity.Erase(true);
            else
                entity.Layer = targetLayer.Name;
        }

        private sealed class LayerSnapshot
        {
            internal List<string> Layers { get; } = new List<string>();
            internal Dictionary<string, int> UsageCounts { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            internal string CurrentLayer { get; set; }
        }
    }
}
