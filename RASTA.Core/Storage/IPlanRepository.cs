using RASTA.Core.Capture;

namespace RASTA.Core.Storage
{
    public interface IPlanRepository
    {
        void Save(CapturePlan plan);
        CapturePlan Load(string friendlyName);
        IEnumerable<CapturePlan> ListPlans(string sdrDeviceId);
    }

}
