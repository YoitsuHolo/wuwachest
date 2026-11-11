// CloudSync.cs — 把日志/截图同步到 GitHub 私有仓库（双向，走本机 git）
// token 用 DPAPI 加密存在数据目录的 cloud-token.bin，不写进 config.json
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WuwaChest
{
    internal static class CloudSync
    {
        private const string TokenFile = "cloud-token.bin";
        private const string RepoDirName = "cloud-repo";

        // 当前这次同步用的代理（空=不走代理）；由 Upload/Pull 按配置设置
        private static string _proxy = "";

        // ------------------------------------------------------------------
        // token（DPAPI，当前用户）
        // ------------------------------------------------------------------
        public static string LoadToken(string dataDir)
        {
            try
            {
                string path = Path.Combine(dataDir, TokenFile);
                if (!File.Exists(path)) return "";
                byte[] raw = File.ReadAllBytes(path);
                byte[] plain = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return ""; }
        }

        public static void SaveToken(string dataDir, string token)
        {
            string path = Path.Combine(dataDir, TokenFile);
            if (string.IsNullOrEmpty(token))
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                return;
            }
            byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, enc);
        }

        public static bool HasToken(string dataDir) { return LoadToken(dataDir).Length > 0; }

        // ------------------------------------------------------------------
        public static string Upload(AppConfig cfg, string dataDir)
        {
            string token = LoadToken(dataDir);
            string repo = (cfg.CloudRepo ?? "").Trim();
            string branch = string.IsNullOrEmpty(cfg.CloudBranch) ? "main" : cfg.CloudBranch.Trim();
            string folder = NormalizeFolder(cfg.CloudFolder);
            _proxy = (cfg.CloudProxy ?? "").Trim();
            if (token.Length == 0) return "还没填 Token";
            if (repo.IndexOf('/') <= 0) return "仓库要写成 owner/repo";

            string work = Path.Combine(dataDir, RepoDirName);
            string err;
            if (!EnsureRepo(work, repo, token, branch, out err)) return err;

            string src = Path.Combine(dataDir, "logs");
            string dst = Path.Combine(work, folder, "logs");
            if (!Directory.Exists(src)) return "本地还没有日志";

            Directory.CreateDirectory(dst);
            int copied = 0;
            foreach (var f in Directory.GetFiles(src, "chest-*.log"))
            {
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
                copied++;
            }
            string srcShots = Path.Combine(src, "rewards");
            if (Directory.Exists(srcShots))
            {
                string dstShots = Path.Combine(dst, "rewards");
                Directory.CreateDirectory(dstShots);
                foreach (var f in Directory.GetFiles(srcShots, "*.png"))
                {
                    string target = Path.Combine(dstShots, Path.GetFileName(f));
                    if (!File.Exists(target)) { File.Copy(f, target, false); copied++; }
                }
            }

            string status;
            Git(work, "add -A", 30000, out status);
            string diffOut;
            Git(work, "status --porcelain", 30000, out diffOut);
            if (string.IsNullOrEmpty(diffOut.Trim())) return "云端已是最新，没有需要上传的文件";

            string msg = "sync: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " from " + Environment.MachineName;
            string commitOut;
            int code = Git(work, "commit -m \"" + msg + "\"", 60000, out commitOut);
            if (code != 0 && commitOut.IndexOf("nothing to commit", StringComparison.OrdinalIgnoreCase) < 0)
                return "提交失败：" + Shorten(commitOut);

            int pushCode = Git(work, "push origin " + branch, 180000, out status);
            if (pushCode != 0) return "推送失败：" + Shorten(status);
            return "已同步 " + copied + " 个文件到云端";
        }

        // ------------------------------------------------------------------
        public static string Pull(AppConfig cfg, string dataDir)
        {
            string token = LoadToken(dataDir);
            string repo = (cfg.CloudRepo ?? "").Trim();
            string branch = string.IsNullOrEmpty(cfg.CloudBranch) ? "main" : cfg.CloudBranch.Trim();
            string folder = NormalizeFolder(cfg.CloudFolder);
            _proxy = (cfg.CloudProxy ?? "").Trim();
            if (token.Length == 0) return "还没填 Token";
            if (repo.IndexOf('/') <= 0) return "仓库要写成 owner/repo";

            string work = Path.Combine(dataDir, RepoDirName);
            string err;
            if (!EnsureRepo(work, repo, token, branch, out err)) return err;

            string status;
            Git(work, "pull --rebase origin " + branch, 180000, out status);

            string src = Path.Combine(work, folder, "logs");
            string dst = Path.Combine(dataDir, "logs");
            if (!Directory.Exists(src)) return "云端还没有数据";

            int mergedLines = 0, copiedShots = 0;
            Directory.CreateDirectory(dst);

            foreach (var f in Directory.GetFiles(src, "chest-*.log"))
            {
                string target = Path.Combine(dst, Path.GetFileName(f));
                mergedLines += MergeLog(target, File.ReadAllText(f, Encoding.UTF8));
            }

            string srcShots = Path.Combine(src, "rewards");
            if (Directory.Exists(srcShots))
            {
                string dstShots = Path.Combine(dst, "rewards");
                Directory.CreateDirectory(dstShots);
                foreach (var f in Directory.GetFiles(srcShots, "*.png"))
                {
                    string target = Path.Combine(dstShots, Path.GetFileName(f));
                    if (!File.Exists(target)) { File.Copy(f, target, false); copiedShots++; }
                }
            }

            if (mergedLines == 0 && copiedShots == 0) return "云端没有新的日志或截图";
            return "已合并 " + mergedLines + " 行日志、" + copiedShots + " 张截图";
        }

        // ------------------------------------------------------------------
        private static bool EnsureRepo(string work, string repo, string token, string branch, out string error)
        {
            error = "";
            try
            {
                string url = "https://x-access-token:" + token + "@github.com/" + repo + ".git";

                if (!Directory.Exists(Path.Combine(work, ".git")))
                {
                    if (Directory.Exists(work))
                    {
                        // 目录存在但不是 git 仓库：清掉重来
                        try { Directory.Delete(work, true); } catch { }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(work));
                    string cloneOut;
                    int code = Git(Path.GetDirectoryName(work), "clone \"" + url + "\" \"" + work + "\"", 180000, out cloneOut);
                    if (code != 0)
                    {
                        // 空仓库 clone 会警告但成功；真失败就报错
                        if (!Directory.Exists(Path.Combine(work, ".git")))
                        {
                            error = "克隆仓库失败：" + Shorten(cloneOut);
                            return false;
                        }
                    }
                }

                // 每次同步前设置好身份和远端（token 可能换过）
                Git(work, "remote set-url origin \"" + url + "\"", 30000, out error);
                Git(work, "config user.name \"WuwaChest\"", 10000, out error);
                Git(work, "config user.email \"wuwachest@users.noreply.github.com\"", 10000, out error);
                return true;
            }
            catch (Exception ex)
            {
                error = "准备仓库失败：" + ex.Message;
                return false;
            }
        }

        private static string _gitExe;

        private static string ResolveGit()
        {
            if (!string.IsNullOrEmpty(_gitExe)) return _gitExe;
            string[] candidates =
            {
                @"C:\Program Files\Git\cmd\git.exe",
                @"C:\Program Files (x86)\Git\cmd\git.exe",
                @"C:\Users\" + Environment.UserName + @"\AppData\Local\Programs\Git\cmd\git.exe"
            };
            foreach (var c in candidates) { if (File.Exists(c)) { _gitExe = c; return c; } }

            try
            {
                var psi = new ProcessStartInfo("where", "git")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string line = p.StandardOutput.ReadLine();
                    p.WaitForExit(5000);
                    if (!string.IsNullOrEmpty(line) && File.Exists(line.Trim())) { _gitExe = line.Trim(); return _gitExe; }
                }
            }
            catch { }

            _gitExe = "git";
            return _gitExe;
        }
        private static int Git(string workDir, string args, int timeoutMs, out string output)
        {
            var sb = new StringBuilder();
            try
            {
                // 有配代理就通过 git -c 注入，避免依赖全局 git 配置
                string gitArgs = args;
                if (!string.IsNullOrEmpty(_proxy) && args.IndexOf("-c http.proxy", StringComparison.Ordinal) < 0)
                    gitArgs = "-c http.proxy=" + _proxy + " -c https.proxy=" + _proxy + " " + args;

                var psi = new ProcessStartInfo(ResolveGit(), gitArgs)
                {
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                if (!string.IsNullOrEmpty(_proxy))
                {
                    psi.EnvironmentVariables["HTTP_PROXY"] = _proxy;
                    psi.EnvironmentVariables["HTTPS_PROXY"] = _proxy;
                }
                using (var p = Process.Start(psi))
                {
                    sb.Append(p.StandardOutput.ReadToEnd());
                    sb.Append(p.StandardError.ReadToEnd());
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        output = sb.ToString() + "\n(超时)";
                        return -1;
                    }
                    output = sb.ToString();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        private static int MergeLog(string localPath, string remoteText)
        {
            var local = new HashSet<string>(StringComparer.Ordinal);
            string existing = "";
            if (File.Exists(localPath))
            {
                existing = File.ReadAllText(localPath, Encoding.UTF8);
                foreach (var line in existing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    local.Add(line.Trim());
            }

            var missing = new List<string>();
            foreach (var line in remoteText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (local.Contains(t)) continue;
                local.Add(t);
                missing.Add(t);
            }
            if (missing.Count == 0) return 0;

            Directory.CreateDirectory(Path.GetDirectoryName(localPath));
            var sb = new StringBuilder(existing);
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append("\r\n");
            sb.Append(string.Join("\r\n", missing.ToArray())).Append("\r\n");
            File.WriteAllText(localPath, sb.ToString(), new UTF8Encoding(false));
            return missing.Count;
        }

        private static string NormalizeFolder(string folder)
        {
            string f = (folder ?? "").Trim().Trim('/');
            return f.Length == 0 ? "wuwachest" : f;
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length > 200 ? text.Substring(0, 200) : text;
        }
    }
}
