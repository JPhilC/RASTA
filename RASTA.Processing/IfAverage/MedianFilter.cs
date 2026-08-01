namespace RASTA.Processing.IfAverage
{
    public class MedianFilter
    {
        private readonly int _size;
        private readonly int _window = 5;
        private readonly double[][] _history;
        private int _index;

        public bool Enabled { get; set; }

        public MedianFilter(int size)
        {
            _size = size;
            _history = new double[_window][];
            for (int i = 0; i < _window; i++)
                _history[i] = new double[size];
        }

        public void Process(double[] input, double[] output)
        {
            if (!Enabled)
            {
                Array.Copy(input, output, _size);
                return;
            }

            // store frame
            Array.Copy(input, _history[_index], _size);
            _index = (_index + 1) % _window;

            // median per bin
            for (int i = 0; i < _size; i++)
            {
                output[i] = Median5(
                    _history[0][i],
                    _history[1][i],
                    _history[2][i],
                    _history[3][i],
                    _history[4][i]);
            }
        }

        private static double Median5(double a, double b, double c, double d, double e)
        {
            if (a > b) (a, b) = (b, a);
            if (c > d) (c, d) = (d, c);
            if (a > c) (a, c) = (c, a);
            if (b > d) (b, d) = (d, b);
            if (b > c) (b, c) = (c, b);

            double m1 = Math.Max(a, Math.Min(b, e));
            return Math.Max(m1, Math.Min(c, Math.Max(d, e)));
        }
    }

}
