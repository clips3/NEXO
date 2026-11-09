using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nexo.AutoCAD
{
    internal static class IsometricSlabInserter
    {
        private const string SlabDefinitionName = "ISO_Laje";
        private const string WidthPropertyName = "X";
        private const string HeightPropertyName = "Y";

        internal static void EnsureDefinitionAvailable(Database database)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(
                    database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (blockTable == null || !blockTable.Has(SlabDefinitionName))
                {
                    throw new InvalidOperationException(
                        "A definição do bloco dinâmico ISO_Laje não está carregada neste desenho. " +
                        "Insira o seu bloco ISO_Laje uma vez no desenho e repita o isométrico.");
                }

                transaction.Commit();
            }
        }

        internal static void Insert(
            Document document,
            Point3d targetCenter,
            Point2d planDimensions,
            int rotationDegrees,
            Matrix3d ucs)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (planDimensions.X <= Tolerance.Global.EqualPoint ||
                planDimensions.Y <= Tolerance.Global.EqualPoint)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(planDimensions), "A largura e a altura da laje precisam ser maiores que zero.");
            }

            Database database = document.Database;
            ObjectId definitionId;
            Vector3d normal = ucs.CoordinateSystem3d.Zaxis.GetNormal();
            // ISO_Laje is already drawn in isometric projection. Its footprint uses
            // the same screen axes for all four plan rotations.
            double rotation = GetBlockRotation(ucs, normal, 0);
            bool swapDimensions = ((rotationDegrees % 180) + 180) % 180 == 0;
            if (swapDimensions)
                planDimensions = new Point2d(planDimensions.Y, planDimensions.X);
            ObjectId slabReferenceId;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(
                    database.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (blockTable == null || !blockTable.Has(SlabDefinitionName))
                {
                    throw new InvalidOperationException(
                        "A definição do bloco dinâmico ISO_Laje não está carregada neste desenho.");
                }
                definitionId = blockTable[SlabDefinitionName];

                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException("Não foi possível acessar o espaço atual para inserir ISO_Laje.");

                BlockReference slabReference = new BlockReference(targetCenter, definitionId)
                {
                    Normal = normal,
                    Rotation = rotation,
                    Layer = "0"
                };
                slabReferenceId = currentSpace.AppendEntity(slabReference);
                transaction.AddNewlyCreatedDBObject(slabReference, true);
                transaction.Commit();
            }

            SetPlanDimensions(database, slabReferenceId, planDimensions);
            AlignAndSendToBack(database, slabReferenceId, targetCenter, normal);
        }

        private static void SetPlanDimensions(Database database, ObjectId slabReferenceId, Point2d planDimensions)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockReference slabReference = transaction.GetObject(
                    slabReferenceId, OpenMode.ForWrite, false) as BlockReference;
                if (slabReference == null)
                    throw new InvalidOperationException("A referência de ISO_Laje não é válida.");
                if (!slabReference.IsDynamicBlock)
                {
                    throw new InvalidOperationException(
                        "ISO_Laje está carregado, mas não é um bloco dinâmico. " +
                        "Ajuste X e Y manualmente ou carregue a definição dinâmica original.");
                }

                bool widthFound = false;
                bool heightFound = false;
                bool thicknessFound = false;
                foreach (DynamicBlockReferenceProperty property in slabReference.DynamicBlockReferencePropertyCollection)
                {
                    if (string.Equals(property.PropertyName, WidthPropertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        SetLengthProperty(property, planDimensions.X);
                        widthFound = true;
                    }
                    else if (string.Equals(property.PropertyName, HeightPropertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        SetLengthProperty(property, planDimensions.Y);
                        heightFound = true;
                    }
                    else if (string.Equals(property.PropertyName, "Z", StringComparison.OrdinalIgnoreCase))
                    {
                        SetLengthProperty(property, 0.15);
                        thicknessFound = true;
                    }
                }

                if (!widthFound || !heightFound || !thicknessFound)
                {
                    throw new InvalidOperationException(
                        "Não encontrei os parâmetros dinâmicos X, Y e Z de ISO_Laje. " +
                        "Confira se a definição inserida é a versão original do bloco.");
                }

                slabReference.RecordGraphicsModified(true);
                transaction.Commit();
            }
        }

        private static void SetLengthProperty(DynamicBlockReferenceProperty property, double value)
        {
            if (property.ReadOnly)
            {
                throw new InvalidOperationException(
                    "O parâmetro dinâmico " + property.PropertyName + " de ISO_Laje está somente para leitura.");
            }

            object currentValue = property.Value;
            Type valueType = currentValue == null ? typeof(double) : currentValue.GetType();
            object convertedValue;
            try
            {
                convertedValue = Convert.ChangeType(value, valueType, CultureInfo.InvariantCulture);
                property.Value = convertedValue;
                double assignedValue = Convert.ToDouble(property.Value, CultureInfo.InvariantCulture);
                if (Math.Abs(assignedValue - value) > Math.Max(Tolerance.Global.EqualPoint, Math.Abs(value) * 1e-9))
                {
                    throw new InvalidOperationException(
                        "O parâmetro aceitou " + assignedValue.ToString("0.###", CultureInfo.InvariantCulture) +
                        " em vez de " + value.ToString("0.###", CultureInfo.InvariantCulture) + ".");
                }
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "Não foi possível definir " + property.PropertyName + " de ISO_Laje para " +
                    value.ToString("0.###", CultureInfo.InvariantCulture) + ". " + exception.Message,
                    exception);
            }
        }

        private static void AlignAndSendToBack(
            Database database,
            ObjectId slabReferenceId,
            Point3d targetCenter,
            Vector3d normal)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockReference slabReference = transaction.GetObject(
                    slabReferenceId, OpenMode.ForWrite, false) as BlockReference;
                if (slabReference == null)
                    throw new InvalidOperationException("A referência de ISO_Laje não é válida após o ajuste de dimensões.");

                // ISO_Laje's six-vertex outline starts at the front corner; its
                // highest vertex is the opposite corner of the top face. Using
                // those two points avoids including thickness and wipeout bounds.
                BlockTableRecord definition = transaction.GetObject(
                    slabReference.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
                Point3d? backCorner = null;
                if (definition != null)
                {
                    foreach (ObjectId id in definition)
                    {
                        Polyline outline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                        if (outline == null || outline.NumberOfVertices != 6 || !outline.Closed)
                            continue;
                        for (int i = 0; i < outline.NumberOfVertices; i++)
                        {
                            Point3d vertex = outline.GetPoint3dAt(i);
                            if (!backCorner.HasValue || vertex.Y > backCorner.Value.Y)
                                backCorner = vertex;
                        }
                    }
                }
                if (!backCorner.HasValue)
                    throw new InvalidOperationException("Não foi possível localizar o contorno superior de ISO_Laje para posicioná-lo.");

                Point3d frontCorner = Point3d.Origin.TransformBy(slabReference.BlockTransform);
                Point3d oppositeCorner = backCorner.Value.TransformBy(slabReference.BlockTransform);
                Point3d topCenter = new Point3d(
                    (frontCorner.X + oppositeCorner.X) * 0.5,
                    (frontCorner.Y + oppositeCorner.Y) * 0.5,
                    (frontCorner.Z + oppositeCorner.Z) * 0.5);
                Vector3d displacement = targetCenter - topCenter;
                displacement -= normal * displacement.DotProduct(normal);
                if (displacement.Length > Tolerance.Global.EqualPoint)
                    slabReference.TransformBy(Matrix3d.Displacement(displacement));

                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (currentSpace != null && !currentSpace.DrawOrderTableId.IsNull)
                {
                    DrawOrderTable drawOrder = transaction.GetObject(
                        currentSpace.DrawOrderTableId, OpenMode.ForWrite, false) as DrawOrderTable;
                    if (drawOrder != null)
                        drawOrder.MoveToBottom(new ObjectIdCollection(new[] { slabReferenceId }));
                }

                transaction.Commit();
            }
        }

        internal static double GetBlockRotation(Matrix3d ucs, Vector3d normal, int rotationDegrees)
        {
            Matrix3d ocsToWcs = Matrix3d.PlaneToWorld(normal);
            Vector3d ocsXAxis = ocsToWcs.CoordinateSystem3d.Xaxis;
            Vector3d ucsXAxis = ucs.CoordinateSystem3d.Xaxis;
            ocsXAxis -= normal * ocsXAxis.DotProduct(normal);
            ucsXAxis -= normal * ucsXAxis.DotProduct(normal);
            ocsXAxis = ocsXAxis.GetNormal();
            ucsXAxis = ucsXAxis.GetNormal();

            double ucsAngle = Math.Atan2(
                normal.DotProduct(ocsXAxis.CrossProduct(ucsXAxis)),
                ocsXAxis.DotProduct(ucsXAxis));
            return ucsAngle + rotationDegrees * Math.PI / 180.0;
        }
    }
}
