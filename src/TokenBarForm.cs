using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TokenBar
{
    internal sealed class TokenBarForm : Form
    {
        private const int WsExToolWindow = 0x00000080;
        private const int CsDropShadow = 0x00020000;
        private const int AbmGetTaskbarPos = 0x00000005;
        private const int AbEdgeLeft = 0;
        private const int AbEdgeTop = 1;
        private const int AbEdgeRight = 2;
        private const int AbEdgeBottom = 3;

        private readonly AppSettings settings;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly System.Windows.Forms.Timer dismissTimer;
        private readonly NotifyIcon trayIcon;
        private readonly Icon applicationIcon;
        private readonly ContextMenuStrip trayMenu;
        private readonly Font titleFont;
        private readonly Font providerFont;
        private readonly Font valueFont;
        private readonly Font smallFont;

        private readonly List<AccountSnapshot> accountSnapshots = new List<AccountSnapshot>();
        // Lists rather than fixed-size arrays so a full account removal
        // (logout) can shrink these in place without needing a restart.
        private readonly List<bool> collectingAccount = new List<bool>();
        private readonly List<DateTime> lastAccountAttempt = new List<DateTime>();
        private bool checkingUpdates;
        private bool resourcesDisposed;
        private Icon updateIcon;
        private UpdateInfo availableUpdate;
        private DateTime lastUpdateCheckRequestUtc = DateTime.MinValue;
        private DateTime lastAutomaticHide = DateTime.MinValue;
        private DateTime openedAt = DateTime.MinValue;
        private Rectangle refreshButton;
        private Rectangle settingsButton;
        private Rectangle updateButton;
        private Rectangle addAccountButton;
        private Rectangle[] logoutButtons = new Rectangle[0];

        public TokenBarForm(AppSettings settings)
        {
            this.settings = settings;
            foreach (AccountConfig account in settings.Accounts)
            {
                accountSnapshots.Add(new AccountSnapshot { Label = account.Label });
                collectingAccount.Add(false);
                lastAccountAttempt.Add(DateTime.MinValue);
            }

            Text = "Token Bar";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Width = 400;
            Height = PreferredHeight(settings.ShowCodexFiveHour, BuildAccountStates());
            MinimumSize = new Size(400, Height);
            MaximumSize = new Size(400, Height);
            BackColor = Color.FromArgb(29, 31, 35);
            DoubleBuffered = true;
            Opacity = 0;

            titleFont = new Font("Segoe UI", 15.0f, FontStyle.Bold, GraphicsUnit.Point);
            providerFont = new Font("Segoe UI", 10.0f, FontStyle.Bold, GraphicsUnit.Point);
            valueFont = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
            smallFont = new Font("Segoe UI", 8.0f, FontStyle.Regular, GraphicsUnit.Point);
            Font = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);

            applicationIcon = CreateTrayIcon(false);
            trayMenu = BuildMenu();
            trayIcon = new NotifyIcon();
            trayIcon.Icon = applicationIcon;
            trayIcon.Text = "Token Bar";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.MouseDown += OnTrayMouseDown;
            trayIcon.BalloonTipClicked += delegate { OpenAvailableUpdate(); };

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 30000;
            refreshTimer.Tick += delegate { RefreshAll(false); };
            dismissTimer = new System.Windows.Forms.Timer();
            dismissTimer.Interval = 250;
            dismissTimer.Tick += delegate
            {
                if (Visible && !trayMenu.Visible &&
                    (DateTime.Now - openedAt).TotalMilliseconds > 700 &&
                    GetForegroundWindow() != Handle)
                {
                    lastAutomaticHide = DateTime.Now;
                    Hide();
                }
            };

            Load += delegate
            {
                refreshTimer.Start();
                dismissTimer.Start();
                RefreshAll(false);
            };
            Shown += delegate
            {
                Hide();
                Opacity = 1;
                BeginInvoke((MethodInvoker)ShowFlyout);
            };
            Deactivate += delegate
            {
                if (!trayMenu.Visible && Visible)
                {
                    lastAutomaticHide = DateTime.Now;
                    Hide();
                }
            };
            Resize += delegate { ApplyRoundedRegion(); };
            MouseUp += OnFlyoutMouseUp;
            MouseMove += OnFlyoutMouseMove;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow;
                cp.ClassStyle |= CsDropShadow;
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                resourcesDisposed = true;
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayMenu.Dispose();
                applicationIcon.Dispose();
                if (updateIcon != null) updateIcon.Dispose();
                titleFont.Dispose();
                providerFont.Dispose();
                valueFont.Dispose();
                smallFont.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle outer = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = RoundedRectangle(outer, 16))
            using (SolidBrush background = new SolidBrush(Color.FromArgb(242, 29, 31, 35)))
            using (Pen border = new Pen(Color.FromArgb(90, 123, 130, 140)))
            {
                e.Graphics.FillPath(background, path);
                e.Graphics.DrawPath(border, path);
            }

            TextRenderer.DrawText(e.Graphics, "Token Bar", titleFont,
                new Rectangle(22, 16, 160, 32), Color.FromArgb(246, 247, 249),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding);

            updateButton = Rectangle.Empty;
            if (availableUpdate != null)
            {
                updateButton = new Rectangle(187, 18, 132, 26);
                DrawUpdateBadge(e.Graphics, updateButton, availableUpdate.Version);
            }
            refreshButton = new Rectangle(326, 17, 28, 28);
            settingsButton = new Rectangle(360, 17, 28, 28);
            DrawRefreshIcon(e.Graphics, refreshButton);
            DrawSettingsIcon(e.Graphics, settingsButton);

            bool showCodexFiveHour = ShouldShowFiveHour(
                "Codex", settings.ShowCodexFiveHour);
            List<AccountState> states = BuildAccountStates();
            int footerTop = FooterTop(showCodexFiveHour, states);

            Size addSize = TextRenderer.MeasureText(e.Graphics, "+ 계정 추가", valueFont);
            addAccountButton = new Rectangle(16 + 368 - addSize.Width, TopMargin,
                addSize.Width, AddAccountRowHeight);
            TextRenderer.DrawText(e.Graphics, "+ 계정 추가", valueFont, addAccountButton,
                Color.FromArgb(70, 139, 255),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding);

            logoutButtons = new Rectangle[accountSnapshots.Count];

            if (accountSnapshots.Count == 0)
            {
                DrawEmptyState(e.Graphics, ContentTop);
            }
            else
            {
                for (int i = 0; i < accountSnapshots.Count; i++)
                {
                    AccountSnapshot account = accountSnapshots[i];
                    AccountState state = states[i];
                    bool loading = collectingAccount[i];
                    int labelTop = AccountLabelTop(i, showCodexFiveHour, states);
                    DrawAccountLabelRow(e.Graphics, i, labelTop, account.Label);

                    // A provider that isn't connected simply isn't drawn — no
                    // placeholder row, no dead-end button. The only way in is
                    // "+ 계정 추가" above, which merges into this account
                    // automatically when the same email is entered again.
                    if (state.CodexConnected)
                        DrawProviderCard(e.Graphics,
                            AccountCodexSlotBounds(i, showCodexFiveHour, states),
                            "Codex", account.Snapshot.Codex, Color.FromArgb(70, 139, 255),
                            showCodexFiveHour, loading);
                    if (state.ClaudeConnected)
                        DrawProviderCard(e.Graphics,
                            AccountClaudeSlotBounds(i, showCodexFiveHour, states),
                            "Claude", account.Snapshot.Claude, Color.FromArgb(255, 126, 51), true,
                            loading);
                }
            }

            bool anyCollecting = collectingAccount.Exists(delegate(bool c) { return c; });
            string updated = "Updated " + LatestAge();
            if (anyCollecting) updated += " · 새로고침 중";
            TextRenderer.DrawText(e.Graphics, updated, smallFont,
                new Rectangle(22, footerTop, 350, 22), Color.FromArgb(170, 178, 188),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private void DrawAccountLabelRow(Graphics graphics, int index, int top, string label)
        {
            TextRenderer.DrawText(graphics, label, providerFont,
                new Rectangle(16, top, 300, 22), Color.FromArgb(200, 205, 212),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            Rectangle logout = new Rectangle(316, top, 68, 22);
            TextRenderer.DrawText(graphics, "계정 삭제", smallFont, logout,
                Color.FromArgb(150, 158, 168),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding);
            logoutButtons[index] = logout;
        }

        private void DrawEmptyState(Graphics graphics, int top)
        {
            Rectangle area = new Rectangle(16, top, 368, EmptyStateHeight);
            using (GraphicsPath path = RoundedRectangle(area, 12))
            using (SolidBrush background = new SolidBrush(Color.FromArgb(115, 20, 22, 26)))
            using (Pen border = new Pen(Color.FromArgb(45, 150, 156, 166)))
            {
                graphics.FillPath(background, path);
                graphics.DrawPath(border, path);
            }
            TextRenderer.DrawText(graphics, "연결된 계정이 없습니다. 위 \"+ 계정 추가\"로 시작하세요.",
                smallFont, new Rectangle(area.Left + 16, area.Top, area.Width - 32, area.Height),
                Color.FromArgb(170, 178, 188),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        private void DrawProviderCard(Graphics graphics, Rectangle area, string name,
            ProviderUsage provider, Color accent, bool showFiveHour, bool loading)
        {
            using (GraphicsPath path = RoundedRectangle(area, 12))
            using (SolidBrush background = new SolidBrush(Color.FromArgb(115, 20, 22, 26)))
            using (Pen border = new Pen(Color.FromArgb(45, 150, 156, 166)))
            {
                graphics.FillPath(background, path);
                graphics.DrawPath(border, path);
            }

            using (SolidBrush dot = new SolidBrush(accent))
                graphics.FillEllipse(dot, area.Left + 16, area.Top + 15, 9, 9);
            TextRenderer.DrawText(graphics, name, providerFont,
                new Rectangle(area.Left + 34, area.Top + 8, 150, 26), accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding);

            if (provider == null || provider.Buckets.Count == 0)
            {
                TextRenderer.DrawText(graphics, loading ? "불러오는 중..." : "사용량 정보 없음",
                    smallFont,
                    new Rectangle(area.Left + 16, area.Top + 39, area.Width - 32,
                        area.Height - 47),
                    Color.FromArgb(140, 148, 158),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
                return;
            }

            UsageBucket fiveHour = FindBucket(provider, 300, 0);
            UsageBucket weekly = FindBucket(provider, 10080, 1);
            if (showFiveHour)
            {
                DrawUsageRow(graphics,
                    new Rectangle(area.Left + 16, area.Top + 39, area.Width - 32, 26),
                    "5h", fiveHour, accent);

                using (Pen divider = new Pen(Color.FromArgb(38, 220, 224, 230)))
                    graphics.DrawLine(divider, area.Left + 16, area.Top + 68,
                        area.Right - 16, area.Top + 68);

                DrawUsageRow(graphics,
                    new Rectangle(area.Left + 16, area.Top + 73, area.Width - 32, 26),
                    "7d", weekly, accent);
            }
            else
            {
                DrawUsageRow(graphics,
                    new Rectangle(area.Left + 16, area.Top + 39, area.Width - 32, 26),
                    "7d", weekly, accent);
            }
        }

        private void DrawUpdateBadge(Graphics graphics, Rectangle area, string version)
        {
            using (GraphicsPath path = RoundedRectangle(area, 12))
            using (SolidBrush background =
                new SolidBrush(Color.FromArgb(60, 70, 139, 255)))
            using (Pen border = new Pen(Color.FromArgb(160, 70, 139, 255)))
            {
                graphics.FillPath(background, path);
                graphics.DrawPath(border, path);
            }
            TextRenderer.DrawText(graphics, "v" + version + " 업데이트", smallFont,
                area, Color.FromArgb(230, 238, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private void DrawUsageRow(Graphics graphics, Rectangle area, string label,
            UsageBucket bucket, Color accent)
        {
            double? remaining = bucket == null ? null : bucket.RemainingPercent;
            string value = remaining.HasValue
                ? Math.Round(remaining.Value).ToString("0", CultureInfo.InvariantCulture) + "%"
                : "--";

            TextRenderer.DrawText(graphics, label, valueFont,
                new Rectangle(area.Left, area.Top, 28, area.Height),
                Color.FromArgb(235, 238, 242),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding);

            Rectangle track = new Rectangle(area.Left + 42, area.Top + 10, 142, 7);
            using (GraphicsPath trackPath = RoundedRectangle(track, 3))
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(70, 180, 187, 196)))
                graphics.FillPath(dim, trackPath);

            if (remaining.HasValue)
            {
                Rectangle fill = track;
                fill.Width = Math.Max(3,
                    (int)Math.Round(track.Width * remaining.Value / 100.0));
                using (GraphicsPath fillPath = RoundedRectangle(fill, 3))
                using (SolidBrush active = new SolidBrush(
                    remaining.Value > 50 ? StatusColor(remaining.Value) : accent))
                    graphics.FillPath(active, fillPath);
            }

            TextRenderer.DrawText(graphics, value, valueFont,
                new Rectangle(area.Left + 198, area.Top, 52, area.Height),
                remaining.HasValue ? StatusColor(remaining.Value) :
                    Color.FromArgb(150, 158, 168),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding);

            TextRenderer.DrawText(graphics, ResetText(bucket), smallFont,
                new Rectangle(area.Left + 258, area.Top, 78, area.Height),
                Color.FromArgb(170, 178, 188),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private static void DrawRefreshIcon(Graphics graphics, Rectangle area)
        {
            using (Pen pen = new Pen(Color.FromArgb(225, 230, 235), 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                Rectangle arc = new Rectangle(area.Left + 5, area.Top + 5, 17, 17);
                graphics.DrawArc(pen, arc, 35, 280);
                graphics.DrawLine(pen, area.Right - 7, area.Top + 4,
                    area.Right - 7, area.Top + 10);
                graphics.DrawLine(pen, area.Right - 7, area.Top + 10,
                    area.Right - 13, area.Top + 10);
            }
        }

        private static void DrawSettingsIcon(Graphics graphics, Rectangle area)
        {
            Point center = new Point(area.Left + area.Width / 2,
                area.Top + area.Height / 2);
            using (Pen pen = new Pen(Color.FromArgb(225, 230, 235), 1.8f))
            {
                graphics.DrawEllipse(pen, center.X - 4, center.Y - 4, 8, 8);
                for (int i = 0; i < 8; i++)
                {
                    double angle = Math.PI * i / 4.0;
                    int x1 = center.X + (int)Math.Round(Math.Cos(angle) * 7);
                    int y1 = center.Y + (int)Math.Round(Math.Sin(angle) * 7);
                    int x2 = center.X + (int)Math.Round(Math.Cos(angle) * 10);
                    int y2 = center.Y + (int)Math.Round(Math.Sin(angle) * 10);
                    graphics.DrawLine(pen, x1, y1, x2, y2);
                }
            }
        }

        private void OnTrayMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if ((DateTime.Now - lastAutomaticHide).TotalMilliseconds < 350)
                return;
            if (Visible) Hide();
            else BeginInvoke((MethodInvoker)ShowFlyout);
        }

        private void OnFlyoutMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (updateButton.Contains(e.Location))
            {
                OpenAvailableUpdate();
                return;
            }
            if (refreshButton.Contains(e.Location))
            {
                RefreshAll(true);
                return;
            }
            if (settingsButton.Contains(e.Location))
            {
                trayMenu.Show(this, new Point(settingsButton.Right,
                    settingsButton.Bottom));
                return;
            }
            if (addAccountButton.Contains(e.Location))
            {
                ShowAddAccountDialog();
                return;
            }
            for (int i = 0; i < logoutButtons.Length; i++)
            {
                if (logoutButtons[i].Contains(e.Location))
                {
                    ConfirmLogout(i);
                    return;
                }
            }
        }

        private void OnFlyoutMouseMove(object sender, MouseEventArgs e)
        {
            bool hand = updateButton.Contains(e.Location) ||
                refreshButton.Contains(e.Location) ||
                settingsButton.Contains(e.Location) ||
                addAccountButton.Contains(e.Location);
            if (!hand)
            {
                for (int i = 0; i < logoutButtons.Length && !hand; i++)
                    hand = logoutButtons[i].Contains(e.Location);
            }
            Cursor = hand ? Cursors.Hand : Cursors.Default;
        }

        // "로그아웃" removes the account entirely — credentials, ini entry,
        // and its row in the list. Kept as a live in-place removal (not a
        // restart-requiring one) by using Lists instead of fixed arrays for
        // all the per-account tracking state, so indices stay aligned.
        private void ConfirmLogout(int index)
        {
            AccountConfig config = settings.Accounts[index];
            DialogResult result = MessageBox.Show(
                "\"" + config.Label + "\" 계정을 삭제할까요? " +
                "이 계정의 로그인 정보와 목록에서의 항목이 모두 삭제됩니다.",
                "Token Bar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;

            try
            {
                string claudeDir = AccountPaths.ClaudeDir(config.Id);
                string codexDir = AccountPaths.CodexDir(config.Id);
                if (Directory.Exists(claudeDir)) Directory.Delete(claudeDir, true);
                if (Directory.Exists(codexDir)) Directory.Delete(codexDir, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Token Bar", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            try { AppSettings.RemoveAccount(AccountSetupForm.IniPath(), config.Id); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Token Bar", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            settings.Accounts.RemoveAt(index);
            accountSnapshots.RemoveAt(index);
            collectingAccount.RemoveAt(index);
            lastAccountAttempt.RemoveAt(index);
            ResizeAndRedraw();
        }

        private void ShowFlyout()
        {
            PositionAboveNotificationArea();
            Show();
            BringToFront();
            Activate();
            openedAt = DateTime.Now;
            Invalidate();
        }

        private void PositionAboveNotificationArea()
        {
            AppBarData data = new AppBarData();
            data.cbSize = Marshal.SizeOf(data);
            if (SHAppBarMessage(AbmGetTaskbarPos, ref data) == IntPtr.Zero)
            {
                Rectangle working = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(working.Right - Width - 12,
                    working.Bottom - Height - 12);
                return;
            }

            Rectangle taskbar = Rectangle.FromLTRB(
                data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
            int x;
            int y;
            if (data.uEdge == AbEdgeBottom)
            {
                x = taskbar.Right - Width - 12;
                y = taskbar.Top - Height - 12;
            }
            else if (data.uEdge == AbEdgeTop)
            {
                x = taskbar.Right - Width - 12;
                y = taskbar.Bottom + 12;
            }
            else if (data.uEdge == AbEdgeLeft)
            {
                x = taskbar.Right + 12;
                y = taskbar.Bottom - Height - 12;
            }
            else
            {
                x = taskbar.Left - Width - 12;
                y = taskbar.Bottom - Height - 12;
            }

            Rectangle bounds = Screen.FromRectangle(taskbar).Bounds;
            x = Math.Max(bounds.Left + 8, Math.Min(x, bounds.Right - Width - 8));
            y = Math.Max(bounds.Top + 8, Math.Min(y, bounds.Bottom - Height - 8));
            Location = new Point(x, y);
        }

        private void RefreshAll(bool force)
        {
            RequestUpdateCheck();
            for (int i = 0; i < settings.Accounts.Count; i++)
                RequestAccountRefresh(i, force);
        }

        private void RequestAccountRefresh(int index, bool force)
        {
            if (collectingAccount[index]) return;
            TimeSpan age = DateTime.Now - lastAccountAttempt[index];
            if (!force && lastAccountAttempt[index] != DateTime.MinValue &&
                age.TotalMinutes < settings.ClaudeRefreshMinutes)
                return;

            collectingAccount[index] = true;
            lastAccountAttempt[index] = DateTime.Now;
            Invalidate();
            AccountConfig account = settings.Accounts[index];
            string claudeDir = AccountPaths.ClaudeDir(account.Id);
            string codexDir = AccountPaths.CodexDir(account.Id);
            ThreadPool.QueueUserWorkItem(delegate
            {
                // Re-check the credential folders fresh from disk every time,
                // rather than trusting cached state — this is the only source
                // of truth for "connected", so it can never drift.
                bool hasClaude = HasClaudeCredential(claudeDir);
                bool hasCodex = HasCodexCredential(codexDir);
                ProviderUsage accountClaude = hasClaude
                    ? ClaudeUsageCollector.Collect(claudeDir)
                    : new ProviderUsage { Name = "Claude" };
                UsageSnapshot updated = hasCodex
                    ? CodexUsageReader.Read(accountClaude, codexDir)
                    : new UsageSnapshot { Claude = accountClaude };
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        // A transient fetch failure (CLI timeout, slow start
                        // right after login, etc.) must not erase the last
                        // good values — only replace a provider's data when
                        // the new fetch actually returned something.
                        UsageSnapshot previous = accountSnapshots[index].Snapshot;
                        if (hasClaude && updated.Claude.Buckets.Count == 0 &&
                            previous.Claude.Buckets.Count > 0)
                            updated.Claude = previous.Claude;
                        if (hasCodex && updated.Codex.Buckets.Count == 0 &&
                            previous.Codex.Buckets.Count > 0)
                            updated.Codex = previous.Codex;

                        accountSnapshots[index].Snapshot = updated;
                        accountSnapshots[index].ClaudeConnected = hasClaude;
                        accountSnapshots[index].CodexConnected = hasCodex;
                        collectingAccount[index] = false;
                        ResizeAndRedraw();
                    });
                }
                catch { collectingAccount[index] = false; }
            });
        }

        // Different Claude CLI versions have stored the live session under
        // different filenames (.credentials.json historically, .claude.json
        // more recently) — check both rather than assuming one.
        internal static bool HasClaudeCredential(string dir)
        {
            return File.Exists(Path.Combine(dir, ".credentials.json")) ||
                File.Exists(Path.Combine(dir, ".claude.json"));
        }

        internal static bool HasCodexCredential(string dir)
        {
            return File.Exists(Path.Combine(dir, "auth.json"));
        }

        private void RequestUpdateCheck()
        {
            DateTime now = DateTime.UtcNow;
            if (checkingUpdates ||
                (lastUpdateCheckRequestUtc != DateTime.MinValue &&
                 now >= lastUpdateCheckRequestUtc &&
                 now - lastUpdateCheckRequestUtc < UpdateChecker.CheckInterval))
                return;

            checkingUpdates = true;
            lastUpdateCheckRequestUtc = now;
            string currentVersion = AppVersion.Read();
            string statePath = Path.Combine(
                RuntimePaths.BaseDirectory, "update-state.ini");
            UpdateChecker.BeginCheck(
                delegate { return UpdateChecker.Check(currentVersion, statePath); },
                delegate(UpdateCheckOutcome outcome)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            checkingUpdates = false;
                            ApplyUpdateOutcome(outcome);
                        });
                    }
                    catch { checkingUpdates = false; }
                });
        }

        private void ApplyUpdateOutcome(UpdateCheckOutcome outcome)
        {
            availableUpdate = outcome == null ? null : outcome.Update;
            if (availableUpdate != null)
            {
                Icon previousUpdateIcon = updateIcon;
                updateIcon = CreateTrayIcon(true);
                trayIcon.Icon = updateIcon;
                if (previousUpdateIcon != null) previousUpdateIcon.Dispose();
                if (outcome.ShouldNotify)
                {
                    trayIcon.BalloonTipTitle = "Token Bar 업데이트";
                    trayIcon.BalloonTipText =
                        "v" + availableUpdate.Version + " 버전을 사용할 수 있습니다.";
                    trayIcon.BalloonTipIcon = ToolTipIcon.Info;
                    trayIcon.ShowBalloonTip(5000);
                }
            }
            else
            {
                trayIcon.Icon = applicationIcon;
                if (updateIcon != null)
                {
                    updateIcon.Dispose();
                    updateIcon = null;
                }
            }
            UpdatePresentation();
        }

        private void OpenAvailableUpdate()
        {
            if (availableUpdate == null ||
                !UpdateChecker.IsAllowedReleaseUrl(availableUpdate.ReleaseUrl))
                return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = availableUpdate.ReleaseUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private List<AccountState> BuildAccountStates()
        {
            List<AccountState> states = new List<AccountState>(accountSnapshots.Count);
            for (int i = 0; i < accountSnapshots.Count; i++)
            {
                AccountSnapshot snap = accountSnapshots[i];
                states.Add(new AccountState(snap.ClaudeConnected, snap.CodexConnected));
            }
            return states;
        }

        private void ApplyHeight(int height)
        {
            MinimumSize = new Size(400, 0);
            MaximumSize = new Size(400, short.MaxValue);
            Height = height;
            MinimumSize = new Size(400, height);
            MaximumSize = new Size(400, height);
        }

        private void ResizeAndRedraw()
        {
            ApplyHeight(PreferredHeight(settings.ShowCodexFiveHour, BuildAccountStates()));
            if (Visible) PositionAboveNotificationArea();
            ApplyRoundedRegion();
            UpdatePresentation();
        }

        private void UpdatePresentation()
        {
            Invalidate();
            string tooltip = BuildTrayTooltip(accountSnapshots);
            try { trayIcon.Text = tooltip.Length > 63 ?
                tooltip.Substring(0, 63) : tooltip; }
            catch (ArgumentException) { trayIcon.Text = "Token Bar"; }
        }

        internal static string BuildTrayTooltip(IList<AccountSnapshot> accounts)
        {
            if (accounts.Count == 0) return "Token Bar\n계정이 없습니다";
            StringBuilder tooltip = new StringBuilder("Token Bar");
            foreach (AccountSnapshot account in accounts)
            {
                tooltip.Append("\n").Append(account.Label).Append(" ")
                    .Append(Percent(AccountMinRemaining(account.Snapshot)));
            }
            return tooltip.ToString();
        }

        private static double? AccountMinRemaining(UsageSnapshot snapshot)
        {
            double? codex = snapshot.Codex == null ? null : snapshot.Codex.RemainingPercent;
            double? claude = snapshot.Claude == null ? null : snapshot.Claude.RemainingPercent;
            if (!codex.HasValue) return claude;
            if (!claude.HasValue) return codex;
            return Math.Min(codex.Value, claude.Value);
        }

        internal static bool ShouldShowFiveHour(string providerName,
            bool showCodexFiveHour)
        {
            return !string.Equals(providerName, "Codex",
                StringComparison.OrdinalIgnoreCase) || showCodexFiveHour;
        }

        // A disconnected provider is never drawn at all — no placeholder,
        // no dead-end button. The only entry point is the persistent
        // "+ 계정 추가" row, which merges into an existing account whenever
        // the same email is entered again.
        internal struct AccountState
        {
            public readonly bool ClaudeConnected;
            public readonly bool CodexConnected;

            public AccountState(bool claudeConnected, bool codexConnected)
            {
                ClaudeConnected = claudeConnected;
                CodexConnected = codexConnected;
            }
        }

        internal const int TopMargin = 60;
        internal const int AddAccountRowHeight = 22;
        internal const int AccountLabelHeight = 32;
        internal const int AccountGap = 22;
        internal const int ContentTop = TopMargin + AddAccountRowHeight + AccountGap;
        internal const int ClaudeCardHeight = 112;
        internal const int EmptyStateHeight = 40;

        private static int CodexCardHeight(bool showCodexFiveHour)
        {
            return showCodexFiveHour ? 112 : 78;
        }

        internal static int AccountContentHeight(AccountState state, bool showCodexFiveHour)
        {
            int height = 0;
            if (state.CodexConnected) height += CodexCardHeight(showCodexFiveHour);
            if (state.ClaudeConnected)
                height += (state.CodexConnected ? 10 : 0) + ClaudeCardHeight;
            return height;
        }

        internal static int AccountBlockHeight(AccountState state, bool showCodexFiveHour)
        {
            return AccountLabelHeight + AccountContentHeight(state, showCodexFiveHour) + AccountGap;
        }

        internal static int AccountLabelTop(int index, bool showCodexFiveHour,
            IList<AccountState> accounts)
        {
            int top = ContentTop;
            for (int i = 0; i < index; i++)
                top += AccountBlockHeight(accounts[i], showCodexFiveHour);
            return top;
        }

        internal static Rectangle AccountCodexSlotBounds(int index, bool showCodexFiveHour,
            IList<AccountState> accounts)
        {
            int top = AccountLabelTop(index, showCodexFiveHour, accounts) + AccountLabelHeight;
            return new Rectangle(16, top, 368, CodexCardHeight(showCodexFiveHour));
        }

        internal static Rectangle AccountClaudeSlotBounds(int index, bool showCodexFiveHour,
            IList<AccountState> accounts)
        {
            int top = AccountLabelTop(index, showCodexFiveHour, accounts) + AccountLabelHeight;
            if (accounts[index].CodexConnected)
                top += CodexCardHeight(showCodexFiveHour) + 10;
            return new Rectangle(16, top, 368, ClaudeCardHeight);
        }

        internal static int FooterTop(bool showCodexFiveHour, IList<AccountState> accounts)
        {
            if (accounts.Count == 0) return ContentTop + EmptyStateHeight + AccountGap;
            int y = ContentTop;
            for (int i = 0; i < accounts.Count; i++)
                y += AccountBlockHeight(accounts[i], showCodexFiveHour);
            return y;
        }

        internal static int PreferredHeight(bool showCodexFiveHour, IList<AccountState> accounts)
        {
            return FooterTop(showCodexFiveHour, accounts) + 35;
        }

        private string LatestAge()
        {
            DateTime latest = DateTime.MinValue;
            foreach (AccountSnapshot account in accountSnapshots)
            {
                UsageSnapshot snap = account.Snapshot;
                if (snap.Codex != null && snap.Codex.CollectedAt.HasValue &&
                    snap.Codex.CollectedAt.Value > latest)
                    latest = snap.Codex.CollectedAt.Value;
                if (snap.Claude != null && snap.Claude.CollectedAt.HasValue &&
                    snap.Claude.CollectedAt.Value > latest)
                    latest = snap.Claude.CollectedAt.Value;
            }
            return latest == DateTime.MinValue ? "just now" : RelativeAge(latest);
        }

        private static string Percent(double? remaining)
        {
            return remaining.HasValue
                ? Math.Round(remaining.Value).ToString("0", CultureInfo.InvariantCulture) + "%"
                : "--";
        }

        private static string ResetText(UsageBucket bucket)
        {
            if (bucket == null || !bucket.ResetsAt.HasValue) return "";
            TimeSpan left = bucket.ResetsAt.Value - DateTime.Now;
            if (left.TotalMinutes <= 1) return "곧 초기화";
            if (left.TotalDays >= 1)
                return ((int)left.TotalDays).ToString(CultureInfo.InvariantCulture) +
                    "d " + left.Hours.ToString(CultureInfo.InvariantCulture) + "h";
            if (left.TotalHours >= 1)
                return ((int)left.TotalHours).ToString(CultureInfo.InvariantCulture) +
                    "h " + left.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
            return Math.Max(1, (int)left.TotalMinutes).ToString(
                CultureInfo.InvariantCulture) + "m";
        }

        internal static UsageBucket FindBucket(ProviderUsage provider, int windowMinutes,
            int fallbackIndex)
        {
            if (provider == null) return null;
            bool hasWindowMetadata = false;
            foreach (UsageBucket bucket in provider.Buckets)
            {
                if (bucket.WindowMinutes.HasValue) hasWindowMetadata = true;
                if (bucket.WindowMinutes == windowMinutes)
                    return bucket;
            }
            if (hasWindowMetadata) return null;
            return fallbackIndex >= 0 && fallbackIndex < provider.Buckets.Count
                ? provider.Buckets[fallbackIndex] : null;
        }

        private static Color StatusColor(double remaining)
        {
            if (remaining <= 10) return Color.FromArgb(255, 89, 94);
            if (remaining <= 25) return Color.FromArgb(255, 159, 67);
            if (remaining <= 50) return Color.FromArgb(250, 202, 70);
            return Color.FromArgb(72, 201, 130);
        }

        private static string RelativeAge(DateTime value)
        {
            TimeSpan age = DateTime.Now - value;
            if (age.TotalSeconds < 90) return "just now";
            if (age.TotalMinutes < 60)
                return Math.Max(1, (int)age.TotalMinutes) + "m ago";
            if (age.TotalHours < 24) return (int)age.TotalHours + "h ago";
            return (int)age.TotalDays + "d ago";
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.Items.Add("Token Bar 열기", null, delegate { ShowFlyout(); });
            menu.Items.Add("지금 새로고침", null, delegate { RefreshAll(true); });
            ToolStripMenuItem startup = new ToolStripMenuItem("Windows 시작 시 실행");
            startup.CheckOnClick = true;
            startup.Checked = StartupRegistration.IsEnabled();
            startup.Click += delegate
            {
                try { StartupRegistration.SetEnabled(startup.Checked); }
                catch (Exception ex)
                {
                    startup.Checked = !startup.Checked;
                    MessageBox.Show(ex.Message, "Token Bar", MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("계정 추가...", null, delegate { ShowAddAccountDialog(); });
            menu.Items.Add("계정 설정 파일 열기", null, delegate { OpenSettingsFile(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("끝내기", null, delegate { Close(); });
            return menu;
        }

        // Adding an account never needs a restart: a merge (same email,
        // second provider) only touches an id already tracked in memory,
        // and a genuinely new id just grows the same Lists ConfirmLogout
        // already knows how to shrink.
        private void ShowAddAccountDialog()
        {
            using (AccountSetupForm dialog = new AccountSetupForm())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
            }

            AppSettings reloaded = AppSettings.Load(AccountSetupForm.IniPath());
            foreach (AccountConfig account in reloaded.Accounts)
            {
                int existingIndex = settings.Accounts.FindIndex(
                    delegate(AccountConfig a) { return a.Id == account.Id; });
                if (existingIndex >= 0)
                {
                    settings.Accounts[existingIndex].Label = account.Label;
                    accountSnapshots[existingIndex].Label = account.Label;
                    continue;
                }
                settings.Accounts.Add(account);
                accountSnapshots.Add(new AccountSnapshot { Label = account.Label });
                collectingAccount.Add(false);
                lastAccountAttempt.Add(DateTime.MinValue);
            }

            RefreshAll(true);
            ResizeAndRedraw();
        }

        private static void OpenSettingsFile()
        {
            string path = Path.Combine(RuntimePaths.BaseDirectory, "tokenbar.ini");
            try
            {
                if (!File.Exists(path))
                {
                    File.WriteAllText(path,
                        "# Token Bar settings\r\n" +
                        "# ClaudeRefreshMinutes=15\r\n" +
                        "# ShowCodexFiveHour=false\r\n" +
                        "#\r\n" +
                        "# 계정은 트레이 메뉴의 \"계정 추가...\"로 등록하세요 — 이름과 로그인만\r\n" +
                        "# 하면 아래 두 줄이 자동으로 추가됩니다. 계정 폴더 경로는 직접 적을\r\n" +
                        "# 필요 없이 계정 이름에서 자동으로 정해집니다.\r\n" +
                        "# Accounts=company\r\n" +
                        "# Account.company.Label=회사 계정\r\n",
                        Encoding.UTF8);
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = "\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Token Bar", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private static Icon CreateTrayIcon(bool hasUpdate)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (SolidBrush background = new SolidBrush(Color.FromArgb(46, 50, 57)))
            using (Pen border = new Pen(Color.FromArgb(100, 210, 216, 224)))
            using (Font iconFont = new Font("Segoe UI", 10.0f, FontStyle.Bold,
                GraphicsUnit.Pixel))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                graphics.FillEllipse(background, 1, 1, 29, 29);
                graphics.DrawEllipse(border, 1, 1, 29, 29);
                TextRenderer.DrawText(graphics, "C", iconFont,
                    new Rectangle(4, 7, 13, 17), Color.FromArgb(70, 139, 255),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
                TextRenderer.DrawText(graphics, "A", iconFont,
                    new Rectangle(16, 7, 13, 17), Color.FromArgb(255, 126, 51),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding);
                if (hasUpdate)
                {
                    using (SolidBrush update = new SolidBrush(Color.FromArgb(255, 159, 67)))
                    using (Pen updateBorder = new Pen(Color.FromArgb(255, 245, 247, 250), 1.2f))
                    {
                        graphics.FillEllipse(update, 23, 1, 8, 8);
                        graphics.DrawEllipse(updateBorder, 23, 1, 8, 8);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                        return (Icon)temporary.Clone();
                }
                finally { DestroyIcon(handle); }
            }
        }

        private void ApplyRoundedRegion()
        {
            using (GraphicsPath path = RoundedRectangle(
                new Rectangle(0, 0, Width, Height), 16))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter,
                diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter,
                diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public Rect rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }

    internal static class StartupRegistration
    {
        private const string RunKey =
            @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TokenBar";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return key != null && key.GetValue(ValueName) != null;
            }
            catch { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null)
                    throw new InvalidOperationException(
                        "시작프로그램 설정을 열 수 없습니다.");
                if (enabled)
                {
                    string command =
                        Environment.GetEnvironmentVariable("TOKENBAR_START_COMMAND");
                    if (string.IsNullOrEmpty(command))
                        command = "\"" + Application.ExecutablePath + "\"";
                    key.SetValue(ValueName, command);
                }
                else key.DeleteValue(ValueName, false);
            }
        }
    }
}
