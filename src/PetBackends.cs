// PetBackends.cs — 读取/领取的两条实现：CDP（优先）与 UIA（兜底）
using System;

namespace WuwaChest
{
    internal interface IPetBackend
    {
        string Name { get; }
        PetSnapshot Read();
        bool Invoke(PetSnapshot snap, out string detail);
        ToastInfo FindToast(PetSnapshot before);
        string CaptureReward(string logsDir, ToastInfo toast, PetSnapshot snap, int index);
        string LastRewardLabel { get; }
    }

    // 传统 UIA：找窗口、读 Text、Invoke 按钮、屏幕截图
    internal sealed class UiaBackend : IPetBackend
    {
        private readonly PetClient _client;

        public UiaBackend(AppConfig cfg)
        {
            _client = new PetClient(cfg);
        }

        public string Name { get { return "uia"; } }

        public PetSnapshot Read()
        {
            return _client.Read();
        }

        public bool Invoke(PetSnapshot snap, out string detail)
        {
            return _client.Invoke(snap, out detail);
        }

        public ToastInfo FindToast(PetSnapshot before)
        {
            return _client.FindToast(before);
        }

        public string CaptureReward(string logsDir, ToastInfo toast, PetSnapshot snap, int index)
        {
            return Screen.CaptureReward(logsDir, toast, snap, index);
        }

        public string LastRewardLabel { get { return ""; } }
    }

    // CDP：直接读页面 JS、点按钮、截弹窗页（透明背景，只有卡片）
    internal sealed class CdpBackend : IPetBackend
    {
        private readonly CdpPetClient _cdp;
        private readonly UiaBackend _fallback;
        private readonly AppConfig _cfg;

        public CdpBackend(AppConfig cfg)
        {
            _cfg = cfg;
            _cdp = new CdpPetClient(cfg);
            _fallback = new UiaBackend(cfg);
        }

        public string Name { get { return "cdp"; } }

        public string LastRewardLabel { get { return _cdp.LastCardLabel; } }

        public PetSnapshot Read()
        {
            PetSnapshot snap = null;
            try { snap = _cdp.Read(); }
            catch { }

            if (snap != null && snap.Ok) return snap;

            // CDP 读失败（端口掉了/页面没起来）→ 退回 UIA，保证还能领
            var fallback = _fallback.Read();
            if (!fallback.Ok && snap != null)
                fallback.Reason = fallback.Reason + "（CDP：" + snap.Reason + "）";
            return fallback;
        }

        public bool Invoke(PetSnapshot snap, out string detail)
        {
            if (snap != null && snap.Source == "cdp")
            {
                string cdpDetail;
                if (_cdp.ClickReward(out cdpDetail))
                {
                    detail = cdpDetail;
                    return true;
                }
                // CDP 点不动就退回 UIA
                bool ok = _fallback.Invoke(snap, out detail);
                if (!ok) detail = cdpDetail + "；UIA：" + detail;
                return ok;
            }

            return _fallback.Invoke(snap, out detail);
        }

        public ToastInfo FindToast(PetSnapshot before)
        {
            var toast = new ToastInfo();
            string text = "";
            try { text = _cdp.ReadToastText(); }
            catch { }

            if (!string.IsNullOrEmpty(text))
            {
                toast.Text = text.Length > 120 ? text.Substring(0, 120) : text;
                toast.Found = true;
                return toast;
            }

            // 读不到文字就退回去找 UIA 元素（截图兜底用）
            try { return _fallback.FindToast(before); }
            catch { return toast; }
        }

        public string CaptureReward(string logsDir, ToastInfo toast, PetSnapshot snap, int index)
        {
            string relative = null;
            string reason = "";

            // 优先 DOM 合成：完全不截屏，桌宠不会闪
            string mode = _cfg == null ? "dom" : (_cfg.RewardShotMode ?? "dom");
            if (!string.Equals(mode, "screen", StringComparison.OrdinalIgnoreCase))
            {
                try { relative = _cdp.CaptureCardFromDom(logsDir, index, out reason, 6); }
                catch (Exception ex) { reason = ex.Message; }
                if (!string.IsNullOrEmpty(relative)) return relative;
                if (!string.IsNullOrEmpty(reason))
                    Log.Warn("DOM 合成奖励图失败（" + reason + "），改用 CDP 截图（可能闪一下）");
            }

            // 兜底：CDP 截图（透明背景，按 alpha 裁完只剩卡片）
            reason = "";
            try { relative = _cdp.CaptureToast(logsDir, index, out reason, 5); }
            catch (Exception ex) { reason = ex.Message; }
            if (!string.IsNullOrEmpty(relative)) return relative;

            if (!string.IsNullOrEmpty(reason))
                Log.Warn("CDP 奖励截图失败（" + reason + "），退回屏幕裁剪");
            return Screen.CaptureReward(logsDir, toast, snap, index);
        }
    }
}
