// ClaimEngine.cs — 后台检测/领取循环（独立线程，所有 UIA 调用都在这里发生）
using System;
using System.IO;
using System.Threading;

namespace WuwaChest
{
    internal sealed class EngineStatus
    {
        public PetSnapshot Pet;
        public DateTime PetAt = DateTime.MinValue;
        public bool Running;
        public bool Busy;
        public DateTime NextRun = DateTime.MinValue;
        public string StatusText = "未启动";
        public DateTime LastChangeAt = DateTime.MinValue;
    }

    internal sealed class ClaimEngine
    {
        private readonly object _lifecycle = new object();
        private readonly object _uia = new object();
        private readonly object _statusLock = new object();
        private readonly ManualResetEvent _wake = new ManualResetEvent(false);

        private volatile AppConfig _cfg;
        private readonly string _logsDir;

        private Thread _thread;
        private volatile bool _running;
        private volatile bool _checkNow;
        private bool _dryRun;

        private PetSnapshot _snap;
        private DateTime _snapAt = DateTime.MinValue;
        private DateTime _nextRun = DateTime.MinValue;
        private string _statusText = "未启动";
        private bool _busy;
        private int _peeking;
        private DateTime _lastPeek = DateTime.MinValue;
        private DateTime _lastTrigger = DateTime.MinValue;
        private int _claimSeq;
        private DateTime _claimSeqAt = DateTime.MinValue;
        private string _lastPushKey = "";
        private DateTime _lastChange = DateTime.MinValue;

        private sealed class Sample
        {
            public DateTime Time;
            public int Number;
        }

        private Sample _sample;

        // 当前使用的读取/领取通道（CDP 或 UIA），每次检测时刷新
        private IPetBackend _backend;
        private CdpWatcher _watcher;

        public ClaimEngine(AppConfig cfg, string logsDir)
        {
            _cfg = cfg;
            _logsDir = logsDir;
        }

        public bool IsRunning { get { return _running; } }

        public void UpdateConfig(AppConfig cfg)
        {
            if (cfg != null) _cfg = cfg;
        }

        public void SetDryRun(bool dryRun)
        {
            _dryRun = dryRun;
        }

        public EngineStatus GetStatus()
        {
            var status = new EngineStatus();
            lock (_statusLock)
            {
                status.Pet = _snap;
                status.PetAt = _snapAt;
                status.NextRun = _nextRun;
                status.StatusText = _statusText;
                status.LastChangeAt = _lastChange;
                status.Busy = _busy;
            }
            status.Running = _running;
            return status;
        }

        public void Start()
        {
            lock (_lifecycle)
            {
                if (_running) return;
                _running = true;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Name = "WuwaChestEngine";
                try { _thread.SetApartmentState(ApartmentState.STA); } catch { }
                _thread.Start();

                // CDP 可用时顺便开一个常驻监听，页面状态一变就推上来
                if (_cfg.CdpEnabled)
                {
                    try
                    {
                        int port = CdpDiscovery.ResolvePort(_cfg.CdpPort);
                        if (CdpDiscovery.IsAvailable(port))
                        {
                            _watcher = new CdpWatcher(port, _cfg);
                            _watcher.Snapshot += OnWatcherSnapshot;
                            _watcher.Start();
                        }
                    }
                    catch { }
                }
            }
        }

        public void Stop()
        {
            Thread thread;
            lock (_lifecycle)
            {
                if (!_running) return;
                _running = false;
                _wake.Set();
                thread = _thread;
                _thread = null;
            }
            if (thread != null)
            {
                try { thread.Join(2500); } catch { }
            }
            if (_watcher != null) { _watcher.Stop(); _watcher = null; }
        }

        // 立即检测：监控在跑就插队，没跑就单独跑一次
        public void CheckNow()
        {
            if (_running)
            {
                _checkNow = true;
                _wake.Set();
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { DoCheck(); }
                catch (Exception ex) { Log.Error("检测异常：" + ex.Message); }
            });
        }

