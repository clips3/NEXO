using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Windows;

namespace Nexo.AutoCAD
{
    internal enum IsometricFace
    {
        Top,
        Left,
        Right
    }

    internal readonly struct IsometricSelection
    {
        internal IsometricSelection(IsometricFace face, int rotationDegrees)
        {
            Face = face;
            RotationDegrees = rotationDegrees;
        }

        internal IsometricFace Face { get; }
        internal int RotationDegrees { get; }
    }

    internal sealed class IsometricDirectionDialog : Window
    {
        private const double PlaneScale = 0.60;
        private const double ArrowScale = 0.42;

        private readonly SolidColorBrush _accentBrush;
        private readonly SolidColorBrush _topFaceBrush;
        private readonly SolidColorBrush _sideFaceBrush;
        private readonly SolidColorBrush _hoverFaceBrush;
        private readonly SolidColorBrush _selectedFaceBrush;
        private readonly SolidColorBrush _previewFillBrush;
        private readonly SolidColorBrush _outlineBrush;
        private readonly SolidColorBrush _foregroundBrush;
        private readonly SolidColorBrush _mutedBrush;
        private readonly SolidColorBrush _borderBrush;
        private readonly SolidColorBrush _surfaceBrush;
        private readonly FaceVisual[] _faceVisuals;
        private readonly TextBlock _rotationText;
        private readonly TextBlock _statusText;
        private readonly Button _applyButton;
        private IsometricFace? _selectedFace;
        private IsometricFace? _hoveredFace;
        private int _rotationDegrees;

