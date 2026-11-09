using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Windows;

namespace Nexo.AutoCAD
{
    internal enum LayerDeleteAction
    {
        DeleteObjects,
        MoveObjects
    }

    internal sealed class LayerDeleteRequest
    {
        internal string LayerName { get; set; }
        internal LayerDeleteAction Action { get; set; }
        internal string TargetLayerName { get; set; }
    }

    internal sealed class LayerDeleteDialog : Window
    {
        private readonly IList<string> _layers;
        private readonly IDictionary<string, int> _usageCounts;
        private readonly string _currentLayer;
        private readonly TextBox _searchBox;
        private readonly ListBox _layerList;
        private readonly ICollectionView _layerView;
        private readonly TextBlock _statusText;
        private readonly RadioButton _deleteObjectsOption;
        private readonly RadioButton _moveObjectsOption;
        private readonly ComboBox _targetLayerBox;
        private readonly Button _applyButton;

        private LayerDeleteDialog(
            IList<string> layers,
            IDictionary<string, int> usageCounts,
            string currentLayer,
            bool lightTheme)
        {
            _layers = layers;
            _usageCounts = usageCounts;
            _currentLayer = currentLayer;

            Brush background = CreateBrush(lightTheme ? 246 : 35, lightTheme ? 247 : 42, lightTheme ? 249 : 52);
            Brush input = CreateBrush(lightTheme ? 255 : 25, lightTheme ? 255 : 30, lightTheme ? 255 : 37);
            Brush foreground = lightTheme ? CreateBrush(32, 35, 39) : Brushes.White;
            Brush border = lightTheme ? CreateBrush(133, 141, 151) : CreateBrush(70, 83, 101);
            Brush button = lightTheme ? CreateBrush(226, 231, 237) : CreateBrush(54, 74, 99);

            Title = "Deletar Layer +";
            Width = 520;
            Height = 610;
            MinWidth = 440;
            MinHeight = 500;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = background;
            Foreground = foreground;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            _searchBox = new TextBox
            {
                Margin = new Thickness(0, 8, 0, 10),
                Padding = new Thickness(8, 6, 8, 6),
                Background = input,
                Foreground = foreground,
                BorderBrush = border
            };
            _layerList = new ListBox
            {
                Background = input,
                Foreground = foreground,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                SelectionMode = SelectionMode.Single
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_layerList, ScrollBarVisibility.Auto);
            _layerView = CollectionViewSource.GetDefaultView(_layers);
            _layerView.Filter = FilterLayer;
            _layerList.ItemsSource = _layerView;

            _statusText = new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 4),
                Foreground = foreground,
                TextWrapping = TextWrapping.Wrap
            };
            _deleteObjectsOption = new RadioButton
            {
                Content = "Excluir os objetos e a layer",
                Margin = new Thickness(0, 8, 0, 4),
                Foreground = foreground,
                IsEnabled = false
            };
            _moveObjectsOption = new RadioButton
            {
                Content = "Mover os objetos para outra layer",
                Margin = new Thickness(0, 4, 0, 4),
                Foreground = foreground,
                IsEnabled = false
            };
            _targetLayerBox = new ComboBox
            {
                Margin = new Thickness(22, 2, 0, 0),
                Padding = new Thickness(6, 4, 6, 4),
                Background = input,
                Foreground = foreground,
                BorderBrush = border,
                IsEnabled = false
            };

            _applyButton = CreateButton("Selecione uma layer", true, button, foreground, border);
            _applyButton.IsEnabled = false;
            Button cancelButton = CreateButton("Cancelar", false, button, foreground, border);

            _searchBox.TextChanged += delegate { _layerView.Refresh(); };
            _layerList.SelectionChanged += delegate { UpdateSelectionState(); };
            _deleteObjectsOption.Checked += delegate { UpdateActionState(); };
            _moveObjectsOption.Checked += delegate { UpdateActionState(); };
            _targetLayerBox.SelectionChanged += delegate { UpdateActionState(); };
            _applyButton.Click += delegate { AcceptSelection(); };
            cancelButton.Click += delegate { DialogResult = false; };

