using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace NoBorder
{
    public partial class ColorPickerDialog : Window
    {
        /// <summary>The chosen color, set when the user clicks "Use this color". Null if cancelled.</summary>
        public Color? SelectedColor { get; private set; }

        private double _hue;        // 0-360
        private double _saturation;  // 0-1
        private double _value;       // 0-1 (this is HSV "value"/brightness, the square's vertical axis)

        private bool _isDraggingSquare;
        private bool _isDraggingHue;
        private bool _isUpdatingFromCode; // suppresses TextChanged feedback loops while we write computed values

        public ColorPickerDialog(Color initialColor)
        {
            InitializeComponent();
            Owner = Application.Current.MainWindow;

            BuildHueGradient();
            SetFromColor(initialColor);

            Loaded += (_, _) => UpdateAllVisuals();
        }

        // ---------------- Color <-> HSV conversion ----------------

        private void SetFromColor(Color color)
        {
            RgbToHsv(color.R, color.G, color.B, out _hue, out _saturation, out _value);
        }

        private Color CurrentColor()
        {
            HsvToRgb(_hue, _saturation, _value, out byte r, out byte g, out byte b);
            return Color.FromRgb(r, g, b);
        }

        private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
        {
            double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
            double max = Math.Max(rf, Math.Max(gf, bf));
            double min = Math.Min(rf, Math.Min(gf, bf));
            double delta = max - min;

            v = max;
            s = max <= 0 ? 0 : delta / max;

            if (delta <= 0.00001)
            {
                h = 0;
            }
            else if (max == rf)
            {
                h = 60 * (((gf - bf) / delta) % 6);
            }
            else if (max == gf)
            {
                h = 60 * (((bf - rf) / delta) + 2);
            }
            else
            {
                h = 60 * (((rf - gf) / delta) + 4);
            }
            if (h < 0) h += 360;
        }

        private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;

            double rf, gf, bf;
            if (h < 60) { rf = c; gf = x; bf = 0; }
            else if (h < 120) { rf = x; gf = c; bf = 0; }
            else if (h < 180) { rf = 0; gf = c; bf = x; }
            else if (h < 240) { rf = 0; gf = x; bf = c; }
            else if (h < 300) { rf = x; gf = 0; bf = c; }
            else { rf = c; gf = 0; bf = x; }

            r = (byte)Math.Round((rf + m) * 255);
            g = (byte)Math.Round((gf + m) * 255);
            b = (byte)Math.Round((bf + m) * 255);
        }

        // ---------------- Visual updates ----------------

        private void BuildHueGradient()
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            // Six stops at the primary/secondary hue boundaries (0/60/120/180/240/300/360)
            // is enough for a smooth-looking hue ramp without needing per-pixel stops.
            (double Offset, double Hue)[] stops =
            {
                (0.0, 0), (1.0 / 6, 60), (2.0 / 6, 120), (3.0 / 6, 180), (4.0 / 6, 240), (5.0 / 6, 300), (1.0, 360)
            };
            foreach (var (offset, hue) in stops)
            {
                HsvToRgb(hue, 1.0, 1.0, out byte r, out byte g, out byte b);
                gradient.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), offset));
            }
            HueGradientRect.Fill = gradient;
        }

        private void UpdateAllVisuals()
        {
            _isUpdatingFromCode = true;

            // Hue backdrop for the sat/lightness square: a fully-saturated, full-value
            // version of the current hue, which the white/black gradients overlay.
            HsvToRgb(_hue, 1.0, 1.0, out byte hr, out byte hg, out byte hb);
            HueBackdrop.Fill = new SolidColorBrush(Color.FromRgb(hr, hg, hb));

            double squareW = SatLightSquare.ActualWidth;
            double squareH = SatLightSquare.ActualHeight;
            if (squareW > 0 && squareH > 0)
            {
                Canvas.SetLeft(SatLightCursor, _saturation * squareW - SatLightCursor.Width / 2);
                Canvas.SetTop(SatLightCursor, (1 - _value) * squareH - SatLightCursor.Height / 2);
            }

            double hueSliderW = HueSliderGrid.ActualWidth;
            if (hueSliderW > 0)
            {
                Canvas.SetLeft(HueCursor, (_hue / 360.0) * hueSliderW - HueCursor.Width / 2);
            }

            var color = CurrentColor();
            PreviewSwatch.Background = new SolidColorBrush(color);
            HexInput.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            RInput.Text = color.R.ToString();
            GInput.Text = color.G.ToString();
            BInput.Text = color.B.ToString();

            _isUpdatingFromCode = false;
        }

        // ---------------- Saturation/Lightness square interaction ----------------

        private void SatLightSquare_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSquare = true;
            UpdateSatLightFromMouse(e.GetPosition(SatLightSquare));
            ((UIElement)sender).CaptureMouse();
        }

        private void SatLightSquare_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingSquare)
            {
                UpdateSatLightFromMouse(e.GetPosition(SatLightSquare));
            }
        }

        private void SatLightSquare_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSquare = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }

        private void UpdateSatLightFromMouse(Point p)
        {
            double w = SatLightSquare.ActualWidth;
            double h = SatLightSquare.ActualHeight;
            if (w <= 0 || h <= 0) return;

            _saturation = Math.Clamp(p.X / w, 0, 1);
            _value = 1 - Math.Clamp(p.Y / h, 0, 1);
            UpdateAllVisuals();
        }

        // ---------------- Hue slider interaction ----------------

        private void HueSlider_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingHue = true;
            UpdateHueFromMouse(e.GetPosition(HueSliderGrid));
            ((UIElement)sender).CaptureMouse();
        }

        private void HueSlider_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingHue)
            {
                UpdateHueFromMouse(e.GetPosition(HueSliderGrid));
            }
        }

        private void HueSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingHue = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }

        private void UpdateHueFromMouse(Point p)
        {
            double w = HueSliderGrid.ActualWidth;
            if (w <= 0) return;

            _hue = Math.Clamp(p.X / w, 0, 1) * 360;
            UpdateAllVisuals();
        }

        // ---------------- Text input sync ----------------

        private void HexInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingFromCode) return;

            string text = HexInput.Text.Trim().TrimStart('#');
            if (text.Length != 6 && text.Length != 8) return;

            try
            {
                int offset = text.Length == 8 ? 2 : 0; // skip alpha if present
                byte r = Convert.ToByte(text.Substring(offset, 2), 16);
                byte g = Convert.ToByte(text.Substring(offset + 2, 2), 16);
                byte b = Convert.ToByte(text.Substring(offset + 4, 2), 16);
                SetFromColor(Color.FromRgb(r, g, b));
                UpdateAllVisuals();
            }
            catch (FormatException)
            {
                // Mid-typing invalid hex (e.g. "#7F7") - just wait for more characters
                // rather than showing an error for an incomplete entry.
            }
        }

        private void RgbInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingFromCode) return;

            if (byte.TryParse(RInput.Text, out byte r) &&
                byte.TryParse(GInput.Text, out byte g) &&
                byte.TryParse(BInput.Text, out byte b))
            {
                SetFromColor(Color.FromRgb(r, g, b));
                UpdateAllVisuals();
            }
        }

        // ---------------- Window chrome ----------------

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedColor = null;
            DialogResult = false;
            Close();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedColor = CurrentColor();
            DialogResult = true;
            Close();
        }
    }
}
