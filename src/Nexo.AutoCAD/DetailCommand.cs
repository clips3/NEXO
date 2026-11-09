using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(Nexo.AutoCAD.DetailCommand))]

namespace Nexo.AutoCAD
{
    public sealed partial class DetailCommand
    {
        private const string DetailLayerName = "PP_REGIÃO";
        private const double FrameWidth = 0.01;

        [NexoRibbonCommand(
            "DET",
            "Detalhe",
            "Detalhe",
            "Cria uma cópia em bloco da área retangular, recortada por XCLIP, com moldura e wipeout.",
            "icon_detail.png")]
        [CommandMethod("DET")]
        public void CreateDetail()
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
                    new PromptPointOptions("\nClique no primeiro canto do detalhe: "));
                if (firstPointResult.Status != PromptStatus.OK)
                    return;

                PromptCornerOptions secondCornerOptions = new PromptCornerOptions(
                    "\nClique no canto oposto do detalhe: ", firstPointResult.Value);
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

                Point3d firstUcsOnPlane = new Point3d(firstUcs.X, firstUcs.Y, 0.0);
                Point3d firstWcsOnPlane = firstUcsOnPlane.TransformBy(ucs);
                PromptPointOptions destinationOptions = new PromptPointOptions(
                    "\nClique onde deseja posicionar o detalhe: ")
                {
                    UseBasePoint = true,
                    BasePoint = firstWcsOnPlane
                };
                PromptPointResult destinationResult = editor.GetPoint(destinationOptions);
                if (destinationResult.Status != PromptStatus.OK)
                    return;

                Point3d destinationUcsRaw = destinationResult.Value.TransformBy(wcsToUcs);
                Point3d destinationUcs = new Point3d(destinationUcsRaw.X, destinationUcsRaw.Y, 0.0);
                string sourcePointString = AutoBlockCommand.FormatPoint(firstUcsOnPlane);
                string destinationPointString = AutoBlockCommand.FormatPoint(destinationUcs);
                string blockName = CreateUniqueDetailBlockName(database);

                oldCommandEcho = Application.GetSystemVariable("CMDECHO");
                commandEchoCaptured = true;
                Application.SetSystemVariable("CMDECHO", 0);

                editor.Command("_.UNDO", "_Begin");
                undoGroupStarted = true;

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

                DetailArtifacts artifacts = CreateDetailArtifacts(
                    database,
                    ucs,
                    minX,
                    minY,
                    maxX,
                    maxY,
                    destinationUcs.X - firstUcs.X,
                    destinationUcs.Y - firstUcs.Y);

                SelectionSet copiedSelection = SelectionSet.FromObjectIds(copiedIds);
                editor.Command("_.-BLOCK", blockName, destinationPointString, copiedSelection, "");
                editor.Command("_.-INSERT", blockName, destinationPointString, 1.0, 1.0, 0.0);

                ObjectId blockReferenceId = FindBlockReference(database, blockName);
                if (blockReferenceId.IsNull)
                    throw new InvalidOperationException("A referência do bloco criado não foi encontrada no espaço atual.");

                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockReference blockReference = transaction.GetObject(
                        blockReferenceId, OpenMode.ForWrite, false) as BlockReference;
                    if (blockReference == null)
                        throw new InvalidOperationException("O detalhe inserido não é uma referência de bloco válida.");

