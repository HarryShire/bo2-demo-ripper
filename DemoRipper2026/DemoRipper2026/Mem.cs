using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace DemoRipper2026
{
    /// <summary>
    /// Read-only access to the retail BO2 process. Self-contained on purpose:
    /// it opens its own handle, so it runs beside any other tool attached to a client.
    /// </summary>
    public static class Mem
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private static IntPtr _handle = IntPtr.Zero;
        private static int _pid = -1;

        public static string AttachedName = "";
        public static IntPtr ModuleBase = IntPtr.Zero;
        public static int ModuleSize;
        public static string ModulePath = "";
        public static int ProcessId = -1;
        public static int OpenError;
        public static string AttachNote = "";

        // ---------- attach ----------

        public static bool Attach(string processName)
        {
            if (string.IsNullOrEmpty(processName)) processName = "t6mp";

            Process[] ps = Process.GetProcessesByName(processName);
            if (ps.Length == 0)
            {
                Detach();
                AttachedName = "";
                ModuleBase = IntPtr.Zero;
                return false;
            }

            Process target = ps[0];
            if (_handle != IntPtr.Zero && _pid == target.Id) return true;

            Detach();
            _handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, target.Id);
            OpenError = _handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            _pid = target.Id;
            ProcessId = target.Id;
            AttachedName = target.ProcessName;
            AttachNote = "";

            try
            {
                ModuleBase = target.MainModule.BaseAddress;
                ModuleSize = target.MainModule.ModuleMemorySize;
                ModulePath = target.MainModule.FileName;
            }
            catch (Exception ex)
            {
                ModuleBase = IntPtr.Zero;
                ModuleSize = 0;
                ModulePath = "";
                AttachNote = ex.Message;
            }

            return _handle != IntPtr.Zero && ModuleBase != IntPtr.Zero;
        }

        private static void Detach()
        {
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
            _pid = -1;
        }

        public static IntPtr At(int offset) { return IntPtr.Add(ModuleBase, offset); }

        // ---------- reads ----------

        public static byte[] ReadBytes(IntPtr address, int count)
        {
            byte[] buf = new byte[count > 0 ? count : 0];
            if (_handle == IntPtr.Zero || count <= 0) return buf;

            int got;
            ReadProcessMemory(_handle, address, buf, count, out got);
            return buf;
        }

        /// <summary>Page-by-page, so one unreadable page doesn't lose the range.</summary>
        public static byte[] ReadWindow(long start, long length)
        {
            if (length <= 0) return new byte[0];
            if (start < 0x10000) start = 0x10000;

            byte[] buf = new byte[length];
            if (_handle == IntPtr.Zero) return buf;

            for (long p = start; p < start + length; p += 0x1000)
            {
                byte[] page = new byte[0x1000];
                int r;
                if (ReadProcessMemory(_handle, (IntPtr)p, page, 0x1000, out r) && r > 0)
                {
                    int dest = (int)(p - start);
                    int len = Math.Min(r, (int)length - dest);
                    if (len > 0) Buffer.BlockCopy(page, 0, buf, dest, len);
                }
            }
            return buf;
        }

        /// <summary>Chunked read for the demo blob, which runs to megabytes.</summary>
        public static byte[] ReadLarge(IntPtr address, int count, Action<int> progress, out int read)
        {
            byte[] outBuf = new byte[count > 0 ? count : 0];
            read = 0;
            if (_handle == IntPtr.Zero || count <= 0) return outBuf;

            const int Chunk = 1024 * 1024;
            byte[] tmp = new byte[Chunk];
            long baseAddr = address.ToInt64();

            while (read < count)
            {
                int want = Math.Min(Chunk, count - read);
                int got;
                if (!ReadProcessMemory(_handle, (IntPtr)(baseAddr + read), tmp, want, out got) || got <= 0)
                    break;

                Buffer.BlockCopy(tmp, 0, outBuf, read, got);
                read += got;
                if (progress != null) progress((int)((long)read * 100 / count));
            }
            return outBuf;
        }

        public static int ReadInt(IntPtr address)
        {
            return BitConverter.ToInt32(ReadBytes(address, 4), 0);
        }

        public static string ReadAscii(IntPtr address, int max)
        {
            byte[] buf = ReadBytes(address, max);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i] == 0) break;
                if (buf[i] < 32 || buf[i] >= 127) break;
                sb.Append((char)buf[i]);
            }
            return sb.ToString();
        }

        public static int LongestZeroRun(byte[] data)
        {
            int best = 0, run = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == 0) { run++; if (run > best) best = run; }
                else run = 0;
            }
            return best;
        }

        // ---------- diagnostics ----------

        public static bool TargetIs32Bit(out bool known)
        {
            known = false;
            if (_handle == IntPtr.Zero) return false;

            bool wow64;
            if (!IsWow64Process(_handle, out wow64)) return false;
            known = true;
            return wow64;
        }

        public class PeInfo
        {
            public bool Valid;
            public ushort Machine;
            public uint TimeDateStamp;
            public string MachineName;
            public DateTime BuildUtc;
        }

        /// <summary>
        /// The PE header is the build's identity. When the timestamp changes,
        /// the offsets have moved and it's time to run Relocate.
        /// </summary>
        public static PeInfo ReadPeHeader()
        {
            PeInfo info = new PeInfo();
            if (_handle == IntPtr.Zero || ModuleBase == IntPtr.Zero) return info;

            byte[] dos = ReadBytes(ModuleBase, 0x40);
            if (dos.Length < 0x40 || dos[0] != 0x4D || dos[1] != 0x5A) return info;

            int lfanew = BitConverter.ToInt32(dos, 0x3C);
            if (lfanew <= 0 || lfanew > 0x1000) return info;

            byte[] pe = ReadBytes(IntPtr.Add(ModuleBase, lfanew), 24);
            if (pe.Length < 24 || pe[0] != 0x50 || pe[1] != 0x45) return info;

            info.Machine = BitConverter.ToUInt16(pe, 4);
            info.TimeDateStamp = BitConverter.ToUInt32(pe, 8);
            info.MachineName = info.Machine == 0x14C ? "x86 (32-bit)"
                             : info.Machine == 0x8664 ? "x64 (64-bit)"
                             : "0x" + info.Machine.ToString("X4");
            info.BuildUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                            .AddSeconds(info.TimeDateStamp);
            info.Valid = true;
            return info;
        }

        public static string HexDump(IntPtr address, int count)
        {
            byte[] b = ReadBytes(address, count);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("X2") + " ");
            return sb.ToString().Trim();
        }

        // ---------- scanning ----------

        private static bool Readable(uint protect)
        {
            if ((protect & 0x100) != 0) return false;
            if ((protect & 0x01) != 0) return false;
            uint p = protect & 0xFF;
            return p == 0x02 || p == 0x04 || p == 0x08
                || p == 0x20 || p == 0x40 || p == 0x80;
        }

        /// <summary>
        /// Byte-signature search. skipExecutable drops code pages, which would
        /// otherwise flood results with instruction bytes that look like data.
        /// </summary>
        public static List<IntPtr> Scan(byte[] needle, bool moduleOnly, bool skipExecutable,
                                          int maxHits, Action<int> progress)
        {
            var hits = new List<IntPtr>();
            if (_handle == IntPtr.Zero || needle == null || needle.Length == 0) return hits;

            long low = moduleOnly ? ModuleBase.ToInt64() : 0x10000;
            long high = moduleOnly ? ModuleBase.ToInt64() + ModuleSize : 0x7FFF0000;
            if (high <= low) return hits;

            const int Chunk = 1024 * 1024;
            byte[] buffer = new byte[Chunk];
            int mbiSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
            long addr = low;

            while (addr < high && hits.Count < maxHits)
            {
                MEMORY_BASIC_INFORMATION mbi;
                if (VirtualQueryEx(_handle, (IntPtr)addr, out mbi, (uint)mbiSize) == 0)
                { addr += 0x1000; continue; }

                long rBase = mbi.BaseAddress.ToInt64();
                long rSize = mbi.RegionSize.ToInt64();
                if (rSize <= 0) { addr += 0x1000; continue; }

                bool exec = (mbi.Protect & 0xF0) != 0;
                if (mbi.State == 0x1000 && Readable(mbi.Protect) && !(skipExecutable && exec))
                {
                    long start = Math.Max(rBase, low);
                    long end = Math.Min(rBase + rSize, high);
                    long off = start;

                    while (off < end && hits.Count < maxHits)
                    {
                        int want = (int)Math.Min(Chunk, end - off);
                        int got;
                        if (ReadProcessMemory(_handle, (IntPtr)off, buffer, want, out got)
                            && got >= needle.Length)
                        {
                            int limit = got - needle.Length;
                            for (int i = 0; i <= limit; i++)
                            {
                                if (buffer[i] != needle[0]) continue;
                                bool ok = true;
                                for (int j = 1; j < needle.Length; j++)
                                    if (buffer[i + j] != needle[j]) { ok = false; break; }
                                if (ok) { hits.Add((IntPtr)(off + i)); if (hits.Count >= maxHits) break; }
                            }
                        }
                        off += Math.Max(1, want - needle.Length);
                    }
                }

                addr = rBase + rSize;
                if (progress != null)
                    progress((int)Math.Max(0, Math.Min(100, (addr - low) * 100 / (high - low))));
            }

            if (progress != null) progress(100);
            return hits;
        }

        // ---------- deflate ----------

        /// <summary>
        /// A .summary is a raw DEFLATE stream with no zlib or gzip wrapper.
        /// Returns bytes produced, or 0 when the data isn't a deflate stream.
        /// </summary>
        public static int TryInflate(byte[] data)
        {
            try
            {
                using (var src = new MemoryStream(data))
                using (var ds = new DeflateStream(src, CompressionMode.Decompress))
                {
                    byte[] chunk = new byte[8192];
                    int total = 0, got;
                    while ((got = ds.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        total += got;
                        if (total > 1 << 20) break;
                    }
                    return total;
                }
            }
            catch { return 0; }
        }

        /// <summary>
        /// Walks a module range attempting an inflate at every 4-byte boundary.
        /// Random bytes fail on the first Huffman symbol, so it's far cheaper than
        /// it looks. Relocate falls back on it when no .summary matched.
        /// </summary>
        public static List<KeyValuePair<int, int>> HuntDeflate(int from, int to, int minOutput,
                                                              Action<int> progress)
        {
            var found = new List<KeyValuePair<int, int>>();
            if (_handle == IntPtr.Zero || ModuleBase == IntPtr.Zero) return found;

            from &= ~3;                 // start on a multiple of 4, so every address tried is one
            if (from < 0) from = 0;
            if (to > ModuleSize) to = ModuleSize;
            if (to <= from) return found;

            const int Block = 262144;
            byte[] win = null;
            int winStart = -1;

            for (int off = from; off + 64 < to; off += 4)
            {
                if (win == null || off - winStart >= Block - 32768)
                {
                    winStart = off;
                    win = ReadWindow(ModuleBase.ToInt64() + winStart, Block);
                    if (progress != null)
                        progress((int)((long)(off - from) * 100 / (to - from)));
                }

                int i = off - winStart;
                if (i < 0 || i + 64 >= win.Length) continue;

                int low3 = win[i] & 0x07;
                if (low3 != 2 && low3 != 3 && low3 != 4 && low3 != 5) continue;
                if (win[i + 1] == 0 && win[i + 2] == 0 && win[i + 3] == 0) continue;

                int avail = Math.Min(32768, win.Length - i);
                byte[] probe = new byte[avail];
                Buffer.BlockCopy(win, i, probe, 0, avail);

                int outLen = TryInflate(probe);
                if (outLen >= minOutput)
                {
                    found.Add(new KeyValuePair<int, int>(off, outLen));
                    if (found.Count >= 20) break;
                }
            }

            if (progress != null) progress(100);
            return found;
        }
    }
}
