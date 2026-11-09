using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(Nexo.AutoCAD.IsometricBlockCommands))]

namespace Nexo.AutoCAD
{
    public sealed class IsometricBlockCommands
    {
        private const double ScaleX = 1.2260;
        private const double ScaleY = 0.7078;
        private const double ScaleZ = 1.2260;

        [NexoRibbonCommand(
            "ISOMETRICO",
            "Conversão",
            "Isométrico",
            "Escolhe uma das três faces do cubo, gira a planta em passos de 90° e aplica a transformação isométrica correspondente.",
            "icon_isometric.png")]
        [CommandMethod("ISOMETRICO", CommandFlags.UsePickSet)]
        public void CreateIsometricBlock()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                ObjectId[] selectedIds = GetImpliedSelection(editor);
                IsometricSelection? orientation = IsometricDirectionDialog.Choose();
                if (!orientation.HasValue)
                    return;

                if (selectedIds.Length == 0)
                {
                    PromptSelectionResult result = editor.GetSelection();
                    if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
                        return;

                    selectedIds = result.Value.GetObjectIds();
                }

                RunIsometricWorkflow(document, selectedIds, orientation.Value);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n[NEXO] Falha em ISOMETRICO: " + exception.Message);
            }
        }

        private static ObjectId[] GetImpliedSelection(Editor editor)
        {
            PromptSelectionResult result = editor.SelectImplied();
            return result.Status == PromptStatus.OK && result.Value != null
                ? result.Value.GetObjectIds()
                : Array.Empty<ObjectId>();
        }

        internal static void RunIsometricWorkflow(
            Document document,
            ObjectId[] selectedIds,
            IsometricSelection orientation,
            bool manageUndoGroup = true,
            Point3d? centerOverride = null,
            Point2d? slabDimensionsOverride = null,
            Point3d? targetFrameCenter = null,
            WallLayout wallLayout = null)
        {
            Editor editor = document.Editor;
            Database database = document.Database;
            Point3d center;
            ObjectId[] eligibleIds;
            if (centerOverride.HasValue)
            {
                eligibleIds = FilterCurrentSpace(database, selectedIds);
                if (eligibleIds.Length == 0)
                {
                    editor.WriteMessage("\n[NEXO] Nenhum objeto da seleção está no espaço atual.");
                    return;
                }
                center = centerOverride.Value;
            }
            else if (!AutoBlockCommand.TryGetSelectionCenter(database, selectedIds, out center, out eligibleIds))
            {
                editor.WriteMessage("\n[NEXO] Não foi possível calcular o centro dos objetos no espaço atual.");
                return;
            }

            if (eligibleIds.Length != selectedIds.Length)
            {
                editor.WriteMessage("\n[NEXO] Apenas objetos do espaço atual podem ser convertidos. A seleção foi ajustada.");
            }

            Matrix3d ucs = editor.CurrentUserCoordinateSystem;
            Point2d? slabDimensions = null;
            if (orientation.Face == IsometricFace.Top)
            {
                IsometricSlabInserter.EnsureDefinitionAvailable(database);
                slabDimensions = slabDimensionsOverride ?? GetSelectionDimensionsInUcs(database, eligibleIds, ucs);
            }

            eligibleIds = DetailCommand.SortByDrawOrder(database, eligibleIds);
            int segmentedArcCount = 0;

            Point3d centerInUcs = center.TransformBy(ucs.Inverse());
            string insertionPoint = AutoBlockCommand.FormatPoint(centerInUcs);
            int planRotation = NormalizeRotation(orientation.RotationDegrees);
            string planRotationAngle = FormatCommandAngle(GetPreRotationDegrees(orientation.Face, planRotation));
            int cubeRotationDegrees = GetPostRotationDegrees(orientation.Face);
            string cubeRotationAngle = cubeRotationDegrees == 0
                ? null
                : FormatCommandAngle(cubeRotationDegrees);
            string blockName = AutoBlockCommand.CreateUniqueBlockName(database);

            bool undoGroupStarted = false;
            bool slabInserted = false;
            try
            {
                if (manageUndoGroup)
                {
                    editor.Command("_.UNDO", "_Begin");
                    undoGroupStarted = true;
                }

                ObjectId[] segmentedIds;
                segmentedArcCount = IsometricArcSegmenter.ConvertSelected(
                    database,
                    eligibleIds,
                    out segmentedIds,
                    centerOverride.HasValue);
                eligibleIds = segmentedIds;
                DetailCommand.SetRelativeDrawOrder(database, database.CurrentSpaceId, eligibleIds);

                SelectionSet selection = SelectionSet.FromObjectIds(eligibleIds);
                editor.Command("_.ROTATE", selection, "", "_non", insertionPoint, planRotationAngle);
                editor.Command("_.-BLOCK", blockName, insertionPoint, selection, "");
                DetailCommand.SetBlockDefinitionDrawOrder(database, blockName, eligibleIds);
                editor.Command("_.-INSERT", blockName, insertionPoint, 1.0, 1.0, 0.0);

                ObjectId blockReferenceId = FindBlockReference(database, blockName);
                if (blockReferenceId.IsNull)
                    throw new InvalidOperationException("A referência do bloco criado não foi encontrada no espaço atual.");

                ApplyIsometricScale(database, blockReferenceId);
                if (cubeRotationAngle != null)
                {
                    SelectionSet blockSelection = SelectionSet.FromObjectIds(new[] { blockReferenceId });
                    editor.Command("_.ROTATE", blockSelection, "", "_non", insertionPoint, cubeRotationAngle);
                }

                if (targetFrameCenter.HasValue)
                {
                    Point3d actualCenter = GetTransformedDetailFrameCenter(database, blockReferenceId);
                    Vector3d displacement = targetFrameCenter.Value - actualCenter;
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        BlockReference reference = transaction.GetObject(blockReferenceId, OpenMode.ForWrite, false) as BlockReference;
                        if (reference == null)
                            throw new InvalidOperationException("Não foi possível posicionar o bloco isométrico.");
                        reference.TransformBy(Matrix3d.Displacement(displacement));
                        transaction.Commit();
                    }
                }

                if (orientation.Face == IsometricFace.Top)
                {
                    Point2d dimensions = slabDimensions.Value;
                    IsometricSlabInserter.Insert(
                        document,
                        slabDimensionsOverride.HasValue
                            ? GetTransformedDetailFrameCenter(database, blockReferenceId)
                            : center,
                        dimensions,
                        planRotation,
                        ucs);
                    slabInserted = true;
                }

                int insertedWalls = 0;
                int insertedOpenings = 0;
                if (wallLayout != null)
                {
                    if (orientation.Face != IsometricFace.Top)
                        throw new InvalidOperationException("A geração de paredes requer a face superior do isométrico.");
                    Point3d[] wallFrame = GetTransformedDetailFrameCorners(database, blockReferenceId);
                    insertedWalls = IsometricCompositeWalls.Insert(database, wallLayout, wallFrame, ucs);
                    insertedOpenings = IsometricOpeningInserter.Insert(database, wallLayout, wallFrame, ucs);
                    if ((insertedWalls > 0 || insertedOpenings > 0) &&
                        Convert.ToInt16(Application.GetSystemVariable("WIPEOUTFRAME"), CultureInfo.InvariantCulture) != 0)
                        Application.SetSystemVariable("WIPEOUTFRAME", 0);
                }

                if (manageUndoGroup)
                {
                    editor.Command("_.UNDO", "_End");
                    undoGroupStarted = false;
                }
                editor.Regen();
                string targetName = orientation.Face == IsometricFace.Top
                    ? GetOrientationName(orientation)
                    : "face " + GetFaceName(orientation.Face);
                editor.WriteMessage(
                    "\n[NEXO] Isométrico " +
                    targetName +
                    " (giro " +
                    planRotation.ToString(CultureInfo.InvariantCulture) + "°)" +
                    " aplicado a " + eligibleIds.Length.ToString(CultureInfo.InvariantCulture) + " objeto(s)." +
                    " Arcos convertidos em segmentos: " + segmentedArcCount.ToString(CultureInfo.InvariantCulture) + "." +
                    (slabInserted
                        ? " ISO_Laje inserido conforme as dimensões da planta."
                        : string.Empty) +
                    (wallLayout != null && insertedWalls > 0 ? " Conjunto de paredes gerado." : string.Empty) +
                    (insertedOpenings > 0
                        ? " Portas e janelas sobrepostas à parede."
                        : string.Empty));
            }
            catch
            {
                if (manageUndoGroup && undoGroupStarted)
                {
                    try
                    {
                        editor.Command("_.UNDO", "_End");
                        // Undo only the group started by this workflow; Back without a mark
                        // can prompt to undo the complete drawing session.
                        editor.Command("_.UNDO", 1);
                    }
                    catch
                    {
                        // Keep the original failure for the caller to report.
                    }
                }
                throw;
            }
        }

        private static Point3d GetTransformedDetailFrameCenter(Database database, ObjectId blockReferenceId)
        {
            Point3d[] points = GetTransformedDetailFrameCorners(database, blockReferenceId);
            return new Point3d((points[0].X + points[1].X + points[2].X + points[3].X) / 4.0,
                (points[0].Y + points[1].Y + points[2].Y + points[3].Y) / 4.0,
                (points[0].Z + points[1].Z + points[2].Z + points[3].Z) / 4.0);
        }

        private static Point3d[] GetTransformedDetailFrameCorners(Database database, ObjectId blockReferenceId)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockReference reference = transaction.GetObject(blockReferenceId, OpenMode.ForRead, false) as BlockReference;
                if (reference == null)
                    throw new InvalidOperationException("Não foi possível acessar o bloco isométrico para alinhar a laje.");

                BlockTableRecord definition = transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                Polyline frame = null;
                if (definition != null)
                {
                    foreach (ObjectId id in definition)
                    {
                        Polyline candidate = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                        if (candidate == null || !candidate.Closed || candidate.NumberOfVertices != 4 ||
                            !string.Equals(candidate.Layer, "PP_REGIÃO", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (frame != null)
                            throw new InvalidOperationException("Mais de uma moldura de detalhe foi encontrada no bloco isométrico.");
                        frame = candidate;
                    }
                }

                if (frame == null)
                    throw new InvalidOperationException("Não foi possível localizar a moldura do detalhe para posicionar ISO_Laje.");

                Point3d[] result = new Point3d[4];
                for (int i = 0; i < 4; i++)
                {
                    result[i] = frame.GetPoint3dAt(i).TransformBy(reference.BlockTransform);
                }
                transaction.Commit();
                return result;
            }
        }

        private static Point2d GetSelectionDimensionsInUcs(
            Database database,
            ObjectId[] objectIds,
            Matrix3d ucs)
        {
            Matrix3d wcsToUcs = ucs.Inverse();
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            bool hasExtents = false;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in objectIds)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null)
                        continue;

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

                        foreach (Point3d corner in corners)
                        {
                            Point3d point = corner.TransformBy(wcsToUcs);
                            minX = Math.Min(minX, point.X);
                            minY = Math.Min(minY, point.Y);
                            maxX = Math.Max(maxX, point.X);
                            maxY = Math.Max(maxY, point.Y);
                            hasExtents = true;
                        }
                    }
                    catch
                    {
                        // Some proxy objects do not expose geometric extents. Other selected
                        // entities still define the insertion slab's overall plan dimensions.
                    }
                }

                transaction.Commit();
            }

            double width = maxX - minX;
            double height = maxY - minY;
            if (!hasExtents || width <= Tolerance.Global.EqualPoint || height <= Tolerance.Global.EqualPoint)
            {
                throw new InvalidOperationException(
                    "Não foi possível obter largura e altura da seleção para dimensionar o bloco ISO_Laje.");
            }

            return new Point2d(width, height);
        }

        private static ObjectId[] FilterCurrentSpace(Database database, ObjectId[] selectedIds)
        {
            System.Collections.Generic.List<ObjectId> accepted =
                new System.Collections.Generic.List<ObjectId>();
            ObjectId currentSpaceId = database.CurrentSpaceId;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selectedIds)
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

        private static int GetPreRotationDegrees(IsometricFace face, int planRotation)
        {
            // Decompose each projected face basis as R(post) * diag(ScaleX, ScaleY) * R(pre).
            // The user rotation is applied in the plane before that face projection.
            switch (face)
            {
                case IsometricFace.Top:
                    return -45 - planRotation;
                case IsometricFace.Left:
                    return 45 - planRotation;
                case IsometricFace.Right:
                    return -45 - planRotation;
                default:
                    throw new ArgumentOutOfRangeException(nameof(face));
            }
        }

        private static int GetPostRotationDegrees(IsometricFace face)
        {
            switch (face)
            {
                case IsometricFace.Top:
                    return 0;
                case IsometricFace.Left:
                    return -60;
                case IsometricFace.Right:
                    return 60;
                default:
                    throw new ArgumentOutOfRangeException(nameof(face));
            }
        }

        private static int NormalizeRotation(int rotationDegrees)
        {
            int normalized = ((rotationDegrees % 360) + 360) % 360;
            if (normalized % 90 != 0)
                throw new ArgumentOutOfRangeException(nameof(rotationDegrees), "A orientação deve avançar em passos de 90°.");
            return normalized;
        }

        private static string FormatCommandAngle(int counterClockwiseDegrees)
        {
            int angleDirection = 0;
            try
            {
                object value = Application.GetSystemVariable("ANGDIR");
                angleDirection = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                // AutoCAD's default is counterclockwise-positive.
            }

            if (angleDirection == 1)
                counterClockwiseDegrees = -counterClockwiseDegrees;

            return (counterClockwiseDegrees < 0 ? "-" : string.Empty) +
                   Math.Abs(counterClockwiseDegrees).ToString(CultureInfo.InvariantCulture) + "d";
        }

        private static string GetOrientationName(IsometricSelection orientation)
        {
            int normalized = NormalizeRotation(orientation.RotationDegrees);
            return normalized == 0 || normalized == 90 ? "Direita" : "Esquerda";
        }

        private static string GetFaceName(IsometricFace face)
        {
            switch (face)
            {
                case IsometricFace.Top:
                    return "superior";
                case IsometricFace.Left:
                    return "lateral esquerda";
                case IsometricFace.Right:
                    return "lateral direita";
                default:
                    throw new ArgumentOutOfRangeException(nameof(face));
            }
        }

        private static ObjectId FindBlockReference(Database database, string blockName)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (currentSpace == null)
                    return ObjectId.Null;

                foreach (ObjectId id in currentSpace)
                {
                    BlockReference reference = transaction.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                    if (reference == null)
                        continue;

                    BlockTableRecord definition = transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                    if (definition != null && string.Equals(definition.Name, blockName, StringComparison.OrdinalIgnoreCase))
                    {
                        transaction.Commit();
                        return id;
                    }
                }

                transaction.Commit();
            }

            return ObjectId.Null;
        }

        private static void ApplyIsometricScale(Database database, ObjectId blockReferenceId)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockReference reference = transaction.GetObject(blockReferenceId, OpenMode.ForWrite, false) as BlockReference;
                if (reference == null)
                    throw new InvalidOperationException("O objeto criado não é uma referência de bloco válida.");

                reference.ScaleFactors = new Scale3d(ScaleX, ScaleY, ScaleZ);
                transaction.Commit();
            }
        }
    }
}
