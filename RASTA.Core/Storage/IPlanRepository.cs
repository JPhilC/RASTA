using RASTA.Core.Capture;

namespace RASTA.Core.Storage
{
    public interface IPlanRepository
    {
        void Save(CapturePlan plan);
        CapturePlan Load(string friendlyName);

        // Plans are no longer tied to a specific SDR device - Plan can now be used entirely
        // offline (e.g. to prepare plans before any hardware is even plugged in), so every
        // plan in the folder is listed.
        IEnumerable<CapturePlan> ListPlans();
    }

}
