using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
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
                TestDisplayPolicy(Path.Combine(root, "settings"));
                TestUpdateChecker(Path.Combine(root, "updates"));
                TestUpdateCheckIsAsynchronous();
                if (string.Equals(Environment.GetEnvironmentVariable(
                    "TOKENBAR_LIVE_UPDATE_TEST"), "1", StringComparison.Ordinal))
                    TestLiveUpdateCheck(Path.Combine(root, "live-update"));
                string previewDirectory = Environment.GetEnvironmentVariable(
                    "TOKENBAR_PREVIEW_DIR");
                if (!string.IsNullOrEmpty(previewDirectory))
                    RenderPreviews(previewDirectory);
                TestDocumentation(Environment.GetEnvironmentVariable(
                    "TOKENBAR_PROJECT_ROOT"));
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
            bool powershellWrapper = expectedPath.EndsWith(
                ".ps1", StringComparison.OrdinalIgnoreCase);
            ConPtyCapture.Run(
                command.BuildCommandLine("--fake-claude " +
                    ClaudeUsageCollector.Quote(marker)),
                directory, new[] { new TimedInput(
                    powershellWrapper ? 3500 : 750, string.Empty) },
                powershellWrapper ? 12000 : 8000);
            Assert(File.Exists(marker),
                "Claude command did not execute through its host: " + expectedPath);
            Console.WriteLine("Claude command passed: {0}", expectedPath);
        }

        private static void TestDisplayPolicy(string root)
        {
            Directory.CreateDirectory(root);
            string missing = Path.Combine(root, "missing.ini");
            AppSettings defaults = AppSettings.Load(missing);
            Assert(!defaults.ShowCodexFiveHour,
                "Codex 5h must be hidden by default in v1.0.3.");

            string enabledPath = Path.Combine(root, "enabled.ini");
            File.WriteAllText(enabledPath,
                "ClaudeRefreshMinutes=20\r\nShowCodexFiveHour=true\r\n");
            AppSettings enabled = AppSettings.Load(enabledPath);
            Assert(enabled.ShowCodexFiveHour,
                "ShowCodexFiveHour=true must restore the Codex 5h row.");
            Assert(enabled.ClaudeRefreshMinutes == 20,
                "Existing Claude refresh settings must remain supported.");

            ProviderUsage codex = new ProviderUsage { Name = "Codex" };
            codex.Buckets.Add(new UsageBucket
            {
                WindowMinutes = 300,
                UsedPercent = 25
            });
            codex.Buckets.Add(new UsageBucket
            {
                WindowMinutes = 10080,
                UsedPercent = 40
            });
            ProviderUsage claude = new ProviderUsage { Name = "Claude" };
            claude.Buckets.Add(new UsageBucket
            {
                WindowMinutes = 300,
                UsedPercent = 10
            });
            claude.Buckets.Add(new UsageBucket
            {
                WindowMinutes = 10080,
                UsedPercent = 20
            });
            UsageSnapshot snapshot = new UsageSnapshot
            {
                Codex = codex,
                Claude = claude
            };

            Assert(TokenBarForm.FindBucket(codex, 300, 0) != null,
                "The hidden Codex 5h bucket must remain parsed and available.");
            Assert(!TokenBarForm.ShouldShowFiveHour("Codex", false),
                "Codex 5h display policy did not hide the row.");
            Assert(TokenBarForm.ShouldShowFiveHour("Codex", true),
                "Codex 5h display policy could not be restored.");
            Assert(TokenBarForm.ShouldShowFiveHour("Claude", false),
                "Claude 5h must remain visible.");
            Assert(TokenBarForm.PreferredHeight(false) == 306 &&
                TokenBarForm.CodexCardBounds(false).Height == 78 &&
                TokenBarForm.ClaudeCardBounds(false).Top == 148 &&
                TokenBarForm.FooterTop(false) == 271,
                "Hidden Codex 5h left the old empty card spacing.");
            Assert(TokenBarForm.PreferredHeight(true) == 340 &&
                TokenBarForm.CodexCardBounds(true).Height == 112 &&
                TokenBarForm.ClaudeCardBounds(true).Top == 182,
                "Restored Codex 5h did not recover the original layout.");

            string hiddenTooltip = TokenBarForm.BuildTrayTooltip(snapshot, false);
            Assert(hiddenTooltip.IndexOf("C 5h", StringComparison.Ordinal) < 0,
                "Hidden tooltip still contains Codex 5h.");
            Assert(hiddenTooltip.IndexOf("C 7d 60%", StringComparison.Ordinal) >= 0,
                "Hidden tooltip lost Codex 7d.");
            Assert(hiddenTooltip.IndexOf("A 5h 90% · 7d 80%",
                StringComparison.Ordinal) >= 0,
                "Hidden tooltip changed Claude 5h or 7d.");

            string restoredTooltip = TokenBarForm.BuildTrayTooltip(snapshot, true);
            Assert(restoredTooltip.IndexOf("C 5h 75% · 7d 60%",
                StringComparison.Ordinal) >= 0,
                "Restored tooltip did not include Codex 5h and 7d.");
            Console.WriteLine("Codex display policy tests passed.");
        }

        private static void TestUpdateChecker(string root)
        {
            Directory.CreateDirectory(root);
            string validUrl =
                "https://github.com/tkjacob/token-bar-windows/releases/tag/v1.0.3";
            string latestJson =
                "{\"tag_name\":\"v1.0.3\",\"html_url\":\"" + validUrl + "\"}";

            UpdateInfo newer = UpdateChecker.ParseLatest("1.0.2", latestJson);
            Assert(newer != null && newer.Version == "1.0.3",
                "A newer v-prefixed release was not detected.");
            Assert(UpdateChecker.ParseLatest("1.0.3", latestJson) == null,
                "The current version must not be reported as an update.");
            Assert(UpdateChecker.ParseLatest("1.0.4", latestJson) == null,
                "An older release must not be reported as an update.");
            Assert(UpdateChecker.ParseLatest("invalid", latestJson) == null,
                "An invalid current version must fail closed.");
            Assert(UpdateChecker.ParseLatest("1.0.2",
                "{\"tag_name\":\"preview\",\"html_url\":\"" + validUrl + "\"}") == null,
                "An invalid release version must fail closed.");
            Assert(!UpdateChecker.IsAllowedReleaseUrl("https://example.com/v1.0.3"),
                "An external release URL was accepted.");
            Assert(!UpdateChecker.IsAllowedReleaseUrl(
                "http://github.com/tkjacob/token-bar-windows/releases/tag/v1.0.3"),
                "A non-HTTPS release URL was accepted.");
            Assert(UpdateChecker.IsAllowedReleaseUrl(validUrl),
                "The repository release URL was rejected.");

            HttpWebRequest request = UpdateChecker.CreateRequest();
            Assert(request.RequestUri.AbsoluteUri ==
                UpdateChecker.LatestReleaseEndpoint,
                "The update checker targets an unexpected endpoint.");
            Assert(request.Timeout == UpdateChecker.RequestTimeoutMilliseconds &&
                request.ReadWriteTimeout == UpdateChecker.RequestTimeoutMilliseconds,
                "The update request timeout is not bounded.");
            Assert(request.Credentials == null && !request.PreAuthenticate &&
                request.Headers["Authorization"] == null,
                "The public update request must not send authentication.");

            string statePath = Path.Combine(root, "update-state.ini");
            DateTime now = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
            int loadCount = 0;
            UpdateCheckOutcome first = UpdateChecker.Check("1.0.2", statePath, now,
                delegate
                {
                    loadCount++;
                    return latestJson;
                });
            Assert(first.Update != null && first.ShouldNotify,
                "A new release must produce one notification.");

            UpdateCheckOutcome repeated = UpdateChecker.Check(
                "1.0.2", statePath, now.AddHours(1),
                delegate
                {
                    loadCount++;
                    throw new InvalidOperationException("Should not run within 24 hours.");
                });
            Assert(repeated.Update != null && !repeated.ShouldNotify,
                "The same release notification was repeated.");
            Assert(loadCount == 1,
                "The release endpoint was queried again within 24 hours.");

            string failureState = Path.Combine(root, "failure-state.ini");
            UpdateCheckOutcome failed = UpdateChecker.Check(
                "1.0.2", failureState, now,
                delegate { throw new WebException("offline"); });
            Assert(failed.Update == null,
                "A network failure must not invent an update.");
            UpdateState persistedFailure = UpdateState.Load(failureState);
            Assert(persistedFailure.LastCheckUtc == now,
                "A failed attempt was not rate-limited for 24 hours.");
            Console.WriteLine("Update checker tests passed.");
        }

        private static void TestUpdateCheckIsAsynchronous()
        {
            ManualResetEvent release = new ManualResetEvent(false);
            ManualResetEvent completed = new ManualResetEvent(false);
            Stopwatch clock = Stopwatch.StartNew();
            UpdateChecker.BeginCheck(
                delegate
                {
                    release.WaitOne();
                    return new UpdateCheckOutcome();
                },
                delegate { completed.Set(); });
            clock.Stop();
            Assert(clock.ElapsedMilliseconds < 100,
                "Starting an update check blocked the caller.");
            Assert(!completed.WaitOne(0),
                "The background check did not wait for its worker.");
            release.Set();
            Assert(completed.WaitOne(2000),
                "The asynchronous update check did not complete.");
            release.Dispose();
            completed.Dispose();
            Console.WriteLine("Asynchronous update check test passed.");
        }

        private static void TestLiveUpdateCheck(string root)
        {
            Directory.CreateDirectory(root);
            string json = UpdateChecker.DownloadLatestReleaseJson();
            UpdateInfo update = UpdateChecker.ParseLatest("0.0.0", json);
            Assert(update != null &&
                UpdateChecker.IsAllowedReleaseUrl(update.ReleaseUrl),
                "The live public GitHub release check did not return a valid update.");
            Console.WriteLine("Live GitHub update check passed: v{0}",
                update.Version);
        }

        private static void RenderPreviews(string directory)
        {
            Directory.CreateDirectory(directory);
            RenderPreview(Path.Combine(directory, "codex-7d-default.png"), false);
            RenderPreview(Path.Combine(directory, "update-available.png"), true);
            Console.WriteLine("UI previews rendered: {0}", directory);
        }

        private static void TestDocumentation(string projectRoot)
        {
            Assert(!string.IsNullOrEmpty(projectRoot),
                "TOKENBAR_PROJECT_ROOT is required.");
            string version = File.ReadAllText(
                Path.Combine(projectRoot, "VERSION")).Trim();
            string readme = File.ReadAllText(
                Path.Combine(projectRoot, "README.md"));
            Assert(readme.IndexOf("version-" + version + "-blue",
                StringComparison.Ordinal) >= 0,
                "README version badge does not match VERSION.");
            Assert(readme.IndexOf(
                "Token Bar 자체가 별도 네트워크 요청을 보내지도 않습니다.",
                StringComparison.Ordinal) < 0,
                "README still claims that Token Bar makes no network requests.");
            Assert(readme.IndexOf("ShowCodexFiveHour=false",
                StringComparison.Ordinal) >= 0 &&
                readme.IndexOf("Codex는 주간(`7d`) 잔여량을",
                    StringComparison.Ordinal) >= 0 &&
                readme.IndexOf(
                    "api.github.com/repos/tkjacob/token-bar-windows/releases/latest",
                    StringComparison.Ordinal) >= 0,
                "README is missing required display or privacy copy.");
            Console.WriteLine("README presentation and privacy copy tests passed.");
        }

        private static void RenderPreview(string path, bool showUpdate)
        {
            AppSettings settings = new AppSettings
            {
                ShowCodexFiveHour = false
            };
            using (TokenBarForm form = new TokenBarForm(settings))
            {
                UsageSnapshot snapshot = new UsageSnapshot();
                snapshot.Codex.Buckets.Add(new UsageBucket
                {
                    WindowMinutes = 300,
                    UsedPercent = 25,
                    ResetsAt = DateTime.Now.AddHours(2)
                });
                snapshot.Codex.Buckets.Add(new UsageBucket
                {
                    WindowMinutes = 10080,
                    UsedPercent = 40,
                    ResetsAt = DateTime.Now.AddDays(3)
                });
                snapshot.Claude.Buckets.Add(new UsageBucket
                {
                    WindowMinutes = 300,
                    UsedPercent = 10,
                    ResetsAt = DateTime.Now.AddHours(4)
                });
                snapshot.Claude.Buckets.Add(new UsageBucket
                {
                    WindowMinutes = 10080,
                    UsedPercent = 20,
                    ResetsAt = DateTime.Now.AddDays(5)
                });
                typeof(TokenBarForm).GetField("snapshot",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(form, snapshot);
                if (showUpdate)
                {
                    typeof(TokenBarForm).GetField("availableUpdate",
                        BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(form, new UpdateInfo
                        {
                            Version = "1.0.4",
                            ReleaseUrl =
                                "https://github.com/tkjacob/token-bar-windows/releases/tag/v1.0.4"
                        });
                }
                form.CreateControl();
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap,
                        new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(path, ImageFormat.Png);
                }
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
