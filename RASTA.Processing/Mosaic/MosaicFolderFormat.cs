using System.IO;
using System.Linq;

namespace RASTA.Processing.Mosaic
{
    /// <summary>
    /// What kind of data a Mosaic session folder actually contains, as picked by
    /// MosaicFolderFormatDetector.Detect. Drives which processor MosaicViewModel.
    /// GenerateMosaicAsync hands the folder to - MosaicProcessor for real RASTA captures,
    /// LabSurveyMosaicProcessor for LAB Survey profile text files used as synthetic test
    /// data (see LabSurveyMosaicProcessor's remarks) - without the caller needing to know
    /// the file-level details itself.
    /// </summary>
    public enum MosaicFolderFormat
    {
        /// <summary>Folder doesn't exist, or contains neither .fits nor recognisable .txt files.</summary>
        Empty,

        /// <summary>One or more .fits files - the real capture pipeline (MosaicProcessor).</summary>
        RastaFits,

        /// <summary>One or more LAB Survey profile .txt files (LabSurveyMosaicProcessor).</summary>
        LabSurveyText,

        /// <summary>Folder has files, but nothing recognised as either format.</summary>
        Unrecognised
    }

    /// <summary>
    /// Sniffs a Mosaic session folder's contents to decide which processor should handle it.
    /// FITS takes priority if a folder somehow has both (a real capture session is never
    /// expected to also contain LAB profile text files, but if it did, the real data should
    /// win) - this is a cheap, format-only sniff (extension + a one-line signature check),
    /// not a full validation; MosaicProcessor/LabSurveyMosaicProcessor still do their own
    /// deeper validation (baseline presence, matching FFT size, parseable header, etc.) once
    /// actually asked to process.
    /// </summary>
    public static class MosaicFolderFormatDetector
    {
        public static MosaicFolderFormat Detect(string? folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return MosaicFolderFormat.Empty;

            if (Directory.GetFiles(folder, "*.fits").Length > 0)
                return MosaicFolderFormat.RastaFits;

            var txtFiles = Directory.GetFiles(folder, "*.txt");
            if (txtFiles.Length == 0)
                return MosaicFolderFormat.Empty;

            return txtFiles.Any(LabSurveyProfileParser.LooksLikeLabProfile)
                ? MosaicFolderFormat.LabSurveyText
                : MosaicFolderFormat.Unrecognised;
        }
    }
}
