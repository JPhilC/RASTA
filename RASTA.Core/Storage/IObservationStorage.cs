using System.Threading.Tasks;
using RASTA.Core.Capture;

namespace RASTA.Core.Storage
{
    public interface IObservationStorage
    {
        Task SaveAsync(string path, ObservationRecord record);
        Task<ObservationRecord> LoadAsync(string path);
    }
}
