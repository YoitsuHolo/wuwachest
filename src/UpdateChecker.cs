// UpdateChecker.cs — 从 GitHub Release 检查并下载新版 WuwaChest.exe
// 版本号来自 Release 的 tag（如 v2.8），仓库里不再放 exe 二进制。
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WuwaChest
{
    internal static class UpdateChecker
    {
        public const string DefaultRepo = "YoitsuHolo/wuwachest";

        public static string Status = "";
        public static bool StagedUpdate;

        // ------------------------------------------------------------------
        public static void CheckAsync(AppConfig cfg, string appDir)
        {
            if (cfg == null || !cfg.UpdateEnabled) return;
            var thread = new Thread(delegate()
            {
                try
                {
                    string stamp = Path.Combine(appDir, "update-state.txt");
                    try
                    {
                        if (File.Exists(stamp) &&
                            (DateTime.Now - File.GetLastWriteTime(stamp)).TotalHours < 6)
                            return;
                    }
                    catch { }

                    string msg = CheckAndStage(cfg, appDir, false);
                    try { File.WriteAllText(stamp, DateTime.Now.ToString("s")); } catch { }

                    if (StagedUpdate) Log.Action("更新：" + msg);
                    else Log.Info("更新检查：" + msg);
                }
                catch (Exception ex)
                {
                    Status = "检查更新失败：" + ex.Message;
                    Log.Warn(Status);
                }
            });
            thread.IsBackground = true;
            thread.Name = "WuwaUpdateCheck";
            thread.Start();
        }

        public static string CheckAndStage(AppConfig cfg, string appDir)
        {
            return CheckAndStage(cfg, appDir, false);
        }

        /// <summary>比对版本号；Release 有更新的 exe 就下载到 WuwaChest.new.exe</summary>
        public static string CheckAndStage(AppConfig cfg, string appDir, bool force)
        {
            string repo = (cfg == null || string.IsNullOrEmpty(cfg.UpdateRepo)) ? DefaultRepo : cfg.UpdateRepo.Trim();
            string exeDir = AppPaths.ExeDir;
            string localExe = Path.Combine(exeDir, "WuwaChest.exe");
            if (!File.Exists(localExe)) { Status = "找不到本机 WuwaChest.exe"; return Status; }

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            string remoteVersion = "";
            string assetUrl = "";
            long assetSize = 0;

            string releaseJson = HttpGet("https://api.github.com/repos/" + repo + "/releases/latest", 15000);
            if (releaseJson != null)
            {
                remoteVersion = ParseTagVersion(releaseJson);
                ParseExeAsset(releaseJson, out assetUrl, out assetSize);
            }

            // 还没发过 Release 时，退回读源码里的 Version 常量（只能提示，不能下载）
            if (string.IsNullOrEmpty(remoteVersion)) remoteVersion = ReadSourceVersion(repo);
            if (string.IsNullOrEmpty(remoteVersion)) { Status = "读不到仓库版本号，跳过"; return Status; }

            string localVersion = Program.Version;
            int cmp = CompareVersion(remoteVersion, localVersion);
            if (cmp <= 0 && !force)
            {
                StagedUpdate = false;
                Status = "已是最新（本地 " + localVersion + " / 远程 " + remoteVersion + "）";
                return Status;
            }

            if (string.IsNullOrEmpty(assetUrl))
            {
                StagedUpdate = false;
                Status = "仓库有新版本 " + remoteVersion + "，但 Release 里没有 exe 资源";
                return Status;
            }

            string staged = Path.Combine(exeDir, "WuwaChest.new.exe");
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }

            if (!Download(assetUrl, staged, 180000))
            {
                Status = "下载失败，稍后再试";
                return Status;
            }

            long got = new FileInfo(staged).Length;
            if (assetSize > 0 && got != assetSize)
            {
                try { File.Delete(staged); } catch { }
                Status = "下载不完整（" + FormatSize(got) + " / " + FormatSize(assetSize) + "），已丢弃";
                return Status;
            }

            StagedUpdate = true;
            Status = "发现新版本 " + localVersion + " → " + remoteVersion +
                     "（" + FormatSize(got) + "），已下载，退出时自动替换";
            return Status;
        }

        /// <summary>退出时调用：有待替换的新 exe 就起后台脚本，等本进程退出后换文件并重启</summary>
        public static bool ApplyIfStaged(string appDir)
        {
            try
            {
                string exeDir = AppPaths.ExeDir;
                string staged = Path.Combine(exeDir, "WuwaChest.new.exe");
                if (!File.Exists(staged)) return false;

                string batch = Path.Combine(exeDir, "apply-update.cmd");
                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("setlocal");
                sb.AppendLine("cd /d \"%~dp0\"");
                sb.AppendLine(":wait");
                sb.AppendLine("tasklist /FI \"IMAGENAME eq WuwaChest.exe\" 2>nul | find /I \"WuwaChest.exe\" >nul");
                sb.AppendLine("if not errorlevel 1 (");
                sb.AppendLine("  timeout /t 1 /nobreak >nul");
                sb.AppendLine("  goto wait");
                sb.AppendLine(")");
                sb.AppendLine("if exist \"WuwaChest.exe\" copy /Y \"WuwaChest.exe\" \"WuwaChest.backup.exe\" >nul 2>&1");
                sb.AppendLine("move /Y \"WuwaChest.new.exe\" \"WuwaChest.exe\" >nul 2>&1");
                sb.AppendLine("start \"\" \"WuwaChest.exe\"");
                sb.AppendLine("del \"%~f0\"");
                File.WriteAllText(batch, sb.ToString(), Encoding.Default);

                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + batch + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = exeDir
                });
                Log.Action("已安排更新：退出后自动替换 WuwaChest.exe 并重启（备份 WuwaChest.backup.exe）");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("安排更新失败：" + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        private static string ParseTagVersion(string releaseJson)
        {
            var m = Regex.Match(releaseJson, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) return "";
            return m.Groups[1].Value.Trim().TrimStart('v', 'V');
        }

        /// <summary>在 assets 里找 .exe（优先名字正好是 WuwaChest.exe 的）</summary>
        private static void ParseExeAsset(string releaseJson, out string url, out long size)
        {
            url = "";
            size = 0;

            // GitHub 的 asset 对象里，"name" 和 "browser_download_url" 之间隔着一大段 uploader 信息
            // （实测 1200+ 字符），所以不能用一个固定的窗口去匹配：
            // 先找每个 download_url，再往前回溯最近的 "name"。
            foreach (Match m in Regex.Matches(releaseJson, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
            {
                int at = m.Index;
                int from = Math.Max(0, at - 6000);
                string window = releaseJson.Substring(from, at - from);

                var names = Regex.Matches(window, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                if (names.Count == 0) continue;
                string name = names[names.Count - 1].Groups[1].Value;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                url = m.Groups[1].Value.Replace("\\/", "/");

                var sizes = Regex.Matches(window, "\"size\"\\s*:\\s*(\\d+)");
                long v;
                if (sizes.Count > 0 && long.TryParse(sizes[sizes.Count - 1].Groups[1].Value, out v)) size = v;
                return;
            }
        }

        /// <summary>没发 Release 时的兜底：读仓库 src/Program.cs 的 Version</summary>
        private static string ReadSourceVersion(string repo)
        {
            try
            {
                string json = HttpGet("https://api.github.com/repos/" + repo + "/contents/src/Program.cs", 15000);
                if (json == null) return "";

                var m = Regex.Match(json, "\"content\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) return "";

                string base64 = m.Groups[1].Value.Replace("\\n", "").Replace("\n", "");
                byte[] bytes = Convert.FromBase64String(base64);
                string source = Encoding.UTF8.GetString(bytes);

                var v = Regex.Match(source, "Version\\s*=\\s*\"([0-9][0-9.]*)\"");
                return v.Success ? v.Groups[1].Value : "";
            }
            catch { return ""; }
        }

        private static int CompareVersion(string remote, string local)
        {
            var r = SplitVersion(remote);
            var l = SplitVersion(local);
            int n = Math.Max(r.Length, l.Length);
            for (int i = 0; i < n; i++)
            {
                int a = i < r.Length ? r[i] : 0;
                int b = i < l.Length ? l[i] : 0;
                if (a != b) return a > b ? 1 : -1;
            }
            return 0;
        }

        private static int[] SplitVersion(string text)
        {
            if (string.IsNullOrEmpty(text)) return new int[0];
            var parts = text.Split('.');
            var list = new System.Collections.Generic.List<int>();
            foreach (var part in parts)
            {
                int v;
                list.Add(int.TryParse(part.Trim(), out v) ? v : 0);
            }
            return list.ToArray();
        }

        private static string HttpGet(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "WuwaChest-Updater";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch { return null; }
        }

        private static bool Download(string url, string path, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "WuwaChest-Updater";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var input = resp.GetResponseStream())
                using (var output = File.Create(path))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        output.Write(buffer, 0, read);
                }
                return true;
            }
            catch { return false; }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024).ToString("0") + " KB";
            return bytes + " B";
        }
    }
}
