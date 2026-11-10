// CdpClient.cs — 通过 WebView2 的 DevTools 协议（CDP）读写桌宠页面
// 依赖：.NET Framework 4.5+ 自带的 ClientWebSocket / HttpWebRequest，无需第三方库
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WuwaChest
{
    internal sealed class CdpTarget
    {
        public string Id;
        public string Url;
        public string Title;
        public string WebSocketUrl;
    }

    internal static class CdpDiscovery
    {
        private static DateTime _checkedAt = DateTime.MinValue;
        private static bool _cachedResult;
        private static int _cachedPort;

        // 1.5 秒内能拿到 /json/version 才算可用（结果缓存 30 秒，避免每轮都等超时）
        public static bool IsAvailable(int port)
        {
            if (port == _cachedPort && (DateTime.Now - _checkedAt).TotalSeconds < 30)
                return _cachedResult;

            bool ok = false;
            try
            {
                var text = GetFromPort(port, "/json/version", 1500);
                ok = text != null && text.IndexOf("webSocketDebuggerUrl", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { ok = false; }

            _checkedAt = DateTime.Now;
            _cachedPort = port;
            _cachedResult = ok;
            return ok;
        }

        private static int _resolvedPort;
        private static DateTime _resolvedAt = DateTime.MinValue;

        // 端口可能被改过（策略里写什么就用什么），所以先看配置，再看策略，再看常见默认
        public static int ResolvePort(int configured)
        {
            if (_resolvedPort != 0 && (DateTime.Now - _resolvedAt).TotalSeconds < 30)
                return _resolvedPort;

            int port = configured;
            if (!IsAvailable(configured))
            {
                int fromPolicy = ReadPolicyPort();
                if (fromPolicy > 0 && fromPolicy != configured && IsAvailable(fromPolicy)) port = fromPolicy;
                else if (IsAvailable(9222)) port = 9222;
            }

            _resolvedPort = port;
            _resolvedAt = DateTime.Now;
            return port;
        }

        private static int ReadPolicyPort()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments"))
                {
                    if (key == null) return 0;
                    var value = key.GetValue("WuwaTappo.exe") as string;
                    if (string.IsNullOrEmpty(value)) return 0;
                    var match = Regex.Match(value, @"--remote-debugging-port=(\d+)");
                    int port;
                    if (match.Success && int.TryParse(match.Groups[1].Value, out port)) return port;
                }
            }
            catch { }
            return 0;
        }
        public static List<CdpTarget> ListTargets(int port)
        {
            var list = new List<CdpTarget>();
            string json;
            try { json = GetFromPort(port, "/json/list", 2000); }
            catch { return list; }
            if (string.IsNullOrEmpty(json)) return list;

            // 极简解析：每个 target 是一个 {…}，字段固定
            foreach (Match block in Regex.Matches(json, "\\{[^{}]*\\}"))
            {
                string text = block.Value;
                var target = new CdpTarget
                {
                    Id = Extract(text, "id"),
                    Url = Extract(text, "url"),
                    Title = Extract(text, "title"),
                    WebSocketUrl = Extract(text, "webSocketDebuggerUrl")
                };
                if (!string.IsNullOrEmpty(target.WebSocketUrl)) list.Add(target);
            }
            return list;
        }

        private static string Extract(string text, string key)
        {
            var m = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return Unescape(m.Groups[1].Value);
        }

        // WebView2 与别的程序可能各占 IPv4/IPv6 的同号端口，所以两个主机名都试
        private static string GetFromPort(int port, string path, int timeoutMs)
        {
            string body = TryGet("http://127.0.0.1:" + port + path, timeoutMs);
            if (body != null) return body;
            return TryGet("http://localhost:" + port + path, timeoutMs);
        }

        private static string TryGet(string url, int timeoutMs)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Proxy = null;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.KeepAlive = false;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch { return null; }
        }

        private static string Get(string url, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.KeepAlive = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        internal static string Unescape(string text)
        {
            if (text == null) return null;
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\' || i + 1 >= text.Length) { sb.Append(c); continue; }
                char next = text[++i];
                switch (next)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < text.Length)
                        {
                            int code;
                            if (int.TryParse(text.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                                sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(next); break;
                }
            }
            return sb.ToString();
        }

        internal static string Escape(string text)
        {
            if (text == null) return "";
            var sb = new StringBuilder(text.Length + 16);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // 一条 WebSocket 会话（一个页面）
    internal sealed class CdpSession : IDisposable
    {
        private readonly ClientWebSocket _socket;
        private int _nextId = 1;
        private readonly StringBuilder _buffer = new StringBuilder();

        private CdpSession(ClientWebSocket socket)
        {
            _socket = socket;
        }

        public static CdpSession Connect(string webSocketUrl, int timeoutMs)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            var task = socket.ConnectAsync(new Uri(webSocketUrl), CancellationToken.None);
            if (!task.Wait(timeoutMs))
            {
                socket.Dispose();
                return null;
            }
            return new CdpSession(socket);
        }

        // 返回指定页面的 JS 字符串结果；失败返回 null
        public string EvaluateString(string expression, int timeoutMs, bool awaitPromise = false)
        {
            string parameters = "{\"expression\":\"" + CdpDiscovery.Escape(expression) +
                                "\",\"returnByValue\":true" + (awaitPromise ? ",\"awaitPromise\":true" : "") + "}";
            string response = Call("Runtime.evaluate", parameters, timeoutMs);
            if (response == null) return null;

            // {"id":1,"result":{"result":{"type":"string","value":"..."}}}
            var m = Regex.Match(response, "\"result\"\\s*:\\s*\\{[^{}]*\"value\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success)
            {
                // 数值型结果
                m = Regex.Match(response, "\"result\"\\s*:\\s*\\{[^{}]*\"value\"\\s*:\\s*(-?[0-9.]+)");
                return m.Success ? m.Groups[1].Value : null;
            }
            return CdpDiscovery.Unescape(m.Groups[1].Value);
        }

        // 整页截图，返回 PNG 字节（omitBackground=true 时背景透明）
        public byte[] CaptureScreenshot(bool omitBackground, int timeoutMs)
        {
            string parameters = "{\"format\":\"png\",\"omitBackground\":" +
                                (omitBackground ? "true" : "false") +
                                ",\"captureBeyondViewport\":false}";
            string response = Call("Page.captureScreenshot", parameters, timeoutMs);
            if (response == null) return null;
            var m = Regex.Match(response, "\"data\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) return null;
            try { return Convert.FromBase64String(m.Groups[1].Value); }
            catch { return null; }
        }

        // 按矩形裁剪截图（弹窗卡片就在主页面上，直接按 DOM 坐标截）
        public byte[] CaptureScreenshotClip(double x, double y, double width, double height,
            bool omitBackground, double scale, int timeoutMs)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            string parameters = "{\"format\":\"png\",\"omitBackground\":" +
                                (omitBackground ? "true" : "false") +
                                ",\"clip\":{\"x\":" + x.ToString("0.##", culture) +
                                ",\"y\":" + y.ToString("0.##", culture) +
                                ",\"width\":" + width.ToString("0.##", culture) +
                                ",\"height\":" + height.ToString("0.##", culture) +
                                ",\"scale\":" + scale.ToString("0.##", culture) + "}" +
                                ",\"captureBeyondViewport\":false}";
            string response = Call("Page.captureScreenshot", parameters, timeoutMs);
            if (response == null) return null;
            var m = Regex.Match(response, "\"data\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) return null;
            try { return Convert.FromBase64String(m.Groups[1].Value); }
            catch { return null; }
        }

        private string Call(string method, string parametersJson, int timeoutMs)
        {
            int id = _nextId++;
            string payload = "{\"id\":" + id + ",\"method\":\"" + method + "\"" +
                             (string.IsNullOrEmpty(parametersJson) ? "" : ",\"params\":" + parametersJson) + "}";

            if (!Send(payload)) return null;

            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                string message = Receive((int)Math.Max(200, (deadline - DateTime.Now).TotalMilliseconds));
                if (message == null) return null;
                if (Regex.IsMatch(message, "\"id\"\\s*:\\s*" + id + "\\b")) return message;
            }
            return null;
        }

        private bool Send(string payload)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                var task = _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                return task.Wait(3000);
            }
            catch { return false; }
        }

        private string Receive(int timeoutMs)
        {
            try
            {
                _buffer.Length = 0;
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var task = _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (!task.Wait(timeoutMs)) return null;
                    var result = task.Result;
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    _buffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage) break;
                }
                return _buffer.ToString();
            }
            catch { return null; }
        }

        public void Dispose()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    var close = _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    close.Wait(800);
                }
            }
            catch { }
            try { _socket.Dispose(); } catch { }
        }
    }

    // 面向桌宠页面的高层封装
    internal sealed class CdpPetClient
    {
        private readonly int _port;
        public int Port { get { return _port; } }
        private readonly AppConfig _cfg;

        public CdpPetClient(AppConfig cfg)
        {
            _cfg = cfg;
            _port = CdpDiscovery.ResolvePort(cfg.CdpPort);
        }

        public bool IsAvailable()
        {
            return CdpDiscovery.IsAvailable(_port);
        }

        private CdpTarget FindMainPage()
        {
            foreach (var target in CdpDiscovery.ListTargets(_port))
            {
                if (target.Url == null) continue;
                if (target.Url.IndexOf("tauri.localhost", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (target.Url.EndsWith("#/", StringComparison.Ordinal) ||
                    target.Url.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
                    return target;
            }
            return null;
        }

        private CdpTarget FindToastPage()
        {
            foreach (var target in CdpDiscovery.ListTargets(_port))
            {
                if (!string.IsNullOrEmpty(target.Url) &&
                    target.Url.IndexOf("#/toast", StringComparison.OrdinalIgnoreCase) >= 0)
                    return target;
            }
            return null;
        }

        private const string ReadStateScript =
            "(function(){" +
            "var n='';var els=document.querySelectorAll('.text.web-bold-font');" +
            "for(var i=0;i<els.length;i++){var t=(els[i].innerText||'').trim();if(/^[0-9,]+$/.test(t)){n=t;break;}}" +
            "var b=document.querySelector('.reward-btn');var dpr=window.devicePixelRatio||1;" +
            "var r=b?b.getBoundingClientRect():null;" +
            "var app=document.querySelector('#app');" +
            "var pinia=app&&app.__vue_app__?app.__vue_app__.config.globalProperties.$pinia:null;" +
            "var st=pinia?pinia._s.get('GlobalState'):null;" +
            "var u=st&&st.$state?st.$state.userInfo||{}:{};" +
            "var left=Number(u.left_treasure_chest||0),right=Number(u.right_treasure_chest||0);" +
            "var total=Number(u.total_click_count||0),consume=Number(u.total_click_consume||0);" +
            "var cum=st?Number(st.cumulativeClickCount||0):total;" +
            "var logged=st&&st.isLoggedIn?1:0;" +
            "var cds=document.querySelectorAll('.reward-countdown');var next=0;" +
            "for(var j=0;j<cds.length;j++){var p=((cds[j].innerText||'').match(/\\d+/g)||[]).map(Number);var sec=p.length===3?p[0]*3600+p[1]*60+p[2]:(p.length===2?p[0]*60+p[1]:0);if(sec>0&&(next===0||sec<next))next=sec;}" +
            "return [n,b?1:0,r?Math.round(r.left*dpr):0,r?Math.round(r.top*dpr):0,r?Math.round(r.width*dpr):0,r?Math.round(r.height*dpr):0,left,right,total-consume,logged,Math.round(cum),next].join('|');" +
            "})()";

        // 用 CDP 读一次状态，返回和 UIA 一样的 PetSnapshot（额外带两个宝箱时间戳和余额）
        public PetSnapshot Read()
        {
            var snap = new PetSnapshot();
            var page = FindMainPage();
            if (page == null)
            {
                snap.Reason = "CDP：找不到桌宠主页面（页面可能还没加载完）";
                return snap;
            }

            string raw = null;
            using (var session = CdpSession.Connect(page.WebSocketUrl, 4000))
            {
                if (session == null)
                {
                    snap.Reason = "CDP：连接调试端口失败";
                    return snap;
                }
                raw = session.EvaluateString(ReadStateScript, 4000);
            }

            if (string.IsNullOrEmpty(raw))
            {
                snap.Reason = "CDP：读取页面状态失败";
                return snap;
            }

            var parts = raw.Split('|');
            if (parts.Length < 12)
            {
                snap.Reason = "CDP：状态格式异常";
                return snap;
            }

            int number;
            string digits = Regex.Replace(parts[0], "[^0-9]", "");
            int.TryParse(digits, out number);

            snap.Ok = true;
            snap.HasNumber = digits.Length > 0;
            snap.Number = number;
            snap.Chest = parts[1] == "1";

            long left, right;
            long.TryParse(parts[6], out left);
            long.TryParse(parts[7], out right);
            int balance;
            int.TryParse(parts[8], out balance);
            int cumulative;
            int.TryParse(parts[10], out cumulative);

            snap.LoggedIn = parts[9] == "1";
            snap.Balance = balance;
            snap.CumulativeClicks = cumulative;
            snap.LeftDueMs = left;
            snap.RightDueMs = right;
            snap.HasChestTimers = left > 0 || right > 0;
            int nextSec;
            if (int.TryParse(parts[11], out nextSec)) snap.NextChestSeconds = nextSec;

            if (snap.Chest)
            {
                snap.ChestX = double.Parse(parts[2]);
                snap.ChestY = double.Parse(parts[3]);
                snap.ChestW = double.Parse(parts[4]);
                snap.ChestH = double.Parse(parts[5]);
                snap.ChestByClass = true;
                snap.Info = string.Format("宝箱 {0},{1} {2}x{3}（CDP）",
                    (int)snap.ChestX, (int)snap.ChestY, (int)snap.ChestW, (int)snap.ChestH);
                snap.ChestElement = null;
            }
            snap.Source = "cdp";
            return snap;
        }

        public bool ClickReward(out string detail)
        {
            detail = "";
            var page = FindMainPage();
            if (page == null) { detail = "CDP：找不到主页面"; return false; }

            const string script =
                "(function(){var b=document.querySelector('.reward-btn');" +
                "if(!b) return 'no-button';" +
                "b.click();return 'clicked';})()";

            using (var session = CdpSession.Connect(page.WebSocketUrl, 4000))
            {
                if (session == null) { detail = "CDP：连接失败"; return false; }
                string result = session.EvaluateString(script, 4000);
                if (result == "clicked") { detail = "CDP 页面点击"; return true; }
                detail = "CDP 点击返回：" + (result ?? "空");
                return false;
            }
        }

        // 走桌宠自带的内部通道加字符：cat-panel-action / boost-local-clicks
        // 这是官方程序里就有的隐藏动作（原始按钮 delta=10000），实测本地计数会累加，
        // 随后由桌宠自己的点击上报同步到服务端（total_click_count）。
        public bool BoostLocalClicks(int delta, out string detail)
        {
            detail = "";
            var page = FindMainPage();
            if (page == null) { detail = "CDP：找不到主页面"; return false; }
            if (delta <= 0) { detail = "数量必须大于 0"; return false; }

            // 新版桌宠删掉了官方的 boost-local-clicks 动作，但 store 里的本地计数还在，
            // 而且桌宠会自己把这些计数上报给服务端（实测 10 秒内 total_click_count 会涨）。
            string script =
                "(function(){" +
                "var a=document.querySelector('#app');" +
                "if(!a||!a.__vue_app__)return 'nostore';" +
                "var p=a.__vue_app__.config.globalProperties.$pinia;" +
                "var gs=p?p._s.get('GlobalState'):null;if(!gs)return 'nostore';" +
                "if(typeof gs.incrementLocalClickCount!=='function')return 'noaction';" +
                "var before=Number(gs.localClickCount||0);" +
                "for(var i=0;i<" + delta + ";i++){ gs.incrementLocalClickCount(); }" +
                "var after=Number(gs.localClickCount||0);" +
                "return 'delta:'+(after-before);" +
                "})()";

            using (var session = CdpSession.Connect(page.WebSocketUrl, 4000))
            {
                if (session == null) { detail = "CDP：连接失败"; return false; }
                string result = session.EvaluateString(script, 15000);

                if (result == "nostore") { detail = "页面里没有 store"; return false; }
                if (result == "noaction") { detail = "这个版本没有本地计数接口"; return false; }

                if (!string.IsNullOrEmpty(result) && result.StartsWith("delta:", StringComparison.Ordinal))
                {
                    int added;
                    int.TryParse(result.Substring(6), out added);
                    if (added > 0)
                    {
                        detail = "本地计数 +" + added + "（桌宠会在几秒内上报）";
                        return true;
                    }
                    detail = "本地计数没有变化";
                    return false;
                }

                detail = "返回：" + (result ?? "空");
                return false;
            }
        }

        public string ReadToastText()
        {
            var page = FindToastPage();
            if (page == null) return "";
            const string script =
                "(function(){var e=document.querySelector('.toast-overlay-root');" +
                "if(!e) return '';" +
                "var t=(e.innerText||'').replace(/\\d{1,3}:\\d{2}/g,' ')  " +
                ".replace(/\\s+/g,' ').trim();" +
                "return t.slice(0,160);})()";
            using (var session = CdpSession.Connect(page.WebSocketUrl, 4000))
            {
                if (session == null) return "";
                string text = session.EvaluateString(script, 4000);
                return text ?? "";
            }
        }

        private const string PopupRectScript =
            // 只取「最后一个」弹窗（DOM 里最新挂上的那个），避免上一张还没淡出时把两张算在一起
            "(function(){var list=document.querySelectorAll('.chest-reward-popup');var root=null;" +
            "for(var i=list.length-1;i>=0;i--){var b=list[i].getBoundingClientRect();" +
            "if(b.width>=40&&b.height>=40&&b.width<=900&&b.height<=900){root=list[i];break;}}" +
            "var els=[];" +
            "if(root){els.push(root);var kids=root.querySelectorAll('*');for(var k=0;k<kids.length;k++)els.push(kids[k]);}" +
            "else{var l2=document.querySelectorAll('.chest-reward-popup-layer>div');" +
            "for(var j=l2.length-1;j>=0;j--){var bb=l2[j].getBoundingClientRect();" +
            "if(bb.width>=40&&bb.height>=40&&bb.width<=900&&bb.height<=900){els.push(l2[j]);break;}}}" +
            "var minx=1e9,miny=1e9,maxx=-1,maxy=-1;var dpr=window.devicePixelRatio||1;" +
            "for(var m=0;m<els.length;m++){var e=els[m];var b=e.getBoundingClientRect();" +
            "if(b.width<40||b.height<40)continue;var cs=getComputedStyle(e);" +
            "if(cs.display==='none'||cs.visibility==='hidden')continue;" +
            "if(parseFloat(cs.opacity||'1')<0.05)continue;" +
            "if(b.left<minx)minx=b.left;if(b.top<miny)miny=b.top;" +
            "if(b.right>maxx)maxx=b.right;if(b.bottom>maxy)maxy=b.bottom;}" +
            "if(maxx<0)return 'none';" +
            "return [minx,miny,(maxx-minx),(maxy-miny),dpr].join(',');})()";

        // 奖励卡片其实渲染在主页面上（.chest-reward-popup），按它的坐标裁剪，背景天然透明
        public string CaptureRewardCard(string logsDir, int index, out string reason, int attempts)
        {
            reason = "";
            var page = FindMainPage();
            if (page == null) { reason = "找不到主页面"; return null; }

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                double[] rect;
                string error;
                if (TryGetPopupRect(page, out rect, out error))
                {
                    byte[] png = null;
                    try
                    {
                        using (var session = CdpSession.Connect(page.WebSocketUrl, 3000))
                        {
                            if (session != null)
                            {
                                png = session.CaptureScreenshotClip(
                                    rect[0] - 12, rect[1] - 10, rect[2] + 24, rect[3] + 52,
                                    true, rect[4], 6000);
                            }
                        }
                    }
                    catch (Exception ex) { reason = "截图异常：" + ex.Message; }

                    if (png != null && png.Length > 0)
                    {
                        try
                        {
                            using (var source = new MemoryStream(png))
                            using (var image = Image.FromStream(source))
                            {
                                string dir = Path.Combine(logsDir, "rewards");
                                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                                string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + index + ".png";
                                using (var bitmap = new Bitmap(image))
                                {
                                    // 再按 alpha 收一次边，去掉弹窗元素自带的透明留白
                                    var croppedBitmap = CropTransparent(bitmap);
                                    if (croppedBitmap != null)
                                    {
                                        croppedBitmap.Save(Path.Combine(dir, name), ImageFormat.Png);
                                        croppedBitmap.Dispose();
                                    }
                                    else
                                    {
                                        bitmap.Save(Path.Combine(dir, name), ImageFormat.Png);
                                    }
                                }
                                return "rewards/" + name;
                            }
                        }
                        catch (Exception ex) { reason = "保存失败：" + ex.Message; }
                    }
                    else if (string.IsNullOrEmpty(reason)) reason = "截图为空";
                }
                else reason = error;

                System.Threading.Thread.Sleep(400);
            }
            return null;
        }

        private bool TryGetPopupRect(CdpTarget page, out double[] rect, out string reason)
        {
            rect = null;
            reason = "";
            try
            {
                using (var session = CdpSession.Connect(page.WebSocketUrl, 3000))
                {
                    if (session == null) { reason = "连接失败"; return false; }
                    string raw = session.EvaluateString(PopupRectScript, 4000);
                    if (string.IsNullOrEmpty(raw) || raw == "none") { reason = "弹窗还没出现"; return false; }

                    var parts = raw.Split(',');
                    if (parts.Length < 5) { reason = "坐标解析失败"; return false; }

                    var values = new double[5];
                    for (int i = 0; i < 5; i++)
                    {
                        if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out values[i]))
                        {
                            reason = "坐标解析失败";
                            return false;
                        }
                    }
                    rect = values;
                    return true;
                }
            }
            catch (Exception ex) { reason = "读取弹窗位置异常：" + ex.Message; return false; }
        }

        // 截弹窗页（透明背景），裁掉四周透明区域，只留卡片
        // 不截图，直接从页面 DOM 取奖励图 + 文字自己合成（完全不碰截图接口，不会闪）
        public string LastCardLabel { get; private set; }

        public string CaptureCardFromDom(string logsDir, int index, out string reason, int attempts)
        {
            reason = "";
            LastCardLabel = "";
            var page = FindMainPage();
            if (page == null) { reason = "找不到主页面"; return null; }

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                string raw = null;
                try
                {
                    using (var session = CdpSession.Connect(page.WebSocketUrl, 3000))
                    {
                        if (session != null) raw = session.EvaluateString(RewardCard.Script, 8000, true);
                    }
                }
                catch (Exception ex) { reason = "读取弹窗异常：" + ex.Message; }

                if (raw == "none") reason = "弹窗还没出现";
                else if (raw == "noimg") reason = "弹窗里没有奖励图";
                else if (raw == "nosize") reason = "奖励图尺寸为 0";
                else if (raw == "fetchfail") reason = "奖励图下载失败";
                else if (!string.IsNullOrEmpty(raw))
                {
                    string result = RewardCard.Render(raw, logsDir, index, out reason);
                      if (!string.IsNullOrEmpty(result))
                      {
                          var labelParts = raw.Split('|');
                          if (labelParts.Length > 2) LastCardLabel = (labelParts[2] ?? "").Trim();
                          return result;
                      }
                }
                else if (string.IsNullOrEmpty(reason)) reason = "读不到弹窗数据";

                System.Threading.Thread.Sleep(400);
            }
            return null;
        }

        public string CaptureToast(string logsDir, int index)
        {
            string reason;
            return CaptureToast(logsDir, index, out reason, 5);
        }

        public string CaptureToast(string logsDir, int index, out string reason, int attempts)
        {
            reason = "";

            // 先按主页面上的弹窗坐标截（正常都是这条路）
            string cardReason;
            string card = CaptureRewardCard(logsDir, index, out cardReason, Math.Max(attempts, 12));
            if (!string.IsNullOrEmpty(card)) return card;

            // 再退回独立 toast 页的透明截图
            string toastReason;
            string toast = CaptureToastPage(logsDir, index, out toastReason, 2);
            if (!string.IsNullOrEmpty(toast)) return toast;

            reason = !string.IsNullOrEmpty(cardReason) ? cardReason : toastReason;
            if (!string.IsNullOrEmpty(cardReason) && !string.IsNullOrEmpty(toastReason))
                reason = cardReason + "；" + toastReason;
            return null;
        }

        // 弹窗有入场动画，一次截不到就多试几次；再校验裁剪结果是不是真卡片（避免整页截图）
        public string CaptureToastPage(string logsDir, int index, out string reason, int attempts)
        {
            reason = "";
            string bestPath = null;
            long bestArea = 0;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                var target = FindToastPage();
                if (target == null) { reason = "找不到弹窗页面"; System.Threading.Thread.Sleep(400); continue; }

                try
                {
                    byte[] png = null;
                    using (var session = CdpSession.Connect(target.WebSocketUrl, 3000))
                    {
                        if (session == null) { reason = "CDP 连接失败"; continue; }
                        png = session.CaptureScreenshot(true, 6000);
                    }
                    if (png == null || png.Length == 0) { reason = "CDP 截图返回空"; continue; }

                    using (var source = new MemoryStream(png))
                    using (var image = Image.FromStream(source))
                    using (var bitmap = new Bitmap(image))
                    {
                        var cropped = CropTransparent(bitmap);
                        if (cropped == null) { reason = "CDP 截图全透明（弹窗还没画出来）"; continue; }

                        // 整页都算不透明 => 截错页面了，丢掉
                        bool tooBig = cropped.Width > bitmap.Width * 0.8 && cropped.Height > bitmap.Height * 0.8;
                        if (tooBig) { cropped.Dispose(); reason = "CDP 截图不是透明卡片"; continue; }

                        long area = (long)cropped.Width * cropped.Height;
                        if (area > bestArea)
                        {
                            if (bestPath != null) { try { File.Delete(bestPath); } catch { } }
                            string dir = Path.Combine(logsDir, "rewards");
                            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + index + ".png";
                            bestPath = Path.Combine(dir, name);
                            cropped.Save(bestPath, ImageFormat.Png);
                            bestArea = area;
                            reason = "";
                        }
                        cropped.Dispose();
                    }
                }
                catch (Exception ex) { reason = "CDP 截图异常：" + ex.Message; }

                if (bestArea >= 20000) break;
                System.Threading.Thread.Sleep(400);
            }

            if (bestPath == null) return null;
            return "rewards/" + Path.GetFileName(bestPath);
        }

        private static Bitmap CropTransparent(Bitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            int minX = width, minY = height, maxX = -1, maxY = -1;

            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    if (bitmap.GetPixel(x, y).A > 8)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0 || maxY < 0) return null;

            int pad = 6;
            minX = Math.Max(0, minX - pad);
            minY = Math.Max(0, minY - pad);
            maxX = Math.Min(width - 1, maxX + pad);
            maxY = Math.Min(height - 1, maxY + pad);

            var rect = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return bitmap.Clone(rect, bitmap.PixelFormat);
        }
    }
}
