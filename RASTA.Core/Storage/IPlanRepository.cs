using RASTA.Core.Capture;

namespace RASTA.Core.Storage
{
    public interface IPlanRepository
    {
        void Save(CapturePlan plan);
        CapturePlan Load(string name);
        IEnumerable<CapturePlan> ListPlans();
    }
}
