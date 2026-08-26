using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RASTA.Processing.Mosaic
{
    /// <summary>
    /// Parses the plain-text profile format returned by the AIfA EU-HOU LABprofile service
    /// (https://www.astro.uni-bonn.de/hisurvey/euhou/LABprofile/) - the Leiden/Argentine/Bonn
    /// (LAB) Galactic HI Survey, used as synthetic multi-position test data for RASTA's Mosaic
    /// 2D/3D visualisation code (see LabSurveyMosaicProcessor). Format, from a real download:
    ///
    ///   %  AIfA EU-HOU server: LAB  survey data
    ///   % wanted position:  l, b, RA, DEC, interpolated  NH and beam width FWHM
    ///   % 84.29 ;  10.41 ; 300.00 ;  50.00 ;  0.107E+22;   9.98
    ///   %%LAB     777  datapoints: v_lsr [km/s], T_B [K], freq. [Mhz], wavel. [cm]
    ///       -399.83      0.00   1422.300   21.07800
    ///       ...
    ///
    /// RA/DEC on the header line are already decimal degrees (not hours) - see the
    /// RaDeg/15.0 conversion in LabSurveyMosaicProcessor, matching how MosaicProcessor's own
    /// FitsFileMetaData.RaDeg -> MosaicPosition.RaHours conversion works for real captures.
    /// </summary>
    public static class LabSurveyProfileParser
    {
        /// <summary>
        /// One parsed LAB Survey profile: the requested sky position (as the service actually
        /// resolved it - see the header line) and its brightness-temperature spectrum.
        /// T_B is a physical brightness temperature in Kelvin, already background/stray-
        /// radiation corrected by the survey itself - unlike a real RASTA capture there's no
        /// separate baseline file to divide out (see LabSurveyMosaicProcessor's remarks on
        /// what this means for the MosaicPosition fields it produces).
        /// </summary>
        public record LabProfile(
            double GalacticLDeg,
            double GalacticBDeg,
            double RaDeg,
            double DecDeg,
            double[] VelocityKmPerSec,
            double[] BrightnessTempK,
            double[] FrequencyMHz);

        /// <summary>
        /// Cheap signature check - does this file look like a LAB profile at all - without
        /// fully parsing it. Reads only the first kilobyte, since the "%%LAB" marker is
        /// always on the file's 4th line.
        /// </summary>
        public static bool LooksLikeLabProfile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var buffer = new byte[1024];
                int read = stream.Read(buffer, 0, buffer.Length);
                string text = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                return text.Contains("%%LAB");
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static LabProfile Parse(string path)
        {
            string[] lines = File.ReadAllLines(path);

            string? positionLine = lines
                .Select(l => l.TrimStart('%').Trim())
                .FirstOrDefault(l => l.Contains(';'));

            if (positionLine is null)
                throw new InvalidOperationException($"'{path}' has no recognisable LAB profile position line.");

            // "84.29 ;  10.41 ; 300.00 ;  50.00 ;  0.107E+22;   9.98"  ->  l ; b ; RA ; DEC ; NH ; beam
            string[] parts = positionLine.Split(';');
            if (parts.Length < 4)
                throw new InvalidOperationException($"'{path}' position line has fewer fields than expected: '{positionLine}'.");

            double lDeg = ParseInvariant(parts[0], path);
            double bDeg = ParseInvariant(parts[1], path);
            double raDeg = ParseInvariant(parts[2], path);
            double decDeg = ParseInvariant(parts[3], path);

            var velocities = new List<double>();
            var temps = new List<double>();
            var freqs = new List<double>();

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('%'))
                    continue;

                string[] cols = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length < 3)
                    continue; // stray blank/short line - the sample downloads end with one

                velocities.Add(ParseInvariant(cols[0], path));
                temps.Add(ParseInvariant(cols[1], path));
                freqs.Add(ParseInvariant(cols[2], path));
            }

            if (velocities.Count == 0)
                throw new InvalidOperationException($"'{path}' contained a LAB profile header but no data rows.");

            return new LabProfile(lDeg, bDeg, raDeg, decDeg, velocities.ToArray(), temps.ToArray(), freqs.ToArray());
        }

        private static double ParseInvariant(string s, string path)
        {
            if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new InvalidOperationException($"'{path}' contains a value that isn't a number: '{s}'.");
            return value;
        }
    }
}
