using RASTA.Core.Config;  
using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace RASTA.Infrastructure.Services
{
    public class UserOptionsService
    {
        private readonly string _path;
        private readonly object _sync = new();

        public UserOptions Options { get; private set; }

        public UserOptionsService()
        {
            _path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RASTA",
                "useroptions.json");

            Options = LoadInternal();
            
            HookChangeTracking();
        }

        // ---------------------------------------------------------
        // Load
        // ---------------------------------------------------------
        private UserOptions LoadInternal()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var loaded = JsonSerializer.Deserialize<UserOptions>(json);

                    if (loaded != null)
                        return loaded;
                }
            }
            catch
            {
                // swallow and fall back to defaults
            }

            return new UserOptions();
        }

        // ---------------------------------------------------------
        // Save
        // ---------------------------------------------------------
        private void SaveInternal()
        {
            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                    var json = JsonSerializer.Serialize(
                        Options,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                    File.WriteAllText(_path, json);
                }
                catch
                {
                    // swallow errors — user options should never crash the app
                }
            }
        }

        // ---------------------------------------------------------
        // Change tracking
        // ---------------------------------------------------------
        private void HookChangeTracking()
        {
            Options.PropertyChanged += (_, __) => SaveInternal();
        }
    }
}
