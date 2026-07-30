using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TokenBar
{
    internal sealed class UpdateInfo
    {
        public string Version;
        public string ReleaseUrl;
    }

    internal sealed class UpdateCheckOutcome
    {
        public UpdateInfo Update;
        public bool ShouldNotify;
    }

    internal sealed class UpdateState
    {
        public DateTime LastCheckUtc = DateTime.MinValue;
        public string LatestVersion;
        public string ReleaseUrl;
        public string LastNotifiedVersion;

        public static UpdateState Load(string path)
        {
            UpdateState state = new UpdateState();
            if (!File.Exists(path)) return state;
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    int equals = raw.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = raw.Substring(0, equals).Trim();
                    string value = raw.Substring(equals + 1).Trim();
                    DateTime parsed;
                    if (key.Equals("LastCheckUtc", StringComparison.OrdinalIgnoreCase) &&
                        DateTime.TryParse(value, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out parsed))
                        state.LastCheckUtc = parsed.ToUniversalTime();
                    else if (key.Equals("LatestVersion",
                        StringComparison.OrdinalIgnoreCase))
                        state.LatestVersion = value;
                    else if (key.Equals("ReleaseUrl",
                        StringComparison.OrdinalIgnoreCase))
                        state.ReleaseUrl = value;
                    else if (key.Equals("LastNotifiedVersion",
                        StringComparison.OrdinalIgnoreCase))
                        state.LastNotifiedVersion = value;
                }
            }
            catch
            {
                return new UpdateState();
            }
            return state;
        }

        public void Save(string path)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllLines(path, new[]
                {
                    "LastCheckUtc=" + LastCheckUtc.ToUniversalTime().ToString("O"),
                    "LatestVersion=" + (LatestVersion ?? string.Empty),
                    "ReleaseUrl=" + (ReleaseUrl ?? string.Empty),
                    "LastNotifiedVersion=" + (LastNotifiedVersion ?? string.Empty)
                }, Encoding.UTF8);
            }
            catch
            {
                // Update metadata must never interrupt usage display.
            }
        }
    }

    internal static class AppVersion
    {
        public static string Read()
        {
            string baseDirectory = RuntimePaths.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDirectory, "VERSION"),
                Path.Combine(baseDirectory, "..", "VERSION")
            };
            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                        return File.ReadAllText(candidate).Trim();
                }
                catch { }
            }
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0" : version.ToString(3);
        }
    }

    internal static class UpdateChecker
    {
        internal const string LatestReleaseEndpoint =
            "https://api.github.com/repos/tkjacob/token-bar-windows/releases/latest";
        internal const int RequestTimeoutMilliseconds = 4000;
        internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

        public static UpdateCheckOutcome Check(string currentVersion, string statePath)
        {
            return Check(currentVersion, statePath, DateTime.UtcNow,
                DownloadLatestReleaseJson);
        }

        internal static UpdateCheckOutcome Check(string currentVersion, string statePath,
            DateTime nowUtc, Func<string> loader)
        {
            UpdateState state = UpdateState.Load(statePath);
            UpdateInfo cached = Evaluate(currentVersion, state.LatestVersion,
                state.ReleaseUrl);
            if (state.LastCheckUtc != DateTime.MinValue &&
                nowUtc >= state.LastCheckUtc &&
                nowUtc - state.LastCheckUtc < CheckInterval)
                return BuildOutcome(cached, state, statePath);

            state.LastCheckUtc = nowUtc;
            try
            {
                UpdateInfo latest = ParseLatest(currentVersion, loader());
                state.LatestVersion = latest == null ? string.Empty : latest.Version;
                state.ReleaseUrl = latest == null ? string.Empty : latest.ReleaseUrl;
                return BuildOutcome(latest, state, statePath);
            }
            catch
            {
                return BuildOutcome(cached, state, statePath);
            }
        }

        internal static void BeginCheck(Func<UpdateCheckOutcome> check,
            Action<UpdateCheckOutcome> completed)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateCheckOutcome result;
                try { result = check(); }
                catch { result = new UpdateCheckOutcome(); }
                if (completed != null) completed(result);
            });
        }

        internal static UpdateInfo ParseLatest(string currentVersion, string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> payload =
                serializer.Deserialize<Dictionary<string, object>>(json);
            if (payload == null || !payload.ContainsKey("tag_name") ||
                !payload.ContainsKey("html_url"))
                return null;
            return Evaluate(currentVersion,
                Convert.ToString(payload["tag_name"]),
                Convert.ToString(payload["html_url"]));
        }

        internal static UpdateInfo Evaluate(string currentVersion,
            string latestVersion, string releaseUrl)
        {
            Version current;
            Version latest;
            string normalizedLatest;
            if (!TryParseVersion(currentVersion, out current, out currentVersion) ||
                !TryParseVersion(latestVersion, out latest, out normalizedLatest) ||
                latest <= current || !IsAllowedReleaseUrl(releaseUrl))
                return null;
            return new UpdateInfo
            {
                Version = normalizedLatest,
                ReleaseUrl = releaseUrl
            };
        }

        internal static bool TryParseVersion(string value, out Version version,
            out string normalized)
        {
            version = null;
            normalized = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string clean = value.Trim();
            if (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(1);
            string[] parts = clean.Split('.');
            if (parts.Length < 2 || parts.Length > 4) return false;
            for (int index = 0; index < parts.Length; index++)
            {
                int number;
                if (parts[index].Length == 0 ||
                    !int.TryParse(parts[index], out number) || number < 0)
                    return false;
            }
            if (!Version.TryParse(clean, out version)) return false;
            normalized = clean;
            return true;
        }

        internal static bool IsAllowedReleaseUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                !uri.Scheme.Equals(Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                (!uri.IsDefaultPort && uri.Port != 443) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
                return false;
            string path = uri.AbsolutePath.TrimEnd('/');
            return path.Equals(
                    "/tkjacob/token-bar-windows/releases/latest",
                    StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(
                    "/tkjacob/token-bar-windows/releases/tag/",
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static HttpWebRequest CreateRequest()
        {
            HttpWebRequest request =
                (HttpWebRequest)WebRequest.Create(LatestReleaseEndpoint);
            request.Method = "GET";
            request.Accept = "application/vnd.github+json";
            request.UserAgent = "Token-Bar-for-Windows/" + AppVersion.Read();
            request.Timeout = RequestTimeoutMilliseconds;
            request.ReadWriteTimeout = RequestTimeoutMilliseconds;
            request.AllowAutoRedirect = false;
            request.Credentials = null;
            request.PreAuthenticate = false;
            return request;
        }

        internal static string DownloadLatestReleaseJson()
        {
            // GitHub requires TLS 1.2. Assemblies built with the inbox compiler can
            // otherwise inherit the legacy .NET 4.0 TLS default.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            HttpWebRequest request = CreateRequest();
            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                char[] buffer = new char[65537];
                int count = reader.ReadBlock(buffer, 0, buffer.Length);
                if (count > 65536)
                    throw new InvalidDataException("Release metadata is too large.");
                return new string(buffer, 0, count);
            }
        }

        private static UpdateCheckOutcome BuildOutcome(UpdateInfo update,
            UpdateState state, string statePath)
        {
            UpdateCheckOutcome outcome = new UpdateCheckOutcome { Update = update };
            if (update != null &&
                !string.Equals(state.LastNotifiedVersion, update.Version,
                    StringComparison.OrdinalIgnoreCase))
            {
                outcome.ShouldNotify = true;
                state.LastNotifiedVersion = update.Version;
            }
            state.Save(statePath);
            return outcome;
        }
    }
}
