using System;
using System.Collections.Generic;
using System.Text;

namespace RASTA.Processing.IfAverage
{
    public class BackgroundSubtractor
    {
        private readonly int _size;
        private readonly double[] _background;

        public bool SubractEnabled { get; set; }

        public bool DivideEnabled { get; set; }

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
            if (!SubractEnabled)
                return;

            for (int i = 0; i < _size; i++)
            {
                double bg = Math.Max(_background[i], 1e-20);
                input[i] = input[i] / bg;   // ratio sweep / baseline, linear
            }
        }

        public void Divide(double[] input)
        {
            if (!DivideEnabled)
                return;

            for (int i = 0; i < _size; i++)
            {
                input[i] = input[i] / _background[i];   // ratio sweep / baseline, linear
            }
        }

    }
}
