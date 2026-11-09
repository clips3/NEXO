using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Nexo.AutoCAD
{
    // One projected object for the union of the selected wall footprints.
    // WPF's boolean geometry gives only the exterior and hole boundaries, so
    // no edges are created at the joins between detected rectangles.
    internal static class IsometricCompositeWalls
    {
        private const double GeometryTolerance = 1e-8;
        private const double DepthTolerance = 1e-7;

        private sealed class Edge
        {
            internal Point3d A, B;
            internal bool Visible;
        }

        private sealed class Face
        {
            internal readonly Point3d[] Corners;
            private readonly double _height;
            private readonly bool _isTop;
            private readonly double _winding;
            private readonly Rect _bounds;

            internal Face(Point3d[] corners, double height, bool isTop)
            {
                Corners = corners;
                _height = height;
                _isTop = isTop;
                _winding = Math.Sign(Vector.CrossProduct(ToPoint(corners[1]) - ToPoint(corners[0]),
                    ToPoint(corners[2]) - ToPoint(corners[1])));
                _bounds = new Rect(ToPoint(corners[0]), ToPoint(corners[1]));
                _bounds.Union(ToPoint(corners[2]));
                _bounds.Union(ToPoint(corners[3]));
            }

            internal bool HasArea => _winding != 0;

            internal bool TryOcclude(Point3d a, Point3d b, double elevationA, double elevationB,
                out double start, out double end)
            {
                start = 0;
                end = 1;
                if (!_bounds.IntersectsWith(new Rect(ToPoint(a), ToPoint(b))))
                    return false;

                // At the same projected point, higher elevation means closer to the viewer.
                // The projection lifts all elevations along screen Y.
                if (!RestrictInterval(ElevationAt(a) - elevationA - DepthTolerance,
                    ElevationAt(b) - elevationB - DepthTolerance, ref start, ref end))
                    return false;

                for (int i = 0; i < Corners.Length; i++)
                {
                    Point origin = ToPoint(Corners[i]);
                    Vector boundary = ToPoint(Corners[(i + 1) % Corners.Length]) - origin;
                    boundary.Normalize();
                    if (!RestrictInterval(
                        _winding * Vector.CrossProduct(boundary, ToPoint(a) - origin) + GeometryTolerance,
                        _winding * Vector.CrossProduct(boundary, ToPoint(b) - origin) + GeometryTolerance,
                        ref start, ref end))
                        return false;
                }
                return true;
            }

            private double ElevationAt(Point3d point)
            {
                if (_isTop) return _height;
                Point3d a = Corners[0], b = Corners[1];
                double along = (point.X - a.X) / (b.X - a.X);
                return point.Y - (a.Y + (b.Y - a.Y) * along);
            }
        }

        internal static int Insert(Database database, WallLayout layout, Point3d[] frame, Matrix3d ucs)
        {
            if (frame == null || frame.Length != 4 || layout.MaxX <= layout.MinX || layout.MaxY <= layout.MinY || layout.Height <= 0)
                throw new InvalidOperationException("A moldura ou a altura das paredes não é válida.");
            List<WallFootprintOptimizer.Piece> pieces = WallFootprintOptimizer.Optimize(layout.Walls);
            if (pieces.Count == 0) return 0;
            Geometry union = WallFootprintUnion.Build(pieces);
            List<Edge> edges = BuildEdges(union, pieces, layout, frame, ucs);
            if (edges.Count == 0) throw new InvalidOperationException("Não foi possível formar o contorno das paredes selecionadas.");

            Matrix3d worldToUcs = ucs.Inverse();
            Vector3d height = new Vector3d(0, layout.Height, 0);
            var faces = new List<Face>();
            foreach (Edge edge in edges)
                if (edge.Visible)
                    faces.Add(new Face(new[] { edge.A, edge.B, edge.B + height, edge.A + height }, layout.Height, false));
            foreach (WallFootprintOptimizer.Piece piece in pieces)
            {
                DetectedWall wall = piece.Wall;
                Point2d[] corners = { wall.A, wall.B, wall.C, wall.D };
                var top = new Point3d[corners.Length];
                for (int i = 0; i < corners.Length; i++)
                    top[i] = Project(corners[i], layout, frame).TransformBy(worldToUcs) + height;
                faces.Add(new Face(top, layout.Height, true));
            }
            faces.RemoveAll(face => !face.HasArea);

            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)tx.GetObject(database.BlockTableId, OpenMode.ForWrite);
                BlockTableRecord definition = new BlockTableRecord { Name = "NEXO_PAREDES_" + Guid.NewGuid().ToString("N") };
                table.Add(definition); tx.AddNewlyCreatedDBObject(definition, true);
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                foreach (Face face in faces)
                    AddWipeout(database, tx, definition, face.Corners, ucs);

                var cornersDrawn = new HashSet<Point3d>();
                foreach (Edge edge in edges)
                {
                    AddLine(database, tx, definition, edge.A + height, edge.B + height, ucs);
                    if (!edge.Visible) continue;
                    DrawVisibleLine(database, tx, definition, ucs, faces, edge.A, edge.B, 0, 0);
                    if (cornersDrawn.Add(edge.A))
                        DrawVisibleLine(database, tx, definition, ucs, faces, edge.A, edge.A + height, 0, layout.Height);
                    if (cornersDrawn.Add(edge.B))
                        DrawVisibleLine(database, tx, definition, ucs, faces, edge.B, edge.B + height, 0, layout.Height);
                }

                BlockReference reference = new BlockReference(Point3d.Origin, definition.ObjectId) { Layer = "0" };
                space.AppendEntity(reference); tx.AddNewlyCreatedDBObject(reference, true);
                tx.Commit();
            }
            return 1;
        }

        private static void DrawVisibleLine(Database database, Transaction tx, BlockTableRecord definition,
            Matrix3d ucs, List<Face> faces, Point3d a, Point3d b, double elevationA, double elevationB)
        {
            var hidden = new List<(double Start, double End)>();
            foreach (Face face in faces)
                if (face.TryOcclude(a, b, elevationA, elevationB, out double start, out double end))
                    hidden.Add((start, end));
            hidden.Sort((left, right) => left.Start.CompareTo(right.Start));

            Vector3d direction = b - a;
            double cursor = 0;
            foreach ((double start, double end) in hidden)
            {
                if (start > cursor + GeometryTolerance)
                    AddLine(database, tx, definition, a + direction * cursor, a + direction * start, ucs);
                cursor = Math.Max(cursor, end);
            }
            if (cursor < 1 - GeometryTolerance)
                AddLine(database, tx, definition, a + direction * cursor, b, ucs);
        }

        private static bool RestrictInterval(double valueA, double valueB, ref double start, ref double end)
        {
            if (valueA >= 0 && valueB >= 0) return true;
            if (valueA < 0 && valueB < 0) return false;
            double crossing = valueA / (valueA - valueB);
            if (valueA < 0) start = Math.Max(start, crossing);
            else end = Math.Min(end, crossing);
            return end - start > GeometryTolerance;
        }

        private static Point ToPoint(Point3d point) => new Point(point.X, point.Y);

        private static List<Edge> BuildEdges(Geometry union, List<WallFootprintOptimizer.Piece> pieces,
            WallLayout layout, Point3d[] frame, Matrix3d ucs)
        {
            PathGeometry outline = union.GetOutlinedPathGeometry(GeometryTolerance, ToleranceType.Absolute);
            PathGeometry flat = outline.GetFlattenedPathGeometry(GeometryTolerance, ToleranceType.Absolute);
            Matrix3d worldToUcs = ucs.Inverse();
            var result = new List<Edge>();
            foreach (PathFigure figure in flat.Figures)
            {
                var points = new List<Point> { figure.StartPoint };
                foreach (PathSegment segment in figure.Segments)
                {
                    if (segment is LineSegment line) points.Add(line.Point);
                    else if (segment is PolyLineSegment poly)
                        foreach (Point point in poly.Points) points.Add(point);
                }
                if (points.Count > 1 && (points[points.Count - 1] - points[0]).Length < GeometryTolerance)
                    points.RemoveAt(points.Count - 1);
                // Remove collinear split vertices left by the union operation.
                bool changed;
                do
                {
                    changed = false;
                    for (int i = 0; i < points.Count && points.Count > 3; i++)
                    {
                        Point a = points[(i + points.Count - 1) % points.Count];
                        Point b = points[i];
                        Point c = points[(i + 1) % points.Count];
                        Vector ab = b - a, bc = c - b;
                        if (Math.Abs(ab.X * bc.Y - ab.Y * bc.X) <=
                            GeometryTolerance * (ab.Length + bc.Length) && Vector.Multiply(ab, bc) >= 0)
                        {
                            points.RemoveAt(i);
                            changed = true;
                            break;
                        }
                    }
                } while (changed);

                for (int i = 0; i < points.Count; i++)
                {
                    Point a = points[i], b = points[(i + 1) % points.Count];
                    Vector tangent = b - a;
                    if (tangent.Length <= GeometryTolerance) continue;
                    Vector side = new Vector(-tangent.Y, tangent.X);
                    side.Normalize();
                    double probe = Math.Max(1e-5, Math.Min(1e-4, tangent.Length * 0.001));
                    Point middle = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    bool leftInside = Contains(pieces, middle + side * probe);
                    bool rightInside = Contains(pieces, middle - side * probe);
                    if (leftInside == rightInside)
                        throw new InvalidOperationException("Não foi possível determinar a face externa da parede.");
                    Vector outward = leftInside ? -side : side;
                    Point2d planMiddle = new Point2d(middle.X, middle.Y);
                    Point3d origin = Project(planMiddle, layout, frame).TransformBy(worldToUcs);
                    Point3d outside = Project(new Point2d(middle.X + outward.X, middle.Y + outward.Y), layout, frame)
                        .TransformBy(worldToUcs);
                    result.Add(new Edge
                    {
                        A = Project(new Point2d(a.X, a.Y), layout, frame).TransformBy(worldToUcs),
                        B = Project(new Point2d(b.X, b.Y), layout, frame).TransformBy(worldToUcs),
                        Visible = outside.Y < origin.Y - GeometryTolerance
                    });
                }
            }
            return result;
        }

        private static bool Contains(List<WallFootprintOptimizer.Piece> pieces, Point point)
        {
            foreach (WallFootprintOptimizer.Piece piece in pieces)
            {
                DetectedWall wall = piece.Wall;
                if (point.X > Math.Min(wall.A.X, wall.C.X) && point.X < Math.Max(wall.A.X, wall.C.X) &&
                    point.Y > Math.Min(wall.A.Y, wall.C.Y) && point.Y < Math.Max(wall.A.Y, wall.C.Y))
                    return true;
            }
            return false;
        }

        private static Point3d Project(Point2d point, WallLayout layout, Point3d[] frame)
        {
            double u = (point.X - layout.MinX) / (layout.MaxX - layout.MinX);
            double v = (point.Y - layout.MinY) / (layout.MaxY - layout.MinY);
            return frame[0] + (frame[1] - frame[0]) * u + (frame[3] - frame[0]) * v;
        }

        private static void AddLine(Database database, Transaction tx,
            BlockTableRecord definition, Point3d a, Point3d b, Matrix3d ucs)
        {
            Line line = new Line(a, b);
            line.SetDatabaseDefaults(database);
            line.Layer = "0";
            line.TransformBy(ucs);
            definition.AppendEntity(line);
            tx.AddNewlyCreatedDBObject(line, true);
        }

        private static void AddWipeout(Database database, Transaction tx,
            BlockTableRecord definition, Point3d[] vertices, Matrix3d ucs)
        {
            Point2dCollection points = new Point2dCollection();
            foreach (Point3d point in vertices)
                points.Add(new Point2d(point.X, point.Y));
            points.Add(new Point2d(vertices[0].X, vertices[0].Y));
            Wipeout wipeout = new Wipeout();
            wipeout.SetDatabaseDefaults(database);
            wipeout.Layer = "0";
            wipeout.SetFrom(points, Vector3d.ZAxis);
            wipeout.TransformBy(ucs);
            definition.AppendEntity(wipeout);
            tx.AddNewlyCreatedDBObject(wipeout, true);
        }
    }
}
