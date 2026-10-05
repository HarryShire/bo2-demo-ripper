using System;

namespace DemoRipper2026
{
    /// <summary>Where the demo lives in retail t6mp, snapshotted from the UI thread.</summary>
    public struct DemoOffsets
    {
        public int DemoData, FileName, Thumb, Summary, SummaryLength;
    }

    /// <summary>
    /// Everything a clip folder needs from the game, read in one go and held in
    /// memory. Taken when Record is pressed, so the save at the end of a take
    /// doesn't depend on the game still having the demo loaded.
    /// </summary>
    public sealed class DemoCapture
    {
        private const int ThumbnailBytes = 255;
        private const int MaxDemoBytes = 256 * 1024 * 1024;
        private const int MaxSummaryBytes = 8 * 1024 * 1024;
        private const int ZeroRunWarn = 50;

        public string FileName = "";
        public string Mode = "";
        public int ModeNumber;          // 0 when the mode is not in the game's table
        public Maps.Entry Map;          // null when the file name matched no known map
        public byte[] Demo, Summary, Thumbnail;

        /// <summary>The .demo filename to write.</summary>
        public string DemoFile
        {
            get
            {
                string f = FileName.Length > 0 ? FileName : "clip.demo";
                return f.EndsWith(".demo", StringComparison.OrdinalIgnoreCase) ? f : f + ".demo";
            }
        }

        /// <summary>
        /// Reads the loaded demo from memory. Returns null with a message when
        /// there's nothing usable — including a demo that's still downloading,
        /// which would otherwise save as a file full of zeros.
        /// </summary>
        public static DemoCapture Read(DemoOffsets o, Action<int> progress, out string error)
        {
            error = null;

            int dataPtr = Mem.ReadInt(Mem.At(o.DemoData));
            int dataSize = Mem.ReadInt(Mem.At(o.DemoData + 4));
            if (dataPtr <= 0x10000 || dataSize <= 0 || dataSize >= MaxDemoBytes)
            {
                error = "No demo loaded in retail BO2, or the offsets have moved.";
                return null;
            }

            int sample = Math.Min(dataSize, 4 * 1024 * 1024);
            int zeros = Mem.LongestZeroRun(Mem.ReadBytes((IntPtr)dataPtr, sample));
            if (zeros >= ZeroRunWarn)
            {
                error = "The demo is still downloading (" + zeros + " zero bytes in a row). Wait, then try again.";
                return null;
            }

            var cap = new DemoCapture();
            cap.FileName = Mem.ReadAscii(Mem.At(o.FileName), 64);

            int read;
            byte[] demo = Mem.ReadLarge((IntPtr)dataPtr, dataSize, progress, out read);
            if (read < dataSize)
            {
                byte[] trimmed = new byte[read];
                Buffer.BlockCopy(demo, 0, trimmed, 0, read);
                demo = trimmed;
            }
            cap.Demo = demo;

            // The summary is still intact during playback, so capturing after the
            // replay has started is fine.
            int slen = Mem.ReadInt(Mem.At(o.SummaryLength));
            if (slen < 0 || slen > MaxSummaryBytes) slen = 0;
            cap.Summary = slen > 0 ? Mem.ReadBytes(Mem.At(o.Summary), slen) : new byte[0];
            cap.Thumbnail = Mem.ReadBytes(Mem.At(o.Thumb), ThumbnailBytes);

            cap.Map = Maps.FromFileName(cap.FileName);
            int us = cap.FileName.IndexOf('_');
            cap.Mode = us > 0 ? cap.FileName.Substring(0, us) : "";
            Tags.TryGameMode(cap.Mode, out cap.ModeNumber);
            return cap;
        }
    }
}
