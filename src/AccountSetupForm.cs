using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TokenBar
{
    internal sealed class AccountSetupForm : Form
    {
        private readonly TextBox emailBox;
        private readonly Label claudeStatus;
        private readonly Label codexStatus;
        private string accountId;

        public AccountSetupForm()
        {
            Text = "계정 추가";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(380, 280);
            Font = new Font("Segoe UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point);

            Label nameLabel = new Label
            {
                Text = "이메일 주소",
                Location = new Point(16, 16),
                AutoSize = true
            };
            emailBox = new TextBox
            {
                Location = new Point(16, 38),
                Width = 348
            };
            Label mergeHint = new Label
            {
                Text = "같은 이메일로 로그인하면 한 계정으로 합쳐집니다.",
                Location = new Point(16, 64),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.0f, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.DimGray
            };

            Label claudeLabel = new Label
            {
                Text = "1) Claude 로그인",
                Location = new Point(16, 92),
                AutoSize = true
            };
            Button claudeButton = new Button
            {
                Text = "Claude 로그인 창 열기",
                Location = new Point(16, 112),
                Width = 348,
                Height = 30
            };
            claudeStatus = new Label
            {
                Text = "",
                Location = new Point(16, 144),
                AutoSize = true,
                ForeColor = Color.DimGray
            };
            claudeButton.Click += delegate { OpenLogin("claude", "/login",
                "CLAUDE_CONFIG_DIR", ClaudeDir(), claudeStatus); };

            Label codexLabel = new Label
            {
                Text = "2) Codex 로그인 (Codex 안 쓰면 건너뛰어도 됩니다)",
                Location = new Point(16, 166),
                AutoSize = true
            };
            Button codexButton = new Button
            {
                Text = "Codex 로그인 창 열기",
                Location = new Point(16, 186),
                Width = 348,
                Height = 30
            };
            codexStatus = new Label
            {
                Text = "",
                Location = new Point(16, 218),
                AutoSize = true,
                ForeColor = Color.DimGray
            };
            codexButton.Click += delegate { OpenLogin("codex", "login",
                "CODEX_HOME", CodexDir(), codexStatus); };

            Button saveButton = new Button
            {
                Text = "저장",
                Location = new Point(196, 244),
                Width = 84,
                Height = 28
            };
            saveButton.Click += OnSave;
            Button cancelButton = new Button
            {
                Text = "취소",
                Location = new Point(288, 244),
                Width = 76,
                Height = 28,
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(nameLabel);
            Controls.Add(emailBox);
            Controls.Add(mergeHint);
            Controls.Add(claudeLabel);
            Controls.Add(claudeButton);
            Controls.Add(claudeStatus);
            Controls.Add(codexLabel);
            Controls.Add(codexButton);
            Controls.Add(codexStatus);
            Controls.Add(saveButton);
            Controls.Add(cancelButton);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        // Reuses an existing account's id whenever the typed email matches
        // one already on record — that's what makes logging Codex in under
        // an email, then Claude under the same email later, merge into one
        // card instead of creating a second account.
        private string EnsureAccountId()
        {
            if (string.IsNullOrEmpty(accountId))
            {
                string email = emailBox.Text.Trim();
                List<AccountConfig> existingAccounts = AppSettings.Load(IniPath()).Accounts;
                List<string> existingIds = new List<string>();
                foreach (AccountConfig account in existingAccounts)
                {
                    existingIds.Add(account.Id);
                    if (accountId == null && string.Equals(account.Label, email,
                        StringComparison.OrdinalIgnoreCase))
                        accountId = account.Id;
                }
                if (accountId == null)
                    accountId = AppSettings.SlugifyAccountId(email, existingIds);
            }
            return accountId;
        }

        internal static string IniPath()
        {
            return Path.Combine(RuntimePaths.BaseDirectory, "tokenbar.ini");
        }

        private string ClaudeDir()
        {
            return AccountPaths.ClaudeDir(EnsureAccountId());
        }

        private string CodexDir()
        {
            return AccountPaths.CodexDir(EnsureAccountId());
        }

        private void OpenLogin(string exeName, string loginArgs, string envVarName,
            string configDir, Label statusLabel)
        {
            if (emailBox.Text.Trim().Length == 0)
            {
                MessageBox.Show("이메일 주소를 먼저 입력하세요.", "Token Bar",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                OpenLoginWindow(exeName, loginArgs, envVarName, configDir);
                statusLabel.Text = "로그인 창을 열었습니다. 완료되면 창이 자동으로 닫힙니다.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Token Bar", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        // On success the window closes itself (`exit`); on failure it stays
        // open with a message so the error is still visible.
        internal static void OpenLoginWindow(string exeName, string loginArgs,
            string envVarName, string configDir)
        {
            Directory.CreateDirectory(configDir);
            string command = "set " + envVarName + "=" + configDir +
                "&& " + exeName + " " + loginArgs + " && exit || " +
                "(echo. & echo 로그인이 완료되지 않았습니다. & " +
                "echo 창을 닫으려면 아무 키나 누르세요. & pause>nul)";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k \"" + command.Replace("\"", "\"\"") + "\"",
                UseShellExecute = true,
                WorkingDirectory = configDir
            });
        }

        private void OnSave(object sender, EventArgs e)
        {
            string label = emailBox.Text.Trim();
            if (label.Length == 0)
            {
                MessageBox.Show("이메일 주소를 입력하세요.", "Token Bar",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string id = EnsureAccountId();
            bool hasClaude = TokenBarForm.HasClaudeCredential(ClaudeDir());
            bool hasCodex = TokenBarForm.HasCodexCredential(CodexDir());
            if (!hasClaude && !hasCodex)
            {
                MessageBox.Show(
                    "Claude 또는 Codex 로그인을 먼저 완료해야 저장할 수 있습니다.",
                    "Token Bar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            AppSettings.AddAccount(IniPath(), id, label);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
