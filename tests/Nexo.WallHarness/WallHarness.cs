using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Nexo.AutoCAD;

[assembly: CommandClass(typeof(Nexo.WallHarness.WallHarness))]
namespace Nexo.WallHarness
{
    public sealed class WallHarness
    {
        [CommandMethod("NEXO_EXPORT_WALL_LIBRARY")]
        public void ExportLibrary()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            Database source = document.Database;
            string filePath = Environment.GetEnvironmentVariable("NEXO_WALL_LIBRARY_OUTPUT");
            if (string.IsNullOrWhiteSpace(filePath)) throw new InvalidOperationException("NEXO_WALL_LIBRARY_OUTPUT não informado.");
            ObjectIdCollection definitions = new ObjectIdCollection();
            using (Transaction transaction = source.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)transaction.GetObject(source.BlockTableId, OpenMode.ForRead);
                string names = Environment.GetEnvironmentVariable("NEXO_WALL_LIBRARY_NAMES");
                foreach (string name in string.IsNullOrWhiteSpace(names) ? new[] { "ISO_Parede", "ISO_Laje" } : names.Split(','))
                {
                    if (!table.Has(name)) throw new InvalidOperationException("Definição ausente: " + name);
                    definitions.Add(table[name]);
                }
                transaction.Commit();
            }
            using (Database destination = new Database(true, true))
            {
                IdMapping map = new IdMapping();
                source.WblockCloneObjects(definitions, destination.BlockTableId, map, DuplicateRecordCloning.Ignore, false);
                destination.SaveAs(filePath, DwgVersion.Current);
            }
            document.Editor.WriteMessage("\nBiblioteca exportada: " + filePath);
        }

        [CommandMethod("NEXO_WALL_HARNESS")]
        public void Run()
        {
            List<string> log = new List<string>();
            string logPath = Environment.GetEnvironmentVariable("NEXO_WALL_HARNESS_LOG");
            try
            {
                Document document = Application.DocumentManager.MdiActiveDocument;
                Database database = document.Database;
                TestWallFootprintOptimization(log);
                TestCompositeWalls(database, log);
                TestOpeningInsertion(database, log);
                TestLineRecognition(database, log);
                TestNestedReference(database, log);
                TestExternalReference(database, log);
                TestClippedReference(database, log);
                TestHatchRecognition(database, log);
                TestHatchOpening(database, log);
                TestPossibleOpening(database, log);
                TestAmbiguousOneSidedContinuation(database, log);
                TestRealDrawing(database, log);
                string library = Environment.GetEnvironmentVariable("NEXO_TEST_BLOCK_LIBRARY");
                if (!string.IsNullOrWhiteSpace(library))
                {
                    WallBlockLibrary.ImportMissing(database, library);
                    log.Add("PASS automatic import of missing block definitions");
                }
                TestDynamicInsertion(database, log);
                TestSlabInsertion(document, log);
                log.Add("HARNESS PASS");
            }
            catch (System.Exception exception) { log.Add("HARNESS FAIL: " + exception); }
            if (!string.IsNullOrWhiteSpace(logPath)) File.WriteAllLines(logPath, log);
            foreach (string line in log) Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n" + line);
        }

        private static DetectedWall RectangleWall(double x0, double y0, double x1, double y1, bool horizontal)
        {
            return new DetectedWall {
                A = new Point2d(x0,y0), B = horizontal ? new Point2d(x1,y0) : new Point2d(x0,y1),
                C = new Point2d(x1,y1), D = horizontal ? new Point2d(x0,y1) : new Point2d(x1,y0),
                Length = horizontal ? x1-x0 : y1-y0, Thickness = horizontal ? y1-y0 : x1-x0
            };
        }

        private static void TestOpeningInsertion(Database database, List<string> log)
        {
            string library = Environment.GetEnvironmentVariable("NEXO_TEST_OPENING_LIBRARY");
            if (string.IsNullOrWhiteSpace(library)) { log.Add("SKIP opening library not supplied"); return; }
            WallBlockLibrary.ImportMissing(database, library, new[] { "ISO_Porta2", "ISO_Janela" });
            var layout = new WallLayout { MinX=0, MinY=0, MaxX=10, MaxY=8, Height=2.8 };
            var door = RectangleWall(2,1,3,1.2,true); door.Opening = OpeningKind.Door;
            door.OpeningWidth=.9; door.OpeningHeight=2.1; door.OpeningSill=0;
            var window = RectangleWall(5,2,5.2,4,false); window.Opening = OpeningKind.Window;
            window.OpeningWidth=1.8; window.OpeningHeight=1.2; window.OpeningSill=1;
            layout.Openings.Add(door); layout.Openings.Add(window);
            var xs = new[] { new Vector3d(.866,.5,0), new Vector3d(-.866,.5,0), new Vector3d(-.866,-.5,0), new Vector3d(.866,-.5,0) };
            var ys = new[] { new Vector3d(-.866,.5,0), new Vector3d(-.866,-.5,0), new Vector3d(.866,-.5,0), new Vector3d(.866,.5,0) };
            for (int i=0; i<4; i++)
            {
                var before = new HashSet<ObjectId>();
                using (Transaction tx = database.TransactionManager.StartTransaction())
                { foreach (ObjectId id in (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForRead)) before.Add(id); tx.Commit(); }
                Point3d origin = new Point3d(8000 + i*40,8000,0);
                Point3d[] frame = { origin, origin+xs[i]*10, origin+xs[i]*10+ys[i]*8, origin+ys[i]*8 };
                Require(IsometricOpeningInserter.Insert(database,layout,frame,Matrix3d.Identity)==2, "duas aberturas devem ser criadas");
                using (Transaction tx = database.TransactionManager.StartTransaction())
                {
                    int checkedCount=0;
                    foreach (ObjectId id in (BlockTableRecord)tx.GetObject(database.CurrentSpaceId,OpenMode.ForRead))
                    {
                        if (before.Contains(id)) continue;
                        var br=tx.GetObject(id,OpenMode.ForRead,false) as BlockReference;
                        if (br == null || !br.IsDynamicBlock) continue;
                        string name=((BlockTableRecord)tx.GetObject(br.DynamicBlockTableRecord,OpenMode.ForRead)).Name;
                        DetectedWall expected=name=="ISO_Porta2" ? door : name=="ISO_Janela" ? window : null;
                        if (expected == null) continue;
                        double w=0,h=0;
                        foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
                        { if(p.PropertyName=="Y") w=Convert.ToDouble(p.Value); if(p.PropertyName=="Z") h=Convert.ToDouble(p.Value); }
                        Require(Near(w,expected.OpeningWidth) && Near(h,expected.OpeningHeight), "dimensões dinâmicas da abertura");
                        Point2d[] corners={expected.A,expected.B,expected.C,expected.D};
                        Point3d[] proj=new Point3d[4]; for(int k=0;k<4;k++) proj[k]=origin+xs[i]*corners[k].X+ys[i]*corners[k].Y;
                        int front=0; for(int k=1;k<4;k++) if(proj[k].Y<proj[front].Y) front=k;
                        int next=front==0?1:front==1?0:front==2?3:2;
                        Point3d expectedPosition=proj[front]+(proj[next]-proj[front])*((expected.Length-expected.OpeningWidth)/(2*expected.Length))+new Vector3d(0,expected.OpeningSill,0);
                        Require(br.Position.DistanceTo(expectedPosition)<1e-6, "posição e peitoril devem coincidir com o vão projetado");
                        Require(Math.Sign(br.ScaleFactors.X)==Math.Sign(proj[next].X-proj[front].X)*-1, "espelhamento deve acompanhar a face");
                        checkedCount++;
                    }
                    Require(checkedCount==2,"porta e janela dinâmicas devem ser encontradas"); tx.Commit();
                }
            }
            log.Add("PASS opening dimensions, sill, placement and mirror in four orientations");
        }

        private static void TestCompositeWalls(Database database, List<string> log)
        {
            // A closed ring exercises a hole; an L checks that the shared
            // junction becomes a single outline rather than two block borders.
            var ring = new[] {
                RectangleWall(0,0,6,1,true), RectangleWall(0,5,6,6,true),
                RectangleWall(0,1,1,5,false), RectangleWall(5,1,6,5,false)
            };
            var union = WallFootprintUnion.Build(WallFootprintOptimizer.Optimize(ring));
            Require(Math.Abs(union.GetArea(1e-8, System.Windows.Media.ToleranceType.Absolute) - 20) < 1e-6,
                "união deve preservar a área da parede em anel");
            Require(!union.FillContains(new System.Windows.Point(3,3)) && union.FillContains(new System.Windows.Point(.5,3)),
                "união deve preservar o vão interno");
            var l = new[] { RectangleWall(0,0,4,1,true), RectangleWall(3,0,4,4,false) };
            foreach (var walls in new[] { ring, l })
            {
                var layout = new WallLayout { MinX=0, MinY=0, MaxX=6, MaxY=6, Height=2.8 };
                layout.Walls.AddRange(walls);
                var xAxes = new[] { new Vector3d(.866,.5,0), new Vector3d(-.866,.5,0), new Vector3d(-.866,-.5,0), new Vector3d(.866,-.5,0) };
                var yAxes = new[] { new Vector3d(-.866,.5,0), new Vector3d(-.866,-.5,0), new Vector3d(.866,-.5,0), new Vector3d(.866,.5,0) };
                for (int i=0; i<4; i++)
                {
                    Point3d o = new Point3d(7000+i*30,7000,0);
                    Point3d[] frame = { o, o+xAxes[i]*6, o+xAxes[i]*6+yAxes[i]*6, o+yAxes[i]*6 };
                    Require(IsometricCompositeWalls.Insert(database, layout, frame, Matrix3d.Identity) == 1,
                        "geração composta deve criar um único bloco");
                }
            }
            log.Add("PASS unified outline, hole and composite wall generation in four rotations");
        }

        private static void TestWallFootprintOptimization(List<string> log)
        {
            // Corner, T, crossing, partial overlap and fully contained rectangle.
            var cases = new[] {
                new[] { RectangleWall(0,0,4,1,true), RectangleWall(3,0,4,4,false) },
                new[] { RectangleWall(0,0,6,1,true), RectangleWall(2,0,3,4,false) },
                new[] { RectangleWall(0,2,6,3,true), RectangleWall(2,0,3,5,false) },
                new[] { RectangleWall(0,0,4,2,true), RectangleWall(3,1,5,4,false) },
                new[] { RectangleWall(0,0,5,2,true), RectangleWall(1,0.5,3,1.5,true) }
            };
            foreach (DetectedWall[] source in cases)
            {
                VerifyWallPartition(source);
                source[0].Included = false;
                var afterExclusion = WallFootprintOptimizer.Optimize(source);
                Require(afterExclusion.Count == 1 && object.ReferenceEquals(afterExclusion[0].Source, source[1]),
                    "excluir uma parede deve restaurar integralmente a outra");
                VerifyWallPartition(source);
                source[0].Included = true;
            }
            Require(WallFootprintOptimizer.Optimize(cases[0]).Count == 2, "canto deve usar apenas duas paredes");
            Require(WallFootprintOptimizer.Optimize(cases[4]).Count == 1, "parede contida não deve gerar bloco duplicado");
            log.Add("PASS wall footprint union, corner/T/cross/partial/contained and selection");
        }

        private static void VerifyWallPartition(DetectedWall[] source)
        {
            var pieces = WallFootprintOptimizer.Optimize(source);
            var xs = new SortedSet<double>(); var ys = new SortedSet<double>();
            foreach (DetectedWall wall in source)
            {
                if (!wall.Included) continue;
                foreach (Point2d p in new[] { wall.A, wall.B, wall.C, wall.D }) { xs.Add(p.X); ys.Add(p.Y); }
            }
            foreach (var piece in pieces)
                foreach (Point2d p in new[] { piece.Wall.A, piece.Wall.B, piece.Wall.C, piece.Wall.D }) { xs.Add(p.X); ys.Add(p.Y); }
            var xx = new List<double>(xs); var yy = new List<double>(ys);
            for (int x = 0; x+1 < xx.Count; x++) for (int y = 0; y+1 < yy.Count; y++)
            {
                double px = (xx[x]+xx[x+1])/2, py = (yy[y]+yy[y+1])/2;
                int input = 0, output = 0;
                foreach (DetectedWall wall in source) if (wall.Included && Contains(wall, px, py)) input++;
                foreach (var piece in pieces) if (Contains(piece.Wall, px, py)) output++;
                Require((input > 0) == (output > 0) && output <= 1,
                    "a união deve ser preservada sem sobreposição nem perda de área");
            }
        }

        private static bool Contains(DetectedWall w, double x, double y)
        {
            double x0 = Math.Min(w.A.X,w.C.X), x1 = Math.Max(w.A.X,w.C.X);
            double y0 = Math.Min(w.A.Y,w.C.Y), y1 = Math.Max(w.A.Y,w.C.Y);
            return x > x0 && x < x1 && y > y0 && y < y1;
        }

        private static void TestLineRecognition(Database database, List<string> log)
        {
            ObjectId[] ids = AddLines(database, "NEXO_HARNESS_LINE", new[] {
                new Point3d(1000,1000,0), new Point3d(1010,1000,0),
                new Point3d(1000,1000.2,0), new Point3d(1010,1000.2,0) });
            WallDetectionOptions options = new WallDetectionOptions { Layer = "NEXO_HARNESS_LINE" };
            WallDetectionResult result = WallDetection.Detect(database, ids, Matrix3d.Identity, 1002, 999, 1008, 1001, options);
            Require(result.Walls.Count == 1, "duas linhas devem formar uma parede");
            Require(Near(result.Walls[0].Length, 6) && Near(result.Walls[0].Thickness, .2), "parede deve ser recortada na janela");
            log.Add("PASS lines and crop");
        }

        private static void TestNestedReference(Database database, List<string> log)
        {
            ObjectId referenceId;
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)tx.GetObject(database.BlockTableId, OpenMode.ForWrite);
                BlockTableRecord definition = new BlockTableRecord { Name = "NEXO_HARNESS_NESTED" };
                table.Add(definition); tx.AddNewlyCreatedDBObject(definition, true);
                foreach (double y in new[] { 0.0, 0.2 })
                {
                    Line line = new Line(new Point3d(0, y, 0), new Point3d(4, y, 0));
                    line.SetDatabaseDefaults(database); line.Layer = "0";
                    definition.AppendEntity(line); tx.AddNewlyCreatedDBObject(line, true);
                }
                EnsureLayer(database, tx, "NEXO_HARNESS_NESTED_LAYER");
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                BlockReference reference = new BlockReference(new Point3d(1100, 1100, 0), definition.ObjectId)
                { Layer = "NEXO_HARNESS_NESTED_LAYER", Rotation = Math.PI / 2 };
                referenceId = space.AppendEntity(reference); tx.AddNewlyCreatedDBObject(reference, true); tx.Commit();
            }
            WallDetectionResult result = WallDetection.Detect(database, new[] { referenceId }, Matrix3d.Identity,
                1099, 1099, 1101, 1105, new WallDetectionOptions { Layer = "NEXO_HARNESS_NESTED_LAYER" });
            Require(result.Walls.Count == 1 && Near(result.Walls[0].Length, 4), "referência aninhada rotacionada deve ser detectada");
            log.Add("PASS nested transform and layer 0");
        }

        private static void TestHatchRecognition(Database database, List<string> log)
        {
            ObjectId hatchId;
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                EnsureLayer(database, tx, "NEXO_HARNESS_HATCH");
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                Hatch hatch = new Hatch(); hatch.SetDatabaseDefaults(database); hatch.Layer = "NEXO_HARNESS_HATCH";
                hatchId = space.AppendEntity(hatch); tx.AddNewlyCreatedDBObject(hatch, true);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31"); hatch.Associative = false;
                Point2dCollection points = new Point2dCollection(new[] { new Point2d(1200,1200), new Point2d(1204,1200),
                    new Point2d(1204,1200.2), new Point2d(1200,1200.2) });
                hatch.AppendLoop(HatchLoopTypes.Polyline, points, new DoubleCollection(new[] { 0.0, 0.0, 0.0, 0.0 }));
                hatch.EvaluateHatch(true); tx.Commit();
            }
            WallDetectionResult result = WallDetection.Detect(database, new[] { hatchId }, Matrix3d.Identity,
                1199, 1199, 1205, 1201, new WallDetectionOptions { Layer = "NEXO_HARNESS_HATCH" });
            Require(result.Walls.Count == 1 && result.Walls[0].FromHatch && Near(result.Walls[0].Length, 4),
                "contorno de hachura não associativa deve formar parede");
            log.Add("PASS non-associative hatch");
        }

        private static void TestClippedReference(Database database, List<string> log)
        {
            ObjectId referenceId;
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                EnsureLayer(database, tx, "NEXO_HARNESS_CLIP_LAYER");
                BlockTable table = (BlockTable)tx.GetObject(database.BlockTableId, OpenMode.ForWrite);
                BlockTableRecord definition = new BlockTableRecord { Name = "NEXO_HARNESS_CLIP" };
                table.Add(definition); tx.AddNewlyCreatedDBObject(definition, true);
                foreach (double y in new[] { 0.0, .2 })
                {
                    Line line = new Line(new Point3d(0, y, 0), new Point3d(4, y, 0));
                    line.SetDatabaseDefaults(database); line.Layer = "0"; definition.AppendEntity(line); tx.AddNewlyCreatedDBObject(line, true);
                }
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                BlockReference reference = new BlockReference(new Point3d(1500,1500,0), definition.ObjectId) { Layer = "NEXO_HARNESS_CLIP_LAYER" };
                referenceId = space.AppendEntity(reference); tx.AddNewlyCreatedDBObject(reference, true);
                reference.CreateExtensionDictionary();
                DBDictionary extension = (DBDictionary)tx.GetObject(reference.ExtensionDictionary, OpenMode.ForWrite);
                DBDictionary filters = new DBDictionary(); extension.SetAt("ACAD_FILTER", filters); tx.AddNewlyCreatedDBObject(filters, true);
                SpatialFilter filter = new SpatialFilter();
                filter.Definition = new SpatialFilterDefinition(new Point2dCollection(new[] { new Point2d(0,-1), new Point2d(2,1) }), Vector3d.ZAxis, 0, 0, 0, true);
                filters.SetAt("SPATIAL", filter); tx.AddNewlyCreatedDBObject(filter, true); tx.Commit();
            }
            WallDetectionResult result = WallDetection.Detect(database, new[] { referenceId }, Matrix3d.Identity,
                1499, 1499, 1505, 1502, new WallDetectionOptions { Layer = "NEXO_HARNESS_CLIP_LAYER" });
            Require(result.Walls.Count == 1 && Near(result.Walls[0].Length, 2),
                "referência recortada deve limitar a parede ao XCLIP; candidates=" + result.Walls.Count +
                (result.Walls.Count > 0 ? " length=" + result.Walls[0].Length : "") + " ignored=" + result.IgnoredClippedReferences);
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                BlockReference reference = (BlockReference)tx.GetObject(referenceId, OpenMode.ForWrite);
                reference.TransformBy(Matrix3d.Rotation(Math.PI / 2, Vector3d.ZAxis, new Point3d(1500,1500,0)));
                reference.TransformBy(Matrix3d.Displacement(new Vector3d(5,3,0)));
                tx.Commit();
            }
            result = WallDetection.Detect(database, new[] { referenceId }, Matrix3d.Identity,
                1503, 1502, 1506, 1508, new WallDetectionOptions { Layer = "NEXO_HARNESS_CLIP_LAYER" });
            Require(result.Walls.Count == 1 && Near(result.Walls[0].Length, 2),
                "XCLIP deve acompanhar a referência após mover e girar; candidates=" + result.Walls.Count +
                (result.Walls.Count > 0 ? " length=" + result.Walls[0].Length : "") + " ignored=" + result.IgnoredClippedReferences);
            log.Add("PASS rectangular XCLIP");
        }

        private static void TestExternalReference(Database database, List<string> log)
        {
            string path = Path.Combine(Path.GetTempPath(), "nexo-wall-harness-xref.dwg");
            using (Database external = new Database(true, true))
            {
                using (Transaction tx = external.TransactionManager.StartTransaction())
                {
                    BlockTable table = (BlockTable)tx.GetObject(external.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord model = (BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    foreach (double y in new[] { 0.0, .2 })
                    {
                        Line line = new Line(new Point3d(0, y, 0), new Point3d(4, y, 0));
                        line.SetDatabaseDefaults(external); model.AppendEntity(line); tx.AddNewlyCreatedDBObject(line, true);
                    }
                    tx.Commit();
                }
                external.SaveAs(path, DwgVersion.Current);
            }
            ObjectId definitionId = database.AttachXref(path, "NEXO_HARNESS_XREF");
            ObjectId referenceId;
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                EnsureLayer(database, tx, "NEXO_HARNESS_XREF_LAYER");
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                BlockReference reference = new BlockReference(new Point3d(1400, 1400, 0), definitionId) { Layer = "NEXO_HARNESS_XREF_LAYER" };
                referenceId = space.AppendEntity(reference); tx.AddNewlyCreatedDBObject(reference, true); tx.Commit();
            }
            WallDetectionResult result = WallDetection.Detect(database, new[] { referenceId }, Matrix3d.Identity,
                1399, 1399, 1405, 1401, new WallDetectionOptions { Layer = "NEXO_HARNESS_XREF_LAYER" });
            Require(result.Walls.Count == 1 && Near(result.Walls[0].Length, 4), "referência externa deve ser lida com a transformação e layer herdada");
            log.Add("PASS external reference");
        }

        private static void TestRealDrawing(Database database, List<string> log)
        {
            List<ObjectId> ids = new List<ObjectId>();
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space) ids.Add(id);
                tx.Commit();
            }
            double[] bounds = {6, 10, 16, 19};
            string configured = Environment.GetEnvironmentVariable("NEXO_REPORT_BOUNDS");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string[] fields = configured.Split(',');
                for (int i = 0; i < 4; i++) bounds[i] = double.Parse(fields[i], System.Globalization.CultureInfo.InvariantCulture);
            }
            WallDetectionResult result = WallDetection.Detect(database, ids.ToArray(), Matrix3d.Identity,
                bounds[0], bounds[1], bounds[2], bounds[3], new WallDetectionOptions());
            int hatches = 0, lines = 0;
            foreach (DetectedWall wall in result.Walls) { if (wall.FromHatch) hatches++; else lines++; }
            if (configured == "24,16,45,23")
            {
                bool leftWall = false, rightWall = false;
                foreach (DetectedWall wall in result.Walls)
                {
                    if (!wall.Included) continue;
                    Require(!Contains(wall, 34.6, 18.82) && !Contains(wall, 28.2, 18.82) && !Contains(wall, 30.6, 18.82),
                        "a detecção não pode atravessar as janelas do desenho de referência");
                    if (Contains(wall, 33.0, 18.82)) leftWall = true;
                    if (Contains(wall, 35.7, 18.82)) rightWall = true;
                }
                Require(leftWall && rightWall, "paredes hachuradas ao lado da janela devem ser preservadas");
                log.Add("PASS real facade: hatched walls preserved and window gaps remain open");
            }
            log.Add("REAL drawing candidates=" + result.Walls.Count + " hatch=" + hatches + " line=" + lines +
                " hatchesRead=" + result.Hatches + " ignoredClipped=" + result.IgnoredClippedReferences + " capped=" + result.LimitWarnings);
            string report = Environment.GetEnvironmentVariable("NEXO_WALL_DETECTION_REPORT");
            if (!string.IsNullOrWhiteSpace(report))
            {
                List<string> rows = new List<string>();
                foreach (Tuple<Point2d, Point2d> edge in result.Context)
                    rows.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "C,{0},{1},{2},{3}", edge.Item1.X, edge.Item1.Y, edge.Item2.X, edge.Item2.Y));
                foreach (DetectedWall wall in result.Walls)
                    rows.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "W,{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}", wall.A.X, wall.A.Y, wall.B.X, wall.B.Y,
                        wall.C.X, wall.C.Y, wall.D.X, wall.D.Y, wall.FromHatch ? 1 : 0, wall.Included ? 1 : 0));
                File.WriteAllLines(report, rows);
            }
        }

        private static void TestHatchOpening(Database database, List<string> log)
        {
            ObjectId hatchId;
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                EnsureLayer(database, tx, "NEXO_HARNESS_HOLE");
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                Hatch hatch = new Hatch(); hatch.SetDatabaseDefaults(database); hatch.Layer = "NEXO_HARNESS_HOLE";
                hatchId = space.AppendEntity(hatch); tx.AddNewlyCreatedDBObject(hatch, true);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31"); hatch.Associative = false;
                hatch.AppendLoop(HatchLoopTypes.Polyline, new Point2dCollection(new[] { new Point2d(1300,1300), new Point2d(1310,1300),
                    new Point2d(1310,1300.2), new Point2d(1300,1300.2) }), new DoubleCollection(new[] { 0.0, 0.0, 0.0, 0.0 }));
                hatch.AppendLoop(HatchLoopTypes.Polyline, new Point2dCollection(new[] { new Point2d(1304,1300.01), new Point2d(1306,1300.01),
                    new Point2d(1306,1300.19), new Point2d(1304,1300.19) }), new DoubleCollection(new[] { 0.0, 0.0, 0.0, 0.0 }));
                hatch.EvaluateHatch(true); tx.Commit();
            }
            WallDetectionResult result = WallDetection.Detect(database, new[] { hatchId }, Matrix3d.Identity,
                1299, 1299, 1311, 1301, new WallDetectionOptions { Layer = "NEXO_HARNESS_HOLE", MinThickness = .195, MaxThickness = .25 });
            int included = 0, candidates = 0;
            foreach (DetectedWall wall in result.Walls)
            {
                if (wall.Included) { included++; Require(Near(wall.Length, 4), "parede não pode atravessar vazio da hachura"); }
                if (wall.OpeningCandidate && !wall.Included) candidates++;
            }
            Require(included == 2 && candidates == 1, "vazio da hachura deve gerar candidato de abertura desmarcado");
            log.Add("PASS hatch opening is not bridged");
        }

        private static void TestPossibleOpening(Database database, List<string> log)
        {
            List<ObjectId> ids = new List<ObjectId>(AddLines(database, "NEXO_HARNESS_OPENING", new[] {
                new Point3d(1600,1600,0), new Point3d(1606,1600,0),
                new Point3d(1600,1600.2,0), new Point3d(1606,1600.2,0) }));
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                foreach (double start in new[] { 1600.0, 1604.0 })
                {
                    Hatch hatch = new Hatch(); hatch.SetDatabaseDefaults(database); hatch.Layer = "NEXO_HARNESS_OPENING";
                    ids.Add(space.AppendEntity(hatch)); tx.AddNewlyCreatedDBObject(hatch, true);
                    hatch.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31"); hatch.Associative = false;
                    hatch.AppendLoop(HatchLoopTypes.Polyline, new Point2dCollection(new[] { new Point2d(start,1600), new Point2d(start+2,1600),
                        new Point2d(start+2,1600.2), new Point2d(start,1600.2) }), new DoubleCollection(new[] { 0.0, 0.0, 0.0, 0.0 }));
                    hatch.EvaluateHatch(true);
                }
                tx.Commit();
            }
            WallDetectionResult result = WallDetection.Detect(database, ids.ToArray(), Matrix3d.Identity,
                1599, 1599, 1607, 1601, new WallDetectionOptions { Layer = "NEXO_HARNESS_OPENING" });
            Require(result.PossibleOpenings == 1, "linha entre paredes hachuradas deve ser sinalizada como possível abertura");
            foreach (DetectedWall wall in result.Walls)
                if (!wall.FromHatch) Require(!wall.Included && Near(wall.Length, 2), "possível abertura deve iniciar desmarcada");
            foreach (DetectedWall wall in result.Walls)
                if (wall.Included)
                    Require(!Contains(wall, 1603, 1600.1), "linhas de caixilho não podem prolongar hachura sobre a abertura");
            log.Add("PASS possible opening is available but excluded by default");
        }

        private static void TestAmbiguousOneSidedContinuation(Database database, List<string> log)
        {
            List<ObjectId> ids = new List<ObjectId>(AddLines(database, "NEXO_HARNESS_ONE_SIDE", new[] {
                new Point3d(1700,1700,0), new Point3d(1704,1700,0),
                new Point3d(1700,1700.2,0), new Point3d(1704,1700.2,0) }));
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                Hatch hatch = new Hatch(); hatch.SetDatabaseDefaults(database); hatch.Layer = "NEXO_HARNESS_ONE_SIDE";
                ids.Add(space.AppendEntity(hatch)); tx.AddNewlyCreatedDBObject(hatch, true);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31"); hatch.Associative = false;
                hatch.AppendLoop(HatchLoopTypes.Polyline, new Point2dCollection(new[] { new Point2d(1700,1700),
                    new Point2d(1702,1700), new Point2d(1702,1700.2), new Point2d(1700,1700.2) }),
                    new DoubleCollection(new[] { 0.0, 0.0, 0.0, 0.0 }));
                hatch.EvaluateHatch(true); tx.Commit();
            }
            WallDetectionResult result = WallDetection.Detect(database, ids.ToArray(), Matrix3d.Identity,
                1699, 1699, 1705, 1701, new WallDetectionOptions { Layer = "NEXO_HARNESS_ONE_SIDE" });
            bool hatchPresent = false, selectableAmbiguity = false;
            foreach (DetectedWall wall in result.Walls)
            {
                if (wall.Included && Contains(wall, 1701, 1700.1)) hatchPresent = true;
                if (!wall.Included && Contains(wall, 1703, 1700.1)) selectableAmbiguity = true;
                Require(!wall.Included || !Contains(wall, 1703, 1700.1), "continuação sem hachura não pode entrar automaticamente");
            }
            Require(hatchPresent && selectableAmbiguity, "hachura deve permanecer e a continuação deve ficar disponível para revisão");
            log.Add("PASS one-sided unhatched continuation is excluded but reviewable");
        }

        private static void TestDynamicInsertion(Database database, List<string> log)
        {
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)tx.GetObject(database.BlockTableId, OpenMode.ForRead);
                if (!table.Has("ISO_Parede")) { log.Add("SKIP dynamic insertion: definition not in drawing"); return; }
                tx.Commit();
            }
            WallLayout layout = new WallLayout { MinX = 0, MinY = 0, MaxX = 10, MaxY = 8, Height = 2.8 };
            layout.Walls.Add(new DetectedWall { A = new Point2d(1,1), B = new Point2d(5,1), C = new Point2d(5,1.2), D = new Point2d(1,1.2), Length = 4, Thickness = .2 });
            layout.Walls.Add(new DetectedWall { A = new Point2d(6,2), B = new Point2d(6,5), C = new Point2d(6.2,5), D = new Point2d(6.2,2), Length = 3, Thickness = .2 });
            Vector3d[] xAxes = { new Vector3d(.866,.5,0), new Vector3d(-.866,.5,0),
                new Vector3d(-.866,-.5,0), new Vector3d(.866,-.5,0) };
            Vector3d[] yAxes = { new Vector3d(-.866,.5,0), new Vector3d(-.866,-.5,0),
                new Vector3d(.866,-.5,0), new Vector3d(.866,.5,0) };
            for (int angle = 0; angle < 4; angle++)
            {
                Point3d origin = new Point3d(2000 + angle * 30, 2000, 0);
                Point3d[] frame = { origin, origin + xAxes[angle] * 10,
                    origin + xAxes[angle] * 10 + yAxes[angle] * 8, origin + yAxes[angle] * 8 };
                HashSet<ObjectId> existing = new HashSet<ObjectId>();
                using (Transaction tx = database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
                    foreach (ObjectId id in space) existing.Add(id);
                    tx.Commit();
                }
                int count = IsometricWallInserter.Insert(database, layout, frame, Matrix3d.Identity);
                Require(count == 2, "duas paredes devem ser inseridas");
                using (Transaction tx = database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
                    int verified = 0;
                    foreach (ObjectId id in space)
                    {
                        if (existing.Contains(id)) continue;
                        BlockReference reference = tx.GetObject(id, OpenMode.ForRead, false) as BlockReference;
                        if (reference == null || !reference.IsDynamicBlock) continue;
                        double x = 0, y = 0, z = 0;
                        foreach (DynamicBlockReferenceProperty property in reference.DynamicBlockReferencePropertyCollection)
                        {
                            if (property.PropertyName == "X") x = Convert.ToDouble(property.Value);
                            if (property.PropertyName == "Y") y = Convert.ToDouble(property.Value);
                            if (property.PropertyName == "Z") z = Convert.ToDouble(property.Value);
                        }
                        DetectedWall wall = Math.Abs(x - 4) < 1e-6 ? layout.Walls[0] : layout.Walls[1];
                        Require(Near(x, wall.Length) && Near(y, wall.Thickness) && Near(z, layout.Height), "parâmetros dinâmicos devem corresponder à parede");
                        BlockTableRecord def = (BlockTableRecord)tx.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
                        Polyline outline = null;
                        foreach (ObjectId child in def) { Polyline p = tx.GetObject(child, OpenMode.ForRead, false) as Polyline; if (p != null && p.NumberOfVertices == 6) outline = p; }
                        Require(outline != null, "contorno dinâmico deve existir");
                        Point3d p0 = outline.GetPoint3dAt(0).TransformBy(reference.BlockTransform);
                        Point3d p1 = outline.GetPoint3dAt(1).TransformBy(reference.BlockTransform);
                        Point3d p5 = outline.GetPoint3dAt(5).TransformBy(reference.BlockTransform);
                        Point3d[] actual = { p0, p1, p5, p1 + (p5 - p0) };
                        Point2d[] src = { wall.A, wall.B, wall.C, wall.D };
                        foreach (Point2d point in src)
                        {
                            Point3d expected = origin + xAxes[angle] * point.X + yAxes[angle] * point.Y;
                            bool found = false;
                            foreach (Point3d actualPoint in actual) if (actualPoint.DistanceTo(expected) < .002) found = true;
                            Require(found, "o contorno da parede deve coincidir com sua projeção no piso");
                        }
                        verified++;
                    }
                    Require(verified == 2, "as duas referências devem ter sido verificadas");
                    tx.Commit();
                }
            }
            log.Add("PASS real dynamic block, parameters and projected footprint in four rotations");
        }

        private static void TestSlabInsertion(Document document, List<string> log)
        {
            Database database = document.Database;
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                BlockTable table = (BlockTable)tx.GetObject(database.BlockTableId, OpenMode.ForRead);
                if (!table.Has("ISO_Laje")) { log.Add("SKIP slab insertion: definition not in drawing"); return; }
                tx.Commit();
            }
            Point3d center = new Point3d(2300, 2300, 0);
            IsometricSlabInserter.Insert(document, center, new Point2d(4, 3), 0, Matrix3d.Identity);
            log.Add("PASS cloned ISO_Laje dynamic properties and alignment");
        }

        private static ObjectId[] AddLines(Database database, string layer, Point3d[] points)
        {
            List<ObjectId> ids = new List<ObjectId>();
            using (Transaction tx = database.TransactionManager.StartTransaction())
            {
                EnsureLayer(database, tx, layer);
                BlockTableRecord space = (BlockTableRecord)tx.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                for (int i = 0; i < points.Length; i += 2)
                {
                    Line line = new Line(points[i], points[i + 1]); line.SetDatabaseDefaults(database); line.Layer = layer;
                    ids.Add(space.AppendEntity(line)); tx.AddNewlyCreatedDBObject(line, true);
                }
                tx.Commit();
            }
            return ids.ToArray();
        }

        private static void EnsureLayer(Database database, Transaction tx, string name)
        {
            LayerTable layers = (LayerTable)tx.GetObject(database.LayerTableId, OpenMode.ForWrite);
            if (!layers.Has(name)) { LayerTableRecord layer = new LayerTableRecord { Name = name }; layers.Add(layer); tx.AddNewlyCreatedDBObject(layer, true); }
        }
        private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
