using System;

using System.Collections;

using System.Collections.Generic;

using System.Globalization;

using System.IO;

using System.Reflection;

using Autodesk.AutoCAD.ApplicationServices;

using Autodesk.AutoCAD.DatabaseServices;

using Autodesk.AutoCAD.Geometry;

using Autodesk.AutoCAD.Runtime;



[assembly: CommandClass(typeof(Nexo.WallHarness.IsometricIntegrationHarness))]

namespace Nexo.WallHarness

{

    public sealed class IsometricIntegrationHarness

    {

        [CommandMethod("NEXO_ISO_INTEGRATION")]

        public void Run()

        {

            string logPath = Environment.GetEnvironmentVariable("NEXO_ISO_INTEGRATION_LOG");

            List<string> log = new List<string>();

            try

            {

                Document doc = Application.DocumentManager.MdiActiveDocument;

                Database db = doc.Database;

                string pluginPath = Environment.GetEnvironmentVariable("NEXO_INTEGRATION_DLL");

                string libraryPath = Environment.GetEnvironmentVariable("NEXO_TEST_BLOCK_LIBRARY");

                Nexo.AutoCAD.WallBlockLibrary.ImportMissing(db, libraryPath);
                string openingLibrary = Environment.GetEnvironmentVariable("NEXO_TEST_OPENING_LIBRARY");
                if (!string.IsNullOrWhiteSpace(openingLibrary))
                    Nexo.AutoCAD.WallBlockLibrary.ImportMissing(db, openingLibrary, new[] { "ISO_Porta2", "ISO_Janela" });

                Assembly plugin = Assembly.LoadFrom(pluginPath);

                Type commands = plugin.GetType("Nexo.AutoCAD.IsometricBlockCommands", true);

                Type faceType = plugin.GetType("Nexo.AutoCAD.IsometricFace", true);

                Type orientationType = plugin.GetType("Nexo.AutoCAD.IsometricSelection", true);

                Type layoutType = plugin.GetType("Nexo.AutoCAD.WallLayout", true);

                Type wallType = plugin.GetType("Nexo.AutoCAD.DetectedWall", true);
                Type openingKind = plugin.GetType("Nexo.AutoCAD.OpeningKind", true);

                MethodInfo workflow = commands.GetMethod("RunIsometricWorkflow", BindingFlags.NonPublic | BindingFlags.Static);

                MethodInfo frameMethod = commands.GetMethod("GetTransformedDetailFrameCorners", BindingFlags.NonPublic | BindingFlags.Static);

                object previousAngleBase = Application.GetSystemVariable("ANGBASE");

                Application.SetSystemVariable("ANGBASE", Math.PI / 2);

                try

                {

                    for (int rotation = 0; rotation < 360; rotation += 90)

                    {

                        double x = 3100 + rotation, y = 3100;

                        ObjectId[] selection = CreateFixture(db, x, y);

                        HashSet<ObjectId> before = GetCurrentIds(db);

                        object orientation = Activator.CreateInstance(orientationType, BindingFlags.NonPublic | BindingFlags.Instance,

                            null, new object[] { Enum.ToObject(faceType, 0), rotation }, CultureInfo.InvariantCulture);

                        object layout = Activator.CreateInstance(layoutType, true);

                        Set(layout, "MinX", x); Set(layout, "MinY", y); Set(layout, "MaxX", x + 10); Set(layout, "MaxY", y + 8); Set(layout, "Height", 2.8);

                        IList walls = (IList)layoutType.GetField("Walls", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(layout);

                        walls.Add(CreateWall(wallType, x, y, true)); walls.Add(CreateWall(wallType, x, y, false));
                        if (!string.IsNullOrWhiteSpace(openingLibrary))
                        {
                            IList openings = (IList)layoutType.GetField("Openings", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(layout);
                            object door = CreateWall(wallType, x, y, true);
                            Set(door, "Opening", Enum.ToObject(openingKind, 1)); Set(door, "OpeningWidth", .9);
                            Set(door, "OpeningHeight", 2.1); Set(door, "OpeningSill", 0.0);
                            object window = CreateWall(wallType, x, y, false);
                            Set(window, "Opening", Enum.ToObject(openingKind, 2)); Set(window, "OpeningWidth", 1.2);
                            Set(window, "OpeningHeight", 1.1); Set(window, "OpeningSill", 1.0);
                            openings.Add(door); openings.Add(window);
                        }

                        Point3d target = new Point3d(4000 + rotation, 4000, 0);

                        try

                        {

                            workflow.Invoke(null, new object[] { doc, selection, orientation, false,

                                new Point3d(x + 5, y + 4, 0), new Point2d(10, 8), target, layout });

                        }

                        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }



                        ObjectId output = ObjectId.Null, slabId = ObjectId.Null;

                        List<ObjectId> wallIds = new List<ObjectId>();

                        int wallCount = 0, slabCount = 0, openingsCount = 0;

                        using (Transaction tx = db.TransactionManager.StartTransaction())

                        {

                            BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

                            foreach (ObjectId id in space)

                            {

                                if (before.Contains(id) || id.IsErased) continue;

                                BlockReference reference = tx.GetObject(id, OpenMode.ForRead, false) as BlockReference;

                                if (reference == null) continue;

                                ObjectId defId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;

                                BlockTableRecord definition = tx.GetObject(defId, OpenMode.ForRead, false) as BlockTableRecord;

                                string name = definition == null ? string.Empty : definition.Name;

                                if (name.StartsWith("NEXO_PAREDES_", StringComparison.OrdinalIgnoreCase)) { wallCount++; wallIds.Add(id); }

                                else if (name.Equals("ISO_Laje", StringComparison.OrdinalIgnoreCase)) { slabCount++; slabId = id; }
                                else if (name.Equals("ISO_Porta2", StringComparison.OrdinalIgnoreCase) || name.Equals("ISO_Janela", StringComparison.OrdinalIgnoreCase)) openingsCount++;

                                else if (name.StartsWith("RndBlk-", StringComparison.OrdinalIgnoreCase)) output = id;

                            }

                            tx.Commit();

                        }

                        Require(!output.IsNull && wallCount == 1 && slabCount == 1,

                            "saída deve conter detalhe, uma laje e um conjunto de paredes; giro=" + rotation + ", paredes=" + wallCount + ", lajes=" + slabCount);
                        if (!string.IsNullOrWhiteSpace(openingLibrary)) Require(openingsCount == 2, "porta e janela devem ser criadas no fluxo integrado");

                        Point3d[] frame = (Point3d[])frameMethod.Invoke(null, new object[] { db, output });

                        Point3d center = new Point3d((frame[0].X + frame[1].X + frame[2].X + frame[3].X) / 4,

                            (frame[0].Y + frame[1].Y + frame[2].Y + frame[3].Y) / 4,

                            (frame[0].Z + frame[1].Z + frame[2].Z + frame[3].Z) / 4);

                        Require(center.DistanceTo(target) < 1e-6, "centro do detalhe deve coincidir com clique; giro=" + rotation);

                        VerifySlabAndWalls(db, slabId, wallIds, frame, x, y, center);

                        log.Add("PASS complete isometric workflow, ANGBASE 90, rotation " + rotation);

                    }

                }

                finally { Application.SetSystemVariable("ANGBASE", previousAngleBase); }

                log.Add("INTEGRATION PASS");

            }

            catch (System.Exception exception) { log.Add("INTEGRATION FAIL: " + exception); }

            if (!string.IsNullOrWhiteSpace(logPath)) File.WriteAllLines(logPath, log);

            foreach (string line in log) Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\n" + line);

        }



        private static void VerifySlabAndWalls(Database db, ObjectId slabId, List<ObjectId> wallIds,

            Point3d[] frame, double x, double y, Point3d targetCenter)

        {

            using (Transaction tx = db.TransactionManager.StartTransaction())

            {

                BlockReference slab = (BlockReference)tx.GetObject(slabId, OpenMode.ForRead);

                Polyline slabOutline = FindOutline(tx, slab);

                Point3d? apex = null;

                for (int i = 0; i < slabOutline.NumberOfVertices; i++)

                {

                    Point3d local = slabOutline.GetPoint3dAt(i);

                    if (!apex.HasValue || local.Y > apex.Value.Y) apex = local;

                }

                Point3d front = Point3d.Origin.TransformBy(slab.BlockTransform);

                Point3d back = apex.Value.TransformBy(slab.BlockTransform);

                Point3d slabCenter = new Point3d((front.X + back.X) / 2, (front.Y + back.Y) / 2, (front.Z + back.Z) / 2);

                Require(slabCenter.DistanceTo(targetCenter) < 1e-5, "centro da laje deve coincidir com moldura");



                BlockReference walls = (BlockReference)tx.GetObject(wallIds[0], OpenMode.ForRead);

                BlockTableRecord wallDefinition = (BlockTableRecord)tx.GetObject(walls.BlockTableRecord, OpenMode.ForRead);

                var lines = new List<Line>(); int masks = 0;

                foreach (ObjectId id in wallDefinition)

                {

                    Entity entity = tx.GetObject(id, OpenMode.ForRead, false) as Entity;

                    if (entity is Line line) lines.Add(line);

                    if (entity is Wipeout) masks++;

                }

                Require(masks > 0, "paredes devem ocultar a geometria atras delas");

                Point2d[] outer = { new Point2d(x+1,y+1), new Point2d(x+5,y+1),

                    new Point2d(x+5,y+1.2), new Point2d(x+1.2,y+1.2),

                    new Point2d(x+1.2,y+4), new Point2d(x+1,y+4) };

                for (int k = 0; k < outer.Length; k++)

                {

                    Point2d a = outer[k], b = outer[(k+1)%outer.Length];

                    Point3d pa = frame[0] + (frame[1]-frame[0])*((a.X-x)/10) + (frame[3]-frame[0])*((a.Y-y)/8) + new Vector3d(0,2.8,0);

                    Point3d pb = frame[0] + (frame[1]-frame[0])*((b.X-x)/10) + (frame[3]-frame[0])*((b.Y-y)/8) + new Vector3d(0,2.8,0);

                    bool found = false;

                    foreach (Line line in lines)

                    {

                        Point3d p = line.StartPoint.TransformBy(walls.BlockTransform), q = line.EndPoint.TransformBy(walls.BlockTransform);

                        if ((p.DistanceTo(pa)<.002 && q.DistanceTo(pb)<.002) || (q.DistanceTo(pa)<.002 && p.DistanceTo(pb)<.002)) found = true;

                    }

                    Require(found, "o contorno superior unico deve coincidir com a uniao projetada");

                }

                tx.Commit();

            }

        }



        private static Polyline FindOutline(Transaction tx, BlockReference reference)

        {

            BlockTableRecord definition = (BlockTableRecord)tx.GetObject(reference.BlockTableRecord, OpenMode.ForRead);

            foreach (ObjectId id in definition)

            {

                Polyline polyline = tx.GetObject(id, OpenMode.ForRead, false) as Polyline;

                if (polyline != null && polyline.NumberOfVertices == 6) return polyline;

            }

            throw new InvalidOperationException("contorno dinâmico não localizado");

        }



        private static ObjectId[] CreateFixture(Database db, double x, double y)

        {

            List<ObjectId> ids = new List<ObjectId>();

            using (Transaction tx = db.TransactionManager.StartTransaction())

            {

                LayerTable layers = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForWrite);

                if (!layers.Has("PP_REGI\u00c3O")) { LayerTableRecord layer = new LayerTableRecord { Name = "PP_REGI\u00c3O" }; layers.Add(layer); tx.AddNewlyCreatedDBObject(layer, true); }

                BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                Polyline frame = new Polyline(4); frame.SetDatabaseDefaults(db); frame.Layer = "PP_REGI\u00c3O";

                frame.AddVertexAt(0, new Point2d(x,y),0,0,0); frame.AddVertexAt(1, new Point2d(x+10,y),0,0,0);

                frame.AddVertexAt(2, new Point2d(x+10,y+8),0,0,0); frame.AddVertexAt(3, new Point2d(x,y+8),0,0,0); frame.Closed = true;

                ids.Add(space.AppendEntity(frame)); tx.AddNewlyCreatedDBObject(frame, true);

                Line line = new Line(new Point3d(x+1,y+1,0), new Point3d(x+3,y+1,0)); line.SetDatabaseDefaults(db);

                ids.Add(space.AppendEntity(line)); tx.AddNewlyCreatedDBObject(line, true); tx.Commit();

            }

            return ids.ToArray();

        }



        private static HashSet<ObjectId> GetCurrentIds(Database db)

        {

            HashSet<ObjectId> ids = new HashSet<ObjectId>();

            using (Transaction tx = db.TransactionManager.StartTransaction())

            {

                BlockTableRecord space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

                foreach (ObjectId id in space) ids.Add(id);

                tx.Commit();

            }

            return ids;

        }



        private static object CreateWall(Type type, double x, double y, bool horizontal)

        {

            object wall = Activator.CreateInstance(type, true);

            Set(wall, "A", new Point2d(x+1, y+1));

            Set(wall, "B", horizontal ? new Point2d(x+5, y+1) : new Point2d(x+1, y+4));

            Set(wall, "C", horizontal ? new Point2d(x+5, y+1.2) : new Point2d(x+1.2, y+4));

            Set(wall, "D", horizontal ? new Point2d(x+1, y+1.2) : new Point2d(x+1.2, y+1));

            Set(wall, "Length", horizontal ? 4.0 : 3.0); Set(wall, "Thickness", 0.2);

            return wall;

        }



        private static void Set(object instance, string field, object value)

        {

            instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value);

        }

        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    }

}

