/****************************************************************************** 
 * PROGRAM NAME:  SDR AVE 
 * CLASS:         IFAverageProcessor 
 * VERSION:       3.x.x 
 * DESCRIPTION:   Advanced Signal Averaging Plugin for #SDR (SDRSharp) 
 * AUTHOR:        Daniel M. Kamiński 
 * LOCATION:      Lublin 2026, Poland 
 * ------------------------------------------------------------------------ 
 * Copyright (c) 2026 Daniel M. Kamiński. All rights reserved. 
 ******************************************************************************/

namespace RASTA.Processing.IfAverage
{
    public class IfAverageProcessor
    {
        public MedianFilter Median { get; private set; }
        public RfiDetector Rfi { get; private set; }
        public IntermediateAverager Intermediate { get; private set; }
        public LongTermAverager LongTerm { get; private set; }
        public BackgroundSubtractor Background { get; private set; }
        public DbConverter Db { get; private set; }
        public SavitzkyGolay SavitzkyGolay { get; private set; }

        public bool LinearOutput { get; set; }

        private readonly double[] _tmp1;
        private readonly double[] _tmp2;

        public IfAverageProcessor(int size)
        {
            Median = new MedianFilter(size);
            Rfi = new RfiDetector();
            Intermediate = new IntermediateAverager(size);
            LongTerm = new LongTermAverager(size);
            Background = new BackgroundSubtractor(size);
            Db = new DbConverter();
            SavitzkyGolay = new SavitzkyGolay();

            _tmp1 = new double[size];
            _tmp2 = new double[size];
        }

        public void Process(double[] power, double[] outputDb)
        {
            // 1. Median
            Median.Process(power, _tmp1);

            // 2. RFI detection
            Rfi.Process(power, _tmp1);

            // 3. Intermediate averaging
            if (!Intermediate.Process(_tmp1, _tmp2))
                return;

            // 4. Long-term averaging
            LongTerm.Process(_tmp2, _tmp1);

            // 5. Background subtraction
            Background.Subtract(_tmp1);

            // 5. Background ratio
            Background.Divide(_tmp1);

            if (!LinearOutput)
            {

                // 6. dB conversion
                Db.Process(_tmp1, outputDb);

                // 7. Savitzky–Golay
                SavitzkyGolay.Process(outputDb);
            }
            else
            {
                // baseline stays in linear domain
                Array.Copy(_tmp1, outputDb, _tmp1.Length);
            }
        }
    }

}
