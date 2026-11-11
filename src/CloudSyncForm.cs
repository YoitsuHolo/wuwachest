// CloudSyncForm.cs — 云同步设置/操作对话框（GitHub 私有仓库）
using System;
using System.Drawing;
using System.Windows.Forms;

namespace WuwaChest
{
    internal sealed class CloudSyncForm : Form
    {
        private readonly AppConfig _cfg;
        private readonly string _dataDir;
        private readonly string _configPath;

        private TextBox _repo;
        private TextBox _branch;
        private TextBox _folder;
        private TextBox _proxy;
        private TextBox _token;
        private CheckBox _autoSync;
        private TextBox _syncMinutes;
        private Label _status;

        public CloudSyncForm(AppConfig cfg, string dataDir, string configPath)
        {
            _cfg = cfg;
            _dataDir = dataDir;
            _configPath = configPath;

            Text = "云同步（GitHub 私有仓库）";
            ClientSize = new Size(470, 400);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(255, 252, 246);
            Font = new Font("Microsoft YaHei UI", 9f);

            int y = 16;
            y = AddRow("仓库（owner/repo）", out _repo, cfg.CloudRepo, y);
            y = AddRow("分支", out _branch, string.IsNullOrEmpty(cfg.CloudBranch) ? "main" : cfg.CloudBranch, y);
            y = AddRow("云端目录", out _folder, string.IsNullOrEmpty(cfg.CloudFolder) ? "wuwachest" : cfg.CloudFolder, y);
            y = AddRow("代理（可选）", out _proxy, cfg.CloudProxy, y);
            y = AddRow("Token（只存本机）", out _token, "", y);
            _token.UseSystemPasswordChar = true;

            y = AddRow("同步间隔（分钟）", out _syncMinutes, cfg.CloudSyncMinutes < 1 ? "30" : cfg.CloudSyncMinutes.ToString(), y);

            _autoSync = new CheckBox
            {
                Text = "定时自动同步（先拉取、再上传）",
                Checked = cfg.CloudAutoSync,
                AutoSize = true,
                Location = new Point(20, y),
                BackColor = Color.Transparent
            };
            Controls.Add(_autoSync);
            y += _autoSync.Height + 8;

            var hint = new Label
            {
                Text = "Token 需要该私有仓库的 Contents 读写权限；保存时用 DPAPI 加密，不会写进 config.json。",
                ForeColor = Color.FromArgb(140, 123, 106),
                AutoSize = false,
                Width = 430,
                Height = 34,
                Location = new Point(20, y),
                BackColor = Color.Transparent
            };
            Controls.Add(hint);
            y += 40;

            var saveButton = MakeButton("保存设置", 96, delegate { SaveSettings(); });
            saveButton.Location = new Point(20, y);
            var uploadButton = MakeButton("上传到云", 96, delegate { RunUpload(); });
            uploadButton.Location = new Point(126, y);
            var pullButton = MakeButton("从云拉取", 96, delegate { RunPull(); });
            pullButton.Location = new Point(232, y);
            var closeButton = MakeButton("关闭", 84, delegate { Close(); });
            closeButton.Location = new Point(338, y);
            Controls.Add(saveButton);
            Controls.Add(uploadButton);
            Controls.Add(pullButton);
            Controls.Add(closeButton);
            y += 42;

            _status = new Label
            {
                Text = CloudSync.HasToken(_dataDir) ? "Token 已保存（本机）" : "还没有保存 Token",
                ForeColor = Color.FromArgb(142, 94, 67),
                AutoSize = false,
                Width = 430,
                Height = 40,
                Location = new Point(20, y),
                BackColor = Color.Transparent
            };
            Controls.Add(_status);
        }

        private int AddRow(string label, out TextBox box, string value, int y)
        {
            var caption = new Label
            {
                Text = label,
                AutoSize = false,
                Width = 150,
                Height = 24,
                Location = new Point(20, y + 4),
                ForeColor = Color.FromArgb(142, 94, 67),
                BackColor = Color.Transparent
            };
            box = new TextBox
            {
                Width = 270,
                Height = 26,
                Location = new Point(176, y),
                Text = value ?? "",
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(caption);
            Controls.Add(box);
            return y + 36;
        }

        private static Button MakeButton(string text, int width, EventHandler onClick)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(255, 253, 248),
                ForeColor = Color.FromArgb(79, 58, 44),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(214, 198, 174);
            button.Click += onClick;
            return button;
        }

        private void SaveSettings()
        {
            _cfg.CloudRepo = _repo.Text.Trim();
            _cfg.CloudBranch = string.IsNullOrEmpty(_branch.Text.Trim()) ? "main" : _branch.Text.Trim();
            _cfg.CloudFolder = string.IsNullOrEmpty(_folder.Text.Trim()) ? "wuwachest" : _folder.Text.Trim();
            _cfg.CloudProxy = (_proxy == null ? "" : _proxy.Text.Trim());
            _cfg.CloudAutoSync = (_autoSync != null) && _autoSync.Checked;
            int syncMin;
            if (!int.TryParse((_syncMinutes == null ? "" : _syncMinutes.Text.Trim()), out syncMin) || syncMin < 1) syncMin = 30;
            _cfg.CloudSyncMinutes = syncMin;
            try
            {
                _cfg.Save(_configPath);
                if (_token.Text.Trim().Length > 0) CloudSync.SaveToken(_dataDir, _token.Text.Trim());
                _status.Text = "已保存" + (CloudSync.HasToken(_dataDir) ? "（Token 已加密存到本机）" : "");
            }
            catch (Exception ex)
            {
                _status.Text = "保存失败：" + ex.Message;
            }
        }

        private void RunUpload()
        {
            SaveSettings();
            _status.Text = "正在上传…";
            Application.DoEvents();
            try { _status.Text = CloudSync.Upload(_cfg, _dataDir); }
            catch (Exception ex) { _status.Text = "上传失败：" + ex.Message; }
        }

        private void RunPull()
        {
            SaveSettings();
            _status.Text = "正在拉取…";
            Application.DoEvents();
            try { _status.Text = CloudSync.Pull(_cfg, _dataDir); }
            catch (Exception ex) { _status.Text = "拉取失败：" + ex.Message; }
        }
    }
}
