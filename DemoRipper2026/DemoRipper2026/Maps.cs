using System;
using System.Collections.Generic;
using System.Linq;

namespace DemoRipper2026
{
    /// <summary>
    /// Retail BO2 multiplayer maps: the internal id used in demo filenames, the
    /// game's number for it, and the name the game shows. Ids and numbers are the
    /// game's mp/mapstable.csv (columns 0 and 5, in its order); the number is what
    /// .demo.tags stores (Tags.cs). Display names are the game's own text for each
    /// map's MPUI_ key.
    /// </summary>
    public static class Maps
    {
        public sealed class Entry
        {
            public readonly string Id;        // "mp_dockside"
            public readonly int Number;       // mapstable.csv column 5 = .demo.tags key 2
            public readonly string Display;   // "Cargo"

            public Entry(string id, int number, string display)
            {
                Id = id;
                Number = number;
                Display = display;
            }

            /// <summary>How the map reads in a clip name: "cargo". Empty for LeaveOut.</summary>
            public string Token
            {
                get { return Id.Length == 0 ? "" : Display.ToLowerInvariant(); }
            }

            /// <summary>What the dropdown shows.</summary>
            public override string ToString()
            {
                return Display;
            }
        }

        /// <summary>Dropdown choice for clip names that don't include the map.</summary>
        public static readonly Entry LeaveOut = new Entry("", 0, "(not in name)");

        public static readonly Entry[] All =
        {
            new Entry("mp_la", 0, "Aftermath"),
            new Entry("mp_dockside", 1, "Cargo"),
            new Entry("mp_carrier", 2, "Carrier"),
            new Entry("mp_drone", 3, "Drone"),
            new Entry("mp_express", 4, "Express"),
            new Entry("mp_hijacked", 5, "Hijacked"),
            new Entry("mp_meltdown", 6, "Meltdown"),
            new Entry("mp_overflow", 7, "Overflow"),
            new Entry("mp_nightclub", 8, "Plaza"),
            new Entry("mp_raid", 9, "Raid"),
            new Entry("mp_slums", 10, "Slums"),
            new Entry("mp_village", 11, "Standoff"),
            new Entry("mp_turbine", 12, "Turbine"),
            new Entry("mp_socotra", 13, "Yemen"),
            new Entry("mp_nuketown_2020", 14, "Nuketown 2025"),
            new Entry("mp_downhill", 15, "Downhill"),
            new Entry("mp_mirage", 16, "Mirage"),
            new Entry("mp_hydro", 17, "Hydro"),
            new Entry("mp_skate", 18, "Grind"),
            new Entry("mp_concert", 19, "Encore"),
            new Entry("mp_magma", 20, "Magma"),
            new Entry("mp_vertigo", 21, "Vertigo"),
            new Entry("mp_studio", 22, "Studio"),
            new Entry("mp_uplink", 23, "Uplink"),
            new Entry("mp_bridge", 24, "Detour"),
            new Entry("mp_castaway", 25, "Cove"),
            new Entry("mp_paintball", 26, "Rush"),
            new Entry("mp_dig", 27, "Dig"),
            new Entry("mp_frostbite", 28, "Frost"),
            new Entry("mp_pod", 29, "Pod"),
            new Entry("mp_takeoff", 30, "Takeoff"),
        };

        public static IEnumerable<Entry> ByDisplayName()
        {
            return All.OrderBy(m => m.Display, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The map in a demo filename, gamemode_map_date_time:
        /// "tdm_mp_nuketown_2020_6_1_2026_20_15.demo". Ids can contain
        /// underscores themselves, so the longest id that fits wins. Null when
        /// nothing matches.
        /// </summary>
        public static Entry FromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            int us = fileName.IndexOf('_');
            if (us < 0) return null;

            string rest = fileName.Substring(us + 1);
            Entry best = null;
            foreach (Entry e in All)
            {
                if (!rest.StartsWith(e.Id + "_", StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || e.Id.Length > best.Id.Length) best = e;
            }
            return best;
        }
    }
}
