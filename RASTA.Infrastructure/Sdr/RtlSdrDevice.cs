using RASTA.Core.Sdr;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RASTA.Infrastructure.Sdr
{
    public class RtlSdrDevice : ISdrDevice, IDisposable
    {
        private IntPtr _device = IntPtr.Zero;
        private bool _isOpen = false;

        // ---------------------------
        // P/Invoke declarations
        // ---------------------------

        [DllImport("rtlsdr.dll")]
        private static extern int rtlsdr_open(out IntPtr device, uint index);

        [DllImport("rtlsdr.dll")]
        private static extern int rtlsdr_close(IntPtr device);

        [DllImport("rtlsdr.dll")]
        private static extern int rtlsdr_set_center_freq(IntPtr device, uint freq);

        [DllImport("rtlsdr.dll")]
        private static extern int rtlsdr_set_sample_rate(IntPtr device, uint rate);

        [DllImport("rtlsdr.dll")]
        private static extern int rtlsdr_set_tuner_gain(IntPtr device, int gain);

        [DllImport("rtlsdr.dll")]
        private static extern int rtlsdr_reset_buffer(IntPtr device);

        [DllImport("rtlsdr.dll")]
        private static extern int rtlsdr_read_sync(
            IntPtr device,
            IntPtr buffer,
            int length,
            out int bytesRead);

        [DllImport("rtlsdr.dll")]
        private static extern int rtlsdr_set_bias_tee(IntPtr device, int on);


        // ---------------------------
        // Public API
        // ---------------------------

        public void Configure(double centerFreqHz, double sampleRateHz, int gain)
        {
            if (!_isOpen)
                OpenDevice();

            Check(rtlsdr_set_center_freq(_device, (uint)centerFreqHz),
                "Failed to set center frequency.");

            Check(rtlsdr_set_sample_rate(_device, (uint)sampleRateHz),
                "Failed to set sample rate.");

            Check(rtlsdr_set_tuner_gain(_device, gain),
                "Failed to set tuner gain.");

            Check(rtlsdr_reset_buffer(_device),
                "Failed to reset RTL-SDR buffer.");
        }

        public void SetBiasTee(bool enabled)
        {
            if (!_isOpen)
                OpenDevice();

            int value = enabled ? 1 : 0;

            Check(rtlsdr_set_bias_tee(_device, value),
                enabled ? "Failed to enable bias-tee." : "Failed to disable bias-tee.");
        }

        public async IAsyncEnumerable<Complex[]> CaptureBlocksAsync(
            int blockSize,
            [EnumeratorCancellation] CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                yield return await ReadSamplesAsync(blockSize);
            }
        }


        // ---------------------------
        // Internal helpers
        // ---------------------------

        private void OpenDevice()
        {
            int result = rtlsdr_open(out _device, 0);

            Check(result, "Failed to open RTL-SDR device.");

            _isOpen = true;
        }

        private async Task<Complex[]> ReadSamplesAsync(int blockSize)
        {
            int bytesNeeded = blockSize * 2; // I + Q per sample

            byte[] managedBuffer = new byte[bytesNeeded];
            IntPtr nativeBuffer = Marshal.AllocHGlobal(bytesNeeded);

            try
            {
                int bytesRead;

                int result = rtlsdr_read_sync(_device, nativeBuffer, bytesNeeded, out bytesRead);

                Check(result, "RTL-SDR read_sync failed.");

                if (bytesRead != bytesNeeded)
                    throw new Exception($"RTL-SDR returned incomplete block: {bytesRead} bytes.");

                Marshal.Copy(nativeBuffer, managedBuffer, 0, bytesNeeded);

                var samples = new Complex[blockSize];

                for (int i = 0; i < blockSize; i++)
                {
                    float iVal = (managedBuffer[2 * i] - 128) / 128f;
                    float qVal = (managedBuffer[2 * i + 1] - 128) / 128f;

                    samples[i] = new Complex(iVal, qVal);
                }

                return samples;
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBuffer);
                await Task.Yield();
            }
        }

        private static void Check(int result, string message)
        {
            if (result != 0)
                throw new Exception(message);
        }


        // ---------------------------
        // Disposal
        // ---------------------------

        public void Dispose()
        {
            if (_isOpen && _device != IntPtr.Zero)
            {
                rtlsdr_close(_device);
                _device = IntPtr.Zero;
                _isOpen = false;
            }
        }
    }
}
