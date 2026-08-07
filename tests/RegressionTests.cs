using System;
using System.Collections.Generic;
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
                TestClaudeResetParsing();
                TestClaudeUsagePercentParsing();
                TestDisplayPolicy(Path.Combine(root, "settings"));
                TestAccounts(Path.Combine(root, "accounts"));
                TestAccountSetupHelpers(Path.Combine(root, "account-setup"));
                TestAccountCredentialDetection(Path.Combine(root, "account-credentials"));
                TestPreserveMissingBuckets();
                TestUsageCache(Path.Combine(root, "usage-cache"));
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

        private static void TestClaudeResetParsing()
        {
            ProviderUsage withTimeZoneParens = new ProviderUsage { Name = "Claude" };
            ClaudeUsageCollector.ParseUsage(
                "Current session\n  23% used\n  Resets 11:30pm (Asia/Seoul)\n\n" +
                "Current week (all models)\n  7% used\n  Resets 11:30pm (Asia/Seoul)\n",
                withTimeZoneParens);
            Assert(withTimeZoneParens.Buckets.Count == 2,
                "Both usage buckets must be parsed.");
            foreach (UsageBucket bucket in withTimeZoneParens.Buckets)
            {
                Assert(bucket.ResetsAt.HasValue,
                    "A trailing timezone name must not block reset-time parsing: " +
                    bucket.Label);
                Assert(bucket.ResetsAt.Value.Hour == 23 &&
                    bucket.ResetsAt.Value.Minute == 30,
                    "The parsed reset time must match the 11:30pm source text.");
            }

            ProviderUsage withTimeZoneAbbreviation = new ProviderUsage { Name = "Claude" };
            ClaudeUsageCollector.ParseUsage(
                "Current session\n  10% used\n  Resets 6:00am KST\n",
                withTimeZoneAbbreviation);
            Assert(withTimeZoneAbbreviation.Buckets.Count == 1 &&
                withTimeZoneAbbreviation.Buckets[0].ResetsAt.HasValue &&
                withTimeZoneAbbreviation.Buckets[0].ResetsAt.Value.Hour == 6,
                "A bare trailing timezone abbreviation must not block reset-time parsing.");

            ProviderUsage withoutDesignatorSpace = new ProviderUsage { Name = "Claude" };
            ClaudeUsageCollector.ParseUsage(
                "Current session\n  5% used\n  Resets 4:59pm\n",
                withoutDesignatorSpace);
            Assert(withoutDesignatorSpace.Buckets.Count == 1 &&
                withoutDesignatorSpace.Buckets[0].ResetsAt.HasValue &&
                withoutDesignatorSpace.Buckets[0].ResetsAt.Value.Hour == 16,
                "A reset time without a space before am/pm must still parse.");

            ProviderUsage monthDayNoYear = new ProviderUsage { Name = "Claude" };
            ClaudeUsageCollector.ParseUsage(
                "Current session\n  15% used\n  Resets Aug 6, 5:59pm\n",
                monthDayNoYear);
            Assert(monthDayNoYear.Buckets.Count == 1 &&
                monthDayNoYear.Buckets[0].ResetsAt.HasValue &&
                monthDayNoYear.Buckets[0].ResetsAt.Value.Month == 8 &&
                monthDayNoYear.Buckets[0].ResetsAt.Value.Day == 6 &&
                monthDayNoYear.Buckets[0].ResetsAt.Value.Hour == 17 &&
                monthDayNoYear.Buckets[0].ResetsAt.Value.Minute == 59,
                "A 'MMM d, h:mm tt' reset (no year, no 'at') must still parse.");
            Assert(monthDayNoYear.Buckets[0].ResetsAt.Value.Year > 1,
                "A year-less 'MMM d' reset must not default to year 1 — it must " +
                "anchor to the current year, or the reset countdown renders blank.");

            ProviderUsage monthDayRoundHour = new ProviderUsage { Name = "Claude" };
            ClaudeUsageCollector.ParseUsage(
                "Current session\n  15% used\n  Resets Aug 6, 5pm\n\n" +
                "Current week (all models)\n  8% used\n  Resets Aug 7, 10am (Asia/Seoul)\n",
                monthDayRoundHour);
            Assert(monthDayRoundHour.Buckets.Count == 2,
                "Both buckets must be parsed for a round-hour reset (no minutes).");
            Assert(monthDayRoundHour.Buckets[0].ResetsAt.HasValue &&
                monthDayRoundHour.Buckets[0].ResetsAt.Value.Hour == 17,
                "A 'MMM d, h tt' reset (no minutes, no 'at') must still parse.");
            Assert(monthDayRoundHour.Buckets[1].ResetsAt.HasValue &&
                monthDayRoundHour.Buckets[1].ResetsAt.Value.Month == 8 &&
                monthDayRoundHour.Buckets[1].ResetsAt.Value.Day == 7 &&
                monthDayRoundHour.Buckets[1].ResetsAt.Value.Hour == 10,
                "A round-hour reset with a trailing timezone must still parse.");
            Assert(monthDayRoundHour.Buckets[1].ResetsAt.Value.Year > 1,
                "A year-less round-hour reset must not default to year 1.");

            // Real captured Claude Code output: ANSI cursor-jump codes leave
            // no separator between the reset text and whatever renders right
            // after it (here, a promo banner) — an unbounded lazy capture
            // with an incomplete lookahead list swallows everything to the
            // end of the buffer instead of just the date.
            ProviderUsage withTrailingPromoBanner = new ProviderUsage { Name = "Claude" };
            ClaudeUsageCollector.ParseUsage(
                "Current session\n  41% used\n  Resets 11:30am (Asia/Seoul)" +
                "Current week (all models)\n  16% used\n  " +
                "Resets Aug 7, 10am (Asia/Seoul)+50% weekly limits promo " +
                "through Aug 19 · clau.de/cc-50-promo\n",
                withTrailingPromoBanner);
            Assert(withTrailingPromoBanner.Buckets.Count == 2,
                "Both buckets must be parsed even with a promo banner glued " +
                "directly onto the reset text.");
            Assert(withTrailingPromoBanner.Buckets[1].UsedPercent == 16,
                "The weekly percent must still be read correctly.");
            Assert(withTrailingPromoBanner.Buckets[1].ResetsAt.HasValue &&
                withTrailingPromoBanner.Buckets[1].ResetsAt.Value.Month == 8 &&
                withTrailingPromoBanner.Buckets[1].ResetsAt.Value.Day == 7 &&
                withTrailingPromoBanner.Buckets[1].ResetsAt.Value.Hour == 10,
                "The weekly reset time must not be swallowed by a trailing " +
                "promo banner glued onto the same line.");

            Console.WriteLine("Claude reset-time parsing tests passed.");
        }

        private static void TestClaudeUsagePercentParsing()
        {
            // Claude Code redraws /usage twice in the same captured buffer:
            // a rough estimate, then a corrected value after "Scanning local
            // sessions…". The first regex match would grab the stale
            // estimate — the parser must take the last (settled) one.
            ProviderUsage tworenders = new ProviderUsage { Name = "Claude" };
            ClaudeUsageCollector.ParseUsage(
                "Current session\n  41% used\n  Resets 11:30am\n\n" +
                "Current week (all models)\n  16% used\n  Resets Aug 7, 10am\n\n" +
                "Scanning local sessions...\n" +
                "Current session\n  42% used\n  Resets 11:29am\n\n" +
                "Current week (all models)\n  17% used\n  Resets Aug 7, 9:59am\n",
                tworenders);
            Assert(tworenders.Buckets.Count == 2,
                "Both buckets must be parsed when the buffer holds two redraws.");
            Assert(tworenders.Buckets[0].UsedPercent == 42,
                "The session percent must come from the final redraw (42%), not the stale first one (41%).");
            Assert(tworenders.Buckets[1].UsedPercent == 17,
                "The weekly percent must come from the final redraw (17%), not the stale first one (16%).");

            // A "used" percent over 100 is impossible — it means the capture
            // glued digits together across overlapping render fragments.
            // Treat it as a failed parse rather than showing a bogus number.
            ProviderUsage impossiblePercent = new ProviderUsage { Name = "Claude" };
            ClaudeUsageCollector.ParseUsage(
                "Current session\n  107% used\n  Resets 11:30am\n",
                impossiblePercent);
            Assert(impossiblePercent.Buckets.Count == 0,
                "A used percent over 100 must be rejected, not treated as real data.");

            Console.WriteLine("Claude usage percent parsing tests passed.");
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

            Assert(TokenBarForm.FindBucket(codex, 300, 0) != null,
                "The hidden Codex 5h bucket must remain parsed and available.");
            Assert(!TokenBarForm.ShouldShowFiveHour("Codex", false),
                "Codex 5h display policy did not hide the row.");
            Assert(TokenBarForm.ShouldShowFiveHour("Codex", true),
                "Codex 5h display policy could not be restored.");
            Assert(TokenBarForm.ShouldShowFiveHour("Claude", false),
                "Claude 5h must remain visible.");
            Console.WriteLine("Codex display policy tests passed.");
        }

        private static void TestAccounts(string root)
        {
            Directory.CreateDirectory(root);

            AppSettings noAccounts = AppSettings.Load(Path.Combine(root, "missing.ini"));
            Assert(noAccounts.Accounts.Count == 0,
                "A config without Accounts= must add no accounts.");

            string path = Path.Combine(root, "accounts.ini");
            File.WriteAllText(path,
                "Accounts=company, personal\r\n" +
                "Account.company.Label=Company\r\n");
            AppSettings loaded = AppSettings.Load(path);
            Assert(loaded.Accounts.Count == 2,
                "Both accounts in the comma-separated list must be parsed.");
            Assert(loaded.Accounts[0].Id == "company" && loaded.Accounts[0].Label == "Company",
                "The company account did not load its configured label.");
            Assert(loaded.Accounts[1].Id == "personal" && loaded.Accounts[1].Label == "personal",
                "An account without a Label must fall back to its id.");

            // Credential folders are derived from the id alone — nothing to
            // configure, nothing that can point somewhere unexpected.
            Assert(AccountPaths.ClaudeDir("company").EndsWith(
                Path.Combine("company", "claude"), StringComparison.OrdinalIgnoreCase) &&
                AccountPaths.CodexDir("company").EndsWith(
                Path.Combine("company", "codex"), StringComparison.OrdinalIgnoreCase),
                "Account credential paths must be derived deterministically from the id.");

            foreach (bool showFiveHour in new[] { false, true })
            {
                List<TokenBarForm.AccountState> zero = new List<TokenBarForm.AccountState>();
                Assert(TokenBarForm.FooterTop(showFiveHour, zero) ==
                    TokenBarForm.ContentTop + TokenBarForm.EmptyStateHeight +
                    TokenBarForm.AccountGap,
                    "Zero accounts must show only the empty-state block.");
                Assert(TokenBarForm.ContentTop == TokenBarForm.TopMargin +
                    TokenBarForm.AddAccountRowHeight + TokenBarForm.AccountGap,
                    "The persistent add-account row must always reserve its own space, " +
                    "even with zero accounts.");

                TokenBarForm.AccountState connectedBoth =
                    new TokenBarForm.AccountState(true, true);
                TokenBarForm.AccountState disconnected =
                    new TokenBarForm.AccountState(false, false);

                // A disconnected provider is never drawn — no placeholder —
                // so a fully-disconnected account contributes zero content.
                Assert(TokenBarForm.AccountContentHeight(disconnected, showFiveHour) == 0,
                    "A fully-disconnected account must contribute no card height.");

                List<TokenBarForm.AccountState> two =
                    new List<TokenBarForm.AccountState> { connectedBoth, connectedBoth };
                int firstTop = TokenBarForm.AccountLabelTop(0, showFiveHour, two);
                int secondTop = TokenBarForm.AccountLabelTop(1, showFiveHour, two);
                Assert(secondTop == firstTop +
                    TokenBarForm.AccountBlockHeight(connectedBoth, showFiveHour),
                    "The second account block must stack directly under the first.");

                Rectangle codexSlot = TokenBarForm.AccountCodexSlotBounds(0, showFiveHour, two);
                Rectangle claudeSlot = TokenBarForm.AccountClaudeSlotBounds(0, showFiveHour, two);
                Assert(codexSlot.Top == firstTop + TokenBarForm.AccountLabelHeight,
                    "The Codex slot must sit directly below the account label.");
                Assert(claudeSlot.Top == codexSlot.Bottom + 10,
                    "The Claude slot must sit below the Codex slot with a fixed gap.");

                List<TokenBarForm.AccountState> claudeOnly = new List<TokenBarForm.AccountState>
                    { new TokenBarForm.AccountState(true, false) };
                Rectangle claudeSlotAlone =
                    TokenBarForm.AccountClaudeSlotBounds(0, showFiveHour, claudeOnly);
                Assert(claudeSlotAlone.Top ==
                    TokenBarForm.AccountLabelTop(0, showFiveHour, claudeOnly) +
                    TokenBarForm.AccountLabelHeight,
                    "With Codex disconnected, the Claude card must sit right under the " +
                    "label — no gap reserved for a Codex slot that isn't drawn.");
                Assert(TokenBarForm.AccountContentHeight(claudeOnly[0], showFiveHour) ==
                    TokenBarForm.ClaudeCardHeight,
                    "A Claude-only account must be exactly one card tall.");

                Assert(TokenBarForm.PreferredHeight(showFiveHour, two) ==
                    TokenBarForm.FooterTop(showFiveHour, two) + 35,
                    "Preferred height must track the footer position plus the fixed margin.");
            }
            Console.WriteLine("Account settings and layout tests passed.");
        }

        private static void TestAccountSetupHelpers(string root)
        {
            Directory.CreateDirectory(root);

            Assert(AppSettings.SlugifyAccountId("Company Corp", new string[0]) ==
                "company-corp",
                "A plain-ASCII label must slugify to lowercase-with-dashes.");
            string korean = AppSettings.SlugifyAccountId("회사 계정", new string[0]);
            Assert(korean == "account",
                "A label with no ASCII characters must fall back to a generic id.");
            Assert(AppSettings.SlugifyAccountId("회사 계정", new[] { "account" }) ==
                "account-2",
                "A colliding id must get a numeric suffix.");

            string path = Path.Combine(root, "tokenbar.ini");
            AppSettings.AddAccount(path, "company", "Company");
            AppSettings firstLoad = AppSettings.Load(path);
            Assert(firstLoad.Accounts.Count == 1 &&
                firstLoad.Accounts[0].Id == "company" &&
                firstLoad.Accounts[0].Label == "Company",
                "AddAccount must write a loadable single-account entry.");

            AppSettings.AddAccount(path, "personal", "Personal");
            AppSettings secondLoad = AppSettings.Load(path);
            Assert(secondLoad.Accounts.Count == 2 &&
                secondLoad.Accounts[0].Id == "company" &&
                secondLoad.Accounts[1].Id == "personal",
                "Adding a second account must keep the first and append the Accounts list.");

            AppSettings.AddAccount(path, "company", "Company Updated");
            AppSettings thirdLoad = AppSettings.Load(path);
            Assert(thirdLoad.Accounts.Count == 2,
                "Re-adding an existing id must update it in place, not duplicate it.");
            AccountConfig updatedCompany = thirdLoad.Accounts[0].Id == "company"
                ? thirdLoad.Accounts[0] : thirdLoad.Accounts[1];
            Assert(updatedCompany.Label == "Company Updated",
                "Re-adding an existing id must overwrite its label.");

            AppSettings.RemoveAccount(path, "company");
            AppSettings afterRemove = AppSettings.Load(path);
            Assert(afterRemove.Accounts.Count == 1 && afterRemove.Accounts[0].Id == "personal",
                "RemoveAccount must drop the id from Accounts= and keep the rest.");

            string removedIniText = File.ReadAllText(path);
            Assert(removedIniText.IndexOf("Account.company.",
                StringComparison.OrdinalIgnoreCase) < 0,
                "RemoveAccount must also delete the account's own Account.<id>.* lines.");

            AppSettings.RemoveAccount(path, "personal");
            Assert(AppSettings.Load(path).Accounts.Count == 0,
                "Removing the last account must leave an empty Accounts= list.");

            Console.WriteLine("Account setup helper tests passed.");
        }

        private static void TestAccountCredentialDetection(string root)
        {
            Directory.CreateDirectory(root);

            string legacyClaudeDir = Path.Combine(root, "legacy-claude");
            Directory.CreateDirectory(legacyClaudeDir);
            Assert(!TokenBarForm.HasClaudeCredential(legacyClaudeDir),
                "An empty account folder must not be reported as connected.");
            File.WriteAllText(Path.Combine(legacyClaudeDir, ".credentials.json"), "{}");
            Assert(TokenBarForm.HasClaudeCredential(legacyClaudeDir),
                "The legacy .credentials.json filename must be recognized.");

            string modernClaudeDir = Path.Combine(root, "modern-claude");
            Directory.CreateDirectory(modernClaudeDir);
            File.WriteAllText(Path.Combine(modernClaudeDir, ".claude.json"), "{}");
            Assert(TokenBarForm.HasClaudeCredential(modernClaudeDir),
                "The newer .claude.json filename must also be recognized.");

            string codexDir = Path.Combine(root, "codex");
            Directory.CreateDirectory(codexDir);
            Assert(!TokenBarForm.HasCodexCredential(codexDir),
                "An empty account folder must not be reported as connected.");
            File.WriteAllText(Path.Combine(codexDir, "auth.json"), "{}");
            Assert(TokenBarForm.HasCodexCredential(codexDir),
                "A present auth.json must be recognized as connected.");

            Console.WriteLine("Account credential detection tests passed.");
        }

        private static void TestPreserveMissingBuckets()
        {
            ProviderUsage previous = new ProviderUsage { Name = "Claude" };
            previous.Buckets.Add(new UsageBucket { Label = "현재 세션", UsedPercent = 41, WindowMinutes = 300 });
            previous.Buckets.Add(new UsageBucket { Label = "주간", UsedPercent = 16, WindowMinutes = 10080 });

            // A fetch that only got the 5h bucket must not lose the
            // previously-known 7d bucket.
            ProviderUsage partial = new ProviderUsage { Name = "Claude" };
            partial.Buckets.Add(new UsageBucket { Label = "현재 세션", UsedPercent = 50, WindowMinutes = 300 });
            TokenBarForm.PreserveMissingBuckets(partial, previous);
            Assert(partial.Buckets.Count == 2,
                "A partially-successful fetch must keep its fresh bucket and backfill the missing one.");
            Assert(partial.Buckets[0].UsedPercent == 50,
                "The fresh 5h bucket must not be overwritten by the stale one.");
            Assert(partial.Buckets[1].UsedPercent == 16 && partial.Buckets[1].WindowMinutes == 10080,
                "The missing 7d bucket must be backfilled from the previous snapshot.");

            // A fetch that got both bucket kinds fresh must not pull in
            // anything stale.
            ProviderUsage complete = new ProviderUsage { Name = "Claude" };
            complete.Buckets.Add(new UsageBucket { Label = "현재 세션", UsedPercent = 55, WindowMinutes = 300 });
            complete.Buckets.Add(new UsageBucket { Label = "주간", UsedPercent = 22, WindowMinutes = 10080 });
            TokenBarForm.PreserveMissingBuckets(complete, previous);
            Assert(complete.Buckets.Count == 2 &&
                complete.Buckets[0].UsedPercent == 55 && complete.Buckets[1].UsedPercent == 22,
                "A fully-successful fetch must not be altered by backfilling.");

            Console.WriteLine("Preserve-missing-buckets tests passed.");
        }

        private static void TestUsageCache(string root)
        {
            Directory.CreateDirectory(root);
            string cachePath = Path.Combine(root, "nested", "cache.json");

            UsageSnapshot original = new UsageSnapshot();
            original.Claude = new ProviderUsage { Name = "Claude", CollectedAt = new DateTime(2026, 8, 7, 11, 30, 0) };
            original.Claude.Buckets.Add(new UsageBucket
            {
                Label = "주간", UsedPercent = 16, WindowMinutes = 10080,
                ResetsAt = new DateTime(2026, 8, 7, 10, 0, 0)
            });
            original.Codex = new ProviderUsage { Name = "Codex" };

            UsageCache.Save(cachePath, original);
            Assert(File.Exists(cachePath), "Saving a cache must create the file (including parent dirs).");

            UsageSnapshot loaded = UsageCache.Load(cachePath);
            Assert(loaded != null, "A saved cache must load back successfully.");
            Assert(loaded.Claude.Buckets.Count == 1 &&
                loaded.Claude.Buckets[0].UsedPercent == 16 &&
                loaded.Claude.Buckets[0].ResetsAt.HasValue &&
                loaded.Claude.Buckets[0].ResetsAt.Value.Hour == 10,
                "A round-tripped bucket must keep its percent and reset time.");
            Assert(loaded.Claude.CollectedAt.HasValue &&
                loaded.Claude.CollectedAt.Value.Hour == 11,
                "A round-tripped provider must keep its collected timestamp.");

            Assert(UsageCache.Load(Path.Combine(root, "missing.json")) == null,
                "Loading a missing cache file must return null, not throw.");

            Console.WriteLine("Usage cache round-trip tests passed.");
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
            RenderTwoAccountPreview(Path.Combine(directory, "two-extra-accounts.png"));
            RenderNotConnectedPreview(Path.Combine(directory, "not-connected.png"));
            RenderEmptyStatePreview(Path.Combine(directory, "empty-state.png"));
            Console.WriteLine("UI previews rendered: {0}", directory);
        }

        private static void RenderEmptyStatePreview(string path)
        {
            AppSettings settings = new AppSettings { ShowCodexFiveHour = false };
            using (TokenBarForm form = new TokenBarForm(settings))
            {
                form.CreateControl();
                InvokeResizeAndRedraw(form);
                SaveFormBitmap(form, path);
            }
        }

        private static void RenderNotConnectedPreview(string path)
        {
            AppSettings settings = new AppSettings { ShowCodexFiveHour = false };
            settings.Accounts.Add(new AccountConfig { Id = "company", Label = "회사 계정" });
            settings.Accounts.Add(new AccountConfig { Id = "personal", Label = "개인 계정" });

            using (TokenBarForm form = new TokenBarForm(settings))
            {
                form.CreateControl();
                List<AccountSnapshot> accountSnapshots = GetAccountSnapshots(form);
                // company: Claude connected, Codex not connected.
                accountSnapshots[0].Snapshot.Claude = SamplePreviewSnapshot().Claude;
                accountSnapshots[0].ClaudeConnected = true;
                // personal: neither provider connected yet (defaults apply).
                InvokeResizeAndRedraw(form);
                SaveFormBitmap(form, path);
            }
        }

        private static void RenderTwoAccountPreview(string path)
        {
            AppSettings settings = new AppSettings { ShowCodexFiveHour = false };
            settings.Accounts.Add(new AccountConfig { Id = "company", Label = "회사 계정" });
            settings.Accounts.Add(new AccountConfig { Id = "personal", Label = "개인 계정" });

            using (TokenBarForm form = new TokenBarForm(settings))
            {
                form.CreateControl();
                foreach (AccountSnapshot account in GetAccountSnapshots(form))
                {
                    account.Snapshot = SamplePreviewSnapshot();
                    account.ClaudeConnected = true;
                    account.CodexConnected = true;
                }
                InvokeResizeAndRedraw(form);
                SaveFormBitmap(form, path);
            }
        }

        private static List<AccountSnapshot> GetAccountSnapshots(TokenBarForm form)
        {
            return (List<AccountSnapshot>)typeof(TokenBarForm).GetField("accountSnapshots",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        }

        private static void InvokeResizeAndRedraw(TokenBarForm form)
        {
            typeof(TokenBarForm).GetMethod("ResizeAndRedraw",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
        }

        private static void SaveFormBitmap(TokenBarForm form, string path)
        {
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static UsageSnapshot SamplePreviewSnapshot()
        {
            UsageSnapshot snapshot = new UsageSnapshot();
            snapshot.Codex.Buckets.Add(new UsageBucket
            {
                WindowMinutes = 300, UsedPercent = 25, ResetsAt = DateTime.Now.AddHours(2)
            });
            snapshot.Codex.Buckets.Add(new UsageBucket
            {
                WindowMinutes = 10080, UsedPercent = 40, ResetsAt = DateTime.Now.AddDays(3)
            });
            snapshot.Claude.Buckets.Add(new UsageBucket
            {
                WindowMinutes = 300, UsedPercent = 10, ResetsAt = DateTime.Now.AddHours(4)
            });
            snapshot.Claude.Buckets.Add(new UsageBucket
            {
                WindowMinutes = 10080, UsedPercent = 20, ResetsAt = DateTime.Now.AddDays(5)
            });
            return snapshot;
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
            AppSettings settings = new AppSettings { ShowCodexFiveHour = false };
            settings.Accounts.Add(new AccountConfig { Id = "preview", Label = "내 계정" });
            using (TokenBarForm form = new TokenBarForm(settings))
            {
                form.CreateControl();
                AccountSnapshot account = GetAccountSnapshots(form)[0];
                account.Snapshot = SamplePreviewSnapshot();
                account.ClaudeConnected = true;
                account.CodexConnected = true;
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
                InvokeResizeAndRedraw(form);
                SaveFormBitmap(form, path);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
