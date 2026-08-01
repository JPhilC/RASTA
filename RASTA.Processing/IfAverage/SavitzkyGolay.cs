namespace RASTA.Processing.IfAverage
{
    public class SavitzkyGolay
    {
        private static readonly double[] C = { -3.0 / 35, 12.0 / 35, 17.0 / 35, 12.0 / 35, -3.0 / 35 };

        public bool Enabled { get; set; }

        public void Process(double[] data)
        {
            if (!Enabled)
                return;

            int n = data.Length;
            double[] tmp = new double[n];

            for (int i = 2; i < n - 2; i++)
            {
                tmp[i] =
                    C[0] * data[i - 2] +
                    C[1] * data[i - 1] +
                    C[2] * data[i] +
                    C[3] * data[i + 1] +
                    C[4] * data[i + 2];
            }

            Array.Copy(tmp, data, n);
        }
    }

}
