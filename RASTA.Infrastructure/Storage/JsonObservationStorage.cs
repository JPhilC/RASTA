using RASTA.Core.Capture;
using RASTA.Core.Storage;
using System.IO;
using System.Text.Json;

namespace RASTA.Infrastructure.Storage
{
    public class JsonObservationStorage : IObservationStorage
    {
        private readonly JsonSerializerOptions _options;

        public JsonObservationStorage()
        {
            _options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                IncludeFields = true
            };
        }

        public async Task SaveAsync(string path, ObservationRecord record)
        {
            using FileStream fs = File.Create(path);
            await JsonSerializer.SerializeAsync(fs, record, _options);
        }

        public async Task<ObservationRecord> LoadAsync(string path)
        {
            using FileStream fs = File.OpenRead(path);
            var record = await JsonSerializer.DeserializeAsync<ObservationRecord>(fs, _options);

            if (record == null)
                throw new IOException("Failed to load observation record.");

            return record;
        }
    }
}
