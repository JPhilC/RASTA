namespace RASTA.Core.Sdr
{
    public class SdrDeviceDescriptor
    {
        public uint Index { get; init; }
        public string Manufacturer { get; init; } = "";
        public string Product { get; init; } = "";
        public string Serial { get; init; } = "";
    }
}
