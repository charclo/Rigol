using System.Diagnostics;
using Scope = Rigol.Rigol;
using ScopeProfile = Rigol.ScopeProfile;
using SoftwareMask = Rigol.SoftwareMask;

namespace RigolUI;

/// <summary>
/// Owns the scope connection and the acquisition loop, and evaluates every frame against the
/// software mask. The reference frames are kept in memory, so changing a tolerance rebuilds the
/// envelope immediately without having to re-capture the golden signal.
/// </summary>
public sealed class ScopeSession : IDisposable
{
    public sealed class Frame
    {
        public required double[] Samples { get; init; }
        public SoftwareMask.Result? Result { get; init; }
        public double TransferMs { get; init; }
    }

    /// <summary>Longest pause between reads while the screen is not refreshing.</summary>
    private const int MaxBackoffMs = 40;

    /// <summary>Consecutive repeat reads before we tell the user the screen looks frozen.</summary>
    private const int StallThreshold = 40;

    /// <summary>
    /// How often the loop re-reads the instrument's acquisition settings, so that turning the
    /// timebase, the vertical scale or the vertical position on the scope is picked up. Four
    /// queries at roughly 28 ms each, so keep it well above the frame interval.
    /// </summary>
    private const int SettingsPollMs = 1000;

    private readonly object scopeLock = new();

    private Scope? scope;
    private Scope.Waveform? waveform;

    /// <summary>
    /// What this model of scope does differently. Set from *IDN? on connect; until then the
    /// fallback profile keeps the tolerance clamps and the graticule at sane values.
    /// </summary>
    public ScopeProfile Profile { get; private set; } = ScopeProfile.Unknown;

    /// <summary>
    /// Largest shortfall accepted as the instrument's own off-by-one. Anything bigger is treated
    /// as a one-off bad read, because :WAVeform:POINts truncates the window rather than decimating
    /// it: asking for 400 points of a 999 point screen shows only the first 40% of the time span.
    /// </summary>
    private const int PointShortfallTolerance = 2;

    /// <summary>Point count currently asked of the instrument, corrected from what it delivers.</summary>
    private int requestedPoints;

    /// <summary>
    /// Points the preamble says the screen holds. Not always what the instrument delivers, but it
    /// is the count a full time window is worth, so corrections are measured against it.
    /// </summary>
    private int screenPoints;
    private CancellationTokenSource? loop;
    private List<double[]> referenceFrames = [];

    public bool IsConnected => scope is not null;
    public bool IsRunning => loop is not null;

    public string Model { get; private set; } = string.Empty;
    public int Channel { get; private set; } = 1;

    /// <summary>
    /// Voltage on the centre line of the graticule, mirroring the channel's vertical position so
    /// the trace sits at the height the instrument's own screen puts it at.
    /// </summary>
    /// <remarks>
    /// Display only. The samples are absolute voltages whatever the vertical position is — the
    /// preamble's YOrigin already accounts for it — so moving the trace up or down on the scope
    /// does not invalidate a mask, and this is deliberately not part of the acquisition settings
    /// a mask is checked against.
    /// </remarks>
    public double VerticalCentre { get; private set; }
    public SoftwareMask? Mask { get; private set; }
    public SoftwareMask.Acquisition? Acquisition { get; private set; }

    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Total => Passed + Failed;

    private double xToleranceDiv = 0.24;
    private double yToleranceDiv = 0.24;

    /// <summary>Horizontal tolerance in divisions, kept inside this model's :MASK:X range.</summary>
    public double XToleranceDiv
    {
        get => xToleranceDiv;
        set { xToleranceDiv = Profile.ClampMaskX(value); RebuildMask(); }
    }

    /// <summary>Vertical tolerance in divisions, kept inside this model's :MASK:Y range.</summary>
    public double YToleranceDiv
    {
        get => yToleranceDiv;
        set { yToleranceDiv = Profile.ClampMaskY(value); RebuildMask(); }
    }

    /// <summary>Samples ignored at both frame edges, where the horizontal tolerance is one-sided.</summary>
    public int EdgeGuard { get; set; }

    /// <summary>
    /// Upper bound on how often a frame is fetched, in hertz. Zero reads as fast as the link and
    /// the instrument allow.
    /// </summary>
    /// <remarks>
    /// Reads are not synchronised to the scope's acquisitions, so running flat out returns the
    /// same frame several times over — two thirds of the reads on a DS1104Z at 200 us/div — and
    /// every one of those duplicates still crosses the wire in full. Pacing the loop trades that
    /// waste away, but it is a real trade: a frame arriving between two reads is never fetched,
    /// so it is never judged, and a fault visible only in that frame goes unseen.
    /// </remarks>
    public int MaxReadHz { get; set; }

