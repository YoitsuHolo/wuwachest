// CloudSyncScheduler.cs — 定时云同步：按配置的间隔自动「先拉取、再上传」
using System;

namespace WuwaChest
{
    internal static class CloudSyncScheduler
    {
        private static System.Threading.Timer _timer;
        private static Func<AppConfig> _cfg;
        private static string _dataDir;
        private static DateTime _lastRun = DateTime.MinValue;
        private static int _busy;

        public static void Start(Func<AppConfig> cfgProvider, string dataDir)
        {
            Stop();
            _cfg = cfgProvider;
            _dataDir = dataDir;
            _lastRun = DateTime.MinValue;
            // 每 60 秒检查一次是否到点，第一次在启动 2 分钟后
            _timer = new System.Threading.Timer(Tick, null, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(60));
        }

        public static void Stop()
        {
            if (_timer != null)
            {
                try { _timer.Dispose(); } catch { }
                _timer = null;
            }
        }

        // 写进 logs\\sync.txt：这个文件不会被同步，避免"同步日志被同步"的死循环
        private static void Note(string text)
        {
            try
            {
                string dir = System.IO.Path.Combine(_dataDir, "logs");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "sync.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
            }
            catch { }
        }

        private static void Tick(object state)
        {
            try
            {
                if (_cfg == null) return;
                var cfg = _cfg();
                if (cfg == null || !cfg.CloudAutoSync) return;

                int minutes = cfg.CloudSyncMinutes < 1 ? 1 : cfg.CloudSyncMinutes;
                if ((DateTime.Now - _lastRun).TotalMinutes < minutes) return;

                // 上一次还没跑完就跳过，避免叠加
                if (System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

                try
                {
                    _lastRun = DateTime.Now;

                    string pull = CloudSync.Pull(cfg, _dataDir);
                    Note("定时拉取：" + pull);

                    string push = CloudSync.Upload(cfg, _dataDir);
                    Note("定时上传：" + push);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _busy, 0);
                }
            }
            catch (Exception ex)
            {
                try { Note("异常：" + ex.Message); } catch { }
            }
        }
    }
}
