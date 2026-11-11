// AppPaths.cs — 决定运行时数据放哪
// 默认放 %LOCALAPPDATA%\WuwaChest（exe 可以单独拷来拷去）；
// 想在 exe 同目录存数据（便携模式），在 exe 旁放一个空的 portable.txt。
using System;
using System.IO;

namespace WuwaChest
{
    internal static class AppPaths
    {
        public static string ExeDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string DataDir
        {
            get
            {
                string exeDir = ExeDir;
                try
                {
                    if (File.Exists(Path.Combine(exeDir, "portable.txt"))) return exeDir;
                }
                catch { }

                string dir;
                try
                {
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuwaChest");
                }
                catch { return exeDir; }

                try { Directory.CreateDirectory(dir); }
                catch { return exeDir; }
                return dir;
            }
        }

        /// <summary>旧版把数据放在 exe 目录：第一次搬到 AppData 时复制过去（不删原文件）</summary>
        public static void Migrate(string dataDir)
        {
            try
            {
                string exeDir = ExeDir;
                if (string.Equals(Path.GetFullPath(dataDir), Path.GetFullPath(exeDir), StringComparison.OrdinalIgnoreCase))
                    return;

                string oldConfig = Path.Combine(exeDir, "config.json");
                string newConfig = Path.Combine(dataDir, "config.json");
                if (!File.Exists(newConfig) && File.Exists(oldConfig))
                {
                    File.Copy(oldConfig, newConfig, true);
                    foreach (var name in new[] { "rooms.txt", "stats.txt" })
                    {
                        string src = Path.Combine(exeDir, name);
                        if (File.Exists(src))
                        {
                            try { File.Copy(src, Path.Combine(dataDir, name), true); } catch { }
                        }
                    }

                    string oldLogs = Path.Combine(exeDir, "logs");
                    if (Directory.Exists(oldLogs))
                    {
                        foreach (var file in Directory.GetFiles(oldLogs, "*", SearchOption.AllDirectories))
                        {
                            string rel = file.Substring(oldLogs.Length).TrimStart('\\', '/');
                            string dst = Path.Combine(dataDir, "logs", rel);
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                                if (!File.Exists(dst)) File.Copy(file, dst);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }
    }
}
