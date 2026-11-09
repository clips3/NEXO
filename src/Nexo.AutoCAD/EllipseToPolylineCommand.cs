using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace Nexo.AutoCAD
{
    public sealed class EllipseToPolylineCommand
    {
        private const double TwoPi = Math.PI * 2.0;
        private const double PointTolerance = 1e-8;
        private const double ParameterTolerance = 1e-10;

        [NexoRibbonCommand(
            "EL2PL",
            "Conversão",
            "Ellipse to Poly",
            "Converte elipses e arcos elípticos selecionados em polylines leves.",
            "icon_ellipse_to_poly.png")]
        [CommandMethod("EL2PL", CommandFlags.UsePickSet)]
        public void ConvertSelectedEllipses()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                ObjectId[] ellipseIds = GetEllipseSelection(editor);
                if (ellipseIds.Length == 0)
                {
                    editor.WriteMessage("\n[NEXO] Nenhuma elipse selecionada.");
                    return;
                }

                bool deleteOriginals = Convert.ToInt32(Application.GetSystemVariable("DELOBJ")) != 0;
                int convertedCount = 0;
                Database database = document.Database;
                ObjectId currentSpaceId = database.CurrentSpaceId;

                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = transaction.GetObject(currentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                    if (currentSpace == null)
                        throw new InvalidOperationException("Não foi possível acessar o espaço atual do desenho.");

                    foreach (ObjectId ellipseId in ellipseIds)
                    {
                        if (ellipseId.IsNull || !ellipseId.IsValid || ellipseId.IsErased)
                            continue;

                        Ellipse ellipse = transaction.GetObject(ellipseId, OpenMode.ForWrite, false) as Ellipse;
                        if (ellipse == null || ellipse.OwnerId != currentSpaceId)
                            continue;

                        ConvertEllipse(transaction, database, currentSpace, ellipse, deleteOriginals);
                        convertedCount++;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(convertedCount == 0
                    ? "\n[NEXO] Nenhuma elipse pôde ser convertida no espaço atual."
                    : "\n[NEXO] Convertidas " + convertedCount + " elipse(s) em polyline(s)." +
                      (deleteOriginals ? string.Empty : " As elipses originais foram mantidas (DELOBJ=0)."));
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n[NEXO] Falha em EL2PL: " + exception.Message);
            }
        }

        private static ObjectId[] GetEllipseSelection(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null)
            {
                List<ObjectId> impliedEllipses = new List<ObjectId>();
                foreach (ObjectId id in implied.Value.GetObjectIds())
                {
                    if (!id.IsNull && id.IsValid && !id.IsErased && id.ObjectClass != null &&
                        string.Equals(id.ObjectClass.DxfName, "ELLIPSE", StringComparison.OrdinalIgnoreCase))
                    {
                        impliedEllipses.Add(id);
                    }
                }

                if (impliedEllipses.Count > 0)
                    return impliedEllipses.ToArray();
            }

            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "ELLIPSE")
            });
            PromptSelectionResult selection = editor.GetSelection(filter);
            return selection.Status == PromptStatus.OK && selection.Value != null
                ? selection.Value.GetObjectIds()
                : Array.Empty<ObjectId>();
        }

        private static void ConvertEllipse(
            Transaction transaction,
            Database database,
            BlockTableRecord currentSpace,
            Ellipse ellipse,
            bool deleteOriginal)
        {
            Matrix3d worldToOcs = Matrix3d.WorldToPlane(ellipse.Normal);
            Point3d centerOcs = ellipse.Center.TransformBy(worldToOcs);
            double elevation = centerOcs.Z;
            Point2d center = new Point2d(centerOcs.X, centerOcs.Y);
            EllipseApproximation approximation = BuildApproximation(ellipse, worldToOcs, center);
            bool closed = IsFullEllipse(ellipse);

            if (closed)
            {
                approximation.Points.RemoveAt(0);
                approximation.Points.Reverse();
                AppendPolyline(transaction, database, currentSpace, approximation.Points,
                    approximation.Bulges, true, ellipse.Normal, elevation);
            }
            else
            {
                approximation.Points.Reverse();
                Polyline guide = AppendPolyline(transaction, database, currentSpace, approximation.Points,
                    approximation.Bulges, false, ellipse.Normal, elevation);
                ClippedPath clipped = ClipToEllipseArc(ellipse, guide, approximation, worldToOcs);
                guide.Erase();

                if (clipped.Points.Count < 2)
                    throw new InvalidOperationException("O arco elíptico selecionado não gerou segmentos válidos.");

                AppendPolyline(transaction, database, currentSpace, clipped.Points,
                    clipped.Bulges, false, ellipse.Normal, elevation);
            }

            if (deleteOriginal)
                ellipse.Erase();
        }

        private static EllipseApproximation BuildApproximation(Ellipse ellipse, Matrix3d worldToOcs, Point2d center)
        {
            Vector3d majorAxis = ellipse.MajorAxis.TransformBy(worldToOcs);
            Vector3d normal = ellipse.Normal.TransformBy(worldToOcs);
            Vector3d minorAxis = normal.CrossProduct(majorAxis).MultiplyBy(ellipse.RadiusRatio);
            Point2d point0 = new Point2d(center.X + majorAxis.X, center.Y + majorAxis.Y);
            Point3d point2World = ellipse.GetPointAtParameter(Math.PI / 4.0);
            Point3d point2Ocs = point2World.TransformBy(worldToOcs);
            Point2d point2 = new Point2d(point2Ocs.X, point2Ocs.Y);
            Point2d point4 = new Point2d(center.X + minorAxis.X, center.Y + minorAxis.Y);

            double angle0 = Angle(center, point0);
            double angle4 = Angle(center, point4);
            double angle04 = Angle(point0, point4);
            double angle02 = Angle(point0, point2);
            double angle24 = Angle(point2, point4);
            double bulgeScale1 = NormalizeAngle(angle02 - angle4) / 2.0;
            double bulgeScale2 = NormalizeAngle(angle04 - angle02) / 2.0;
            double bulgeScale3 = NormalizeAngle(angle24 - angle04) / 2.0;
            double bulgeScale4 = NormalizeAngle((angle0 + Math.PI) - angle24) / 2.0;

            Point2d point1 = IntersectLines(
                point0,
                Polar(point0, angle0 + Math.PI / 2.0 + bulgeScale1, 1.0),
                point2,
                Polar(point2, angle02 + bulgeScale2, 1.0),
                ellipse,
                worldToOcs,
                Math.PI / 8.0);
            Point2d point3 = IntersectLines(
                point2,
                Polar(point2, angle04 + bulgeScale3, 1.0),
                point4,
                Polar(point4, angle24 + bulgeScale4, 1.0),
                ellipse,
                worldToOcs,
                3.0 * Math.PI / 8.0);

            List<Point2d> points = new List<Point2d> { point4, point3, point2, point1, point0 };
            ReflectTailAcrossAxis(points, center, angle4);
            ReflectTailAcrossAxis(points, center, angle0);

            List<double> bulges = new List<double>
            {
                Math.Tan(bulgeScale4 / 2.0),
                Math.Tan(bulgeScale3 / 2.0),
                Math.Tan(bulgeScale2 / 2.0),
                Math.Tan(bulgeScale1 / 2.0)
            };
            for (int repeat = 0; repeat < 2; repeat++)
            {
                double[] existing = bulges.ToArray();
                foreach (double bulge in existing)
                    bulges.Insert(0, bulge);
            }

            return new EllipseApproximation(points, bulges);
        }

        private static void ReflectTailAcrossAxis(List<Point2d> points, Point2d center, double axisAngle)
        {
            Point2d[] originalTail = points.GetRange(1, points.Count - 1).ToArray();
            foreach (Point2d point in originalTail)
                points.Insert(0, ReflectAcrossAxis(center, point, axisAngle));
        }

        private static Point2d ReflectAcrossAxis(Point2d center, Point2d point, double axisAngle)
        {
            double angle = Angle(center, point);
            double reflectedAngle = (2.0 * axisAngle) - angle;
            return Polar(center, reflectedAngle, center.GetDistanceTo(point));
        }

        private static Point2d IntersectLines(
            Point2d firstStart,
            Point2d firstSecond,
            Point2d secondStart,
            Point2d secondSecond,
            Ellipse ellipse,
            Matrix3d worldToOcs,
            double fallbackParameter)
        {
            double firstX = firstSecond.X - firstStart.X;
            double firstY = firstSecond.Y - firstStart.Y;
            double secondX = secondSecond.X - secondStart.X;
            double secondY = secondSecond.Y - secondStart.Y;
            double denominator = Cross(firstX, firstY, secondX, secondY);

            if (Math.Abs(denominator) < 1e-12)
                return ToOcs2d(ellipse.GetPointAtParameter(fallbackParameter), worldToOcs);

            double offsetX = secondStart.X - firstStart.X;
            double offsetY = secondStart.Y - firstStart.Y;
            double firstRatio = Cross(offsetX, offsetY, secondX, secondY) / denominator;
            return new Point2d(firstStart.X + firstRatio * firstX, firstStart.Y + firstRatio * firstY);
        }

        private static ClippedPath ClipToEllipseArc(
            Ellipse ellipse,
            Polyline guide,
            EllipseApproximation approximation,
            Matrix3d worldToOcs)
        {
            Point3d startPoint = guide.GetClosestPointTo(ellipse.StartPoint, false);
            Point3d endPoint = guide.GetClosestPointTo(ellipse.EndPoint, false);
            double startParameter = guide.GetParameterAtPoint(startPoint);
            double endParameter = guide.GetParameterAtPoint(endPoint);
            bool startAtSeam = IsNear(startPoint, guide.StartPoint) || IsNear(startPoint, guide.EndPoint);
            bool endAtSeam = IsNear(endPoint, guide.StartPoint) || IsNear(endPoint, guide.EndPoint);

            if (startAtSeam)
                startParameter = 0.0;
            if (endAtSeam)
                endParameter = 0.0;

            int startIndex = Math.Max(0, Math.Min(approximation.Bulges.Count - 1, (int)Math.Floor(startParameter)));
            int endIndex = Math.Max(0, Math.Min(approximation.Bulges.Count - 1, (int)Math.Floor(endParameter)));
            bool startIsVertex = IsIntegerParameter(startParameter);
            bool endIsVertex = IsIntegerParameter(endParameter);
            Point2d startOcs = ToOcs2d(startPoint, worldToOcs);
            Point2d endOcs = ToOcs2d(endPoint, worldToOcs);

            if (startAtSeam)
            {
                if (endIsVertex && !endAtSeam)
                {
                    return new ClippedPath(
                        Slice(approximation.Points, 0, 1 + endIndex),
                        Slice(approximation.Bulges, 0, 1 + endIndex));
                }

                if (!endAtSeam)
                {
                    double endRatio = EndBulgeRatio(guide, endParameter, endIndex);
                    List<Point2d> points = Slice(approximation.Points, 0, 1 + endIndex);
                    points.Add(endOcs);
                    List<double> bulges = Slice(approximation.Bulges, 0, 1 + endIndex);
                    bulges.Add(ScaleBulge(approximation.Bulges[endIndex], endRatio));
                    return new ClippedPath(points, bulges);
                }
            }

            if (endAtSeam)
            {
                if (startIsVertex)
                {
                    return new ClippedPath(
                        Slice(approximation.Points, startIndex, null),
                        Slice(approximation.Bulges, startIndex, null));
                }

                double startRatio = StartBulgeRatio(guide, startParameter, startIndex);
                List<Point2d> points = new List<Point2d> { startOcs };
                points.AddRange(Slice(approximation.Points, startIndex + 1, null));
                List<double> bulges = new List<double> { ScaleBulge(approximation.Bulges[startIndex], startRatio) };
                bulges.AddRange(Slice(approximation.Bulges, startIndex + 1, null));
                return new ClippedPath(points, bulges);
            }

            double startFraction = startIsVertex ? 1.0 : StartBulgeRatio(guide, startParameter, startIndex);
            double endFraction = endIsVertex ? 1.0 : EndBulgeRatio(guide, endParameter, endIndex);

            if (endParameter < startParameter)
            {
                List<Point2d> points = startIsVertex
                    ? Slice(approximation.Points, startIndex, null)
                    : PrefixWithPoint(startOcs, Slice(approximation.Points, startIndex + 1, null));
                List<Point2d> firstPart = Slice(approximation.Points, 0, 1 + endIndex);
                if (firstPart.Count > 1)
                    points.AddRange(firstPart.GetRange(1, firstPart.Count - 1));
                if (!endIsVertex)
                    points.Add(endOcs);

                List<double> bulges = startIsVertex
                    ? Slice(approximation.Bulges, startIndex, null)
                    : PrefixWithValue(ScaleBulge(approximation.Bulges[startIndex], startFraction),
                        Slice(approximation.Bulges, startIndex + 1, null));
                bulges.AddRange(Slice(approximation.Bulges, 0, endIndex));
                bulges.Add(endIsVertex
                    ? approximation.Bulges[endIndex]
                    : ScaleBulge(approximation.Bulges[endIndex], endFraction));
                return new ClippedPath(points, bulges);
            }

            List<Point2d> forwardPoints = startIsVertex
                ? Slice(approximation.Points, startIndex, Math.Max(1, endIndex - startIndex + 1))
                : PrefixWithPoint(startOcs,
                    Slice(approximation.Points, startIndex + 1, Math.Max(0, endIndex - startIndex)));
            forwardPoints.Add(endOcs);

            List<double> forwardBulges = startIsVertex
                ? Slice(approximation.Bulges, startIndex, Math.Max(0, endIndex - startIndex))
                : PrefixWithValue(ScaleBulge(approximation.Bulges[startIndex], startFraction),
                    Slice(approximation.Bulges, startIndex + 1, Math.Max(0, endIndex - startIndex)));
            forwardBulges.Add(endIsVertex
                ? approximation.Bulges[endIndex]
                : ScaleBulge(approximation.Bulges[endIndex], endFraction));
            return new ClippedPath(forwardPoints, forwardBulges);
        }

        private static double StartBulgeRatio(Polyline guide, double parameter, int index)
        {
            double segmentStart = guide.GetDistanceAtParameter(index);
            double segmentEnd = guide.GetDistanceAtParameter(index + 1.0);
            double pointDistance = guide.GetDistanceAtParameter(parameter);
            double length = segmentEnd - segmentStart;
            return length <= 1e-12 ? 1.0 : ClampRatio((segmentEnd - pointDistance) / length);
        }

        private static double EndBulgeRatio(Polyline guide, double parameter, int index)
        {
            double segmentStart = guide.GetDistanceAtParameter(index);
            double segmentEnd = guide.GetDistanceAtParameter(index + 1.0);
            double pointDistance = guide.GetDistanceAtParameter(parameter);
            double length = segmentEnd - segmentStart;
            return length <= 1e-12 ? 0.0 : ClampRatio((pointDistance - segmentStart) / length);
        }

        private static double ScaleBulge(double bulge, double ratio)
        {
            return Math.Tan(Math.Atan(bulge) * ratio);
        }

        private static Polyline AppendPolyline(
            Transaction transaction,
            Database database,
            BlockTableRecord currentSpace,
            IList<Point2d> points,
            IList<double> bulges,
            bool closed,
            Vector3d normal,
            double elevation)
        {
            if (points == null || points.Count < 2)
                throw new InvalidOperationException("A conversão não produziu vértices suficientes para uma polyline.");

            Polyline polyline = new Polyline(points.Count);
            polyline.SetDatabaseDefaults(database);
            polyline.Normal = normal;
            polyline.Elevation = elevation;
            for (int index = 0; index < points.Count; index++)
            {
                double bulge = bulges != null && index < bulges.Count ? bulges[index] : 0.0;
                polyline.AddVertexAt(index, points[index], bulge, 0.0, 0.0);
            }
            polyline.Closed = closed;

            currentSpace.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            return polyline;
        }

        private static bool IsFullEllipse(Ellipse ellipse)
        {
            return Math.Abs(ellipse.StartParam) <= ParameterTolerance &&
                   Math.Abs(ellipse.EndParam - TwoPi) <= ParameterTolerance;
        }

        private static bool IsIntegerParameter(double parameter)
        {
            return Math.Abs(parameter - Math.Round(parameter)) <= ParameterTolerance;
        }

        private static bool IsNear(Point3d first, Point3d second)
        {
            return first.DistanceTo(second) <= PointTolerance;
        }

        private static Point2d ToOcs2d(Point3d point, Matrix3d worldToOcs)
        {
            Point3d ocsPoint = point.TransformBy(worldToOcs);
            return new Point2d(ocsPoint.X, ocsPoint.Y);
        }

        private static Point2d Polar(Point2d origin, double angle, double distance)
        {
            return new Point2d(origin.X + Math.Cos(angle) * distance, origin.Y + Math.Sin(angle) * distance);
        }

        private static double Angle(Point2d from, Point2d to)
        {
            return NormalizeAngle(Math.Atan2(to.Y - from.Y, to.X - from.X));
        }

        private static double NormalizeAngle(double angle)
        {
            angle %= TwoPi;
            return angle < 0.0 ? angle + TwoPi : angle;
        }

        private static double Cross(double firstX, double firstY, double secondX, double secondY)
        {
            return firstX * secondY - firstY * secondX;
        }

        private static double ClampRatio(double ratio)
        {
            return Math.Max(0.0, Math.Min(1.0, ratio));
        }

        private static List<T> Slice<T>(IList<T> source, int start, int? count)
        {
            List<T> result = new List<T>();
            if (source == null || start < 0 || start >= source.Count)
                return result;

            int available = source.Count - start;
            int length = count.HasValue ? Math.Max(0, Math.Min(available, count.Value)) : available;
            for (int index = 0; index < length; index++)
                result.Add(source[start + index]);
            return result;
        }

        private static List<T> PrefixWithPoint<T>(T first, List<T> remainder)
        {
            List<T> result = new List<T> { first };
            result.AddRange(remainder);
            return result;
        }

        private static List<T> PrefixWithValue<T>(T first, List<T> remainder)
        {
            List<T> result = new List<T> { first };
            result.AddRange(remainder);
            return result;
        }

        private sealed class EllipseApproximation
        {
            internal EllipseApproximation(List<Point2d> points, List<double> bulges)
            {
                Points = points;
                Bulges = bulges;
            }

            internal List<Point2d> Points { get; }
            internal List<double> Bulges { get; }
        }

        private sealed class ClippedPath
        {
            internal ClippedPath(List<Point2d> points, List<double> bulges)
            {
                Points = points;
                Bulges = bulges;
            }

            internal List<Point2d> Points { get; }
            internal List<double> Bulges { get; }
        }
    }
}
