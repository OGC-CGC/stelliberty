using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Stelliberty.Presentation.Formatting;

namespace Stelliberty.Desktop.Controls;

public sealed class Sparkline : Control
{
    private const double CurveTension = 1d / 6d;
    private const int AxisDivisions = 4;
    private const double AxisLabelFontSize = 11;
    private const double AxisLabelGap = 8;
    private const double AxisGutterWidth = 44;
    private static readonly Typeface AxisTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(StrokeThickness), 2d);

    public static readonly StyledProperty<IBrush?> AxisLabelBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(AxisLabelBrush));

    public static readonly StyledProperty<IBrush?> GridLineBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(GridLineBrush));

    public static readonly StyledProperty<double> AxisMaximumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(AxisMaximum));

    public static readonly StyledProperty<bool> DisplayTrafficInBytesProperty =
        AvaloniaProperty.Register<Sparkline, bool>(nameof(DisplayTrafficInBytes));

    private IBrush? _anchoredSource;
    private IBrush? _anchoredFill;
    private double _anchoredTop = double.NaN;
    private double _anchoredBottom = double.NaN;

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, FillProperty, StrokeThicknessProperty,
            AxisLabelBrushProperty, GridLineBrushProperty, AxisMaximumProperty, DisplayTrafficInBytesProperty);
    }

    public Sparkline()
    {
        IsHitTestVisible = false;
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? AxisLabelBrush
    {
        get => GetValue(AxisLabelBrushProperty);
        set => SetValue(AxisLabelBrushProperty, value);
    }

    public IBrush? GridLineBrush
    {
        get => GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    public double AxisMaximum
    {
        get => GetValue(AxisMaximumProperty);
        set => SetValue(AxisMaximumProperty, value);
    }

    public bool DisplayTrafficInBytes
    {
        get => GetValue(DisplayTrafficInBytesProperty);
        set => SetValue(DisplayTrafficInBytesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var values = Values;
        var width = Bounds.Width;
        var height = Bounds.Height;
        var maximum = AxisMaximum;
        if (values is null || values.Count < 2 || maximum <= 0)
        {
            return;
        }

        var labels = CreateAxisLabels(maximum);
        var left = Math.Max(AxisGutterWidth, labels.Max(label => label.Width) + AxisLabelGap);
        var verticalPadding = Math.Max(StrokeThickness, labels[0].Height / 2);
        if (width <= left || height <= verticalPadding * 2)
        {
            return;
        }

        var plot = new Rect(left, verticalPadding, width - left, height - verticalPadding * 2);
        var points = BuildPoints(values, plot, plot.Height / maximum);
        DrawVerticalAxis(context, plot, labels);

        using (context.PushClip(new Rect(plot.Left, 0, plot.Width, height)))
        {
            DrawFill(context, points, plot);
            DrawLine(context, points, plot);
        }
    }

    private FormattedText[] CreateAxisLabels(double maximum)
    {
        var labels = new FormattedText[AxisDivisions + 1];
        var culture = CultureInfo.CurrentCulture;
        for (var i = 0; i < labels.Length; i++)
        {
            var rate = maximum * i / AxisDivisions;
            labels[i] = new FormattedText(
                TrafficSize.FormatAxisRate(rate, DisplayTrafficInBytes), culture, FlowDirection.LeftToRight,
                AxisTypeface, AxisLabelFontSize, AxisLabelBrush ?? Brushes.Gray);
        }

        return labels;
    }

    private void DrawVerticalAxis(DrawingContext context, Rect plot, IReadOnlyList<FormattedText> labels)
    {
        if (GridLineBrush is { } gridBrush)
        {
            var pen = new Pen(gridBrush);
            using (context.PushOpacity(0.5))
            {
                for (var i = 0; i < labels.Count; i++)
                {
                    var y = GetTickY(plot, i);
                    context.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
                }
            }
        }

        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            context.DrawText(label, new Point(
                plot.Left - AxisLabelGap - label.Width, GetTickY(plot, i) - label.Height / 2));
        }
    }

    private static double GetTickY(Rect plot, int index) => plot.Bottom - plot.Height * index / AxisDivisions;

    private void DrawFill(DrawingContext context, IReadOnlyList<Point> points, Rect plot)
    {
        if (ResolveFill(plot.Top, plot.Bottom) is not { } fill)
        {
            return;
        }

        var area = new StreamGeometry();
        using (var geometry = area.Open())
        {
            geometry.BeginFigure(new Point(points[0].X, plot.Bottom), true);
            geometry.LineTo(points[0]);
            AppendSmoothCurve(geometry, points, plot);
            geometry.LineTo(new Point(points[^1].X, plot.Bottom));
            geometry.EndFigure(true);
        }

        context.DrawGeometry(fill, null, area);
    }

    private void DrawLine(DrawingContext context, IReadOnlyList<Point> points, Rect plot)
    {
        if (Stroke is not { } stroke)
        {
            return;
        }

        var line = new StreamGeometry();
        using (var geometry = line.Open())
        {
            geometry.BeginFigure(points[0], false);
            AppendSmoothCurve(geometry, points, plot);
            geometry.EndFigure(false);
        }

        context.DrawGeometry(
            null,
            new Pen(stroke, StrokeThickness, lineCap: PenLineCap.Flat, lineJoin: PenLineJoin.Round),
            line);
    }

    // 渐变用绘图区绝对坐标锚定，避免峰值变化时按几何边界框重新映射。
    private IBrush? ResolveFill(double top, double bottom)
    {
        if (Fill is not LinearGradientBrush gradient)
        {
            return Fill;
        }

        if (_anchoredFill is not null
            && ReferenceEquals(_anchoredSource, gradient)
            && Math.Abs(_anchoredTop - top) < 0.01d
            && Math.Abs(_anchoredBottom - bottom) < 0.01d)
        {
            return _anchoredFill;
        }

        var anchored = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, top, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, bottom, RelativeUnit.Absolute),
            SpreadMethod = gradient.SpreadMethod,
            Opacity = gradient.Opacity,
        };
        foreach (var stop in gradient.GradientStops)
        {
            anchored.GradientStops.Add(new GradientStop(stop.Color, stop.Offset));
        }

        _anchoredSource = gradient;
        _anchoredTop = top;
        _anchoredBottom = bottom;
        _anchoredFill = anchored;
        return anchored;
    }

    private static Point[] BuildPoints(IReadOnlyList<double> values, Rect plot, double scale)
    {
        var points = new Point[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            points[i] = new Point(plot.Left + plot.Width * (i / (values.Count - 1d)), plot.Bottom - values[i] * scale);
        }

        return points;
    }

    private static void AppendSmoothCurve(StreamGeometryContext context, IReadOnlyList<Point> points, Rect plot)
    {
        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[i == 0 ? 0 : i - 1];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[i + 2 < points.Count ? i + 2 : points.Count - 1];
            GetCubicControls(p0, p1, p2, p3, plot, out var c1, out var c2);
            context.CubicBezierTo(c1, c2, p2);
        }
    }

    private static void GetCubicControls(Point p0, Point p1, Point p2, Point p3, Rect plot, out Point c1, out Point c2)
    {
        c1 = new Point(
            p1.X + (p2.X - p0.X) * CurveTension,
            Math.Clamp(p1.Y + (p2.Y - p0.Y) * CurveTension, plot.Top, plot.Bottom));
        c2 = new Point(
            p2.X - (p3.X - p1.X) * CurveTension,
            Math.Clamp(p2.Y - (p3.Y - p1.Y) * CurveTension, plot.Top, plot.Bottom));
    }
}
