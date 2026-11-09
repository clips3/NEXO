using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Nexo.AutoCAD
{
    internal static class NexoRibbonUi
    {
        private const string TabId = "NEXO_RIBBON_2027";
        private const int MaxInitializationAttempts = 120;
        private static readonly Dictionary<string, ImageSource> IconCache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static DispatcherTimer _retryTimer;
        private static int _retryCount;

        internal static void Initialize()
        {
            if (_retryTimer != null)
                return;

            _retryCount = 0;
            _retryTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _retryTimer.Tick += TryBuildRibbon;
            _retryTimer.Start();
        }

        internal static void Shutdown()
        {
            StopRetryTimer();
        }

        private static void TryBuildRibbon(object sender, EventArgs eventArgs)
        {
            _retryCount++;
            RibbonControl ribbon = ComponentManager.Ribbon;
            if (ribbon == null)
            {
                if (_retryCount >= MaxInitializationAttempts)
                    StopRetryTimer();
                return;
            }

            StopRetryTimer();
            if (FindTab(ribbon) != null)
                return;

            List<RibbonCommandDefinition> commands = GetCommandDefinitions();
            if (commands.Count == 0)
                return;

            RibbonTab tab = new RibbonTab { Id = TabId, Title = "NEXO" };
            AddCommandPanels(tab, commands);
            ribbon.Tabs.Add(tab);
        }

        private static List<RibbonCommandDefinition> GetCommandDefinitions()
        {
            List<RibbonCommandDefinition> commands = new List<RibbonCommandDefinition>();
            HashSet<string> seenCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Type type in typeof(NexoRibbonUi).Assembly.GetTypes())
            {
                MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (MethodInfo method in methods)
                {
                    NexoRibbonCommandAttribute metadata = Attribute.GetCustomAttribute(
                        method, typeof(NexoRibbonCommandAttribute)) as NexoRibbonCommandAttribute;
                    if (metadata == null || string.IsNullOrWhiteSpace(metadata.CommandName) ||
                        !seenCommands.Add(metadata.CommandName))
                    {
                        continue;
                    }

                    commands.Add(new RibbonCommandDefinition(metadata));
                }
            }

            return commands;
        }

        private static void AddCommandPanels(RibbonTab tab, IList<RibbonCommandDefinition> commands)
        {
            Dictionary<string, List<RibbonCommandDefinition>> commandsByPanel =
                new Dictionary<string, List<RibbonCommandDefinition>>(StringComparer.OrdinalIgnoreCase);
            List<string> panelOrder = new List<string>();

            foreach (RibbonCommandDefinition command in commands)
            {
                string panelTitle = string.IsNullOrWhiteSpace(command.PanelTitle) ? "Comandos" : command.PanelTitle;
                if (!commandsByPanel.TryGetValue(panelTitle, out List<RibbonCommandDefinition> panelCommands))
                {
                    panelCommands = new List<RibbonCommandDefinition>();
                    commandsByPanel.Add(panelTitle, panelCommands);
                    panelOrder.Add(panelTitle);
                }
                panelCommands.Add(command);
            }

            foreach (string panelTitle in panelOrder)
            {
                List<RibbonCommandDefinition> panelCommands = commandsByPanel[panelTitle];
                RibbonPanelSource source = new RibbonPanelSource { Title = panelTitle };
                RibbonRowPanel rows = new RibbonRowPanel { IsTopJustified = true };

                for (int index = 0; index < panelCommands.Count; index++)
                {
                    rows.Items.Add(CreateButton(panelCommands[index]));
                    if (index < panelCommands.Count - 1)
                        rows.Items.Add(new RibbonRowBreak());
                }

                source.Items.Add(rows);
                tab.Panels.Add(new RibbonPanel { Source = source });
            }
        }

        private static RibbonButton CreateButton(RibbonCommandDefinition command)
        {
            string id = "NEXO_RIBBON_" + command.CommandName.ToUpperInvariant();
            return new RibbonButton
            {
                Id = id,
                Text = command.ButtonText,
                ShowText = true,
                ShowImage = true,
                Size = RibbonItemSize.Standard,
                Image = LoadRibbonIcon(command.IconFileName, 16),
                LargeImage = LoadRibbonIcon(command.IconFileName, 32),
                ToolTip = command.ToolTip,
                IsToolTipEnabled = true,
                CommandHandler = new RibbonCommandHandler(command.CommandName)
            };
        }

        private static ImageSource LoadRibbonIcon(string fileName, int expectedSize)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            string cacheKey = expectedSize + ":" + fileName;
            if (IconCache.TryGetValue(cacheKey, out ImageSource cachedImage))
                return cachedImage;

            if (string.Equals(fileName, "icon_ellipse_to_poly.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateEllipseToPolylineIcon(expectedSize);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            if (string.Equals(fileName, "icon_burst_plus.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateBurstPlusIcon(expectedSize);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            if (string.Equals(fileName, "icon_auto_block.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateAutoBlockIcon(expectedSize);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            if (string.Equals(fileName, "icon_clean_polyline_vertex.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateCleanPolylineVertexIcon(expectedSize);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            if (string.Equals(fileName, "icon_break_polyline.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateBreakPolylineIcon(expectedSize);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            if (string.Equals(fileName, "icon_delete_layer.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateDeleteLayerIcon(expectedSize);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            if (string.Equals(fileName, "icon_detail.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateDetailIcon(expectedSize);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            if (string.Equals(fileName, "icon_detail_light.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateLightDetailIcon(expectedSize);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            if (string.Equals(fileName, "icon_detail_light_isometric.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateLightIsometricDetailIcon(expectedSize);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            if (string.Equals(fileName, "icon_wall_isometric.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateWallIsometricIcon(expectedSize);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            if (string.Equals(fileName, "icon_isometric.png", StringComparison.OrdinalIgnoreCase))
            {
                ImageSource vectorIcon = CreateIsometricIcon(expectedSize, true);
                IconCache[cacheKey] = vectorIcon;
                return vectorIcon;
            }

            string assemblyDirectory = Path.GetDirectoryName(typeof(NexoRibbonUi).Assembly.Location);
            if (string.IsNullOrEmpty(assemblyDirectory))
                return null;

            string iconPath = Path.Combine(assemblyDirectory, "Resources", fileName);
            if (!File.Exists(iconPath))
                return null;

            BitmapImage image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(iconPath, UriKind.Absolute);
            image.DecodePixelWidth = expectedSize;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            if (image.CanFreeze)
                image.Freeze();

            IconCache[cacheKey] = image;
            return image;
        }

        private static ImageSource CreateEllipseToPolylineIcon(int expectedSize)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(145, 82, 213));
            brush.Freeze();
            Pen pen = new Pen(brush, 2.4)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            Geometry ellipse = Geometry.Parse("M 2,16 C 2,9 5,5 9,5 C 13,5 16,9 16,16 C 16,23 13,27 9,27 C 5,27 2,23 2,16 Z");
            Geometry polyline = Geometry.Parse("M 21,9 L 24,6 L 28,9 L 30,16 L 28,23 L 24,26 L 21,23 L 19,16 Z");
            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawGeometry(null, pen, ellipse);
                context.DrawGeometry(null, pen, polyline);
                context.DrawLine(pen, new Point(12, 16), new Point(18, 16));
                context.DrawLine(pen, new Point(15.5, 13.5), new Point(18, 16));
                context.DrawLine(pen, new Point(15.5, 18.5), new Point(18, 16));
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static ImageSource CreateBurstPlusIcon(int expectedSize)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(145, 82, 213));
            brush.Freeze();
            Pen pen = new Pen(brush, 2.3)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            Geometry block = Geometry.Parse("M 3,9 L 11,4 L 20,9 L 12,14 Z M 3,9 L 3,19 L 12,25 L 12,14 M 20,9 L 20,15");
            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawGeometry(null, pen, block);
                context.DrawLine(pen, new Point(25, 12), new Point(25, 22));
                context.DrawLine(pen, new Point(20, 17), new Point(30, 17));
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static ImageSource CreateAutoBlockIcon(int expectedSize)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(145, 82, 213));
            brush.Freeze();
            Pen pen = new Pen(brush, 2.2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            Geometry block = Geometry.Parse("M 3,10 L 16,3 L 29,10 L 16,17 Z M 3,10 L 3,24 L 16,31 L 16,17 M 29,10 L 29,20");
            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawGeometry(null, pen, block);
                context.DrawLine(pen, new Point(22, 18), new Point(22, 29));
                context.DrawLine(pen, new Point(19, 26), new Point(22, 29));
                context.DrawLine(pen, new Point(25, 26), new Point(22, 29));
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static ImageSource CreateCleanPolylineVertexIcon(int expectedSize)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(145, 82, 213));
            brush.Freeze();
            Pen pen = new Pen(brush, 2.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawLine(pen, new Point(3, 24), new Point(10, 12));
                context.DrawLine(pen, new Point(10, 12), new Point(18, 24));
                context.DrawLine(pen, new Point(18, 24), new Point(29, 8));
                context.DrawEllipse(brush, null, new Point(10, 12), 3.2, 3.2);
                context.DrawEllipse(brush, null, new Point(18, 24), 3.2, 3.2);
                context.DrawLine(pen, new Point(22, 7), new Point(22, 14));
                context.DrawLine(pen, new Point(18.5, 10.5), new Point(25.5, 10.5));
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static ImageSource CreateBreakPolylineIcon(int expectedSize)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(145, 82, 213));
            brush.Freeze();
            Pen pen = new Pen(brush, 2.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawLine(pen, new Point(3, 8), new Point(12, 20));
                context.DrawLine(pen, new Point(12, 20), new Point(18, 12));
                context.DrawLine(pen, new Point(23, 12), new Point(29, 24));
                context.DrawLine(pen, new Point(18, 8), new Point(18, 16));
                context.DrawLine(pen, new Point(15, 12), new Point(21, 12));
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static ImageSource CreateDeleteLayerIcon(int expectedSize)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(145, 82, 213));
            brush.Freeze();
            Pen pen = new Pen(brush, 2.3)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawLine(pen, new Point(4, 8), new Point(21, 8));
                context.DrawLine(pen, new Point(4, 14), new Point(18, 14));
                context.DrawLine(pen, new Point(4, 20), new Point(14, 20));
                context.DrawLine(pen, new Point(18, 20), new Point(28, 30));
                context.DrawLine(pen, new Point(28, 20), new Point(18, 30));
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static ImageSource CreateDetailIcon(int expectedSize)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(145, 82, 213));
            brush.Freeze();
            Pen pen = new Pen(brush, 2.2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawRectangle(null, pen, new Rect(4, 4, 24, 24));
                context.DrawRectangle(null, pen, new Rect(9, 9, 14, 14));
                context.DrawLine(pen, new Point(21, 11), new Point(27, 5));
                context.DrawLine(pen, new Point(23, 5), new Point(27, 5));
                context.DrawLine(pen, new Point(27, 5), new Point(27, 9));
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static ImageSource CreateLightDetailIcon(int expectedSize)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(145, 82, 213));
            brush.Freeze();
            Pen pen = new Pen(brush, 2.2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawRectangle(null, pen, new Rect(4, 5, 24, 22));
                context.DrawLine(pen, new Point(9, 11), new Point(17, 11));
                context.DrawLine(pen, new Point(9, 16), new Point(21, 16));
                context.DrawLine(pen, new Point(9, 21), new Point(15, 21));
                context.DrawLine(pen, new Point(20, 21), new Point(24, 17));
                context.DrawLine(pen, new Point(24, 17), new Point(28, 21));
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static ImageSource CreateWallIsometricIcon(int expectedSize)
        {
            SolidColorBrush outline = new SolidColorBrush(Color.FromRgb(132, 76, 201));
            SolidColorBrush fill = new SolidColorBrush(Color.FromArgb(75, 145, 82, 213));
            outline.Freeze(); fill.Freeze();
            Pen pen = new Pen(outline, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            Geometry wall = Geometry.Parse("M 6,22 L 6,9 L 22,1 L 26,3 L 26,16 L 10,24 Z M 6,9 L 10,11 L 26,3 M 10,11 L 10,24");
            Geometry floor = Geometry.Parse("M 2,24 L 11,29 L 30,19 L 26,17 M 2,24 L 6,22 M 11,29 L 11,31 L 30,21 L 30,19");
            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawGeometry(fill, pen, wall);
                context.DrawGeometry(null, pen, floor);
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing); image.Freeze(); return image;
        }

        private static ImageSource CreateLightIsometricDetailIcon(int expectedSize)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(145, 82, 213));
            brush.Freeze();
            Pen pen = new Pen(brush, 2.1)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            Geometry cube = Geometry.Parse(
                "M 20,9 L 26,5 L 31,8 L 25,12 Z M 20,9 L 20,16 L 25,19 L 25,12 M 31,8 L 31,15 L 25,19");
            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawRectangle(null, pen, new Rect(2, 9, 15, 18));
                context.DrawLine(pen, new Point(5, 14), new Point(12, 14));
                context.DrawLine(pen, new Point(5, 18), new Point(10, 18));
                context.DrawGeometry(null, pen, cube);
                context.DrawLine(pen, new Point(13, 6), new Point(20, 6));
                context.DrawLine(pen, new Point(17, 4), new Point(20, 6));
                context.DrawLine(pen, new Point(17, 8), new Point(20, 6));
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static ImageSource CreateIsometricIcon(int expectedSize, bool right)
        {
            SolidColorBrush outlineBrush = new SolidColorBrush(Color.FromRgb(107, 63, 171));
            SolidColorBrush topBrush = new SolidColorBrush(Color.FromRgb(185, 151, 225));
            SolidColorBrush leftBrush = new SolidColorBrush(right ? Color.FromRgb(129, 90, 193) : Color.FromRgb(152, 116, 210));
            SolidColorBrush rightBrush = new SolidColorBrush(right ? Color.FromRgb(152, 116, 210) : Color.FromRgb(129, 90, 193));
            outlineBrush.Freeze();
            topBrush.Freeze();
            leftBrush.Freeze();
            rightBrush.Freeze();

            Pen pen = new Pen(outlineBrush, 1.8)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();

            Geometry top = Geometry.Parse("M 16,3 L 29,10 L 16,17 L 3,10 Z");
            Geometry left = Geometry.Parse("M 3,10 L 16,17 L 16,30 L 3,23 Z");
            Geometry rightFace = Geometry.Parse("M 29,10 L 16,17 L 16,30 L 29,23 Z");
            DrawingGroup drawing = new DrawingGroup();
            using (DrawingContext context = drawing.Open())
            {
                double scale = Math.Max(1, expectedSize) / 32.0;
                context.PushTransform(new ScaleTransform(scale, scale));
                context.DrawGeometry(topBrush, pen, top);
                context.DrawGeometry(leftBrush, pen, left);
                context.DrawGeometry(rightBrush, pen, rightFace);
                context.Pop();
            }
            drawing.Freeze();
            DrawingImage image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static RibbonTab FindTab(RibbonControl ribbon)
        {
            foreach (RibbonTab tab in ribbon.Tabs)
            {
                if (string.Equals(tab.Id, TabId, StringComparison.Ordinal))
                    return tab;
            }

            return null;
        }

        private static void StopRetryTimer()
        {
            if (_retryTimer == null)
                return;

            _retryTimer.Stop();
            _retryTimer.Tick -= TryBuildRibbon;
            _retryTimer = null;
        }

        private sealed class RibbonCommandDefinition
        {
            internal RibbonCommandDefinition(NexoRibbonCommandAttribute metadata)
            {
                CommandName = metadata.CommandName;
                PanelTitle = metadata.PanelTitle;
                ButtonText = metadata.ButtonText;
                ToolTip = metadata.ToolTip;
                IconFileName = metadata.IconFileName;
            }

            internal string CommandName { get; }
            internal string PanelTitle { get; }
            internal string ButtonText { get; }
            internal string ToolTip { get; }
            internal string IconFileName { get; }
        }

        private sealed class RibbonCommandHandler : ICommand
        {
            private readonly string _commandName;

            internal RibbonCommandHandler(string commandName)
            {
                _commandName = commandName;
            }

            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;

            public void Execute(object parameter)
            {
                Document document = AcadApplication.DocumentManager.MdiActiveDocument;
                document?.SendStringToExecute(_commandName + "\n", true, false, false);
            }
        }
    }
}
