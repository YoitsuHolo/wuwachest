// Program.cs — 入口：命令行诊断模式 + 图形界面
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WuwaChest
{
    internal static class Program
    {
        private const string MutexName = "Local\\WuwaChestV2";
        private const string WindowTitle = "鸣潮桌宠 · 宝箱助手";

        [STAThread]
        private static int Main(string[] args)
        {
            // 数据默认放 %LOCALAPPDATA%\WuwaChest，exe 可以单独拷来拷去；
            // 在 exe 旁放 portable.txt 则改用 exe 同目录。
            string dir = AppPaths.DataDir;
            AppPaths.Migrate(dir);
            string configPath = Path.Combine(dir, "config.json");
            string logsDir = Path.Combine(dir, "logs");
            var cfg = AppConfig.Load(configPath);

            if (HasArg(args, "--help") || HasArg(args, "-h") || HasArg(args, "-?"))
            {
                Native.TryAttachConsole();
                WriteHelp();
                return 0;
            }

            bool probe = HasArg(args, "--probe");
            bool once = HasArg(args, "--once");
            bool dryRun = HasArg(args, "--dry-run");

            Native.MakeDpiAware();
            Log.Init(logsDir, cfg.LogRetentionDays);

            // 命令行：设置背景图（也方便脚本/测试）
            string bgArg = ArgString(args, "--set-background");
            if (!string.IsNullOrEmpty(bgArg))
            {
                try
                {
                    string stored = BackgroundFile.Install(dir, bgArg);
                    var bgCfg = AppConfig.Load(configPath);
                    bgCfg.BackgroundImage = stored;
                    bgCfg.Save(configPath);
                    Log.Action("背景图已设置：" + stored);
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Error("设置背景图失败：" + ex.Message);
                    return 1;
                }
            }

            // 命令行：导出 / 导入同步包
            string syncExport = ArgString(args, "--sync-export");
            if (!string.IsNullOrEmpty(syncExport))
            {
                try { SyncService.ExportToFile(syncExport, dir, logsDir); Log.Action("已导出同步包：" + syncExport); return 0; }
                catch (Exception ex) { Log.Error("导出失败：" + ex.Message); return 1; }
            }
            string syncImport = ArgString(args, "--sync-import");
            if (!string.IsNullOrEmpty(syncImport))
            {
                try
                {
                    var pkg2 = SyncService.ReadPackage(syncImport);
                    var res = SyncService.Apply(pkg2, dir, logsDir, HasArg(args, "--with-settings"));
                    Log.Action(string.Format("已导入同步包：领取 +{0}，房间 {1}，字符数 {2}{3}",
                        res.ClaimsAdded, res.RoomsMerged, res.StatsMerged, res.ConfigApplied ? "，设置已覆盖" : ""));
                    return 0;
                }
                catch (Exception ex) { Log.Error("导入失败：" + ex.Message); return 1; }
            }

            if (probe) return RunProbe(cfg);
            if (HasArg(args, "--token-show"))
            {
                Native.TryAttachConsole();
                int tokenPort = CdpDiscovery.ResolvePort(cfg.CdpPort);
                string token = TokenTools.ReadToken(tokenPort);
                string tokenDesc = token == null ? "(读不到，可能未登录或 CDP 未连上)"
                    : token.Substring(0, Math.Min(12, token.Length)) + "… (" + token.Length + " 字符)";
                string expiryDesc = TokenTools.DescribeExpiry(TokenTools.ReadExpiry(tokenPort));
                Console.WriteLine("端口: " + tokenPort);
                Console.WriteLine("Token: " + tokenDesc);
                Console.WriteLine("有效期: " + expiryDesc);
                Log.Info("Token 检查：端口 " + tokenPort + "｜" + tokenDesc + "｜" + expiryDesc);
                return 0;
            }
            if (HasArg(args, "--update-force")) { Native.TryAttachConsole(); string uf = UpdateChecker.CheckAndStage(cfg, dir, true); Log.Info("强制检查更新：" + uf); Console.WriteLine(uf); return 0; }
            if (HasArg(args, "--cloud-token"))
            {
                Native.TryAttachConsole();
                string tk = ArgString(args, "--cloud-token");
                if (string.IsNullOrEmpty(tk)) { Console.WriteLine("用法: --cloud-token <token>"); return 1; }
                CloudSync.SaveToken(dir, tk.Trim());
                Console.WriteLine("token 已保存（DPAPI 加密）");
                return 0;
            }
            if (HasArg(args, "--cloud-upload") || HasArg(args, "--cloud-pull"))
            {
                Native.TryAttachConsole();
                string result;
                try
                {
                    result = HasArg(args, "--cloud-upload")
                        ? CloudSync.Upload(cfg, dir)
                        : CloudSync.Pull(cfg, dir);
                }
                catch (Exception ex) { result = "云同步异常：" + ex.GetType().Name + "：" + ex.Message; }
                Log.Info("云同步：" + result);
                Console.WriteLine(result);
                return 0;
            }            if (HasArg(args, "--update-check")) { Native.TryAttachConsole(); string um = UpdateChecker.CheckAndStage(cfg, dir); Log.Info("更新检查：" + um); Console.WriteLine(um); return 0; }
            if (HasArg(args, "--card-test")) return RunCardTest(cfg);
            if (HasArg(args, "--shot-test")) return RunShotTest(cfg);
            // 后台检查 GitHub 更新（不阻塞界面）
            UpdateChecker.CheckAsync(cfg, dir);
            // 启动时自动开启 CDP（策略不存在且未提权时，会请求管理员权限重启自己）
            if (CdpSetup.PrepareAndMaybeElevate(cfg, args)) return 0;

            if (once)
            {
                int onceCode = RunOnce(cfg, dryRun);
                UpdateChecker.ApplyIfStaged(dir);
                CdpSetup.RestoreOnExit(cfg);
                return onceCode;
            }


            int boostCount = ArgInt(args, "--boost", 0);
            if (boostCount > 0) return RunBoost(boostCount, cfg);

            bool tray = HasArg(args, "--tray");
            int tab = HasArg(args, "--history") ? 1 : (HasArg(args, "--settings") ? 2 : (HasArg(args, "--tools") ? 3 : (HasArg(args, "--stats") ? 4 : 0)));
            return RunGui(cfg, tray, tab);
        }

        private static int ArgInt(string[] args, string name, int fallback)
        {
            if (args == null) return fallback;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    int value;
                    if (int.TryParse(args[i + 1], out value)) return value;
                }
            }
            return fallback;
        }

        private static string ArgString(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }
        private static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            foreach (var arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal const string Version = "2.11";

        private static void WriteHelp()
        {
            Console.WriteLine("鸣潮桌宠 · 宝箱助手 v" + Version);
            Console.WriteLine("");
            Console.WriteLine("用法：");
            Console.WriteLine("  WuwaChest.exe                 打开面板（默认启动监控）");
            Console.WriteLine("  WuwaChest.exe --tray          只驻留托盘，不弹窗口");
            Console.WriteLine("  WuwaChest.exe --history       打开面板并切到「领取记录」");
            Console.WriteLine("  WuwaChest.exe --settings      打开面板并切到「设置」");
            Console.WriteLine("  WuwaChest.exe --tools         打开面板并切到「工具」（加字符）");
            Console.WriteLine("  WuwaChest.exe --stats         打开面板并切到「统计」");
            Console.WriteLine("  WuwaChest.exe --probe         打印当前识别到的界面元素（排障）");
            Console.WriteLine("  WuwaChest.exe --once          立即检测并领取一次，然后退出");
            Console.WriteLine("  WuwaChest.exe --once --dry-run  只看不点");
            Console.WriteLine("  WuwaChest.exe --boost 1000    给本地计数加 1000 字符（需 CDP）");
            Console.WriteLine("");
            Console.WriteLine("配置：config.json    日志：logs\\chest-yyyyMMdd.log");
        }

        // ------------------------------------------------------------------
        // 命令行诊断
        // ------------------------------------------------------------------
        private static int RunProbe(AppConfig cfg)
        {
            bool attached = Native.TryAttachConsole();
            var text = new StringBuilder();
            var client = new PetClient(cfg);
            PetSnapshot snap = null;

            try { snap = client.Read(); }
            catch (Exception ex) { text.AppendLine("读取异常：" + ex.Message); }

            if (snap != null && snap.Ok)
            {
                text.AppendLine("状态：已就绪");
                text.AppendLine(string.Format("字符数：{0}（阈值 {1}）", snap.Number, AppConfig.ClaimCost));
                text.AppendLine(snap.Chest
                    ? "宝箱：已出现  " + snap.Info
                    : "宝箱：未出现");
                text.AppendLine("--- 界面元素（有尺寸的）---");
                foreach (var element in snap.Elements) text.AppendLine("  " + element);
            }
            else
            {
                text.AppendLine("状态：未就绪");
                if (snap != null) text.AppendLine("原因：" + snap.Reason);
            }

            string output = text.ToString();
            if (cfg.CdpEnabled)
            {
                var cdpText = new StringBuilder(output);
                var cdp = new CdpPetClient(cfg);
                bool available = false;
                try { available = cdp.IsAvailable(); }
                catch { }

                cdpText.AppendLine(string.Format("--- CDP（端口 {0}）---", cdp.Port));
                cdpText.AppendLine(available ? "调试端口：可用" : "调试端口：不可用（回退 UIA）");
                if (available)
                {
                    var cdpSnap = cdp.Read();
                    cdpText.AppendLine(cdpSnap.Ok
                        ? string.Format("CDP 读数：字符数 {0}，宝箱 {1}", cdpSnap.Number, cdpSnap.Chest ? "有" : "无")
                        : "CDP 读取失败：" + cdpSnap.Reason);
                    string toast = "";
                    try { toast = cdp.ReadToastText(); }
                    catch { }
                    cdpText.AppendLine("弹窗文字：" + (string.IsNullOrEmpty(toast) ? "（空）" : toast));
                }
                output = cdpText.ToString();
            }

            try { File.WriteAllText(Path.Combine(Log.Dir, "probe.txt"), output, new UTF8Encoding(false)); }
            catch { }
            if (attached) Console.WriteLine(output);
            return 0;
        }

        // 命令行加字符：WuwaChest.exe --boost 1000
        private static int RunBoost(int count, AppConfig cfg)
        {
            bool attached = Native.TryAttachConsole();
            var cdp = new CdpPetClient(cfg);
            if (!cdp.IsAvailable())
            {
                if (attached) Console.WriteLine("调试端口不可用");
                return 1;
            }

            string detail;
            bool ok = cdp.BoostLocalClicks(count, out detail);
            if (attached) Console.WriteLine((ok ? "成功：" : "失败：") + detail);
            Log.Action("命令行加字符：" + (ok ? "+" + count : "失败 " + detail));
            return ok ? 0 : 1;
        }

        // 开发用：只验证截图区域是否正确，不领取
        /// <summary>诊断：只跑一次「DOM 合成奖励卡片」，并打印从弹窗里读到的奖励名</summary>
        private static int RunCardTest(AppConfig cfg)
        {
            bool attached = Native.TryAttachConsole();
            var cdp = new CdpPetClient(cfg);
            string reason = "";
            string rel = null;
            try { rel = cdp.CaptureCardFromDom(Log.Dir, 99, out reason, 2); }
            catch (Exception ex) { reason = ex.Message; }

            string text = rel == null
                ? "合成失败：" + reason
                : "已保存：" + Path.Combine(Log.Dir, rel.Replace('/', '\\'));
            if (attached)
            {
                Console.WriteLine(text);
                Console.WriteLine("奖励名：" + (string.IsNullOrEmpty(cdp.LastCardLabel) ? "(空)" : cdp.LastCardLabel));
            }
            Log.Info("卡片诊断：" + text + "｜奖励名 " + (string.IsNullOrEmpty(cdp.LastCardLabel) ? "(空)" : cdp.LastCardLabel));
            return 0;
        }
        private static int RunShotTest(AppConfig cfg)
        {
            bool attached = Native.TryAttachConsole();
            PetSnapshot snap = null;
            try { snap = new PetClient(cfg).Read(); }
            catch { }

            string rel = Screen.CaptureReward(Log.Dir, null, snap, 0);
            string text = rel == null
                ? "截图失败（桌宠是否可见？）"
                : "已保存：" + Path.Combine(Log.Dir, rel.Replace('/', '\\'));

            if (attached) Console.WriteLine(text);
            return 0;
        }

        private static int RunOnce(AppConfig cfg, bool dryRun)
        {
            bool attached = Native.TryAttachConsole();
            if (attached)
            {
                Log.Written += delegate(LogEntry entry) { Console.WriteLine(entry.Line); };
            }

            Log.Info("单次检测开始（阈值 " + AppConfig.ClaimCost + (dryRun ? "，预演模式）" : "）"));

            var engine = new ClaimEngine(cfg, Log.Dir);
            engine.SetDryRun(dryRun);

            try { engine.RunOnceSync(); }
            catch (Exception ex) { Log.Error("检测异常：" + ex.Message); }

            return 0;
        }

        // ------------------------------------------------------------------
        // 图形界面
        // ------------------------------------------------------------------
        private static int RunGui(AppConfig cfg, bool startHidden, int startTab)
        {
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    ActivateExistingWindow();
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                if (Native.IsLowIntegrity())
                {
                    MessageBox.Show(
                        "当前程序以受限权限（低完整性）运行，读不到桌宠界面。\r\n\r\n" +
                        "请直接双击 WuwaChest.exe 运行；不要从受限的终端或沙箱里启动它。",
                        "权限不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                Application.Run(new MainForm(cfg, startHidden, startTab));
                UpdateChecker.ApplyIfStaged(AppDomain.CurrentDomain.BaseDirectory);
                CdpSetup.RestoreOnExit(cfg);
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static void ActivateExistingWindow()
        {
            var handle = Native.FindWindow(null, WindowTitle);
            if (handle == IntPtr.Zero) return;
            Native.ShowWindow(handle, 5);   // SW_SHOW（托盘隐藏时也能拉出来）
            Native.ShowWindow(handle, 9);   // SW_RESTORE
            Native.SetForegroundWindow(handle);
        }
    }
}
