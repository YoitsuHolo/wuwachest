// UpdateChecker.cs — 从 GitHub 仓库检查并下载新版 WuwaChest.exe
// 判断依据是版本号（仓库 src/Program.cs 里的 Version 常量 vs 本程序 Program.Version）。
// 只替换 exe，不同步源码，避免覆盖本地改动。
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

        /// <summary>最近一次检查的说明（给界面/日志用）</summary>
        public static string Status = "";
        public static bool StagedUpdate;

        // ------------------------------------------------------------------
        /// <summary>启动时后台检查：6 小时内查过就跳过，不阻塞界面</summary>
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

        /// <summary>
        /// 同步检查：比对版本号，仓库更新就下载到 WuwaChest.new.exe。
        /// force = true 时忽略版本判断（调试用）。
        /// </summary>
        public static string CheckAndStage(AppConfig cfg, string appDir, bool force)
        {
            string repo = (cfg == null || string.IsNullOrEmpty(cfg.UpdateRepo)) ? DefaultRepo : cfg.UpdateRepo.Trim();
            string localExe = Path.Combine(appDir, "WuwaChest.exe");
            if (!File.Exists(localExe)) { Status = "找不到本机 WuwaChest.exe"; return Status; }

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            string api = "https://api.github.com/repos/" + repo + "/contents/WuwaChest.exe";
            string json = HttpGet(api, 15000);
            if (json == null) { Status = "连不上 GitHub，稍后再试"; return Status; }

            long remoteSize = JsonLong(json, "size");
            if (remoteSize <= 0) { Status = "仓库里没有 WuwaChest.exe"; return Status; }

            string remoteVersion = ReadRemoteVersion(repo);
            string localVersion = Program.Version;
            if (string.IsNullOrEmpty(remoteVersion))
            {
                Status = "读不到仓库版本号，跳过";
                return Status;
            }

            int cmp = CompareVersion(remoteVersion, localVersion);
            if (cmp <= 0 && !force)
            {
                StagedUpdate = false;
                Status = "已是最新（本地 " + localVersion + " / 远程 " + remoteVersion + "）";
                return Status;
            }

            string staged = Path.Combine(appDir, "WuwaChest.new.exe");
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }

            // raw.githubusercontent 在这边只有几 KB/s，改走 codeload 的 zip（实测 1MB/秒）
            if (!DownloadFromZip(repo, staged))
            {
                Status = "下载失败，稍后再试";
                return Status;
            }

            long got = new FileInfo(staged).Length;
            if (got != remoteSize)
            {
                try { File.Delete(staged); } catch { }
                Status = "下载不完整（" + FormatSize(got) + " / " + FormatSize(remoteSize) + "），已丢弃";
                return Status;
            }

            StagedUpdate = true;
            Status = "发现新版本 " + localVersion + " → " + remoteVersion +
                     "（" + FormatSize(remoteSize) + "），已下载，退出时自动替换";
            return Status;
        }

        /// <summary>退出时调用：有待替换的新 exe 就起后台脚本，等本进程退出后换文件并重启</summary>
        public static bool ApplyIfStaged(string appDir)
        {
            try
            {
                string staged = Path.Combine(appDir, "WuwaChest.new.exe");
                if (!File.Exists(staged)) return false;

                string batch = Path.Combine(appDir, "apply-update.cmd");
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
                    WorkingDirectory = appDir
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
        /// <summary>从仓库 src/Program.cs 里读 Version 常量（取不到返回空）</summary>
        private static string ReadRemoteVersion(string repo)
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

        /// <summary>比较版本号；返回 &gt;0 表示 remote 更新</summary>
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

        // ------------------------------------------------------------------
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

        /// <summary>下载仓库 zip 并从中取出 WuwaChest.exe</summary>
        private static bool DownloadFromZip(string repo, string destPath)
        {
            string zipPath = destPath + ".zip";
            try
            {
                string url = "https://codeload.github.com/" + repo + "/zip/refs/heads/main";
                if (!Download(url, zipPath, 120000)) return false;

                using (var zip = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    System.IO.Compression.ZipArchiveEntry found = null;
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith("WuwaChest.exe", StringComparison.OrdinalIgnoreCase) &&
                            entry.FullName.IndexOf("/src/", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            found = entry;
                            break;
                        }
                    }
                    if (found == null) return false;

                    using (var input = found.Open())
                    using (var output = File.Create(destPath))
                        input.CopyTo(output);
                }
                return true;
            }
            catch { return false; }
            finally { try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { } }
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

        private static long JsonLong(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\\d+)");
            long v;
            return m.Success && long.TryParse(m.Groups[1].Value, out v) ? v : 0;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024).ToString("0") + " KB";
            return bytes + " B";
        }
    }
}
