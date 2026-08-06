using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TokenBar
{
    internal static class CodexUsageReader
    {
        private const int MaxFiles = 80;
        private const int TailBytes = 131072;

        public static UsageSnapshot Read(ProviderUsage claude)
        {
            return Read(claude, null);
        }

        public static UsageSnapshot Read(ProviderUsage claude, string codexHome)
        {
            UsageSnapshot snapshot = new UsageSnapshot();
            snapshot.Claude = claude ?? new ProviderUsage { Name = "Claude" };

            string home = string.IsNullOrEmpty(codexHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex")
                : codexHome;
            string root = Path.Combine(home, "sessions");
            if (!Directory.Exists(root))
            {
                snapshot.Codex.Error = "Codex 세션 폴더를 찾을 수 없습니다.";
                return snapshot;
            }

            try
            {
                FileInfo[] files = SelectNewestFiles(root, MaxFiles);

                Dictionary<string, ProviderUsage> byLimit =
                    new Dictionary<string, ProviderUsage>(StringComparer.OrdinalIgnoreCase);

                foreach (FileInfo file in files)
                {
                    foreach (string line in ReadTailLinesReverse(file.FullName))
                    {
                        if (line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0)
                            continue;

                        ProviderUsage parsed = ParseLine(line);
                        if (parsed == null || parsed.Buckets.Count == 0) continue;
                        string key = string.IsNullOrEmpty(parsed.LimitId) ? "codex" : parsed.LimitId;
                        if (!byLimit.ContainsKey(key))
                            byLimit.Add(key, parsed);
                        break;
                    }
                }

                ProviderUsage main;
                if (!byLimit.TryGetValue("codex", out main))
                {
                    main = byLimit.Values
                        .Where(delegate(ProviderUsage item)
                        {
                            return item.LimitId == null ||
                                item.LimitId.IndexOf("spark", StringComparison.OrdinalIgnoreCase) < 0;
                        })
                        .OrderByDescending(delegate(ProviderUsage item)
                        {
                            return item.CollectedAt ?? DateTime.MinValue;
                        })
                        .FirstOrDefault();
                }
                if (main == null)
                    main = byLimit.Values.OrderByDescending(delegate(ProviderUsage item)
                    {
                        return item.CollectedAt ?? DateTime.MinValue;
                    }).FirstOrDefault();

                if (main == null)
                {
                    snapshot.Codex.Error = "Codex 사용량 기록이 아직 없습니다.";
                    return snapshot;
                }

                snapshot.Codex = main;
                foreach (ProviderUsage item in byLimit.Values
                    .Where(delegate(ProviderUsage item) { return !object.ReferenceEquals(item, main); })
                    .OrderBy(delegate(ProviderUsage item) { return item.LimitId; }))
                {
                    snapshot.OtherCodexLimits.Add(item);
                }
            }
            catch (Exception ex)
            {
                snapshot.Codex.Error = "Codex 로그 읽기 실패: " + ex.Message;
            }
            return snapshot;
        }

        internal static FileInfo[] SelectNewestFiles(string root, int maxFiles)
        {
            if (maxFiles <= 0) return new FileInfo[0];

            List<FileInfo> newest = new List<FileInfo>(maxFiles + 1);
            FileInfoComparer comparer = new FileInfoComparer();
            foreach (FileInfo file in new DirectoryInfo(root)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
            {
                int index = newest.BinarySearch(file, comparer);
                if (index < 0) index = ~index;
                newest.Insert(index, file);
                if (newest.Count > maxFiles)
                    newest.RemoveAt(0);
            }
            newest.Reverse();
            return newest.ToArray();
        }

        private sealed class FileInfoComparer : IComparer<FileInfo>
        {
            public int Compare(FileInfo left, FileInfo right)
            {
                int byTime = left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc);
                if (byTime != 0) return byTime;
                return string.Compare(left.FullName, right.FullName,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        private static IEnumerable<string> ReadTailLinesReverse(string path)
        {
            byte[] bytes;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, stream.Length - TailBytes);
                stream.Seek(start, SeekOrigin.Begin);
                bytes = new byte[stream.Length - start];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
            }
            string text = Encoding.UTF8.GetString(bytes);
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
                yield return lines[i];
        }

        private static ProviderUsage ParseLine(string line)
        {
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = Math.Max(line.Length * 2, 1024 * 1024);
                object root = serializer.DeserializeObject(line);
                IDictionary<string, object> rootMap = root as IDictionary<string, object>;
                if (rootMap == null) return null;

                IDictionary<string, object> rateLimits = FindMap(rootMap, "rate_limits", 0);
                if (rateLimits == null) return null;

                ProviderUsage result = new ProviderUsage { Name = "Codex" };
                result.LimitId = GetString(rateLimits, "limit_id");
                result.CollectedAt = ParseTimestamp(GetString(rootMap, "timestamp"));

                AddBucket(result, GetMap(rateLimits, "primary"), "기본");
                AddBucket(result, GetMap(rateLimits, "secondary"), "보조");
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static void AddBucket(ProviderUsage target, IDictionary<string, object> map, string fallbackLabel)
        {
            if (map == null) return;
            double? used = GetDouble(map, "used_percent");
            int? window = GetInt(map, "window_minutes");
            long? resetEpoch = GetLong(map, "resets_at");
            if (!used.HasValue && !resetEpoch.HasValue) return;

            DateTime? reset = null;
            if (resetEpoch.HasValue)
                reset = EpochToLocal(resetEpoch.Value);

            bool expired = reset.HasValue && reset.Value <= DateTime.Now;
            if (expired && window.HasValue && window.Value > 0)
            {
                while (reset.Value <= DateTime.Now)
                    reset = reset.Value.AddMinutes(window.Value);
            }

            target.Buckets.Add(new UsageBucket
            {
                Label = WindowLabel(window, fallbackLabel),
                UsedPercent = used,
                WindowMinutes = window,
                ResetsAt = reset,
                ResetEstimated = expired
            });
        }

        private static string WindowLabel(int? minutes, string fallback)
        {
            if (!minutes.HasValue) return fallback;
            if (minutes.Value == 300) return "5시간";
            if (minutes.Value == 1440) return "1일";
            if (minutes.Value == 10080) return "주간";
            if (minutes.Value % 1440 == 0)
                return (minutes.Value / 1440).ToString(CultureInfo.InvariantCulture) + "일";
            if (minutes.Value % 60 == 0)
                return (minutes.Value / 60).ToString(CultureInfo.InvariantCulture) + "시간";
            return minutes.Value.ToString(CultureInfo.InvariantCulture) + "분";
        }

        private static IDictionary<string, object> FindMap(
            IDictionary<string, object> map, string key, int depth)
        {
            if (depth > 4) return null;
            object direct;
            if (map.TryGetValue(key, out direct))
                return direct as IDictionary<string, object>;

            foreach (object value in map.Values)
            {
                IDictionary<string, object> nested = value as IDictionary<string, object>;
                if (nested == null) continue;
                IDictionary<string, object> found = FindMap(nested, key, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        private static IDictionary<string, object> GetMap(IDictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value)
                ? value as IDictionary<string, object>
                : null;
        }

        private static string GetString(IDictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : null;
        }

        private static double? GetDouble(IDictionary<string, object> map, string key)
        {
            object value;
            double parsed;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return null;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : (double?)null;
        }

        private static int? GetInt(IDictionary<string, object> map, string key)
        {
            double? value = GetDouble(map, key);
            return value.HasValue ? Convert.ToInt32(value.Value) : (int?)null;
        }

        private static long? GetLong(IDictionary<string, object> map, string key)
        {
            object value;
            long parsed;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return null;
            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : (long?)null;
        }

        private static DateTime? ParseTimestamp(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)
                ? parsed.ToLocalTime()
                : (DateTime?)null;
        }

        private static DateTime EpochToLocal(long epoch)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddSeconds(epoch).ToLocalTime();
        }
    }
}