    /// <summary>Reads that returned the same frame as the previous one, and were not counted.</summary>
    public int Duplicates { get; private set; }

    /// <summary>Distinct frames per second, measured over the acquisition loop.</summary>
    public double FramesPerSecond { get; private set; }

    public event Action<Frame>? FrameReady;
    public event Action<string>? Status;

    /// <summary>Raised when the instrument's acquisition settings turn out to have changed.</summary>
    public event Action? AcquisitionChanged;

    public void Connect(string ipAddress, int channel)
    {
        Disconnect();

        var connected = new Scope(ipAddress);
        if (connected.Connect() != Scope.ErrorCode.None)
            throw new IOException($"Could not connect to {ipAddress}:5555");

        connected.IDN();

        lock (scopeLock)
        {
            scope = connected;
            waveform = new Scope.Waveform(connected);
            Channel = channel;
            Model = connected.Model;
            Profile = ScopeProfile.For(connected.Model);
        }

        // The tolerances were clamped against whatever profile was in force before, so re-apply
        // them: a DHO's 0.01 div is below the DS1000Z's 0.02 div floor, and the other way round a
        // DS1000Z mask of 3 div is beyond anything the DHO accepts.
        xToleranceDiv = Profile.ClampMaskX(xToleranceDiv);
        yToleranceDiv = Profile.ClampMaskY(yToleranceDiv);

        Configure(channel);
        Status?.Invoke($"Connected to {Model} at {ipAddress}");
    }

    public void Disconnect()
    {
        Stop();

        lock (scopeLock)
        {
            scope?.Disconnect();
            scope = null;
            waveform = null;
        }

        Mask = null;
        Acquisition = null;
        referenceFrames = [];
        ResetCounters();
    }

    /// <summary>
    /// Put the scope in the transfer mode the software mask needs and read back the acquisition
    /// settings.
    /// </summary>
    /// <remarks>
    /// ASCii is chosen for readability while debugging: the response is the voltages themselves,
    /// so a capture can be read straight off the wire. It costs about 3 ms a frame against WORD,
    /// which is roughly 8% and lost in the instrument's own 28 ms round trip. It also avoids two
    /// failure modes that only exist for block transfers: a header that announces more bytes than
    /// the instrument sends, and voltages computed from preamble factors that have gone stale.
    /// WORD is still supported and is what a RAW-memory read would want.
    /// </remarks>
    public void Configure(int channel)
    {
        lock (scopeLock)
        {
            if (scope is null || waveform is null)
                throw new InvalidOperationException("Not connected.");

            Channel = channel;

            scope.SendCommand($":CHANnel{channel}:DISPlay ON");
            waveform.SetSource(Enum.Parse<Scope.Waveform.Source>($"Channel{channel}"));
            waveform.SetMode(Scope.Waveform.Mode.Normal);
            waveform.SetFormat(Scope.Waveform.Format.ASCii);
            requestedPoints = Profile.ScreenPoints;
            ApplyPointRequest();

            // The scope triggers on its own; the preamble is meaningful once the screen is filled.
            scope.Run();
            Thread.Sleep(200);

            if (waveform.Preamble() != Scope.ErrorCode.None)
                throw new IOException("Could not read the waveform preamble.");

            // Ask for exactly what the instrument holds. Requesting more makes it announce a
            // longer block than it sends, and every read then waits out the difference. The
            // preamble is only a first guess here; AcquireFrame corrects it against what actually
            // arrives, because the preamble itself can claim a point the instrument never sends.
            if (waveform.WaveformPoints > 0 && waveform.WaveformPoints != requestedPoints)
            {
                requestedPoints = waveform.WaveformPoints;
                if (ApplyPointRequest() && waveform.Preamble() != Scope.ErrorCode.None)
                    throw new IOException("Could not read the waveform preamble.");
            }

            screenPoints = waveform.WaveformPoints;

            if (scope.TimebaseScale(out double timeScale) != Scope.ErrorCode.None)
                throw new IOException("Could not read the timebase scale.");
            if (scope.ChannelScale(channel, out double voltScale) != Scope.ErrorCode.None)
                throw new IOException("Could not read the channel scale.");

            // Where the trace sits is presentation, not acquisition: a scope that will not answer
            // is worth drawing around zero rather than refusing to connect over.
            if (scope.ChannelOffset(channel, out double voltOffset) == Scope.ErrorCode.None)
                VerticalCentre = -voltOffset;

            Acquisition = new SoftwareMask.Acquisition
            {
                Channel = channel,
                Points = waveform.WaveformPoints,
                XIncrement = waveform.XIncrement,
                XOrigin = waveform.XOrigin,
                TimeScale = timeScale,
                VoltScale = voltScale,
            };
        }
    }

