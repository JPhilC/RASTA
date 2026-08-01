using System;
using System.Collections.Generic;
using System.Text;

namespace RASTA.Processing.IfAverage
{
    public class BackgroundSubtractor
    {
        private readonly int _size;
        private readonly double[] _background;

        public bool Enabled { get; set; }
        public bool Recording { get; set; }

        public BackgroundSubtractor(int size)
        {
            _size = size;
            _background = new double[size];
        }

        public void Load(double[] baseline)
        {
            Array.Copy(baseline, _background, _size);
        }

        public void Process(double[] input)
        {
            if (Recording)
            {
                for (int i = 0; i < _size; i++)
                    _background[i] = Math.Max(input[i], 1e-20);
            }
        }

        public void Subtract(double[] input)
        {
            if (!Enabled)
                return;

            for (int i = 0; i < _size; i++)
            {
                double bg = Math.Max(_background[i], 1e-20);
                input[i] = 20 * Math.Log10(input[i] + 1e-20) - 20 * Math.Log10(bg);
            }
        }
    }
}
