using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Nexo.AutoCAD
{
    public sealed partial class DetailCommand
    {
        [NexoRibbonCommand(
            "DETLEVE",
            "Detalhe",
            "Detalhe Leve",
            "Cria um detalhe aparado: expande blocos explodíveis, remove conteúdo fora da janela e aplica XCLIP no resultado.",
            "icon_detail_light.png")]
        [CommandMethod("DETLEVE")]
        public void CreateLightDetail()
        {
            RunLightDetailWorkflow(false);
        }

        [NexoRibbonCommand(
            "DETLEVEISO",
            "Detalhe",
            "Detalhe Leve + Isométrico",
            "Cria o detalhe leve e aplica a transformação isométrica escolhida em uma única operação.",
            "icon_detail_light_isometric.png")]
        [CommandMethod("DETLEVEISO")]
        public void CreateLightDetailIsometric()
        {
            RunLightDetailWorkflow(true);
        }

        [NexoRibbonCommand(
            "DETISOPAREDES",
            "Detalhe",
            "Isométrico + Paredes",
            "Reconhece paredes na arquitetura, permite revisar a planta e cria o detalhe isométrico com laje e paredes.",
            "icon_wall_isometric.png")]
        [CommandMethod("DETISOPAREDES")]
        public void CreateIsometricDetailWithWalls()
        {
            RunLightDetailWorkflow(true, true);
        }

        private void RunLightDetailWorkflow(bool applyIsometric, bool automaticWalls = false)
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            Database database = document.Database;
            object oldOsMode = null;
            object oldCommandEcho = null;
            bool osModeCaptured = false;
            bool commandEchoCaptured = false;
            bool undoGroupStarted = false;

            try
            {
                oldOsMode = Application.GetSystemVariable("OSMODE");
                osModeCaptured = true;
                Application.SetSystemVariable("OSMODE", 0);

                PromptPointResult firstPointResult = editor.GetPoint(
                    new PromptPointOptions("\nClique no primeiro canto do detalhe leve: "));
                if (firstPointResult.Status != PromptStatus.OK)
                    return;

                PromptCornerOptions secondCornerOptions = new PromptCornerOptions(
                    "\nClique no canto oposto do detalhe leve: ", firstPointResult.Value);
                PromptPointResult secondPointResult = editor.GetCorner(secondCornerOptions);
                if (secondPointResult.Status != PromptStatus.OK)
                    return;

                Application.SetSystemVariable("OSMODE", oldOsMode);
                osModeCaptured = false;

                Matrix3d ucs = editor.CurrentUserCoordinateSystem;
                Matrix3d wcsToUcs = ucs.Inverse();
                Point3d firstUcs = firstPointResult.Value.TransformBy(wcsToUcs);
                Point3d secondUcs = secondPointResult.Value.TransformBy(wcsToUcs);
                double minX = Math.Min(firstUcs.X, secondUcs.X);
                double minY = Math.Min(firstUcs.Y, secondUcs.Y);
                double maxX = Math.Max(firstUcs.X, secondUcs.X);
                double maxY = Math.Max(firstUcs.Y, secondUcs.Y);
                if (Math.Abs(maxX - minX) < Tolerance.Global.EqualPoint ||
                    Math.Abs(maxY - minY) < Tolerance.Global.EqualPoint)
                {
                    editor.WriteMessage("\n[NEXO] O retângulo do detalhe precisa ter largura e altura.");
                    return;
                }

                Point3d[] regionCornersUcs = CreateRectangleCorners(minX, minY, maxX, maxY, 0.0, 0.0);
                Point3dCollection regionCornersWcs = new Point3dCollection();
                foreach (Point3d point in regionCornersUcs)
                    regionCornersWcs.Add(point.TransformBy(ucs));

                PromptSelectionResult regionSelection = editor.SelectCrossingPolygon(regionCornersWcs);
                if (regionSelection.Status != PromptStatus.OK || regionSelection.Value == null)
                {
                    editor.WriteMessage("\n[NEXO] Nenhum objeto foi encontrado dentro ou cruzando o retângulo.");
                    return;
                }

                ObjectId[] selectedIds = FilterCurrentSpace(database, regionSelection.Value.GetObjectIds());
                if (selectedIds.Length == 0)
                {
                    editor.WriteMessage("\n[NEXO] Nenhum objeto do espaço atual foi encontrado no retângulo.");
                    return;
                }
                selectedIds = SortByDrawOrder(database, selectedIds);

                WallLayout wallLayout = null;
                if (automaticWalls)
                {
                    WallPreviewDialog wallPreview = WallPreviewDialog.Choose(
                        database, selectedIds, ucs, minX, minY, maxX, maxY);
                    if (wallPreview == null)
                        return;
                    wallLayout = new WallLayout { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
                        Height = wallPreview.Options.Height };
                    foreach (WallFootprintOptimizer.Piece piece in WallFootprintOptimizer.Optimize(wallPreview.Result.Walls))
                        wallLayout.Walls.Add(piece.Wall);
                    foreach (DetectedWall opening in wallPreview.Result.Walls)
                        if (opening.Included && opening.Opening != OpeningKind.None) wallLayout.Openings.Add(opening);
                }

                Point3d firstUcsOnPlane = new Point3d(firstUcs.X, firstUcs.Y, 0.0);
                Point3d firstWcsOnPlane = firstUcsOnPlane.TransformBy(ucs);
                Point3d sourceCenterUcs = new Point3d((minX + maxX) * 0.5, (minY + maxY) * 0.5, 0.0);
                PromptPointOptions destinationOptions = new PromptPointOptions(
                    applyIsometric ? "\nClique onde deseja posicionar o centro do isométrico: " : "\nClique onde deseja posicionar o detalhe leve: ")
                {
                    UseBasePoint = true,
                    BasePoint = applyIsometric ? sourceCenterUcs.TransformBy(ucs) : firstWcsOnPlane
                };
                PromptPointResult destinationResult = editor.GetPoint(destinationOptions);
                if (destinationResult.Status != PromptStatus.OK)
                    return;

                IsometricSelection? isometricOrientation = null;
                if (applyIsometric)
                {
                    isometricOrientation = IsometricDirectionDialog.Choose();
                    if (!isometricOrientation.HasValue)
                        return;
                    if (automaticWalls && isometricOrientation.Value.Face != IsometricFace.Top)
                    {
                        editor.WriteMessage("\n[NEXO] A geração automática de paredes usa a face superior do cubo. Escolha essa face e execute novamente.");
                        return;
                    }
                }

                Point3d destinationUcsRaw = destinationResult.Value.TransformBy(wcsToUcs);
                Point3d destinationUcs = new Point3d(destinationUcsRaw.X, destinationUcsRaw.Y, 0.0);
                string sourcePointString = AutoBlockCommand.FormatPoint(firstUcsOnPlane);
                string destinationPointString = AutoBlockCommand.FormatPoint(destinationUcs);
                string blockName = CreateUniqueLightDetailBlockName(database);

                oldCommandEcho = Application.GetSystemVariable("CMDECHO");
                commandEchoCaptured = true;
                Application.SetSystemVariable("CMDECHO", 0);

                editor.Command("_.UNDO", "_Begin");
                undoGroupStarted = true;

                if (automaticWalls)
                {
                    WallBlockLibrary.ImportMissing(database, null, new[] { "ISO_Laje" });
                    if (wallLayout.Openings.Count > 0)
                        WallBlockLibrary.ImportOpenings(database, wallLayout.Openings);
                }

                HashSet<ObjectId> objectsBeforeCopy = GetCurrentSpaceIds(database);
                SelectionSet sourceSelection = SelectionSet.FromObjectIds(selectedIds);
                editor.Command(
                    "_.COPY",
                    sourceSelection,
                    "",
                    "_non",
                    sourcePointString,
                    "_non",
                    destinationPointString,
                    "");

                ObjectId[] copiedIds = GetNewCurrentSpaceIds(database, objectsBeforeCopy);
                if (copiedIds.Length == 0)
                    throw new InvalidOperationException("O AutoCAD não criou as cópias dos objetos selecionados.");
                copiedIds = SortByDrawOrder(database, copiedIds);

                int expandedReferenceCount = 0;
                int preservedDrawOrderReferenceCount = 0;
                Dictionary<ObjectId, ObjectId[]> expandedEntitiesByReference;
                try
                {
                    BurstPlusCommand.BurstCopiesForDetail(
                        database,
                        copiedIds,
                        out expandedReferenceCount,
                        out preservedDrawOrderReferenceCount,
                        out expandedEntitiesByReference);
                }
                catch
                {
                    // Keep original copied references intact if a special block cannot be expanded.
                    expandedEntitiesByReference = new Dictionary<ObjectId, ObjectId[]>();
                    expandedReferenceCount = 0;
                    preservedDrawOrderReferenceCount = 0;
                }
                List<ObjectId> detailCandidates = new List<ObjectId>();
                HashSet<ObjectId> addedCandidateIds = new HashSet<ObjectId>();
                foreach (ObjectId id in copiedIds)
                {
                    ObjectId[] expandedIdsForReference;
                    if (expandedEntitiesByReference.TryGetValue(id, out expandedIdsForReference))
                    {
                        foreach (ObjectId expandedId in expandedIdsForReference)
                        {
                            if (addedCandidateIds.Add(expandedId))
                                detailCandidates.Add(expandedId);
                        }
                    }
                    else if (addedCandidateIds.Add(id))
                    {
                        detailCandidates.Add(id);
                    }
                }

                LightCropSummary cropSummary = PruneDetailGeometry(
                    database,
                    detailCandidates,
                    ucs,
                    minX + destinationUcs.X - firstUcs.X,
                    minY + destinationUcs.Y - firstUcs.Y,
                    maxX + destinationUcs.X - firstUcs.X,
                    maxY + destinationUcs.Y - firstUcs.Y);

                ObjectId[] finalContentIds = GetLiveCurrentSpaceIds(database, detailCandidates);
                if (finalContentIds.Length == 0)
                    throw new InvalidOperationException("Nenhuma geometria permaneceu dentro da área do detalhe.");

                // Explosion appends children at the end of the current space. Restore the exact
                // intended relative order before BLOCK consumes the entities.
                SetRelativeDrawOrder(database, database.CurrentSpaceId, finalContentIds);

                double offsetX = destinationUcs.X - firstUcs.X;
                double offsetY = destinationUcs.Y - firstUcs.Y;
                DetailArtifacts artifacts = CreateDetailArtifacts(
                    database, ucs, minX, minY, maxX, maxY, offsetX, offsetY);

                SelectionSet finalSelection = SelectionSet.FromObjectIds(finalContentIds);
                editor.Command("_.-BLOCK", blockName, destinationPointString, finalSelection, "");
                SetBlockDefinitionDrawOrder(database, blockName, finalContentIds);
                editor.Command("_.-INSERT", blockName, destinationPointString, 1.0, 1.0, 0.0);

                ObjectId blockReferenceId = FindBlockReference(database, blockName);
                if (blockReferenceId.IsNull)
                    throw new InvalidOperationException("A referência do bloco de detalhe leve não foi encontrada.");

                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockReference blockReference = transaction.GetObject(
                        blockReferenceId, OpenMode.ForWrite, false) as BlockReference;
                    if (blockReference == null)
                        throw new InvalidOperationException("O detalhe leve inserido não é uma referência de bloco válida.");

                    blockReference.Layer = "0";
                    transaction.Commit();
                }

                SelectionSet blockSelection = SelectionSet.FromObjectIds(new[] { blockReferenceId });
                SelectionSet boundarySelection = SelectionSet.FromObjectIds(new[] { artifacts.DestinationFrameId });
                editor.Command("_.XCLIP", blockSelection, "", "_N", "_S", boundarySelection, "");
                ApplyDetailDrawOrder(database, blockReferenceId, artifacts);

                if (isometricOrientation.HasValue)
                {
                    Point3d isometricCenterUcs = new Point3d(
                        (minX + maxX) * 0.5 + offsetX,
                        (minY + maxY) * 0.5 + offsetY,
                        0.0);
                    Point3d isometricCenterWcs = isometricCenterUcs.TransformBy(ucs);
                    IsometricBlockCommands.RunIsometricWorkflow(
                        document,
                        new[] { blockReferenceId, artifacts.DestinationFrameId, artifacts.WipeoutId },
                        isometricOrientation.Value,
                        false,
                        isometricCenterWcs,
                        new Point2d(maxX - minX, maxY - minY),
                        destinationUcs.TransformBy(ucs),
                        wallLayout);
                }

                editor.Command("_.UNDO", "_End");
                undoGroupStarted = false;
                editor.Regen();

                editor.WriteMessage(
                    "\n[NEXO] Detalhe leve criado no bloco " + blockName + ". Blocos expandidos: " +
                    expandedReferenceCount.ToString(CultureInfo.InvariantCulture) +
                    "; objetos removidos fora da área: " +
                    cropSummary.RemovedOutside.ToString(CultureInfo.InvariantCulture) +
                    "; referências com draw order personalizado mantidas intactas: " +
                    preservedDrawOrderReferenceCount.ToString(CultureInfo.InvariantCulture) +
                    "; elementos que cruzam o limite mantidos intactos sob XCLIP: " +
                    cropSummary.KeptForXclip.ToString(CultureInfo.InvariantCulture) + ".");
            }
            catch (System.Exception exception)
            {
                if (undoGroupStarted)
                {
                    try
                    {
                        editor.Command("_.UNDO", "_End");
                        // The grouped workflow is one undo operation. UNDO Back requires a
                        // mark and, without one, AutoCAD offers to undo the entire session.
                        editor.Command("_.UNDO", 1);
                    }
                    catch
                    {
                        // Preserve the original exception if AutoCAD cannot undo the command group.
                    }
                }

                editor.WriteMessage("\n[NEXO] Falha em DETLEVE: " + exception.Message);
            }
            finally
            {
                if (osModeCaptured)
                {
                    try { Application.SetSystemVariable("OSMODE", oldOsMode); }
                    catch { }
                }
                if (commandEchoCaptured)
                {
                    try { Application.SetSystemVariable("CMDECHO", oldCommandEcho); }
                    catch { }
                }
            }
        }

        private static LightCropSummary PruneDetailGeometry(
            Database database,
            IEnumerable<ObjectId> candidateIds,
            Matrix3d ucs,
            double minX,
            double minY,
            double maxX,
            double maxY)
        {
            LightCropSummary summary = new LightCropSummary();
            Matrix3d wcsToUcs = ucs.Inverse();
            double tolerance = Math.Max(Tolerance.Global.EqualPoint, 1e-8);

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("Não foi possível acessar o espaço atual do desenho.");

                foreach (ObjectId id in candidateIds)
                {
                    if (id.IsNull || !id.IsValid || id.IsErased)
                        continue;

                    Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    if (entity == null || entity.OwnerId != currentSpace.ObjectId)
                        continue;

                    DetailBounds bounds;
                    if (!TryGetBoundsInUcs(entity, wcsToUcs, out bounds))
                    {
                        summary.KeptForXclip++;
                        continue;
                    }

                    if (bounds.MaxX < minX - tolerance || bounds.MinX > maxX + tolerance ||
                        bounds.MaxY < minY - tolerance || bounds.MinY > maxY + tolerance)
                    {
                        entity.Erase();
                        summary.RemovedOutside++;
                        continue;
                    }

                    bool entirelyInside = bounds.MinX >= minX - tolerance &&
                                          bounds.MaxX <= maxX + tolerance &&
                                          bounds.MinY >= minY - tolerance &&
                                          bounds.MaxY <= maxY + tolerance;
                    if (!entirelyInside)
                    {
                        // Keep crossing geometry unchanged; the final XCLIP is the exact visual crop.
                        summary.KeptForXclip++;
                    }
                }

                transaction.Commit();
            }

            return summary;
        }

        private static string CreateUniqueLightDetailBlockName(Database database)
        {
            string baseName = "DET_L_" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            string blockName = baseName;
            int suffix = 1;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                while (blockTable != null && blockTable.Has(blockName))
                {
                    blockName = baseName + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                    suffix++;
                }
                transaction.Commit();
            }
            return blockName;
        }

        private static bool TryGetBoundsInUcs(Entity entity, Matrix3d wcsToUcs, out DetailBounds bounds)
        {
            bounds = new DetailBounds();
            try
            {
                Extents3d extents = entity.GeometricExtents;
                Point3d[] corners =
                {
                    new Point3d(extents.MinPoint.X, extents.MinPoint.Y, extents.MinPoint.Z),
                    new Point3d(extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.Z),
                    new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, extents.MinPoint.Z),
                    new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, extents.MaxPoint.Z),
                    new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, extents.MinPoint.Z),
                    new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, extents.MaxPoint.Z),
                    new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, extents.MinPoint.Z),
                    new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, extents.MaxPoint.Z)
                };

                bounds.MinX = double.PositiveInfinity;
                bounds.MinY = double.PositiveInfinity;
                bounds.MaxX = double.NegativeInfinity;
                bounds.MaxY = double.NegativeInfinity;
                foreach (Point3d corner in corners)
                {
                    Point3d point = corner.TransformBy(wcsToUcs);
                    bounds.MinX = Math.Min(bounds.MinX, point.X);
                    bounds.MinY = Math.Min(bounds.MinY, point.Y);
                    bounds.MaxX = Math.Max(bounds.MaxX, point.X);
                    bounds.MaxY = Math.Max(bounds.MaxY, point.Y);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ObjectId[] GetLiveCurrentSpaceIds(Database database, IEnumerable<ObjectId> candidateIds)
        {
            List<ObjectId> liveIds = new List<ObjectId>();
            ObjectId currentSpaceId = database.CurrentSpaceId;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in candidateIds)
                {
                    if (id.IsNull || !id.IsValid || id.IsErased)
                        continue;

                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity != null && entity.OwnerId == currentSpaceId)
                        liveIds.Add(id);
                }
                transaction.Commit();
            }
            return liveIds.ToArray();
        }

        internal static ObjectId[] SortByDrawOrder(Database database, ObjectId[] objectIds)
        {
            if (database == null || objectIds == null || objectIds.Length < 2)
                return objectIds ?? Array.Empty<ObjectId>();

            List<ObjectId> orderedIds = new List<ObjectId>(objectIds);
            Dictionary<ObjectId, int> naturalOrder = new Dictionary<ObjectId, int>();
            Dictionary<ObjectId, int> inputOrder = new Dictionary<ObjectId, int>();
            Dictionary<ObjectId, long> sortKeys = new Dictionary<ObjectId, long>();
            for (int index = 0; index < objectIds.Length; index++)
                inputOrder[objectIds[index]] = index;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (currentSpace == null)
                    return objectIds;

                int ordinal = 0;
                foreach (ObjectId id in currentSpace)
                {
                    naturalOrder[id] = ordinal;
                    ordinal++;
                }

                ObjectId drawOrderTableId = currentSpace.DrawOrderTableId;
                if (!drawOrderTableId.IsNull)
                {
                    try
                    {
                        DrawOrderTable drawOrderTable = transaction.GetObject(
                            drawOrderTableId, OpenMode.ForRead, false) as DrawOrderTable;
                        if (drawOrderTable != null)
                        {
                            foreach (ObjectId id in objectIds)
                                sortKeys[id] = drawOrderTable.GetSortHandle(id).Value;
                        }
                    }
                    catch
                    {
                        // Fall back to the block table record's natural append order.
                        sortKeys.Clear();
                    }
                }

                orderedIds.Sort((left, right) =>
                {
                    int comparison;
                    if (sortKeys.Count == objectIds.Length)
                    {
                        comparison = sortKeys[left].CompareTo(sortKeys[right]);
                        if (comparison != 0)
                            return comparison;
                    }

                    int leftOrdinal;
                    int rightOrdinal;
                    bool hasLeftOrdinal = naturalOrder.TryGetValue(left, out leftOrdinal);
                    bool hasRightOrdinal = naturalOrder.TryGetValue(right, out rightOrdinal);
                    if (hasLeftOrdinal && hasRightOrdinal)
                    {
                        comparison = leftOrdinal.CompareTo(rightOrdinal);
                        if (comparison != 0)
                            return comparison;
                    }

                    return inputOrder[left].CompareTo(inputOrder[right]);
                });
                transaction.Commit();
            }

            return orderedIds.ToArray();
        }

        internal static void SetRelativeDrawOrder(
            Database database,
            ObjectId blockRecordId,
            ObjectId[] orderedIds)
        {
            if (database == null || blockRecordId.IsNull || orderedIds == null || orderedIds.Length < 2)
                return;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord blockRecord = transaction.GetObject(
                    blockRecordId, OpenMode.ForRead, false) as BlockTableRecord;
                if (blockRecord == null || blockRecord.DrawOrderTableId.IsNull)
                    return;

                DrawOrderTable drawOrderTable = transaction.GetObject(
                    blockRecord.DrawOrderTableId, OpenMode.ForWrite, false) as DrawOrderTable;
                if (drawOrderTable == null)
                    return;

                ObjectIdCollection collection = new ObjectIdCollection();
                foreach (ObjectId id in orderedIds)
                {
                    if (id.IsNull || !id.IsValid || id.IsErased)
                        continue;

                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity != null && entity.OwnerId == blockRecordId)
                        collection.Add(id);
                }

                if (collection.Count > 1)
                    drawOrderTable.SetRelativeDrawOrder(collection);

                transaction.Commit();
            }
        }

        internal static void SetBlockDefinitionDrawOrder(
            Database database,
            string blockName,
            ObjectId[] orderedIds)
        {
            if (database == null || string.IsNullOrWhiteSpace(blockName) ||
                orderedIds == null || orderedIds.Length < 2)
            {
                return;
            }

            ObjectId definitionId = ObjectId.Null;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(
                    database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (blockTable == null || !blockTable.Has(blockName))
                    return;

                definitionId = blockTable[blockName];
                transaction.Commit();
            }

            SetRelativeDrawOrder(database, definitionId, orderedIds);
        }

        private sealed class DetailBounds
        {
            internal double MinX;
            internal double MinY;
            internal double MaxX;
            internal double MaxY;
        }

        private sealed class LightCropSummary
        {
            internal int RemovedOutside;
            internal int KeptForXclip;
        }
    }
}