    /// <summary>
    /// Ask the instrument to return <see cref="requestedPoints"/> per read, on the models where
    /// the read size can be asked for at all.
    /// </summary>
    /// <remarks>
    /// The DS1000Z has no :WAVeform:POINts command: a screen read is always the full 1,200 points
    /// and the size is fixed by the mode, not by a request. The surrounding corrections all exist
    /// to keep a DHO's request in step with what it actually delivers, so on a DS1000Z they stay
    /// bookkeeping and nothing goes to the wire.
    /// </remarks>
    /// <returns>True when a request was sent, so the caller knows whether to re-read the preamble.</returns>
    private bool ApplyPointRequest()
    {
        if (!Profile.SupportsWaveformPoints || waveform is null)
            return false;

        waveform.SetPoints(requestedPoints);
        return true;
    }

    /// <summary>
    /// Re-read the acquisition settings and report whether they changed. Cheap enough to poll,
    /// unlike <see cref="Configure"/>, which re-sends the whole readout configuration.
    /// </summary>
    private bool RefreshAcquisition()
    {
        lock (scopeLock)
        {
            if (scope is null || waveform is null || Acquisition is null)
                return false;

            // Repair a request that drifted well below the full screen, so a single bad read
            // cannot leave us permanently looking at a slice of the instrument's time window.
            // Measured against the screen, not a fixed 1000: a fast timebase holds fewer points,
            // and resetting to 1000 there would ask for a window the instrument does not have.
            int fullScreen = screenPoints > 0 ? screenPoints : Profile.ScreenPoints;
            if (requestedPoints < fullScreen - PointShortfallTolerance)
            {
                requestedPoints = fullScreen;
                ApplyPointRequest();
            }

            if (waveform.Preamble() != Scope.ErrorCode.None)
                return false;

            // A new timebase can change how many points the screen holds, and asking for more
            // than the instrument has makes every read wait out the difference.
            if (waveform.WaveformPoints > 0 && waveform.WaveformPoints != Acquisition.Points)
            {
                requestedPoints = waveform.WaveformPoints;
                if (ApplyPointRequest() && waveform.Preamble() != Scope.ErrorCode.None)
                    return false;
            }

            screenPoints = waveform.WaveformPoints;

            if (scope.TimebaseScale(out double timeScale) != Scope.ErrorCode.None)
                return false;
            if (scope.ChannelScale(Channel, out double voltScale) != Scope.ErrorCode.None)
                return false;

            // Turning the vertical position knob changes nothing about the acquisition, so it
            // will not show up in the comparison below, but the plot still has to follow it.
            bool centreMoved = false;
            if (scope.ChannelOffset(Channel, out double voltOffset) == Scope.ErrorCode.None)
            {
                double centre = -voltOffset;
                centreMoved = Math.Abs(centre - VerticalCentre) > voltScale / 1000.0;
                VerticalCentre = centre;
            }

            var updated = new SoftwareMask.Acquisition
            {
                Channel = Channel,
                Points = waveform.WaveformPoints,
                XIncrement = waveform.XIncrement,
                XOrigin = waveform.XOrigin,
                TimeScale = timeScale,
                VoltScale = voltScale,
            };

            if (IsSameAcquisition(updated, Acquisition))
                return centreMoved;

            Acquisition = updated;
            return true;
        }
    }

    private static bool IsSameAcquisition(SoftwareMask.Acquisition a, SoftwareMask.Acquisition b)
    {
        return a.Points == b.Points
            && NearlyEqual(a.XIncrement, b.XIncrement)
            && NearlyEqual(a.XOrigin, b.XOrigin)
            && NearlyEqual(a.TimeScale, b.TimeScale)
            && NearlyEqual(a.VoltScale, b.VoltScale);
    }

    private static bool NearlyEqual(double a, double b)
    {
        double scale = Math.Max(Math.Abs(a), Math.Abs(b));
        return Math.Abs(a - b) <= (scale * 1e-6);
    }

