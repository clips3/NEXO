using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(Nexo.AutoCAD.BurstPlusCommand))]

namespace Nexo.AutoCAD
{
    public sealed class BurstPlusCommand
    {
        [NexoRibbonCommand(
            "NBURST",
            "Blocos",
            "Burst +",
            "Explode blocos selecionados, converte atributos em texto e percorre blocos aninhados.",
            "icon_burst_plus.png")]
        [CommandMethod("NBURST", CommandFlags.UsePickSet)]
        public void BurstPlus()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                ObjectId[] blockIds = GetBlockSelection(editor);
                if (blockIds.Length == 0)
                {
                    editor.WriteMessage("\n[NEXO] Nenhum bloco selecionado para Burst +.");
                    return;
                }

                Database database = document.Database;
                ObjectId currentSpaceId = database.CurrentSpaceId;
                BurstContext context;
                int selectedCount = 0;

                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = transaction.GetObject(currentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                    if (currentSpace == null)
                        throw new InvalidOperationException("Não foi possível acessar o espaço atual do desenho.");

                    context = new BurstContext(database, transaction, currentSpace);
                    foreach (ObjectId blockId in blockIds)
                    {
                        if (blockId.IsNull || !blockId.IsValid || blockId.IsErased)
                            continue;

                        BlockReference reference = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockReference;
                        if (reference == null || reference.OwnerId != currentSpaceId || context.IsLayerLocked(reference.Layer))
                            continue;

                        reference = transaction.GetObject(blockId, OpenMode.ForWrite, false) as BlockReference;
                        if (reference != null && context.BurstReference(reference, true))
                            selectedCount++;
                    }

                    transaction.Commit();
                }

                if (selectedCount == 0)
                {
                    editor.WriteMessage("\n[NEXO] Nenhum bloco selecionado pôde ser explodido. Xrefs, layers bloqueadas e blocos não explodíveis foram ignorados.");
                    return;
                }

