using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Nexo.AutoCAD;

namespace Nexo.PreviewHarness
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string file = args[0]; bool light = args.Length < 2 || args[1] == "light";
            Application app = new Application();
            WallPreviewDialog dialog = new WallPreviewDialog(null, Array.Empty<ObjectId>(), new Matrix3d(), 0, 0, 10, 8, light)
            {
                ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
            };
            dialog.Loaded += (sender, eventArgs) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                dialog.UpdateLayout();
                int width = (int)Math.Ceiling(dialog.ActualWidth);
                int height = (int)Math.Ceiling(dialog.ActualHeight);
                RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(dialog);
                PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream output = File.Create(file)) encoder.Save(output);
                dialog.Close();
            }));
            app.Run(dialog);
        }
    }
}

namespace Autodesk.AutoCAD.DatabaseServices { internal sealed class Database { } internal readonly struct ObjectId { } }
namespace Autodesk.AutoCAD.Geometry
{
    internal readonly struct Matrix3d { }
    internal readonly struct Point2d
    {
        internal Point2d(double x, double y) { X = x; Y = y; }
        internal double X { get; }
        internal double Y { get; }
    }
}
namespace Autodesk.AutoCAD.ApplicationServices
{
    internal static class Application { internal static object GetSystemVariable(string name) => 1; }
}
namespace Autodesk.Windows { internal static class ComponentManager { internal static IntPtr ApplicationWindow => IntPtr.Zero; } }
namespace Nexo.AutoCAD
{
    internal enum OpeningKind { None, Door, Window }
    internal sealed class DetectedWall
    {
        internal Point2d A, B, C, D;
        internal double Length, Thickness;
        internal bool Included = true, FromHatch;
        internal bool OpeningCandidate;
        internal OpeningKind Opening;
        internal double OpeningWidth, OpeningHeight, OpeningSill;
    }
    internal sealed class WallDetectionOptions
    {
        internal string Layer;
        internal double MinThickness, MaxThickness, MinLength, Height;
    }
    internal sealed class WallDetectionResult
    {
        internal readonly List<DetectedWall> Walls = new List<DetectedWall>();
        internal readonly List<Tuple<Point2d, Point2d>> Context = new List<Tuple<Point2d, Point2d>>();
        internal int Hatches = 3, IgnoredCurves = 2, IgnoredClippedReferences, PossibleOpenings = 1, LimitWarnings;
    }
    internal static class WallDetection
    {
        internal static WallDetectionResult Detect(Database db, ObjectId[] ids, Matrix3d ucs, double minX, double minY, double maxX, double maxY, WallDetectionOptions options)
        {
            WallDetectionResult result = new WallDetectionResult();
            result.Walls.Add(new DetectedWall { A=new Point2d(1,1), B=new Point2d(8,1), C=new Point2d(8,1.2), D=new Point2d(1,1.2), Length=7, Thickness=.2, FromHatch=true });
            result.Walls.Add(new DetectedWall { A=new Point2d(1,1.2), B=new Point2d(1,6), C=new Point2d(1.2,6), D=new Point2d(1.2,1.2), Length=4.8, Thickness=.2, FromHatch=false });
            result.Walls.Add(new DetectedWall { A=new Point2d(4,4), B=new Point2d(8,4), C=new Point2d(8,4.2), D=new Point2d(4,4.2), Length=4, Thickness=.2, FromHatch=false, Included=false });
            result.Context.Add(Tuple.Create(new Point2d(0,3), new Point2d(10,3)));
            return result;
        }
    }
}
