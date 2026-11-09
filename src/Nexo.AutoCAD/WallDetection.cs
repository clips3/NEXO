using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;

namespace Nexo.AutoCAD
{
    internal enum OpeningKind { None, Door, Window }
    internal sealed class WallDetectionOptions
    {
        internal string Layer = "PP_ARQUITETURA";
        internal double MinThickness = 0.06;
        internal double MaxThickness = 0.40;
        internal double MinLength = 0.30;
        internal double Height = 2.80;
    }

    internal sealed class DetectedWall
    {
        internal Point2d A;
        internal Point2d B;
        internal Point2d C;
        internal Point2d D;
        internal double Length;
        internal double Thickness;
        internal bool FromHatch;
        internal bool Included = true;
        internal bool OpeningCandidate;
        internal OpeningKind Opening;
        internal double OpeningWidth, OpeningHeight, OpeningSill;
    }

    internal sealed class WallDetectionResult
    {
        internal readonly List<DetectedWall> Walls = new List<DetectedWall>();
        internal readonly List<Tuple<Point2d, Point2d>> Context = new List<Tuple<Point2d, Point2d>>();
        internal int IgnoredCurves;
        internal int IgnoredClippedReferences;
        internal int LimitWarnings;
        internal int Hatches;
        internal int Lines;
        internal int PossibleOpenings;
    }

    internal sealed class WallLayout
    {
        internal readonly List<DetectedWall> Walls = new List<DetectedWall>();
        internal readonly List<DetectedWall> Openings = new List<DetectedWall>();
        internal double MinX, MinY, MaxX, MaxY;
        internal double Height;
    }

    internal static class WallDetection
    {
        private const int MaxDepth = 20;
        private const int MaxSegments = 6000;
        private const double AngleTolerance = 0.035;

        private sealed class Segment
        {
            internal Point2d A, B;
            internal int Axis;
            internal double Offset, Start, End;
            internal int HatchId;
            internal double MinX, MinY, MaxX, MaxY;
        }

        private sealed class HatchRegion
        {
            internal readonly List<List<Point2d>> Loops = new List<List<Point2d>>();
            internal bool Valid = true;
        }

        internal static WallDetectionResult Detect(
            Database database, ObjectId[] selectedIds, Matrix3d ucs,
            double minX, double minY, double maxX, double maxY, WallDetectionOptions options)
        {
            if (database == null || selectedIds == null || options == null)
                throw new ArgumentNullException();
            WallDetectionResult result = new WallDetectionResult();
            List<Segment> segments = new List<Segment>();
            Dictionary<int, HatchRegion> regions = new Dictionary<int, HatchRegion>();
            Matrix3d worldToUcs = ucs.Inverse();
            int nextHatchId = 1;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                HashSet<ObjectId> path = new HashSet<ObjectId>();
                foreach (ObjectId id in selectedIds)
                {
                    if (segments.Count >= MaxSegments) break;
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity != null)
                        ReadEntity(entity, Matrix3d.Identity, null, 0, transaction, path, worldToUcs,
                            minX, minY, maxX, maxY, options, segments, regions, result, ref nextHatchId);
                }
                transaction.Commit();
            }

