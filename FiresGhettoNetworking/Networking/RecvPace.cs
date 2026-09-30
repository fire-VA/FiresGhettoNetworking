using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// How much a big download brings in per frame. Every Steam message this process receives is counted in the frame that
    /// drained it; a five-second window that took in a megabyte or more is logged with the bytes per frame and the frame
    /// rate. A transfer paced by the receiver's frames (the 2026-09-28 BC join sync: ~128 KB a frame at 32 fps and at 8 fps)
    /// shows as a flat KB-a-frame figure whatever the frame rate.
    /// </summary>
    internal static class RecvPace
    {
        private const float WindowSeconds = 5f;
        private const long BulkWindowBytes = 1024 * 1024;
        private const float BytesPerKilobyte = 1024f;
        private const float BytesPerMegabyte = 1024f * 1024f;

        private static int s_frame = -1;
        private static long s_frameBytes;
        private static int s_frameMessages;
        private static long s_windowBytes;
        private static long s_windowPeakBytes;
        private static int s_windowFramesWithData;
        private static int s_windowMessages;
        private static int s_windowPeakMessages;
        private static float s_windowStart = -1f;
        private static int s_windowStartFrame;

        public static void Note(int bytes)
        {
            int frame = Time.frameCount;
            if (frame != s_frame)
            {
                CloseFrame();
                s_frame = frame;
            }
            s_frameBytes += bytes;
            s_frameMessages++;
        }

        private static void CloseFrame()
        {
            if (s_frameBytes <= 0) return;
            s_windowBytes += s_frameBytes;
            s_windowFramesWithData++;
            s_windowMessages += s_frameMessages;
            if (s_frameBytes > s_windowPeakBytes) s_windowPeakBytes = s_frameBytes;
            if (s_frameMessages > s_windowPeakMessages) s_windowPeakMessages = s_frameMessages;
            s_frameBytes = 0;
            s_frameMessages = 0;
        }

        /// <summary>Called after the frame's receives (from the ZNet.Update postfix).</summary>
        public static void ReportPeriodically()
        {
            float now = Time.unscaledTime;
            if (s_windowStart < 0f) { StartWindow(now); return; }
            if (now - s_windowStart < WindowSeconds) return;
            CloseFrame();
            float seconds = Mathf.Max(0.001f, now - s_windowStart);
            int frames = Mathf.Max(1, Time.frameCount - s_windowStartFrame);
            if (s_windowBytes >= BulkWindowBytes)
            {
                float perFrame = s_windowBytes / (float)Mathf.Max(1, s_windowFramesWithData) / BytesPerKilobyte;
                LoggerOptions.LogMessage($"[RecvPace] last {seconds:F0} s: {s_windowBytes / BytesPerMegabyte:F1} MB in {s_windowMessages} Steam "
                    + $"message(s) over {s_windowFramesWithData} of {frames} frame(s) at {frames / seconds:F0} fps "
                    + $"({s_windowBytes / BytesPerMegabyte / seconds:F1} MB/s): {perFrame:F0} KB a frame with data, peak "
                    + $"{s_windowPeakBytes / BytesPerKilobyte:F0} KB and {s_windowPeakMessages} message(s) in one frame.");
            }
            StartWindow(now);
        }

        private static void StartWindow(float now)
        {
            s_windowStart = now;
            s_windowStartFrame = Time.frameCount;
            s_windowBytes = 0;
            s_windowPeakBytes = 0;
            s_windowFramesWithData = 0;
            s_windowMessages = 0;
            s_windowPeakMessages = 0;
        }
    }
}
