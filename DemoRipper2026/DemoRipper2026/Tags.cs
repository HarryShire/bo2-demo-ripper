using System;
using System.Collections.Generic;
using System.IO;

namespace DemoRipper2026
{
    /// <summary>
    /// The .demo.tags file the theater reads beside a demo: a mark (8) and an int32 count of key/value
    /// pairs, then the pairs, each written as two records of 0x0A followed by an int64 (the key, then the value).
    ///
    /// Key 2 is the map's number in the game's mp/mapstable.csv (column 5, kept in Maps.cs) and key 1 is the
    /// mode's number in mp/gametypestable.csv (column 5, below). The other pairs carry the same fixed values
    /// for every demo.
    /// </summary>
    public static class Tags
    {
        private const byte CountMark = 8;
        private const byte Record = 0x0A;
        private const long ModeKey = 1;
        private const long MapKey = 2;

        // key, value, in the order they are written; the map and the mode are filled in per demo
        private static readonly long[][] Pairs =
        {
            new long[] { 3, 1 },
            new long[] { MapKey, 0 },
            new long[] { ModeKey, 0 },
            new long[] { 13, 0 },
            new long[] { 8, unchecked((long)0xFFFFFFFFFFFFFF01) },
            new long[] { 6, 1 },
            new long[] { 5, 1 },
        };

        // mp/gametypestable.csv: each mode id in a demo's filename and the game's number for it (column 5),
        // in the table's own order.
        private static readonly Dictionary<string, int> Modes =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "tdm", 1 }, { "dm", 2 }, { "sd", 9 }, { "dom", 3 }, { "koth", 6 }, { "hq", 7 }, { "dem", 4 },
            { "ctf", 8 }, { "conf", 5 }, { "gun", 10 }, { "oic", 11 }, { "shrp", 12 }, { "sas", 13 },
            { "oneflag", 14 },
        };

        public static bool TryGameMode(string id, out int value)
        {
            value = 0;
            return id != null && Modes.TryGetValue(id, out value);
        }

        /// <summary>A .tags for a demo of this map and mode, as the game's tables number them.</summary>
        public static byte[] Build(int map, int mode)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(CountMark);
                w.Write(Pairs.Length);
                foreach (long[] kv in Pairs)
                {
                    long value = kv[0] == MapKey ? map : kv[0] == ModeKey ? mode : kv[1];
                    w.Write(Record);
                    w.Write(kv[0]);
                    w.Write(Record);
                    w.Write(value);
                }
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
