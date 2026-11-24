namespace RASTA.Core.Sdr;

/// <summary>
/// ADC clipping check on raw RTL-SDR I/Q bytes (issue #33).
///
/// The RTL-SDR delivers unsigned 8-bit I and Q samples, so a sample at 0 or 255 is pinned at
/// the ADC rail. The fraction of bytes at the rail is the direct hardware measure of front-end
/// overload: unlike anything read off the spectrum, it can't be fooled by a strong spur that
/// never actually clipped. Ordinary Gaussian noise puts a few samples on the rail, so the
/// limit is a small fraction rather than zero.
///
/// Used by the gain sweep (against a terminator), by the cold-sky baseline (which is refused
/// over the limit) and by every sweep and Quick capture (kept, but warned about and recorded
/// in FITS as CLIPFRAC). H1-HUNTER counts bytes ≤ 3 or ≥ 252 and rejects a calibration
/// capture above 0.1%; RASTA keeps its stricter rail-only count and the 0.05% limit the gain
/// sweep has always used.
/// </summary>
public static class AdcClipping
{
    /// <summary>Largest acceptable fraction of raw I/Q bytes at the rail (0.05%).</summary>
    public const double MaxAcceptableFraction = 0.0005;

    /// <summary>Fraction of raw I/Q bytes at 0 or 255; 0 for an empty buffer.</summary>
    public static double Fraction(ReadOnlySpan<byte> rawIq)
    {
        if (rawIq.IsEmpty)
            return 0.0;

        // MemoryExtensions.Count is vectorised - a 60 s capture is ~290 MB.
        int clipped = rawIq.Count((byte)0) + rawIq.Count((byte)255);
        return (double)clipped / rawIq.Length;
    }

    /// <summary>True when a measured fraction is over the limit; false when not measured.</summary>
    public static bool IsClipped(double? fraction) => fraction > MaxAcceptableFraction;

    /// <summary>Short display form, e.g. "0.012%" or "0.310% - clipped".</summary>
    public static string Describe(double? fraction)
    {
        if (fraction is not double f)
            return "not measured";
        return IsClipped(f) ? $"{Percent(f)} - clipped" : Percent(f);
    }

    /// <summary>A fraction as a percentage, e.g. "0.012%".</summary>
    public static string Percent(double fraction) =>
        fraction > 0.0 && fraction < 0.00001 ? "<0.001%" : $"{fraction * 100.0:0.000}%";
}
