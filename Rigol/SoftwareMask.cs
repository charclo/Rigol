using System.Globalization;
using System.Text.Json;

namespace Rigol;

/// <summary>
/// Pass/fail mask evaluated on the PC instead of on the scope.
///
/// The DHO800/DHO900 :MASK subsystem can only regenerate an envelope around the waveform
/// that happens to be on screen (:MASK:X / :MASK:Y / :MASK:CREate) and offers no SCPI command
/// to load a stored *.pf file. This class stores the envelope itself, so it can be reloaded at
/// start-up without the golden signal being connected.
///
/// The envelope is built the same way the scope builds its own: the reference trace is dilated
/// by the horizontal tolerance and then widened by the vertical tolerance. For a rectangular
/// tolerance window that is exactly a running min/max over the window followed by a constant
/// offset, so the result is directly comparable with the hardware mask.
/// </summary>
public class SoftwareMask
{
    /// <summary>
    /// Acquisition settings the mask was built for. Used to detect that the scope has been
    /// reconfigured since the mask was saved, which would silently invalidate the comparison.
    /// </summary>
    public sealed class Acquisition
    {
        public int Channel { get; set; } = 1;
        public int Points { get; set; }
        /// <summary>Time between two samples, in seconds.</summary>
        public double XIncrement { get; set; }
        /// <summary>Start time of the first sample, in seconds.</summary>
        public double XOrigin { get; set; }
        /// <summary>Horizontal scale, in seconds per division.</summary>
        public double TimeScale { get; set; }
        /// <summary>Vertical scale, in volts per division.</summary>
        public double VoltScale { get; set; }

        public Acquisition Clone() => (Acquisition)MemberwiseClone();
    }

    public sealed class Result
    {
        public bool Passed { get; init; }
        public int FailedSamples { get; init; }
        public int FirstFailureIndex { get; init; } = -1;
        /// <summary>Largest excursion outside the band, in volts. Zero when the frame passed.</summary>
        public double WorstDeviation { get; init; }
        public int WorstDeviationIndex { get; init; } = -1;
    }

    public Acquisition Settings { get; set; } = new();
    /// <summary>Horizontal tolerance in divisions, matching :MASK:X (0.01 to 2 div).</summary>
    public double XToleranceDiv { get; set; }
    /// <summary>Vertical tolerance in divisions, matching :MASK:Y (0.04 to 2 div).</summary>
    public double YToleranceDiv { get; set; }
    /// <summary>Number of reference frames the envelope was built from.</summary>
    public int ReferenceFrames { get; set; }
    public double[] Lower { get; set; } = [];
    public double[] Upper { get; set; } = [];

    /// <summary>Horizontal tolerance expressed in samples, as applied when the mask was built.</summary>
    public int ToleranceSamples { get; set; }

    /// <summary>
    /// Build a mask from one or more reference frames of a known-good signal. Using several
    /// frames folds the trigger jitter and noise of the reference into the envelope.
    /// </summary>
    public static SoftwareMask Create(
        IReadOnlyList<double[]> referenceFrames,
        Acquisition acquisition,
        double xToleranceDiv,
        double yToleranceDiv)
    {
        ArgumentNullException.ThrowIfNull(referenceFrames);
        if (referenceFrames.Count == 0)
            throw new ArgumentException("At least one reference frame is required.", nameof(referenceFrames));

        int points = referenceFrames[0].Length;
        if (points == 0)
            throw new ArgumentException("Reference frames are empty.", nameof(referenceFrames));

        foreach (double[] frame in referenceFrames)
        {
            if (frame.Length != points)
                throw new ArgumentException("All reference frames must have the same length.", nameof(referenceFrames));
        }

        // Envelope of the reference set itself, before any tolerance is applied.
        double[] referenceMin = new double[points];
        double[] referenceMax = new double[points];
        for (int i = 0; i < points; i++)
        {
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            foreach (double[] frame in referenceFrames)
            {
                if (frame[i] < min) min = frame[i];
                if (frame[i] > max) max = frame[i];
            }
            referenceMin[i] = min;
            referenceMax[i] = max;
        }

        int radius = ToleranceInSamples(xToleranceDiv, acquisition);
        double yTolerance = yToleranceDiv * acquisition.VoltScale;

        double[] lower = new double[points];
        double[] upper = new double[points];
        for (int i = 0; i < points; i++)
        {
            int from = Math.Max(0, i - radius);
            int to = Math.Min(points - 1, i + radius);

            double min = referenceMin[from];
            double max = referenceMax[from];
            for (int j = from + 1; j <= to; j++)
            {
                if (referenceMin[j] < min) min = referenceMin[j];
                if (referenceMax[j] > max) max = referenceMax[j];
            }

            lower[i] = min - yTolerance;
            upper[i] = max + yTolerance;
        }

        Acquisition settings = acquisition.Clone();
        settings.Points = points;

        return new SoftwareMask
        {
            Settings = settings,
            XToleranceDiv = xToleranceDiv,
            YToleranceDiv = yToleranceDiv,
            ReferenceFrames = referenceFrames.Count,
            ToleranceSamples = radius,
            Lower = lower,
            Upper = upper,
        };
    }

