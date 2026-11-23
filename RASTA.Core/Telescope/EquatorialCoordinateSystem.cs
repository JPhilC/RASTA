namespace RASTA.Core.Telescope;

/// <summary>
/// The equinox/epoch a mount reports and accepts RA/Dec in - the values of the ASCOM
/// <c>EquatorialCoordinateType</c> enumeration (Alpaca <c>equatorialsystem</c>), same numbers.
/// Everything inside RASTA (plans, TelescopeState, FITS headers, LSR, Galactic conversion) is
/// J2000; AscomTelescopeMount converts at the boundary - see issue #38.
/// </summary>
public enum EquatorialCoordinateSystem
{
    /// <summary>equOther: custom or unknown - RASTA falls back to a user setting.</summary>
    Other = 0,

    /// <summary>equTopocentric ("JNow"): coordinates of date. The usual amateur-mount choice.</summary>
    Topocentric = 1,

    /// <summary>equJ2000.</summary>
    J2000 = 2,

    /// <summary>equJ2050.</summary>
    J2050 = 3,

    /// <summary>equB1950.</summary>
    B1950 = 4,
}

public static class EquatorialCoordinateSystemExtensions
{
    public static string DisplayName(this EquatorialCoordinateSystem system) => system switch
    {
        EquatorialCoordinateSystem.Topocentric => "JNow (of date)",
        EquatorialCoordinateSystem.J2000 => "J2000",
        EquatorialCoordinateSystem.J2050 => "J2050",
        EquatorialCoordinateSystem.B1950 => "B1950",
        _ => "Other / unknown",
    };
}
