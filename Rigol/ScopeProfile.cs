namespace Rigol;

/// <summary>
/// The instrument facts that differ between the scope families this tool drives, looked up from
/// the model reported by *IDN?.
/// </summary>
/// <remarks>
/// Almost everything else is written against the waveform preamble, which is model-neutral: the
/// sample interval, the vertical scaling and the point count all come off the wire. These are the
/// values the preamble does not carry — the graticule the user is looking at, whether the read
/// size can be asked for at all, and the ranges the instrument's own pass/fail mask accepts.
/// </remarks>
public sealed record ScopeProfile
{
    /// <summary>Family name, as shown in the window title.</summary>
    public required string Family { get; init; }

    /// <summary>Graticule columns. The DHO draws ten, the DS1000Z twelve.</summary>
    public required int HorizontalDivisions { get; init; }

    /// <summary>Graticule rows. Eight on both families.</summary>
    public required int VerticalDivisions { get; init; }

    /// <summary>Points a full screen holds in NORMal mode.</summary>
    public required int ScreenPoints { get; init; }

    /// <summary>
    /// Whether the read size can be set with :WAVeform:POINts. The DHO documents the command;
    /// the DS1000Z programming guide has no such command at all — its :WAVeform subsystem sizes
    /// reads with STARt/STOP, and a screen read is always the full 1200 points. The instrument
    /// does answer the query, but nothing documents the setter, so it is left alone.
    /// </summary>
    public required bool SupportsWaveformPoints { get; init; }

    /// <summary>Lowest :MASK:X the instrument accepts, in divisions.</summary>
    public required double MaskXMinimum { get; init; }

    /// <summary>Highest :MASK:X the instrument accepts, in divisions.</summary>
    public required double MaskXMaximum { get; init; }

    /// <summary>Step :MASK:X is quantised to, in divisions.</summary>
    public required double MaskXStep { get; init; }

    /// <summary>Lowest :MASK:Y the instrument accepts, in divisions.</summary>
    public required double MaskYMinimum { get; init; }

    /// <summary>Highest :MASK:Y the instrument accepts, in divisions.</summary>
    public required double MaskYMaximum { get; init; }

    /// <summary>Step :MASK:Y is quantised to, in divisions.</summary>
    public required double MaskYStep { get; init; }

    /// <summary>False when the model was not recognised and these are fallback values.</summary>
    public bool Recognised { get; init; } = true;

    /// <summary>
    /// DHO800/DHO900. Ranges from the programming guide, sections 3.15.4 and 3.15.5; the ten
    /// columns follow from XINCrement being TimeScale/100 over a 1,000 point screen.
    /// </summary>
    public static readonly ScopeProfile Dho = new()
    {
        Family = "DHO800/DHO900",
        HorizontalDivisions = 10,
        VerticalDivisions = 8,
        ScreenPoints = 1000,
        SupportsWaveformPoints = true,
        MaskXMinimum = 0.01,
        MaskXMaximum = 2.0,
        MaskXStep = 0.01,
        MaskYMinimum = 0.04,
        MaskYMaximum = 2.0,
        MaskYStep = 0.01,
    };

    /// <summary>
    /// DS1000Z / MSO1000Z. Twelve columns and the 1,200 point screen are from the user guide;
    /// the mask ranges and their steps are from the programming guide, :MASK:X and :MASK:Y.
    /// </summary>
    public static readonly ScopeProfile Ds1000Z = new()
    {
        Family = "DS1000Z/MSO1000Z",
        HorizontalDivisions = 12,
        VerticalDivisions = 8,
        ScreenPoints = 1200,
        SupportsWaveformPoints = false,
        MaskXMinimum = 0.02,
        MaskXMaximum = 4.0,
        MaskXStep = 0.02,
        MaskYMinimum = 0.04,
        MaskYMaximum = 5.12,
        MaskYStep = 0.04,
    };

    /// <summary>
    /// Used when *IDN? reports something neither family claims. The DHO values are the safer
    /// fallback: its mask range is the narrower of the two, so a tolerance accepted here is
    /// accepted by both, and asking for fewer screen points than the instrument holds costs
    /// resolution rather than failing the read.
    /// </summary>
    public static readonly ScopeProfile Unknown = Dho with { Family = "unknown model", Recognised = false };

    /// <summary>
    /// Pick the profile for a model string as *IDN? reports it, e.g. "DS1104Z" or "DHO924S".
    /// </summary>
    /// <remarks>
    /// Matched on the family rather than on a list of models: the DS1000Z series alone covers
    /// DS1054Z, DS1074Z, DS1104Z and their -S and Plus variants, plus the MSO1000Z models that
    /// share the same command set. A DS1054Z with the bandwidth options unlocked reports itself
    /// as a DS1104Z, so an exact model match would miss the very scope this was written for.
    /// </remarks>
    public static ScopeProfile For(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return Unknown;

        string name = model.Trim().ToUpperInvariant();

        if (name.StartsWith("DHO", StringComparison.Ordinal))
            return Dho;

        // DS1000Z and MSO1000Z: a 1000-series number with the Z suffix somewhere after it.
        if ((name.StartsWith("DS1", StringComparison.Ordinal) || name.StartsWith("MSO1", StringComparison.Ordinal))
            && name.Contains('Z', StringComparison.Ordinal))
            return Ds1000Z;

        return Unknown;
    }

    /// <summary>
    /// Most waveform points the instrument returns for a single :WAVeform:DATA?, for the format
    /// in use. The DS1000Z programming guide tabulates these and a longer read has to be made in
    /// batches; the DHO guide documents no cap and its own examples read 100,000 points at once,
    /// so it is reported as unlimited.
    /// </summary>
    public int MaxPointsPerRead(Rigol.Waveform.Format format)
    {
        if (this == Ds1000Z)
        {
            return format switch
            {
                Rigol.Waveform.Format.BYTE => 250_000,
                Rigol.Waveform.Format.WORD => 125_000,
                _ => 15_625,
            };
        }

        return int.MaxValue;
    }

    /// <summary>Clamp a horizontal tolerance to what the instrument's mask accepts.</summary>
    public double ClampMaskX(double divisions) => Math.Clamp(divisions, MaskXMinimum, MaskXMaximum);

    /// <summary>Clamp a vertical tolerance to what the instrument's mask accepts.</summary>
    public double ClampMaskY(double divisions) => Math.Clamp(divisions, MaskYMinimum, MaskYMaximum);
}
