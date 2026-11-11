// AppConfig.cs — 配置读写（兼容旧版 config.json 的字段名，直接手改也能生效）
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WuwaChest
{
    internal sealed class AppConfig
    {
        // 官方规则固定：每领一个宝箱消耗 1000 字符数
        public const int ClaimCost = 1000;
        // 没有 CDP 时间戳时的兜底检测间隔（分钟）
        public const double FallbackIntervalMinutes = 30;

        public double NotReadyRetryMinutes = 5;   // 桌宠没开/读不到界面时的重试间隔
        public double HeartbeatSeconds = 5;       // 面板状态刷新间隔
        public int VerifyDelayMs = 1200;          // 点击后等多久复查
        public bool NotifyOnClaim = true;         // 领取后弹托盘通知
        public bool AutoStartMonitor = true;      // 程序启动后自动开始监控
        public int LogRetentionDays = 30;
        public bool RewardScreenshots = true;     // 领取时截图记录「拿到了什么」
        public string RewardShotMode = "dom";    // 奖励图：dom=合成（不闪屏），screen=CDP 截图（会闪）
        public int RewardShotDelayMs = 900;      // 点击后等多久截图（弹窗动画刚展开）
        public string BackgroundImage = "";       // 面板背景图（相对 exe 目录或绝对路径）
        public int BackgroundDim = 45;
        public bool CdpEnabled = true;            // 优先用 CDP（WebView2 调试端口）读写页面，失败退回 UIA
        public int CdpPort = 9333;                // 调试端口（与 WebView2 策略里写的一致）
        public bool CdpAutoEnable = true;         // 启动时自动写入调试策略（需要管理员）
        public bool CdpAutoRestartPet = true;     // 写入策略后自动重启桌宠让它生效
        public bool CdpDisableOnExit = true;      // 退出时撤销策略
        public bool UpdateEnabled = true;                  // 启动时检查 GitHub 更新
        public string UpdateRepo = "YoitsuHolo/wuwachest"; // 更新来源仓库
        public string CloudRepo = "";                  // 云同步：owner/repo
        public string CloudBranch = "main";            // 云同步分支
        public string CloudFolder = "wuwachest";       // 云同步目录
        public string CloudProxy = "";                 // 云同步走 git 时的代理，如 http://127.0.0.1:7890

        private static string Token(string json, string key)
        {
            var m = Regex.Match(json,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|-?[0-9]+(?:\\.[0-9]+)?|true|false)",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static int Int(string json, string key, int def)
        {
            string t = Token(json, key);
            int v;
            if (t != null && int.TryParse(t.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        private static double Double(string json, string key, double def)
        {
            string t = Token(json, key);
            double v;
            if (t != null && double.TryParse(t.Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        private static bool Bool(string json, string key, bool def)
        {
            string t = Token(json, key);
            if (t == null) return def;
            t = t.Trim('"').ToLowerInvariant();
            return t == "true" || t == "1";
        }

        private static string Str(string json, string key, string def)
        {
            string t = Token(json, key);
            if (t == null) return def;
            t = t.Trim('"');
            return t.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        public AppConfig Clone()
        {
            return (AppConfig)MemberwiseClone();
        }

        public static AppConfig Load(string path)
        {
            var cfg = new AppConfig();
            if (!File.Exists(path)) return cfg;

            string json;
            try { json = File.ReadAllText(path, Encoding.UTF8); }
            catch { return cfg; }
            if (string.IsNullOrEmpty(json)) return cfg;

            cfg.NotReadyRetryMinutes = Double(json, "notReadyRetryMinutes", cfg.NotReadyRetryMinutes);
            cfg.HeartbeatSeconds = Double(json, "heartbeatSeconds", cfg.HeartbeatSeconds);
            cfg.VerifyDelayMs = Int(json, "verifyDelayMs", cfg.VerifyDelayMs);
            cfg.NotifyOnClaim = Bool(json, "notifyOnClaim", cfg.NotifyOnClaim);
            cfg.AutoStartMonitor = Bool(json, "autoStartMonitor", cfg.AutoStartMonitor);
            cfg.LogRetentionDays = Int(json, "logRetentionDays", cfg.LogRetentionDays);
            cfg.RewardScreenshots = Bool(json, "rewardScreenshots", cfg.RewardScreenshots);
            cfg.RewardShotDelayMs = Int(json, "rewardShotDelayMs", cfg.RewardShotDelayMs);
            cfg.RewardShotMode = Str(json, "rewardShotMode", cfg.RewardShotMode);
            cfg.BackgroundImage = Str(json, "backgroundImage", cfg.BackgroundImage);
            cfg.BackgroundDim = Int(json, "backgroundDim", cfg.BackgroundDim);
            cfg.CdpEnabled = Bool(json, "cdpEnabled", cfg.CdpEnabled);
            cfg.CdpPort = Int(json, "cdpPort", cfg.CdpPort);
            cfg.CdpAutoEnable = Bool(json, "cdpAutoEnable", cfg.CdpAutoEnable);
            cfg.CdpAutoRestartPet = Bool(json, "cdpAutoRestartPet", cfg.CdpAutoRestartPet);
            cfg.CdpDisableOnExit = Bool(json, "cdpDisableOnExit", cfg.CdpDisableOnExit);
            cfg.UpdateEnabled = Bool(json, "updateEnabled", cfg.UpdateEnabled);
            cfg.UpdateRepo = Str(json, "updateRepo", cfg.UpdateRepo);
            cfg.CloudRepo = Str(json, "cloudRepo", cfg.CloudRepo);
            cfg.CloudBranch = Str(json, "cloudBranch", cfg.CloudBranch);
            cfg.CloudFolder = Str(json, "cloudFolder", cfg.CloudFolder);
            cfg.CloudProxy = Str(json, "cloudProxy", cfg.CloudProxy);




            // 防御：手改坏了也不至于把程序搞挂
            if (cfg.VerifyDelayMs < 300) cfg.VerifyDelayMs = 300;
            if (cfg.HeartbeatSeconds < 1) cfg.HeartbeatSeconds = 1;
            if (cfg.LogRetentionDays < 1) cfg.LogRetentionDays = 30;
            if (cfg.RewardShotDelayMs < 200) cfg.RewardShotDelayMs = 900;
            if (cfg.BackgroundDim < 0) cfg.BackgroundDim = 0;
            if (cfg.BackgroundDim > 90) cfg.BackgroundDim = 90;
            return cfg;
        }

        private static string Num(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Esc(string text)
        {
            if (text == null) return "";
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"notReadyRetryMinutes\": " + Num(NotReadyRetryMinutes) + ",");
            sb.AppendLine("  \"heartbeatSeconds\": " + Num(HeartbeatSeconds) + ",");
            sb.AppendLine("  \"verifyDelayMs\": " + VerifyDelayMs + ",");
            sb.AppendLine("  \"notifyOnClaim\": " + (NotifyOnClaim ? "true" : "false") + ",");
            sb.AppendLine("  \"autoStartMonitor\": " + (AutoStartMonitor ? "true" : "false") + ",");
            sb.AppendLine("  \"logRetentionDays\": " + LogRetentionDays + ",");
            sb.AppendLine("  \"rewardScreenshots\": " + (RewardScreenshots ? "true" : "false") + ",");
            sb.AppendLine("  \"rewardShotDelayMs\": " + RewardShotDelayMs + ",");
            sb.AppendLine("  \"rewardShotMode\": \"" + RewardShotMode + "\",");
            sb.AppendLine("  \"backgroundImage\": \"" + Esc(BackgroundImage) + "\",");
            sb.AppendLine("  \"backgroundDim\": " + BackgroundDim + ",");
            sb.AppendLine("  \"cdpEnabled\": " + (CdpEnabled ? "true" : "false") + ",");
            sb.AppendLine("  \"cdpPort\": " + CdpPort + ",");
            sb.AppendLine("  \"cdpAutoEnable\": " + (CdpAutoEnable ? "true" : "false") + ",");
            sb.AppendLine("  \"cdpAutoRestartPet\": " + (CdpAutoRestartPet ? "true" : "false") + ",");
            sb.AppendLine("  \"cdpDisableOnExit\": " + (CdpDisableOnExit ? "true" : "false") + ",");
            sb.AppendLine("  \"updateEnabled\": " + (UpdateEnabled ? "true" : "false") + ",");
            sb.AppendLine("  \"updateRepo\": \"" + UpdateRepo + "\",");
            sb.AppendLine("  \"cloudRepo\": \"" + Esc(CloudRepo) + "\",");
            sb.AppendLine("  \"cloudBranch\": \"" + Esc(CloudBranch) + "\",");
            sb.AppendLine("  \"cloudFolder\": \"" + Esc(CloudFolder) + "\",");
            sb.AppendLine("  \"cloudProxy\": \"" + Esc(CloudProxy) + "\"");



            sb.AppendLine("}");

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
