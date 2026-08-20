using System.Diagnostics;
using System.Globalization;

namespace Rigol;

/// <summary>
/// Measures where the time in a mask-test cycle actually goes, so optimisation effort can be
/// aimed at the part that costs something. Run with: Rigol --profile [ip].
/// </summary>
public static class Profiler
{
    public static void Run(Rigol rigol, int channel = 1)
    {
        var waveform = new Rigol.Waveform(rigol);

        rigol.SendCommand($":CHANnel{channel}:DISPlay ON");
        waveform.SetSource(Enum.Parse<Rigol.Waveform.Source>($"Channel{channel}"));
        waveform.SetMode(Rigol.Waveform.Mode.Normal);
        waveform.SetFormat(Rigol.Waveform.Format.ASCii);

        Console.WriteLine($"Model {rigol.Model}");
        Console.WriteLine();

        try
        {
            MeasureQueryOverhead(rigol);
            MeasureTransfer(rigol, waveform);
            MeasureFreeRun(rigol, waveform);
        }
        finally
        {
            rigol.Run();
        }
    }

    /// <summary>
    /// Read the screen in a loop while the timebase is changed underneath, which is what a user
    /// does when they turn the knob to push the signal out of the mask. Checks that a failed or
    /// oddly sized block response never leaves unread bytes in the socket: that would desynchronise
    /// the stream and make every later read fail.
    /// </summary>
    public static void ReproduceTimebaseChurn(Rigol rigol, int channel = 1)
    {
        var waveform = new Rigol.Waveform(rigol);

        rigol.SendCommand($":CHANnel{channel}:DISPlay ON");
        waveform.SetSource(Enum.Parse<Rigol.Waveform.Source>($"Channel{channel}"));
        waveform.SetMode(Rigol.Waveform.Mode.Normal);
        waveform.SetFormat(Rigol.Waveform.Format.WORD);
        waveform.SetPoints(1000);
        rigol.Run();
        Thread.Sleep(300);
        waveform.Preamble();

        string original = string.Empty;
        rigol.Query(":TIMebase:MAIN:SCALe?", ref original);

        string[] scales = ["1e-4", "2e-4", "5e-4", "2e-4", "5e-5"];

        try
        {
            Console.WriteLine("A. baseline, no timebase change");
            Churn(rigol, waveform, channel, null, reconfigure: false);

            Console.WriteLine();
            Console.WriteLine("B. timebase changes, no reconfigure after the change");
            Churn(rigol, waveform, channel, scales, reconfigure: false);

            Console.WriteLine();
            Console.WriteLine("C. timebase changes, waveform readout reconfigured after each change");
            Churn(rigol, waveform, channel, scales, reconfigure: true);
        }
        finally
        {
            if (original.Trim().Length > 0)
                rigol.SendCommand(":TIMebase:MAIN:SCALe " + original.Trim());
        }
    }

