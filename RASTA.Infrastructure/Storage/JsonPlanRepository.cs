using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RASTA.Core.Capture;

namespace RASTA.Infrastructure.Storage
{
    public interface IPlanRepository
    {
        void Save(CapturePlan plan);
        CapturePlan Load(string friendlyName);
        IEnumerable<CapturePlan> ListPlans(string sdrDeviceId);
    }

    public class JsonPlanRepository : IPlanRepository
    {
        private readonly string folder;

        private static readonly JsonSerializerOptions Options =
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true
            };

        public JsonPlanRepository()
        {
            folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RASTA",
                "Plans");

            Directory.CreateDirectory(folder);
        }

        public void Save(CapturePlan plan)
        {
            if (string.IsNullOrWhiteSpace(plan.FriendlyName))
                throw new ArgumentException("CapturePlan must have a FriendlyName before saving.");

            var fileName = SanitizeFileName(plan.FriendlyName) + ".json";
            var path = Path.Combine(folder, fileName);

            var json = JsonSerializer.Serialize(plan, Options);
            File.WriteAllText(path, json);
        }

        public CapturePlan Load(string friendlyName)
        {
            var fileName = SanitizeFileName(friendlyName) + ".json";
            var path = Path.Combine(folder, fileName);

            if (!File.Exists(path))
                throw new FileNotFoundException($"Plan '{friendlyName}' not found.", path);

            var json = File.ReadAllText(path);
            var plan = JsonSerializer.Deserialize<CapturePlan>(json, Options);

            if (plan == null)
                throw new InvalidOperationException($"Failed to deserialize plan '{friendlyName}'.");

            return plan;
        }

        public IEnumerable<CapturePlan> ListPlans(string sdrDeviceId)
        {
            foreach (var file in Directory.GetFiles(folder, "*.json"))
            {
                CapturePlan? plan = null;

                try
                {
                    var json = File.ReadAllText(file);
                    plan = JsonSerializer.Deserialize<CapturePlan>(json, Options);
                }
                catch
                {
                    // Skip corrupted or unreadable files
                }

                if (plan != null && plan.SdrDeviceId == sdrDeviceId)
                    yield return plan;
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name.Trim();
        }
    }
}
