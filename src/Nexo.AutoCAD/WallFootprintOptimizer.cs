using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace Nexo.AutoCAD
{
    // Partition the union of the selected rectangular wall footprints. The source
    // wall remains attached to each piece so preview clicks keep editing the
    // original selection, even when a junction has to split into several pieces.
    internal static class WallFootprintOptimizer
    {
        internal sealed class Piece
        {
            internal DetectedWall Source;
            internal DetectedWall Wall;
        }

        private struct Rect
        {
            internal double X0, Y0, X1, Y1;
            internal Rect(double x0, double y0, double x1, double y1)
            { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        }

        private const double Tolerance = 1e-8;

        internal static List<Piece> Optimize(IEnumerable<DetectedWall> walls)
        {
            var selected = new List<DetectedWall>();
            foreach (DetectedWall wall in walls)
                if (wall.Included) selected.Add(wall);
            // Assign intersections to the longer wall first, usually avoiding a
            // split at L and T junctions and keeping fewer editable blocks.
            selected.Sort((a, b) => b.Length.CompareTo(a.Length));
            var result = new List<Piece>();
            var covered = new List<Rect>();
            foreach (DetectedWall source in selected)
            {
                var remaining = new List<Rect> { GetBounds(source) };
                foreach (Rect occupied in covered)
                {
                    var next = new List<Rect>();
                    foreach (Rect fragment in remaining) Subtract(fragment, occupied, next);
                    remaining = next;
                    if (remaining.Count == 0) break;
                }
                bool horizontal = Math.Abs(source.B.X - source.A.X) > Math.Abs(source.B.Y - source.A.Y);
                foreach (Rect fragment in remaining)
                {
                    covered.Add(fragment);
                    result.Add(new Piece { Source = source, Wall = CreateWall(fragment, horizontal, source) });
                }
            }
            return result;
        }

        private static Rect GetBounds(DetectedWall wall)
        {
            return new Rect(Math.Min(Math.Min(wall.A.X, wall.B.X), Math.Min(wall.C.X, wall.D.X)),
                Math.Min(Math.Min(wall.A.Y, wall.B.Y), Math.Min(wall.C.Y, wall.D.Y)),
                Math.Max(Math.Max(wall.A.X, wall.B.X), Math.Max(wall.C.X, wall.D.X)),
                Math.Max(Math.Max(wall.A.Y, wall.B.Y), Math.Max(wall.C.Y, wall.D.Y)));
        }

        private static void Subtract(Rect a, Rect b, List<Rect> output)
        {
            double x0 = Math.Max(a.X0, b.X0), x1 = Math.Min(a.X1, b.X1);
            double y0 = Math.Max(a.Y0, b.Y0), y1 = Math.Min(a.Y1, b.Y1);
            if (x1 - x0 <= Tolerance || y1 - y0 <= Tolerance) { output.Add(a); return; }
            Add(output, a.X0, a.Y0, x0, a.Y1);
            Add(output, x1, a.Y0, a.X1, a.Y1);
            Add(output, x0, a.Y0, x1, y0);
            Add(output, x0, y1, x1, a.Y1);
        }

        private static void Add(List<Rect> output, double x0, double y0, double x1, double y1)
        {
            if (x1 - x0 > Tolerance && y1 - y0 > Tolerance) output.Add(new Rect(x0, y0, x1, y1));
        }

        private static DetectedWall CreateWall(Rect r, bool horizontal, DetectedWall source)
        {
            return new DetectedWall {
                A = new Point2d(r.X0, r.Y0),
                B = horizontal ? new Point2d(r.X1, r.Y0) : new Point2d(r.X0, r.Y1),
                C = new Point2d(r.X1, r.Y1),
                D = horizontal ? new Point2d(r.X0, r.Y1) : new Point2d(r.X1, r.Y0),
                Length = horizontal ? r.X1-r.X0 : r.Y1-r.Y0,
                Thickness = horizontal ? r.Y1-r.Y0 : r.X1-r.X0,
                FromHatch = source.FromHatch, Included = true
            };
        }
    }
}
