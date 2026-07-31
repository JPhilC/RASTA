using CommunityToolkit.Mvvm.ComponentModel;

namespace RASTA.Core.Config
{
    public partial class UserOptions: ObservableObject
    {
        [ObservableProperty]
        private string captureFolder = "C:\\RAW\\RASTA\\Captures";

        [ObservableProperty]
        private string plansFolder = "C:\\RAW\\RASTA\\Plans";

    }
}
