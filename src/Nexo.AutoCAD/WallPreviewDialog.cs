using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Windows;

namespace Nexo.AutoCAD
{
    internal sealed class WallPreviewDialog : Window
    {
        private readonly Database _database;
        private readonly ObjectId[] _selectedIds;
        private readonly Matrix3d _ucs;
        private readonly double _minX, _minY, _maxX, _maxY;
        private readonly Canvas _canvas;
        private readonly TextBox _layer, _minThickness, _maxThickness, _minLength, _height;
        private readonly TextBlock _status;
        private readonly Button _accept;
        private readonly bool _light;
        private WallDetectionResult _result;
        private WallDetectionOptions _options;

        internal WallPreviewDialog(Database database, ObjectId[] selectedIds, Matrix3d ucs,
            double minX, double minY, double maxX, double maxY, bool? lightThemeOverride = null)
        {
            _database = database; _selectedIds = selectedIds; _ucs = ucs;
            _minX = minX; _minY = minY; _maxX = maxX; _maxY = maxY;
            if (lightThemeOverride.HasValue) _light = lightThemeOverride.Value;
            else
            {
                try { _light = Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("COLORTHEME"), CultureInfo.InvariantCulture) == 1; }
                catch { _light = true; }
            }
            Title = "Paredes automáticas — NEXO";
            Width = 1040; Height = 680; MinWidth = 800; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brush(_light ? "#F4F5F8" : "#222731");
            Foreground = Brush(_light ? "#20242B" : "#F1F3F6");
            FontFamily = new FontFamily("Segoe UI"); FontSize = 13;

