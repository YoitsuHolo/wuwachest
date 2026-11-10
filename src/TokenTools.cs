// TokenTools.cs — 通过 CDP 读取 / 续期 / 导入桌宠登录 Token
// 原理与 WuwaTappoPlus 一致：调用官方前端模块导出的 renewTokenIfNeeded / applyAuthTokens，
// 直接操作 cookie 里的 kuro_infoToken（登录凭证）与 kuro_lastLoginTime（有效期）。
using System;
using System.Globalization;
using System.Text;

namespace WuwaChest
{
    internal static class TokenTools
    {
        private const string CookieToken = "kuro_infoToken";
        private const string CookieExpiry = "kuro_lastLoginTime";

        // ------------------------------------------------------------------
        private static CdpTarget FindMainPage(int port)
        {
            try
            {
                foreach (var target in CdpDiscovery.ListTargets(port))
                {
                    if (target == null) continue;
                    if (string.IsNullOrEmpty(target.Url)) continue;
                    if (target.Url.IndexOf("tauri.localhost", StringComparison.OrdinalIgnoreCase) >= 0)
                        return target;
                }
            }
            catch { }
            return null;
        }

        private static string Eval(int port, string expression, bool awaitPromise, int timeoutMs)
        {
            var page = FindMainPage(port);
            if (page == null) return null;
            try
            {
                using (var session = CdpSession.Connect(page.WebSocketUrl, 4000))
                {
                    if (session == null) return null;
                    return session.EvaluateString(expression, timeoutMs, awaitPromise);
                }
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------
        /// <summary>读 cookie 里的 token；读不到返回 null</summary>
        public static string ReadToken(int port)
        {
            const string script =
                "(function(){var m=document.cookie.match(/(?:^|;\\s*)" + CookieToken + "=([^;]*)/);" +
                "return m?decodeURIComponent(m[1]):'';})()";
            string value = Eval(port, script, false, 4000);
            if (value == null) return null;
            value = value.Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>读有效期（本地时间）；0 表示读不到</summary>
        public static DateTime ReadExpiry(int port)
        {
            const string script =
                "(function(){var m=document.cookie.match(/(?:^|;\\s*)" + CookieExpiry + "=([^;]*)/);" +
                "if(!m)return 0;var v=Number(decodeURIComponent(m[1]));" +
                "return (isFinite(v)&&v>0)?(v<1e12?v*1000:v):0;})()";
            string raw = Eval(port, script, false, 4000);
            double ms;
            if (string.IsNullOrEmpty(raw) || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out ms) || ms <= 0)
                return DateTime.MinValue;
            try { return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime; }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// 强制续期：把有效期临时改到 1 分钟后，触发官方的 renewTokenIfNeeded，再恢复原值。
        /// 返回 ok / no-module / no-api / rejected / error / no-cdp
        /// </summary>
        public static string Renew(int port)
        {
            string script =
                "(function(){var s=document.querySelector('script[type=\"module\"][src]');" +
                "if(!s)return Promise.resolve('no-module');" +
                "var m=document.cookie.match(/(?:^|;\\s*)" + CookieExpiry + "=([^;]*)/);" +
                "var original=m?decodeURIComponent(m[1]):'';" +
                "function setC(v){document.cookie='" + CookieExpiry + "='+encodeURIComponent(v)+" +
                "'; expires='+new Date(Date.now()+7*86400000).toUTCString()+'; path=/';}" +
                "function restore(){if(original)setC(original);" +
                "else document.cookie='" + CookieExpiry + "=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';}" +
                "setC(String(Math.floor((Date.now()+60000)/1000)));" +
                "return import(s.src).then(function(mod){" +
                "var f=Object.values(mod).find(function(v){return typeof v==='function'&&v.name==='renewTokenIfNeeded';});" +
                "if(!f){restore();return 'no-api';}" +
                "return f().then(function(r){if(!r){restore();return 'rejected';}return 'ok';}," +
                "function(e){restore();return 'error';});" +
                "}).catch(function(e){restore();return 'import-failed';});})()";

            string result = Eval(port, script, true, 15000);
            return result == null ? "no-cdp" : result.Trim();
        }

        /// <summary>用别的设备提取的 token 登录（写入 cookie + 广播官方登录成功事件）</summary>
        public static string Login(int port, string token)
        {
            if (string.IsNullOrEmpty(token)) return "empty";
            string script =
                "(function(){var s=document.querySelector('script[type=\"module\"][src]');" +
                "if(!s)return Promise.resolve('no-module');" +
                "return import(s.src).then(function(mod){" +
                "var f=Object.values(mod).find(function(v){return typeof v==='function'&&v.name==='applyAuthTokens';});" +
                "if(!f)return 'no-api';" +
                "f({token:" + JsLiteral(token) + ", expire_time:String(Date.now()+7*86400000)});" +
                "try{if(window.__TAURI_INTERNALS__&&window.__TAURI_INTERNALS__.invoke){" +
                "return window.__TAURI_INTERNALS__.invoke('plugin:event|emit',{event:'cat-auth-login-success',payload:{}})" +
                ".then(function(){return 'ok';},function(){return 'ok';});}}catch(e){}" +
                "return 'ok';}).catch(function(e){return 'error';});})()";

            string result = Eval(port, script, true, 15000);
            return result == null ? "no-cdp" : result.Trim();
        }

        // ------------------------------------------------------------------
        private static string JsLiteral(string text)
        {
            var sb = new StringBuilder(text.Length + 8);
            sb.Append('\'');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('\'');
            return sb.ToString();
        }

        /// <summary>把剩余时间说成人话</summary>
        public static string DescribeExpiry(DateTime expiry)
        {
            if (expiry == DateTime.MinValue) return "读不到有效期（可能未登录或 cookie 被清）";
            var span = expiry - DateTime.Now;
            if (span.TotalSeconds <= 0) return "已过期（" + expiry.ToString("MM-dd HH:mm") + "），需要重新登录或续期";
            int days = (int)span.TotalDays;
            int hours = span.Hours;
            int minutes = span.Minutes;
            string left = (days > 0 ? days + " 天 " : "") + (hours > 0 ? hours + " 小时 " : "") + minutes + " 分钟";
            return "剩余 " + left + "（" + expiry.ToString("MM-dd HH:mm") + " 到期）";
        }
    }
}
