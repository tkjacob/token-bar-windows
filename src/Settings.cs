using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TokenBar
{
    internal static class RuntimePaths
    {
        public static string BaseDirectory
        {
            get
            {
                string overridden = Environment.GetEnvironmentVariable("TOKENBAR_BASE_DIR");
                return string.IsNullOrEmpty(overridden)
                    ? AppDomain.CurrentDomain.BaseDirectory
                    : overridden;
            }
        }
    }

    // An account's credential folders are always derived from its id — never
    // stored separately — so there is nothing that can drift out of sync
    // with what is actually on disk. A refresh always re-checks these paths.
    internal static class AccountPaths
    {
        public static string Root()
        {
            return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "TokenBar", "accounts");
        }

        public static string ClaudeDir(string id)
        {
            return Path.Combine(Root(), id, "claude");
        }

        public static string CodexDir(string id)
        {
            return Path.Combine(Root(), id, "codex");
        }
    }

    internal sealed class AccountConfig
    {
        public string Id;
        public string Label;
    }

    internal sealed class AppSettings
    {
        public int ClaudeRefreshMinutes = 15;
        public bool ShowCodexFiveHour = false;
        public readonly List<AccountConfig> Accounts = new List<AccountConfig>();

        public static AppSettings Load()
        {
            string path = Path.Combine(RuntimePaths.BaseDirectory, "tokenbar.ini");
            return Load(path);
        }

        internal static AppSettings Load(string path)
        {
            AppSettings result = new AppSettings();
            if (!File.Exists(path)) return result;

            try
            {
                Dictionary<string, string> entries = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                        continue;
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = line.Substring(0, equals).Trim();
                    string rawValue = line.Substring(equals + 1).Trim();
                    if (key.Length == 0) continue;
                    entries[key] = rawValue;
                }

                string showCodexFiveHour;
                if (entries.TryGetValue("ShowCodexFiveHour", out showCodexFiveHour))
                {
                    bool show;
                    if (bool.TryParse(showCodexFiveHour, out show))
                        result.ShowCodexFiveHour = show;
                }

                string claudeRefreshMinutes;
                if (entries.TryGetValue("ClaudeRefreshMinutes", out claudeRefreshMinutes))
                {
                    int value;
                    if (int.TryParse(claudeRefreshMinutes, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out value))
                        result.ClaudeRefreshMinutes = Clamp(value, 5, 120);
                }

                string accounts;
                if (entries.TryGetValue("Accounts", out accounts))
                {
                    foreach (string rawId in accounts.Split(','))
                    {
                        string id = rawId.Trim();
                        if (id.Length == 0) continue;
                        string label;
                        entries.TryGetValue("Account." + id + ".Label", out label);
                        result.Accounts.Add(new AccountConfig
                        {
                            Id = id,
                            Label = string.IsNullOrEmpty(label) ? id : label
                        });
                    }
                }
            }
            catch
            {
                // A malformed or temporarily locked config should not stop the bar.
            }
            return result;
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        internal static string SlugifyAccountId(string label, ICollection<string> existingIds)
        {
            string basis = Regex.Replace(label ?? string.Empty, @"[^A-Za-z0-9]+", "-")
                .Trim('-').ToLowerInvariant();
            if (basis.Length == 0) basis = "account";

            string candidate = basis;
            int suffix = 2;
            while (existingIds.Contains(candidate))
            {
                candidate = basis + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }
            return candidate;
        }

        internal static void AddAccount(string path, string id, string label)
        {
            List<string> lines = File.Exists(path)
                ? new List<string>(File.ReadAllLines(path))
                : new List<string>();

            int accountsLine = -1;
            List<string> ids = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (!trimmed.StartsWith("Accounts", StringComparison.OrdinalIgnoreCase))
                    continue;
                int equals = trimmed.IndexOf('=');
                if (equals <= 0 ||
                    !trimmed.Substring(0, equals).Trim().Equals(
                        "Accounts", StringComparison.OrdinalIgnoreCase))
                    continue;
                accountsLine = i;
                foreach (string rawId in trimmed.Substring(equals + 1).Split(','))
                {
                    string existingId = rawId.Trim();
                    if (existingId.Length > 0) ids.Add(existingId);
                }
                break;
            }

            if (!ids.Contains(id)) ids.Add(id);
            string accountsValue = "Accounts=" + string.Join(",", ids.ToArray());
            if (accountsLine >= 0) lines[accountsLine] = accountsValue;
            else lines.Add(accountsValue);

            string labelKey = "Account." + id + ".Label";
            int labelLine = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                int equals = trimmed.IndexOf('=');
                if (equals > 0 && trimmed.Substring(0, equals).Trim().Equals(
                    labelKey, StringComparison.OrdinalIgnoreCase))
                {
                    labelLine = i;
                    break;
                }
            }
            string newLabelLine = labelKey + "=" + label;
            if (labelLine >= 0) lines[labelLine] = newLabelLine;
            else lines.Add(newLabelLine);

            File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
        }

        internal static void RemoveAccount(string path, string id)
        {
            if (!File.Exists(path)) return;
            List<string> lines = new List<string>(File.ReadAllLines(path));

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                int equals = trimmed.IndexOf('=');
                if (equals <= 0 ||
                    !trimmed.Substring(0, equals).Trim().Equals(
                        "Accounts", StringComparison.OrdinalIgnoreCase))
                    continue;

                List<string> ids = new List<string>();
                foreach (string rawId in trimmed.Substring(equals + 1).Split(','))
                {
                    string existingId = rawId.Trim();
                    if (existingId.Length > 0 && !existingId.Equals(id,
                        StringComparison.OrdinalIgnoreCase))
                        ids.Add(existingId);
                }
                lines[i] = "Accounts=" + string.Join(",", ids.ToArray());
                break;
            }

            string prefix = "Account." + id + ".";
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    lines.RemoveAt(i);
            }

            File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
        }
    }
}
