using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;

namespace NoBorder
{
    public partial class WindowPickerDialog : Window
    {
        /// <summary>Set when the user picks a window; null if they cancel. Holds the process
        /// name (preferred match key) or falls back to the title if the process name lookup
        /// failed for that window.</summary>
        public string? SelectedPattern { get; private set; }

        public WindowPickerDialog(List<BorderRemovalEngine.OpenWindowInfo> windows)
        {
            InitializeComponent();
            Owner = Application.Current.Windows.Count > 0 ? Application.Current.MainWindow : null;

            foreach (var info in windows)
            {
                string pattern = info.ProcessName.Length > 0 ? info.ProcessName : info.Title;

                var row = new Button
                {
                    Style = (Style)FindResource("QuietButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 2),
                    Tag = pattern
                };

                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = info.ProcessName.Length > 0 ? info.ProcessName : info.Title,
                    Style = (Style)FindResource("BodyText")
                });
                stack.Children.Add(new TextBlock
                {
                    Text = info.Title,
                    Style = (Style)FindResource("LabelMuted"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0)
                });
                row.Content = stack;
                row.Click += Row_Click;

                WindowList.Items.Add(row);
            }
        }

        private void Row_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string pattern })
            {
                SelectedPattern = pattern;
                DialogResult = true;
                Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
