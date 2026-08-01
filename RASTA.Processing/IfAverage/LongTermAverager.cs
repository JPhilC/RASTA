namespace RASTA.Processing.IfAverage
{
    public class LongTermAverager
    {
        private readonly int _size;
        private double[] _sum;
        private double[] _history;
        private double[] _output;

        private int _window;
        private int _index;
        private int _count;

        public int Window
        {
            get => _window;
            set
            {
                _window = Math.Max(1, value);
                _history = new double[_window * _size];
                _sum = new double[_size];
                _output = new double[_size];
                _index = 0;
                _count = 0;
            }
        }

        public LongTermAverager(int size)
        {
            _size = size;
            Window = 10;
        }

        public void Process(double[] input, double[] output)
        {
            int offset = _index * _size;
            bool full = _count >= _window;

            for (int i = 0; i < _size; i++)
            {
                if (full)
                    _sum[i] -= _history[offset + i];

                _sum[i] += input[i];
                _history[offset + i] = input[i];
            }

            _index = (_index + 1) % _window;
            if (!full) _count++;

            double div = _count;
            for (int i = 0; i < _size; i++)
                output[i] = _sum[i] / div;
        }
    }

}
