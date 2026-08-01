namespace RASTA.Processing.IfAverage
{
    public class IntermediateAverager
    {
        private readonly int _size;
        private readonly double[] _sum;
        private int _count;

        public int Window { get; set; } = 10;

        public IntermediateAverager(int size)
        {
            _size = size;
            _sum = new double[size];
        }

        public bool Process(double[] input, double[] output)
        {
            for (int i = 0; i < _size; i++)
                _sum[i] += input[i];

            _count++;

            if (_count < Window)
                return false;

            for (int i = 0; i < _size; i++)
                output[i] = Math.Sqrt((_sum[i] / _count) / _size);

            Array.Clear(_sum, 0, _size);
            _count = 0;

            return true;
        }
    }

}
