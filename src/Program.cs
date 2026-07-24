using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TokenBar
{
    internal static class Program
    {
        private static Mutex singleInstance;

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--collect", StringComparison.OrdinalIgnoreCase))
            {
                RunCollectionMode();
                return;
            }
            if (args.Length > 1 &&
                args[0].Equals("--collect-to", StringComparison.OrdinalIgnoreCase))
            {
                RunCollectionToFile(args[1]);
                return;
            }
            bool created;
            singleInstance = new Mutex(true, @"Local\TokenBar.SingleInstance", out created);
            if (!created) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TokenBarForm(AppSettings.Load()));
            GC.KeepAlive(singleInstance);
        }

        private static void RunCollectionMode()
        {
            // Attach to the invoking console even though the normal app is a windowed executable.
            NativeConsole.AttachParent();
            ProviderUsage claude = ClaudeUsageCollector.Collect();
            UsageSnapshot snapshot = CodexUsageReader.Read(claude);
            Console.WriteLine("{");
            WriteProvider("codex", snapshot.Codex, true);
            WriteProvider("claude", snapshot.Claude, false);
            Console.WriteLine("}");
        }

        private static void RunCollectionToFile(string path)
        {
            ProviderUsage claude = ClaudeUsageCollector.Collect();
            UsageSnapshot snapshot = CodexUsageReader.Read(claude);
            StringBuilder json = new StringBuilder();
            json.AppendLine("{");
            AppendProvider(json, "codex", snapshot.Codex, true);
            AppendProvider(json, "claude", snapshot.Claude, false);
            json.AppendLine("}");
            File.WriteAllText(path, json.ToString(), Encoding.UTF8);
        }

        private static void AppendProvider(StringBuilder json, string key,
            ProviderUsage provider, bool comma)
        {
            string remaining = provider.RemainingPercent.HasValue
                ? provider.RemainingPercent.Value.ToString("0.##", CultureInfo.InvariantCulture)
                : "null";
            json.Append("  \"").Append(key).Append("\": { \"remaining_percent\": ")
                .Append(remaining).Append(", \"error\": ");
            if (string.IsNullOrEmpty(provider.Error)) json.Append("null");
            else json.Append("\"").Append(Escape(provider.Error)).Append("\"");
            json.Append(" }").AppendLine(comma ? "," : "");
        }

        private static void WriteProvider(string key, ProviderUsage provider, bool comma)
        {
            string remaining = provider.RemainingPercent.HasValue
                ? provider.RemainingPercent.Value.ToString("0.##", CultureInfo.InvariantCulture)
                : "null";
            Console.Write("  \"{0}\": {{ \"remaining_percent\": {1}, \"error\": ",
                key, remaining);
            if (string.IsNullOrEmpty(provider.Error)) Console.Write("null");
            else Console.Write("\"" + Escape(provider.Error) + "\"");
            Console.WriteLine(" }" + (comma ? "," : ""));
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    internal static class NativeConsole
    {
        private const uint AttachParentProcess = 0xFFFFFFFF;

        public static void AttachParent()
        {
            AttachConsole(AttachParentProcess);
        }

        public static void FreeCurrent()
        {
            FreeConsole();
            SetStdHandle(-10, IntPtr.Zero);
            SetStdHandle(-11, IntPtr.Zero);
            SetStdHandle(-12, IntPtr.Zero);
        }

        public static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(uint processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetStdHandle(int stdHandle, IntPtr handle);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }
}
