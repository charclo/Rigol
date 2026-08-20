using System.Diagnostics;
using System.Globalization;

namespace RigolUI;

/// <summary>
/// Headless smoke test of <see cref="ScopeSession"/>, the engine behind the window. Run with:
/// Rigol.UI --check [ip-address]. It never touches the scope's own pass/fail test.
/// </summary>
public static class SessionCheck
{
    public static int Run(string[] args)
    {
        string address = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "192.168.1.12";

        using var session = new ScopeSession();
        session.Status += message => Console.WriteLine("  " + message);

        try
        {
            Console.WriteLine($"Connecting to {address}...");
            session.Connect(address, channel: 1);

            var acquisition = session.Acquisition!;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0}  CH{1}  {2} points  {3:G4} s/div  {4:G4} V/div",
                session.Model, acquisition.Channel, acquisition.Points,
                acquisition.TimeScale, acquisition.VoltScale));

            Console.WriteLine();
            Console.WriteLine("Building mask from 4 reference frames...");
            session.BuildMask(4);

            Console.WriteLine();
            Console.WriteLine("Acquiring 5 frames:");
            for (int i = 0; i < 5; i++)
            {
                ScopeSession.Frame? frame = session.AcquireFrame(evaluate: true);
                if (frame?.Result is null)
                {
                    Console.WriteLine($"  frame {i + 1}: no result");
                    continue;
                }

                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  frame {0}: {1,-4}  {2} failed sample(s)  transfer {3:0} ms",
                    i + 1, frame.Result.Passed ? "PASS" : "FAIL", frame.Result.FailedSamples,
                    frame.TransferMs));
            }

            // The point of keeping the reference frames: moving a tolerance has to change the
            // envelope immediately, without re-capturing the golden signal.
            Console.WriteLine();
            Console.WriteLine("Tolerance changes without re-capturing:");
            foreach (double y in new[] { 0.24, 0.20, 0.04 })
            {
                session.YToleranceDiv = y;
                double width = session.Mask!.Upper[0] - session.Mask.Lower[0];
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  Y = {0:0.00} div -> band at sample 0 is {1:0.#} mV wide", y, width * 1e3));
            }

            foreach (double x in new[] { 0.24, 0.05, 0.01 })
            {
                session.XToleranceDiv = x;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  X = {0:0.00} div -> dilation window is {1} sample(s)", x, session.Mask!.ToleranceSamples));
            }

            // Turning the timebase on the scope has to move the axes with it, and invalidate a mask
            // that was built for the old one.
            Console.WriteLine();
            Console.WriteLine("Following a timebase change made on the instrument:");

            double originalScale = session.Acquisition!.TimeScale;
            int changes = 0;
            session.AcquisitionChanged += () => Interlocked.Increment(ref changes);

            session.Start();
            Thread.Sleep(1500);

            double target = originalScale * 2;
            session.SendCommand(FormattableString.Invariant($":TIMebase:MAIN:SCALe {target:G6}"));

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < 5000 && Volatile.Read(ref changes) == 0)
                Thread.Sleep(50);

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  set {0:G4} s/div, session reports {1:G4} s/div after {2} ms, {3} change event(s)",
                target, session.Acquisition!.TimeScale, stopwatch.ElapsedMilliseconds, changes));

            string? incompatible = session.Mask?.CheckCompatibility(session.Acquisition!);
            Console.WriteLine($"  mask compatibility now: {incompatible ?? "still matches"}");

            session.Stop();
            session.SendCommand(FormattableString.Invariant($":TIMebase:MAIN:SCALe {originalScale:G6}"));
            Thread.Sleep(300);

            if (changes == 0)
                throw new IOException("The timebase change was not picked up.");

            Console.WriteLine();
            string path = Path.Combine(Path.GetTempPath(), "rigol_ui_check.json");
            session.SaveMask(path);
            session.LoadMask(path);
            File.Delete(path);

            Console.WriteLine();
            Console.WriteLine("Session check passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("FAILED: " + exception.Message);
            return 1;
        }
    }
}
