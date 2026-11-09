using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nexo.AutoCAD
{
    internal sealed class IsometricOpeningPlacement
    {
        internal DetectedWall Opening;
        internal string BlockName;
        internal Point3d LocalPosition;
        internal Vector3d LocalWidth;
        internal Vector3d Normal;
        internal double Rotation;
        internal double Depth;
        internal bool FlipX;
        internal ObjectId ReferenceId;
    }

    internal static class IsometricOpeningInserter
    {
        internal static int Insert(Database database, WallLayout layout, Point3d[] frame, Matrix3d ucs)
        {
            List<IsometricOpeningPlacement> placements = Prepare(layout, frame, ucs);
            if (placements.Count == 0) return 0;
            placements.Sort((left, right) => right.Depth.CompareTo(left.Depth));
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                foreach (IsometricOpeningPlacement placement in placements)
                    AppendToSpace(database, transaction, table, space, ucs, placement);
                transaction.Commit();
            }
            ApplyDynamicProperties(database, placements);
            return placements.Count;
        }

        private static List<IsometricOpeningPlacement> Prepare(
            WallLayout layout, Point3d[] frame, Matrix3d ucs)
        {
            var placements = new List<IsometricOpeningPlacement>();
            if (layout.Openings.Count == 0) return placements;

            Matrix3d inverse = ucs.Inverse();
            Vector3d normal = ucs.CoordinateSystem3d.Zaxis.GetNormal();
            double rotation = IsometricSlabInserter.GetBlockRotation(ucs, normal, 0);
            foreach (DetectedWall opening in layout.Openings)
            {
                if (opening.Opening == OpeningKind.None) continue;
                if (opening.OpeningWidth <= 0 || opening.OpeningWidth > opening.Length + 1e-6 ||
                    opening.OpeningHeight <= 0 || opening.OpeningSill < 0 ||
                    opening.OpeningSill + opening.OpeningHeight > layout.Height + 1e-6)
                    throw new InvalidOperationException("Dimensões inválidas para porta ou janela.");

                Point2d[] source = { opening.A, opening.B, opening.C, opening.D };
                Point3d[] projected = new Point3d[4];
                for (int i = 0; i < 4; i++)
                    projected[i] = Project(source[i], layout, frame).TransformBy(inverse);
                int front = 0;
                for (int i = 1; i < 4; i++)
                    if (projected[i].Y < projected[front].Y) front = i;
                int neighbor = front == 0 ? 1 : front == 1 ? 0 : front == 2 ? 3 : 2;
                Vector3d direction = projected[neighbor] - projected[front];
                if (direction.Length < 1e-8 || direction.Y <= 1e-8 || Math.Abs(direction.X) < 1e-8)
                    throw new InvalidOperationException("Não foi possível alinhar a abertura à face isométrica da parede.");

                double startRatio = (opening.Length - opening.OpeningWidth) / (2 * opening.Length);
                double endRatio = startRatio + opening.OpeningWidth / opening.Length;
                Point2d faceStart = source[front] + (source[neighbor] - source[front]) * startRatio;
                Point2d faceEnd = source[front] + (source[neighbor] - source[front]) * endRatio;
                Point3d localPosition = Project(faceStart, layout, frame).TransformBy(inverse) +
                    new Vector3d(0, opening.OpeningSill, 0);
                Vector3d localWidth = Project(faceEnd, layout, frame).TransformBy(inverse) -
                    Project(faceStart, layout, frame).TransformBy(inverse);
                Point2d sourceCenter = new Point2d((faceStart.X + faceEnd.X) * 0.5,
                    (faceStart.Y + faceEnd.Y) * 0.5);
                double depth = Project(sourceCenter, layout, frame).TransformBy(inverse).Y;

                placements.Add(new IsometricOpeningPlacement
                {
                    Opening = opening,
                    BlockName = opening.Opening == OpeningKind.Door ? "ISO_Porta2" : "ISO_Janela",
                    LocalPosition = localPosition,
                    LocalWidth = localWidth,
                    Normal = normal,
                    Rotation = rotation,
                    Depth = depth,
                    FlipX = direction.X > 0
                });
            }
            return placements;
        }

        private static void AppendToSpace(Database database, Transaction transaction,
            BlockTable table, BlockTableRecord currentSpace,
            Matrix3d ucs, IsometricOpeningPlacement placement)
        {
            if (!table.Has(placement.BlockName))
                throw new InvalidOperationException("Bloco de abertura não encontrado: " + placement.BlockName);

            AddOpeningMask(database, transaction, currentSpace, placement.LocalPosition,
                placement.LocalWidth, placement.Opening.OpeningHeight, ucs);

            var reference = new BlockReference(placement.LocalPosition.TransformBy(ucs),
                table[placement.BlockName])
            {
                Layer = "0",
                Normal = placement.Normal,
                Rotation = placement.Rotation,
                ScaleFactors = new Scale3d(placement.FlipX ? -1 : 1, 1, 1)
            };
            placement.ReferenceId = currentSpace.AppendEntity(reference);
            transaction.AddNewlyCreatedDBObject(reference, true);
        }

        private static void ApplyDynamicProperties(Database database,
            IEnumerable<IsometricOpeningPlacement> placements)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (IsometricOpeningPlacement placement in placements)
                {
                    if (placement.ReferenceId.IsNull) continue;
                    BlockReference reference = (BlockReference)transaction.GetObject(
                        placement.ReferenceId, OpenMode.ForWrite);
                    if (!reference.IsDynamicBlock)
                        throw new InvalidOperationException("O bloco de abertura precisa ser dinâmico.");
                    bool width = false, height = false;
                    foreach (DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
                    {
                        if (string.Equals(property.PropertyName, "Y", StringComparison.OrdinalIgnoreCase))
                        {
                            SetValue(property, placement.Opening.OpeningWidth);
                            width = true;
                        }
                        if (string.Equals(property.PropertyName, "Z", StringComparison.OrdinalIgnoreCase))
                        {
                            SetValue(property, placement.Opening.OpeningHeight);
                            height = true;
                        }
                    }
                    if (!width || !height)
                        throw new InvalidOperationException("O bloco de abertura precisa dos parâmetros Y e Z.");
                    reference.RecordGraphicsModified(true);
                }
                transaction.Commit();
            }
        }

        private static void AddOpeningMask(Database database, Transaction transaction,
            BlockTableRecord space, Point3d position, Vector3d width, double height, Matrix3d ucs)
        {
            Point3d[] corners = {
                position,
                position + width,
                position + width + new Vector3d(0, height, 0),
                position + new Vector3d(0, height, 0)
            };
            Point2dCollection points = new Point2dCollection();
            foreach (Point3d corner in corners) points.Add(new Point2d(corner.X, corner.Y));
            points.Add(new Point2d(corners[0].X, corners[0].Y));
            Wipeout mask = new Wipeout();
            mask.SetDatabaseDefaults(database);
            mask.Layer = "0";
            mask.SetFrom(points, Vector3d.ZAxis);
            mask.TransformBy(ucs);
            space.AppendEntity(mask);
            transaction.AddNewlyCreatedDBObject(mask, true);
        }

        private static Point3d Project(Point2d point, WallLayout layout, Point3d[] frame)
        {
            return frame[0] + (frame[1] - frame[0]) * ((point.X - layout.MinX) / (layout.MaxX - layout.MinX)) +
                (frame[3] - frame[0]) * ((point.Y - layout.MinY) / (layout.MaxY - layout.MinY));
        }

        private static void SetValue(DynamicBlockReferenceProperty property, double value)
        {
            if (property.ReadOnly)
                throw new InvalidOperationException("Parâmetro " + property.PropertyName + " da abertura é somente leitura.");
            object original = property.Value;
            property.Value = Convert.ChangeType(value,
                original == null ? typeof(double) : original.GetType(), CultureInfo.InvariantCulture);
            if (Math.Abs(Convert.ToDouble(property.Value, CultureInfo.InvariantCulture) - value) > 1e-7)
                throw new InvalidOperationException("O bloco não aceitou a dimensão de " + property.PropertyName + ".");
        }
    }
}
