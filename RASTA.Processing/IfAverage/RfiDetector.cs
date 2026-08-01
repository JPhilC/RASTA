namespace RASTA.Processing.IfAverage
{
    public class RfiDetector
    {
        public bool Enabled { get; set; }
        public double Threshold { get; set; } = 10.0;
        public int RejectedFrames { get; private set; }

        public void Process(double[] raw, double[] median)
        {
            if (!Enabled)
                return;

            int mid = raw.Length / 2;

            if (raw[mid] > median[mid] * Threshold)
                RejectedFrames++;
        }
    }

}
