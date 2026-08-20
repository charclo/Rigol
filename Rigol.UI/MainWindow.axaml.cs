using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace RigolUI;

public partial class MainWindow : Window
{
    private static readonly IBrush PassBrush = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3d));
    private static readonly IBrush FailBrush = new SolidColorBrush(Color.FromRgb(0xb9, 0x1c, 0x1c));
    private static readonly IBrush IdleBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x31, 0x3c));

    private readonly ScopeSession session = new();
    private readonly AppSettings settings = AppSettings.Load();
    private bool suppressToleranceEvents;

    /// <summary>Bounds of the redraw cap. Sixty already exceeds what a display can show.</summary>
    private const int MinRedrawHz = 1;
    private const int MaxRedrawHz = 60;
    private const int DefaultRedrawHz = 20;

    /// <summary>
    /// Drives the display. Frames arrive on the acquisition thread and are parked in
    /// <see cref="pendingFrame"/>; this timer draws whichever one is newest when it fires, so the
    /// redraw rate is set here instead of by how fast the scope happens to produce frames.
    /// </summary>
    private readonly DispatcherTimer redrawTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(1000.0 / DefaultRedrawHz),
    };

    /// <summary>Newest frame not yet drawn, or null when the display is up to date.</summary>
    private ScopeSession.Frame? pendingFrame;

    /// <summary>
    /// Distinct frames that were judged and counted but never drawn, because a newer one replaced
    /// them first. Now that the same cap paces the fetching, this should stay near zero; it grows
    /// only when a redraw is held up long enough for the next frame to arrive behind it.
    /// </summary>
    private int droppedRedraws;

    public MainWindow()
    {
        InitializeComponent();

        session.FrameReady += OnFrameReady;
        session.AcquisitionChanged += () => Dispatcher.UIThread.Post(() =>
        {
            // The scope was retuned: move the axes with it and re-check the mask against the new
            // settings, because a mask built for another timebase no longer describes this signal.
            ShowAcquisition();
            ShowMaskInfo();
        });
        session.Status += message => Dispatcher.UIThread.Post(() => StatusText.Text = message);

        redrawTimer.Tick += (_, _) => DrawPendingFrame();
        RedrawRateBox.ValueChanged += (_, _) => ApplyRedrawRate();

        ConnectButton.Click += async (_, _) => await ToggleConnectionAsync();
        RunButton.Click += (_, _) => ToggleRun();
        BuildMaskButton.Click += async (_, _) => await BuildMaskAsync();
        SaveMaskButton.Click += async (_, _) => await SaveMaskAsync();
        LoadMaskButton.Click += async (_, _) => await LoadMaskAsync();
        ResetButton.Click += (_, _) =>
        {
            session.ResetCounters();
            Interlocked.Exchange(ref droppedRedraws, 0);
            UpdateStatistics();
        };

        XToleranceBox.ValueChanged += (_, _) => ApplyTolerances();
        YToleranceBox.ValueChanged += (_, _) => ApplyTolerances();
        EdgeGuardBox.ValueChanged += (_, _) => ApplyTolerances();

        Closing += (_, _) =>
        {
            redrawTimer.Stop();
            settings.Save();
            session.Dispose();
        };

        IpBox.Text = settings.IpAddress;
        ChannelBox.SelectedIndex = Math.Clamp(settings.Channel - 1, 0, 3);

        // Setting the value only raises ValueChanged when it differs from the one in the markup,
        // so apply the rate explicitly as well.
        RedrawRateBox.Value = Math.Clamp(settings.MaxRedrawHz, MinRedrawHz, MaxRedrawHz);
        ApplyRedrawRate();

        if (settings.AutoStart || Program.StartupArgs.Contains("--autostart"))
            Loaded += async (_, _) => await AutoStartAsync();
    }

    /// <summary>
    /// Connect, put the configured mask in place and start running, without any clicking.
    /// </summary>
    /// <remarks>
    /// Driven by settings.json next to the executable. With --tight the mask is built from the
    /// live signal at minimum tolerances instead, which makes most frames fail; that is the load
    /// case used to exercise the window.
    /// </remarks>
    private async Task AutoStartAsync()
    {
        string[] args = Program.StartupArgs;

        int index = Array.IndexOf(args, "--autostart");
        if (index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--"))
            IpBox.Text = args[index + 1];

        await ToggleConnectionAsync();
        if (!session.IsConnected)
            return;

        if (args.Contains("--tight"))
        {
            XToleranceBox.Value = 0.01m;
            YToleranceBox.Value = 0.04m;
            await BuildMaskAsync();
        }
        else if (File.Exists(settings.ResolvedMaskPath))
        {
            try
            {
                LoadMaskFrom(settings.ResolvedMaskPath);
            }
            catch (Exception exception)
            {
                StatusText.Text = $"Could not load {settings.ResolvedMaskPath}: {exception.Message}";
            }
        }
        else
        {
            StatusText.Text = $"No mask at {settings.ResolvedMaskPath}. "
                            + "Build one from the golden signal, then save it there.";
        }

        ToggleRun();

        // Retune the scope from here, so the axes can be seen following a timebase change without
        // anyone touching the front panel.
        if (args.Contains("--churn") && session.Acquisition is not null)
        {
            double doubled = session.Acquisition.TimeScale * 2;
            _ = Task.Run(async () =>
            {
                await Task.Delay(6000);
                session.SendCommand(FormattableString.Invariant($":TIMebase:MAIN:SCALe {doubled:G6}"));
            });
        }
    }

    private async Task ToggleConnectionAsync()
    {
        if (session.IsConnected)
        {
            session.Disconnect();
            ConnectButton.Content = "Connect";
            RunButton.Content = "Start";
            RunButton.IsEnabled = false;
            BuildMaskButton.IsEnabled = false;
            SaveMaskButton.IsEnabled = false;
            ModelText.Text = string.Empty;
            StatusText.Text = "Disconnected.";
            return;
        }

        string address = IpBox.Text?.Trim() ?? string.Empty;
        int channel = ChannelBox.SelectedIndex + 1;

        ConnectButton.IsEnabled = false;
        StatusText.Text = $"Connecting to {address}...";
        try
        {
            await Task.Run(() => session.Connect(address, channel));

            ApplyProfile();
            ConnectButton.Content = "Disconnect";
            RunButton.IsEnabled = true;
            BuildMaskButton.IsEnabled = true;
            ApplyTolerances();
            ShowAcquisition();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void ToggleRun()
    {
        if (session.IsRunning)
        {
            session.Stop();

            // Show whatever the cap was still holding back, so stopping does not leave the plot
            // one frame behind the counters.
            DrawPendingFrame();

            RunButton.Content = "Start";
            StatusText.Text = "Stopped.";
        }
        else
        {
            session.Start();
            RunButton.Content = "Stop";
            StatusText.Text = "Running.";
        }
    }

    private async Task BuildMaskAsync()
    {
        bool wasRunning = session.IsRunning;
        session.Stop();

        int frames = (int)(ReferenceFramesBox.Value ?? 8);
        BuildMaskButton.IsEnabled = false;
        try
        {
            await Task.Run(() => session.BuildMask(frames));
            SaveMaskButton.IsEnabled = true;
            ShowMaskInfo();
            UpdateStatistics();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            BuildMaskButton.IsEnabled = true;
            if (wasRunning)
            {
                session.Start();
                RunButton.Content = "Stop";
            }
        }
    }

    private async Task SaveMaskAsync()
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save mask",
            SuggestedFileName = "mask.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Mask (JSON)") { Patterns = ["*.json"] }],
        });

        if (file is null)
            return;

        try
        {
            session.SaveMask(file.Path.LocalPath);
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async Task LoadMaskAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load mask",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Mask (JSON)") { Patterns = ["*.json"] }],
        });

        if (files.Count == 0)
            return;

        try
        {
            LoadMaskFrom(files[0].Path.LocalPath);
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void LoadMaskFrom(string path)
    {
        session.LoadMask(path);
        SaveMaskButton.IsEnabled = true;

        // The file carries the tolerances it was built with; show them without looping back
        // into RebuildMask, which cannot run without the original reference frames.
        suppressToleranceEvents = true;
        XToleranceBox.Value = (decimal)session.XToleranceDiv;
        YToleranceBox.Value = (decimal)session.YToleranceDiv;
        suppressToleranceEvents = false;

        ShowMaskInfo();
        UpdateStatistics();
    }

    /// <summary>
    /// Match the window to the scope that just answered: its graticule, and the tolerance ranges
    /// its own pass/fail mask accepts. Called once per connect, before the tolerances are applied,
    /// so a value carried over from another model lands inside the new range.
    /// </summary>
    private void ApplyProfile()
    {
        var profile = session.Profile;

        Plot.HorizontalDivisions = profile.HorizontalDivisions;
        Plot.VerticalDivisions = profile.VerticalDivisions;

        // Widening the bounds can pull the current value with it, which would loop straight back
        // into ApplyTolerances before the session knows about the new profile.
        suppressToleranceEvents = true;
        XToleranceBox.Minimum = (decimal)profile.MaskXMinimum;
        XToleranceBox.Maximum = (decimal)profile.MaskXMaximum;
        XToleranceBox.Increment = (decimal)profile.MaskXStep;
        YToleranceBox.Minimum = (decimal)profile.MaskYMinimum;
        YToleranceBox.Maximum = (decimal)profile.MaskYMaximum;
        YToleranceBox.Increment = (decimal)profile.MaskYStep;
        XToleranceBox.Value = (decimal)session.XToleranceDiv;
        YToleranceBox.Value = (decimal)session.YToleranceDiv;
        suppressToleranceEvents = false;

        ModelText.Text = profile.Recognised
            ? session.Model
            : $"{session.Model} (unrecognised)";

        Title = $"Rigol {session.Model} - Mask Test";
    }

    private void ApplyTolerances()
    {
        if (suppressToleranceEvents)
            return;

        session.XToleranceDiv = (double)(XToleranceBox.Value ?? 0.24m);
        session.YToleranceDiv = (double)(YToleranceBox.Value ?? 0.24m);
        session.EdgeGuard = (int)(EdgeGuardBox.Value ?? 0m);

        ShowMaskInfo();
        UpdateStatistics();
        Plot.Mask = session.Mask;
    }

    private void ShowAcquisition()
    {
        if (session.Acquisition is null)
            return;

        Plot.XOrigin = session.Acquisition.XOrigin;
        Plot.XIncrement = session.Acquisition.XIncrement;
        Plot.VoltsPerDivision = session.Acquisition.VoltScale;
        Plot.SecondsPerDivision = session.Acquisition.TimeScale;
        Plot.VerticalCentre = session.VerticalCentre;
        StatusText.Text = string.Format(CultureInfo.InvariantCulture,
            "{0} - CH{1}, {2} points, {3:G4} s/div, {4:G4} V/div",
            session.Model, session.Acquisition.Channel, session.Acquisition.Points,
            session.Acquisition.TimeScale, session.Acquisition.VoltScale);
    }

    private void ShowMaskInfo()
    {
        if (session.Mask is null)
        {
            MaskInfoText.Text = "No mask loaded.";
            return;
        }

        string? incompatible = session.Acquisition is null
            ? null
            : session.Mask.CheckCompatibility(session.Acquisition);

        MaskInfoText.Text = incompatible is null
            ? session.Mask.ToString()
            : session.Mask + "\n\nWARNING: " + incompatible;
    }

    /// <summary>
    /// Park the newest frame for the redraw timer to pick up. Runs on the acquisition thread and
    /// deliberately touches nothing else, so reading stays independent of drawing.
    /// </summary>
    /// <remarks>
    /// Posting every frame unconditionally is what made the window stop responding as soon as
    /// samples started failing: a failing frame is far more expensive to draw, the UI thread fell
    /// behind the acquisition loop, and the dispatcher queue grew without bound. Keeping a single
    /// slot means the display always shows the latest frame and simply skips the ones in between.
    /// The statistics are counted in the session, so a skipped redraw never loses a verdict.
    /// </remarks>
    private void OnFrameReady(ScopeSession.Frame frame)
    {
        // A frame still in the slot was never drawn: either the cap held the redraw back, or the
        // UI thread could not keep up. It was judged and counted all the same.
        if (Interlocked.Exchange(ref pendingFrame, frame) is not null)
            Interlocked.Increment(ref droppedRedraws);
    }

    /// <summary>Draw the frame waiting in the slot, if any. Runs on the UI thread.</summary>
    private void DrawPendingFrame()
    {
        ScopeSession.Frame? latest = Interlocked.Exchange(ref pendingFrame, null);
        if (latest is null)
            return;

        try
        {
            ShowFrame(latest);
        }
        catch (Exception exception)
        {
            StatusText.Text = "Display error: " + exception.Message;
        }
    }

    /// <summary>
    /// Apply the frame rate from the spinner, to the acquisition loop as well as to the drawing.
    /// The timer runs for the life of the window: a tick with an empty slot costs nothing, and
    /// there is no state to keep in step with the session.
    /// </summary>
    /// <remarks>
    /// The cap governs fetching, not just painting, so turning it down takes the reads it saves
    /// off the wire instead of reading frames only to discard them. At the top of the range the
    /// interval is shorter than a read takes anyway, which leaves the loop running flat out.
    /// </remarks>
    private void ApplyRedrawRate()
    {
        int hz = Math.Clamp((int)(RedrawRateBox.Value ?? DefaultRedrawHz), MinRedrawHz, MaxRedrawHz);

        settings.MaxRedrawHz = hz;
        session.MaxReadHz = hz;
        redrawTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / hz);
        redrawTimer.Start();
    }

    private void ShowFrame(ScopeSession.Frame frame)
    {
        Plot.Mask = session.Mask;
        Plot.Samples = frame.Samples;

        if (frame.Result is null)
        {
            SetVerdict(session.Mask is null ? "NO MASK" : "MISMATCH", IdleBrush);
            FailedSamplesText.Text = "-";
            WorstText.Text = "-";
            WorstAtText.Text = "-";
        }
        else
        {
            SetVerdict(frame.Result.Passed ? "PASS" : "FAIL", frame.Result.Passed ? PassBrush : FailBrush);
            FailedSamplesText.Text = frame.Result.FailedSamples.ToString(CultureInfo.InvariantCulture);
            WorstText.Text = frame.Result.WorstDeviation > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.###} mV", frame.Result.WorstDeviation * 1e3)
                : "-";
            WorstAtText.Text = frame.Result.WorstDeviationIndex >= 0 && session.Mask is not null
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.##} us",
                    session.Mask.TimeAt(frame.Result.WorstDeviationIndex) * 1e6)
                : "-";
        }

        TimingText.Text = string.Format(CultureInfo.InvariantCulture, "{0:0} ms", frame.TransferMs);

        UpdateStatistics();
    }

    private void SetVerdict(string text, IBrush background)
    {
        VerdictText.Text = text;
        VerdictBorder.Background = background;
    }

    private void UpdateStatistics()
    {
        PassedText.Text = session.Passed.ToString(CultureInfo.InvariantCulture);
        FailedText.Text = session.Failed.ToString(CultureInfo.InvariantCulture);
        TotalText.Text = session.Total.ToString(CultureInfo.InvariantCulture);
        DuplicateText.Text = session.Duplicates.ToString(CultureInfo.InvariantCulture);
        DroppedText.Text = Volatile.Read(ref droppedRedraws).ToString(CultureInfo.InvariantCulture);
        RateText.Text = session.FramesPerSecond > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} /s", session.FramesPerSecond)
            : "-";
    }
}
