using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace TokenBar
{
    internal sealed class UsageBucket
    {
        public string Label;
        public double? UsedPercent;
        public DateTime? ResetsAt;
        public int? WindowMinutes;
        public bool ResetEstimated;

        public double? RemainingPercent
        {
            get
            {
                if (ResetEstimated) return 100.0;
                if (!UsedPercent.HasValue) return null;
                return Math.Max(0.0, Math.Min(100.0, 100.0 - UsedPercent.Value));
            }
        }
    }

    internal sealed class ProviderUsage
    {
        public string Name;
        public string LimitId;
        public DateTime? CollectedAt;
        public string Error;
        public readonly List<UsageBucket> Buckets = new List<UsageBucket>();

        public double? RemainingPercent
        {
            get
            {
                double? result = null;
                foreach (UsageBucket bucket in Buckets)
                {
                    double? remaining = bucket.RemainingPercent;
                    if (!remaining.HasValue) continue;
                    if (!result.HasValue || remaining.Value < result.Value)
                        result = remaining.Value;
                }
                return result;
            }
        }
    }

    internal sealed class UsageSnapshot
    {
        public ProviderUsage Codex = new ProviderUsage { Name = "Codex" };
        public ProviderUsage Claude = new ProviderUsage { Name = "Claude" };
        public readonly List<ProviderUsage> OtherCodexLimits = new List<ProviderUsage>();
    }

    internal sealed class AccountSnapshot
    {
        public string Label;
        public UsageSnapshot Snapshot = new UsageSnapshot();

        // Whether a credential file exists on disk for this provider, checked
        // fresh on every refresh from AccountPaths — independent of whether
        // that refresh's data fetch happened to succeed. This is what decides
        // "show the card" vs. "show a connect button", so a transient fetch
        // error never flips a genuinely connected account back to disconnected.
        public bool ClaudeConnected;
        public bool CodexConnected;
    }

    // Persists the last successfully collected snapshot per account to disk,
    // so a restart (or a stretch where every fetch fails) still shows the
    // last known values and their real age instead of going blank.
    //
    // DateTimes are hand-converted to/from tick counts rather than handed to
    // JavaScriptSerializer directly — its built-in "\/Date(ms)\/" converter
    // round-trips through UTC and silently shifts every local wall-clock
    // DateTime (Kind=Unspecified, as used everywhere in this codebase) by
    // the machine's UTC offset.
    internal static class UsageCache
    {
        public static void Save(string path, UsageSnapshot snapshot)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["Claude"] = ProviderToDictionary(snapshot.Claude);
                root["Codex"] = ProviderToDictionary(snapshot.Codex);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                File.WriteAllText(path, serializer.Serialize(root));
            }
            catch { }
        }

        public static UsageSnapshot Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                object raw = serializer.DeserializeObject(File.ReadAllText(path));
                IDictionary<string, object> root = raw as IDictionary<string, object>;
                if (root == null) return null;

                UsageSnapshot snapshot = new UsageSnapshot();
                snapshot.Claude = ProviderFromDictionary(GetDict(root, "Claude"), "Claude");
                snapshot.Codex = ProviderFromDictionary(GetDict(root, "Codex"), "Codex");
                return snapshot;
            }
            catch { return null; }
        }

        private static Dictionary<string, object> ProviderToDictionary(ProviderUsage provider)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            map["Name"] = provider.Name;
            map["CollectedAt"] = provider.CollectedAt.HasValue
                ? (object)provider.CollectedAt.Value.Ticks : null;
            List<object> buckets = new List<object>();
            foreach (UsageBucket bucket in provider.Buckets)
            {
                Dictionary<string, object> bucketMap = new Dictionary<string, object>();
                bucketMap["Label"] = bucket.Label;
                bucketMap["UsedPercent"] = bucket.UsedPercent.HasValue
                    ? (object)bucket.UsedPercent.Value : null;
                bucketMap["ResetsAt"] = bucket.ResetsAt.HasValue
                    ? (object)bucket.ResetsAt.Value.Ticks : null;
                bucketMap["WindowMinutes"] = bucket.WindowMinutes.HasValue
                    ? (object)bucket.WindowMinutes.Value : null;
                bucketMap["ResetEstimated"] = bucket.ResetEstimated;
                buckets.Add(bucketMap);
            }
            map["Buckets"] = buckets;
            return map;
        }

        private static ProviderUsage ProviderFromDictionary(
            IDictionary<string, object> map, string fallbackName)
        {
            ProviderUsage provider = new ProviderUsage { Name = fallbackName };
            if (map == null) return provider;

            object name;
            if (map.TryGetValue("Name", out name) && name != null)
                provider.Name = Convert.ToString(name);
            provider.CollectedAt = GetTicksAsDateTime(map, "CollectedAt");

            object rawBuckets;
            // JavaScriptSerializer.DeserializeObject returns JSON arrays as
            // ArrayList, not List<object> — cast to the non-generic
            // IEnumerable so both that and List<object> work.
            System.Collections.IEnumerable buckets = map.TryGetValue("Buckets", out rawBuckets)
                ? rawBuckets as System.Collections.IEnumerable : null;
            if (buckets == null) return provider;

            foreach (object item in buckets)
            {
                IDictionary<string, object> bucketMap = item as IDictionary<string, object>;
                if (bucketMap == null) continue;
                object label, used, window, estimated;
                bucketMap.TryGetValue("Label", out label);
                bucketMap.TryGetValue("UsedPercent", out used);
                bucketMap.TryGetValue("WindowMinutes", out window);
                bucketMap.TryGetValue("ResetEstimated", out estimated);
                provider.Buckets.Add(new UsageBucket
                {
                    Label = label == null ? null : Convert.ToString(label),
                    UsedPercent = used == null ? (double?)null : Convert.ToDouble(used),
                    ResetsAt = GetTicksAsDateTime(bucketMap, "ResetsAt"),
                    WindowMinutes = window == null ? (int?)null : Convert.ToInt32(window),
                    ResetEstimated = estimated != null && Convert.ToBoolean(estimated)
                });
            }
            return provider;
        }

        private static DateTime? GetTicksAsDateTime(IDictionary<string, object> map, string key)
        {
            object value;
            if (!map.TryGetValue(key, out value) || value == null) return null;
            return new DateTime(Convert.ToInt64(value));
        }

        private static IDictionary<string, object> GetDict(
            IDictionary<string, object> map, string key)
        {
            object value;
            return map.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
        }
    }
}