            Grid root = new Grid { Margin = new Thickness(18), Background = Background };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock heading = new TextBlock { Text = "Reconhecimento de paredes", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
            root.Children.Add(heading);

            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            Grid.SetRow(body, 1); root.Children.Add(body);
            Border preview = new Border { Background = Brush(_light ? "#FFFFFF" : "#303844"), BorderBrush = Brush(_light ? "#CCD2DD" : "#566170"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 16, 0), ClipToBounds = true };
            _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            preview.Child = _canvas; body.Children.Add(preview);
            _canvas.SizeChanged += (sender, args) => Redraw();

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
            StackPanel controls = new StackPanel(); scroll.Content = controls;
            controls.Children.Add(new TextBlock { Text = "A prévia mostra o recorte em planta. Clique em uma parede para incluí-la ou removê-la. Portas e janelas reconhecidas aparecem em azul e verde.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            _layer = AddInput(controls, "Layer de arquitetura", "PP_ARQUITETURA");
            Grid dimensions = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            dimensions.ColumnDefinitions.Add(new ColumnDefinition());
            dimensions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            dimensions.ColumnDefinitions.Add(new ColumnDefinition());
            dimensions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            dimensions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _minThickness = AddInput(Cell(dimensions, 0, 0), "Espessura mínima", "0.06");
            _maxThickness = AddInput(Cell(dimensions, 0, 2), "Espessura máxima", "0.40");
            _minLength = AddInput(Cell(dimensions, 1, 0), "Comprimento mínimo", "0.30");
            _height = AddInput(Cell(dimensions, 1, 2), "Altura das paredes", "2.80");
            controls.Children.Add(dimensions);
            controls.Children.Add(new TextBlock { Text = "Valores nas unidades do desenho. Objetos na layer 0 dentro de blocos herdam a layer da inserção.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8) });
            Button detect = new Button { Content = "Identificar novamente", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 12) };
            detect.Click += (sender, args) => Detect(); controls.Children.Add(detect);
            StackPanel selectionButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            Button selectAll = new Button { Content = "Selecionar todas", Padding = new Thickness(6), Margin = new Thickness(0, 0, 5, 0) };
            Button clear = new Button { Content = "Limpar seleção", Padding = new Thickness(6) };
            selectAll.Click += (sender, args) => SetIncluded(true);
            clear.Click += (sender, args) => SetIncluded(false);
            selectionButtons.Children.Add(selectAll); selectionButtons.Children.Add(clear); controls.Children.Add(selectionButtons);
            controls.Children.Add(new TextBlock { Text = "Roxo: hachura   •   Âmbar: linhas   •   Cinza: excluída\nAzul: porta (< 1,00 m)   •   Verde: janela (≥ 1,00 m)\nClassificação automática somente pela largura do vão. Botão direito para trocar o tipo ou ajustar as dimensões.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap }; controls.Children.Add(_status);

            DockPanel footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            Grid.SetRow(footer, 2); root.Children.Add(footer);
            TextBlock hint = new TextBlock { Text = "Confira encontros, aberturas e objetos que não são paredes.", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
            DockPanel.SetDock(hint, Dock.Left); footer.Children.Add(hint);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = new Button { Content = "Cancelar", Width = 100, Padding = new Thickness(8), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            _accept = new Button { Content = "Gerar isométrico", MinWidth = 150, Padding = new Thickness(8), IsDefault = true, IsEnabled = false };
            _accept.Click += (sender, args) =>
            {
                if (_result == null || !_result.Walls.Any(w => w.Included)) return;
                if (!TryNumber(_height.Text, out double height) || height <= 0)
                {
                    _status.Text = "Informe uma altura positiva para as paredes.";
                    return;
                }
                if (_result.Walls.Any(w => w.Included && w.Opening != OpeningKind.None && w.OpeningSill + w.OpeningHeight > height + 1e-6))
                {
                    _status.Text = "Uma porta ou janela ultrapassa a altura da parede. Ajuste suas dimensões.";
                    return;
                }
                _options.Height = height;
                DialogResult = true;
            };
            buttons.Children.Add(cancel); buttons.Children.Add(_accept); footer.Children.Add(buttons);
            Content = root;
            foreach (TextBox input in new[] { _layer, _minThickness, _maxThickness, _minLength })
                input.TextChanged += (sender, args) =>
                {
                    _result = null; _accept.IsEnabled = false;
                    _status.Text = "Os critérios mudaram. Clique em Identificar novamente.";
                };
            Loaded += (sender, args) => Detect();
        }

        internal WallDetectionResult Result => _result;
        internal WallDetectionOptions Options => _options;

        internal static WallPreviewDialog Choose(Database database, ObjectId[] selectedIds, Matrix3d ucs,
            double minX, double minY, double maxX, double maxY)
        {
            WallPreviewDialog dialog = new WallPreviewDialog(database, selectedIds, ucs, minX, minY, maxX, maxY);
            IntPtr owner = ComponentManager.ApplicationWindow;
            if (owner != IntPtr.Zero) new WindowInteropHelper(dialog).Owner = owner;
            return dialog.ShowDialog() == true ? dialog : null;
        }

        private TextBox AddInput(Panel parent, string label, string value)
        {
            parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
            TextBox box = new TextBox { Text = value, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 9),
                Background = Brush(_light ? "#FFFFFF" : "#353D49"), Foreground = Foreground,
                BorderBrush = Brush(_light ? "#B7C0CD" : "#728092") };
            parent.Children.Add(box); return box;
        }

        private static StackPanel Cell(Grid grid, int row, int column)
        {
            StackPanel panel = new StackPanel();
            Grid.SetRow(panel, row); Grid.SetColumn(panel, column); grid.Children.Add(panel);
            return panel;
        }

        private void Detect()
        {
            if (!TryNumber(_minThickness.Text, out double minThickness) || !TryNumber(_maxThickness.Text, out double maxThickness) ||
                !TryNumber(_minLength.Text, out double minLength) || !TryNumber(_height.Text, out double height) ||
                minThickness <= 0 || maxThickness <= minThickness || minLength <= 0 || height <= 0 || string.IsNullOrWhiteSpace(_layer.Text))
            {
                _status.Text = "Informe uma layer e valores positivos; a espessura máxima deve ser maior que a mínima.";
                _accept.IsEnabled = false; return;
            }
            try
            {
                Cursor = Cursors.Wait;
                _options = new WallDetectionOptions { Layer = _layer.Text.Trim(), MinThickness = minThickness,
                    MaxThickness = maxThickness, MinLength = minLength, Height = height };
                _result = WallDetection.Detect(_database, _selectedIds, _ucs, _minX, _minY, _maxX, _maxY, _options);
                Redraw();
            }
            catch (Exception exception)
            {
                _result = null; _accept.IsEnabled = false;
                _status.Text = "Falha ao examinar a arquitetura: " + exception.Message;
            }
            finally { Cursor = null; }
        }

        private void Redraw()
        {
            if (_canvas == null || _result == null) return;
            _canvas.Children.Clear();
            double width = Math.Max(1, _canvas.ActualWidth), height = Math.Max(1, _canvas.ActualHeight);
            double scale = Math.Min((width - 40) / (_maxX - _minX), (height - 40) / (_maxY - _minY));
            if (scale <= 0) return;
            double left = (width - (_maxX - _minX) * scale) / 2;
            double top = (height - (_maxY - _minY) * scale) / 2;
            Rectangle frame = new Rectangle { Width = (_maxX - _minX) * scale, Height = (_maxY - _minY) * scale,
                Stroke = Brush(_light ? "#9EA9B9" : "#95A2B3"), StrokeThickness = 1, IsHitTestVisible = false };
            Canvas.SetLeft(frame, left); Canvas.SetTop(frame, top); _canvas.Children.Add(frame);
            SolidColorBrush contextBrush = Brush(_light ? "#C3CAD4" : "#7D8998");
            SolidColorBrush excludedStroke = Brush("#9299A3"), hatchStroke = Brush("#7544BA");
            SolidColorBrush excludedFill = Brush("#559299A3"), hatchFill = Brush("#997B4BC4"), lineFill = Brush("#AAE2A540");
            SolidColorBrush doorFill = Brush("#AA3485DA"), windowFill = Brush("#AA32A991");
            StreamGeometry contextGeometry = new StreamGeometry();
            using (StreamGeometryContext geometry = contextGeometry.Open())
            {
                foreach (Tuple<Point2d, Point2d> context in _result.Context)
                {
                    geometry.BeginFigure(new Point(left + (context.Item1.X - _minX) * scale,
                        top + (_maxY - context.Item1.Y) * scale), false, false);
                    geometry.LineTo(new Point(left + (context.Item2.X - _minX) * scale,
                        top + (_maxY - context.Item2.Y) * scale), true, false);
                }
            }
            contextGeometry.Freeze();
            _canvas.Children.Add(new System.Windows.Shapes.Path { Data = contextGeometry,
                Stroke = contextBrush, StrokeThickness = 0.8, IsHitTestVisible = false });
            void DrawWall(DetectedWall wall, DetectedWall source)
            {
                Polygon polygon = new Polygon { StrokeThickness = 1.4, Cursor = Cursors.Hand,
                    Stroke = !source.Included ? excludedStroke : null,
                    Fill = !source.Included ? excludedFill : source.Opening == OpeningKind.Door ? doorFill :
                        source.Opening == OpeningKind.Window ? windowFill : source.FromHatch ? hatchFill : lineFill,
                    ToolTip = string.Format(CultureInfo.CurrentCulture, "{0} — comprimento {1:0.###}; espessura {2:0.###}",
                        source.FromHatch ? "Hachura" : "Linhas", wall.Length, wall.Thickness) };
                foreach (Point2d point in new[] { wall.A, wall.B, wall.C, wall.D })
                    polygon.Points.Add(new Point(left + (point.X - _minX) * scale, top + (_maxY - point.Y) * scale));
                polygon.MouseLeftButtonDown += (sender, args) => { source.Included = !source.Included; Redraw(); args.Handled = true; };
                if (source.OpeningCandidate)
                {
                    ContextMenu menu = new ContextMenu();
                    MenuItem door = new MenuItem { Header = "Definir como porta..." };
                    MenuItem window = new MenuItem { Header = "Definir como janela..." };
                    MenuItem plain = new MenuItem { Header = "Tratar como parede" };
                    MenuItem ignore = new MenuItem { Header = "Ignorar vão" };
                    door.Click += (sender, args) => ConfigureOpening(source, OpeningKind.Door);
                    window.Click += (sender, args) => ConfigureOpening(source, OpeningKind.Window);
                    plain.Click += (sender, args) => { source.Opening = OpeningKind.None; source.Included = true; Redraw(); };
                    ignore.Click += (sender, args) => { source.Opening = OpeningKind.None; source.Included = false; Redraw(); };
                    menu.Items.Add(door); menu.Items.Add(window); menu.Items.Add(plain); menu.Items.Add(ignore); polygon.ContextMenu = menu;
                }
                _canvas.Children.Add(polygon);
            }
            // Keep excluded candidates clickable, then draw the non-overlapping
            // footprint that will actually be used for the selected walls.
            foreach (DetectedWall wall in _result.Walls)
                if (!wall.Included) DrawWall(wall, wall);
            var selectedPieces = WallFootprintOptimizer.Optimize(_result.Walls);
            foreach (WallFootprintOptimizer.Piece piece in selectedPieces)
                DrawWall(piece.Wall, piece.Source);
            if (selectedPieces.Count > 0)
            {
                Geometry outline = WallFootprintUnion.Build(selectedPieces);
                outline.Transform = new MatrixTransform(scale, 0, 0, -scale, left - _minX * scale, top + _maxY * scale);
                _canvas.Children.Add(new System.Windows.Shapes.Path { Data = outline, Stroke = hatchStroke,
                    StrokeThickness = 1.4, IsHitTestVisible = false });
            }
            int selected = _result.Walls.Count(w => w.Included);
            int doors = _result.Walls.Count(w => w.Opening == OpeningKind.Door);
            int windows = _result.Walls.Count(w => w.Opening == OpeningKind.Window);
            int unresolvedOpenings = _result.Walls.Count(w => w.OpeningCandidate && w.Opening == OpeningKind.None);
            _accept.IsEnabled = selected > 0;
            _status.Text = string.Format(CultureInfo.CurrentCulture,
                "{0} parede(s) selecionada(s) de {1}.\nPortas reconhecidas: {5}; janelas reconhecidas: {6}.\nVãos para confirmar manualmente: {7}.\nHachuras examinadas: {2}.\nSegmentos não suportados: {3}.\nReferências com recorte incompatível: {4}.{8}",
                selected, _result.Walls.Count, _result.Hatches, _result.IgnoredCurves, _result.IgnoredClippedReferences,
                doors, windows, unresolvedOpenings, _result.LimitWarnings > 0 ? "\nLimite de processamento atingido; reduza o recorte." : string.Empty);
        }

        private void ConfigureOpening(DetectedWall wall, OpeningKind kind)
        {
            Window dialog = new Window { Title = kind == OpeningKind.Door ? "Porta" : "Janela", Owner = this,
                SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, Background = Background, Foreground = Foreground, FontFamily = FontFamily };
            StackPanel body = new StackPanel { Margin = new Thickness(18), MinWidth = 260 };
            TextBox width = AddInput(body, "Largura", (wall.OpeningWidth > 0 ? wall.OpeningWidth : wall.Length).ToString("0.###", CultureInfo.CurrentCulture));
            TextBox height = AddInput(body, "Altura", (wall.Opening == kind && wall.OpeningHeight > 0 ? wall.OpeningHeight : kind == OpeningKind.Door ? 2.1 : 1.2).ToString("0.###", CultureInfo.CurrentCulture));
            TextBox sill = AddInput(body, "Peitoril", (wall.Opening == kind ? wall.OpeningSill : kind == OpeningKind.Door ? 0 : 1.0).ToString("0.###", CultureInfo.CurrentCulture));
            TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8) }; body.Children.Add(status);
            Button apply = new Button { Content = "Confirmar", Padding = new Thickness(8), IsDefault = true };
            apply.Click += (sender, args) => {
                if (!TryNumber(width.Text, out double w) || !TryNumber(height.Text, out double h) || !TryNumber(sill.Text, out double s) ||
                    !TryNumber(_height.Text, out double wallHeight) || w <= 0 || w > wall.Length + 1e-6 || h <= 0 || s < 0 || h + s > wallHeight + 1e-6)
                { status.Text = "Confira a largura do vão e a altura total em relação à parede."; return; }
                wall.OpeningWidth = w; wall.OpeningHeight = h; wall.OpeningSill = s;
                wall.Opening = kind; wall.Included = true; dialog.DialogResult = true;
            };
            body.Children.Add(apply); dialog.Content = body;
            if (dialog.ShowDialog() == true) Redraw();
        }

        private void SetIncluded(bool included)
        {
            if (_result == null) return;
            foreach (DetectedWall wall in _result.Walls) wall.Included = included;
            Redraw();
        }

        private static bool TryNumber(string text, out double result)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static SolidColorBrush Brush(string color)
        {
            SolidColorBrush brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color);
            brush.Freeze(); return brush;
        }
    }
}
