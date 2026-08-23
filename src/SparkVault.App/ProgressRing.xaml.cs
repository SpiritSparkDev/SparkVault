using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace SparkVault.App;

// Circular percent indicator (Ellipse track + arc drawn via StrokeDashArray-free ArcSegment
// geometry, since WPF has no conic-gradient/native ring primitive). Used for the Übersicht
// hero status and the running-transfer progress views, both idle and live.
public partial class ProgressRing : UserControl
{
    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(nameof(Percent), typeof(double), typeof(ProgressRing),
            new PropertyMetadata(0.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty DiameterProperty =
        DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(ProgressRing),
            new PropertyMetadata(160.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty RingThicknessProperty =
        DependencyProperty.Register(nameof(RingThickness), typeof(double), typeof(ProgressRing),
            new PropertyMetadata(14.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty RingBrushProperty =
        DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(ProgressRing),
            new PropertyMetadata(null, OnRingBrushChanged));

    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }
    public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }
    public Brush? RingBrush { get => (Brush?)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }

    public string CenterValue { get => CenterValueText.Text; set => CenterValueText.Text = value; }
    public string CenterLabel { get => CenterLabelText.Text; set => CenterLabelText.Text = value; }

    public ProgressRing()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateVisuals();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ProgressRing)d).UpdateVisuals();

    private static void OnRingBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (ProgressRing)d;
        ring.ArcPath.Stroke = ring.RingBrush ?? (Brush)ring.FindResource("RingGradientBrush");
    }

    private void UpdateVisuals()
    {
        var diameter = Diameter;
        var thickness = RingThickness;
        var radius = (diameter - thickness) / 2;
        var center = diameter / 2;

        RootGrid.Width = RootGrid.Height = diameter;
        TrackEllipse.Width = TrackEllipse.Height = diameter;
        TrackEllipse.StrokeThickness = thickness;
        ArcPath.StrokeThickness = thickness;

        var pct = System.Math.Clamp(Percent, 0, 100);
        if (pct <= 0.05)
        {
            ArcPath.Data = null;
            return;
        }

        var startPoint = PointOnCircle(center, radius, -90);
        var figure = new PathFigure { StartPoint = startPoint, IsClosed = false };

        if (pct >= 99.95)
        {
            // A full circle can't be a single ArcSegment (degenerate start == end), so split
            // it into two half-arcs instead.
            var midPoint = PointOnCircle(center, radius, 90);
            var endPoint = PointOnCircle(center, radius, 269.999);
            figure.Segments.Add(new ArcSegment(midPoint, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new ArcSegment(endPoint, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
        }
        else
        {
            var angle = pct / 100.0 * 360.0;
            var endPoint = PointOnCircle(center, radius, -90 + angle);
            figure.Segments.Add(new ArcSegment(endPoint, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true));
        }

        ArcPath.Data = new PathGeometry(new[] { figure });
    }

    private static Point PointOnCircle(double center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * System.Math.PI / 180.0;
        return new Point(center + radius * System.Math.Cos(radians), center + radius * System.Math.Sin(radians));
    }
}
