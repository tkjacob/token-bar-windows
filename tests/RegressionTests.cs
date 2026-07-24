using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace TokenBar.Tests
{
    internal static class RegressionTests
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--fake-claude")
            {
                if (args.Length > 1) File.WriteAllText(args[1], "FAKE_CLAUDE_OK");
                Console.WriteLine("FAKE_CLAUDE_OK " +
                    (args.Length > 1 ? args[1] : string.Empty));
                Console.Out.Flush();
                Thread.Sleep(300);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--hang")
            {
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }

            try
            {
                string root = Environment.GetEnvironmentVariable(
                    "TOKENBAR_REGRESSION_ROOT");
                if (string.IsNullOrEmpty(root))
                    throw new InvalidOperationException(
                        "TOKENBAR_REGRESSION_ROOT is required.");

                TestNewestFileSelection(Path.Combine(root, "sessions"));
                TestTimeoutBudget();
                TestClaudeCommands(Path.Combine(root, "commands"));
                Console.WriteLine("C# regression tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void TestNewestFileSelection(string root)
        {
            Directory.CreateDirectory(root);
            DateTime start = new DateTime(2026, 1, 1, 0, 0, 0,
                DateTimeKind.Utc);
            const int count = 5000;
            for (int index = 0; index < count; index++)
            {
                string folder = Path.Combine(root, (index % 25).ToString("00"));
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder,
                    index.ToString("00000") + ".jsonl");
                File.WriteAllText(path, "{}");
                File.SetLastWriteTimeUtc(path, start.AddSeconds(index));
            }

            Stopwatch clock = Stopwatch.StartNew();
            FileInfo[] selected = CodexUsageReader.SelectNewestFiles(root, 80);
            clock.Stop();
            Assert(selected.Length == 80, "Newest selection must be bounded to 80.");
            for (int index = 1; index < selected.Length; index++)
                Assert(selected[index - 1].LastWriteTimeUtc >=
                    selected[index].LastWriteTimeUtc,
                    "Newest selection must be descending.");
            Assert(selected[0].Name == "04999.jsonl",
                "The newest session file was not selected.");
            Assert(selected[79].Name == "04920.jsonl",
                "The bounded selection did not retain the expected cutoff.");
            Console.WriteLine("Newest-file selection: 5,000 -> 80 in {0} ms",
                clock.ElapsedMilliseconds);
        }

        private static void TestTimeoutBudget()
        {
            Assert(ConPtyCapture.RemainingTimeoutMilliseconds(15000, 11000) == 4000,
                "Elapsed input time must be removed from the timeout.");
            Assert(ConPtyCapture.RemainingTimeoutMilliseconds(1000, 1200) == 0,
                "Expired timeout must return zero.");

            string executable = Assembly.GetExecutingAssembly().Location;
            Stopwatch clock = Stopwatch.StartNew();
            ConPtyCapture.Run(
                ClaudeUsageCollector.Quote(executable) + " --hang",
                Path.GetDirectoryName(executable),
                new[]
                {
                    new TimedInput(700, "\r"),
                    new TimedInput(700, "\r")
                },
                1000);
            clock.Stop();
            Assert(clock.ElapsedMilliseconds < 2800,
                "ConPTY exceeded the overall timeout plus cleanup allowance: " +
                clock.ElapsedMilliseconds + " ms");
            Console.WriteLine("ConPTY timeout test: {0} ms", clock.ElapsedMilliseconds);
        }

        private static void TestClaudeCommands(string root)
        {
            Directory.CreateDirectory(root);
            string executable = Assembly.GetExecutingAssembly().Location;
            TestNative(root, executable, ".exe", ".EXE");
            TestNative(root, executable, ".com", ".COM");
            TestNative(root, executable, string.Empty, ".EXE;.CMD");
            TestWrapper(root, executable, ".cmd", ".CMD");
            TestWrapper(root, executable, ".bat", ".BAT");
            TestWrapper(root, executable, ".ps1", ".PS1");
            TestPathextPriority(root, executable);
        }

        private static void TestNative(string root, string executable,
            string extension, string pathExt)
        {
            string directory = Path.Combine(root,
                extension.Length == 0 ? "extensionless shim" : extension.Substring(1));
            Directory.CreateDirectory(directory);
            string candidate = Path.Combine(directory, "claude" + extension);
            File.Copy(executable, candidate, true);
            VerifyCommand(directory, pathExt, candidate);
        }

        private static void TestWrapper(string root, string executable,
            string extension, string pathExt)
        {
            string directory = Path.Combine(root, extension.Substring(1));
            Directory.CreateDirectory(directory);
            string candidate = Path.Combine(directory, "claude" + extension);
            if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(candidate, "& '" +
                    executable.Replace("'", "''") + "' @args\r\n");
            }
            else
            {
                File.WriteAllText(candidate, "@echo off\r\n\"" +
                    executable + "\" %*\r\n");
            }
            VerifyCommand(directory, pathExt, candidate);
        }

        private static void TestPathextPriority(string root, string executable)
        {
            string directory = Path.Combine(root, "pathext priority");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "claude"), "#!/bin/sh\r\nexit 1\r\n");
            string commandWrapper = Path.Combine(directory, "claude.cmd");
            File.WriteAllText(commandWrapper, "@echo off\r\n\"" +
                executable + "\" %*\r\n");
            ClaudeCommand command = ClaudeUsageCollector.FindClaudeCommand(
                directory, ".EXE;.CMD", Path.Combine(directory, "missing-appdata"));
            Assert(command != null && command.Path.EndsWith("claude.cmd",
                StringComparison.OrdinalIgnoreCase),
                "PATHEXT command must win over a same-name extensionless shell script.");
            VerifyCommand(directory, ".EXE;.CMD", commandWrapper);
        }

        private static void VerifyCommand(string directory, string pathExt,
            string expectedPath)
        {
            ClaudeCommand command = ClaudeUsageCollector.FindClaudeCommand(
                directory, pathExt, Path.Combine(directory, "missing-appdata"));
            Assert(command != null, "Claude command was not detected: " + expectedPath);
            Assert(string.Equals(command.Path, Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase),
                "Unexpected Claude command: " + command.Path);
            string marker = Path.Combine(directory, "execution marker.txt");
            ConPtyCapture.Run(
                command.BuildCommandLine("--fake-claude " +
                    ClaudeUsageCollector.Quote(marker)),
                directory, new[] { new TimedInput(500, string.Empty) }, 4000);
            Assert(File.Exists(marker),
                "Claude command did not execute through its host: " + expectedPath);
            Console.WriteLine("Claude command passed: {0}", expectedPath);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
