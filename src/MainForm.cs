// MainForm.cs — 面板：状态 / 领取记录 / 设置，加上托盘与自启
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WuwaChest
{
    internal sealed class MainForm : Form
    {
        private readonly string _dir;
        private readonly string _configPath;
        private readonly string _logsDir;
        private AppConfig _cfg;
        private TextBox _tokenInput;
        private readonly ClaimEngine _engine;

        private readonly Label _dot;
        private readonly Label _monitorText;
        private readonly Label _numberText;
        private readonly Label _numberHint;
        private readonly Label _chestBadge;
        private readonly Label _lastCheck;
        private readonly Label _nextCheck;
        private readonly Label _claims;
        private readonly Label _lastClaim;
        private readonly TextBox _logBox;
        private readonly Label _footer;
        private readonly Label _versionLabel;

        private readonly TabControl _tabs;
        private readonly ListView _claimList;
        private readonly Label _claimSummary;
        private readonly PictureBox _claimPreview;
        private readonly Label _claimPreviewHint;

        private readonly NumericUpDown _setNotReady;
        private readonly NumericUpDown _setBackgroundDim;
        private readonly CheckBox _setNotify;
        private readonly CheckBox _setRewardShot;
        private readonly CheckBox _setCdp;
        private readonly CheckBox _setAutostart;
        private readonly CheckBox _setAutoMonitor;

        private readonly Label _farmChars;
        private readonly Label _farmLabel;
        private RoomService _rooms;
        private Label _roomCurrentText;
        private Label _roomStatusText;
        private FlowLayoutPanel _roomRows;
        private bool _roomBusy;
        private StatsStore _stats;
        private ChartPanel _chart;

        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _trayAutostart;
        private readonly System.Windows.Forms.Timer _timer;
        private TrayWatcher _trayWatcher;

        private readonly List<string> _logLines = new List<string>();
        private readonly object _logLock = new object();
        private bool _hiddenToTray;
        private bool _reallyExit;
        private int _claimsToday;
        private string _lastClaimText = "—";
        private string _lastRenderedTail = "";
        private DateTime _claimsLoadedAt = DateTime.MinValue;
        private bool _settingsLoaded;

        private static readonly Color Cream = Color.FromArgb(251, 243, 230);
        private static readonly Color Ink = Color.FromArgb(90, 66, 39);
        private static readonly Color Muted = Color.FromArgb(140, 120, 96);
        private static readonly Color Good = Color.FromArgb(46, 139, 87);
        private static readonly Color Bad = Color.FromArgb(190, 76, 60);
        private static readonly Color Paper = Color.FromArgb(255, 252, 246);

        private static readonly Regex ClaimPattern = new Regex(
            @"领取成功（本次第 \d+ 个）：字符数 (\d+) -> (\d+)", RegexOptions.Compiled);

        public MainForm(AppConfig cfg, bool startHidden, int startTab)
        {
            _cfg = cfg;
            _dir = Application.StartupPath;
            _configPath = Path.Combine(_dir, "config.json");
            _logsDir = Path.Combine(_dir, "logs");
            _engine = new ClaimEngine(cfg, _logsDir);

            Text = "鸣潮桌宠 · 宝箱助手";
            ClientSize = new Size(480, 644);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Cream;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            try { Icon = AppIcon.Get(32); } catch { }
            if (Icon == null) { try { Icon = CreateTrayIcon(); } catch { } }

            // ---------------- 状态页 ----------------
            var statusPage = new TabPage("状态") { BackColor = Cream, Padding = new Padding(0) };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                BackColor = Color.Transparent,
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(120, 82, 45) };
            _dot = new Label
            {
                Text = "●",
                ForeColor = Bad,
                Font = new Font("Microsoft YaHei UI", 11f),
                AutoSize = true,
                Location = new Point(16, 14),
                BackColor = Color.Transparent
            };
            _monitorText = new Label
            {
                Text = "监控：未启动",
                ForeColor = Color.FromArgb(255, 248, 238),
                Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(38, 12),
                BackColor = Color.Transparent
            };
            header.Controls.Add(_dot);
            header.Controls.Add(_monitorText);

            var card = new GlassPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(14, 8, 14, 4) };
            card.Controls.Add(new Label
            {
                Text = "字符数",
                ForeColor = Muted,
                AutoSize = true,
                Location = new Point(18, 8),
                BackColor = Color.Transparent
            });
            _numberText = new Label
            {
                Text = "—",
                ForeColor = Ink,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 32f, FontStyle.Bold),
                Location = new Point(14, 22),
                BackColor = Color.Transparent
            };
            _numberHint = new Label
            {
                Text = "等待监控进程…",
                ForeColor = Muted,
                AutoSize = false,
                Size = new Size(290, 20),
                Location = new Point(18, 92),
                BackColor = Color.Transparent,
                AutoEllipsis = true
            };
            _chestBadge = new Label
            {
                Text = "宝箱：—",
                AutoSize = false,
                Size = new Size(120, 34),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(330, 40),
                BackColor = Color.FromArgb(238, 228, 212),
                ForeColor = Muted,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
            };
            card.Controls.Add(_numberText);
            card.Controls.Add(_numberHint);
            card.Controls.Add(_chestBadge);

            var info = new GlassTable
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                BackColor = Color.Transparent,
                Margin = new Padding(14, 0, 14, 4),
                Padding = new Padding(14, 4, 14, 4)
            };
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) info.RowStyles.Add(new RowStyle(SizeType.Percent, 25));

            info.Controls.Add(MakeKey("状态更新"), 0, 0);
            _lastCheck = MakeValue("—");
            info.Controls.Add(_lastCheck, 1, 0);
            info.Controls.Add(MakeKey("下次检测"), 0, 1);
            _nextCheck = MakeValue("—");
            info.Controls.Add(_nextCheck, 1, 1);
            info.Controls.Add(MakeKey("今日已领"), 0, 2);
            _claims = MakeValue("0 个");
            info.Controls.Add(_claims, 1, 2);
            info.Controls.Add(MakeKey("最近一次"), 0, 3);
            _lastClaim = MakeValue("—");
            info.Controls.Add(_lastClaim, 1, 3);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(14, 4, 14, 4),
                BackColor = Color.Transparent
            };
            buttons.Controls.Add(MakeButton("开始监控", StartMonitor));
            buttons.Controls.Add(MakeButton("停止监控", StopMonitor));
            buttons.Controls.Add(MakeButton("立即检测", RunOnce));
            buttons.Controls.Add(MakeButton("日志目录", OpenLogs));

            var logPanel = new GlassPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Margin = new Padding(14, 4, 14, 4),
                Padding = new Padding(12, 6, 12, 6)
            };
            logPanel.Controls.Add(new Label
            {
                Text = "最近日志",
                ForeColor = Muted,
                Dock = DockStyle.Top,
                Height = 18,
                BackColor = Color.Transparent
            });
            _logBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Paper,
                ForeColor = Color.FromArgb(110, 92, 70),
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 8f)
            };
            logPanel.Controls.Add(_logBox);

            _footer = new Label
            {
                Text = "单文件程序 · 关闭窗口收进托盘 · 退出请用托盘菜单",
                ForeColor = Muted,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei UI", 8f)
            };

            _versionLabel = new Label
            {
                Text = "v" + Program.Version,
                ForeColor = Muted,
                Dock = DockStyle.Right,
                Width = 56,
                TextAlign = ContentAlignment.MiddleRight,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 0, 8, 0),
                Font = new Font("Microsoft YaHei UI", 8f)
            };

            var footerBar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(246, 236, 222) };
            footerBar.Controls.Add(_footer);
            footerBar.Controls.Add(_versionLabel);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(card, 0, 1);
            root.Controls.Add(info, 0, 2);
            root.Controls.Add(buttons, 0, 3);
            root.Controls.Add(logPanel, 0, 4);
            root.Controls.Add(footerBar, 0, 5);
            statusPage.Controls.Add(root);

            // ---------------- 领取记录页 ----------------
            var historyPage = new TabPage("领取记录") { BackColor = Cream, Padding = new Padding(0) };
            var histLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Color.Transparent
            };
            histLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            histLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            histLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            histLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            histLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

            _claimSummary = new Label
            {
                Text = "今日 0 个 · 累计 0 个",
                Dock = DockStyle.Fill,
                ForeColor = Ink,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 0, 0),
                BackColor = Paper,
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };

            _claimList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MultiSelect = false,
                BackColor = Paper,
                ForeColor = Ink,
                BorderStyle = BorderStyle.None,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                Margin = new Padding(14, 0, 14, 0)
            };
            _claimList.Columns.Add("时间", 132, HorizontalAlignment.Left);
            _claimList.Columns.Add("字符数变化", 120, HorizontalAlignment.Left);
            _claimList.Columns.Add("消耗", 60, HorizontalAlignment.Left);
            _claimList.Columns.Add("奖励", 100, HorizontalAlignment.Left);
            _claimList.SelectedIndexChanged += delegate { ShowSelectedClaim(); };
            _claimList.DoubleClick += delegate { OpenSelectedClaim(); };

            var histButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(14, 4, 14, 4),
                BackColor = Color.Transparent
            };
            histButtons.Controls.Add(MakeButton("刷新记录", delegate { _claimsLoadedAt = DateTime.MinValue; LoadClaims(); }));
            histButtons.Controls.Add(MakeButton("导出 CSV", delegate { ExportClaims(); }));
            histButtons.Controls.Add(MakeButton("日志目录", OpenLogs));

            var previewPanel = new GlassPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Margin = new Padding(14, 0, 14, 4),
                Padding = new Padding(6)
            };
            _claimPreview = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Paper,
                Visible = false
            };
            _claimPreviewHint = new Label
            {
                Text = "选中一条记录查看奖励截图，双击打开原图",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Muted,
                BackColor = Color.Transparent
            };
            previewPanel.Controls.Add(_claimPreview);
            previewPanel.Controls.Add(_claimPreviewHint);

            histLayout.Controls.Add(_claimSummary, 0, 0);
            histLayout.Controls.Add(_claimList, 0, 1);
            histLayout.Controls.Add(previewPanel, 0, 2);
            histLayout.Controls.Add(histButtons, 0, 3);
            historyPage.Controls.Add(histLayout);

            // ---------------- 设置页 ----------------
            var settingsPage = new TabPage("设置") { BackColor = Cream, Padding = new Padding(12, 10, 12, 10) };
            var settingsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(8, 8, 8, 8),
                BackColor = Color.Transparent,
                AutoScroll = true
            };

            _setNotReady = MakeNumber(1, 120, 1, 1, _cfg.NotReadyRetryMinutes);
            _setBackgroundDim = MakeNumber(0, 90, 0, 5, _cfg.BackgroundDim);

            _setNotify = MakeCheck("领取成功后弹桌面通知", _cfg.NotifyOnClaim);
            _setRewardShot = MakeCheck("领取时截图记录奖励", _cfg.RewardScreenshots);
            _setCdp = MakeCheck("优先用 CDP 读页面（需已启用调试端口）", _cfg.CdpEnabled);
            _setAutoMonitor = MakeCheck("程序启动后自动开始监控", _cfg.AutoStartMonitor);
            _setAutostart = MakeCheck("开机自动启动（托盘常驻）", Autostart.IsEnabled(Application.ExecutablePath));

            var backgroundButtons = new FlowLayoutPanel
            {
                Width = 204, Height = 28,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            backgroundButtons.Controls.Add(MakeButton("选图", delegate { ChooseBackground(); }));
            backgroundButtons.Controls.Add(MakeButton("清除", delegate { ClearBackground(); }));

            settingsFlow.Controls.Add(MakeSettingRow("界面重试（分钟）", _setNotReady));
            settingsFlow.Controls.Add(MakeSettingRow("", _setNotify));
            settingsFlow.Controls.Add(MakeSettingRow("", _setRewardShot));
            settingsFlow.Controls.Add(MakeSettingRow("", _setCdp));
            settingsFlow.Controls.Add(MakeSettingRow("", _setAutoMonitor));
            settingsFlow.Controls.Add(MakeSettingRow("", _setAutostart));
            foreach (Control bgChild in backgroundButtons.Controls) { bgChild.Width = 96; bgChild.Margin = new Padding(0, 1, 6, 1); }
            settingsFlow.Controls.Add(MakeSettingRow("面板背景图", backgroundButtons));
            settingsFlow.Controls.Add(MakeSettingRow("背景图浓度", _setBackgroundDim));
            var syncButtons = new FlowLayoutPanel
            {
                Width = 204, Height = 28,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            syncButtons.Controls.Add(MakeButton("导出同步", delegate { ExportSync(); }));
            syncButtons.Controls.Add(MakeButton("导入同步", delegate { ImportSync(); }));
            foreach (Control syncChild in syncButtons.Controls) { syncChild.Width = 96; syncChild.Margin = new Padding(0, 1, 6, 1); }
            settingsFlow.Controls.Add(MakeSettingRow("跨电脑同步", syncButtons));
            var tokenViewButtons = new FlowLayoutPanel
            {
                Width = 204, Height = 28,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            var viewButton = MakeButton("有效期", delegate { ShowTokenExpiry(); });
            viewButton.Width = 96;
            viewButton.Margin = new Padding(0, 1, 6, 1);
            var renewButton = MakeButton("续期", delegate { RenewToken(); });
            renewButton.Width = 96;
            renewButton.Margin = new Padding(0, 1, 6, 1);
            tokenViewButtons.Controls.Add(viewButton);
            tokenViewButtons.Controls.Add(renewButton);
            settingsFlow.Controls.Add(MakeSettingRow("登录 Token", tokenViewButtons));

            var tokenExtractButtons = new FlowLayoutPanel
            {
                Width = 204, Height = 28,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            var extractButton = MakeButton("提取", delegate { CopyToken(); });
            extractButton.Width = 96;
            extractButton.Margin = new Padding(0, 1, 6, 1);
            tokenExtractButtons.Controls.Add(extractButton);
            settingsFlow.Controls.Add(MakeSettingRow("提取 Token", tokenExtractButtons));

            _tokenInput = new TextBox
            {
                Width = 104,
                Height = 26,
                Margin = new Padding(0, 2, 8, 0),
                UseSystemPasswordChar = false,
                BorderStyle = BorderStyle.FixedSingle
            };
            var tokenLoginPanel = new FlowLayoutPanel
            {
                Width = 214, Height = 28,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            tokenLoginPanel.Controls.Add(_tokenInput);
            var loginButton = MakeButton("登录", delegate { LoginWithToken(); });
            loginButton.Width = 96;
            loginButton.Margin = new Padding(0, 1, 6, 1);
            tokenLoginPanel.Controls.Add(loginButton);
            settingsFlow.Controls.Add(MakeSettingRow("Token 登录", tokenLoginPanel));

            var settingsButtons = new FlowLayoutPanel
            {
                Width = 360,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 6, 0, 2)
            };
            settingsButtons.Controls.Add(MakeButton("保存并应用", SaveSettings));
            settingsButtons.Controls.Add(MakeButton("恢复默认", ResetSettings));
            settingsFlow.Controls.Add(settingsButtons);

            settingsFlow.Controls.Add(new Label
            {
                Text = "以上设置会写入 config.json，下次启动继续生效。\r\n高级参数（窗口标题/类名、点击后等待、日志保留天数等）可直接编辑该文件。",
                ForeColor = Muted,
                AutoSize = false,
                Width = 360,
                Height = 56,
                BackColor = Color.Transparent
            });
            var settingsCard = new GlassPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(14, 10, 14, 10)
            };
            settingsCard.Controls.Add(settingsFlow);
            settingsPage.Controls.Add(settingsCard);

            // ---------------- 工具页 ----------------
            var toolsPage = new TabPage("工具") { BackColor = Cream, Padding = new Padding(12, 10, 12, 10) };
            var toolsCard = new GlassPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = new Padding(14, 12, 14, 12)
            };
            var toolsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.Transparent,
                AutoScroll = true
            };

            BuildRoomSection(toolsFlow);

            toolsFlow.Controls.Add(new Label
            {
                Text = "加字符（本地计数）",
                ForeColor = Ink,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                BackColor = Color.Transparent
            });
            toolsFlow.Controls.Add(new Label
            {
                Text = "给桌宠的本地计数加数，它自己会把累计值上报给服务器（几秒后同步）。\r\n" +
                       "不用模拟鼠标、不用点界面；风险提示见 README。",
                ForeColor = Muted,
                AutoSize = true,
                MaximumSize = new Size(400, 0),
                BackColor = Color.Transparent,
                Margin = new Padding(0, 4, 0, 8)
            });

            _farmChars = new Label
            {
                Text = "当前字符数：—",
                ForeColor = Ink,
                AutoSize = true,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 8)
            };
            toolsFlow.Controls.Add(_farmChars);


            var boostButtons = new FlowLayoutPanel
            {
                Width = 356,
                Height = 40,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 10)
            };
            foreach (var amount in new[] { 1000, 5000, 10000 })
            {
                int value = amount;
                boostButtons.Controls.Add(MakeButton("+" + value, delegate { BoostClicks(value); }));
            }
            toolsFlow.Controls.Add(boostButtons);

            _farmLabel = new Label
            {
                Text = "空闲",
                ForeColor = Ink,
                AutoSize = false,
                Width = 356,
                Height = 24,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 2, 0, 8)
            };
            toolsFlow.Controls.Add(_farmLabel);

            toolsCard.Controls.Add(toolsFlow);
            toolsPage.Controls.Add(toolsCard);

            // ---------------- 标签页 ----------------
            _tabs = new CompositedTabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9f) };
            _tabs.TabPages.Add(statusPage);
            _tabs.TabPages.Add(historyPage);
            _tabs.TabPages.Add(settingsPage);
            _tabs.TabPages.Add(toolsPage);

            // ---------------- 统计页 ----------------
            var statsPage = new TabPage("统计") { BackColor = Cream, Padding = new Padding(12, 10, 12, 10) };
            var statsHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

            var modeRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 38,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 0, 0, 6)
            };
            modeRow.Controls.Add(MakeButton("逐小时", delegate { SetChartMode(ChartMode.Hourly); }));
            modeRow.Controls.Add(MakeButton("逐天", delegate { SetChartMode(ChartMode.Daily); }));

            _chart = new ChartPanel { Dock = DockStyle.Fill };
            statsHost.Controls.Add(modeRow);
            statsHost.Controls.Add(_chart);
            statsPage.Controls.Add(statsHost);

            _tabs.TabPages.Add(statsPage);
            if (startTab >= 0 && startTab < _tabs.TabPages.Count) _tabs.SelectedIndex = startTab;
            _tabs.SelectedIndexChanged += delegate
            {
                if (_tabs.SelectedIndex == 1) LoadClaims();
                if (_tabs.SelectedIndex == 2) LoadSettings();
            };
            Controls.Add(_tabs);

            // 减少标签悬停/切换时的闪烁
            EnableDoubleBuffer(this);
            EnableDoubleBuffer(_tabs);
            foreach (TabPage tabPage in _tabs.TabPages) EnableDoubleBuffer(tabPage);
            HookScrollRepaint(_tabs);

            // ---------------- 托盘 ----------------
            _tray = new NotifyIcon
            {
                Icon = Icon,
                Text = "鸣潮桌宠 · 宝箱助手",
                Visible = true
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示面板", null, delegate { ShowPanel(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("开始监控", null, delegate { StartMonitor(null, EventArgs.Empty); });
            menu.Items.Add("停止监控", null, delegate { StopMonitor(null, EventArgs.Empty); });
            menu.Items.Add("立即检测", null, delegate { RunOnce(null, EventArgs.Empty); });
            menu.Items.Add(new ToolStripSeparator());
            _trayAutostart = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
            _trayAutostart.Checked = Autostart.IsEnabled(Application.ExecutablePath);
            _trayAutostart.Click += delegate
            {
                bool enable = _trayAutostart.Checked;
                ApplyAutostart(enable, false);
                if (_settingsLoaded) _setAutostart.Checked = enable;
            };
            menu.Items.Add(_trayAutostart);
            menu.Items.Add("打开日志目录", null, delegate { OpenLogs(null, EventArgs.Empty); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { _reallyExit = true; Close(); });
            menu.Opening += delegate
            {
                bool enabled = Autostart.IsEnabled(Application.ExecutablePath);
                _trayAutostart.Checked = enabled;
            };
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowPanel(); };
            _tray.BalloonTipClicked += delegate { ShowPanel(); };

            // ---------------- 行为 ----------------
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && !_hiddenToTray)
                    BeginInvoke((Action)HideToTray);
            };
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (_rooms != null) _rooms.Stop();
                if (_stats != null) _stats.Save();
                if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                }
            };
            FormClosed += delegate
            {
                _timer.Stop();
                _timer.Dispose();
                if (_trayWatcher != null) { _trayWatcher.DestroyHandle(); _trayWatcher = null; }
                _tray.Visible = false;
                _tray.Dispose();
                try { _engine.Stop(); } catch { }
            };

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += delegate { RefreshAll(); };
            Load += delegate
            {
                SeedFromLog();
                Log.Written += OnLogWritten;
                _trayWatcher = new TrayWatcher();
                _trayWatcher.TaskbarRestarted += delegate
                {
                    // 资源管理器重启后重新注册托盘图标
                    try { _tray.Visible = false; _tray.Visible = true; } catch { }
                };
                _timer.Start();
                if (_cfg.AutoStartMonitor) _engine.Start();
                Shown += delegate { ApplyBackground(); };
            ApplyBackground();

            // 联机房间：后台上报当前房间 + 记录历史
            _rooms = new RoomService(_cfg, _dir);
            _rooms.Changed += delegate
            {
                try { if (IsHandleCreated) BeginInvoke((Action)RefreshRoomUi); }
                catch { }
            };
            _rooms.Start();

            _stats = new StatsStore(_dir);
            if (_chart != null) _chart.Bind(_stats);
            RefreshRoomUi();
            EnableDoubleBuffering(this);
                _engine.Peek();
                RefreshAll();
                if (startHidden) BeginInvoke((Action)HideToTray);
            };
        }

        // ------------------------------------------------------------------
        // 小工具
        // ------------------------------------------------------------------
        private static Label MakeKey(string text)
        {
            return new Label
            {
                Text = text,
                ForeColor = Muted,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent
            };
        }

        private static Label MakeValue(string text)
        {
            return new Label
            {
                Text = text,
                ForeColor = Ink,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent,
                AutoEllipsis = true
            };
        }

        // 只装按钮的横向 FlowLayoutPanel：让高度跟着按钮走，否则按钮被行高裁掉
        private static void RelaxButtonRows(Control root)
        {
            foreach (Control child in root.Controls)
            {
                var flow = child as FlowLayoutPanel;
                if (flow != null && flow.FlowDirection == FlowDirection.LeftToRight && flow.Controls.Count > 0)
                {
                    bool onlyButtons = true;
                    foreach (Control c in flow.Controls) { if (!(c is Button)) { onlyButtons = false; break; } }
                    if (onlyButtons)
                    {
                        flow.AutoSize = true;
                        flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                        flow.MinimumSize = new Size(0, 0);
                    }
                }
                RelaxButtonRows(child);
            }
        }

        private Button MakeButton(string text, EventHandler onClick)
        {
            // 高度跟字体走（+14 给边框和上下留白）；宽度按文字量算，避免不同 DPI 下被裁
            int width = TextRenderer.MeasureText(text, Font).Width + 28;
            var button = new Button
            {
                Text = text,
                AutoSize = false,
                Width = Math.Max(80, width),
                Height = Font.Height + 10,
                FlatStyle = FlatStyle.Flat,
                BackColor = Paper,
                ForeColor = Ink,
                Margin = new Padding(0, 1, 8, 1),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(214, 198, 174);
            button.Click += onClick;
            return button;
        }

        private static NumericUpDown MakeNumber(decimal min, decimal max, int decimals, decimal step, double value)
        {
                        var box = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Increment = step,
                Width = 90,
                Height = 28,
                Font = new Font("Microsoft YaHei UI", 9f)
            };
            decimal v;
            try { v = (decimal)value; }
            catch { v = min; }
            if (v < min) v = min;
            if (v > max) v = max;
            box.Value = v;
            return box;
        }

        private static CheckBox MakeCheck(string text, bool value)
        {
            return new CheckBox
            {
                Text = text,
                Checked = value,
                AutoSize = true,
                ForeColor = Ink,
                Location = new Point(196, 6),
                BackColor = Color.Transparent
            };
        }

        private static Panel MakeSettingRow(string label, Control control)
        {
            // 行容器里如果有 FlowLayoutPanel，先把它撑到能装下最高的子控件，再据此定行高
            var flow = control as FlowLayoutPanel;
            if (flow != null)
            {
                int tallest = 0;
                foreach (Control child in flow.Controls)
                {
                    int need = child.Height + child.Margin.Top + child.Margin.Bottom;
                    if (need > tallest) tallest = need;
                }
                if (tallest > 0) flow.Height = tallest + 4;
            }

            int height = Math.Max(control.Height, 26) + 6;
            var panel = new Panel { Width = 360, Height = height, BackColor = Color.Transparent, Margin = new Padding(0, 1, 0, 1) };

            int labelHeight = Math.Max(24, control.Height);
            if (!string.IsNullOrEmpty(label))
            {
                panel.Controls.Add(new Label
                {
                    Text = label,
                    ForeColor = Ink,
                    AutoSize = false,
                    Width = 140,
                    Height = labelHeight,
                    Location = new Point(0, Math.Max(0, (height - labelHeight) / 2)),
                    TextAlign = ContentAlignment.MiddleLeft,
                    BackColor = Color.Transparent
                });
            }

            control.Location = new Point(string.IsNullOrEmpty(label) ? 0 : 146, Math.Max(0, (height - control.Height) / 2));
            panel.Controls.Add(control);
            return panel;
        }

        // ------------------------------------------------------------------
        // 数据
        // ------------------------------------------------------------------
        private void SeedFromLog()
        {
            var lines = Log.ReadDay(_logsDir, null);
            _claimsToday = 0;
            foreach (var line in lines)
            {
                if (line.IndexOf("领取成功", StringComparison.Ordinal) >= 0) _claimsToday++;
                var m = ClaimPattern.Match(line);
                if (m.Success) _lastClaimText = m.Groups[1].Value + " → " + m.Groups[2].Value;
            }

            lock (_logLock)
            {
                _logLines.Clear();
                int start = Math.Max(0, lines.Count - 200);
                for (int i = start; i < lines.Count; i++) _logLines.Add(lines[i]);
            }
        }

        private void OnLogWritten(LogEntry entry)
        {
            lock (_logLock)
            {
                _logLines.Add(entry.Line);
                while (_logLines.Count > 300) _logLines.RemoveAt(0);
            }

            if (entry.Message.IndexOf("领取成功", StringComparison.Ordinal) >= 0)
            {
                _claimsToday++;
                var m = ClaimPattern.Match(entry.Message);
                if (m.Success)
                {
                    _lastClaimText = m.Groups[1].Value + " → " + m.Groups[2].Value;
                    ShowClaimBalloon(_lastClaimText);
                }
            }
        }

        private void ShowClaimBalloon(string text)
        {
            try
            {
                if (!_cfg.NotifyOnClaim) return;
                _tray.ShowBalloonTip(4000, "宝箱已领取", "字符数 " + text, ToolTipIcon.Info);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // 刷新
        // ------------------------------------------------------------------
        private void RefreshAll()
        {
            var status = _engine.GetStatus();
            bool running = status.Running;
            if (!running) _engine.Peek();
            var pet = status.Pet;

            // 统计：把当前累计字符数记进小时桶
            if (_stats != null && pet != null && pet.Ok && pet.CumulativeClicks > 0)
            {
                if (_stats.Note(pet.CumulativeClicks) && _chart != null) _chart.Invalidate();
            }
            bool fresh = status.PetAt != DateTime.MinValue && (DateTime.Now - status.PetAt).TotalSeconds <= 20;

            if (running)
            {
                SetColor(_dot, Color.FromArgb(126, 217, 87), false);
                SetText(_monitorText, status.Busy ? "监控运行中 · 检测中…" : "监控运行中 · " + status.StatusText);
            }
            else
            {
                SetColor(_dot, Color.FromArgb(230, 120, 100), false);
                SetText(_monitorText, "监控已停止");
            }

            if (fresh && pet != null && pet.Ok)
            {
                SetText(_numberText, pet.Number.ToString("N0", CultureInfo.InvariantCulture));
                SetColor(_numberText, pet.Number >= AppConfig.ClaimCost ? Good : Ink, false);
                SetText(_numberHint, pet.Number >= AppConfig.ClaimCost
                    ? "已够 " + AppConfig.ClaimCost + "，可以领取"
                    : "再攒 " + (AppConfig.ClaimCost - pet.Number).ToString("N0", CultureInfo.InvariantCulture) + " 就能领");

                if (pet.Chest)
                {
                    SetText(_chestBadge, "宝箱：有");
                    SetColor(_chestBadge, Color.FromArgb(215, 238, 222), true);
                    SetColor(_chestBadge, Good, false);
                }
                else
                {
                    SetText(_chestBadge, "宝箱：无");
                    SetColor(_chestBadge, Color.FromArgb(238, 228, 212), true);
                    SetColor(_chestBadge, Muted, false);
                }
            }
            else
            {
                SetText(_numberText, "—");
                SetColor(_numberText, Muted, false);
                SetText(_chestBadge, "宝箱：—");
                SetColor(_chestBadge, Color.FromArgb(238, 228, 212), true);
                SetColor(_chestBadge, Muted, false);

                if (!running) SetText(_numberHint, "监控未运行，点击「开始监控」");
                else if (pet != null && !pet.Ok) SetText(_numberHint, pet.Reason);
                else SetText(_numberHint, "等待监控刷新…");
            }

            SetText(_lastCheck, fresh
                ? (status.LastChangeAt > DateTime.MinValue ? status.LastChangeAt : status.PetAt).ToString("HH:mm:ss") + "（" + pet.Number.ToString("N0", CultureInfo.InvariantCulture) + "）"
                : "—");

            SetText(_nextCheck, running && status.NextRun > DateTime.MinValue
                ? status.NextRun.ToString("HH:mm:ss")
                : "—");

            SetText(_claims, _claimsToday + " 个");
            SetText(_lastClaim, _lastClaimText);

            if (_farmChars != null)
            {
                _farmChars.Text = fresh && pet != null && pet.Ok
                    ? "当前字符数：" + pet.Number.ToString("N0", CultureInfo.InvariantCulture)
                    : "当前字符数：—";
            }

            string tail;
            lock (_logLock)
            {
                int start = Math.Max(0, _logLines.Count - 3);
                var parts = new List<string>();
                for (int i = start; i < _logLines.Count; i++) parts.Add(_logLines[i]);
                tail = parts.Count == 0 ? "（今天还没有日志）" : string.Join("\r\n", parts.ToArray());
            }
            if (tail != _lastRenderedTail)
            {
                _lastRenderedTail = tail;
                _logBox.Text = tail;
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }

            _footer.Text = string.Format("阈值 {0}（固定）· 实时读取 · 状态更新 {1}",
                AppConfig.ClaimCost,
                fresh ? (status.LastChangeAt > DateTime.MinValue ? status.LastChangeAt : status.PetAt).ToString("HH:mm:ss") : "—");

            try
            {
                string text = "宝箱助手 · " + _claimsToday + " 个 · " + (fresh && pet != null && pet.Ok ? pet.Number.ToString() : "—");
                if (text.Length > 63) text = text.Substring(0, 63);
                _tray.Text = text;
            }
            catch { }

            if (_tabs.SelectedIndex == 1 && (DateTime.Now - _claimsLoadedAt).TotalSeconds >= 10)
                LoadClaims();
        }

        private void LoadClaims()
        {
            List<ClaimRecord> claims;
            try { claims = History.ReadClaims(_logsDir, 500); }
            catch { claims = new List<ClaimRecord>(); }

            _claimsLoadedAt = DateTime.Now;
            _claimList.BeginUpdate();
            try
            {
                _claimList.Items.Clear();
                foreach (var claim in claims)
                {
                    var item = new ListViewItem(claim.When.ToString("MM-dd HH:mm:ss"));
                    item.SubItems.Add(claim.Before.ToString("N0") + " → " + claim.After.ToString("N0"));
                    item.SubItems.Add("-" + AppConfig.ClaimCost.ToString("N0"));
                    item.SubItems.Add(!string.IsNullOrEmpty(claim.Reward) ? claim.Reward
                        : (!string.IsNullOrEmpty(claim.Screenshot) ? "有截图" : "—"));
                    item.Tag = claim;
                    if (claim.When.Date == DateTime.Today) item.ForeColor = Good;
                    _claimList.Items.Add(item);
                }
            }
            finally
            {
                _claimList.EndUpdate();
            }

            int today = 0;
            foreach (var claim in claims)
                if (claim.When.Date == DateTime.Today) today++;

            string last = claims.Count > 0 ? claims[0].When.ToString("MM-dd HH:mm") : "—";
            _claimSummary.Text = string.Format("今日 {0} 个 · 累计 {1} 个 · 最近 {2}", today, claims.Count, last);
        }

        private void ShowSelectedClaim()
        {
            var record = _claimList.SelectedItems.Count > 0 ? _claimList.SelectedItems[0].Tag as ClaimRecord : null;
            string path = ResolveShot(record);

            if (_claimPreview.Image != null)
            {
                var old = _claimPreview.Image;
                _claimPreview.Image = null;
                old.Dispose();
            }

            if (path != null)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var source = Image.FromStream(stream))
                    {
                        _claimPreview.Image = new Bitmap(source);
                    }
                    _claimPreview.Visible = true;
                    _claimPreviewHint.Visible = false;
                    return;
                }
                catch { }
            }

            _claimPreview.Visible = false;
            _claimPreviewHint.Visible = true;
            _claimPreviewHint.Text = record == null
                ? "选中一条记录查看奖励截图，双击打开原图"
                : "这条记录没有截图";
        }

        private string ResolveShot(ClaimRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.Screenshot)) return null;
            var path = Path.Combine(_logsDir, record.Screenshot.Replace('/', '\\'));
            return File.Exists(path) ? path : null;
        }

        private void OpenSelectedClaim()
        {
            var record = _claimList.SelectedItems.Count > 0 ? _claimList.SelectedItems[0].Tag as ClaimRecord : null;
            var path = ResolveShot(record);
            if (path == null) return;
            try { Process.Start(path); } catch { }
        }

        // --------------------------------------------------------------
        // 背景图
        // --------------------------------------------------------------
        private void ChooseBackground()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp";
                dialog.Title = "选择面板背景图";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    string stored = BackgroundFile.Install(_dir, dialog.FileName);
                    _cfg = _cfg.Clone();
                    _cfg.BackgroundImage = stored;
                    _cfg.Save(_configPath);
                    _engine.UpdateConfig(_cfg);
                    ApplyBackground();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "设置背景图失败：" + ex.Message, "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ClearBackground()
        {
            _cfg = _cfg.Clone();
            _cfg.BackgroundImage = "";
            try { _cfg.Save(_configPath); } catch { }
            _engine.UpdateConfig(_cfg);
            ApplyBackground();
        }

        /// <summary>
        /// 滚动时整页重绘：AutoScroll 容器滚动用的是位块搬移，透明白底 + 固定背景图会被搬成一条一条的条纹。
        /// 滚一下就把所在标签页整体重画一次。
        /// </summary>
        private void HookScrollRepaint(Control root)
        {
            foreach (Control child in root.Controls)
            {
                var scrollable = child as ScrollableControl;
                if (scrollable != null && scrollable.AutoScroll)
                {
                    Control page = scrollable;
                    while (page != null && !(page is TabPage)) page = page.Parent;
                    var target = page;
                    scrollable.Scroll += delegate
                    {
                        if (target == null || !target.IsHandleCreated) return;
                        if (_scrollRepainting) return;
                        _scrollRepainting = true;
                        try
                        {
                            target.Invalidate(true);
                            target.Update();
                        }
                        finally { _scrollRepainting = false; }
                    };
                }
                HookScrollRepaint(child);
            }
        }

        private bool _scrollRepainting;

        private static void EnableDoubleBuffer(Control control)
        {
            try
            {
                var property = typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (property != null) property.SetValue(control, true, null);
            }
            catch { }
        }

        private static void SetText(Control control, string value)
        {
            if (control.Text != value) control.Text = value;
        }

        private static void SetColor(Control control, Color color, bool isBackColor)
        {
            if (isBackColor)
            {
                if (control.BackColor != color) control.BackColor = color;
            }
            else
            {
                if (control.ForeColor != color) control.ForeColor = color;
            }
        }

        private static void EnableDoubleBuffering(Control root)
        {
            if (!(root is TextBox) && !(root is ListView)) EnableDoubleBuffer(root);
            foreach (Control child in root.Controls) EnableDoubleBuffering(child);
        }

        private void ApplyBackground()
        {
            BackgroundImage = null;
            foreach (TabPage page in _tabs.TabPages) page.BackgroundImage = null;

            Image source = null;
            string name = _cfg.BackgroundImage;
            if (!string.IsNullOrEmpty(name))
            {
                string path = Path.IsPathRooted(name) ? name : Path.Combine(_dir, name);
                if (File.Exists(path))
                {
                    try { source = Image.FromFile(path); }
                    catch { source = null; }
                }
            }

            // 没配置、或者配置的文件不在了 → 用内嵌的默认背景
            if (source == null) source = DefaultBackground.Load();
            if (source == null) return;

            try
            {
                {
                    int width = ClientSize.Width;
                    int height = ClientSize.Height;
                    var bitmap = new Bitmap(width, height);
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        float scale = Math.Max((float)width / source.Width, (float)height / source.Height);
                        int w = (int)(source.Width * scale) + 1;
                        int h = (int)(source.Height * scale) + 1;
                        g.DrawImage(source, new Rectangle((width - w) / 2, (height - h) / 2, w, h));

                        int dim = Math.Max(0, Math.Min(90, _cfg.BackgroundDim));
                        using (var overlay = new SolidBrush(Color.FromArgb(dim * 255 / 100, Cream)))
                            g.FillRectangle(overlay, 0, 0, width, height);
                    }

                    // 整个窗口只用一张图：每个标签页按它在窗口里的位置裁剪同一张图，
                    // 标签栏和页面拼起来就是连续的一张，不会出现“两张图”。
                    BackgroundImage = bitmap;
                    BackgroundImageLayout = ImageLayout.None;

                    foreach (TabPage page in _tabs.TabPages)
                    {
                        try
                        {
                            Point origin = PointToClient(page.PointToScreen(Point.Empty));
                            int pw = page.ClientSize.Width;
                            int ph = page.ClientSize.Height;
                            if (pw <= 0 || ph <= 0) { page.BackgroundImage = bitmap; continue; }

                            var pageBmp = new Bitmap(pw, ph);
                            using (var pg = Graphics.FromImage(pageBmp))
                            {
                                pg.DrawImage(bitmap, new Rectangle(-origin.X, -origin.Y, width, height));
                            }
                            page.BackgroundImage = pageBmp;
                            page.BackgroundImageLayout = ImageLayout.None;
                            page.BackColor = Cream;
                        }
                        catch { page.BackgroundImage = bitmap; }
                    }
                }
                if (source != null) source.Dispose();
            }
            catch { if (source != null) source.Dispose(); }
        }

        /// <summary>给可滚动容器铺一张和窗口背景完全对齐的图，并设为不透明（否则滚动会出现条纹）</summary>
        private void PaintScrollPanels(Control root, Bitmap windowBitmap, int winWidth, int winHeight)
        {
            foreach (Control child in root.Controls)
            {
                var scrollable = child as ScrollableControl;
                if (scrollable != null && scrollable.AutoScroll)
                {
                    try
                    {
                        int cw = scrollable.ClientSize.Width;
                        int ch = scrollable.ClientSize.Height;
                        if (cw > 100 && ch > 100)
                        {
                            Point origin = PointToClient(scrollable.PointToScreen(Point.Empty));
                            var bmp = new Bitmap(cw, ch);
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.DrawImage(windowBitmap, new Rectangle(-origin.X, -origin.Y, winWidth, winHeight));
                                using (var brush = new SolidBrush(Color.FromArgb(190, 255, 252, 246)))
                                    g.FillRectangle(brush, 0, 0, cw, ch);
                            }
                            var old = scrollable.BackgroundImage;
                            scrollable.BackgroundImage = bmp;
                            scrollable.BackgroundImageLayout = ImageLayout.None;
                            scrollable.BackColor = Cream;
                            if (old != null) old.Dispose();
                        }
                    }
                    catch { }
                }
                PaintScrollPanels(child, windowBitmap, winWidth, winHeight);
            }
        }

        // ---------------- 联机房间 ----------------
        private void BuildRoomSection(FlowLayoutPanel flow)
        {
            flow.Controls.Add(new Label
            {
                Text = "联机房间",
                ForeColor = Ink,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                BackColor = Color.Transparent
            });

            _roomCurrentText = new Label
            {
                Text = "当前房间：读取中…",
                ForeColor = Ink,
                AutoSize = true,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 4, 0, 2)
            };
            flow.Controls.Add(_roomCurrentText);

            _roomStatusText = new Label
            {
                Text = "",
                ForeColor = Muted,
                AutoSize = false,
                Width = 356,
                Height = 34,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 4)
            };
            flow.Controls.Add(_roomStatusText);

            _roomRows = new FlowLayoutPanel
            {
                Width = 356,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 8)
            };
            flow.Controls.Add(_roomRows);

            flow.Controls.Add(new Label
            {
                Text = "进过的房间会自动记下来；点「重连」= 先离开当前房间，再自动填号加入，失败会写原因。",
                ForeColor = Muted,
                AutoSize = true,
                MaximumSize = new Size(400, 0),
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 12)
            });
        }

        private void RefreshRoomUi()
        {
            if (_rooms == null || IsDisposed) return;

            var current = _rooms.Current;
            SetText(_roomCurrentText, current.InRoom
                ? "当前房间：" + current.Id + (string.IsNullOrEmpty(current.Owner) ? "" : "    房主 " + current.Owner)
                : "当前房间：不在房间里");

            RebuildRoomRows();
        }

        private void RebuildRoomRows()
        {
            if (_roomRows == null || IsDisposed) return;
            _roomRows.SuspendLayout();
            while (_roomRows.Controls.Count > 0)
            {
                var old = _roomRows.Controls[0];
                _roomRows.Controls.RemoveAt(0);
                old.Dispose();
            }

            var rooms = _rooms.Snapshot();
            string current = _rooms.Current.Id;

            foreach (var entry in rooms)
            {
                var row = new FlowLayoutPanel
                {
                    Width = 356,
                    Height = 34,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 2, 0, 2)
                };

                string label = entry.Id;
                if (!string.IsNullOrEmpty(entry.Owner)) label += "  " + entry.Owner;
                if (entry.Visits > 1) label += "  ×" + entry.Visits;

                var text = new Label
                {
                    Text = label,
                    AutoSize = false,
                    Width = 206,
                    Height = 30,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = entry.Id == current ? Good : Ink,
                    BackColor = Color.Transparent
                };
                row.Controls.Add(text);

                string roomId = entry.Id;
                var reconnect = MakeButton("重连", delegate { ReconnectRoom(roomId); });
                reconnect.Width = 68;
                row.Controls.Add(reconnect);

                var forget = MakeButton("×", delegate { ForgetRoom(roomId); });
                forget.Width = 40;
                row.Controls.Add(forget);

                _roomRows.Controls.Add(row);
            }

            if (rooms.Count == 0)
            {
                _roomRows.Controls.Add(new Label
                {
                    Text = "还没有记录，进一次房间就有了。",
                    ForeColor = Muted,
                    AutoSize = true,
                    BackColor = Color.Transparent
                });
            }
            _roomRows.ResumeLayout();
        }

        private void ReconnectRoom(string roomId)
        {
            if (_rooms == null || _roomBusy) return;
            _roomBusy = true;
            SetText(_roomStatusText, "正在重连 " + roomId + "…");

            var service = _rooms;
            var thread = new Thread(new ThreadStart(delegate
            {
                string reason;
                bool ok = service.Reconnect(roomId, out reason);
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        _roomBusy = false;
                        if (ok)
                        {
                            SetText(_roomStatusText, "已进入 " + roomId);
                            Log.Action("房间重连成功：" + roomId);
                        }
                        else
                        {
                            SetText(_roomStatusText, "重连失败：" + reason);
                            Log.Warn("房间重连失败（" + roomId + "）：" + reason);
                        }
                        RefreshRoomUi();
                    });
                }
                catch { }
            }));
            thread.IsBackground = true;
            thread.Start();
        }

        private void ForgetRoom(string roomId)
        {
            if (_rooms == null) return;
            _rooms.Forget(roomId);
        }

        private void SetChartMode(ChartMode mode)
        {
            if (_chart == null) return;
            _chart.Mode = mode;
        }

        private void ExportClaims()
        {
            List<ClaimRecord> claims;
            try { claims = History.ReadClaims(_logsDir, 5000); }
            catch { claims = new List<ClaimRecord>(); }

            if (claims.Count == 0)
            {
                MessageBox.Show(this, "还没有领取记录。", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "CSV 文件 (*.csv)|*.csv";
                dialog.FileName = "宝箱领取记录-" + DateTime.Now.ToString("yyyyMMdd") + ".csv";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                var sb = new StringBuilder();
                sb.AppendLine("时间,领取前,领取后,消耗");
                foreach (var claim in claims)
                {
                    sb.AppendLine(string.Format("{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3}",
                        claim.When, claim.Before, claim.After, claim.Before - claim.After));
                }
                try
                {
                    File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
                    MessageBox.Show(this, "已导出 " + claims.Count + " 条记录。", "导出完成",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ------------------------------------------------------------------
        // 跨电脑同步
        // ------------------------------------------------------------------
        // ------------------------------------------------------------------
        // 登录 Token（通过 CDP 读写桌宠 cookie）
        // ------------------------------------------------------------------
        private int TokenPort()
        {
            try { return CdpDiscovery.ResolvePort(_cfg.CdpPort); }
            catch { return _cfg.CdpPort; }
        }

        private void ShowTokenExpiry()
        {
            int port = TokenPort();
            string token = TokenTools.ReadToken(port);
            if (token == null)
            {
                MessageBox.Show(this,
                    "读不到 Token。\r\n\r\n可能原因：桌宠没启动、CDP 没连上（端口 " + port + "），或者当前未登录。",
                    "登录 Token", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var expiry = TokenTools.ReadExpiry(port);
            MessageBox.Show(this,
                "Token：" + token.Substring(0, Math.Min(12, token.Length)) + "…（共 " + token.Length + " 字符）\r\n" +
                TokenTools.DescribeExpiry(expiry) + "\r\n\r\n" +
                "Token 是账号的登录凭证，看到即可用于登录，别截图外发。",
                "登录 Token", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RenewToken()
        {
            var confirm = MessageBox.Show(this,
                "续期会调用桌宠自己的 renewTokenIfNeeded 向服务器换取新 Token，有效期顺延约 7 天。\r\n\r\n" +
                "注意：续期后 Token 可能发生变化，如果另一台电脑在用旧 Token，需要重新提取并导入。\r\n\r\n继续吗？",
                "续期", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            int port = TokenPort();
            string result = TokenTools.Renew(port);
            string text;
            switch (result)
            {
                case "ok":
                    text = "续期成功。\r\n" + TokenTools.DescribeExpiry(TokenTools.ReadExpiry(port));
                    break;
                case "no-cdp":
                    text = "读不到桌宠页面：CDP 没连上（端口 " + port + "）。";
                    break;
                case "no-module":
                    text = "找不到桌宠的前端模块，续期未执行。";
                    break;
                case "no-api":
                    text = "这个桌宠版本没有暴露 renewTokenIfNeeded，无法手动续期。";
                    break;
                case "rejected":
                    text = "服务器拒绝了续期：Token 可能已失效，需要重新登录。";
                    break;
                default:
                    text = "续期失败（" + result + "），已恢复原来的有效期。";
                    break;
            }
            MessageBox.Show(this, text, "续期",
                MessageBoxButtons.OK, result == "ok" ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void CopyToken()
        {
            int port = TokenPort();
            string token = TokenTools.ReadToken(port);
            if (token == null)
            {
                MessageBox.Show(this, "读不到 Token：桌宠没启动、CDP 没连上，或者当前未登录。", "提取",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Clipboard.SetText(token);
                MessageBox.Show(this,
                    "Token 已复制到剪贴板（" + token.Length + " 字符）。\r\n\r\n" +
                    "把它粘贴到另一台电脑的「Token 登录」就能免扫码登录；\r\n" +
                    "不要发到群里或粘进不可信的软件，泄露等于账号被接管。",
                    "提取", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "复制失败：" + ex.Message, "提取",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void LoginWithToken()
        {
            string token = (_tokenInput == null ? "" : _tokenInput.Text).Trim();
            if (token.Length < 16)
            {
                MessageBox.Show(this, "请先把从另一台电脑「提取」的 Token 粘贴到左边的输入框。", "Token 登录",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var confirm = MessageBox.Show(this,
                "会用这个 Token 覆盖当前登录状态，并通知桌宠重新登录。\r\n继续吗？",
                "Token 登录", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            int port = TokenPort();
            string result = TokenTools.Login(port, token);
            if (result == "ok")
            {
                if (_tokenInput != null) _tokenInput.Text = "";
                MessageBox.Show(this, "Token 已写入，桌宠正在重新登录。\r\n稍等几秒，" + TokenTools.DescribeExpiry(TokenTools.ReadExpiry(port)),
                    "Token 登录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                string text = result == "no-cdp" ? "读不到桌宠页面：CDP 没连上（端口 " + port + "）。"
                    : result == "no-api" ? "这个桌宠版本没有暴露 applyAuthTokens，无法用 Token 登录。"
                    : result == "no-module" ? "找不到桌宠的前端模块。"
                    : "Token 登录失败（" + result + "），请确认 Token 有效。";
                MessageBox.Show(this, text, "Token 登录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ExportSync()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "WuwaChest 同步包 (*" + SyncService.Extension + ")|*" + SyncService.Extension + "|JSON 文件 (*.json)|*.json";
                dialog.FileName = "WuwaChest-" + Environment.MachineName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + SyncService.Extension;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    SyncService.ExportToFile(dialog.FileName, _dir, _logsDir);
                    var pkg = SyncService.Collect(_dir, _logsDir);
                    MessageBox.Show(this,
                        "已导出：\r\n\r\n领取记录 " + pkg.Claims.Count + " 条\r\n房间 " + pkg.Rooms.Count + " 条\r\n字符数 " + pkg.Stats.Count + " 条\r\n设置已包含\r\n\r\n" + dialog.FileName,
                        "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ImportSync()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "WuwaChest 同步包 (*" + SyncService.Extension + ")|*" + SyncService.Extension + "|JSON 文件 (*.json)|*.json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                SyncPackage pkg;
                try { pkg = SyncService.ReadPackage(dialog.FileName); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "不能读取这个文件", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var choice = MessageBox.Show(this,
                    "来自：" + pkg.Machine + "    导出时间：" + pkg.ExportedAt + "\r\n" +
                    "领取记录 " + pkg.Claims.Count + " 条 / 房间 " + pkg.Rooms.Count + " 条 / 字符数 " + pkg.Stats.Count + " 条\r\n\r\n" +
                    "「是」= 合并数据，并用包里的设置覆盖本机设置\r\n" +
                    "「否」= 只合并数据，保留本机设置\r\n" +
                    "「取消」= 什么都不做",
                    "导入同步包", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                if (choice == DialogResult.Cancel) return;
                bool withSettings = choice == DialogResult.Yes;

                try
                {
                    var result = SyncService.Apply(pkg, _dir, _logsDir, withSettings);

                    // 同步内存里的统计与房间（否则它们下次保存会把合并结果盖掉）
                    if (_stats != null) _stats.MergeLines(pkg.Stats);
                    if (_rooms != null) _rooms.MergeLines(pkg.Rooms);

                    if (result.ConfigApplied)
                    {
                        var reloaded = AppConfig.Load(Path.Combine(_dir, "config.json"));
                        if (reloaded != null) _cfg = reloaded;
                    }

                    LoadClaims();

                    var text = new StringBuilder();
                    text.AppendLine("合并完成：");
                    text.AppendLine("· 领取记录 +" + result.ClaimsAdded + " 条");
                    text.AppendLine("· 房间 " + result.RoomsMerged + " 条");
                    text.AppendLine("· 字符数 " + result.StatsMerged + " 个时段");
                    text.AppendLine(result.ConfigApplied ? "· 设置已用同步包覆盖（部分项重启后生效）" : "· 本机设置保持不变");
                    foreach (var note in result.Notes) text.AppendLine("! " + note);

                    MessageBox.Show(this, text.ToString(), "导入完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        // ------------------------------------------------------------------
        // 设置
        // ------------------------------------------------------------------
        private void LoadSettings()
        {
            _settingsLoaded = true;
            _setNotReady.Value = Clamp(_setNotReady, (decimal)_cfg.NotReadyRetryMinutes);
            _setBackgroundDim.Value = Clamp(_setBackgroundDim, (decimal)_cfg.BackgroundDim);
            _setNotify.Checked = _cfg.NotifyOnClaim;
            _setRewardShot.Checked = _cfg.RewardScreenshots;
            _setCdp.Checked = _cfg.CdpEnabled;
            _setAutoMonitor.Checked = _cfg.AutoStartMonitor;
            _setAutostart.Checked = Autostart.IsEnabled(Application.ExecutablePath);
        }

        private static decimal Clamp(NumericUpDown box, decimal value)
        {
            if (value < box.Minimum) return box.Minimum;
            if (value > box.Maximum) return box.Maximum;
            return value;
        }

        private void SaveSettings(object sender, EventArgs e)
        {
            var cfg = _cfg.Clone();
            cfg.NotReadyRetryMinutes = (double)_setNotReady.Value;
            cfg.BackgroundDim = (int)_setBackgroundDim.Value;
            cfg.NotifyOnClaim = _setNotify.Checked;
            cfg.RewardScreenshots = _setRewardShot.Checked;
            cfg.CdpEnabled = _setCdp.Checked;
            cfg.AutoStartMonitor = _setAutoMonitor.Checked;

            try
            {
                cfg.Save(_configPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存 config.json 失败：" + ex.Message, "保存失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _cfg = cfg;
            _engine.UpdateConfig(cfg);
            ApplyAutostart(_setAutostart.Checked, false);
            ApplyBackground();

            MessageBox.Show(this, "设置已保存并生效。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshAll();
        }

        private void ResetSettings(object sender, EventArgs e)
        {
            var defaults = new AppConfig();
            _setNotReady.Value = (decimal)defaults.NotReadyRetryMinutes;
            _setNotify.Checked = defaults.NotifyOnClaim;
            _setAutoMonitor.Checked = defaults.AutoStartMonitor;
        }

        private void ApplyAutostart(bool enable, bool quiet)
        {
            try
            {
                if (enable) Autostart.Enable(Application.ExecutablePath);
                else Autostart.Disable();

                _trayAutostart.Checked = enable;
                if (_settingsLoaded) _setAutostart.Checked = enable;
                if (!quiet)
                    _tray.ShowBalloonTip(2000, "开机自启",
                        enable ? "已设置为登录后自动启动" : "已取消开机自启", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                if (!quiet)
                    MessageBox.Show(this, "设置开机自启失败：" + ex.Message, "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------------
        // 按钮
        // ------------------------------------------------------------------
        private void StartMonitor(object sender, EventArgs e)
        {
            if (_engine.IsRunning) return;
            _engine.Start();
            Log.Action("监控已启动：基础间隔 " + AppConfig.FallbackIntervalMinutes.ToString("0.#", CultureInfo.InvariantCulture) +
                       " 分钟，阈值 " + AppConfig.ClaimCost);
            RefreshAll();
        }

        private void StopMonitor(object sender, EventArgs e)
        {
            if (!_engine.IsRunning) return;
            _engine.Stop();
            Log.Action("监控已停止");
            RefreshAll();
        }

        private void RunOnce(object sender, EventArgs e)
        {
            // 监控没在跑时只跑这一次，不会顺手把持续监控打开
            _engine.CheckNow();
        }

        private void OpenLogs(object sender, EventArgs e)
        {
            try
            {
                if (!Directory.Exists(_logsDir)) Directory.CreateDirectory(_logsDir);
                Process.Start("explorer.exe", "\"" + _logsDir + "\"");
            }
            catch { }
        }


        // 加字符：直接给桌宠的本地计数加数，它自己会上报给服务端
        private void BoostClicks(int delta)
        {
            if (!CdpDiscovery.IsAvailable(_cfg.CdpPort))
            {
                MessageBox.Show(this,
                    "调试端口没开，加字符用不了。\r\n\r\n" +
                    "可以先用下面的「真实点击」档位，或者用 运行CDP测试.cmd 启用调试端口。",
                    "加字符不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirm = MessageBox.Show(this,
                string.Format("给桌宠的本地计数加 {0} 字符？\r\n\r\n" +
                              "这条通道官方程序里本来就有（隐藏的 +10000 按钮），实测服务端会接受；\r\n" +
                              "但本质仍是利用调试接口，请自行判断风险。", delta),
                "加字符", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (confirm != DialogResult.OK) return;

            var cdp = new CdpPetClient(_cfg);
            string detail;
            if (cdp.BoostLocalClicks(delta, out detail))
            {
                Log.Action("加字符：+" + delta);
                _farmLabel.Text = "已加 " + delta + "（桌宠会在几秒内上报）";
                _engine.Peek();
            }
            else
            {
                Log.Warn("加字符失败：" + detail);
                _farmLabel.Text = "加字符失败：" + detail;
            }
        }



        // ------------------------------------------------------------------
        // 托盘
        // ------------------------------------------------------------------
        private static Icon CreateTrayIcon()
        {
            var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var bg = new SolidBrush(Color.FromArgb(200, 121, 31)))
                    g.FillEllipse(bg, 1, 1, 30, 30);
                using (var fg = new SolidBrush(Color.FromArgb(255, 248, 236)))
                {
                    g.FillRectangle(fg, 9, 13, 14, 10);
                    g.FillRectangle(fg, 8, 10, 16, 4);
                }
                using (var lockBrush = new SolidBrush(Color.FromArgb(200, 121, 31)))
                    g.FillRectangle(lockBrush, 15, 15, 2, 7);
            }

            IntPtr handle = bitmap.GetHicon();
            try
            {
                using (var temp = Icon.FromHandle(handle)) { return (Icon)temp.Clone(); }
            }
            finally
            {
                DestroyIcon(handle);
                bitmap.Dispose();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private void HideToTray()
        {
            _hiddenToTray = true;
            Hide();
            ShowInTaskbar = false;
            try
            {
                _tray.ShowBalloonTip(1500, "宝箱助手", "已最小化到托盘，双击图标可恢复", ToolTipIcon.Info);
            }
            catch { }
        }

        private void ShowPanel()
        {
            _hiddenToTray = false;
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }
    }
}
