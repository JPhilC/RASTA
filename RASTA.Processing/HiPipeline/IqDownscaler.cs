namespace RASTA.Processing.HiPipeline
{
    /// <summary>
    /// Downscales raw IQ frames to a smaller FFT size by block-averaging groups of
    /// consecutive samples within each frame. Promoted from VisualiseViewModel's private
    /// DownscaleIq so both the single-capture Visualise flow and the multi-position Mosaic
    /// flow (MosaicProcessor) share one implementation.
    /// </summary>
    public static class IqDownscaler
    {
        /// <summary>
        /// Downscale raw IQ frames from originalFftSize → targetFftSize,
        /// automatically determining the number of frames from the input length.
        /// </summary>
        public static byte[] Downscale(byte[] iq, int originalFftSize, int targetFftSize)
        {
            int bytesPerFrameIn = originalFftSize * 2;      // IQ: 2 bytes per complex sample
            int bytesPerFrameOut = targetFftSize * 2;

            // Floor the number of frames, ignore any trailing partial frame
            int numFrames = iq.Length / bytesPerFrameIn;
            if (numFrames == 0)
                throw new InvalidOperationException(
                    $"IQ buffer length {iq.Length} is too small for even one frame ({bytesPerFrameIn} bytes each).");

            int factor = originalFftSize / targetFftSize;
            if (originalFftSize % targetFftSize != 0)
                throw new InvalidOperationException(
                    $"FFT downscale must be integer ratio: {originalFftSize} → {targetFftSize}");

            var output = new byte[numFrames * bytesPerFrameOut];

            byte[] DownsampleFrame(byte[] frame)
            {
                var result = new byte[bytesPerFrameOut];

                for (int i = 0; i < targetFftSize; i++)
                {
                    int start = i * factor;

                    double sumI = 0;
                    double sumQ = 0;

                    for (int j = 0; j < factor; j++)
                    {
                        int idx = (start + j) * 2;
                        sumI += frame[idx];
                        sumQ += frame[idx + 1];
                    }

                    result[i * 2] = (byte)(sumI / factor);
                    result[i * 2 + 1] = (byte)(sumQ / factor);
                }

                return result;
            }

            for (int f = 0; f < numFrames; f++)
            {
                var frameIn = new byte[bytesPerFrameIn];
                Buffer.BlockCopy(iq, f * bytesPerFrameIn, frameIn, 0, bytesPerFrameIn);

                var frameOut = DownsampleFrame(frameIn);

                Buffer.BlockCopy(frameOut, 0, output, f * bytesPerFrameOut, bytesPerFrameOut);
            }

            return output;
        }
    }
}
