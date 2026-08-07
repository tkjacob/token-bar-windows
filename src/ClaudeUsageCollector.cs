using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TokenBar
{
    internal static class ClaudeUsageCollector
    {
        private static readonly Regex Ansi = new Regex(
            @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))",
            RegexOptions.Compiled);

        // Multiple accounts refresh concurrently (one ThreadPool item per
        // account), but each Claude fetch spawns a real ConPTY + CLI process
        // and depends on fixed-millisecond timing to type "/usage" and read
        // it back. Several of those running at once contend for CPU and the
        // timing budget stops being enough, so every one of them can come
        // back empty at once. Serializing just the Claude portion (Codex is
        // cheap local file reads, left concurrent) fixes that at the cost of
        // accounts finishing one after another instead of all at once.
        private static readonly object CollectLock = new object();

        public static ProviderUsage Collect()
        {
            return Collect(null);
        }

        public static ProviderUsage Collect(string claudeConfigDir)
        {
            lock (CollectLock)
            {
                return CollectLocked(claudeConfigDir);
            }
        }

        private static ProviderUsage CollectLocked(string claudeConfigDir)
        {
            ProviderUsage result = new ProviderUsage { Name = "Claude", CollectedAt = DateTime.Now };
            try
            {
                ClaudeCommand command = FindClaudeCommand();
                if (command == null)
                {
                    result.Error = "Claude CLI를 찾을 수 없습니다.";
                    return result;
                }

                string commandLine = command.BuildCommandLine(
                    "--settings \"{\\\"disableAllHooks\\\":true}\"");

                Dictionary<string, string> environmentOverrides = null;
                if (!string.IsNullOrEmpty(claudeConfigDir))
                {
                    environmentOverrides = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                    environmentOverrides["CLAUDE_CONFIG_DIR"] = claudeConfigDir;
                }

                string captured = ConPtyCapture.Run(
                    commandLine,
                    RuntimePaths.BaseDirectory,
                    new[]
                    {
                        new TimedInput(2500, "\r"),
                        new TimedInput(1000, "/usage"),
                        // The command text and Enter must land as separate
                        // writes with a gap between them — sending them in
                        // one burst races the slash-command autocomplete
                        // dropdown (seen live in Claude Code v2.1.223+) and
                        // Enter gets swallowed by the dropdown instead of
                        // submitting, so /usage never actually runs.
                        new TimedInput(500, "\r"),
                        new TimedInput(7000, "\x03"),
                        new TimedInput(500, "\x03")
                    },
                    15000,
                    environmentOverrides);

                ParseUsage(captured, result);
                if (result.Buckets.Count == 0)
                    result.Error = "Claude /usage 화면을 해석하지 못했습니다.";
            }
            catch (Exception ex)
            {
                result.Error = "Claude 갱신 실패: " + ex.Message;
            }
            return result;
        }

        internal static void ParseUsage(string raw, ProviderUsage result)
        {
            string text = Normalize(raw);

            // The reset-time capture is bounded (max ~60 chars) and stops at
            // a "+50% weekly limits promo…" style banner in addition to the
            // usual section headers — ANSI cursor-jump codes leave no space
            // between the reset text and whatever the CLI renders right
            // after it, so an unbounded lazy capture with an incomplete
            // lookahead list runs all the way to the end of the captured
            // terminal buffer and turns the whole reset time into garbage.
            Match session = Regex.Match(text,
                @"Current\s+session.*?(\d{1,3})\s*%\s*used.*?Resets\s+(.{1,60}?)(?=Current\s+week|All\s+models|Sonnet|Opus|Extra\s+usage|\+\d|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!session.Success)
            {
                session = Regex.Match(text,
                    @"Current.*?(\d{1,3})\s*%\s*used.*?Resets\s+(.{1,60}?)(?=Weekly|Week|Extra|\+\d|$)",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
            AddMatch(result, session, "현재 세션", 300);

            Match week = Regex.Match(text,
                @"(?:Current\s+week|All\s+models).*?(\d{1,3})\s*%\s*used.*?Resets\s+(.{1,60}?)(?=Sonnet|Opus|Extra\s+usage|\+\d|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!week.Success)
            {
                week = Regex.Match(text,
                    @"Weekly.*?(\d{1,3})\s*%\s*used.*?Resets\s+(.{1,60}?)(?=Extra|\+\d|$)",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
            AddMatch(result, week, "주간", 10080);
        }

        private static void AddMatch(ProviderUsage result, Match match, string label,
            int windowMinutes)
        {
            if (!match.Success) return;
            double used;
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out used))
                return;

            result.Buckets.Add(new UsageBucket
            {
                Label = label,
                UsedPercent = used,
                WindowMinutes = windowMinutes,
                ResetsAt = ParseClaudeReset(match.Groups[2].Value)
            });
        }

        private static DateTime? ParseClaudeReset(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string compact = Regex.Replace(value, @"\s+", " ").Trim();
            compact = Regex.Replace(compact, @"[│╰─╭╮╯┌┐└┘]+", " ").Trim();
            compact = StripTrailingTimeZone(compact);
            // "4:59pm" (no space before the designator) must still match the
            // "h:mm tt" family of exact formats below.
            compact = Regex.Replace(compact, @"(\d)(am|pm)\b", "$1 $2",
                RegexOptions.IgnoreCase);
            DateTime parsed;
            string[] patterns =
            {
                // Each pair covers both "10:00am" and the round-hour form
                // "10am" (no minutes) — Claude's reset text uses either.
                "h:mm tt", "hh:mm tt", "h tt", "hh tt",
                "MMM d 'at' h:mm tt", "MMM d 'at' h tt",
                "MMM d, yyyy 'at' h:mm tt", "MMM d, yyyy 'at' h tt",
                "MMMM d 'at' h:mm tt", "MMMM d 'at' h tt",
                "MMM d, h:mm tt", "MMM d, h tt"
            };
            foreach (string pattern in patterns)
            {
                if (DateTime.TryParseExact(compact, pattern, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out parsed))
                {
                    if (pattern.IndexOf("MMM", StringComparison.Ordinal) < 0)
                    {
                        DateTime candidate = DateTime.Today.Add(parsed.TimeOfDay);
                        if (candidate < DateTime.Now.AddMinutes(-2)) candidate = candidate.AddDays(1);
                        return candidate;
                    }
                    if (pattern.IndexOf("yyyy", StringComparison.Ordinal) < 0)
                    {
                        // No year in the source text, so TryParseExact defaults
                        // it to year 1 — anchor to the current year instead,
                        // rolling forward across a Dec-to-Jan boundary the same
                        // way the time-only branch rolls forward a day.
                        DateTime candidate = new DateTime(DateTime.Now.Year, parsed.Month,
                            parsed.Day, parsed.Hour, parsed.Minute, 0);
                        if (candidate < DateTime.Now.AddMinutes(-2))
                            candidate = candidate.AddYears(1);
                        return candidate;
                    }
                    return parsed;
                }
            }
            if (DateTime.TryParse(compact, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out parsed))
                return parsed;
            return null;
        }

        private static string StripTrailingTimeZone(string value)
        {
            // "Resets 4:59pm (Asia/Seoul)" / "Resets 4:59pm KST" — the exact-match
            // patterns below only know the clock/date portion, so a trailing zone
            // name or offset must come off first or every pattern fails silently.
            string result = Regex.Replace(value, @"\s*\([^()]*\)\s*$", "").Trim();
            result = Regex.Replace(result,
                @"\s+(?!(?:am|pm)\b)(?:[A-Za-z]{2,5}|GMT[+-]?\d{1,2}(?::?\d{2})?|UTC[+-]?\d{1,2}(?::?\d{2})?|[+-]\d{2}:?\d{2})$",
                "", RegexOptions.IgnoreCase).Trim();
            return result.Length == 0 ? value : result;
        }

        private static string Normalize(string raw)
        {
            if (raw == null) return string.Empty;
            string text = Ansi.Replace(raw, string.Empty);
            text = text.Replace("\0", string.Empty).Replace("\r", "\n");
            // Terminals may redraw a line with backspaces.
            while (text.IndexOf('\b') >= 0)
                text = Regex.Replace(text, @".\x08", string.Empty);
            return Regex.Replace(text, @"[ \t]+", " ");
        }

        internal static ClaudeCommand FindClaudeCommand()
        {
            return FindClaudeCommand(
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATHEXT"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        }

        internal static ClaudeCommand FindClaudeCommand(
            string path, string pathExt, string applicationData)
        {
            path = path ?? string.Empty;
            List<string> extensions = new List<string>();
            foreach (string extension in (pathExt ?? string.Empty).Split(Path.PathSeparator))
                AddExtension(extensions, extension);
            AddExtension(extensions, ".exe");
            AddExtension(extensions, ".cmd");
            AddExtension(extensions, ".bat");
            AddExtension(extensions, ".com");
            AddExtension(extensions, ".ps1");
            // Match Windows command resolution: executable PATHEXT entries win over
            // a same-name extensionless shell script. Native extensionless shims
            // remain supported as the final candidate.
            extensions.Add(string.Empty);

            foreach (string directory in path.Split(Path.PathSeparator))
            {
                string cleanDirectory = directory.Trim().Trim('"');
                if (cleanDirectory.Length == 0) continue;
                foreach (string extension in extensions)
                {
                    string candidate;
                    try { candidate = Path.Combine(cleanDirectory, "claude" + extension); }
                    catch { continue; }
                    if (File.Exists(candidate)) return new ClaudeCommand(candidate);
                }
            }

            string npmRoot = Path.Combine(applicationData ?? string.Empty, "npm");
            foreach (string extension in extensions)
            {
                string wrapper = Path.Combine(npmRoot, "claude" + extension);
                if (File.Exists(wrapper)) return new ClaudeCommand(wrapper);
            }
            string native = Path.Combine(npmRoot, "node_modules", "@anthropic-ai",
                "claude-code", "bin", "claude.exe");
            return File.Exists(native) ? new ClaudeCommand(native) : null;
        }

        private static void AddExtension(List<string> extensions, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string normalized = value.Trim();
            if (!normalized.StartsWith(".", StringComparison.Ordinal))
                normalized = "." + normalized;
            foreach (string existing in extensions)
            {
                if (string.Equals(existing, normalized,
                    StringComparison.OrdinalIgnoreCase))
                    return;
            }
            extensions.Add(normalized);
        }

        internal static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }

    internal sealed class ClaudeCommand
    {
        public readonly string Path;

        public ClaudeCommand(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
        }

        public string BuildCommandLine(string arguments)
        {
            string extension = System.IO.Path.GetExtension(Path);
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                string host = Environment.GetEnvironmentVariable("COMSPEC");
                if (string.IsNullOrEmpty(host)) host = "cmd.exe";
                return ClaudeUsageCollector.Quote(host) +
                    " /d /s /c \"\"" + Path + "\" " + arguments + "\"";
            }
            if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                string windows = Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows);
                string host = System.IO.Path.Combine(windows, "System32",
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(host)) host = "powershell.exe";
                return ClaudeUsageCollector.Quote(host) +
                    " -NoLogo -NoProfile -ExecutionPolicy Bypass -File " +
                    ClaudeUsageCollector.Quote(Path) + " " + arguments;
            }
            return ClaudeUsageCollector.Quote(Path) + " " + arguments;
        }
    }

    internal sealed class TimedInput
    {
        public readonly int DelayMilliseconds;
        public readonly string Text;
        public TimedInput(int delayMilliseconds, string text)
        {
            DelayMilliseconds = delayMilliseconds;
            Text = text;
        }
    }

    internal static class ConPtyCapture
    {
        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint WaitTimeout = 0x00000102;
        private const uint HandleFlagInherit = 0x00000001;

        public static string Run(string commandLine, string currentDirectory,
            IEnumerable<TimedInput> inputs, int overallTimeoutMilliseconds)
        {
            return Run(commandLine, currentDirectory, inputs,
                overallTimeoutMilliseconds, null);
        }

        public static string Run(string commandLine, string currentDirectory,
            IEnumerable<TimedInput> inputs, int overallTimeoutMilliseconds,
            IDictionary<string, string> environmentOverrides)
        {
            Stopwatch clock = Stopwatch.StartNew();
            IntPtr inputRead = IntPtr.Zero;
            IntPtr inputWrite = IntPtr.Zero;
            IntPtr outputRead = IntPtr.Zero;
            IntPtr outputWrite = IntPtr.Zero;
            IntPtr pseudoConsole = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr environmentBlock = IntPtr.Zero;
            ProcessInformation processInfo = new ProcessInformation();
            MemoryStream captured = new MemoryStream();
            Thread reader = null;

            try
            {
                SecurityAttributes security = new SecurityAttributes();
                security.nLength = Marshal.SizeOf(security);
                security.bInheritHandle = true;

                Check(CreatePipe(out inputRead, out inputWrite, ref security, 0), "CreatePipe(stdin)");
                Check(CreatePipe(out outputRead, out outputWrite, ref security, 0), "CreatePipe(stdout)");
                Check(SetHandleInformation(inputWrite, HandleFlagInherit, 0),
                    "SetHandleInformation(stdin)");
                Check(SetHandleInformation(outputRead, HandleFlagInherit, 0),
                    "SetHandleInformation(stdout)");

                HResult(CreatePseudoConsole(new Coord(160, 50), inputRead, outputWrite, 0,
                    out pseudoConsole), "CreatePseudoConsole");

                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                attributeList = Marshal.AllocHGlobal(size);
                Check(InitializeProcThreadAttributeList(attributeList, 1, 0, ref size),
                    "InitializeProcThreadAttributeList");
                Check(UpdateProcThreadAttribute(attributeList, 0,
                    (IntPtr)ProcThreadAttributePseudoConsole, pseudoConsole, (IntPtr)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute");

                StartupInfoEx startup = new StartupInfoEx();
                startup.StartupInfo.cb = Marshal.SizeOf(startup);
                startup.lpAttributeList = attributeList;

                if (environmentOverrides != null && environmentOverrides.Count > 0)
                    environmentBlock = BuildEnvironmentBlock(environmentOverrides);

                StringBuilder mutableCommand = new StringBuilder(commandLine);
                Check(CreateProcess(null, mutableCommand, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, environmentBlock,
                    currentDirectory, ref startup, out processInfo), "CreateProcess");

                if (environmentBlock != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(environmentBlock);
                    environmentBlock = IntPtr.Zero;
                }

                Close(ref inputRead);
                Close(ref outputWrite);

                IntPtr localOutputRead = outputRead;
                outputRead = IntPtr.Zero;
                reader = new Thread(delegate()
                {
                    try
                    {
                        using (SafeFileHandle safe = new SafeFileHandle(localOutputRead, true))
                        using (FileStream stream = new FileStream(safe, FileAccess.Read, 4096, false))
                        {
                            byte[] buffer = new byte[4096];
                            int count;
                            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                lock (captured) captured.Write(buffer, 0, count);
                            }
                        }
                    }
                    catch { }
                });
                reader.IsBackground = true;
                reader.Start();

                using (SafeFileHandle safeInput = new SafeFileHandle(inputWrite, true))
                using (FileStream writer = new FileStream(safeInput, FileAccess.Write, 1024, false))
                {
                    inputWrite = IntPtr.Zero;
                    foreach (TimedInput item in inputs)
                    {
                        if (clock.ElapsedMilliseconds + item.DelayMilliseconds > overallTimeoutMilliseconds)
                            break;
                        Thread.Sleep(item.DelayMilliseconds);
                        byte[] bytes = Encoding.UTF8.GetBytes(item.Text);
                        writer.Write(bytes, 0, bytes.Length);
                        writer.Flush();
                    }
                }

                uint remaining = (uint)RemainingTimeoutMilliseconds(
                    overallTimeoutMilliseconds, clock.ElapsedMilliseconds);
                uint wait = WaitForSingleObject(processInfo.hProcess, remaining);
                if (wait == WaitTimeout)
                    TerminateProcess(processInfo.hProcess, 0);

                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;
                if (reader != null) reader.Join(1500);

                lock (captured)
                    return Encoding.UTF8.GetString(captured.ToArray());
            }
            finally
            {
                if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole);
                Close(ref inputRead);
                Close(ref inputWrite);
                Close(ref outputRead);
                Close(ref outputWrite);
                Close(ref processInfo.hThread);
                Close(ref processInfo.hProcess);
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
                if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
                captured.Dispose();
            }
        }

        internal static int RemainingTimeoutMilliseconds(
            int overallTimeoutMilliseconds, long elapsedMilliseconds)
        {
            long remaining = (long)overallTimeoutMilliseconds - elapsedMilliseconds;
            if (remaining <= 0) return 0;
            return remaining > int.MaxValue ? int.MaxValue : (int)remaining;
        }

        private static IntPtr BuildEnvironmentBlock(IDictionary<string, string> overrides)
        {
            SortedDictionary<string, string> merged = new SortedDictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                merged[(string)entry.Key] = (string)entry.Value;
            foreach (KeyValuePair<string, string> pair in overrides)
                merged[pair.Key] = pair.Value;

            StringBuilder block = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in merged)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;
                block.Append(pair.Key).Append('=').Append(pair.Value ?? string.Empty).Append('\0');
            }
            block.Append('\0');
            return Marshal.StringToHGlobalUni(block.ToString());
        }

        private static void Check(bool success, string operation)
        {
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
        }

        private static void HResult(int value, string operation)
        {
            if (value < 0) Marshal.ThrowExceptionForHR(value);
        }

        private static void Close(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord
        {
            public short X;
            public short Y;
            public Coord(short x, short y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe,
            ref SecurityAttributes pipeAttributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output,
            uint flags, out IntPtr pseudoConsole);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList,
            int attributeCount, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags,
            IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
            uint creationFlags, IntPtr environment, string currentDirectory,
            ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
