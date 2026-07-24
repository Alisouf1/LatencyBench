using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LatencyBench.App.Controls;

/// <summary>
/// Small hand-drawn vector icons built from basic shapes (rectangles, lines, polygons) rather
/// than an icon font, so rendering doesn't depend on guessing the right glyph codepoint.
/// </summary>
public partial class Icon : UserControl
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty IconBrushProperty =
        DependencyProperty.Register(nameof(IconBrush), typeof(Brush), typeof(Icon), new PropertyMetadata(Brushes.Black, OnChanged));

    public string? Kind
    {
        get => (string?)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush IconBrush
    {
        get => (Brush)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public Icon()
    {
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Icon icon && icon.IsLoaded)
        {
            icon.Rebuild();
        }
    }

    private void Rebuild()
    {
        Host.Children.Clear();
        var brush = IconBrush;

        switch (Kind)
        {
            case "Dashboard":
                AddRect(2, 2, 5, 5, brush);
                AddRect(9, 2, 5, 5, brush);
                AddRect(2, 9, 5, 5, brush);
                AddRect(9, 9, 5, 5, brush);
                break;

            case "PortTest":
                AddPolygon(brush, (10, 0), (3, 9), (7, 9), (5, 16), (13, 7), (9, 7));
                break;

            case "DpcIsr":
                AddPolyline(brush, (0, 12), (4, 6), (7, 9), (11, 2), (16, 7));
                break;

            case "UsbTree":
                AddLine(8, 4, 3, 12, brush);
                AddLine(8, 4, 13, 12, brush);
                AddEllipse(6, 1, 4, 4, brush);
                AddEllipse(1, 11, 4, 4, brush);
                AddEllipse(11, 11, 4, 4, brush);
                break;

            case "Affinity":
                AddRectOutline(2.5, 2.5, 11, 11, brush);
                AddRect(6, 6, 4, 4, brush);
                break;

            case "Advisor":
                AddPolygon(brush, (8, 0), (10, 6), (16, 8), (10, 10), (8, 16), (6, 10), (0, 8), (6, 6));
                break;

            case "Plug":
                AddRectOutline(3, 6, 10, 8, brush);
                AddLine(6, 6, 6, 1, brush);
                AddLine(10, 6, 10, 1, brush);
                break;

            case "Tweaks":
                AddLine(1, 3, 15, 3, brush);
                AddLine(1, 8, 15, 8, brush);
                AddLine(1, 13, 15, 13, brush);
                AddEllipse(4, 1.5, 3, 3, brush);
                AddEllipse(10, 6.5, 3, 3, brush);
                AddEllipse(6, 11.5, 3, 3, brush);
                break;

            case "Msi":
                AddRectOutline(1, 3, 14, 10, brush);
                AddLine(1, 3, 8, 9, brush);
                AddLine(15, 3, 8, 9, brush);
                break;

            case "Mouse":
                AddRectOutline(4, 1.5, 8, 13, brush);
                AddLine(8, 1.5, 8, 6, brush);
                AddEllipse(7, 3, 2, 2.5, brush);
                break;

            case "Cpu":
                AddRectOutline(4, 4, 8, 8, brush);
                AddLine(6, 1, 6, 4, brush);
                AddLine(10, 1, 10, 4, brush);
                AddLine(6, 12, 6, 15, brush);
                AddLine(10, 12, 10, 15, brush);
                AddLine(1, 6, 4, 6, brush);
                AddLine(1, 10, 4, 10, brush);
                AddLine(12, 6, 15, 6, brush);
                AddLine(12, 10, 15, 10, brush);
                break;
        }
    }

    private void AddRect(double x, double y, double w, double h, Brush brush)
    {
        var rect = new Rectangle { Width = w, Height = h, Fill = brush, RadiusX = 1.2, RadiusY = 1.2 };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        Host.Children.Add(rect);
    }

    private void AddRectOutline(double x, double y, double w, double h, Brush brush)
    {
        var rect = new Rectangle { Width = w, Height = h, Stroke = brush, StrokeThickness = 1.4, RadiusX = 1.5, RadiusY = 1.5 };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        Host.Children.Add(rect);
    }

    private void AddEllipse(double x, double y, double w, double h, Brush brush)
    {
        var ellipse = new Ellipse { Width = w, Height = h, Fill = brush };
        Canvas.SetLeft(ellipse, x);
        Canvas.SetTop(ellipse, y);
        Host.Children.Add(ellipse);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush)
    {
        var line = new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Host.Children.Add(line);
    }

    private void AddPolygon(Brush brush, params (double X, double Y)[] points)
    {
        var polygon = new Polygon { Fill = brush };
        foreach (var (x, y) in points)
        {
            polygon.Points.Add(new Point(x, y));
        }

        Host.Children.Add(polygon);
    }

    private void AddPolyline(Brush brush, params (double X, double Y)[] points)
    {
        var polyline = new Polyline
        {
            Stroke = brush,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        foreach (var (x, y) in points)
        {
            polyline.Points.Add(new Point(x, y));
        }

        Host.Children.Add(polyline);
    }
}
