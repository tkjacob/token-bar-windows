using System;
using System.Globalization;
using System.IO;

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

    internal sealed class AppSettings
    {
        public int ClaudeRefreshMinutes = 15;
        public bool ShowCodexFiveHour = false;

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
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                        continue;
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = line.Substring(0, equals).Trim();
                    string rawValue = line.Substring(equals + 1).Trim();

                    if (key.Equals("ShowCodexFiveHour",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        bool show;
                        if (bool.TryParse(rawValue, out show))
                            result.ShowCodexFiveHour = show;
                        continue;
                    }

                    int value;
                    if (!int.TryParse(rawValue, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out value))
                        continue;

                    if (key.Equals("ClaudeRefreshMinutes", StringComparison.OrdinalIgnoreCase))
                        result.ClaudeRefreshMinutes = Clamp(value, 5, 120);
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
    }
}