        private IsometricDirectionDialog(bool lightTheme)
        {
            _accentBrush = CreateBrush(132, 76, 201);
            _topFaceBrush = CreateBrush(lightTheme ? 238 : 78, lightTheme ? 241 : 86, lightTheme ? 245 : 96);
            _sideFaceBrush = CreateBrush(lightTheme ? 226 : 64, lightTheme ? 230 : 71, lightTheme ? 236 : 83);
            _hoverFaceBrush = CreateBrush(lightTheme ? 232 : 83, lightTheme ? 220 : 66, lightTheme ? 247 : 101);
            _selectedFaceBrush = CreateBrush(lightTheme ? 224 : 100, lightTheme ? 207 : 72, lightTheme ? 245 : 123);
            _previewFillBrush = CreateBrush((byte)(lightTheme ? 65 : 92), 132, 76, 201);
            _outlineBrush = CreateBrush(lightTheme ? 108 : 184, lightTheme ? 119 : 168, lightTheme ? 132 : 201);
            _foregroundBrush = CreateBrush(lightTheme ? 32 : 241, lightTheme ? 36 : 243, lightTheme ? 43 : 247);
            _mutedBrush = CreateBrush(lightTheme ? 101 : 181, lightTheme ? 111 : 190, lightTheme ? 124 : 201);
            _borderBrush = CreateBrush(lightTheme ? 202 : 75, lightTheme ? 208 : 86, lightTheme ? 216 : 99);
            _surfaceBrush = CreateBrush(lightTheme ? 255 : 43, lightTheme ? 255 : 50, lightTheme ? 255 : 59);

            Title = "Orientação isométrica";
            Width = 900;
            Height = 590;
            MinWidth = 860;
            MinHeight = 560;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = CreateBrush(lightTheme ? 246 : 35, lightTheme ? 247 : 42, lightTheme ? 249 : 58);
            Foreground = _foregroundBrush;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            Grid root = new Grid { Margin = new Thickness(24, 18, 24, 20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(new TextBlock
            {
                Text = "Orientação isométrica",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = _foregroundBrush
            });
            header.Children.Add(new TextBlock
            {
                Text = "A planta fica fixa como referência. Escolha qualquer face visível; clique novamente para girar em passos de 90°.",
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = _mutedBrush,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            Grid illustrations = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            illustrations.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(285) });
            illustrations.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Canvas planCanvas = CreatePlanCanvas();
            Grid.SetColumn(planCanvas, 0);
            illustrations.Children.Add(planCanvas);

            Canvas cubeCanvas = CreateCubeCanvas(out _faceVisuals);
            Grid.SetColumn(cubeCanvas, 1);
            illustrations.Children.Add(cubeCanvas);
            Grid.SetRow(illustrations, 1);
            root.Children.Add(illustrations);

            Grid footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _rotationText = new TextBlock
            {
                Text = "Giro  0°",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _foregroundBrush
            };
            _statusText = new TextBlock
            {
                Text = "Passe o cursor sobre uma face para pré-visualizar.",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _mutedBrush
            };
            Grid.SetRow(_rotationText, 0);
            Grid.SetRow(_statusText, 0);
            Grid.SetColumn(_statusText, 1);
            footer.Children.Add(_rotationText);
            footer.Children.Add(_statusText);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            Button cancelButton = CreateButton("Cancelar", false);
            cancelButton.Click += delegate { DialogResult = false; };
            _applyButton = CreateButton("Aplicar", true);
            _applyButton.IsEnabled = false;
            _applyButton.Click += delegate
            {
                if (_selectedFace.HasValue)
                {
                    SelectedSelection = new IsometricSelection(_selectedFace.Value, _rotationDegrees);
                    DialogResult = true;
                }
            };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(_applyButton);
            Grid.SetRow(buttons, 1);
            Grid.SetColumn(buttons, 0);
            Grid.SetColumnSpan(buttons, 2);
            footer.Children.Add(buttons);

            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            Content = root;
            RefreshVisuals();
        }

        internal IsometricSelection? SelectedSelection { get; private set; }

        internal static IsometricSelection? Choose()
        {
            IsometricDirectionDialog dialog = new IsometricDirectionDialog(IsLightTheme());
            IntPtr owner = ComponentManager.ApplicationWindow;
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(dialog).Owner = owner;

            return dialog.ShowDialog() == true ? dialog.SelectedSelection : null;
        }

        private Canvas CreatePlanCanvas()
        {
            Canvas canvas = new Canvas { Width = 260, Height = 410, HorizontalAlignment = HorizontalAlignment.Center };
            AddSectionLabel(canvas, "PLANTA", 260);

            Rectangle plane = new Rectangle
            {
                Width = 170,
                Height = 170,
                Stroke = _accentBrush,
                StrokeThickness = 2.6,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(plane, 45);
            Canvas.SetTop(plane, 110);
            canvas.Children.Add(plane);
            AddArrow(canvas, new Point(130, 195), new Vector(0, -68), _accentBrush, 3.8, 16, 8, false);
            return canvas;
        }

        private Canvas CreateCubeCanvas(out FaceVisual[] faceVisuals)
        {
            Canvas canvas = new Canvas { Width = 510, Height = 410, HorizontalAlignment = HorizontalAlignment.Center };
            AddSectionLabel(canvas, "VISTA ISOMÉTRICA", 510);

            Point cubeTop = new Point(255, 40);
            Point upperLeft = new Point(120, 118);
            Point topFront = new Point(255, 196);
            Point upperRight = new Point(390, 118);
            Point lowerLeft = new Point(120, 314);
            Point bottom = new Point(255, 392);
            Point lowerRight = new Point(390, 314);

            Polygon leftSurface = CreateFace(
                new[] { upperLeft, topFront, bottom, lowerLeft }, _sideFaceBrush, "Face lateral esquerda");
            Polygon rightSurface = CreateFace(
                new[] { topFront, upperRight, lowerRight, bottom }, _sideFaceBrush, "Face lateral direita");
            Polygon topSurface = CreateFace(
                new[] { cubeTop, upperRight, topFront, upperLeft }, _topFaceBrush, "Face superior");

            canvas.Children.Add(leftSurface);
            canvas.Children.Add(rightSurface);
            canvas.Children.Add(topSurface);

            FaceVisual topVisual = CreateFaceVisual(
                canvas, IsometricFace.Top, topSurface, _topFaceBrush,
                new Point(255, 118), new Vector(67.5, 39), new Vector(-67.5, 39));
            FaceVisual leftVisual = CreateFaceVisual(
                canvas, IsometricFace.Left, leftSurface, _sideFaceBrush,
                new Point(187.5, 255), new Vector(67.5, 39), new Vector(0, 98));
            FaceVisual rightVisual = CreateFaceVisual(
                canvas, IsometricFace.Right, rightSurface, _sideFaceBrush,
                new Point(322.5, 255), new Vector(67.5, -39), new Vector(0, 98));

            faceVisuals = new[] { topVisual, leftVisual, rightVisual };
            return canvas;
        }

        private FaceVisual CreateFaceVisual(
            Canvas canvas,
            IsometricFace face,
            Polygon surface,
            SolidColorBrush baseBrush,
            Point center,
            Vector basisX,
            Vector basisY)
        {
            surface.Cursor = Cursors.Hand;
            surface.Focusable = true;
            surface.ToolTip = "Clique para posicionar a planta; clique novamente nesta face para girar 90°.";
            surface.MouseEnter += delegate
            {
                _hoveredFace = face;
                RefreshVisuals();
            };
            surface.MouseLeave += delegate
            {
                if (_hoveredFace == face)
                    _hoveredFace = null;
                RefreshVisuals();
            };
            surface.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                SelectFace(face);
                args.Handled = true;
            };
            surface.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter || args.Key == Key.Space)
                {
                    SelectFace(face);
                    args.Handled = true;
                }
            };

            Polygon plane = CreateProjectedPlane(center, basisX, basisY);
            Line arrowLine = new Line
            {
                Stroke = _accentBrush,
                StrokeThickness = 3.3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
            Polygon arrowHead = new Polygon { Fill = _accentBrush, IsHitTestVisible = false };
            plane.IsHitTestVisible = false;
            canvas.Children.Add(plane);
            canvas.Children.Add(arrowLine);
            canvas.Children.Add(arrowHead);
            return new FaceVisual(face, surface, baseBrush, center, basisX, basisY, plane, arrowLine, arrowHead);
        }

        private void SelectFace(IsometricFace face)
        {
            if (_selectedFace == face)
                _rotationDegrees = (_rotationDegrees + 90) % 360;
            else
            {
                _selectedFace = face;
                _rotationDegrees = 0;
            }

            RefreshVisuals();
        }

        private void RefreshVisuals()
        {
            IsometricFace? previewFace = _hoveredFace ?? _selectedFace;
            foreach (FaceVisual visual in _faceVisuals)
            {
                bool selected = _selectedFace == visual.Face;
                bool hovered = _hoveredFace == visual.Face;
                visual.Surface.Fill = selected ? _selectedFaceBrush : (hovered ? _hoverFaceBrush : visual.BaseBrush);
                visual.Surface.Stroke = selected || hovered ? _accentBrush : _outlineBrush;
                visual.Surface.StrokeThickness = selected ? 3.2 : (hovered ? 2.7 : 1.8);

                bool showPreview = previewFace == visual.Face;
                visual.Plane.Visibility = showPreview ? Visibility.Visible : Visibility.Hidden;
                visual.ArrowLine.Visibility = showPreview ? Visibility.Visible : Visibility.Hidden;
                visual.ArrowHead.Visibility = showPreview ? Visibility.Visible : Visibility.Hidden;
                if (!showPreview)
                    continue;

                int previewRotation = _hoveredFace.HasValue && _hoveredFace != _selectedFace ? 0 : _rotationDegrees;
                double radians = previewRotation * Math.PI / 180.0;
                Vector projectedArrow = (visual.BasisX * Math.Sin(radians)) + (visual.BasisY * -Math.Cos(radians));
                projectedArrow *= ArrowScale;
                UpdateArrow(visual.ArrowLine, visual.ArrowHead, visual.Center, projectedArrow, 10, 5);
                visual.Plane.Fill = _previewFillBrush;
                visual.Plane.Stroke = _accentBrush;
                visual.Plane.StrokeThickness = 2.2;
            }

            _rotationText.Text = "Giro  " + _rotationDegrees.ToString(CultureInfo.InvariantCulture) + "°";
            _applyButton.IsEnabled = _selectedFace.HasValue;
            if (_hoveredFace.HasValue && _hoveredFace != _selectedFace)
            {
                _statusText.Text = "Prévia da " + FaceName(_hoveredFace.Value) + " — clique para selecionar.";
                _statusText.Foreground = _mutedBrush;
            }
            else if (_selectedFace.HasValue)
            {
                _statusText.Text = "Clique novamente na " + FaceName(_selectedFace.Value) + " para girar 90°.";
                _statusText.Foreground = _accentBrush;
            }
            else if (_hoveredFace.HasValue)
            {
                _statusText.Text = "Prévia da " + FaceName(_hoveredFace.Value) + " — clique para selecionar.";
                _statusText.Foreground = _mutedBrush;
            }
            else
            {
                _statusText.Text = "Passe o cursor sobre uma face para pré-visualizar.";
                _statusText.Foreground = _mutedBrush;
            }
        }

        private Polygon CreateProjectedPlane(Point center, Vector basisX, Vector basisY)
        {
            Vector x = basisX * PlaneScale;
            Vector y = basisY * PlaneScale;
            return new Polygon
            {
                Points = new PointCollection(new[]
                {
                    center - x - y,
                    center + x - y,
                    center + x + y,
                    center - x + y
                }),
                Fill = _previewFillBrush,
                Stroke = _accentBrush,
                StrokeThickness = 2.2,
                StrokeLineJoin = PenLineJoin.Round
            };
        }

        private Polygon CreateFace(Point[] points, Brush fill, string name)
        {
            return new Polygon
            {
                Points = new PointCollection(points),
                Fill = fill,
                Stroke = _outlineBrush,
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round,
                ToolTip = name
            };
        }

        private void AddSectionLabel(Canvas canvas, string text, double width)
        {
            TextBlock label = new TextBlock
            {
                Text = text,
                Width = width,
                TextAlignment = TextAlignment.Center,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = _mutedBrush
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, 3);
            canvas.Children.Add(label);
        }

        private void AddArrow(
            Canvas canvas,
            Point center,
            Vector vector,
            Brush brush,
            double thickness,
            double headLength,
            double halfWidth,
            bool hitTestVisible)
        {
            Line line = new Line
            {
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = hitTestVisible
            };
            Polygon head = new Polygon { Fill = brush, IsHitTestVisible = hitTestVisible };
            canvas.Children.Add(line);
            canvas.Children.Add(head);
            UpdateArrow(line, head, center, vector, headLength, halfWidth);
        }

        private static void UpdateArrow(
            Line line,
            Polygon head,
            Point center,
            Vector vector,
            double headLength,
            double halfWidth)
        {
            double length = vector.Length;
            if (length < 0.001)
                return;

            Vector direction = vector / length;
            Point tip = center + vector;
            Point basePoint = tip - (direction * headLength);
            Vector perpendicular = new Vector(-direction.Y, direction.X) * halfWidth;
            line.X1 = (center - vector).X;
            line.Y1 = (center - vector).Y;
            line.X2 = basePoint.X;
            line.Y2 = basePoint.Y;
            head.Points = new PointCollection(new[] { tip, basePoint + perpendicular, basePoint - perpendicular });
        }

        private string FaceName(IsometricFace face)
        {
            switch (face)
            {
                case IsometricFace.Top:
                    return "face superior";
                case IsometricFace.Left:
                    return "face lateral esquerda";
                default:
                    return "face lateral direita";
            }
        }

        private Button CreateButton(string text, bool isPrimary)
        {
            return new Button
            {
                Content = text,
                MinWidth = 96,
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(8, 0, 0, 0),
                Background = isPrimary ? _accentBrush : _surfaceBrush,
                Foreground = isPrimary ? Brushes.White : _foregroundBrush,
                BorderBrush = isPrimary ? _accentBrush : _borderBrush,
                IsDefault = isPrimary,
                IsCancel = !isPrimary
            };
        }

        private static bool IsLightTheme()
        {
            try
            {
                object value = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("COLORTHEME");
                return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
            }
            catch
            {
                return false;
            }
        }

        private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush CreateBrush(int red, int green, int blue)
        {
            return CreateBrush((byte)red, (byte)green, (byte)blue);
        }

        private sealed class FaceVisual
        {
            internal FaceVisual(
                IsometricFace face,
                Polygon surface,
                SolidColorBrush baseBrush,
                Point center,
                Vector basisX,
                Vector basisY,
                Polygon plane,
                Line arrowLine,
                Polygon arrowHead)
            {
                Face = face;
                Surface = surface;
                BaseBrush = baseBrush;
                Center = center;
                BasisX = basisX;
                BasisY = basisY;
                Plane = plane;
                ArrowLine = arrowLine;
                ArrowHead = arrowHead;
            }

            internal IsometricFace Face { get; }
            internal Polygon Surface { get; }
            internal SolidColorBrush BaseBrush { get; }
            internal Point Center { get; }
            internal Vector BasisX { get; }
            internal Vector BasisY { get; }
            internal Polygon Plane { get; }
            internal Line ArrowLine { get; }
            internal Polygon ArrowHead { get; }
        }
    }
}
