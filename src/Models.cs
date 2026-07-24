using System;
using System.Collections.Generic;

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
}

