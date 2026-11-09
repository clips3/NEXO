using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nexo.AutoCAD
{
    /// <summary>
    /// Ports ARC2SEG's 64-segment approximation for curves that otherwise distort
    /// when the isometric workflow applies non-uniform block scaling.
    /// </summary>
    internal static class IsometricArcSegmenter
    {
        private const int SegmentsPerArc = 64;
        private const double GeometryTolerance = 1e-10;
        private const string GeneratedDetailPrefix = "DET_L_";

        internal static int ConvertSelected(
            Database database,
            ObjectId[] selectedIds,
            out ObjectId[] updatedIds,
            bool processGeneratedDetailDefinition)
        {
            updatedIds = selectedIds ?? Array.Empty<ObjectId>();
            if (database == null || updatedIds.Length == 0)
                return 0;

            List<ObjectId> resultIds = new List<ObjectId>(updatedIds);
            Dictionary<ObjectId, ObjectId> replacements = new Dictionary<ObjectId, ObjectId>();
            HashSet<ObjectId> processedDefinitions = new HashSet<ObjectId>();
            int convertedArcCount = 0;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                for (int index = 0; index < updatedIds.Length; index++)
                {
                    ObjectId id = updatedIds[index];
                    if (id.IsNull || !id.IsValid || id.IsErased)
                        continue;

                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null)
                        continue;

                    ObjectId replacementId;
                    int entityArcCount;
                    if (TryConvertEntity(
                        transaction,
                        database,
                        entity,
                        out replacementId,
                        out entityArcCount))
                    {
                        replacements[id] = replacementId;
                        resultIds[index] = replacementId;
                        convertedArcCount += entityArcCount;
                        continue;
                    }

                    if (processGeneratedDetailDefinition && entity is BlockReference)
                    {
                        BlockReference reference = (BlockReference)entity;
                        ObjectId definitionId = reference.BlockTableRecord;
                        if (processedDefinitions.Add(definitionId))
                        {
                            convertedArcCount += ConvertGeneratedDetailDefinition(
                                transaction,
                                database,
                                definitionId);
                        }
                    }
                }

                transaction.Commit();
            }

            updatedIds = resultIds.ToArray();
            return convertedArcCount;
        }

        private static int ConvertGeneratedDetailDefinition(
            Transaction transaction,
            Database database,
            ObjectId definitionId)
        {
            BlockTableRecord definition = transaction.GetObject(
                definitionId, OpenMode.ForRead, false) as BlockTableRecord;
            if (definition == null ||
                !definition.Name.StartsWith(GeneratedDetailPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            List<ObjectId> contentIds = new List<ObjectId>();
            foreach (ObjectId id in definition)
                contentIds.Add(id);

            ObjectId[] orderedIds = SortByDrawOrder(transaction, definitionId, contentIds.ToArray());
            Dictionary<ObjectId, ObjectId> replacements = new Dictionary<ObjectId, ObjectId>();
            int convertedArcCount = 0;
            foreach (ObjectId id in orderedIds)
            {
                if (id.IsNull || !id.IsValid || id.IsErased)
                    continue;

                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                ObjectId replacementId;
                int entityArcCount;
                if (entity != null && TryConvertEntity(
                    transaction,
                    database,
                    entity,
                    out replacementId,
                    out entityArcCount))
                {
                    replacements[id] = replacementId;
                    convertedArcCount += entityArcCount;
                }
            }

            if (replacements.Count > 0)
            {
                ObjectId[] updatedOrder = ReplaceIds(orderedIds, replacements);
                SetRelativeDrawOrder(transaction, definitionId, updatedOrder);
            }

            return convertedArcCount;
        }

        private static bool TryConvertEntity(
            Transaction transaction,
            Database database,
            Entity entity,
            out ObjectId replacementId,
            out int convertedArcCount)
        {
            replacementId = entity.ObjectId;
            convertedArcCount = 0;
            if (entity.IsAProxy || entity.HasFields || !entity.ExtensionDictionary.IsNull)
                return false;

            Polyline polyline = entity as Polyline;
            if (polyline != null)
            {
                List<PolylineVertex> vertices;
                bool closed;
                int bulgedArcCount;
                if (!TrySegmentPolyline(polyline, out vertices, out closed, out bulgedArcCount))
                    return false;

                replacementId = ReplacePolyline(
                    transaction,
                    database,
                    polyline,
                    vertices,
                    closed,
                    polyline.Normal,
                    polyline.Elevation,
                    polyline.Thickness,
                    polyline.ConstantWidth,
                    polyline.Plinegen);
                convertedArcCount = bulgedArcCount;
                return true;
            }

            Arc arc = entity as Arc;
            if (arc != null)
            {
                List<Point3d> points = SampleArc(arc);
                replacementId = ReplaceCircularEntity(
                    transaction,
                    database,
                    arc,
                    points,
                    false,
                    arc.Normal,
                    arc.Thickness);
                convertedArcCount = 1;
                return true;
            }

            Circle circle = entity as Circle;
            if (circle != null)
            {
                List<Point3d> points = SampleCircle(circle);
                replacementId = ReplaceCircularEntity(
                    transaction,
                    database,
                    circle,
                    points,
                    true,
                    circle.Normal,
                    circle.Thickness);
                convertedArcCount = 1;
                return true;
            }

            return false;
        }

        private static bool TrySegmentPolyline(
            Polyline source,
            out List<PolylineVertex> vertices,
            out bool closed,
            out int bulgedArcCount)
        {
            vertices = new List<PolylineVertex>();
            closed = source.Closed;
            bulgedArcCount = 0;
            int vertexCount = source.NumberOfVertices;
            if (vertexCount == 0)
                return false;

            int segmentCount = closed ? vertexCount : vertexCount - 1;
            for (int index = 0; index < segmentCount; index++)
            {
                int nextIndex = (index + 1) % vertexCount;
                Point2d start = source.GetPoint2dAt(index);
                Point2d end = source.GetPoint2dAt(nextIndex);
                double startWidth = source.GetStartWidthAt(index);
                double endWidth = source.GetEndWidthAt(index);
                double bulge = source.GetBulgeAt(index);

                if (Math.Abs(bulge) <= GeometryTolerance)
                {
                    vertices.Add(new PolylineVertex(start, startWidth, endWidth));
                    continue;
                }

                Vector2d chord = end - start;
                double chordLength = chord.Length;
                double sweep = 4.0 * Math.Atan(bulge);
                double tangent = Math.Tan(sweep * 0.5);
                if (chordLength <= GeometryTolerance || Math.Abs(tangent) <= GeometryTolerance)
                    return false;

                Point2d midpoint = new Point2d(
                    (start.X + end.X) * 0.5,
                    (start.Y + end.Y) * 0.5);
                double centerOffset = chordLength / (2.0 * tangent);
                Point2d center = new Point2d(
                    midpoint.X - chord.Y / chordLength * centerOffset,
                    midpoint.Y + chord.X / chordLength * centerOffset);
                Vector2d startRadius = start - center;
                double widthStep = (endWidth - startWidth) / SegmentsPerArc;

                for (int segment = 0; segment < SegmentsPerArc; segment++)
                {
                    double angle = sweep * segment / SegmentsPerArc;
                    double cosine = Math.Cos(angle);
                    double sine = Math.Sin(angle);
                    Point2d point = new Point2d(
                        center.X + startRadius.X * cosine - startRadius.Y * sine,
                        center.Y + startRadius.X * sine + startRadius.Y * cosine);
                    double segmentStartWidth = startWidth + widthStep * segment;
                    double segmentEndWidth = startWidth + widthStep * (segment + 1);
                    vertices.Add(new PolylineVertex(point, segmentStartWidth, segmentEndWidth));
                }

                bulgedArcCount++;
            }

            if (!closed)
            {
                int last = vertexCount - 1;
                vertices.Add(new PolylineVertex(
                    source.GetPoint2dAt(last),
                    source.GetStartWidthAt(last),
                    source.GetEndWidthAt(last)));
            }

            return bulgedArcCount > 0;
        }

        private static List<Point3d> SampleArc(Arc arc)
        {
            Point3d center = arc.Center;
            Vector3d normal = arc.Normal.GetNormal();
            Vector3d startRadius = (arc.StartPoint - center).GetNormal();
            Vector3d tangent = normal.CrossProduct(startRadius).GetNormal();
            double signedSweep = arc.EndAngle - arc.StartAngle;
            if (signedSweep < 0.0)
                signedSweep += Math.PI * 2.0;
            if (signedSweep <= 0.0)
                signedSweep = Math.PI * 2.0;

            double radius = center.DistanceTo(arc.StartPoint);
            List<Point3d> points = new List<Point3d>(SegmentsPerArc + 1);
            for (int index = 0; index <= SegmentsPerArc; index++)
            {
                double angle = signedSweep * index / SegmentsPerArc;
                points.Add(center + startRadius * (radius * Math.Cos(angle)) +
                    tangent * (radius * Math.Sin(angle)));
            }
            return points;
        }

        private static List<Point3d> SampleCircle(Circle circle)
        {
            Point3d center = circle.Center;
            Vector3d normal = circle.Normal.GetNormal();
            Vector3d startRadius = normal.GetPerpendicularVector().GetNormal();
            Vector3d tangent = normal.CrossProduct(startRadius).GetNormal();
            double radius = circle.Radius;
            List<Point3d> points = new List<Point3d>(SegmentsPerArc);
            for (int index = 0; index < SegmentsPerArc; index++)
            {
                double angle = Math.PI * 2.0 * index / SegmentsPerArc;
                points.Add(center + startRadius * (radius * Math.Cos(angle)) +
                    tangent * (radius * Math.Sin(angle)));
            }
            return points;
        }

        private static ObjectId ReplaceCircularEntity(
            Transaction transaction,
            Database database,
            Entity source,
            IList<Point3d> worldPoints,
            bool closed,
            Vector3d normal,
            double thickness)
        {
            Matrix3d worldToOcs = Matrix3d.WorldToPlane(normal.GetNormal());
            List<Point2d> vertices = new List<Point2d>(worldPoints.Count);
            double elevation = 0.0;
            for (int index = 0; index < worldPoints.Count; index++)
            {
                Point3d point = worldPoints[index].TransformBy(worldToOcs);
                if (index == 0)
                    elevation = point.Z;
                vertices.Add(new Point2d(point.X, point.Y));
            }

            PolylineVertex[] data = new PolylineVertex[vertices.Count];
            for (int index = 0; index < vertices.Count; index++)
                data[index] = new PolylineVertex(vertices[index], 0.0, 0.0);

            return ReplacePolyline(
                transaction,
                database,
                source,
                data,
                closed,
                normal.GetNormal(),
                elevation,
                thickness,
                0.0,
                false);
        }

        private static ObjectId ReplacePolyline(
            Transaction transaction,
            Database database,
            Entity source,
            IList<PolylineVertex> vertices,
            bool closed,
            Vector3d normal,
            double elevation,
            double thickness,
            double constantWidth,
            bool plinegen)
        {
            BlockTableRecord owner = transaction.GetObject(
                source.OwnerId, OpenMode.ForRead, false) as BlockTableRecord;
            if (owner == null)
                throw new InvalidOperationException("Não foi possível acessar o espaço proprietário da curva.");
            if (!owner.IsWriteEnabled)
                owner.UpgradeOpen();

            Polyline replacement = new Polyline(vertices.Count);
            replacement.SetDatabaseDefaults(database);
            replacement.SetPropertiesFrom(source);
            replacement.Normal = normal.GetNormal();
            replacement.Elevation = elevation;
            replacement.Thickness = thickness;
            replacement.ConstantWidth = constantWidth;
            replacement.Plinegen = plinegen;
            for (int index = 0; index < vertices.Count; index++)
            {
                PolylineVertex vertex = vertices[index];
                replacement.AddVertexAt(index, vertex.Point, 0.0, vertex.StartWidth, vertex.EndWidth);
            }
            replacement.Closed = closed;

            owner.AppendEntity(replacement);
            transaction.AddNewlyCreatedDBObject(replacement, true);
            CopyXData(source, replacement);

            source.UpgradeOpen();
            source.Erase();
            return replacement.ObjectId;
        }

        private static void CopyXData(Entity source, Entity destination)
        {
            using (ResultBuffer sourceXData = source.XData)
            {
                if (sourceXData == null)
                    return;

                TypedValue[] values = sourceXData.AsArray();
                if (values.Length > 0)
                    destination.XData = new ResultBuffer(values);
            }
        }

        private static ObjectId[] SortByDrawOrder(
            Transaction transaction,
            ObjectId blockRecordId,
            ObjectId[] ids)
        {
            if (ids == null || ids.Length < 2)
                return ids ?? Array.Empty<ObjectId>();

            Dictionary<ObjectId, int> inputIndex = new Dictionary<ObjectId, int>();
            Dictionary<ObjectId, int> naturalOrder = new Dictionary<ObjectId, int>();
            Dictionary<ObjectId, long> sortKeys = new Dictionary<ObjectId, long>();
            for (int index = 0; index < ids.Length; index++)
                inputIndex[ids[index]] = index;

            BlockTableRecord blockRecord = transaction.GetObject(
                blockRecordId, OpenMode.ForRead, false) as BlockTableRecord;
            if (blockRecord == null)
                return ids;

            int ordinal = 0;
            foreach (ObjectId id in blockRecord)
            {
                naturalOrder[id] = ordinal++;
            }

            if (!blockRecord.DrawOrderTableId.IsNull)
            {
                try
                {
                    DrawOrderTable drawOrder = transaction.GetObject(
                        blockRecord.DrawOrderTableId, OpenMode.ForRead, false) as DrawOrderTable;
                    if (drawOrder != null)
                    {
                        foreach (ObjectId id in ids)
                            sortKeys[id] = drawOrder.GetSortHandle(id).Value;
                    }
                }
                catch
                {
                    sortKeys.Clear();
                }
            }

            List<ObjectId> ordered = new List<ObjectId>(ids);
            ordered.Sort((left, right) =>
            {
                if (sortKeys.Count == ids.Length)
                {
                    int sortComparison = sortKeys[left].CompareTo(sortKeys[right]);
                    if (sortComparison != 0)
                        return sortComparison;
                }

                int leftOrdinal;
                int rightOrdinal;
                bool hasLeft = naturalOrder.TryGetValue(left, out leftOrdinal);
                bool hasRight = naturalOrder.TryGetValue(right, out rightOrdinal);
                if (hasLeft && hasRight && leftOrdinal != rightOrdinal)
                    return leftOrdinal.CompareTo(rightOrdinal);
                return inputIndex[left].CompareTo(inputIndex[right]);
            });
            return ordered.ToArray();
        }

        private static void SetRelativeDrawOrder(
            Transaction transaction,
            ObjectId blockRecordId,
            ObjectId[] orderedIds)
        {
            if (orderedIds == null || orderedIds.Length < 2)
                return;

            BlockTableRecord blockRecord = transaction.GetObject(
                blockRecordId, OpenMode.ForRead, false) as BlockTableRecord;
            if (blockRecord == null || blockRecord.DrawOrderTableId.IsNull)
                return;

            DrawOrderTable drawOrder = transaction.GetObject(
                blockRecord.DrawOrderTableId, OpenMode.ForRead, false) as DrawOrderTable;
            if (drawOrder == null)
                return;
            if (!drawOrder.IsWriteEnabled)
                drawOrder.UpgradeOpen();

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
                drawOrder.SetRelativeDrawOrder(collection);
        }

        private static ObjectId[] ReplaceIds(
            ObjectId[] ids,
            IDictionary<ObjectId, ObjectId> replacements)
        {
            ObjectId[] result = new ObjectId[ids.Length];
            for (int index = 0; index < ids.Length; index++)
            {
                ObjectId replacement;
                result[index] = replacements.TryGetValue(ids[index], out replacement)
                    ? replacement
                    : ids[index];
            }
            return result;
        }

        private struct PolylineVertex
        {
            internal readonly Point2d Point;
            internal readonly double StartWidth;
            internal readonly double EndWidth;

            internal PolylineVertex(Point2d point, double startWidth, double endWidth)
            {
                Point = point;
                StartWidth = startWidth;
                EndWidth = endWidth;
            }
        }
    }
}
