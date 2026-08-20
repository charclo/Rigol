using System.Globalization;

namespace Rigol;

/// <summary>
/// Offline checks on <see cref="SoftwareMask"/> using synthetic waveforms, so the mask semantics
/// can be verified without a scope attached. Run with: Rigol --selftest
/// </summary>
public static class SelfTest
{
    private const int Points = 1000;
    private const double Amplitude = 2.0;
    private const int Cycles = 5;

    /// <summary>
    /// Tolerances the mask under test is built with. Everything below is derived from these, so
    /// changing them cannot leave a stale number in an expectation or a label.
    /// </summary>
    private const double XToleranceDiv = 0.24;
    private const double YToleranceDiv = 0.24;

    public static int Run()
    {
        var acquisition = new SoftwareMask.Acquisition
        {
            Channel = 1,
            Points = Points,
            XIncrement = 1e-6,      // 1 us/sample
            XOrigin = -500e-6,
            TimeScale = 100e-6,     // 100 us/div => 100 samples/div
            VoltScale = 1.0,        // 1 V/div
        };

        double[] reference = Sine(shiftSamples: 0, offset: 0);
        SoftwareMask mask = SoftwareMask.Create([reference], acquisition, XToleranceDiv, YToleranceDiv);

        // The vertical tolerance in volts is what the offset cases have to be measured against.
        double yTolerance = YToleranceDiv * acquisition.VoltScale;

        Console.WriteLine("Mask: " + mask);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "Band width at the first sample: {0:0.###} mV (2 x {1:0.###} V vertical tolerance = {2:0.###} mV, "
            + "the rest is horizontal dilation)",
            (mask.Upper[0] - mask.Lower[0]) * 1e3, yTolerance, 2 * yTolerance * 1e3));
        Console.WriteLine();

        int guard = mask.ToleranceSamples;

        Console.WriteLine("  case                                  edge  expected  actual  failed  worst dev");
        Console.WriteLine("  ------------------------------------  ----  --------  ------  ------  ---------");

        int failures = 0;
        failures += Expect(mask, "identical to reference", Sine(0, 0), 0, shouldPass: true);
        // A uniform offset is bounded by the vertical tolerance alone: at a peak the horizontal
        // dilation adds nothing, so the band there is exactly the reference plus the tolerance.
        double inside = 0.6 * yTolerance;
        double outside = 1.5 * yTolerance;
        failures += Expect(mask, FormattableString.Invariant($"offset +{inside:0.###} V (< {yTolerance:0.###} V tol)"),
            Sine(0, inside), 0, shouldPass: true);
        failures += Expect(mask, FormattableString.Invariant($"offset +{outside:0.###} V (> {yTolerance:0.###} V tol)"),
            Sine(0, outside), 0, shouldPass: false);
        failures += Expect(mask, FormattableString.Invariant($"offset -{outside:0.###} V"),
            Sine(0, -outside), 0, shouldPass: false);
        failures += Expect(mask, "10 mV noise", Noisy(0.010), 0, shouldPass: true);
        failures += Expect(mask, "single +2 V glitch at index 500", Glitch(500, 2.0), 0, shouldPass: false);
        failures += Expect(mask, "amplitude +30%", Scaled(1.30), 0, shouldPass: false);

        // Within ToleranceSamples of an edge the horizontal tolerance is one-sided, so a purely
        // time-shifted signal fails there. Guarding the edges isolates the horizontal tolerance.
        failures += Expect(mask, "shift 20 samples, edges guarded", Sine(20, 0), guard, shouldPass: true);
        failures += Expect(mask, "shift 24 samples, edges guarded", Sine(24, 0), guard, shouldPass: true);
        failures += Expect(mask, "shift 40 samples, edges guarded", Sine(40, 0), guard, shouldPass: false);
        failures += Expect(mask, "shift 20 samples, no edge guard", Sine(20, 0), 0, shouldPass: false);
        failures += Expect(mask, "glitch at index 500 survives guard", Glitch(500, 2.0), guard, shouldPass: false);

        Console.WriteLine();

        // The horizontal tolerance is the floor, not the ceiling: past the dilation window the
        // vertical tolerance still absorbs a shift, by its size divided by the local slope.
        int firstFailingShift = 0;
        for (int shift = 1; shift <= Points / 2; shift++)
        {
            if (!mask.Test(Sine(shift, 0), guard).Passed)
            {
                firstFailingShift = shift;
                break;
            }
        }

        double slopePerSample = Amplitude * 2 * Math.PI * Cycles / Points;
        int predicted = guard + (int)Math.Floor(yTolerance / slopePerSample);
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  smallest time shift that fails: {0} samples (X tolerance {1} + ~{2} from the Y tolerance at {3:0.####} V/sample)",
            firstFailingShift, guard, predicted - guard, slopePerSample));
        failures += Report("time shift within the X tolerance always passes", firstFailingShift > guard);
        failures += Report("time shift far beyond both tolerances fails", firstFailingShift < 2 * predicted);

        // Round-trip through the JSON file, which is the whole point of the exercise.
        string path = Path.Combine(Path.GetTempPath(), "rigol_mask_selftest.json");
        mask.Save(path);
        SoftwareMask reloaded = SoftwareMask.Load(path);
        bool identical = reloaded.Lower.SequenceEqual(mask.Lower)
                      && reloaded.Upper.SequenceEqual(mask.Upper)
                      && reloaded.ToleranceSamples == mask.ToleranceSamples;
        failures += Report("save/load round-trip identical", identical);

        SoftwareMask.Result before = mask.Test(Sine(0, 0.60));
        SoftwareMask.Result after = reloaded.Test(Sine(0, 0.60));
        failures += Report("reloaded mask gives the same verdict",
            before.Passed == after.Passed && before.FailedSamples == after.FailedSamples);

        // A reconfigured scope has to be detected, not silently accepted.
        failures += Report("compatibility: unchanged settings accepted",
            reloaded.CheckCompatibility(acquisition) is null);

        var retimed = acquisition.Clone();
        retimed.TimeScale = 200e-6;
        failures += Report("compatibility: changed timebase rejected",
            reloaded.CheckCompatibility(retimed) is not null);

        var repointed = acquisition.Clone();
        repointed.Points = 500;
        failures += Report("compatibility: changed point count rejected",
            reloaded.CheckCompatibility(repointed) is not null);

        // Multi-frame reference should widen the envelope, not narrow it.
        SoftwareMask multi = SoftwareMask.Create(
            [Sine(0, 0), Sine(3, 0), Sine(-3, 0), Noisy(0.05)], acquisition, 0.24, 24);
        bool wider = multi.Upper.Zip(mask.Upper).All(p => p.First >= p.Second - 1e-12)
                  && multi.Lower.Zip(mask.Lower).All(p => p.First <= p.Second + 1e-12);
        failures += Report("multi-frame reference widens the envelope", wider);

        File.Delete(path);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED.");
        return failures == 0 ? 0 : 1;
    }

    private static int Expect(SoftwareMask mask, string label, double[] frame, int edgeGuard, bool shouldPass)
    {
        SoftwareMask.Result result = mask.Test(frame, edgeGuard);
        bool ok = result.Passed == shouldPass;

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "  {0,-36}  {1,4}  {2,-8}  {3,-6}  {4,6}  {5,9}  {6}",
            label,
            edgeGuard,
            shouldPass ? "PASS" : "FAIL",
            result.Passed ? "PASS" : "FAIL",
            result.FailedSamples,
            result.WorstDeviation > 0 ? $"{result.WorstDeviation * 1e3:0.##} mV" : "-",
            ok ? "" : "<-- unexpected"));

        return ok ? 0 : 1;
    }

    private static int Report(string label, bool ok)
    {
        Console.WriteLine($"  {label,-52}  {(ok ? "ok" : "FAILED")}");
        return ok ? 0 : 1;
    }

    private static double[] Sine(int shiftSamples, double offset)
    {
        double[] samples = new double[Points];
        for (int i = 0; i < Points; i++)
        {
            int source = ((i - shiftSamples) % Points + Points) % Points;
            samples[i] = (Amplitude * Math.Sin(2 * Math.PI * Cycles * source / Points)) + offset;
        }
        return samples;
    }

    private static double[] Scaled(double factor)
    {
        double[] samples = Sine(0, 0);
        for (int i = 0; i < samples.Length; i++)
            samples[i] *= factor;
        return samples;
    }

    private static double[] Noisy(double amplitude)
    {
        var random = new Random(20260817);
        double[] samples = Sine(0, 0);
        for (int i = 0; i < samples.Length; i++)
            samples[i] += ((random.NextDouble() * 2) - 1) * amplitude;
        return samples;
    }

    private static double[] Glitch(int index, double amplitude)
    {
        double[] samples = Sine(0, 0);
        samples[index] += amplitude;
        return samples;
    }
}