    /// <summary>
    /// Convert a tolerance in horizontal divisions to a number of samples.
    /// </summary>
    public static int ToleranceInSamples(double toleranceDiv, Acquisition acquisition)
    {
        if (acquisition.XIncrement <= 0)
            throw new ArgumentException("XIncrement must be positive.", nameof(acquisition));

        double samplesPerDivision = acquisition.TimeScale / acquisition.XIncrement;
        return (int)Math.Round(toleranceDiv * samplesPerDivision, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Compare one acquired frame against the mask.
    /// </summary>
    /// <param name="samples">One acquired frame, same length as the mask.</param>
    /// <param name="ignoreEdgeSamples">
    /// Number of samples to skip at both ends of the frame. Within <see cref="ToleranceSamples"/>
    /// of an edge the horizontal tolerance cannot be applied in both directions, so the band is
    /// one-sided there and a signal that is merely shifted in time reads as a failure. Pass
    /// <see cref="ToleranceSamples"/> to judge only the part of the frame where the horizontal
    /// tolerance is fully effective. The default of 0 matches the scope's own pass/fail test,
    /// which has the same edge behaviour.
    /// </param>
    public Result Test(double[] samples, int ignoreEdgeSamples = 0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length != Lower.Length)
            throw new ArgumentException($"Frame has {samples.Length} samples, mask has {Lower.Length}.", nameof(samples));
        ArgumentOutOfRangeException.ThrowIfNegative(ignoreEdgeSamples);

        int from = Math.Min(ignoreEdgeSamples, samples.Length);
        int to = Math.Max(from, samples.Length - ignoreEdgeSamples);

        int failed = 0;
        int firstFailure = -1;
        double worst = 0;
        int worstIndex = -1;

        for (int i = from; i < to; i++)
        {
            double deviation = 0;
            if (samples[i] > Upper[i])
                deviation = samples[i] - Upper[i];
            else if (samples[i] < Lower[i])
                deviation = Lower[i] - samples[i];

            if (deviation <= 0)
                continue;

            failed++;
            if (firstFailure < 0)
                firstFailure = i;
            if (deviation > worst)
            {
                worst = deviation;
                worstIndex = i;
            }
        }

        return new Result
        {
            Passed = failed == 0,
            FailedSamples = failed,
            FirstFailureIndex = firstFailure,
            WorstDeviation = worst,
            WorstDeviationIndex = worstIndex,
        };
    }

    /// <summary>
    /// Time of a sample index, in seconds relative to the trigger.
    /// </summary>
    public double TimeAt(int index) => Settings.XOrigin + (index * Settings.XIncrement);

    /// <summary>
    /// Report why the mask does not match the current acquisition settings, or null when it does.
    /// The mask is stored in absolute volts, so a changed vertical scale does not invalidate it;
    /// a changed point count or timebase does.
    /// </summary>
    public string? CheckCompatibility(Acquisition current)
    {
        if (current.Points != Settings.Points)
            return $"point count differs: mask {Settings.Points}, scope {current.Points}";

        if (!NearlyEqual(current.XIncrement, Settings.XIncrement))
            return FormattableString.Invariant(
                $"sample interval differs: mask {Settings.XIncrement:G6} s, scope {current.XIncrement:G6} s");

        if (!NearlyEqual(current.TimeScale, Settings.TimeScale))
            return FormattableString.Invariant(
                $"timebase differs: mask {Settings.TimeScale:G6} s/div, scope {current.TimeScale:G6} s/div");

        if (current.Channel != Settings.Channel)
            return $"channel differs: mask CH{Settings.Channel}, scope CH{current.Channel}";

        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static SoftwareMask Load(string path)
    {
        SoftwareMask? mask = JsonSerializer.Deserialize<SoftwareMask>(File.ReadAllText(path));
        if (mask is null || mask.Lower.Length == 0 || mask.Lower.Length != mask.Upper.Length)
            throw new InvalidDataException($"'{path}' does not contain a valid mask.");

        return mask;
    }

    /// <summary>
    /// Write the envelope to CSV so it can be plotted next to a captured waveform.
    /// </summary>
    public void ExportCsv(string path)
    {
        using StreamWriter writer = new(path);
        writer.WriteLine("time_s,lower_v,upper_v");
        for (int i = 0; i < Lower.Length; i++)
        {
            writer.WriteLine(FormattableString.Invariant($"{TimeAt(i):G9},{Lower[i]:G9},{Upper[i]:G9}"));
        }
    }

    public override string ToString()
    {
        return string.Format(CultureInfo.InvariantCulture,
            "CH{0}, {1} points, {2:G4} s/div, X {3:0.###} div ({4} samples), Y {5:0.###} div ({6:G4} V), {7} reference frame(s)",
            Settings.Channel, Settings.Points, Settings.TimeScale,
            XToleranceDiv, ToleranceSamples,
            YToleranceDiv, YToleranceDiv * Settings.VoltScale,
            ReferenceFrames);
    }

    private static bool NearlyEqual(double a, double b)
    {
        double scale = Math.Max(Math.Abs(a), Math.Abs(b));
        return Math.Abs(a - b) <= (scale * 1e-6);
    }
}