            Grid content = new Grid { Margin = new Thickness(16) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock description = new TextBlock
            {
                Text = "Escolha a layer que deseja remover:",
                Foreground = foreground,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetRow(description, 0);
            content.Children.Add(description);
            Grid.SetRow(_searchBox, 1);
            content.Children.Add(_searchBox);
            Grid.SetRow(_layerList, 2);
            content.Children.Add(_layerList);

            StackPanel actionPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            actionPanel.Children.Add(_statusText);
            actionPanel.Children.Add(_deleteObjectsOption);
            actionPanel.Children.Add(_moveObjectsOption);
            actionPanel.Children.Add(_targetLayerBox);
            Grid.SetRow(actionPanel, 3);
            content.Children.Add(actionPanel);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(_applyButton);
            Grid.SetRow(buttons, 4);
            content.Children.Add(buttons);
            Content = content;
        }

        internal static LayerDeleteRequest Choose(
            IList<string> layers,
            IDictionary<string, int> usageCounts,
            string currentLayer)
        {
            LayerDeleteDialog dialog = new LayerDeleteDialog(layers, usageCounts, currentLayer, IsLightTheme());
            IntPtr owner = ComponentManager.ApplicationWindow;
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(dialog).Owner = owner;

            return dialog.ShowDialog() == true ? dialog.SelectedRequest : null;
        }

        internal LayerDeleteRequest SelectedRequest { get; private set; }

        private bool FilterLayer(object item)
        {
            string search = _searchBox.Text;
            return string.IsNullOrWhiteSpace(search) ||
                ((string)item).IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateSelectionState()
        {
            string selectedLayer = _layerList.SelectedItem as string;
            _deleteObjectsOption.IsChecked = false;
            _moveObjectsOption.IsChecked = false;
            _deleteObjectsOption.IsEnabled = false;
            _moveObjectsOption.IsEnabled = false;
            _targetLayerBox.IsEnabled = false;
            _targetLayerBox.ItemsSource = null;
            _applyButton.IsEnabled = false;

            if (selectedLayer == null)
            {
                _statusText.Text = "Selecione uma layer para ver as opções.";
                _applyButton.Content = "Selecione uma layer";
                return;
            }

            if (string.Equals(selectedLayer, _currentLayer, StringComparison.OrdinalIgnoreCase))
            {
                _statusText.Text = "A layer atual não pode ser removida. Altere a layer atual antes de usar este comando.";
                _applyButton.Content = "Layer atual";
                return;
            }

            int usageCount = _usageCounts.TryGetValue(selectedLayer, out int count) ? count : 0;
            if (usageCount == 0)
            {
                _statusText.Text = "Esta layer não contém objetos. Ela será removida.";
                _applyButton.Content = "Excluir layer";
                _applyButton.IsEnabled = true;
                return;
            }

            _statusText.Text = "Encontrados " + usageCount + " objeto(s) na layer, incluindo definições de bloco.";
            _deleteObjectsOption.IsEnabled = true;
            _moveObjectsOption.IsEnabled = true;
            List<string> targets = _layers
                .Where(layer => !string.Equals(layer, selectedLayer, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _targetLayerBox.ItemsSource = targets;
            _targetLayerBox.SelectedIndex = targets.Count > 0 ? 0 : -1;
            UpdateActionState();
        }

        private void UpdateActionState()
        {
            string selectedLayer = _layerList.SelectedItem as string;
            if (selectedLayer == null || string.Equals(selectedLayer, _currentLayer, StringComparison.OrdinalIgnoreCase))
                return;

            int usageCount = _usageCounts.TryGetValue(selectedLayer, out int count) ? count : 0;
            if (usageCount == 0)
            {
                _applyButton.Content = "Excluir layer";
                _applyButton.IsEnabled = true;
                _targetLayerBox.IsEnabled = false;
                return;
            }

            if (_deleteObjectsOption.IsChecked == true)
            {
                _applyButton.Content = "Excluir objetos e layer";
                _applyButton.IsEnabled = true;
                _targetLayerBox.IsEnabled = false;
            }
            else if (_moveObjectsOption.IsChecked == true)
            {
                _applyButton.Content = "Mover e excluir layer";
                _targetLayerBox.IsEnabled = true;
                _applyButton.IsEnabled = _targetLayerBox.SelectedItem != null;
            }
            else
            {
                _applyButton.Content = "Escolha uma ação";
                _applyButton.IsEnabled = false;
                _targetLayerBox.IsEnabled = false;
            }
        }

        private void AcceptSelection()
        {
            string selectedLayer = _layerList.SelectedItem as string;
            if (selectedLayer == null)
                return;

            int usageCount = _usageCounts.TryGetValue(selectedLayer, out int count) ? count : 0;
            LayerDeleteAction action = LayerDeleteAction.DeleteObjects;
            string targetLayer = null;
            if (usageCount > 0)
            {
                if (_deleteObjectsOption.IsChecked == true)
                    action = LayerDeleteAction.DeleteObjects;
                else if (_moveObjectsOption.IsChecked == true && _targetLayerBox.SelectedItem is string selectedTarget)
                {
                    action = LayerDeleteAction.MoveObjects;
                    targetLayer = selectedTarget;
                }
                else
                    return;
            }

            SelectedRequest = new LayerDeleteRequest
            {
                LayerName = selectedLayer,
                Action = action,
                TargetLayerName = targetLayer
            };
            DialogResult = true;
        }

        private static Button CreateButton(string text, bool isDefault, Brush background, Brush foreground, Brush border)
        {
            return new Button
            {
                Content = text,
                MinWidth = 112,
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