            if (segments.Count >= MaxSegments)
                result.LimitWarnings++;
            FindWalls(segments, regions, options, result, minX, minY, maxX, maxY);
            return result;
        }

        private static void ReadEntity(
            Entity entity, Matrix3d transform, string inheritedLayer, int depth,
            Transaction transaction, HashSet<ObjectId> path, Matrix3d worldToUcs,
            double minX, double minY, double maxX, double maxY, WallDetectionOptions options,
            List<Segment> segments, Dictionary<int, HatchRegion> regions,
            WallDetectionResult result, ref int nextHatchId)
        {
            if (segments.Count >= MaxSegments || depth > MaxDepth || !entity.Visible)
                return;

            string layer = entity.Layer;
            if (string.Equals(layer, "0", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(inheritedLayer))
                layer = inheritedLayer;

            if (entity is BlockReference reference)
            {
                if (!MayIntersect(reference, transform, worldToUcs, minX, minY, maxX, maxY, options.MaxThickness))
                    return;
                if (reference.IsDynamicBlock && reference.BlockTableRecord.IsNull)
                    return;
                ClipState clipState = GetRectangularClip(reference, transform, worldToUcs, transaction,
                    out double clipMinX, out double clipMinY, out double clipMaxX, out double clipMaxY);
                if (clipState == ClipState.Unsupported)
                {
                    result.IgnoredClippedReferences++;
                    return;
                }
                if (clipState == ClipState.Rectangle)
                {
                    minX = Math.Max(minX, clipMinX); minY = Math.Max(minY, clipMinY);
                    maxX = Math.Min(maxX, clipMaxX); maxY = Math.Min(maxY, clipMaxY);
                    if (maxX <= minX || maxY <= minY) return;
                }
                ObjectId definitionId = reference.BlockTableRecord;
                if (definitionId.IsNull || !path.Add(definitionId))
                    return;
                try
                {
                    BlockTableRecord definition = transaction.GetObject(definitionId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (definition == null)
                        return;
                    Matrix3d childTransform = transform * reference.BlockTransform;
                    foreach (ObjectId childId in definition)
                    {
                        if (segments.Count >= MaxSegments) break;
                        Entity child = transaction.GetObject(childId, OpenMode.ForRead, false) as Entity;
                        if (child != null)
                            ReadEntity(child, childTransform, layer, depth + 1, transaction, path, worldToUcs,
                                minX, minY, maxX, maxY, options, segments, regions, result, ref nextHatchId);
                    }
                }
                finally { path.Remove(definitionId); }
                return;
            }

            if (!MayIntersect(entity, transform, worldToUcs, minX, minY, maxX, maxY, options.MaxThickness))
                return;

            if (!string.Equals(layer, options.Layer, StringComparison.OrdinalIgnoreCase)) return;

            if (entity is Line line)
            {
                result.Lines++;
                AddSegment(line.StartPoint, line.EndPoint, transform, worldToUcs,
                    minX, minY, maxX, maxY, options, segments, 0, result);
            }
            else if (entity is Polyline polyline)
            {
                int count = polyline.NumberOfVertices;
                int segmentCount = polyline.Closed ? count : count - 1;
                for (int i = 0; i < segmentCount; i++)
                {
                    if (Math.Abs(polyline.GetBulgeAt(i)) > 1e-9)
                    {
                        result.IgnoredCurves++;
                        continue;
                    }
                    AddSegment(polyline.GetPoint3dAt(i), polyline.GetPoint3dAt((i + 1) % count),
                        transform, worldToUcs, minX, minY, maxX, maxY, options, segments, 0, result);
                }
            }
            else if (entity is Hatch hatch)
            {
                result.Hatches++;
                int hatchId = nextHatchId++;
                HatchRegion region = new HatchRegion();
                regions[hatchId] = region;
                Matrix3d planeToWorld = Matrix3d.PlaneToWorld(hatch.Normal);
                try
                {
                    for (int i = 0; i < hatch.NumberOfLoops; i++)
                    {
                        HatchLoop loop = hatch.GetLoopAt(i);
                        List<Point2d> polygon = new List<Point2d>();
                        List<Tuple<Point3d, Point3d>> edges = new List<Tuple<Point3d, Point3d>>();
                        if (loop.IsPolyline)
                        {
                            BulgeVertexCollection vertices = loop.Polyline;
                            for (int j = 0; j < vertices.Count; j++)
                            {
                                BulgeVertex v = vertices[j];
                                BulgeVertex next = vertices[(j + 1) % vertices.Count];
                                if (Math.Abs(v.Bulge) > 1e-9) { region.Valid = false; break; }
                                Point3d a = new Point3d(v.Vertex.X, v.Vertex.Y, hatch.Elevation).TransformBy(planeToWorld);
                                Point3d b = new Point3d(next.Vertex.X, next.Vertex.Y, hatch.Elevation).TransformBy(planeToWorld);
                                edges.Add(Tuple.Create(a, b));
                            }
                        }
                        else
                        {
                            foreach (Curve2d curve in loop.Curves)
                            {
                                if (!(curve is LineSegment2d lineSegment)) { region.Valid = false; break; }
                                Point3d a = new Point3d(lineSegment.StartPoint.X, lineSegment.StartPoint.Y, hatch.Elevation).TransformBy(planeToWorld);
                                Point3d b = new Point3d(lineSegment.EndPoint.X, lineSegment.EndPoint.Y, hatch.Elevation).TransformBy(planeToWorld);
                                edges.Add(Tuple.Create(a, b));
                            }
                        }
                        if (!region.Valid || edges.Count < 3) { region.Valid = false; break; }
                        foreach (Tuple<Point3d, Point3d> edge in edges)
                        {
                            Point3d point = edge.Item1.TransformBy(transform).TransformBy(worldToUcs);
                            polygon.Add(new Point2d(point.X, point.Y));
                            AddSegment(edge.Item1, edge.Item2, transform, worldToUcs,
                                minX, minY, maxX, maxY, options, segments, hatchId, result);
                        }
                        region.Loops.Add(polygon);
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception) { region.Valid = false; }
                if (!region.Valid) result.IgnoredCurves++;
            }
        }

        private enum ClipState { None, Rectangle, Unsupported }

        private static ClipState GetRectangularClip(BlockReference reference, Matrix3d parentTransform,
            Matrix3d worldToUcs, Transaction transaction,
            out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = maxX = maxY = 0;
            if (reference.ExtensionDictionary.IsNull)
                return ClipState.None;
            DBDictionary dictionary = transaction.GetObject(reference.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains("ACAD_FILTER"))
                return ClipState.None;
            DBDictionary filters = transaction.GetObject(dictionary.GetAt("ACAD_FILTER"), OpenMode.ForRead, false) as DBDictionary;
            if (filters == null || !filters.Contains("SPATIAL")) return ClipState.None;
            try
            {
                SpatialFilter filter = transaction.GetObject(filters.GetAt("SPATIAL"), OpenMode.ForRead, false) as SpatialFilter;
                if (filter == null || !filter.Definition.Enabled) return ClipState.None;
                if (filter.Inverted) return ClipState.Unsupported;
                Point2dCollection points = filter.Definition.GetPoints();
                if (points.Count != 2 && points.Count != 4) return ClipState.Unsupported;
                List<Point2d> vertices = new List<Point2d>();
                if (points.Count == 2)
                {
                    vertices.Add(points[0]);
                    vertices.Add(new Point2d(points[1].X, points[0].Y));
                    vertices.Add(points[1]);
                    vertices.Add(new Point2d(points[0].X, points[1].Y));
                }
                else foreach (Point2d point in points) vertices.Add(point);
                List<Point3d> transformed = new List<Point3d>();
                minX = minY = double.PositiveInfinity;
                maxX = maxY = double.NegativeInfinity;
                foreach (Point2d point in vertices)
                {
                    Point3d p = new Point3d(point.X, point.Y, 0.0)
                        .TransformBy(filter.ClipSpaceToWorldCoordinateSystemTransform)
                        .TransformBy(filter.OriginalInverseBlockTransform)
                        .TransformBy(reference.BlockTransform)
                        .TransformBy(parentTransform).TransformBy(worldToUcs);
                    transformed.Add(p);
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                }
                double tolerance = Math.Max(1e-7, Math.Max(maxX - minX, maxY - minY) * 1e-7);
                foreach (Point3d point in transformed)
                {
                    bool cornerX = Math.Abs(point.X - minX) <= tolerance || Math.Abs(point.X - maxX) <= tolerance;
                    bool cornerY = Math.Abs(point.Y - minY) <= tolerance || Math.Abs(point.Y - maxY) <= tolerance;
                    if (!cornerX || !cornerY) return ClipState.Unsupported;
                }
                return maxX - minX > tolerance && maxY - minY > tolerance ? ClipState.Rectangle : ClipState.Unsupported;
            }
            catch { return ClipState.Unsupported; }
        }

        private static bool MayIntersect(Entity entity, Matrix3d transform, Matrix3d worldToUcs,
            double minX, double minY, double maxX, double maxY, double margin)
        {
            try
            {
                Extents3d ext = entity.GeometricExtents;
                double lowX = double.PositiveInfinity, lowY = double.PositiveInfinity;
                double highX = double.NegativeInfinity, highY = double.NegativeInfinity;
                for (int mask = 0; mask < 8; mask++)
                {
                    Point3d point = new Point3d((mask & 1) == 0 ? ext.MinPoint.X : ext.MaxPoint.X,
                        (mask & 2) == 0 ? ext.MinPoint.Y : ext.MaxPoint.Y,
                        (mask & 4) == 0 ? ext.MinPoint.Z : ext.MaxPoint.Z).TransformBy(transform).TransformBy(worldToUcs);
                    lowX = Math.Min(lowX, point.X); lowY = Math.Min(lowY, point.Y);
                    highX = Math.Max(highX, point.X); highY = Math.Max(highY, point.Y);
                }
                return highX >= minX - margin && lowX <= maxX + margin && highY >= minY - margin && lowY <= maxY + margin;
            }
            catch { return true; }
        }

        private static void AddSegment(Point3d start, Point3d end, Matrix3d transform, Matrix3d worldToUcs,
            double minX, double minY, double maxX, double maxY, WallDetectionOptions options,
            List<Segment> segments, int hatchId, WallDetectionResult result)
        {
            if (segments.Count >= MaxSegments) return;
            Point3d a = start.TransformBy(transform).TransformBy(worldToUcs);
            Point3d b = end.TransformBy(transform).TransformBy(worldToUcs);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            // Keep very short, axis-aligned edges in the candidate pool. A short
            // wall return can be valid when it meets a longer recognized wall;
            // connectivity is checked after the full-length candidates exist.
            double minimumEdgeLength = Math.Max(0.05, Math.Min(options.MinLength, options.MinThickness * 0.5));
            if (length < minimumEdgeLength) return;
            int axis;
            if (Math.Abs(dy) <= length * AngleTolerance) axis = 0;
            else if (Math.Abs(dx) <= length * AngleTolerance) axis = 1;
            else { result.IgnoredCurves++; return; }

            double offset = axis == 0 ? (a.Y + b.Y) * 0.5 : (a.X + b.X) * 0.5;
            double lo = axis == 0 ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y);
            double hi = axis == 0 ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y);
            double clipLo = axis == 0 ? minX : minY;
            double clipHi = axis == 0 ? maxX : maxY;
            double offsetLo = axis == 0 ? minY : minX;
            double offsetHi = axis == 0 ? maxY : maxX;
            if (offset < offsetLo - options.MaxThickness || offset > offsetHi + options.MaxThickness) return;
            lo = Math.Max(lo, clipLo); hi = Math.Min(hi, clipHi);
            if (hi - lo < minimumEdgeLength) return;
            segments.Add(new Segment { Axis = axis, Offset = offset, Start = lo, End = hi, HatchId = hatchId,
                MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
                A = new Point2d(a.X, a.Y), B = new Point2d(b.X, b.Y) });
            if (offset >= offsetLo && offset <= offsetHi)
                result.Context.Add(axis == 0
                    ? Tuple.Create(new Point2d(lo, offset), new Point2d(hi, offset))
                    : Tuple.Create(new Point2d(offset, lo), new Point2d(offset, hi)));
        }

        private static void FindWalls(List<Segment> segments, Dictionary<int, HatchRegion> regions,
            WallDetectionOptions options, WallDetectionResult result,
            double minX, double minY, double maxX, double maxY)
        {
            segments.Sort((a, b) => { int axis = a.Axis.CompareTo(b.Axis); return axis != 0 ? axis : a.Offset.CompareTo(b.Offset); });
            List<DetectedWall> candidates = new List<DetectedWall>();
            for (int i = 0; i < segments.Count; i++)
            {
                Segment first = segments[i];
                for (int j = i + 1; j < segments.Count; j++)
                {
                    Segment second = segments[j];
                    if (second.Axis != first.Axis || second.Offset - first.Offset > options.MaxThickness) break;
                    double thickness = second.Offset - first.Offset;
                    if (thickness < options.MinThickness) continue;
                    // A wall can be bounded by one hatch edge and one nearby
                    // architectural line (often the separate finish/facing line).
                    // Only reject pairs belonging to two different hatch regions.
                    if (first.HatchId != 0 && second.HatchId != 0 && first.HatchId != second.HatchId) continue;
                    double lo = Math.Max(first.Start, second.Start);
                    double hi = Math.Min(first.End, second.End);
                    double length = hi - lo;
                    bool fromHatch = first.HatchId != 0 && first.HatchId == second.HatchId;
                    double minimumCandidateLength = Math.Max(0.05, Math.Min(options.MinLength, thickness * 0.5));
                    if (length < minimumCandidateLength || (fromHatch && length < options.MinLength)) continue;
                    double firstClipMin = first.Axis == 0 ? first.MinY : first.MinX;
                    double secondClipMin = first.Axis == 0 ? second.MinY : second.MinX;
                    double firstClipMax = first.Axis == 0 ? first.MaxY : first.MaxX;
                    double secondClipMax = first.Axis == 0 ? second.MaxY : second.MaxX;
                    double lowOffset = Math.Max(first.Offset, Math.Max(firstClipMin, secondClipMin));
                    double highOffset = Math.Min(second.Offset, Math.Min(firstClipMax, secondClipMax));
                    if (highOffset - lowOffset <= 1e-8) continue;
                    List<Tuple<double, double>> intervals;
                    if (fromHatch)
                    {
                        if (!regions.TryGetValue(first.HatchId, out HatchRegion region) || !region.Valid) continue;
                        intervals = HatchIntervals(region, first.Axis, (lowOffset + highOffset) * 0.5, lo, hi);
                    }
                    else intervals = new List<Tuple<double, double>> { Tuple.Create(lo, hi) };

                    foreach (Tuple<double, double> interval in intervals)
                    {
                        double intervalLo = interval.Item1, intervalHi = interval.Item2;
                        double intervalLength = intervalHi - intervalLo;
                        if (intervalLength < minimumCandidateLength || (fromHatch && intervalLength < options.MinLength)) continue;
                        Point2d a = first.Axis == 0 ? new Point2d(intervalLo, lowOffset) : new Point2d(lowOffset, intervalLo);
                        Point2d b = first.Axis == 0 ? new Point2d(intervalHi, lowOffset) : new Point2d(lowOffset, intervalHi);
                        Point2d c = first.Axis == 0 ? new Point2d(intervalHi, highOffset) : new Point2d(highOffset, intervalHi);
                        Point2d d = first.Axis == 0 ? new Point2d(intervalLo, highOffset) : new Point2d(highOffset, intervalLo);
                        candidates.Add(new DetectedWall { A = a, B = b, C = c, D = d,
                            Length = intervalHi - intervalLo, Thickness = highOffset - lowOffset, FromHatch = fromHatch });
                        if (candidates.Count >= 20000) { result.LimitWarnings++; break; }
                    }
                    if (candidates.Count >= 20000) break;
                }
                if (candidates.Count >= 20000) break;
            }

            // Prefer hatches and longer contiguous segments over overlapping duplicates.
            candidates.Sort((a, b) => { int evidence = b.FromHatch.CompareTo(a.FromHatch); return evidence != 0 ? evidence : b.Length.CompareTo(a.Length); });
            foreach (DetectedWall candidate in candidates)
            {
                bool horizontal = Math.Abs(candidate.B.X - candidate.A.X) > Math.Abs(candidate.B.Y - candidate.A.Y);
                double start = horizontal ? candidate.A.X : candidate.A.Y;
                double end = horizontal ? candidate.B.X : candidate.B.Y;
                bool shortConnectedReturn = candidate.Length < options.MinLength &&
                    IsConnectedShortReturn(candidate, result.Walls, options.MaxThickness);
                if (candidate.Length < options.MinLength && !shortConnectedReturn)
                    continue;
                List<Tuple<double, double>> remaining = new List<Tuple<double, double>> { Tuple.Create(start, end) };
                foreach (DetectedWall existing in result.Walls)
                {
                    if (!SameWallStrip(candidate, existing)) continue;
                    double existingStart = horizontal ? existing.A.X : existing.A.Y;
                    double existingEnd = horizontal ? existing.B.X : existing.B.Y;
                    List<Tuple<double, double>> next = new List<Tuple<double, double>>();
                    foreach (Tuple<double, double> interval in remaining)
                    {
                        if (existingEnd <= interval.Item1 || existingStart >= interval.Item2) { next.Add(interval); continue; }
                        if (existingStart > interval.Item1) next.Add(Tuple.Create(interval.Item1, Math.Min(existingStart, interval.Item2)));
                        if (existingEnd < interval.Item2) next.Add(Tuple.Create(Math.Max(existingEnd, interval.Item1), interval.Item2));
                    }
                    remaining = next;
                    if (remaining.Count == 0) break;
                }
                foreach (Tuple<double, double> interval in remaining)
                {
                    double intervalLength = interval.Item2 - interval.Item1;
                    // Subtracting nearly coincident wall edges can leave numerical
                    // slivers even when the original short return was valid.
                    // Apply the same minimum used to admit a wall candidate to
                    // each resulting fragment, and check its own connectivity.
                    double minimumFragmentLength = Math.Max(0.05, Math.Min(options.MinLength, candidate.Thickness * 0.5));
                    if (intervalLength < minimumFragmentLength) continue;
                    DetectedWall fragment = WithInterval(candidate, interval.Item1, interval.Item2, horizontal);
                    if (intervalLength < options.MinLength &&
                        (!shortConnectedReturn || !IsConnectedShortReturn(fragment, result.Walls, options.MaxThickness)))
                        continue;
                    result.Walls.Add(fragment);
                    if (result.Walls.Count >= 1000) { result.LimitWarnings++; break; }
                }
                if (result.Walls.Count >= 1000) break;
            }

            // A line-pair adjacent to a hatched wall can be a window, door,
            // sill or another unhatched continuation. Leave such ambiguous
            // spans available for review but do not generate them by default.
            foreach (DetectedWall candidate in result.Walls)
            {
                if (candidate.FromHatch) continue;
                bool horizontal = Math.Abs(candidate.B.X - candidate.A.X) > Math.Abs(candidate.B.Y - candidate.A.Y);
                double start = horizontal ? candidate.A.X : candidate.A.Y;
                double end = horizontal ? candidate.B.X : candidate.B.Y;
                double tolerance = Math.Max(1e-6, options.MaxThickness * 0.3);
                bool before = false, after = false;
                foreach (DetectedWall hatch in result.Walls)
                {
                    if (!hatch.FromHatch || !SameWallStrip(candidate, hatch)) continue;
                    double hatchStart = horizontal ? hatch.A.X : hatch.A.Y;
                    double hatchEnd = horizontal ? hatch.B.X : hatch.B.Y;
                    if (Math.Abs(hatchEnd - start) <= tolerance) before = true;
                    if (Math.Abs(hatchStart - end) <= tolerance) after = true;
                    if (before && after) break;
                }
                if (before || after) { candidate.Included = false; candidate.OpeningCandidate = true; result.PossibleOpenings++; }
            }

            // Some architectural sets draw only one thin finish/face line beside
            // a hatched wall. It cannot form a valid thickness pair by itself,
            // so absorb it as boundary evidence when it follows the wall closely.
            AbsorbNearbyBoundaryLines(segments, result.Walls, options.MaxThickness);

            // A second parallel band very close to a wall is commonly its
            // facing/cladding. Treat the two detected strips as one wall run.
            MergeParallelWallBands(result.Walls, options.MaxThickness);

            // Small gaps between perpendicular wall runs are usually caused by
            // separate hatch boundaries at a corner. Extend the run to the
            // neighboring wall's outer face so the preview and generated solids
            // form a continuous, filled junction. Larger gaps (doors/openings)
            // remain untouched.
            JoinNearbyOrthogonalWalls(result.Walls, options.MaxThickness);
            FindUnmarkedOpeningGaps(result);
            ClassifyOpeningCandidates(result, options.Height);
        }

        private static void FindUnmarkedOpeningGaps(WallDetectionResult result)
        {
            // A missing interval between aligned wall runs can be a door/window,
            // regardless of whether either run came from a hatch or line pair.
            var additions = new List<DetectedWall>();
            foreach (DetectedWall first in result.Walls)
            {
                if (!first.Included || first.OpeningCandidate || first.Opening != OpeningKind.None) continue;
                bool horizontal = IsHorizontal(first);
                Bounds(first, out _, out _, out double firstMaxX, out double firstMaxY);
                double end = horizontal ? firstMaxX : firstMaxY;
                DetectedWall nearest = null; double nearestGap = double.PositiveInfinity;
                foreach (DetectedWall second in result.Walls)
                {
                    if (second == first || !second.Included || second.OpeningCandidate ||
                        second.Opening != OpeningKind.None || !SameOpeningBand(first, second)) continue;
                    Bounds(second, out double secondMinX, out double secondMinY, out _, out _);
                    double start = horizontal ? secondMinX : secondMinY;
                    double gap = start - end;
                    if (gap >= 0.30 && gap <= 3.5 && gap < nearestGap) { nearest = second; nearestGap = gap; }
                }
                if (nearest == null) continue;
                Bounds(nearest, out double nearestMinX, out double nearestMinY, out _, out _);
                double stop = horizontal ? nearestMinX : nearestMinY;
                bool covered = false;
                foreach (DetectedWall other in result.Walls)
                {
                    if (other == first || other == nearest || !other.Included || other.OpeningCandidate ||
                        other.Opening != OpeningKind.None || !SameOpeningBand(first, other)) continue;
                    Bounds(other, out double otherMinX, out double otherMinY, out double otherMaxX, out double otherMaxY);
                    double a = horizontal ? otherMinX : otherMinY, b = horizontal ? otherMaxX : otherMaxY;
                    if (Math.Min(stop,b) - Math.Max(end,a) > 1e-6) { covered = true; break; }
                }
                if (covered) continue;
                DetectedWall gapWall = WithInterval(first, end, stop, horizontal);
                gapWall.FromHatch = false; gapWall.Included = false; gapWall.OpeningCandidate = true;
                bool duplicate = false;
                foreach (DetectedWall existing in additions)
                {
                    if (!SameOpeningBand(gapWall, existing)) continue;
                    Bounds(existing, out double existingMinX, out double existingMinY, out double existingMaxX, out double existingMaxY);
                    double startA = end;
                    double startB = horizontal ? existingMinX : existingMinY;
                    double endA = stop;
                    double endB = horizontal ? existingMaxX : existingMaxY;
                    double tolerance = Math.Max(0.08, Math.Min(0.20, gapWall.Thickness * 0.5));
                    if (Math.Abs(startA - startB) <= tolerance && Math.Abs(endA - endB) <= tolerance)
                    { duplicate = true; break; }
                }
                if (!duplicate) additions.Add(gapWall);
            }
            result.Walls.AddRange(additions);
            result.PossibleOpenings += additions.Count;
        }

        private static bool SameOpeningBand(DetectedWall a, DetectedWall b)
        {
            bool horizontal = IsHorizontal(a);
            if (horizontal != IsHorizontal(b)) return false;
            Bounds(a, out double ax0, out double ay0, out double ax1, out double ay1);
            Bounds(b, out double bx0, out double by0, out double bx1, out double by1);
            double aLow = horizontal ? ay0 : ax0, aHigh = horizontal ? ay1 : ax1;
            double bLow = horizontal ? by0 : bx0, bHigh = horizontal ? by1 : bx1;
            double aThickness = aHigh - aLow, bThickness = bHigh - bLow;
            double overlap = Math.Min(aHigh, bHigh) - Math.Max(aLow, bLow);
            double minThickness = Math.Min(aThickness, bThickness);
            double maxThickness = Math.Max(aThickness, bThickness);
            double centerDistance = Math.Abs((aLow + aHigh - bLow - bHigh) * 0.5);
            return minThickness > 1e-8 && overlap >= minThickness * 0.35 &&
                centerDistance <= Math.Max(0.08, maxThickness * 0.45) &&
                Math.Abs(aThickness - bThickness) <= Math.Max(0.08, maxThickness * 0.50);
        }

        private static void ClassifyOpeningCandidates(WallDetectionResult result, double wallHeight)
        {
            foreach (DetectedWall opening in result.Walls)
            {
                if (!opening.OpeningCandidate || opening.Opening != OpeningKind.None) continue;
                Bounds(opening, out double minX, out double minY, out double maxX, out double maxY);
                double width = IsHorizontal(opening) ? maxX - minX : maxY - minY;
                if (width <= 0) continue;

                // O tipo da abertura é determinado somente pela largura do vão,
                // em unidades do desenho: abaixo de 1 m é porta; 1 m ou mais é janela.
                OpeningKind kind = width < 1.0 ? OpeningKind.Door : OpeningKind.Window;

                double height = kind == OpeningKind.Door ? Math.Min(2.1, wallHeight) : Math.Min(1.2, wallHeight);
                double sill = kind == OpeningKind.Door ? 0 : Math.Min(1.0, Math.Max(0, wallHeight - height));
                opening.Opening = kind;
                opening.OpeningWidth = Math.Min(width, opening.Length);
                opening.OpeningHeight = height;
                opening.OpeningSill = sill;
                opening.Included = true;
            }
        }

        private static void AbsorbNearbyBoundaryLines(List<Segment> segments, List<DetectedWall> walls, double maxThickness)
        {
            foreach (DetectedWall wall in walls)
            {
                if (!wall.Included) continue;
                bool horizontal = IsHorizontal(wall);
                Bounds(wall, out double x0, out double y0, out double x1, out double y1);
                double start = horizontal ? x0 : y0, end = horizontal ? x1 : y1;
                double low = horizontal ? y0 : x0, high = horizontal ? y1 : x1;
                double absorbDistance = Math.Min(maxThickness, Math.Max(0.08, wall.Thickness * 0.75));
                foreach (Segment segment in segments)
                {
                    if (segment.HatchId != 0 || segment.Axis != (horizontal ? 0 : 1)) continue;
                    double overlap = Math.Min(end, segment.End) - Math.Max(start, segment.Start);
                    double shorter = Math.Min(end - start, segment.End - segment.Start);
                    if (shorter <= 1e-8 || overlap / shorter < 0.70) continue;
                    double distanceToLow = Math.Abs(segment.Offset - low);
                    double distanceToHigh = Math.Abs(segment.Offset - high);
                    if (distanceToLow > absorbDistance && distanceToHigh > absorbDistance) continue;
                    double expandedLow = Math.Min(low, segment.Offset);
                    double expandedHigh = Math.Max(high, segment.Offset);
                    if (expandedHigh - expandedLow > maxThickness + 1e-8) continue;
                    // A nearby finish line can refine thickness only over the
                    // already recognized interval. Extending to its endpoints
                    // turns long window/sill lines into imaginary wall runs.
                    low = expandedLow;
                    high = expandedHigh;
                }
                if (horizontal)
                {
                    wall.A = new Point2d(start, low); wall.B = new Point2d(end, low);
                    wall.C = new Point2d(end, high); wall.D = new Point2d(start, high);
                }
                else
                {
                    wall.A = new Point2d(low, start); wall.B = new Point2d(low, end);
                    wall.C = new Point2d(high, end); wall.D = new Point2d(high, start);
                }
                wall.Length = end - start;
                wall.Thickness = high - low;
            }
        }

        private static void MergeParallelWallBands(List<DetectedWall> walls, double maxThickness)
        {
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < walls.Count && !changed; i++)
                {
                    DetectedWall a = walls[i];
                    if (!a.Included) continue;
                    bool horizontal = IsHorizontal(a);
                    Bounds(a, out double ax0, out double ay0, out double ax1, out double ay1);
                    double aStart = horizontal ? ax0 : ay0;
                    double aEnd = horizontal ? ax1 : ay1;
                    double aLow = horizontal ? ay0 : ax0;
                    double aHigh = horizontal ? ay1 : ax1;
                    for (int j = i + 1; j < walls.Count; j++)
                    {
                        DetectedWall b = walls[j];
                        if (!b.Included || IsHorizontal(b) != horizontal) continue;
                        Bounds(b, out double bx0, out double by0, out double bx1, out double by1);
                        double bStart = horizontal ? bx0 : by0;
                        double bEnd = horizontal ? bx1 : by1;
                        double bLow = horizontal ? by0 : bx0;
                        double bHigh = horizontal ? by1 : bx1;
                        double overlap = Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart);
                        double shorter = Math.Min(aEnd - aStart, bEnd - bStart);
                        double gap = Math.Max(0, Math.Max(bLow - aHigh, aLow - bHigh));
                        double combinedThickness = Math.Max(aHigh, bHigh) - Math.Min(aLow, bLow);
                        // A bounding rectangle is a valid union only when the
                        // two strips share their ends or their side boundaries.
                        // Otherwise it fills areas for which no wall was found.
                        const double mergeTolerance = 1e-6;
                        bool sameEnds = Math.Abs(aStart - bStart) <= mergeTolerance &&
                            Math.Abs(aEnd - bEnd) <= mergeTolerance;
                        bool sameBand = Math.Abs(aLow - bLow) <= mergeTolerance &&
                            Math.Abs(aHigh - bHigh) <= mergeTolerance;
                        if (shorter <= 1e-8 || combinedThickness > maxThickness + mergeTolerance ||
                            !(sameEnds && gap <= mergeTolerance || sameBand && overlap >= -mergeTolerance))
                            continue;

                        double start = Math.Min(aStart, bStart), end = Math.Max(aEnd, bEnd);
                        double low = Math.Min(aLow, bLow), high = Math.Max(aHigh, bHigh);
                        a.A = horizontal ? new Point2d(start, low) : new Point2d(low, start);
                        a.B = horizontal ? new Point2d(end, low) : new Point2d(low, end);
                        a.C = horizontal ? new Point2d(end, high) : new Point2d(high, end);
                        a.D = horizontal ? new Point2d(start, high) : new Point2d(high, start);
                        a.Length = end - start;
                        a.Thickness = high - low;
                        a.FromHatch = a.FromHatch || b.FromHatch;
                        walls.RemoveAt(j);
                        changed = true;
                        break;
                    }
                }
            } while (changed);
        }

        private static void JoinNearbyOrthogonalWalls(List<DetectedWall> walls, double maxGap)
        {
            for (int i = 0; i < walls.Count; i++)
            {
                DetectedWall first = walls[i];
                if (!first.Included) continue;
                for (int j = i + 1; j < walls.Count; j++)
                {
                    DetectedWall second = walls[j];
                    if (!second.Included) continue;
                    bool firstHorizontal = IsHorizontal(first);
                    bool secondHorizontal = IsHorizontal(second);
                    if (firstHorizontal == secondHorizontal) continue;

                    DetectedWall horizontal = firstHorizontal ? first : second;
                    DetectedWall vertical = firstHorizontal ? second : first;
                    double tolerance = Math.Max(1e-6,
                        Math.Min(maxGap, Math.Max(horizontal.Thickness, vertical.Thickness) * 1.5));
                    Bounds(horizontal, out double hx0, out double hy0, out double hx1, out double hy1);
                    Bounds(vertical, out double vx0, out double vy0, out double vx1, out double vy1);

                    // A horizontal wall must overlap the vertical wall's Y band;
                    // only its endpoint may need extending in X.
                    double yOverlap = Math.Min(hy1, vy1) - Math.Max(hy0, vy0);
                    if (yOverlap > 1e-8)
                    {
                        if (hx1 <= vx0 && vx0 - hx1 <= tolerance)
                            SetHorizontalEnd(horizontal, false, vx0);
                        else if (vx1 <= hx0 && hx0 - vx1 <= tolerance)
                            SetHorizontalEnd(horizontal, true, vx1);
                    }

                    // Conversely, close vertical endpoints can meet the side of
                    // a horizontal wall. Recompute bounds after a possible X join.
                    Bounds(horizontal, out hx0, out hy0, out hx1, out hy1);
                    Bounds(vertical, out vx0, out vy0, out vx1, out vy1);
                    double xOverlap = Math.Min(hx1, vx1) - Math.Max(hx0, vx0);
                    if (xOverlap > 1e-8)
                    {
                        if (vy1 <= hy0 && hy0 - vy1 <= tolerance)
                            SetVerticalEnd(vertical, false, hy0);
                        else if (hy1 <= vy0 && vy0 - hy1 <= tolerance)
                            SetVerticalEnd(vertical, true, hy1);
                    }
                }
            }
        }

        private static bool IsHorizontal(DetectedWall wall)
        {
            return Math.Abs(wall.B.X - wall.A.X) > Math.Abs(wall.B.Y - wall.A.Y);
        }

        private static bool IsConnectedShortReturn(DetectedWall candidate, List<DetectedWall> recognized, double maxGap)
        {
            foreach (DetectedWall wall in recognized)
            {
                if (!wall.Included || wall.Length < candidate.Length || IsHorizontal(wall) == IsHorizontal(candidate))
                    continue;
                double tolerance = Math.Max(1e-6,
                    Math.Min(maxGap, Math.Max(candidate.Thickness, wall.Thickness) * 1.5));
                Bounds(candidate, out double cx0, out double cy0, out double cx1, out double cy1);
                Bounds(wall, out double wx0, out double wy0, out double wx1, out double wy1);
                double xOverlap = Math.Min(cx1, wx1) - Math.Max(cx0, wx0);
                double yOverlap = Math.Min(cy1, wy1) - Math.Max(cy0, wy0);
                double xGap = Math.Max(0, Math.Max(wx0 - cx1, cx0 - wx1));
                double yGap = Math.Max(0, Math.Max(wy0 - cy1, cy0 - wy1));
                if ((yOverlap > 1e-8 && xGap <= tolerance) ||
                    (xOverlap > 1e-8 && yGap <= tolerance))
                    return true;
            }
            return false;
        }

        private static void Bounds(DetectedWall wall, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = Math.Min(Math.Min(wall.A.X, wall.B.X), Math.Min(wall.C.X, wall.D.X));
            maxX = Math.Max(Math.Max(wall.A.X, wall.B.X), Math.Max(wall.C.X, wall.D.X));
            minY = Math.Min(Math.Min(wall.A.Y, wall.B.Y), Math.Min(wall.C.Y, wall.D.Y));
            maxY = Math.Max(Math.Max(wall.A.Y, wall.B.Y), Math.Max(wall.C.Y, wall.D.Y));
        }

        private static void SetHorizontalEnd(DetectedWall wall, bool start, double x)
        {
            if (start) { wall.A = new Point2d(x, wall.A.Y); wall.D = new Point2d(x, wall.D.Y); }
            else { wall.B = new Point2d(x, wall.B.Y); wall.C = new Point2d(x, wall.C.Y); }
            wall.Length = Math.Abs(wall.B.X - wall.A.X);
        }

        private static void SetVerticalEnd(DetectedWall wall, bool start, double y)
        {
            if (start) { wall.A = new Point2d(wall.A.X, y); wall.D = new Point2d(wall.D.X, y); }
            else { wall.B = new Point2d(wall.B.X, y); wall.C = new Point2d(wall.C.X, y); }
            wall.Length = Math.Abs(wall.B.Y - wall.A.Y);
        }

        private static List<Tuple<double, double>> HatchIntervals(HatchRegion region, int axis,
            double offset, double minimum, double maximum)
        {
            List<double> cuts = new List<double> { minimum, maximum };
            foreach (List<Point2d> loop in region.Loops)
            {
                for (int i = 0; i < loop.Count; i++)
                {
                    Point2d a = loop[i], b = loop[(i + 1) % loop.Count];
                    double aAcross = axis == 0 ? a.Y : a.X;
                    double bAcross = axis == 0 ? b.Y : b.X;
                    if ((aAcross > offset) == (bAcross > offset)) continue;
                    double aAlong = axis == 0 ? a.X : a.Y;
                    double bAlong = axis == 0 ? b.X : b.Y;
                    double cut = aAlong + (bAlong - aAlong) * (offset - aAcross) / (bAcross - aAcross);
                    if (cut > minimum && cut < maximum) cuts.Add(cut);
                }
            }
            cuts.Sort();
            List<Tuple<double, double>> intervals = new List<Tuple<double, double>>();
            for (int i = 1; i < cuts.Count; i++)
            {
                double lo = cuts[i - 1], hi = cuts[i];
                if (hi - lo < 1e-9) continue;
                double middle = (lo + hi) * 0.5;
                Point2d sample = axis == 0 ? new Point2d(middle, offset) : new Point2d(offset, middle);
                if (Contains(region, sample)) intervals.Add(Tuple.Create(lo, hi));
            }
            return intervals;
        }

        private static bool Contains(HatchRegion region, Point2d point)
        {
            bool inside = false;
            foreach (List<Point2d> loop in region.Loops)
            {
                bool inLoop = false;
                for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                {
                    Point2d a = loop[i], b = loop[j];
                    if ((a.Y > point.Y) != (b.Y > point.Y) &&
                        point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                        inLoop = !inLoop;
                }
                if (inLoop) inside = !inside;
            }
            return inside;
        }

        private static bool SameWallStrip(DetectedWall a, DetectedWall b)
        {
            bool horizontalA = Math.Abs(a.B.X - a.A.X) > Math.Abs(a.B.Y - a.A.Y);
            bool horizontalB = Math.Abs(b.B.X - b.A.X) > Math.Abs(b.B.Y - b.A.Y);
            if (horizontalA != horizontalB) return false;
            double aCenter = horizontalA ? (a.A.Y + a.D.Y) * 0.5 : (a.A.X + a.D.X) * 0.5;
            double bCenter = horizontalB ? (b.A.Y + b.D.Y) * 0.5 : (b.A.X + b.D.X) * 0.5;
            if (Math.Abs(aCenter - bCenter) > Math.Min(a.Thickness, b.Thickness) * 0.3 ||
                Math.Abs(a.Thickness - b.Thickness) > Math.Max(a.Thickness, b.Thickness) * 0.3) return false;
            return true;
        }

        private static DetectedWall WithInterval(DetectedWall wall, double lo, double hi, bool horizontal)
        {
            return new DetectedWall
            {
                A = horizontal ? new Point2d(lo, wall.A.Y) : new Point2d(wall.A.X, lo),
                B = horizontal ? new Point2d(hi, wall.B.Y) : new Point2d(wall.B.X, hi),
                C = horizontal ? new Point2d(hi, wall.C.Y) : new Point2d(wall.C.X, hi),
                D = horizontal ? new Point2d(lo, wall.D.Y) : new Point2d(wall.D.X, lo),
                Length = hi - lo, Thickness = wall.Thickness, FromHatch = wall.FromHatch
            };
        }
    }
}
