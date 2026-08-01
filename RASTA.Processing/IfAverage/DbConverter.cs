namespace RASTA.Processing.IfAverage
{
    public class DbConverter
    {
        public double Offset { get; set; }

        public void Process(double[] input, double[] output)
        {
            for (int i = 0; i < input.Length; i++)
                output[i] = 20 * Math.Log10(input[i] + 1e-20) + Offset;
        }
    }

}
