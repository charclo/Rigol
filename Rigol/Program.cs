// Software pass/fail mask test bench for the Rigol DHO924S.
//
// The scope's own :MASK subsystem can only regenerate an envelope around whatever waveform is
// on screen and has no SCPI command to load a stored *.pf file, so the mask is built, stored and
// evaluated here. The bench mirrors the same tolerances onto the hardware mask and compares the
// two verdicts frame by frame.
//
// Usage: Rigol [ip-address] [options]
//          --selftest         verify the mask logic on synthetic waveforms, no scope needed
//          --x <div>          horizontal tolerance, 0.01 to 2   (default 0.24)
//          --y <div>          vertical tolerance,   0.04 to 2   (default 24)
//          --ref <n>          reference frames used to build the mask (default 8)
//          --frames <n>       test frames                             (default 30)
//          --channel <n>      channel under test                      (default 1)
//          --edge-guard <n>   samples ignored at both frame edges     (default 0)
//          --mask <path>      where to store the mask          (default mask.json)
//          --no-hw            do not touch the scope's own pass/fail test
//          --format <fmt>     ASCii, WORD or BYTE                     (default ASCii)
//          --profile          measure where the time in a frame cycle goes

if (args.Contains("--selftest"))
    return Rigol.SelfTest.Run();

string ipAddress = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "192.168.1.12";

var options = new Rigol.MaskBench.Options
{
    Channel = IntArg("--channel", 1),
    Points = IntArg("--points", 1000),
    ReferenceFrames = IntArg("--ref", 8),
    TestFrames = IntArg("--frames", 30),
    XToleranceDiv = DoubleArg("--x", 0.24),
    YToleranceDiv = DoubleArg("--y", 0.24),
    IgnoreEdgeSamples = IntArg("--edge-guard", 0),
    MaskPath = StringArg("--mask", "mask.json"),
    CompareWithHardware = !args.Contains("--no-hw"),
    Format = Enum.TryParse(StringArg("--format", "ASCii"), true, out Rigol.Rigol.Waveform.Format format)
        ? format
        : Rigol.Rigol.Waveform.Format.ASCii,
};

Rigol.Rigol rigol = new(ipAddress);

if (rigol.Connect() != Rigol.Rigol.ErrorCode.None)
{
    Console.WriteLine($"Could not connect to {ipAddress}:5555");
    return 1;
}

try
{
    rigol.IDN();

    if (args.Contains("--repro"))
        Rigol.Profiler.ReproduceTimebaseChurn(rigol, options.Channel);
    else if (args.Contains("--profile"))
        Rigol.Profiler.Run(rigol, options.Channel);
    else
        Rigol.MaskBench.Run(rigol, options);
}
finally
{
    rigol.Disconnect();
}

return 0;

string? RawArg(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

string StringArg(string name, string fallback) => RawArg(name) ?? fallback;

int IntArg(string name, int fallback) =>
    int.TryParse(RawArg(name), System.Globalization.NumberStyles.Integer,
        System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : fallback;

double DoubleArg(string name, double fallback) =>
    double.TryParse(RawArg(name), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : fallback;
