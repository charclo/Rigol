using System.Diagnostics;
using System.Globalization;

namespace Rigol;

/// <summary>
/// Test harness for <see cref="SoftwareMask"/>. It builds a mask from a golden signal, stores and
/// reloads it, and then runs a series of acquisitions through both the software mask and the
/// scope's own :MASK test so the two verdicts can be compared frame by frame.
/// </summary>
public static class MaskBench
{
    public sealed class Options
    {
        public int Channel { get; set; } = 1;
        /// <summary>Points per frame. NORMal mode reads the screen: 1 to 1000.</summary>
        public int Points { get; set; } = 1000;
        /// <summary>Frames of the known-good signal used to build the envelope.</summary>
        public int ReferenceFrames { get; set; } = 8;
        /// <summary>Frames acquired afterwards to exercise the mask.</summary>
        public int TestFrames { get; set; } = 30;
        /// <summary>Horizontal tolerance in divisions (:MASK:X range is 0.01 to 2).</summary>
        public double XToleranceDiv { get; set; } = 0.24;
        /// <summary>Vertical tolerance in divisions (:MASK:Y range is 0.04 to 2).</summary>
        public double YToleranceDiv { get; set; } = 0.24;
        public string MaskPath { get; set; } = "mask.json";
        /// <summary>Also configure the scope's own pass/fail test and compare verdicts.</summary>
        public bool CompareWithHardware { get; set; } = true;
        /// <summary>
        /// Samples ignored at both frame edges, where the horizontal tolerance is one-sided.
        /// Keep at 0 to stay comparable with the hardware mask, which has the same edge behaviour.
        /// </summary>
        public int IgnoreEdgeSamples { get; set; }
        /// <summary>
        /// Transfer format. ASCii returns the voltages themselves, which is far easier to read
        /// while debugging and costs about 3 ms a frame against WORD. WORD is the same data at
        /// 2 bytes per point instead of roughly 14, which only starts to matter for RAW memory
        /// reads; BYTE halves that again but discards the lower 4 bits.
        /// </summary>
        public Rigol.Waveform.Format Format { get; set; } = Rigol.Waveform.Format.ASCii;
    }

    private sealed class FrameTiming
    {
        public double TransferMs;
        public double EvaluateMs;
    }

