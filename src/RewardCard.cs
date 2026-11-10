// RewardCard.cs — 不截图，直接从页面 DOM 里取奖励图 + 文字，自己拼一张卡片
// 背景：CDP 的 Page.captureScreenshot 在透明窗口上会强制重合成，导致桌宠闪一下；
// 改成只读 DOM（Runtime.evaluate + fetch），完全不碰截图接口，就不会闪。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace WuwaChest
{
    internal static class RewardCard
    {
        // 返回：宽|高|文字|文字颜色|字号|字体|弹窗底色|圆角|base64(PNG)
        public const string Script =
            "(async function(){" +
            "var list=document.querySelectorAll('.chest-reward-popup');var root=null;" +
            // 优先要「显示着」的那个（有尺寸）
            "for(var i=list.length-1;i>=0;i--){var b=list[i].getBoundingClientRect();" +
            "if(b.width>60&&b.height>60){root=list[i];break;}}" +
            // 桌宠隐藏时整页没有布局，弹窗宽高都是 0；此时只要它带着图就照样取
            "if(!root){for(var k=list.length-1;k>=0;k--){if(list[k].querySelector('img')){root=list[k];break;}}}" +
            "if(!root)return 'none';" +
            "var img=root.querySelector('img.prize-image')||root.querySelector('img');" +
            "if(!img||!img.src)return 'noimg';" +
            "var w=img.naturalWidth||img.width||0,h=img.naturalHeight||img.height||0;" +
            "if(!w||!h)return 'nosize';" +
            "var label=root.querySelector('.chest-reward-popup__label');" +
            "var cs=label?getComputedStyle(label):null;var ps=getComputedStyle(root);" +
            "var data='';try{var r=await fetch(img.src);var ab=await (await r.blob()).arrayBuffer();" +
            "var u=new Uint8Array(ab);var s='';for(var k=0;k<u.length;k+=0x8000){s+=String.fromCharCode.apply(null,u.subarray(k,k+0x8000));}" +
            "data=btoa(s);}catch(e){return 'fetchfail';}" +
            "return [w,h,(label?label.textContent:'').replace(/\\|/g,' '),cs?cs.color:'',cs?cs.fontSize:''," +
            "cs?cs.fontFamily:'',ps.backgroundColor,ps.borderTopLeftRadius,data].join('|');" +
            "})()";

        public static string Render(string raw, string logsDir, int index, out string reason)
        {
            reason = "";
            var parts = raw.Split(new[] { '|' }, 9);
            if (parts.Length < 9) { reason = "弹窗数据格式异常"; return null; }

            int width, height;
            int.TryParse(parts[0], out width);
            int.TryParse(parts[1], out height);

            byte[] bytes;
            try { bytes = Convert.FromBase64String(parts[8]); }
            catch { reason = "奖励图解码失败"; return null; }

            // 桌宠隐藏时页面没有布局，naturalWidth 可能为 0；直接用解码后的图片定尺寸
            if (width <= 0 || height <= 0)
            {
                try
                {
                    using (var probeStream = new MemoryStream(bytes))
                    using (var probeImage = Image.FromStream(probeStream))
                    {
                        width = probeImage.Width;
                        height = probeImage.Height;
                    }
                }
                catch { reason = "奖励图尺寸异常"; return null; }
            }

            string label = (parts[2] ?? "").Trim();
            Color textColor = ParseColor(parts[3], Color.FromArgb(198, 152, 82));
            if (textColor.A < 60) textColor = Color.FromArgb(255, textColor);
            float fontSize = ParsePx(parts[4], 20f);
            string family = PickFont(parts[5]);
            Color bg = ParseColor(parts[6], Color.FromArgb(255, 248, 243, 228));
            if (bg.A < 60) bg = Color.FromArgb(255, 248, 243, 228);
            int radius = (int)Math.Max(0, Math.Min(64, ParsePx(parts[7], 26f)));

            string dir = Path.Combine(logsDir, "rewards");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + index + ".png";

            try
            {
                using (var stream = new MemoryStream(bytes))
                using (var prize = Image.FromStream(stream))
                using (var font = new Font(family, fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    int pad = 20, gap = 10;

                    // 先量文字，决定画布大小
                    SizeF textSize;
                    using (var probe = new Bitmap(1, 1))
                    using (var pg = Graphics.FromImage(probe))
                    using (var pf = new Font(family, fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        pg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                        textSize = string.IsNullOrEmpty(label) ? SizeF.Empty : pg.MeasureString(label, pf);
                    }

                    int canvasWidth = Math.Max(width + pad * 2, (int)Math.Ceiling(textSize.Width) + pad * 2);
                    int canvasHeight = height + pad * 2;
                    if (!string.IsNullOrEmpty(label)) canvasHeight += gap + (int)Math.Ceiling(textSize.Height);

                    using (var bitmap = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb))
                    using (var g = Graphics.FromImage(bitmap))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                        using (var path = RoundRect(new Rectangle(0, 0, canvasWidth, canvasHeight), radius))
                        using (var brush = new SolidBrush(bg))
                        using (var edge = new Pen(Color.FromArgb(46, 190, 160, 120), 1.4f))
                        {
                            g.FillPath(brush, path);
                            g.DrawPath(edge, path);
                        }

                        g.DrawImage(prize, new Rectangle((canvasWidth - width) / 2, pad, width, height));

                        if (!string.IsNullOrEmpty(label))
                        {
                            using (var brush = new SolidBrush(textColor))
                            {
                                g.DrawString(label, font, brush,
                                    (canvasWidth - textSize.Width) / 2f,
                                    pad + height + gap);
                            }
                        }

                        bitmap.Save(Path.Combine(dir, name), ImageFormat.Png);
                    }
                }
            }
            catch (Exception ex)
            {
                reason = "合成失败：" + ex.Message;
                return null;
            }

            return "rewards/" + name;
        }

        private static GraphicsPath RoundRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0) { path.AddRectangle(rect); path.CloseFigure(); return path; }

            int d = radius * 2;
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static float ParsePx(string text, float fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            var m = Regex.Match(text, @"([0-9]*\.?[0-9]+)");
            float value;
            if (m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }

        private static Color ParseColor(string text, Color fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            text = text.Trim();

            if (text.StartsWith("#") && (text.Length == 7 || text.Length == 9))
            {
                try
                {
                    int r = Convert.ToInt32(text.Substring(1, 2), 16);
                    int g = Convert.ToInt32(text.Substring(3, 2), 16);
                    int b = Convert.ToInt32(text.Substring(5, 2), 16);
                    int a = text.Length == 9 ? Convert.ToInt32(text.Substring(7, 2), 16) : 255;
                    return Color.FromArgb(a, r, g, b);
                }
                catch { return fallback; }
            }

            var m = Regex.Match(text, @"rgba?\(([^)]+)\)");
            if (m.Success)
            {
                var chunks = m.Groups[1].Value.Split(',');
                if (chunks.Length >= 3)
                {
                    try
                    {
                        int r = (int)Math.Round(ParsePx(chunks[0], 0));
                        int g = (int)Math.Round(ParsePx(chunks[1], 0));
                        int b = (int)Math.Round(ParsePx(chunks[2], 0));
                        int a = 255;
                        if (chunks.Length >= 4)
                        {
                            float alpha = ParsePx(chunks[3], 1f);
                            a = (int)Math.Round(alpha > 1.0f ? alpha : alpha * 255);
                        }
                        return Color.FromArgb(Math.Max(0, Math.Min(255, a)),
                            Math.Max(0, Math.Min(255, r)),
                            Math.Max(0, Math.Min(255, g)),
                            Math.Max(0, Math.Min(255, b)));
                    }
                    catch { return fallback; }
                }
            }

            return fallback;
        }

        // 页面上的字体可能是 "975MaruSC", "Microsoft YaHei", ... 取第一个可用的
        private static string PickFont(string cssFamily)
        {
            if (!string.IsNullOrEmpty(cssFamily))
            {
                foreach (var raw in cssFamily.Split(','))
                {
                    string name = raw.Trim().Trim('"', '\'');
                    if (string.IsNullOrEmpty(name)) continue;
                    try
                    {
                        using (var test = new Font(name, 12f))
                        {
                            if (string.Equals(test.Name, name, StringComparison.OrdinalIgnoreCase)) return name;
                        }
                    }
                    catch { }
                }
            }
            return "Microsoft YaHei UI";
        }
    }
}