                    blockReference.Layer = "0";
                    transaction.Commit();
                }

                SelectionSet blockSelection = SelectionSet.FromObjectIds(new[] { blockReferenceId });
                SelectionSet boundarySelection = SelectionSet.FromObjectIds(new[] { artifacts.DestinationFrameId });
                editor.Command("_.XCLIP", blockSelection, "", "_N", "_S", boundarySelection, "");

                ApplyDetailDrawOrder(database, blockReferenceId, artifacts);

                editor.Command("_.UNDO", "_End");
                undoGroupStarted = false;
                editor.Regen();
                editor.WriteMessage(
                    "\n[NEXO] Detalhe criado com " + copiedIds.Length.ToString(CultureInfo.InvariantCulture) +
                    " objeto(s) no bloco " + blockName + ".");
            }
            catch (System.Exception exception)
            {
                if (undoGroupStarted)
                {
                    try
                    {
                        editor.Command("_.UNDO", "_End");
                        // The operation group is a single undo item. Back without a mark
                        // can prompt AutoCAD to undo the entire session.
                        editor.Command("_.UNDO", 1);
                    }
                    catch
                    {
                        // Preserve the original exception if AutoCAD cannot undo the command group.
                    }
                }

                editor.WriteMessage("\n[NEXO] Falha em DET: " + exception.Message);
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

        private static ObjectId[] FilterCurrentSpace(Database database, ObjectId[] selectionIds)
        {
            List<ObjectId> accepted = new List<ObjectId>();
            ObjectId currentSpaceId = database.CurrentSpaceId;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selectionIds)
                {
                    if (id.IsNull || !id.IsValid || id.IsErased)
                        continue;

                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity != null && entity.OwnerId == currentSpaceId)
                        accepted.Add(id);
                }
                transaction.Commit();
            }

            return accepted.ToArray();
        }

        private static HashSet<ObjectId> GetCurrentSpaceIds(Database database)
        {
            HashSet<ObjectId> ids = new HashSet<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (currentSpace != null)
                {
                    foreach (ObjectId id in currentSpace)
                        ids.Add(id);
                }
                transaction.Commit();
            }
            return ids;
        }

        private static ObjectId[] GetNewCurrentSpaceIds(Database database, HashSet<ObjectId> existingIds)
        {
            List<ObjectId> newIds = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (currentSpace != null)
                {
                    foreach (ObjectId id in currentSpace)
                    {
                        if (!existingIds.Contains(id))
                            newIds.Add(id);
                    }
                }
                transaction.Commit();
            }
            return newIds.ToArray();
        }

        private static DetailArtifacts CreateDetailArtifacts(
            Database database,
            Matrix3d ucs,
            double minX,
            double minY,
            double maxX,
            double maxY,
            double offsetX,
            double offsetY)
        {
            ObjectId destinationFrameId;
            ObjectId wipeoutId;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("Não foi possível acessar o espaço atual do desenho.");

                ObjectId detailLayerId = EnsureDetailLayer(database, transaction);
                Polyline sourceFrame = CreateRectangle(database, ucs, minX, minY, maxX, maxY, 0.0, 0.0, detailLayerId);
                currentSpace.AppendEntity(sourceFrame);
                transaction.AddNewlyCreatedDBObject(sourceFrame, true);

                Polyline destinationFrame = CreateRectangle(
                    database, ucs, minX, minY, maxX, maxY, offsetX, offsetY, detailLayerId);
                currentSpace.AppendEntity(destinationFrame);
                transaction.AddNewlyCreatedDBObject(destinationFrame, true);
                destinationFrameId = destinationFrame.ObjectId;

                Wipeout wipeout = CreateWipeout(database, ucs, minX, minY, maxX, maxY, offsetX, offsetY);
                currentSpace.AppendEntity(wipeout);
                transaction.AddNewlyCreatedDBObject(wipeout, true);
                wipeoutId = wipeout.ObjectId;

                transaction.Commit();
            }

            return new DetailArtifacts(destinationFrameId, wipeoutId);
        }

        private static ObjectId EnsureDetailLayer(Database database, Transaction transaction)
        {
            LayerTable layerTable = transaction.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (layerTable == null)
                throw new InvalidOperationException("Não foi possível acessar a tabela de layers.");

            ObjectId layerId;
            if (layerTable.Has(DetailLayerName))
            {
                layerId = layerTable[DetailLayerName];
                LayerTableRecord existingLayer = transaction.GetObject(layerId, OpenMode.ForRead) as LayerTableRecord;
                if (existingLayer != null && existingLayer.IsLocked)
                    throw new InvalidOperationException("A layer PP_REGIÃO está bloqueada.");
                return layerId;
            }

            layerTable = transaction.GetObject(database.LayerTableId, OpenMode.ForWrite) as LayerTable;
            LayerTableRecord detailLayer = new LayerTableRecord { Name = DetailLayerName };
            layerId = layerTable.Add(detailLayer);
            transaction.AddNewlyCreatedDBObject(detailLayer, true);
            return layerId;
        }

        private static Polyline CreateRectangle(
            Database database,
            Matrix3d ucs,
            double minX,
            double minY,
            double maxX,
            double maxY,
            double offsetX,
            double offsetY,
            ObjectId layerId)
        {
            Point3d[] corners = CreateRectangleCorners(minX, minY, maxX, maxY, offsetX, offsetY);
            Polyline rectangle = new Polyline(corners.Length);
            rectangle.SetDatabaseDefaults(database);
            rectangle.LayerId = layerId;
            rectangle.ConstantWidth = FrameWidth;
            for (int index = 0; index < corners.Length; index++)
            {
                rectangle.AddVertexAt(index, new Point2d(corners[index].X, corners[index].Y), 0.0, 0.0, 0.0);
            }
            rectangle.Closed = true;
            rectangle.TransformBy(ucs);
            return rectangle;
        }

        private static Wipeout CreateWipeout(
            Database database,
            Matrix3d ucs,
            double minX,
            double minY,
            double maxX,
            double maxY,
            double offsetX,
            double offsetY)
        {
            Point3d[] corners = CreateRectangleCorners(minX, minY, maxX, maxY, offsetX, offsetY);
            Point2dCollection points = new Point2dCollection();
            foreach (Point3d point in corners)
                points.Add(new Point2d(point.X, point.Y));
            points.Add(new Point2d(corners[0].X, corners[0].Y));

            Wipeout wipeout = new Wipeout();
            wipeout.SetDatabaseDefaults(database);
            wipeout.SetFrom(points, Vector3d.ZAxis);
            wipeout.TransformBy(ucs);
            return wipeout;
        }

        private static Point3d[] CreateRectangleCorners(
            double minX,
            double minY,
            double maxX,
            double maxY,
            double offsetX,
            double offsetY)
        {
            return new[]
            {
                new Point3d(minX + offsetX, minY + offsetY, 0.0),
                new Point3d(maxX + offsetX, minY + offsetY, 0.0),
                new Point3d(maxX + offsetX, maxY + offsetY, 0.0),
                new Point3d(minX + offsetX, maxY + offsetY, 0.0)
            };
        }

        private static string CreateUniqueDetailBlockName(Database database)
        {
            string baseName = "DET_" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
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

        private static ObjectId FindBlockReference(Database database, string blockName)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (currentSpace == null)
                    return ObjectId.Null;

                foreach (ObjectId id in currentSpace)
                {
                    BlockReference reference = transaction.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                    if (reference == null)
                        continue;

                    BlockTableRecord definition = transaction.GetObject(
                        reference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                    if (definition != null && string.Equals(
                        definition.Name, blockName, StringComparison.OrdinalIgnoreCase))
                    {
                        transaction.Commit();
                        return id;
                    }
                }

                transaction.Commit();
            }
            return ObjectId.Null;
        }

        private static void ApplyDetailDrawOrder(
            Database database,
            ObjectId blockReferenceId,
            DetailArtifacts artifacts)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("Não foi possível atualizar a ordem visual do detalhe.");

                DrawOrderTable drawOrder = transaction.GetObject(
                    currentSpace.DrawOrderTableId, OpenMode.ForWrite) as DrawOrderTable;
                if (drawOrder != null)
                {
                    // The wipeout must cover existing geometry, then the block and frame sit above it.
                    drawOrder.MoveToTop(new ObjectIdCollection { artifacts.WipeoutId });
                    drawOrder.MoveToTop(new ObjectIdCollection { blockReferenceId });
                    drawOrder.MoveToTop(new ObjectIdCollection { artifacts.DestinationFrameId });
                }
                transaction.Commit();
            }
        }

        private sealed class DetailArtifacts
        {
            internal DetailArtifacts(ObjectId destinationFrameId, ObjectId wipeoutId)
            {
                DestinationFrameId = destinationFrameId;
                WipeoutId = wipeoutId;
            }

            internal ObjectId DestinationFrameId { get; }
            internal ObjectId WipeoutId { get; }
        }
    }
}