    private static void Churn(Rigol rigol, Rigol.Waveform waveform, int channel,
        string[]? scales, bool reconfigure)
    {
        int reads = 0, failures = 0, longestRun = 0, currentRun = 0;

        foreach (string? scale in (object?[])(scales ?? [null, null, null]))
        {
            if (scale is string value)
            {
                rigol.SendCommand(":TIMebase:MAIN:SCALe " + value);
                Thread.Sleep(150);

                if (reconfigure)
                    Reconfigure(rigol, waveform, channel);
            }

            for (int i = 0; i < 15; i++)
            {
                reads++;
                if (waveform.ReadSamples(out double[] samples) == Rigol.ErrorCode.None && samples.Length > 0)
                {
                    currentRun = 0;
                    continue;
                }

                failures++;
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
        }

        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "   {0} reads, {1} failed, longest consecutive failure run {2}  =>  {3}",
            reads, failures, longestRun,
            longestRun >= 10 ? "NEVER RECOVERS" : failures > 0 ? "recovers" : "clean"));
    }

    /// <summary>
    /// Re-send the waveform readout settings and re-read the preamble. The instrument drops this
    /// configuration when the acquisition changes, and the scaling factors change with it.
    /// </summary>
    private static void Reconfigure(Rigol rigol, Rigol.Waveform waveform, int channel)
    {
        waveform.SetSource(Enum.Parse<Rigol.Waveform.Source>($"Channel{channel}"));
        waveform.SetMode(Rigol.Waveform.Mode.Normal);
        waveform.SetFormat(Rigol.Waveform.Format.WORD);
        waveform.SetPoints(1000);
        waveform.Preamble();
    }

    /// <summary>
    /// Fixed cost of one SCPI round trip, independent of payload. Everything else builds on this.
    /// </summary>
    private static void MeasureQueryOverhead(Rigol rigol)
    {
        const int repetitions = 100;

        rigol.TriggerStatus(out _);   // warm up

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < repetitions; i++)
            rigol.TriggerStatus(out _);
        double perQuery = stopwatch.Elapsed.TotalMilliseconds / repetitions;

        Console.WriteLine("1. SCPI round trip (:TRIGger:STATus?, ~5 byte response)");
        Console.WriteLine(FormattableString.Invariant($"   {perQuery:0.00} ms per query"));
        Console.WriteLine();
    }

    /// <summary>
    /// Transfer cost per format, and whether the binary paths reproduce the voltages that the
    /// instrument itself computes for ASCii. The scope is stopped so every format reads the
    /// same frame.
    /// </summary>
    private static void MeasureTransfer(Rigol rigol, Rigol.Waveform waveform)
    {
        Console.WriteLine("2. :WAVeform:DATA? per transfer format (1000 points, same frame)");
        Console.WriteLine("   format   ms    vs ASCii");

        waveform.SetPoints(1000);

        // Freeze one frame so every format reads exactly the same data.
        rigol.Run();
        Thread.Sleep(200);
        rigol.Stop();
        Thread.Sleep(100);

        // Ask for the number of points the instrument actually delivers. ASCii is self-delimiting
        // and never notices the difference, so leaving this uncorrected would compare a penalised
        // binary read against an unpenalised text one.
        waveform.SetFormat(Rigol.Waveform.Format.ASCii);
        waveform.Preamble();
        if (waveform.ReadSamples(out double[] probe) == Rigol.ErrorCode.None && probe.Length > 0)
        {
            waveform.SetPoints(probe.Length);
            Console.WriteLine($"   (instrument delivers {probe.Length} points)");
        }

        double[]? reference = null;

        foreach (Rigol.Waveform.Format format in new[]
                 { Rigol.Waveform.Format.ASCii, Rigol.Waveform.Format.WORD, Rigol.Waveform.Format.BYTE })
        {
            waveform.SetFormat(format);
            waveform.Preamble();   // the scaling factors depend on the format

            if (waveform.ReadSamples(out double[] samples) != Rigol.ErrorCode.None)
            {
                Console.WriteLine($"   {format,-6}   read failed");
                continue;
            }

            const int repetitions = 5;
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < repetitions; i++)
                waveform.ReadSamples(out _);
            double milliseconds = stopwatch.Elapsed.TotalMilliseconds / repetitions;

            string difference = "-";
            if (reference is null)
            {
                reference = samples;
            }
            else if (samples.Length == reference.Length)
            {
                double worst = 0;
                for (int i = 0; i < samples.Length; i++)
                    worst = Math.Max(worst, Math.Abs(samples[i] - reference[i]));
                difference = FormattableString.Invariant($"max {worst * 1e3:0.###} mV");
            }
            else
            {
                difference = $"{samples.Length} vs {reference.Length} points";
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "   {0,-6}  {1,5:0.0}   {2}", format, milliseconds, difference));
        }

        waveform.SetFormat(Rigol.Waveform.Format.WORD);
        waveform.Preamble();
        Console.WriteLine();
    }

    /// <summary>
    /// The rate the application actually runs at: the scope free runs and we read the screen.
    /// Reads outrun the screen refresh, so the distinct-frame count is the number that matters.
    /// </summary>
    private static void MeasureFreeRun(Rigol rigol, Rigol.Waveform waveform)
    {
        // The format the application actually runs on, so this number is the one that matters.
        waveform.SetFormat(Rigol.Waveform.Format.ASCii);
        waveform.Preamble();

        rigol.Run();
        Thread.Sleep(300);

        const int frames = 20;
        int distinct = 0;
        double[]? previous = null;

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            if (waveform.ReadSamples(out double[] samples) != Rigol.ErrorCode.None)
                continue;

            if (previous is null || !samples.SequenceEqual(previous))
                distinct++;
            previous = samples;
        }
        double average = stopwatch.Elapsed.TotalMilliseconds / frames;

        Console.WriteLine("3. Free-running screen reads (ASCii)");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "   {0:0.0} ms/read => {1:0.0} reads/s, {2}/{3} distinct => {4:0.0} distinct frames/s",
            average, 1000 / average, distinct, frames, 1000 / average * distinct / frames));
        Console.WriteLine();
    }
}
