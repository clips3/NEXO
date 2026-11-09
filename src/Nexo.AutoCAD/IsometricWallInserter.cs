using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nexo.AutoCAD
{
    internal static class IsometricWallInserter
    {
        private const string WallBlockName = "ISO_Parede";

        private sealed class Placement
        {
            internal Point3d Position;
            internal double ScreenY;
            internal bool Mirror;
            internal DetectedWall Wall;
        }

        internal static void EnsureDefinitionAvailable(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (table == null || !table.Has(WallBlockName))
                    throw new InvalidOperationException("O bloco dinâmico ISO_Parede não está carregado neste desenho. Insira o bloco original uma vez e repita o comando.");
                transaction.Commit();
            }
        }

        internal static int Insert(Database database, WallLayout layout, Point3d[] frame, Matrix3d ucs)
        {
            if (frame == null || frame.Length != 4 || layout.MaxX <= layout.MinX || layout.MaxY <= layout.MinY)
                throw new InvalidOperationException("A moldura isométrica não contém os pontos necessários para projetar as paredes.");

            Matrix3d worldToUcs = ucs.Inverse();
            Vector3d normal = ucs.CoordinateSystem3d.Zaxis.GetNormal();
            double rotation = IsometricSlabInserter.GetBlockRotation(ucs, normal, 0);
            List<Placement> placements = new List<Placement>();
            foreach (DetectedWall wall in layout.Walls)
            {
                if (!wall.Included) continue;
                Point2d[] source = { wall.A, wall.B, wall.C, wall.D };
                Point3d[] projected = new Point3d[4];
                Point3d[] projectedUcs = new Point3d[4];
                for (int i = 0; i < 4; i++)
                {
                    projected[i] = Project(source[i], layout, frame);
                    projectedUcs[i] = projected[i].TransformBy(worldToUcs);
                }
                int front = 0;
                for (int i = 1; i < 4; i++)
                    if (projectedUcs[i].Y < projectedUcs[front].Y) front = i;
                int longNeighbor = front == 0 ? 1 : front == 1 ? 0 : front == 2 ? 3 : 2;
                Vector3d direction = projectedUcs[longNeighbor] - projectedUcs[front];
                double absX = Math.Abs(direction.X);
                double absY = Math.Abs(direction.Y);
                double slope = absY > 1e-8 ? absX / absY : double.PositiveInfinity;
                double inverseIsometricSlope = 1.0 / Math.Sqrt(3.0);
                double isometricSlope = Math.Sqrt(3.0);
                bool matchesIsometricAxis =
                    Math.Abs(slope - inverseIsometricSlope) <= 0.15 ||
                    Math.Abs(slope - isometricSlope) <= 0.15;
                if (direction.Y <= 1e-8 || absX <= 1e-8 || !matchesIsometricAxis)
                {
                    throw new InvalidOperationException(
                        "A parede projetada não coincide com nenhum dos dois eixos isométricos do bloco ISO_Parede " +
                        "(inclinação medida X/Y: " + slope.ToString("0.###", CultureInfo.InvariantCulture) + "). " +
                        "Confira a moldura do detalhe e a orientação da planta.");
                }
                placements.Add(new Placement { Position = projected[front], ScreenY = projectedUcs[front].Y,
                    Mirror = direction.X < 0, Wall = wall });
            }
            // Draw distant wall references first so that near walls cover their intersections.
            placements.Sort((a, b) => b.ScreenY.CompareTo(a.ScreenY));
            List<Tuple<ObjectId, DetectedWall>> inserted = new List<Tuple<ObjectId, DetectedWall>>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (table == null || !table.Has(WallBlockName))
                    throw new InvalidOperationException("O bloco dinâmico ISO_Parede não está carregado no desenho.");
                BlockTableRecord space = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                if (space == null) throw new InvalidOperationException("Não foi possível acessar o espaço atual.");
                ObjectId definitionId = table[WallBlockName];
                foreach (Placement placement in placements)
                {
                    BlockReference reference = new BlockReference(placement.Position, definitionId)
                    {
                        Layer = "0", Normal = normal, Rotation = rotation,
                        ScaleFactors = new Scale3d(placement.Mirror ? -1.0 : 1.0, 1.0, 1.0)
                    };
                    ObjectId id = space.AppendEntity(reference);
                    transaction.AddNewlyCreatedDBObject(reference, true);
                    inserted.Add(Tuple.Create(id, placement.Wall));
                }
                transaction.Commit();
            }

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (Tuple<ObjectId, DetectedWall> item in inserted)
                {
                    BlockReference reference = transaction.GetObject(item.Item1, OpenMode.ForWrite, false) as BlockReference;
                    if (reference == null || !reference.IsDynamicBlock)
                        throw new InvalidOperationException("ISO_Parede precisa ser o bloco dinâmico original com parâmetros X, Y e Z.");
                    bool x = false, y = false, z = false;
                    foreach (DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
                    {
                        if (string.Equals(property.PropertyName, "X", StringComparison.OrdinalIgnoreCase)) { SetValue(property, item.Item2.Length); x = true; }
                        else if (string.Equals(property.PropertyName, "Y", StringComparison.OrdinalIgnoreCase)) { SetValue(property, item.Item2.Thickness); y = true; }
                        else if (string.Equals(property.PropertyName, "Z", StringComparison.OrdinalIgnoreCase)) { SetValue(property, layout.Height); z = true; }
                    }
                    if (!x || !y || !z) throw new InvalidOperationException("Não encontrei os parâmetros X, Y e Z em ISO_Parede.");
                    reference.RecordGraphicsModified(true);
                }
                transaction.Commit();
            }
            return inserted.Count;
        }

        private static Point3d Project(Point2d point, WallLayout layout, Point3d[] frame)
        {
            double u = (point.X - layout.MinX) / (layout.MaxX - layout.MinX);
            double v = (point.Y - layout.MinY) / (layout.MaxY - layout.MinY);
            return frame[0] + (frame[1] - frame[0]) * u + (frame[3] - frame[0]) * v;
        }

        private static void SetValue(DynamicBlockReferenceProperty property, double value)
        {
            if (property.ReadOnly) throw new InvalidOperationException("O parâmetro " + property.PropertyName + " de ISO_Parede está somente para leitura.");
            try
            {
                object original = property.Value;
                property.Value = Convert.ChangeType(value, original == null ? typeof(double) : original.GetType(), CultureInfo.InvariantCulture);
                double actual = Convert.ToDouble(property.Value, CultureInfo.InvariantCulture);
                if (Math.Abs(actual - value) > Math.Max(1e-8, Math.Abs(value) * 1e-9))
                    throw new InvalidOperationException("O valor informado não foi aceito pelo bloco.");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Não foi possível ajustar " + property.PropertyName + " em ISO_Parede: " + exception.Message, exception);
            }
        }
    }
}
