using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// One per-tick deterministic state record written to the replay timeline file.
    /// Dependency-free (no DXVision) so the comparison logic can be unit-tested offline.
    /// </summary>
    public struct ReplayTickRecord
    {
        public int Run;
        public int Tick;
        public ulong Hash;
        public int Gold, Wood, Stone, Iron, Oil, EntityCount;
        public long PosChecksum, LifeChecksum, ProjectileChecksum, ProductionChecksum, ReservationChecksum;

        public string ToJson()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"run\":").Append(Run).Append(',');
            sb.Append("\"tick\":").Append(Tick).Append(',');
            sb.Append("\"hash\":\"0x").Append(Hash.ToString("X16", CultureInfo.InvariantCulture)).Append("\",");
            sb.Append("\"gold\":").Append(Gold).Append(',');
            sb.Append("\"wood\":").Append(Wood).Append(',');
            sb.Append("\"stone\":").Append(Stone).Append(',');
            sb.Append("\"iron\":").Append(Iron).Append(',');
            sb.Append("\"oil\":").Append(Oil).Append(',');
            sb.Append("\"entities\":").Append(EntityCount).Append(',');
            sb.Append("\"pos\":").Append(PosChecksum).Append(',');
            sb.Append("\"life\":").Append(LifeChecksum).Append(',');
            sb.Append("\"proj\":").Append(ProjectileChecksum).Append(',');
            sb.Append("\"prod\":").Append(ProductionChecksum).Append(',');
            sb.Append("\"resv\":").Append(ReservationChecksum);
            sb.Append('}');
            return sb.ToString();
        }

        public static ReplayTickRecord FromJson(string line)
        {
            return new ReplayTickRecord
            {
                Run = (int)ExtractLong(line, "run"),
                Tick = (int)ExtractLong(line, "tick"),
                Hash = ParseHash(ExtractString(line, "hash")),
                Gold = (int)ExtractLong(line, "gold"),
                Wood = (int)ExtractLong(line, "wood"),
                Stone = (int)ExtractLong(line, "stone"),
                Iron = (int)ExtractLong(line, "iron"),
                Oil = (int)ExtractLong(line, "oil"),
                EntityCount = (int)ExtractLong(line, "entities"),
                PosChecksum = ExtractLong(line, "pos"),
                LifeChecksum = ExtractLong(line, "life"),
                ProjectileChecksum = ExtractLong(line, "proj"),
                ProductionChecksum = ExtractLong(line, "prod"),
                ReservationChecksum = ExtractLong(line, "resv")
            };
        }

        private static ulong ParseHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0UL;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v) ? v : 0UL;
        }

        private static long ExtractLong(string line, string key)
        {
            string raw = ExtractRaw(line, key);
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0L;
        }

        private static string ExtractString(string line, string key)
        {
            string raw = ExtractRaw(line, key);
            return raw.Trim('"');
        }

        private static string ExtractRaw(string line, string key)
        {
            if (string.IsNullOrEmpty(line)) return string.Empty;
            string token = "\"" + key + "\":";
            int idx = line.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return string.Empty;
            int start = idx + token.Length;
            int end = start;
            bool inQuotes = false;
            while (end < line.Length)
            {
                char c = line[end];
                if (c == '"') inQuotes = !inQuotes;
                else if (!inQuotes && (c == ',' || c == '}')) break;
                end++;
            }
            return line.Substring(start, end - start);
        }
    }

    public struct ReplayComparisonResult
    {
        public bool Pass;
        public int Run1Records;
        public int Run2Records;
        public int FirstDivergenceIndex;
        public int Run1TickAtDivergence;
        public int Run2TickAtDivergence;
        public string Detail;

        public override string ToString()
        {
            return Pass
                ? $"PASS ({Run1Records} ticks identical)"
                : $"FAIL at index {FirstDivergenceIndex} (run1 tick {Run1TickAtDivergence}, run2 tick {Run2TickAtDivergence}): {Detail}";
        }
    }

    public static class ReplayTimeline
    {
        public static List<ReplayTickRecord> ParseFile(string path)
        {
            var list = new List<ReplayTickRecord>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return list;
            foreach (string line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;
                list.Add(ReplayTickRecord.FromJson(line));
            }
            return list;
        }

        public static ReplayComparisonResult Compare(IReadOnlyList<ReplayTickRecord> a, IReadOnlyList<ReplayTickRecord> b)
        {
            var result = new ReplayComparisonResult
            {
                Run1Records = a?.Count ?? 0,
                Run2Records = b?.Count ?? 0,
                FirstDivergenceIndex = -1,
                Pass = true
            };

            if (a == null || b == null || a.Count == 0 || b.Count == 0)
            {
                result.Pass = false;
                result.Detail = "one or both timelines are empty";
                return result;
            }

            // Align by ABSOLUTE SimTick (both runs restore the save's tick, so equal tick == same
            // simulation point). This tolerates the two processes choosing slightly different
            // start ticks and only compares the overlapping window.
            var byTick = new Dictionary<int, ReplayTickRecord>();
            foreach (var r in b) byTick[r.Tick] = r;

            int compared = 0;
            foreach (var x in a)
            {
                if (!byTick.TryGetValue(x.Tick, out ReplayTickRecord y)) continue;
                compared++;
                string diff = DiffComponents(x, y);
                if (diff != null)
                {
                    result.Pass = false;
                    result.FirstDivergenceIndex = x.Tick;
                    result.Run1TickAtDivergence = x.Tick;
                    result.Run2TickAtDivergence = y.Tick;
                    result.Detail = diff;
                    return result;
                }
            }

            if (compared < 10)
            {
                result.Pass = false;
                result.Detail = $"insufficient tick overlap (only {compared} common ticks; run1={a.Count}, run2={b.Count})";
            }
            return result;
        }

        private static string DiffComponents(ReplayTickRecord x, ReplayTickRecord y)
        {
            if (x.Hash != y.Hash)
            {
                var parts = new List<string>();
                if (x.Gold != y.Gold || x.Wood != y.Wood || x.Stone != y.Stone || x.Iron != y.Iron || x.Oil != y.Oil)
                    parts.Add($"res({x.Gold},{x.Wood},{x.Stone},{x.Iron},{x.Oil}|{y.Gold},{y.Wood},{y.Stone},{y.Iron},{y.Oil})");
                if (x.EntityCount != y.EntityCount) parts.Add($"entities({x.EntityCount}|{y.EntityCount})");
                if (x.PosChecksum != y.PosChecksum) parts.Add("pos");
                if (x.LifeChecksum != y.LifeChecksum) parts.Add("life");
                if (x.ProjectileChecksum != y.ProjectileChecksum) parts.Add("proj");
                if (x.ProductionChecksum != y.ProductionChecksum) parts.Add("prod");
                if (x.ReservationChecksum != y.ReservationChecksum) parts.Add("resv");
                if (parts.Count == 0) parts.Add("hash");
                return $"hash 0x{x.Hash:X16} != 0x{y.Hash:X16} [{string.Join(",", parts)}]";
            }
            return null;
        }
    }
}