    /// <summary>
    /// Capture the golden signal and build the mask from it.
    /// </summary>
    public void BuildMask(int frames)
    {
        if (Acquisition is null)
            throw new InvalidOperationException("Not connected.");

        RunContinuously();

        // We can read faster than the scope produces frames, so insist on distinct frames:
        // duplicates would make the envelope narrower than the signal warrants.
        var captured = new List<double[]>();
        int attempts = 0;
        while (captured.Count < frames && attempts < frames * 20)
        {
            attempts++;

            Frame? frame = AcquireFrame(evaluate: false);
            if (frame is null)
                throw new IOException($"Reference frame {captured.Count + 1} could not be acquired.");

            if (captured.Count > 0 && IsSameFrame(frame.Samples, captured[^1]))
                continue;

            captured.Add(frame.Samples);
            Status?.Invoke($"Captured reference frame {captured.Count}/{frames}");
        }

        if (captured.Count < frames)
            throw new IOException($"Only {captured.Count} distinct reference frame(s) in {attempts} reads.");

        // Consecutive reads can differ by a point: the instrument sometimes announces one more
        // than it sends. Settle on the shortest so every frame describes the same time span.
        int length = captured.Min(frame => frame.Length);
        for (int i = 0; i < captured.Count; i++)
        {
            if (captured[i].Length != length)
                captured[i] = captured[i][..length];
        }

        if (Acquisition.Points != length)
        {
            SoftwareMask.Acquisition adjusted = Acquisition.Clone();
            adjusted.Points = length;
            Acquisition = adjusted;
        }

        referenceFrames = captured;
        RebuildMask();
        ResetCounters();
        Status?.Invoke($"Mask built from {frames} frame(s): {Mask}");
    }

    /// <summary>
    /// Recompute the envelope from the stored reference frames. Called whenever a tolerance
    /// changes, so the sliders act instantly instead of requiring a new capture.
    /// </summary>
    private void RebuildMask()
    {
        if (referenceFrames.Count == 0 || Acquisition is null)
            return;

        Mask = SoftwareMask.Create(referenceFrames, Acquisition, xToleranceDiv, yToleranceDiv);
        ResetCounters();
    }

    public void LoadMask(string path)
    {
        SoftwareMask loaded = SoftwareMask.Load(path);

        // Reloading replaces the reference set, so the tolerances stored in the file win until
        // the next capture. Rebuilding from tolerances alone is impossible without the reference.
        referenceFrames = [];
        Mask = loaded;
        xToleranceDiv = loaded.XToleranceDiv;
        yToleranceDiv = loaded.YToleranceDiv;
        ResetCounters();

        string? incompatible = Acquisition is null ? null : loaded.CheckCompatibility(Acquisition);
        Status?.Invoke(incompatible is null
            ? $"Loaded {Path.GetFileName(path)}: {loaded}"
            : $"Loaded {Path.GetFileName(path)}, but {incompatible}");
    }

    public void SaveMask(string path)
    {
        if (Mask is null)
            throw new InvalidOperationException("No mask to save.");

        Mask.Save(path);
        Status?.Invoke($"Saved {Path.GetFileName(path)}");
    }

    /// <summary>
    /// Send a raw SCPI command over the session's connection, sharing the acquisition lock so it
    /// cannot interleave with a read in progress.
    /// </summary>
    public void SendCommand(string command)
    {
        lock (scopeLock)
            scope?.SendCommand(command);
    }

    public void ResetCounters()
    {
        Passed = 0;
        Failed = 0;
        Duplicates = 0;
        FramesPerSecond = 0;
    }

