using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
namespace Nexo.AutoCAD
{
    internal static class WallFootprintUnion
    {
        internal static Geometry Build(IEnumerable<WallFootprintOptimizer.Piece> pieces)
        {
            Geometry union = null;
            foreach (WallFootprintOptimizer.Piece piece in pieces)
            {
                DetectedWall w = piece.Wall;
                double minX = Math.Min(w.A.X,w.C.X), maxX = Math.Max(w.A.X,w.C.X);
                double minY = Math.Min(w.A.Y,w.C.Y), maxY = Math.Max(w.A.Y,w.C.Y);
                Geometry rectangle = new RectangleGeometry(new Rect(minX, minY, maxX-minX, maxY-minY));
                union = union == null ? rectangle : Geometry.Combine(union, rectangle, GeometryCombineMode.Union, null,
                    1e-8, ToleranceType.Absolute);
            }
            return union;
        }
    }
}
