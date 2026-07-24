using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
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

        private UsageSnapshot snapshot = new UsageSnapshot();
        private ProviderUsage claude =
            new ProviderUsage { Name = "Claude", Error = "갱신 중…" };
        private bool collectingClaude;
        private DateTime lastClaudeAttempt = DateTime.MinValue;
        private DateTime lastAutomaticHide = DateTime.MinValue;
        private DateTime openedAt = DateTime.MinValue;
        private Rectangle refreshButton;
        private Rectangle settingsButton;

        public TokenBarForm(AppSettings settings)
        {
            this.settings = settings;
            Text = "Token Bar";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Width = 400;
            Height = 340;
            MinimumSize = new Size(400, 340);
            MaximumSize = new Size(400, 340);
            BackColor = Color.FromArgb(29, 31, 35);
            DoubleBuffered = true;
            Opacity = 0;

            titleFont = new Font("Segoe UI", 15.0f, FontStyle.Bold, GraphicsUnit.Point);
            providerFont = new Font("Segoe UI", 10.0f, FontStyle.Bold, GraphicsUnit.Point);
            valueFont = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
            smallFont = new Font("Segoe UI", 8.0f, FontStyle.Regular, GraphicsUnit.Point);
            Font = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);

            applicationIcon = CreateTrayIcon();
            trayMenu = BuildMenu();
            trayIcon = new NotifyIcon();
            trayIcon.Icon = applicationIcon;
            trayIcon.Text = "Token Bar";
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.MouseDown += OnTrayMouseDown;

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
            FormClosed += delegate
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayMenu.Dispose();
                applicationIcon.Dispose();
                titleFont.Dispose();
                providerFont.Dispose();
                valueFont.Dispose();
                smallFont.Dispose();
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
                new Rectangle(22, 16, 230, 32), Color.FromArgb(246, 247, 249),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding);

            refreshButton = new Rectangle(326, 17, 28, 28);
            settingsButton = new Rectangle(360, 17, 28, 28);
            DrawRefreshIcon(e.Graphics, refreshButton);
            DrawSettingsIcon(e.Graphics, settingsButton);

            DrawProviderCard(e.Graphics, new Rectangle(16, 60, 368, 112),
                "Codex", snapshot.Codex, Color.FromArgb(70, 139, 255));
            DrawProviderCard(e.Graphics, new Rectangle(16, 182, 368, 112),
                "Claude", snapshot.Claude, Color.FromArgb(255, 126, 51));

            string updated = "Updated " + LatestAge();
            if (collectingClaude) updated += " · Claude 새로고침 중";
            TextRenderer.DrawText(e.Graphics, updated, smallFont,
                new Rectangle(22, 305, 350, 22), Color.FromArgb(170, 178, 188),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private void DrawProviderCard(Graphics graphics, Rectangle area, string name,
            ProviderUsage provider, Color accent)
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

            UsageBucket fiveHour = FindBucket(provider, 300, 0);
            UsageBucket weekly = FindBucket(provider, 10080, 1);
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
            if (refreshButton.Contains(e.Location))
                RefreshAll(true);
            else if (settingsButton.Contains(e.Location))
                trayMenu.Show(this, new Point(settingsButton.Right,
                    settingsButton.Bottom));
        }

        private void OnFlyoutMouseMove(object sender, MouseEventArgs e)
        {
            Cursor = refreshButton.Contains(e.Location) ||
                settingsButton.Contains(e.Location)
                ? Cursors.Hand : Cursors.Default;
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

        private void RefreshAll(bool forceClaude)
        {
            snapshot = CodexUsageReader.Read(claude);
            UpdatePresentation();

            TimeSpan age = DateTime.Now - lastClaudeAttempt;
            if (!collectingClaude &&
                (forceClaude || lastClaudeAttempt == DateTime.MinValue ||
                 age.TotalMinutes >= settings.ClaudeRefreshMinutes))
            {
                collectingClaude = true;
                lastClaudeAttempt = DateTime.Now;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    ProviderUsage updated = ClaudeUsageCollector.Collect();
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            claude = updated;
                            collectingClaude = false;
                            snapshot = CodexUsageReader.Read(claude);
                            UpdatePresentation();
                        });
                    }
                    catch { collectingClaude = false; }
                });
            }
        }

        private void UpdatePresentation()
        {
            Invalidate();
            string tooltip = BuildTrayTooltip();
            try { trayIcon.Text = tooltip.Length > 63 ?
                tooltip.Substring(0, 63) : tooltip; }
            catch (ArgumentException) { trayIcon.Text = "Token Bar"; }
        }

        private string BuildTrayTooltip()
        {
            UsageBucket c5 = FindBucket(snapshot.Codex, 300, 0);
            UsageBucket c7 = FindBucket(snapshot.Codex, 10080, 1);
            UsageBucket a5 = FindBucket(snapshot.Claude, 300, 0);
            UsageBucket a7 = FindBucket(snapshot.Claude, 10080, 1);
            return string.Format(CultureInfo.InvariantCulture,
                "Token Bar\nC 5h {0} · 7d {1}\nA 5h {2} · 7d {3}",
                Percent(c5), Percent(c7), Percent(a5), Percent(a7));
        }

        private string LatestAge()
        {
            DateTime latest = DateTime.MinValue;
            if (snapshot.Codex != null && snapshot.Codex.CollectedAt.HasValue)
                latest = snapshot.Codex.CollectedAt.Value;
            if (snapshot.Claude != null && snapshot.Claude.CollectedAt.HasValue &&
                snapshot.Claude.CollectedAt.Value > latest)
                latest = snapshot.Claude.CollectedAt.Value;
            return latest == DateTime.MinValue ? "just now" : RelativeAge(latest);
        }

        private static string Percent(UsageBucket bucket)
        {
            return bucket != null && bucket.RemainingPercent.HasValue
                ? Math.Round(bucket.RemainingPercent.Value).ToString("0",
                    CultureInfo.InvariantCulture) + "%"
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

        private static UsageBucket FindBucket(ProviderUsage provider, int windowMinutes,
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
            menu.Items.Add("끝내기", null, delegate { Close(); });
            return menu;
        }

        private static Icon CreateTrayIcon()
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