        // 监控停止时也让面板能看到实时字符数：只读、不领取、不打日志
        public void Peek()
        {
            if (_running) return;
            if (System.Threading.Interlocked.CompareExchange(ref _peeking, 1, 0) != 0) return;
            if ((DateTime.Now - _lastPeek).TotalSeconds < 5)
            {
                System.Threading.Interlocked.Exchange(ref _peeking, 0);
                return;
            }
            _lastPeek = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    lock (_uia) { Publish(GetBackend(_cfg).Read()); }
                }
                catch { }
                finally { System.Threading.Interlocked.Exchange(ref _peeking, 0); }
            });
        }

        // 阻塞式跑一次（命令行模式用）
        public void RunOnceSync()
        {
            lock (_uia)
            {
                DoCheck();
            }
        }

        private void Loop()
        {
            _nextRun = DateTime.Now;
            while (_running)
            {
                try
                {
                    if (_checkNow)
                    {
                        _checkNow = false;
                        DoCheck();
                        continue;
                    }

                    if (DateTime.Now >= _nextRun)
                    {
                        DoCheck();
                        continue;
                    }

                    if ((DateTime.Now - _snapAt).TotalSeconds >= Math.Max(1, _cfg.HeartbeatSeconds))
                        Heartbeat();

                    _wake.WaitOne(250);
                }
                catch (Exception ex)
                {
                    Log.Error("监控异常：" + ex.Message);
                    Thread.Sleep(2000);
                }
            }
        }

        private void Heartbeat()
        {
            if (_watcher != null && _watcher.IsRunning) return;   // 有推送就不用轮询
            PetSnapshot snap;
            lock (_uia)
            {
                try { snap = GetBackend(_cfg).Read(); }
                catch { return; }
            }
            Publish(snap);
        }

        private void DoCheck()
        {
            var cfg = _cfg;
            PetSnapshot snap;
            string status;
            int number = 0;

            lock (_uia)
            {
                SetBusy(true);
                try
                {
                    _backend = SelectBackend(cfg);
                    snap = ReadWithRetry(cfg, _backend);
                    Publish(snap);

                    if (!snap.Ok)
                    {
                        Log.Warn("本次检测跳过：" + snap.Reason);
                        status = "notReady";
                    }
                    else
                    {
                        number = snap.Number;
                        status = ClaimIfPossible(cfg, _backend, ref snap);
                    }
                }
                finally
                {
                    SetBusy(false);
                }
            }

            // 用「上一次样本」算 ETA，再把本次样本记下来（顺序反了 ETA 永远算不出来）
            double delay = ComputeDelay(cfg, status, number, snap);
            if ((status == "noChest" || status == "insufficient") && number > 0)
                _sample = new Sample { Time = DateTime.Now, Number = number };
            else
                _sample = null;

            var next = DateTime.Now.AddMinutes(delay);
            lock (_statusLock)
            {
                _nextRun = next;
                _statusText = Describe(status);
            }

            Log.Info(string.Format("下次检测：{0}（{1}）",
                next.ToString("yyyy-MM-dd HH:mm:ss"), FormatDelay(delay)));
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // 把延迟格式化成「约 X 分钟」或「约 X 秒」
        private static string FormatDelay(double minutes)
        {
            if (minutes < 1) return string.Format("约 {0:0} 秒后", minutes * 60);
            return string.Format("约 {0:0.0} 分钟后", minutes);
        }

        // 下一次「到期」距离现在多少分钟（没有时间戳时返回 0）
        private static double MinutesToNextDue(PetSnapshot snap, long now)
        {
            if (snap == null || !snap.HasChestTimers) return 0;
            long next = 0;
            if (snap.LeftDueMs > now && (next == 0 || snap.LeftDueMs < next)) next = snap.LeftDueMs;
            if (snap.RightDueMs > now && (next == 0 || snap.RightDueMs < next)) next = snap.RightDueMs;
            if (next == 0) return 0;
            return (next - now) / 60000.0;
        }

        private string ClaimIfPossible(AppConfig cfg, IPetBackend backend, ref PetSnapshot snap)
        {
            const int threshold = AppConfig.ClaimCost;
            const int maxClaims = 2;
            string tag = backend.Name == "cdp" ? "CDP" : "UIA";
            long now = NowMs();

            if (!snap.Chest)
            {
                if (snap.NextChestSeconds > 0)
                {
                    Log.Info(string.Format("检测（{0}）：字符数 = {1}，下一个宝箱还有 {2} 分 {3} 秒",
                        tag, snap.Number, snap.NextChestSeconds / 60, snap.NextChestSeconds % 60));
                    return "waiting";
                }

                if (snap.Balance >= 0 && snap.Balance < threshold)
                {
                    Log.Info(string.Format("检测（{0}）：字符数 = {1}，余额不够 {2}，宝箱按钮被页面隐藏",
                        tag, snap.Number, threshold));
                    return "insufficient";
                }

                Log.Info(string.Format("检测（{0}）：字符数 = {1}，宝箱未出现  -> 不操作", tag, snap.Number));
                return "noChest";
            }

            if (snap.Number < threshold)
            {
                Log.Info(string.Format("检测（{0}）：字符数 = {1}，宝箱已出现，但还差 {2} 才能领取（每次 {3}），等待累积",
                    tag, snap.Number, threshold - snap.Number, threshold));
                return "insufficient";
            }

            string status = "noChest";
            int claims = 0;

            for (int round = 1; round <= maxClaims; round++)
            {
                if (!snap.Chest || !snap.HasNumber || snap.Number < threshold) break;

                Log.Info(string.Format("检测（{2}）：字符数 = {0}，宝箱已出现（{1}），准备领取", snap.Number, snap.Info, tag));

                if (_dryRun)
                {
                    Log.Action("预演模式：跳过点击");
                    return "dryrun";
                }

                string detail;
                if (!backend.Invoke(snap, out detail))
                {
                    Log.Error("宝箱点击失败：" + detail);
                    return "failed";
                }
                Log.Action("已通过" + detail + "触发宝箱按钮");

                string shotRel = null;
                string rewardName = "";
                int totalDelay = Math.Max(300, cfg.VerifyDelayMs);

                if (cfg.RewardScreenshots)
                {
                    int shotAt = Math.Min(cfg.RewardShotDelayMs, totalDelay - 150);
                    if (shotAt < 200) shotAt = 200;
                    Thread.Sleep(shotAt);

                    ToastInfo toast = null;
                    try { toast = backend.FindToast(snap); }
                    catch { }

                    shotRel = backend.CaptureReward(_logsDir, toast, snap, claims + 1);
                    if (toast != null && !string.IsNullOrEmpty(toast.Text)) rewardName = toast.Text.Trim();
                      // 弹窗文字读不到时，用 DOM 卡片里的标签（新角色/新配件/新表情/重复收集物）
                      if (string.IsNullOrEmpty(rewardName))
                      {
                          try { rewardName = (backend.LastRewardLabel ?? "").Trim(); } catch { }
                      }

                    int remain = totalDelay - shotAt;
                    if (remain > 0) Thread.Sleep(remain);
                }
                else
                {
                    Thread.Sleep(totalDelay);
                }

                PetSnapshot after = null;
                try { after = backend.Read(); }
                catch { }

                if (after == null || !after.Ok)
                {
                    Log.Warn("点击后复查失败，无法确认结果");
                    return "failed";
                }

                bool chestGone = !after.Chest;
                bool dropped = after.HasNumber && after.Number < snap.Number;

                if (!chestGone && !dropped)
                {
                    Log.Warn(string.Format("点击后状态未变化（字符数仍为 {0}，宝箱仍在）", after.Number));
                    return "failed";
                }

                claims++;
                // 编号跨轮连续：同一波领取算 1、2，隔一分钟再领重新从 1 开始
                if ((DateTime.Now - _claimSeqAt).TotalSeconds > 60) _claimSeq = 0;
                _claimSeq++;
                _claimSeqAt = DateTime.Now;

                status = "claimed";

                int before = snap.Number;
                string tail = chestGone ? "，已领取（30 秒后复查是否还有）" : "，槽位里还有宝箱";
                string extra = "";
                if (!string.IsNullOrEmpty(shotRel)) extra += "｜截图 " + shotRel;
                if (!string.IsNullOrEmpty(rewardName)) extra += "｜奖励 " + rewardName;

                Log.Action(string.Format("领取成功（本次第 {0} 个）：字符数 {1} -> {2}{3}{4}",
                    _claimSeq, before, after.Number, tail, extra));

                snap = after;
                Publish(snap);
            }

            return status;
        }

        private IPetBackend SelectBackend(AppConfig cfg)
        {
            if (cfg.CdpEnabled)
            {
                try
                {
                    int port = CdpDiscovery.ResolvePort(cfg.CdpPort);
                    if (CdpDiscovery.IsAvailable(port))
                        return new CdpBackend(cfg);
                }
                catch { }
            }
            return new UiaBackend(cfg);
        }

        private IPetBackend GetBackend(AppConfig cfg)
        {
            var backend = _backend;
            if (backend != null) return backend;

            backend = SelectBackend(cfg);
            _backend = backend;
            return backend;
        }

        private PetSnapshot ReadWithRetry(AppConfig cfg, IPetBackend backend)
        {
            const int attempts = 2;
            PetSnapshot snap = null;

            for (int i = 1; i <= attempts; i++)
            {
                try { snap = backend.Read(); }
                catch (Exception ex) { snap = new PetSnapshot { Reason = ex.Message }; }

                if (snap != null && snap.Ok) return snap;

                if (i < attempts)
                    Thread.Sleep(2000);
            }

            return snap ?? new PetSnapshot { Reason = "读取失败" };
        }

        // 有 CDP 时间戳时按「到期 + 2 秒」精确唤醒；没有时间戳（UIA）时退回基础间隔
        private double ComputeDelay(AppConfig cfg, string status, int number, PetSnapshot snap)
        {
            const double baseMinutes = AppConfig.FallbackIntervalMinutes;

            switch (status)
            {
                case "claimed":
                case "failed":
                    // 领完/失败：30 秒后复查（失败重试；领完则重新读页面排下一次）
                    return 0.5;

                case "dryrun":
                    return Math.Min(baseMinutes, 3);

                case "notReady":
                    return Math.Max(1, cfg.NotReadyRetryMinutes);

                case "insufficient":
                    // 余额不够：5 分钟后重新检查（期间攒字符）
                    return 5;

                case "waiting":
                    // 界面倒计时为准：到期后等 2 秒再动手（早了会被拒：code 70006 宝箱冷却中）
                    if (snap != null && snap.NextChestSeconds > 0)
                    {
                        double minutes = snap.NextChestSeconds / 60.0 + 2.0 / 60.0;
                        return Math.Max(0.5, Math.Min(70, minutes));
                    }
                    return baseMinutes;

                case "noChest":
                    return baseMinutes;

                default:
                    return baseMinutes;
            }
        }

        private static string Describe(string status)
        {
            switch (status)
            {
                case "notReady": return "读不到桌宠界面";
                case "noChest": return "等待宝箱";
                case "waiting": return "等待宝箱";
                case "insufficient": return "字符数不足";
                case "claimed": return "刚领取";
                case "failed": return "上次点击失败";
                case "dryrun": return "预演";
                default: return "空闲";
            }
        }

        private void OnWatcherSnapshot(PetSnapshot snap)
        {
            // 只有关键字段变化才刷新“状态更新”时间，避免倒计时每秒都触发重绘
            string key = snap.Number + "|" + (snap.Chest ? 1 : 0) + "|" + snap.Balance;
            lock (_statusLock)
            {
                _snap = snap;
                _snapAt = DateTime.Now;
                if (key != _lastPushKey) { _lastPushKey = key; _lastChange = DateTime.Now; }
            }
            // 页面推过来"宝箱按钮出现了"，立刻插队去领
            // 按钮出现就插队去领，但别让它变成死循环
            if (snap.Chest && !_busy && (DateTime.Now - _lastTrigger).TotalSeconds >= 3)
            {
                _lastTrigger = DateTime.Now;
                _checkNow = true;
                _wake.Set();
            }
        }

        private void Publish(PetSnapshot snap)
        {
            lock (_statusLock)
            {
                _snap = snap;
                _snapAt = DateTime.Now;
                _lastChange = DateTime.Now;
            }
        }

        private void SetBusy(bool busy)
        {
            lock (_statusLock) { _busy = busy; }
        }
    }
}
