using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Nexo.AutoCAD;
[assembly: CommandClass(typeof(Nexo.WallHarness.RealWallRegression))]
namespace Nexo.WallHarness
{
    public sealed class RealWallRegression
    {
        [CommandMethod("NEXO_REAL_WALL_REGRESSION")]
        public void Run()
        {
            var log = new List<string>();
            try
            {
                var db = Application.DocumentManager.MdiActiveDocument.Database;
                var ids = new List<ObjectId>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    foreach (ObjectId id in space) ids.Add(id);
                    tr.Commit();
                }
                // Region including the near-zero fragment observed in the user's suite.
                double minX = 34, minY = 12, maxX = 38, maxY = 16;
                if (Environment.GetEnvironmentVariable("NEXO_REAL_WIDE") == "1")
                { minX = 6; minY = 10; maxX = 16; maxY = 19; }
                if (Environment.GetEnvironmentVariable("NEXO_REAL_FACADE") == "1")
                { minX = 24; minY = 16; maxX = 45; maxY = 23; }
                var detected = WallDetection.Detect(db, ids.ToArray(), Matrix3d.Identity,
                    minX, minY, maxX, maxY, new WallDetectionOptions());
                var layout = new WallLayout { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY, Height = 2.8 };
                int originalOverlaps = CountOverlaps(detected.Walls);
                foreach (var piece in WallFootprintOptimizer.Optimize(detected.Walls)) layout.Walls.Add(piece.Wall);
                if (originalOverlaps == 0) throw new InvalidOperationException("Test region must contain an overlapping junction");
                if (CountOverlaps(layout.Walls) != 0) throw new InvalidOperationException("Optimized walls still overlap");
                log.Add("Original overlaps=" + originalOverlaps + "; optimized overlaps=0");
                if (layout.Walls.Count == 0) throw new InvalidOperationException("No walls in regression region");
                int tiny = 0;
                foreach (var wall in layout.Walls) if (wall.Length < 1e-8) tiny++;
                log.Add("Detected " + layout.Walls.Count + " included walls; near-zero=" + tiny);
                WallBlockLibrary.ImportMissing(db, Environment.GetEnvironmentVariable("NEXO_TEST_BLOCK_LIBRARY"));
                var xs = new[] { new Vector3d(.866,.5,0), new Vector3d(-.866,.5,0), new Vector3d(-.866,-.5,0), new Vector3d(.866,-.5,0) };
                var ys = new[] { new Vector3d(-.866,.5,0), new Vector3d(-.866,-.5,0), new Vector3d(.866,-.5,0), new Vector3d(.866,.5,0) };
                for (int i = 0; i < 4; i++)
                {
                    Point3d origin = new Point3d(5000 + i * 50, 5000, 0);
                    Point3d[] frame = { origin, origin + xs[i] * (maxX-minX), origin + xs[i] * (maxX-minX) + ys[i] * (maxY-minY), origin + ys[i] * (maxY-minY) };
                    int count = IsometricCompositeWalls.Insert(db, layout, frame, Matrix3d.Identity);
                    if (count != 1) throw new InvalidOperationException("Expected one composite wall object");
                    log.Add("PASS projected real walls rotation " + (i * 90));
                }
                if (tiny != 0) throw new InvalidOperationException("Near-zero fragments must not be detected");
                log.Add("REAL REGRESSION PASS");
            }
            catch (System.Exception e) { log.Add("REAL REGRESSION FAIL: " + e); }
            File.WriteAllLines(Environment.GetEnvironmentVariable("NEXO_REAL_WALL_LOG"), log);
        }

        private static int CountOverlaps(IList<DetectedWall> walls)
        {
            int count = 0;
            for (int i = 0; i < walls.Count; i++) for (int j = i + 1; j < walls.Count; j++)
            {
                var a = walls[i]; var b = walls[j];
                if (!a.Included || !b.Included) continue;
                double dx = Math.Min(Math.Max(a.A.X,a.C.X), Math.Max(b.A.X,b.C.X)) -
                    Math.Max(Math.Min(a.A.X,a.C.X), Math.Min(b.A.X,b.C.X));
                double dy = Math.Min(Math.Max(a.A.Y,a.C.Y), Math.Max(b.A.Y,b.C.Y)) -
                    Math.Max(Math.Min(a.A.Y,a.C.Y), Math.Min(b.A.Y,b.C.Y));
                if (dx > 1e-8 && dy > 1e-8) count++;
            }
            return count;
        }
    }
}