    public void Start()
    {
        if (loop is not null || !IsConnected)
            return;

        RunContinuously();

        var cancellation = new CancellationTokenSource();
        loop = cancellation;

        _ = Task.Run(() =>
        {
            double[]? previous = null;
            int duplicateStreak = 0;
            int readFailures = 0;
            bool reportedStall = false;
            var sinceLastFrame = Stopwatch.StartNew();
            var sinceSettingsPoll = Stopwatch.StartNew();
            var sinceReadStarted = Stopwatch.StartNew();

            while (!cancellation.IsCancellationRequested)
            {
                // Hold the read rate to what was asked for. Measured from the start of the previous
                // read, so the time the instrument itself took and any backoff already served both
                // count towards the interval rather than adding to it.
                int readHz = MaxReadHz;
                if (readHz > 0)
                {
                    double remaining = (1000.0 / readHz) - sinceReadStarted.Elapsed.TotalMilliseconds;
                    if (remaining >= 1 && cancellation.Token.WaitHandle.WaitOne((int)remaining))
                        break;
                }

                sinceReadStarted.Restart();

                // Pick up a timebase or vertical scale the user changed on the scope itself.
                if (sinceSettingsPoll.ElapsedMilliseconds >= SettingsPollMs)
                {
                    sinceSettingsPoll.Restart();
                    try
                    {
                        if (RefreshAcquisition())
                            AcquisitionChanged?.Invoke();
                    }
                    catch (Exception exception)
                    {
                        Status?.Invoke("Could not re-read the settings: " + exception.Message);
                    }
                }

                Frame? frame;
                try
                {
                    frame = AcquireFrame(evaluate: true);
                }
                catch (Exception exception)
                {
                    Status?.Invoke("Acquisition error: " + exception.Message);
                    break;
                }

                // A read can fail transiently, for instance while the scope reconfigures after the
                // timebase is changed. Never spin on it, and re-apply the readout settings if it
                // keeps failing: the display going quiet with no explanation is worse than an error.
                if (frame is null)
                {
                    readFailures++;
                    if (readFailures == 1)
                        Status?.Invoke("Read failed, retrying...");

                    Thread.Sleep(50);

                    if (readFailures % 20 == 0)
                    {
                        Status?.Invoke($"{readFailures} failed reads - re-applying the readout settings.");
                        try
                        {
                            Configure(Channel);
                        }
                        catch (Exception exception)
                        {
                            Status?.Invoke("Reconfigure failed: " + exception.Message);
                        }
                    }

                    continue;
                }

                if (readFailures > 0)
                {
                    Status?.Invoke($"Reading recovered after {readFailures} failed read(s).");
                    readFailures = 0;
                }

                // Reads are not synchronised to acquisitions, so the same frame can come back
                // several times. Counting it more than once would inflate the statistics.
                if (previous is not null && IsSameFrame(frame.Samples, previous))
                {
                    Duplicates++;

                    // We read faster than the scope refreshes its screen. Backing off costs no
                    // throughput and keeps two thirds of the traffic off the wire.
                    duplicateStreak++;
                    Thread.Sleep(Math.Min(duplicateStreak * 5, MaxBackoffMs));

                    if (duplicateStreak == StallThreshold && !reportedStall)
                    {
                        reportedStall = true;
                        Status?.Invoke("The scope is not producing new frames - is it still running?");
                    }

                    continue;
                }

                duplicateStreak = 0;
                reportedStall = false;
                previous = frame.Samples;

                double seconds = sinceLastFrame.Elapsed.TotalSeconds;
                sinceLastFrame.Restart();
                if (seconds > 0)
                    FramesPerSecond = FramesPerSecond == 0 ? 1 / seconds : (FramesPerSecond * 0.7) + (0.3 / seconds);

                if (frame.Result is not null)
                {
                    if (frame.Result.Passed) Passed++;
                    else Failed++;
                }

                FrameReady?.Invoke(frame);
            }
        }, cancellation.Token);
    }

    /// <summary>
    /// Put the scope in continuous acquisition so the screen keeps updating without us arming it.
    /// </summary>
    private void RunContinuously()
    {
        lock (scopeLock)
            scope?.Run();
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

    public void Stop()
    {
        loop?.Cancel();
        loop?.Dispose();
        loop = null;
    }

    /// <summary>
    /// Read whatever frame is on screen and, when a mask is loaded, evaluate it. The scope free
    /// runs, so there is nothing to arm and nothing to wait for.
    /// </summary>
    public Frame? AcquireFrame(bool evaluate)
    {
        double[] samples;
        double transferMs;

        lock (scopeLock)
        {
            if (scope is null || waveform is null)
                return null;

            var transfer = Stopwatch.StartNew();
            if (waveform.ReadSamples(out samples) != Scope.ErrorCode.None)
                return null;
            transferMs = transfer.Elapsed.TotalMilliseconds;

            // Believe the data, not the preamble: a DHO924S at 50 us/div reports 1000 points and
            // then delivers 999. Correct for that once, while the request still matches what the
            // preamble announced. Lowering it again on every short read would ratchet the request
            // down read after read, and since :WAVeform:POINts truncates the window rather than
            // decimating it, the time span would shrink with it.
            int shortfall = requestedPoints - samples.Length;
            if (samples.Length > 0 && shortfall > 0 && shortfall <= PointShortfallTolerance
                && requestedPoints == screenPoints)
            {
                requestedPoints = samples.Length;
                ApplyPointRequest();
            }
        }

        // Trim a frame that came back one point longer than the mask, so a block that overshoots
        // by a sample does not read as a mismatch.
        if (Mask is not null && samples.Length > Mask.Lower.Length)
            samples = samples[..Mask.Lower.Length];

        SoftwareMask.Result? result = null;
        if (evaluate && Mask is not null && Mask.Lower.Length == samples.Length)
            result = Mask.Test(samples, EdgeGuard);

        return new Frame
        {
            Samples = samples,
            Result = result,
            TransferMs = transferMs,
        };
    }

    public void Dispose() => Disconnect();
}
