using NUnit.Framework;
using RASTA.Processing.HiPipeline;

namespace RASTA.Tests.HiPipeline
{
    [TestFixture]
    public class SpectrumBinnerTests
    {
        [Test]
        public void BinAverage_AveragesAdjacentBins()
        {
            double[] spectrum = { 1, 3, 5, 7, 10, 20, 0, 4 };

            double[] binned = SpectrumBinner.BinAverage(spectrum, 4);

            Assert.That(binned, Is.EqualTo(new double[] { 2, 6, 15, 2 }));
        }

        [Test]
        public void BinAverage_SameSize_ReturnsInput()
        {
            double[] spectrum = { 1, 2, 3, 4 };

            Assert.That(SpectrumBinner.BinAverage(spectrum, 4), Is.SameAs(spectrum));
        }

        [Test]
        public void BinAverage_PreservesMeanLevel()
        {
            var rng = new Random(1);
            double[] spectrum = Enumerable.Range(0, 4096).Select(_ => rng.NextDouble()).ToArray();

            double[] binned = SpectrumBinner.BinAverage(spectrum, 256);

            Assert.That(binned.Average(), Is.EqualTo(spectrum.Average()).Within(1e-12));
        }

        [Test]
        public void BinAverage_NonIntegerRatio_Throws()
        {
            Assert.That(() => SpectrumBinner.BinAverage(new double[10], 4), Throws.InvalidOperationException);
        }
    }
}
