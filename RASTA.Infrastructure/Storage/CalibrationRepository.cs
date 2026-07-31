using RASTA.Core.Calibration;
using RASTA.Infrastructure.Services;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;

namespace RASTA.Infrastructure.Storage
{

    public class CalibrationRepository
    {
        private readonly UserOptionsService _userOptionsService;
        private readonly string filePath;

        private static readonly JsonSerializerOptions Options =
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };

        public CalibrationRepository(UserOptionsService userOptionsService)
        {
            _userOptionsService = userOptionsService;
            var folder = Path.Combine(_userOptionsService.Options.CaptureFolder);
            Directory.CreateDirectory(folder);

            filePath = Path.Combine(folder, "calibration.json");
        }

        public async Task SaveAsync(CalibrationProfile profile)
        {
            var json = JsonSerializer.Serialize(profile, Options);
            await File.WriteAllTextAsync(filePath, json);
        }

        public async Task<CalibrationProfile?> LoadAsync()
        {
            if (!File.Exists(filePath))
                return null;

            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<CalibrationProfile>(json, Options);
        }
    }
}
