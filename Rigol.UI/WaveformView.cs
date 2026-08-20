using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SoftwareMask = Rigol.SoftwareMask;

namespace RigolUI;

/// <summary>
/// Draws one acquired frame together with the mask envelope, highlighting the samples that fall
/// outside the band. Frames are screen waveforms — at most 1,000 points on a DHO, 1,200 on a
/// DS1000Z — so everything is drawn exactly; a RAW-mode record would need min/max decimation per
/// pixel column.
/// </summary>
public sealed class WaveformView : Control
{
    public static readonly StyledProperty<double[]?> SamplesProperty =
        AvaloniaProperty.Register<WaveformView, double[]?>(nameof(Samples));

    public static readonly StyledProperty<SoftwareMask?> MaskProperty =
        AvaloniaProperty.Register<WaveformView, SoftwareMask?>(nameof(Mask));

    public static readonly StyledProperty<double> XOriginProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(XOrigin));

    public static readonly StyledProperty<double> XIncrementProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(XIncrement));

    /// <summary>
    /// The instrument's vertical scale. When set, the view shows the same divisions the scope
    /// does, so one grid cell is one division and the scale only changes when the scope's does.
    /// Leave at zero to fall back to a stepped auto range.
    /// </summary>
    public static readonly StyledProperty<double> VoltsPerDivisionProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(VoltsPerDivision));

    /// <summary>
    /// The instrument's horizontal scale. When set, the view shows the same divisions the scope
    /// does, so one grid cell is one division whatever the record happens to cover. Leave at zero
    /// to stretch the record across the full width instead.
    /// </summary>
    public static readonly StyledProperty<double> SecondsPerDivisionProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(SecondsPerDivision));

    /// <summary>
    /// Graticule columns, matching the instrument: ten on a DHO, twelve on a DS1000Z. The time
    /// window one screen covers is this many divisions wide, so getting it wrong stretches the
    /// trace against the grid.
    /// </summary>
    public static readonly StyledProperty<int> HorizontalDivisionsProperty =
        AvaloniaProperty.Register<WaveformView, int>(nameof(HorizontalDivisions), 10);

    /// <summary>Graticule rows. Eight on both families, but kept alongside its horizontal twin.</summary>
    public static readonly StyledProperty<int> VerticalDivisionsProperty =
        AvaloniaProperty.Register<WaveformView, int>(nameof(VerticalDivisions), 8);

    /// <summary>
    /// Voltage on the centre line, taken from the channel's vertical position, so the trace sits
    /// at the same height as on the instrument's screen. Zero puts the centre line at 0 V.
    /// </summary>
    public static readonly StyledProperty<double> VerticalCentreProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(VerticalCentre));

    public double[]? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public SoftwareMask? Mask
    {
        get => GetValue(MaskProperty);
        set => SetValue(MaskProperty, value);
    }

    public double XOrigin
    {
        get => GetValue(XOriginProperty);
        set => SetValue(XOriginProperty, value);
    }

    public double XIncrement
    {
        get => GetValue(XIncrementProperty);
        set => SetValue(XIncrementProperty, value);
    }

    public double VoltsPerDivision
    {
        get => GetValue(VoltsPerDivisionProperty);
        set => SetValue(VoltsPerDivisionProperty, value);
    }

    public int HorizontalDivisions
    {
        get => GetValue(HorizontalDivisionsProperty);
        set => SetValue(HorizontalDivisionsProperty, value);
    }

    public int VerticalDivisions
    {
        get => GetValue(VerticalDivisionsProperty);
        set => SetValue(VerticalDivisionsProperty, value);
    }

    public double VerticalCentre
    {
        get => GetValue(VerticalCentreProperty);
        set => SetValue(VerticalCentreProperty, value);
    }

    public double SecondsPerDivision
    {
        get => GetValue(SecondsPerDivisionProperty);
        set => SetValue(SecondsPerDivisionProperty, value);
    }

    static WaveformView()
    {
        AffectsRender<WaveformView>(SamplesProperty, MaskProperty, XOriginProperty, XIncrementProperty,
            VoltsPerDivisionProperty, SecondsPerDivisionProperty,
            HorizontalDivisionsProperty, VerticalDivisionsProperty, VerticalCentreProperty);
    }

    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1c));
    private static readonly IBrush BandBrush = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6), 0.16);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xa3, 0xb8));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x2a, 0x31, 0x3c)), 1);
    private static readonly IPen CentrePen = new Pen(new SolidColorBrush(Color.FromRgb(0x4c, 0x57, 0x66)), 1);
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromRgb(0x3f, 0x49, 0x57)), 1);
    private static readonly IPen BandPen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xa5, 0xfa)), 1);
    private static readonly IPen TracePen = new Pen(new SolidColorBrush(Color.FromRgb(0xfa, 0xcc, 0x15)), 1.4);
    private static readonly IBrush FailBrush = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

    private const double MarginLeft = 68;
    private const double MarginRight = 12;
    private const double MarginTop = 12;
    private const double MarginBottom = 30;

    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);
        context.FillRectangle(Background, area);

        var plot = new Rect(
            MarginLeft,
            MarginTop,
            Math.Max(1, area.Width - MarginLeft - MarginRight),
            Math.Max(1, area.Height - MarginTop - MarginBottom));

        double[]? samples = Samples;
        SoftwareMask? mask = Mask;

        // The mask only lines up with the frame when both describe the same acquisition.
        bool maskUsable = mask is not null && samples is not null && mask.Lower.Length == samples.Length;
        int points = samples?.Length ?? (mask?.Lower.Length ?? 0);

        if (points < 2)
        {
            DrawGrid(context, plot);
            DrawCentredText(context, plot, "no frame yet");
            return;
        }

        double centre = VoltsPerDivision > 0 ? VerticalCentre : 0;
        double half = VerticalHalfRange(samples, maskUsable ? mask : null, centre);
        double minimum = centre - half;
        double maximum = centre + half;

        // The record does not always cover the instrument's full time window: :WAVeform:POINts
        // truncates rather than decimates, so a request short of the screen returns the first part
        // of the span. Placing samples by their own timestamps keeps one grid cell at one division
        // and lets a short record stop before the right edge, instead of being stretched to fit
        // and quietly changing what a division means.
        double window = SecondsPerDivision * HorizontalDivisions;
        bool timeAnchored = window > 0 && XIncrement > 0;

        double MapX(int index) => timeAnchored
            ? plot.X + (plot.Width * index * XIncrement / window)
            : plot.X + (plot.Width * index / (points - 1.0));
        double MapY(double volts) => plot.Y + (plot.Height * (maximum - volts) / (maximum - minimum));

        DrawGrid(context, plot);

        // A record longer than the window would otherwise be drawn over the axis labels.
        using (context.PushClip(plot))
        {
            if (maskUsable)
                DrawBand(context, mask!, points, MapX, MapY);

            if (samples is not null)
            {
                DrawTrace(context, samples, MapX, MapY);
                if (maskUsable)
                    DrawFailures(context, samples, mask!, MapX, MapY);
            }
        }

        DrawAxes(context, plot, minimum, maximum, points, timeAnchored);
    }

    /// <summary>
    /// Half of the vertical span, measured from <paramref name="centre"/>.
    /// </summary>
    /// <remarks>
    /// Fitting the range to each frame made the axis move continuously, which makes a waveform
    /// impossible to read: every acquisition rescaled the picture by a hair. With the instrument's
    /// scale known the window is exactly its graticule — the same divisions, around the same
    /// centre line — so the picture matches the scope's screen and one grid cell is always one
    /// division. Anything the instrument would push off its own screen is clipped here too.
    /// </remarks>
    private double VerticalHalfRange(double[]? samples, SoftwareMask? mask, double centre)
    {
        double division = VoltsPerDivision;
        if (division > 0)
            return division * (VerticalDivisions / 2.0);

        double needed = 0;

        if (mask is not null)
        {
            foreach (double value in mask.Lower) needed = Math.Max(needed, Math.Abs(value - centre));
            foreach (double value in mask.Upper) needed = Math.Max(needed, Math.Abs(value - centre));
        }

        if (samples is not null)
        {
            foreach (double value in samples) needed = Math.Max(needed, Math.Abs(value - centre));
        }

        // No instrument scale yet: step through 1-2-5 so the range still settles instead of
        // tracking every frame.
        return needed > 0 ? NiceStep(needed) : 1;
    }

    private static double NiceStep(double value)
    {
        double exponent = Math.Floor(Math.Log10(value));
        double magnitude = Math.Pow(10, exponent);
        double normalised = value / magnitude;

        double step = normalised <= 1 ? 1 : normalised <= 2 ? 2 : normalised <= 5 ? 5 : 10;
        return step * magnitude;
    }

    private void DrawGrid(DrawingContext context, Rect plot)
    {
        int columns = HorizontalDivisions;
        int rows = VerticalDivisions;

        for (int i = 1; i < columns; i++)
        {
            double x = plot.X + (plot.Width * i / columns);
            context.DrawLine(i == columns / 2 ? CentrePen : GridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
        }

        for (int i = 1; i < rows; i++)
        {
            double y = plot.Y + (plot.Height * i / rows);
            // The window is symmetric about the centre line, which carries the channel's vertical
            // position — the same line the scope draws its own trace against.
            context.DrawLine(i == rows / 2 ? CentrePen : GridPen, new Point(plot.X, y), new Point(plot.Right, y));
        }

        context.DrawRectangle(null, BorderPen, plot);
    }

    private static void DrawBand(DrawingContext context, SoftwareMask mask, int points,
        Func<int, double> mapX, Func<double, double> mapY)
    {
        var band = new StreamGeometry();
        using (StreamGeometryContext geometry = band.Open())
        {
            geometry.BeginFigure(new Point(mapX(0), mapY(mask.Upper[0])), true);
            for (int i = 1; i < points; i++)
                geometry.LineTo(new Point(mapX(i), mapY(mask.Upper[i])));
            for (int i = points - 1; i >= 0; i--)
                geometry.LineTo(new Point(mapX(i), mapY(mask.Lower[i])));
            geometry.EndFigure(true);
        }

        context.DrawGeometry(BandBrush, null, band);
        DrawPolyline(context, BandPen, points, mapX, i => mapY(mask.Upper[i]));
        DrawPolyline(context, BandPen, points, mapX, i => mapY(mask.Lower[i]));
    }

    private static void DrawTrace(DrawingContext context, double[] samples,
        Func<int, double> mapX, Func<double, double> mapY)
    {
        DrawPolyline(context, TracePen, samples.Length, mapX, i => mapY(samples[i]));
    }

    private static void DrawPolyline(DrawingContext context, IPen pen, int points,
        Func<int, double> mapX, Func<int, double> mapY)
    {
        var line = new StreamGeometry();
        using (StreamGeometryContext geometry = line.Open())
        {
            geometry.BeginFigure(new Point(mapX(0), mapY(0)), false);
            for (int i = 1; i < points; i++)
                geometry.LineTo(new Point(mapX(i), mapY(i)));
            geometry.EndFigure(false);
        }

        context.DrawGeometry(null, pen, line);
    }

    /// <summary>
    /// Mark every sample outside the band. All markers go into a single geometry: a frame that
    /// fails badly can put hundreds of them on screen, and one draw call per marker made the
    /// render cost jump exactly when the signal started failing.
    /// </summary>
    private static void DrawFailures(DrawingContext context, double[] samples, SoftwareMask mask,
        Func<int, double> mapX, Func<double, double> mapY)
    {
        const double radius = 2;

        var markers = new StreamGeometry();
        bool any = false;

        using (StreamGeometryContext geometry = markers.Open())
        {
            for (int i = 0; i < samples.Length; i++)
            {
                if (samples[i] <= mask.Upper[i] && samples[i] >= mask.Lower[i])
                    continue;

                double x = mapX(i);
                double y = mapY(samples[i]);

                geometry.BeginFigure(new Point(x - radius, y - radius), true);
                geometry.LineTo(new Point(x + radius, y - radius));
                geometry.LineTo(new Point(x + radius, y + radius));
                geometry.LineTo(new Point(x - radius, y + radius));
                geometry.EndFigure(true);

                any = true;
            }
        }

        if (any)
            context.DrawGeometry(FailBrush, null, markers);
    }

    private void DrawAxes(DrawingContext context, Rect plot, double minimum, double maximum, int points,
        bool timeAnchored)
    {
        int rows = VerticalDivisions;
        for (int i = 0; i <= rows; i++)
        {
            double volts = maximum - ((maximum - minimum) * i / rows);
            double y = plot.Y + (plot.Height * i / rows);
            DrawText(context, FormatEngineering(volts, "V"), new Point(4, y - 8), TextAlignment.Right, MarginLeft - 10);
        }

        if (!timeAnchored && XIncrement <= 0)
            return;

        int columns = HorizontalDivisions;
        for (int i = 0; i <= columns; i += 2)
        {
            // One column is one division when the instrument's scale is known. Without it there is
            // nothing to anchor to, so fall back to spreading the record over all the columns.
            double time = timeAnchored
                ? XOrigin + (SecondsPerDivision * i)
                : XOrigin + (XIncrement * (points - 1) * i / columns);

            double x = plot.X + (plot.Width * i / columns);
            DrawText(context, FormatEngineering(time, "s"), new Point(x - 30, plot.Bottom + 6), TextAlignment.Center, 60);
        }
    }

    private static void DrawCentredText(DrawingContext context, Rect plot, string text)
    {
        DrawText(context, text, new Point(plot.X, plot.Center.Y - 8), TextAlignment.Center, plot.Width);
    }

    private static void DrawText(DrawingContext context, string text, Point origin, TextAlignment alignment, double width)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, LabelBrush)
        {
            TextAlignment = alignment,
            MaxTextWidth = width,
        };

        context.DrawText(formatted, origin);
    }

    /// <summary>
    /// Format a value with an SI prefix, the way a scope labels its axes.
    /// </summary>
    private static string FormatEngineering(double value, string unit)
    {
        double magnitude = Math.Abs(value);
        (double scale, string prefix) = magnitude switch
        {
            >= 1 => (1.0, ""),
            >= 1e-3 => (1e3, "m"),
            >= 1e-6 => (1e6, "u"),
            >= 1e-9 => (1e9, "n"),
            _ => (1.0, ""),
        };

        if (magnitude < 1e-12)
            return "0 " + unit;

        return string.Format(CultureInfo.InvariantCulture, "{0:0.###} {1}{2}", value * scale, prefix, unit);
    }
}