                editor.WriteMessage(
                    "\n[NEXO] Burst + concluiu " + context.ProcessedReferences + " referência(s) de bloco, incluindo os blocos aninhados, a partir de " +
                    selectedCount + " bloco(s) selecionado(s).");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n[NEXO] Falha em NBURST: " + exception.Message);
            }
        }

        internal static void BurstCopiesForDetail(
            Database database,
            ObjectId[] copiedBlockIds,
            out int processedReferenceCount,
            out int preservedDrawOrderReferenceCount,
            out Dictionary<ObjectId, ObjectId[]> expandedEntitiesByReference)
        {
            processedReferenceCount = 0;
            preservedDrawOrderReferenceCount = 0;
            expandedEntitiesByReference = new Dictionary<ObjectId, ObjectId[]>();
            if (database == null || copiedBlockIds == null || copiedBlockIds.Length == 0)
                return;

            ObjectId currentSpaceId = database.CurrentSpaceId;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    currentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("Não foi possível acessar o espaço atual do desenho.");

                BurstContext context = new BurstContext(database, transaction, currentSpace, true);
                foreach (ObjectId blockId in copiedBlockIds)
                {
                    if (blockId.IsNull || !blockId.IsValid || blockId.IsErased)
                        continue;

                    BlockReference reference = transaction.GetObject(
                        blockId, OpenMode.ForRead, false) as BlockReference;
                    if (reference == null || reference.OwnerId != currentSpaceId ||
                        context.IsLayerLocked(reference.Layer))
                    {
                        continue;
                    }

                    reference = transaction.GetObject(blockId, OpenMode.ForWrite, false) as BlockReference;
                    if (reference == null)
                        continue;

                    int firstCreatedIndex = context.CreatedEntityCount;
                    if (context.BurstReference(reference, true))
                    {
                        expandedEntitiesByReference[blockId] =
                            context.GetCreatedEntityIdsFrom(firstCreatedIndex);
                    }
                }

                processedReferenceCount = context.ProcessedReferences;
                preservedDrawOrderReferenceCount = context.PreservedDrawOrderReferences;
                transaction.Commit();
            }
        }

        private static ObjectId[] GetBlockSelection(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null)
            {
                List<ObjectId> impliedBlocks = new List<ObjectId>();
                foreach (ObjectId id in implied.Value.GetObjectIds())
                {
                    if (!id.IsNull && id.IsValid && !id.IsErased && id.ObjectClass != null &&
                        string.Equals(id.ObjectClass.DxfName, "INSERT", StringComparison.OrdinalIgnoreCase))
                    {
                        impliedBlocks.Add(id);
                    }
                }

                if (impliedBlocks.Count > 0)
                    return impliedBlocks.ToArray();
            }

            PromptSelectionOptions options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelecione os blocos para Burst +: ",
                AllowDuplicates = false
            };
            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "INSERT")
            });
            PromptSelectionResult selection = editor.GetSelection(options, filter);
            return selection.Status == PromptStatus.OK && selection.Value != null
                ? selection.Value.GetObjectIds()
                : Array.Empty<ObjectId>();
        }

        private sealed class BurstContext
        {
            private readonly Database _database;
            private readonly Transaction _transaction;
            private readonly BlockTableRecord _ownerSpace;
            private readonly Dictionary<string, bool> _lockedLayers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<ObjectId> _activeDefinitions = new HashSet<ObjectId>();
            private readonly List<ObjectId> _createdEntityIds = new List<ObjectId>();
            private readonly Dictionary<ObjectId, bool> _protectedReferenceContentCache =
                new Dictionary<ObjectId, bool>();
            private readonly Dictionary<ObjectId, bool> _customDrawOrderCache =
                new Dictionary<ObjectId, bool>();
            private readonly bool _preserveAttributedReferences;

            internal BurstContext(
                Database database,
                Transaction transaction,
                BlockTableRecord ownerSpace,
                bool preserveAttributedReferences = false)
            {
                _database = database;
                _transaction = transaction;
                _ownerSpace = ownerSpace;
                _preserveAttributedReferences = preserveAttributedReferences;
            }

            internal int ProcessedReferences { get; private set; }
            internal int PreservedDrawOrderReferences { get; private set; }
            internal int CreatedEntityCount { get { return _createdEntityIds.Count; } }

            internal ObjectId[] GetCreatedEntityIdsFrom(int startIndex)
            {
                if (startIndex < 0 || startIndex >= _createdEntityIds.Count)
                    return Array.Empty<ObjectId>();
                return _createdEntityIds.GetRange(
                    startIndex, _createdEntityIds.Count - startIndex).ToArray();
            }

            internal bool IsLayerLocked(string layerName)
            {
                if (string.IsNullOrWhiteSpace(layerName))
                    return false;
                if (_lockedLayers.TryGetValue(layerName, out bool isLocked))
                    return isLocked;

                LayerTable table = (LayerTable)_transaction.GetObject(_database.LayerTableId, OpenMode.ForRead);
                if (table.Has(layerName))
                {
                    LayerTableRecord layer = _transaction.GetObject(table[layerName], OpenMode.ForRead, false) as LayerTableRecord;
                    isLocked = layer != null && layer.IsLocked;
                }
                else
                {
                    isLocked = false;
                }

                _lockedLayers[layerName] = isLocked;
                return isLocked;
            }

            private bool ContainsProtectedReferenceContent(
                BlockTableRecord definition,
                HashSet<ObjectId> visitedDefinitions)
            {
                if (definition == null)
                    return false;

                ObjectId definitionId = definition.ObjectId;
                if (_protectedReferenceContentCache.TryGetValue(definitionId, out bool cachedResult))
                    return cachedResult;
                if (!visitedDefinitions.Add(definitionId))
                    return false;

                foreach (ObjectId entityId in definition)
                {
                    Entity entity = _transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                    if (entity == null)
                        continue;

                    if (entity is AttributeDefinition)
                    {
                        _protectedReferenceContentCache[definitionId] = true;
                        return true;
                    }

                    BlockReference nestedReference = entity as BlockReference;
                    if (nestedReference == null)
                        continue;

                    if (nestedReference.IsDynamicBlock || HasAttachedAttributes(nestedReference) ||
                        HasInstanceMetadata(nestedReference))
                    {
                        _protectedReferenceContentCache[definitionId] = true;
                        return true;
                    }

                    BlockTableRecord nestedDefinition = _transaction.GetObject(
                        nestedReference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                    if (ContainsProtectedReferenceContent(nestedDefinition, visitedDefinitions))
                    {
                        _protectedReferenceContentCache[definitionId] = true;
                        return true;
                    }
                }

                _protectedReferenceContentCache[definitionId] = false;
                return false;
            }

            private bool ContainsCustomDrawOrder(
                BlockTableRecord definition,
                HashSet<ObjectId> visitedDefinitions)
            {
                if (definition == null)
                    return false;

                ObjectId definitionId = definition.ObjectId;
                if (_customDrawOrderCache.TryGetValue(definitionId, out bool cachedResult))
                    return cachedResult;
                if (!visitedDefinitions.Add(definitionId))
                    return false;

                bool hasCustomOrder = false;
                try
                {
                    ObjectId drawOrderTableId = definition.DrawOrderTableId;
                    if (!drawOrderTableId.IsNull)
                    {
                        DrawOrderTable drawOrderTable = _transaction.GetObject(
                            drawOrderTableId, OpenMode.ForRead, false) as DrawOrderTable;
                        if (drawOrderTable == null)
                        {
                            // If sort-order data cannot be inspected, preserve the block as-is.
                            hasCustomOrder = true;
                        }
                        else
                        {
                            bool hasPreviousSortHandle = false;
                            long previousSortHandle = long.MinValue;
                            foreach (ObjectId entityId in definition)
                            {
                                if (entityId.IsNull || !entityId.IsValid || entityId.IsErased)
                                    continue;

                                long sortHandle = drawOrderTable.GetSortHandle(entityId).Value;
                                if (hasPreviousSortHandle && sortHandle <= previousSortHandle)
                                {
                                    // Explode appends entities in definition order. If that differs
                                    // from the BTR's draw order, keep the reference intact so XCLIP
                                    // retains AutoCAD's original stacking order.
                                    hasCustomOrder = true;
                                    break;
                                }

                                previousSortHandle = sortHandle;
                                hasPreviousSortHandle = true;
                            }
                        }
                    }

                    if (!hasCustomOrder)
                    {
                        foreach (ObjectId entityId in definition)
                        {
                            Entity entity = _transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                            BlockReference nestedReference = entity as BlockReference;
                            if (nestedReference == null)
                                continue;

                            BlockTableRecord nestedDefinition = _transaction.GetObject(
                                nestedReference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                            if (ContainsCustomDrawOrder(nestedDefinition, visitedDefinitions))
                            {
                                hasCustomOrder = true;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    // Conservative fallback: do not explode a block if its sort order is unclear.
                    hasCustomOrder = true;
                }

                _customDrawOrderCache[definitionId] = hasCustomOrder;
                return hasCustomOrder;
            }

            private bool HasAttachedAttributes(BlockReference reference)
            {
                if (reference == null)
                    return false;

                try
                {
                    foreach (ObjectId attributeId in reference.AttributeCollection)
                    {
                        if (!attributeId.IsNull && attributeId.IsValid && !attributeId.IsErased)
                            return true;
                    }
                }
                catch
                {
                    // If attributes cannot be inspected, preserve the reference conservatively.
                    return true;
                }

                return false;
            }

            private static bool HasInstanceMetadata(BlockReference reference)
            {
                if (reference == null)
                    return false;

                try
                {
                    if (!reference.ExtensionDictionary.IsNull)
                        return true;

                    using (ResultBuffer xdata = reference.XData)
                        return xdata != null;
                }
                catch
                {
                    // If metadata cannot be inspected, preserve the reference conservatively.
                    return true;
                }
            }

            internal bool BurstReference(BlockReference reference, bool databaseResident)
            {
                if (reference == null || reference.IsErased)
                    return false;

                ObjectId definitionId = reference.BlockTableRecord;
                if (definitionId.IsNull || !definitionId.IsValid || definitionId.IsErased || _activeDefinitions.Contains(definitionId))
                    return false;

                BlockTableRecord definition = _transaction.GetObject(definitionId, OpenMode.ForRead, false) as BlockTableRecord;
                if (definition == null || definition.IsFromExternalReference || definition.IsFromOverlayReference ||
                    !definition.Explodable || IsLayerLocked(reference.Layer))
                {
                    return false;
                }

                if (_preserveAttributedReferences)
                {
                    bool hasCustomDrawOrder = ContainsCustomDrawOrder(
                        definition, new HashSet<ObjectId>());
                    if (hasCustomDrawOrder)
                        PreservedDrawOrderReferences++;

                    bool preserveReference = reference.IsDynamicBlock ||
                                             HasAttachedAttributes(reference) ||
                                             HasInstanceMetadata(reference) ||
                                             ContainsProtectedReferenceContent(
                                                 definition, new HashSet<ObjectId>()) ||
                                             hasCustomDrawOrder;
                    if (preserveReference)
                    {
                        // Keep tagged, dynamic, custom, or explicitly sorted references intact in
                        // lightweight details. XCLIP crops them without changing their contents/order.
                        return false;
                    }
                }

                _activeDefinitions.Add(definitionId);
                try
                {
                    string blockLayer = reference.Layer;
                    string blockLinetype = reference.Linetype;
                    using (Color blockColor = (Color)reference.Color.Clone())
                    using (DBObjectCollection explodedObjects = new DBObjectCollection())
                    {
                        try
                        {
                            reference.Explode(explodedObjects);
                        }
                        catch
                        {
                            DisposeTransientObjects(explodedObjects);
                            return false;
                        }

                        if (explodedObjects.Count == 0)
                            return false;

                        // Attribute references belong to database-resident INSERTs. References
                        // returned by Explode are transient and do not own an AttributeCollection.
                        if (databaseResident)
                            BurstAttributes(reference, blockLayer, blockColor, blockLinetype);

                        foreach (DBObject dbObject in explodedObjects)
                        {
                            Entity entity = dbObject as Entity;
                            if (entity == null)
                            {
                                DisposeTransient(dbObject);
                                continue;
                            }

                            if (IsLayerLocked(entity.Layer))
                            {
                                AppendEntity(entity);
                                continue;
                            }

                            if (!entity.Visible)
                            {
                                DisposeTransient(entity);
                                continue;
                            }

                            ApplyBlockProperties(entity, blockLayer, blockColor, blockLinetype);

                            AttributeDefinition attributeDefinition = entity as AttributeDefinition;
                            if (attributeDefinition != null)
                            {
                                if (attributeDefinition.Constant && !attributeDefinition.Invisible)
                                {
                                    AppendAttributeText(CreateTextFromAttribute(attributeDefinition));
                                }
                                DisposeTransient(attributeDefinition);
                                continue;
                            }

                            BlockReference nestedReference = entity as BlockReference;
                            if (nestedReference != null && BurstReference(nestedReference, false))
                                continue;

                            AppendEntity(entity);
                        }

                        if (databaseResident)
                            reference.Erase();
                        else
                            reference.Dispose();

                        ProcessedReferences++;
                        return true;
                    }
                }
                finally
                {
                    _activeDefinitions.Remove(definitionId);
                }
            }

            private void BurstAttributes(BlockReference reference, string blockLayer, Color blockColor, string blockLinetype)
            {
                foreach (ObjectId attributeId in reference.AttributeCollection)
                {
                    if (attributeId.IsNull || !attributeId.IsValid || attributeId.IsErased)
                        continue;

                    AttributeReference attribute = _transaction.GetObject(attributeId, OpenMode.ForRead, false) as AttributeReference;
                    if (attribute == null)
                        continue;

                    bool layerLocked = IsLayerLocked(attribute.Layer);
                    if (!layerLocked)
                    {
                        attribute = _transaction.GetObject(attributeId, OpenMode.ForWrite, false) as AttributeReference;
                        if (attribute == null)
                            continue;
                        ApplyBlockProperties(attribute, blockLayer, blockColor, blockLinetype);
                    }

                    if (!attribute.Invisible && attribute.Visible)
                        AppendAttributeText(CreateTextFromAttribute(attribute));
                }
            }

            private Entity CreateTextFromAttribute(Entity source)
            {
                AttributeReference attributeReference = source as AttributeReference;
                if (attributeReference != null && attributeReference.IsMTextAttribute)
                {
                    MText mtext = new MText();
                    mtext.CopyFrom(attributeReference.MTextAttribute);
                    mtext.SetPropertiesFrom(source);
                    return mtext;
                }

                AttributeDefinition attributeDefinition = source as AttributeDefinition;
                if (attributeDefinition != null && attributeDefinition.IsMTextAttributeDefinition)
                {
                    MText mtext = new MText();
                    mtext.CopyFrom(attributeDefinition.MTextAttributeDefinition);
                    mtext.SetPropertiesFrom(source);
                    return mtext;
                }

                DBText sourceText = source as DBText;
                if (sourceText == null)
                    throw new InvalidOperationException("O atributo selecionado não pôde ser convertido em texto.");

                DBText text = new DBText();
                text.SetDatabaseDefaults(_database);
                text.SetPropertiesFrom(source);
                text.TextStyleId = sourceText.TextStyleId;
                text.TextString = sourceText.TextString;
                text.Height = sourceText.Height;
                text.WidthFactor = sourceText.WidthFactor;
                text.Oblique = sourceText.Oblique;
                text.Rotation = sourceText.Rotation;
                text.Normal = sourceText.Normal;
                text.Thickness = sourceText.Thickness;
                text.IsMirroredInX = sourceText.IsMirroredInX;
                text.IsMirroredInY = sourceText.IsMirroredInY;

                // Set justification before its points. AlignmentPoint is not applicable
                // to the default left/baseline mode and AutoCAD returns eNotApplicable.
                text.HorizontalMode = sourceText.HorizontalMode;
                text.VerticalMode = sourceText.VerticalMode;
                if (sourceText.HorizontalMode == TextHorizontalMode.TextLeft &&
                    sourceText.VerticalMode == TextVerticalMode.TextBase)
                {
                    text.Position = sourceText.Position;
                }
                else if (sourceText.HorizontalMode == TextHorizontalMode.TextAlign ||
                         sourceText.HorizontalMode == TextHorizontalMode.TextFit)
                {
                    // Aligned/Fit text uses both endpoints.
                    text.Position = sourceText.Position;
                    text.AlignmentPoint = sourceText.AlignmentPoint;
                }
                else
                {
                    text.AlignmentPoint = sourceText.AlignmentPoint;
                }
                return text;
            }

            private void AppendAttributeText(Entity text)
            {
                AppendEntity(text);

                // AdjustAlignment needs the text's database context. Run it after the new
                // DBText is appended; an inapplicable adjustment should not abort the burst,
                // because the source attribute already supplies its insertion/alignment data.
                DBText dbText = text as DBText;
                if (dbText == null)
                    return;

                try
                {
                    dbText.AdjustAlignment(_database);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception exception)
                {
                    if (exception.ErrorStatus != ErrorStatus.NotApplicable)
                        throw;
                }
            }

            private static void ApplyBlockProperties(Entity entity, string blockLayer, Color blockColor, string blockLinetype)
            {
                if (string.Equals(entity.Layer, "0", StringComparison.OrdinalIgnoreCase))
                    entity.Layer = blockLayer;
                if (entity.Color.IsByBlock)
                    entity.Color = blockColor;
                if (string.Equals(entity.Linetype, "ByBlock", StringComparison.OrdinalIgnoreCase))
                    entity.Linetype = blockLinetype;
            }

            private void AppendEntity(Entity entity)
            {
                _ownerSpace.AppendEntity(entity);
                _transaction.AddNewlyCreatedDBObject(entity, true);
                _createdEntityIds.Add(entity.ObjectId);
            }

            private static void DisposeTransientObjects(DBObjectCollection objects)
            {
                foreach (DBObject dbObject in objects)
                    DisposeTransient(dbObject);
            }

            private static void DisposeTransient(DBObject dbObject)
            {
                if (dbObject == null)
                    return;

                try
                {
                    if (dbObject.ObjectId.IsNull)
                        dbObject.Dispose();
                }
                catch
                {
                    // Disposal is best-effort for transient objects returned by Explode.
                }
            }
        }
    }
}