    public static void Run(Rigol rigol, Options options)
    {
        var waveform = new Rigol.Waveform(rigol);
        var hardwareMask = new Rigol.Mask(rigol);

        Console.WriteLine($"Model {rigol.Model}, serial {rigol.SerialNumber}, firmware {rigol.SoftwareVersion}");
        Console.WriteLine();

        // --- 1. Acquisition setup -------------------------------------------------------
        rigol.SendCommand($":CHANnel{options.Channel}:DISPlay ON");
        waveform.SetSource(Enum.Parse<Rigol.Waveform.Source>($"Channel{options.Channel}"));
        waveform.SetMode(Rigol.Waveform.Mode.Normal);
        waveform.SetFormat(options.Format);
        waveform.SetPoints(options.Points);

        if (!TryReadAcquisition(rigol, waveform, options.Channel, options.Points, out SoftwareMask.Acquisition acquisition))
        {
            Console.WriteLine("Could not read the acquisition settings - aborting.");
            return;
        }

        Console.WriteLine("Acquisition");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  CH{0}  {1} pts  {2:G4} s/sample  {3:G4} s/div  {4:G4} V/div",
            acquisition.Channel, acquisition.Points, acquisition.XIncrement,
            acquisition.TimeScale, acquisition.VoltScale));
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  X tolerance {0:0.###} div = {1} samples, Y tolerance {2:0.###} div = {3:G4} V",
            options.XToleranceDiv, SoftwareMask.ToleranceInSamples(options.XToleranceDiv, acquisition),
            options.YToleranceDiv, options.YToleranceDiv * acquisition.VoltScale));
        Console.WriteLine(options.IgnoreEdgeSamples > 0
            ? $"  ignoring {options.IgnoreEdgeSamples} sample(s) at each frame edge"
            : "  judging the full frame, like the hardware mask");
        Console.WriteLine($"  free-running screen reads, {options.Format} format");
        Console.WriteLine();

        // --- 2. Capture the golden signal ------------------------------------------------
        Console.WriteLine($"Capturing {options.ReferenceFrames} reference frame(s)...");
        var referenceFrames = new List<double[]>();
        int referenceReads = 0;

        while (referenceFrames.Count < options.ReferenceFrames &&
               referenceReads < options.ReferenceFrames * 20)
        {
            referenceReads++;

            if (!TryAcquire(waveform, out double[] frame, out _))
            {
                Console.WriteLine($"  reference frame {referenceFrames.Count + 1} failed - aborting.");
                return;
            }

            // Reads are not synchronised to acquisitions, so the same frame can come back more
            // than once. Duplicates would make the envelope narrower than the signal warrants.
            if (referenceFrames.Count > 0 && IsSameFrame(frame, referenceFrames[^1]))
                continue;

            referenceFrames.Add(frame);
        }

        if (referenceFrames.Count < options.ReferenceFrames)
        {
            Console.WriteLine($"  only {referenceFrames.Count} distinct frame(s) in {referenceReads} reads - aborting.");
            return;
        }

        // Consecutive reads can differ by a point: the instrument sometimes announces one more
        // than it sends. Settle on the shortest so every frame describes the same time span.
        int frameLength = referenceFrames.Min(frame => frame.Length);
        for (int i = 0; i < referenceFrames.Count; i++)
        {
            if (referenceFrames[i].Length != frameLength)
                referenceFrames[i] = referenceFrames[i][..frameLength];
        }
        acquisition.Points = frameLength;

        // Believe the data, not the preamble: the instrument can report one more point than it
        // sends. Only follow a shortfall of a point or two - :WAVeform:POINts truncates the time
        // window rather than decimating it, so asking for fewer points would silently show only
        // the start of the instrument's window.
        int shortfall = options.Points - frameLength;
        if (frameLength > 0 && shortfall > 0 && shortfall <= 2)
        {
            waveform.SetPoints(frameLength);
            Console.WriteLine($"  instrument delivers {frameLength} points, asking for that from now on");
        }
        else if (shortfall > 2)
        {
            Console.WriteLine($"  warning: asked for {options.Points} points but got {frameLength}; "
                              + "the frame covers only part of the instrument's time window.");
        }

        SoftwareMask mask = SoftwareMask.Create(referenceFrames, acquisition, options.XToleranceDiv, options.YToleranceDiv);
        Console.WriteLine($"  mask built: {mask}");

        // --- 3. Store and reload ---------------------------------------------------------
        mask.Save(options.MaskPath);
        SoftwareMask reloaded = SoftwareMask.Load(options.MaskPath);
        bool roundTripOk = reloaded.Lower.SequenceEqual(mask.Lower) && reloaded.Upper.SequenceEqual(mask.Upper);
        Console.WriteLine($"  saved to {options.MaskPath} ({new FileInfo(options.MaskPath).Length / 1024} kB), " +
                          $"reload round-trip {(roundTripOk ? "identical" : "MISMATCH")}");

        string? incompatible = reloaded.CheckCompatibility(acquisition);
        Console.WriteLine($"  compatibility check: {incompatible ?? "matches current settings"}");

        mask.ExportCsv(Path.ChangeExtension(options.MaskPath, ".envelope.csv"));
        Console.WriteLine($"  envelope exported to {Path.ChangeExtension(options.MaskPath, ".envelope.csv")}");
        Console.WriteLine();

        mask = reloaded;

        // --- 4. Mirror the mask onto the scope's own pass/fail test ----------------------
        bool hardwareReady = false;
        if (options.CompareWithHardware)
        {
            hardwareMask.Enable(true);
            hardwareMask.SetSource(options.Channel);
            hardwareMask.Operate(Rigol.Mask.Operation.STOP);
            hardwareMask.SetX(options.XToleranceDiv);
            hardwareMask.SetY(options.YToleranceDiv);
            // Creates from the frame still on screen, which is the last reference frame.
            hardwareMask.Create();
            hardwareMask.Reset();
            hardwareMask.Operate(Rigol.Mask.Operation.RUN);

            hardwareReady = hardwareMask.GetStatistics(out _, out _, out _) == Rigol.ErrorCode.None;
            Console.WriteLine(hardwareReady
                ? "Hardware mask configured with the same tolerances."
                : "Hardware mask could not be read - continuing with the software mask only.");
            Console.WriteLine();
        }

        // --- 5. Run the comparison -------------------------------------------------------
        Console.WriteLine($"Running {options.TestFrames} test frame(s)...");
        Console.WriteLine();
        Console.WriteLine("  frame  sw      failed  worst dev   at        hw      hw n  xfer ms  eval ms");
        Console.WriteLine("  -----  ------  ------  ----------  --------  ------  ----  -------  -------");

        var timings = new List<FrameTiming>();
        int agree = 0, swFailHwPass = 0, swPassHwFail = 0, hwUnavailable = 0, ambiguous = 0;
        int duplicates = 0, reads = 0, tested = 0;
        double[]? previousFrame = null;
        int previousTotal = 0, previousFailed = 0;
        if (hardwareReady)
            hardwareMask.GetStatistics(out _, out previousFailed, out previousTotal);

        while (tested < options.TestFrames && reads < options.TestFrames * 20)
        {
            reads++;

            if (!TryAcquire(waveform, out double[] frame, out FrameTiming timing))
            {
                Console.WriteLine($"  {tested + 1,5}  read failed");
                continue;
            }

            // Reads outrun the screen refresh, so the same frame comes back repeatedly. Counting
            // it more than once would inflate the statistics.
            if (previousFrame is not null && IsSameFrame(frame, previousFrame))
            {
                duplicates++;
                continue;
            }
            previousFrame = frame;
            tested++;

            if (frame.Length > mask.Lower.Length)
                frame = frame[..mask.Lower.Length];
            else if (frame.Length != mask.Lower.Length)
            {
                Console.WriteLine($"  {tested,5}  frame has {frame.Length} points, mask has {mask.Lower.Length} - skipped");
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            SoftwareMask.Result result = mask.Test(frame, options.IgnoreEdgeSamples);
            stopwatch.Stop();
            timing.EvaluateMs = stopwatch.Elapsed.TotalMilliseconds;
            timings.Add(timing);

            string hardwareVerdict = "n/a";
            int framesTested = 0;
            if (hardwareReady &&
                hardwareMask.GetStatistics(out _, out int failedNow, out int totalNow) == Rigol.ErrorCode.None)
            {
                framesTested = totalNow - previousTotal;
                int framesFailed = failedNow - previousFailed;
                previousTotal = totalNow;
                previousFailed = failedNow;

                if (framesTested <= 0)
                {
                    hardwareVerdict = "idle";
                    hwUnavailable++;
                }
                else if (framesTested > 1)
                {
                    // The scope evaluated more frames than we read back, so the verdicts are not
                    // about the same frame and comparing them would be meaningless.
                    hardwareVerdict = framesFailed == 0 ? "pass*" : "fail*";
                    ambiguous++;
                }
                else
                {
                    bool hardwarePassed = framesFailed == 0;
                    hardwareVerdict = hardwarePassed ? "PASS" : "FAIL";

                    if (hardwarePassed == result.Passed) agree++;
                    else if (result.Passed) swPassHwFail++;
                    else swFailHwPass++;
                }
            }

            string at = result.WorstDeviationIndex >= 0
                ? FormattableString.Invariant($"{mask.TimeAt(result.WorstDeviationIndex) * 1e6:0.##} us")
                : "-";

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,5}  {1,-6}  {2,6}  {3,10}  {4,-8}  {5,-6}  {6,4}  {7,7:0.0}  {8,7:0.000}",
                tested,
                result.Passed ? "PASS" : "FAIL",
                result.FailedSamples,
                result.WorstDeviation > 0 ? FormattableString.Invariant($"{result.WorstDeviation * 1e3:0.###} mV") : "-",
                at,
                hardwareVerdict,
                framesTested,
                timing.TransferMs,
                timing.EvaluateMs));
        }

        // --- 6. Summary ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("Summary");
        if (timings.Count == 0)
        {
            Console.WriteLine("  no frames completed.");
            return;
        }

        Report("  transfer (:WAVeform:DATA?)", timings.Select(t => t.TransferMs));
        Report("  evaluate (mask compare)  ", timings.Select(t => t.EvaluateMs));

        double averageReadMs = timings.Average(t => t.TransferMs + t.EvaluateMs);
        Console.WriteLine(FormattableString.Invariant(
            $"  {averageReadMs:0.0} ms per read => {1000.0 / averageReadMs:0.0} reads/s"));
        Console.WriteLine(FormattableString.Invariant(
            $"  {reads} read(s) yielded {tested} distinct frame(s), {duplicates} repeat(s)"));
        if (reads > 0)
            Console.WriteLine(FormattableString.Invariant(
                $"  effective {1000.0 / averageReadMs * tested / reads:0.0} distinct frames/s"));
        Console.WriteLine(FormattableString.Invariant(
            $"  evaluation alone would sustain {1000.0 / Math.Max(timings.Average(t => t.EvaluateMs), 1e-6):0} frames/s"));

        if (hardwareReady)
        {
            int compared = agree + swFailHwPass + swPassHwFail;
            Console.WriteLine();
            Console.WriteLine($"  compared against hardware mask on {compared} frame(s) - indicative only:");
            Console.WriteLine("    reads are not tied to acquisitions, so the frame the scope judged");
            Console.WriteLine("    is not necessarily the frame we read back.");
            if (compared > 0)
                Console.WriteLine(FormattableString.Invariant($"    agree            {agree} ({100.0 * agree / compared:0.0}%)"));
            Console.WriteLine($"    sw FAIL, hw PASS {swFailHwPass}");
            Console.WriteLine($"    sw PASS, hw FAIL {swPassHwFail}");
            if (ambiguous > 0)
                Console.WriteLine($"    not comparable   {ambiguous} (scope evaluated >1 frame per read, marked *)");
            if (hwUnavailable > 0)
                Console.WriteLine($"    hw counter did not advance on {hwUnavailable} frame(s)");
        }
    }

    private static bool TryReadAcquisition(Rigol rigol, Rigol.Waveform waveform, int channel, int points,
        out SoftwareMask.Acquisition acquisition)
    {
        acquisition = new SoftwareMask.Acquisition { Channel = channel };

        // Let the scope trigger on its own; the preamble is meaningful once the screen is filled.
        rigol.Run();
        Thread.Sleep(200);

        if (waveform.Preamble() != Rigol.ErrorCode.None) return false;

        // Ask for exactly what the instrument holds. Requesting more makes it announce a longer
        // block than it sends, and every read then waits out the difference: 95 ms instead of 36.
        if (waveform.WaveformPoints > 0 && waveform.WaveformPoints != points)
        {
            waveform.SetPoints(waveform.WaveformPoints);
            if (waveform.Preamble() != Rigol.ErrorCode.None) return false;
        }

        if (rigol.TimebaseScale(out double timeScale) != Rigol.ErrorCode.None) return false;
        if (rigol.ChannelScale(channel, out double voltScale) != Rigol.ErrorCode.None) return false;

        acquisition.Points = waveform.WaveformPoints;
        acquisition.XIncrement = waveform.XIncrement;
        acquisition.XOrigin = waveform.XOrigin;
        acquisition.TimeScale = timeScale;
        acquisition.VoltScale = voltScale;

        return acquisition.XIncrement > 0 && acquisition.Points > 0;
    }

    /// <summary>
    /// Read whatever is on screen. The scope free runs, so there is nothing to arm and nothing to
    /// wait for: every :TRIGger:STATus? poll costs a full SCPI round trip, which was more expensive
    /// than the acquisition it was waiting on.
    /// </summary>
    private static bool TryAcquire(Rigol.Waveform waveform, out double[] frame, out FrameTiming timing)
    {
        timing = new FrameTiming();

        var transfer = Stopwatch.StartNew();
        var result = waveform.ReadSamples(out frame);
        timing.TransferMs = transfer.Elapsed.TotalMilliseconds;

        return result == Rigol.ErrorCode.None;
    }

    private static bool IsSameFrame(double[] a, double[] b)
    {
        if (a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }

    private static void Report(string label, IEnumerable<double> values)
    {
        double[] samples = values.ToArray();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0}  min {1,7:0.000}  avg {2,7:0.000}  max {3,7:0.000} ms",
            label, samples.Min(), samples.Average(), samples.Max()));
    }
}
