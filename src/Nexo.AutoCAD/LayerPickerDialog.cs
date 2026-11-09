using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Windows;

namespace Nexo.AutoCAD
{
    internal sealed class LayerPickerDialog : Window
    {
        private readonly TextBox _searchBox;
        private readonly ListBox _layerList;
        private readonly ICollectionView _layerView;
        private readonly Button _selectButton;

        private LayerPickerDialog(IList<string> layers, bool lightTheme)
        {
            Brush backgroundBrush = CreateBrush(lightTheme ? 246 : 35, lightTheme ? 247 : 42, lightTheme ? 249 : 52);
            Brush inputBrush = CreateBrush(lightTheme ? 255 : 25, lightTheme ? 255 : 30, lightTheme ? 255 : 37);
            Brush foregroundBrush = lightTheme ? CreateBrush(32, 35, 39) : Brushes.White;
            Brush borderBrush = lightTheme ? CreateBrush(133, 141, 151) : CreateBrush(70, 83, 101);
            Brush buttonBrush = lightTheme ? CreateBrush(226, 231, 237) : CreateBrush(54, 74, 99);

            Title = "Selecionar layer";
            Width = 470;
            Height = 540;
            MinWidth = 390;
            MinHeight = 420;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = backgroundBrush;
            Foreground = foregroundBrush;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            _searchBox = new TextBox
            {
                Margin = new Thickness(0, 8, 0, 10),
                Padding = new Thickness(8, 6, 8, 6),
                Background = inputBrush,
                Foreground = foregroundBrush,
                BorderBrush = borderBrush
            };
            _layerList = new ListBox
            {
                Background = inputBrush,
                Foreground = foregroundBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                SelectionMode = SelectionMode.Single
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_layerList, ScrollBarVisibility.Auto);
            _layerView = CollectionViewSource.GetDefaultView(layers);
            _layerView.Filter = FilterLayer;
            _layerList.ItemsSource = _layerView;

            _selectButton = CreateButton("Selecionar", true, buttonBrush, foregroundBrush, borderBrush);
            _selectButton.IsEnabled = false;
            Button cancelButton = CreateButton("Cancelar", false, buttonBrush, foregroundBrush, borderBrush);

            _searchBox.TextChanged += delegate { _layerView.Refresh(); };
            _layerList.SelectionChanged += delegate { _selectButton.IsEnabled = _layerList.SelectedItem != null; };
            _layerList.MouseDoubleClick += delegate { AcceptSelection(); };
            _selectButton.Click += delegate { AcceptSelection(); };
            cancelButton.Click += delegate { DialogResult = false; };

            Grid content = new Grid { Margin = new Thickness(16) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock description = new TextBlock
            {
                Text = "Escolha uma layer para selecionar os objetos correspondentes:",
                Margin = new Thickness(0, 0, 0, 2),
                Foreground = foregroundBrush,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(description, 0);
            content.Children.Add(description);

            Grid.SetRow(_searchBox, 1);
            content.Children.Add(_searchBox);

            Grid.SetRow(_layerList, 2);
            content.Children.Add(_layerList);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(_selectButton);
            Grid.SetRow(buttons, 3);
            content.Children.Add(buttons);
            Content = content;
        }

        internal string SelectedLayer { get; private set; }

        internal static string ChooseLayer(IList<string> layers)
        {
            LayerPickerDialog dialog = new LayerPickerDialog(layers, IsLightTheme());
            IntPtr owner = ComponentManager.ApplicationWindow;
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(dialog).Owner = owner;

            return dialog.ShowDialog() == true ? dialog.SelectedLayer : null;
        }

        private bool FilterLayer(object item)
        {
            string search = _searchBox.Text;
            return string.IsNullOrWhiteSpace(search) ||
                ((string)item).IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AcceptSelection()
        {
            if (_layerList.SelectedItem is string selectedLayer)
            {
                SelectedLayer = selectedLayer;
                DialogResult = true;
            }
        }

        private static Button CreateButton(string text, bool isDefault, Brush background, Brush foreground, Brush border)
        {
            return new Button
            {
                Content = text,
                MinWidth = 96,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(6, 0, 0, 0),
                Background = background,
                Foreground = foreground,
                BorderBrush = border,
                IsDefault = isDefault,
                IsCancel = !isDefault
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

        private static Brush CreateBrush(int red, int green, int blue)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromRgb((byte)red, (byte)green, (byte)blue));
            brush.Freeze();
            return brush;
        }
    }
}
