// PetClient.cs — 通过 UI 自动化读取桌宠状态并触发领取
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace WuwaChest
{
    internal sealed class ElementInfo
    {
        public string ControlType;
        public string Name;
        public string ClassName;
        public double X;
        public double Y;
        public double W;
        public double H;
        public bool Offscreen;

        public override string ToString()
        {
            return string.Format("{0,-8} name=[{1}] class=[{2}] rect={3},{4} {5}x{6}{7}",
                ControlType, Name, ClassName, (int)X, (int)Y, (int)W, (int)H, Offscreen ? " offscreen" : "");
        }
    }

    internal sealed class PetSnapshot
    {
        public bool Ok;
        public string Reason = "";
        public bool HasNumber;
        public int Number;
        public bool Chest;
        public bool ChestByClass;
        public double ChestX;
        public double ChestY;
        public double ChestW;
        public double ChestH;
        public string Info = "";
        public string Source = "uia";   // uia / cdp
        // CDP 额外信息：两个宝箱槽的到期时间（epoch ms）、服务端余额、累计点击
        public bool HasChestTimers;
        public int NextChestSeconds;   // 界面上 reward-countdown 显示的最短剩余秒数
        public long LeftDueMs;
        public long RightDueMs;
        public int Balance;
        public int CumulativeClicks;
        public bool LoggedIn;
        public List<ElementInfo> Elements = new List<ElementInfo>();

        // 只在执行领取的线程里使用
        public AutomationElement ChestElement;
    }

    internal sealed class ToastInfo
    {
        public bool Found;
        public string Text = "";
        public string Debug = "";
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public List<ElementInfo> Elements = new List<ElementInfo>();
    }

    internal sealed class PetClient
    {
        private static readonly Regex NumberPattern = new Regex(@"^\s*\d[\d,\s]*$", RegexOptions.Compiled);
        private readonly AppConfig _cfg;

        public PetClient(AppConfig cfg)
        {
            _cfg = cfg;
        }

        private AutomationElement FindWindow()
        {
            var cls = new PropertyCondition(AutomationElement.ClassNameProperty, "Tauri Window");
            var name = new PropertyCondition(AutomationElement.NameProperty, "Wuwa Tappo");
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(cls, name));
        }

        public PetSnapshot Read()
        {
            var snap = new PetSnapshot();
            snap.Balance = -1;   // UIA 读不到余额，用 -1 表示未知
            try
            {
                AutomationElement win = FindWindow();
                if (win == null)
                {
                    snap.Reason = "未找到桌宠窗口（标题 'Wuwa Tappo'，类名 'Tauri Window'），桌宠可能没有启动";
                    return snap;
                }

                AutomationElement doc;
                try
                {
                    doc = win.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                }
                catch (Exception ex)
                {
                    snap.Reason = "读取桌宠界面失败：" + ex.Message;
                    return snap;
                }

                if (doc == null)
                {
                    snap.Reason = "桌宠窗口已找到，但没有读到 Web 内容（桌宠可能被隐藏了，或界面还没加载完）";
                    return snap;
                }

                var all = doc.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                var texts = new List<AutomationElement>();
                var buttons = new List<AutomationElement>();

                foreach (AutomationElement element in all)
                {
                    string type;
                    string name;
                    string cls;
                    System.Windows.Rect rect;
                    bool offscreen;
                    try
                    {
                        type = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
                        name = element.Current.Name;
                        cls = element.Current.ClassName;
                        rect = element.Current.BoundingRectangle;
                        offscreen = element.Current.IsOffscreen;
                    }
                    catch { continue; }

                    if (rect.Width > 0 && rect.Height > 0)
                    {
                        snap.Elements.Add(new ElementInfo
                        {
                            ControlType = type,
                            Name = name,
                            ClassName = cls,
                            X = rect.X,
                            Y = rect.Y,
                            W = rect.Width,
                            H = rect.Height,
                            Offscreen = offscreen
                        });
                    }

                    if (element.Current.ControlType == ControlType.Text && NumberPattern.IsMatch(name ?? ""))
                        texts.Add(element);
                    else if (element.Current.ControlType == ControlType.Button)
                        buttons.Add(element);
                }

                if (texts.Count == 0)
                {
                    snap.Reason = "没有找到桌宠下方的字符数（桌宠可能被隐藏了，或界面还没加载完）";
                    return snap;
                }

                // ---- 宝箱按钮：优先按类名，退化为几何方位 ----
                AutomationElement chest = null;
                bool byClass = false;

                {
                    foreach (var button in buttons)
                    {
                        try
                        {
                            if (!string.Equals(button.Current.ClassName, "reward-btn", StringComparison.OrdinalIgnoreCase))
                                continue;
                            var r = button.Current.BoundingRectangle;
                            if (r.Width <= 0 || r.Height <= 0) continue;
                            if (button.Current.IsOffscreen) continue;
                            chest = button;
                            byClass = true;
                            break;
                        }
                        catch { }
                    }
                }

                // 先确定一个「数字」；有宝箱时取离宝箱最近的那个（数字卡片就在宝箱正下方）
                AutomationElement numberElement = chest != null
                    ? NearestText(texts, chest.Current.BoundingRectangle)
                    : texts[0];

                if (chest == null)
                {
                    var numberRect = numberElement.Current.BoundingRectangle;

                    // 几何：位于数字卡片上方、且在数字右侧的按钮里，取最靠下的一个
                    foreach (var button in buttons)
                    {
                        try
                        {
                            var r = button.Current.BoundingRectangle;
                            if (r.Width <= 0 || r.Height <= 0) continue;
                            if (button.Current.IsOffscreen) continue;
                            if (r.Bottom > numberRect.Top + 4) continue;
                            if (r.Left <= numberRect.Left) continue;
                        }
                        catch { continue; }

                        try
                        {
                            if (chest == null || button.Current.BoundingRectangle.Bottom > chest.Current.BoundingRectangle.Bottom)
                                chest = button;
                        }
                        catch { }
                    }

                    // 几何命中后，再用宝箱位置校正数字
                    if (chest != null)
                        numberElement = NearestText(texts, chest.Current.BoundingRectangle);
                }

                snap.HasNumber = true;
                snap.Number = ParseNumber(numberElement.Current.Name);
                snap.Chest = chest != null;
                snap.ChestByClass = byClass;

                if (chest != null)
                {
                    var r = chest.Current.BoundingRectangle;
                    snap.ChestX = r.X;
                    snap.ChestY = r.Y;
                    snap.ChestW = r.Width;
                    snap.ChestH = r.Height;
                    snap.ChestElement = chest;
                    snap.Info = string.Format("宝箱位置 {0},{1} {2}x{3}{4}",
                        (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height,
                        byClass ? "，reward-btn" : "");
                }

                snap.Ok = true;
            }
            catch (Exception ex)
            {
                snap.Reason = "读取界面异常：" + ex.Message;
            }
            return snap;
        }

        // 领取后「新表情！/新配件！」弹窗：找新出现的元素，返回包围盒和文字
        public ToastInfo FindToast(PetSnapshot before)
        {
            var toast = new ToastInfo();

            // 1) Toast 窗口（弹窗通常在独立的一层）
            try
            {
                var win = FindWindowByTitle("Toast");
                if (win != null) CollectNewElements(win, before, toast);
            }
            catch { }

            // 2) 主窗口里新冒出来的元素（有的弹窗画在主页面）
            if (!toast.Found)
            {
                try
                {
                    var win = FindWindowByTitle("Wuwa Tappo");
                    if (win != null) CollectNewElements(win, before, toast);
                }
                catch { }
            }

            return toast;
        }

        private AutomationElement FindWindowByTitle(string title)
        {
            var cls = new PropertyCondition(AutomationElement.ClassNameProperty, "Tauri Window");
            var name = new PropertyCondition(AutomationElement.NameProperty, title);
            return AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(cls, name));
        }

        private static void CollectNewElements(AutomationElement window, PetSnapshot before, ToastInfo toast)
        {
            AutomationElement doc;
            try
            {
                doc = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            }
            catch { return; }
            if (doc == null) return;

            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            AutomationElementCollection all;
            try { all = doc.FindAll(TreeScope.Descendants, Condition.TrueCondition); }
            catch { return; }

            var fresh = new List<ElementInfo>();

            foreach (AutomationElement element in all)
            {
                ElementInfo info;
                try
                {
                    var rect = element.Current.BoundingRectangle;
                    if (rect.Width < 24 || rect.Height < 24) continue;
                    if (rect.Width >= bounds.Width * 0.9 && rect.Height >= bounds.Height * 0.9) continue;

                    info = new ElementInfo
                    {
                        ControlType = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                        Name = element.Current.Name ?? "",
                        ClassName = element.Current.ClassName ?? "",
                        X = rect.X,
                        Y = rect.Y,
                        W = rect.Width,
                        H = rect.Height,
                        Offscreen = element.Current.IsOffscreen
                    };
                }
                catch { continue; }

                // 排除桌宠自己的界面元素：数字、宝箱按钮、设置按钮、整体容器
                if (info.ClassName == "reward-btn" || info.ClassName == "settings-btn" || info.ClassName == "reward-countdown" ||
                    info.ClassName == "pet-dock-content") continue;
                if (NumberPattern.IsMatch(info.Name)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(info.Name, @"^\d{1,3}:\d{2}$")) continue;

                // 只认真正的奖励弹窗：联机时头像栏/聊天也会"新出现"，不能当弹窗
                bool isReward = (info.ClassName ?? "").IndexOf("chest-reward", StringComparison.OrdinalIgnoreCase) >= 0
                                || (info.Name ?? "").StartsWith("新")
                                || (info.Name ?? "").StartsWith("重复");
                if (!isReward) continue;

                if (before != null && IsKnown(before, info)) continue;
                fresh.Add(info);
            }

            if (fresh.Count == 0) return;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            var names = new List<string>();

            foreach (var info in fresh)
            {
                if (info.X < minX) minX = info.X;
                if (info.Y < minY) minY = info.Y;
                if (info.X + info.W > maxX) maxX = info.X + info.W;
                if (info.Y + info.H > maxY) maxY = info.Y + info.H;
                var label = info.Name;
                if (!string.IsNullOrEmpty(label) && label.StartsWith("新") && !names.Contains(label)) names.Add(label);
            }

            toast.Elements.AddRange(fresh);
            toast.X = (int)minX - 8;
            toast.Y = (int)minY - 8;
            toast.Width = (int)(maxX - minX) + 16;
            toast.Height = (int)(maxY - minY) + 16;
            // 只保留最新一条奖励名：两次领取间隔很短时，上一张弹窗可能还在，别把它的名字带进来
            toast.Text = names.Count > 0 ? names[names.Count - 1] : "";
            toast.Found = toast.Width > 40 && toast.Height > 40;

            var debug = new System.Text.StringBuilder();
            foreach (var info in fresh)
            {
                debug.AppendLine(string.Format("{0} name=[{1}] class=[{2}] {3},{4} {5}x{6}",
                    info.ControlType, info.Name, info.ClassName,
                    (int)info.X, (int)info.Y, (int)info.W, (int)info.H));
            }
            toast.Debug = debug.ToString();
        }

        private static bool IsKnown(PetSnapshot before, ElementInfo candidate)
        {
            foreach (var known in before.Elements)
            {
                if (known.ControlType != candidate.ControlType) continue;
                if ((known.Name ?? "") != (candidate.Name ?? "")) continue;
                if ((known.ClassName ?? "") != (candidate.ClassName ?? "")) continue;
                if (Math.Abs(known.X - candidate.X) > 12) continue;
                if (Math.Abs(known.Y - candidate.Y) > 12) continue;
                if (Math.Abs(known.W - candidate.W) > 12) continue;
                if (Math.Abs(known.H - candidate.H) > 12) continue;
                return true;
            }
            return false;
        }

        private static AutomationElement NearestText(List<AutomationElement> texts, System.Windows.Rect target)
        {
            AutomationElement best = null;
            double bestDistance = double.MaxValue;
            foreach (var text in texts)
            {
                try
                {
                    var r = text.Current.BoundingRectangle;
                    double dx = (r.X + r.Width / 2) - (target.X + target.Width / 2);
                    double dy = (r.Y + r.Height / 2) - (target.Y + target.Height / 2);
                    double distance = dx * dx + dy * dy;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = text;
                    }
                }
                catch { }
            }
            return best ?? texts[0];
        }

        private static int ParseNumber(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0;
            var clean = new string(Array.FindAll(raw.ToCharArray(), char.IsDigit));  // 去掉逗号/空格
            int value;
            int.TryParse(clean, out value);
            return value;
        }

        // 返回 true 表示已触发；detail 说明用的哪条路径
        public bool Invoke(PetSnapshot snap, out string detail)
        {
            detail = "";
            if (snap == null || snap.ChestElement == null)
            {
                detail = "没有可点击的宝箱按钮";
                return false;
            }

            try
            {
                object pattern = snap.ChestElement.GetCurrentPattern(InvokePattern.Pattern);
                if (pattern != null)
                {
                    ((InvokePattern)pattern).Invoke();
                    detail = "UI 自动化 Invoke";
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = "Invoke 失败：" + ex.Message;
            }

            if (true)
            {
                try
                {
                    int x = (int)(snap.ChestX + snap.ChestW / 2);
                    int y = (int)(snap.ChestY + snap.ChestH / 2);
                    Native.ClickAt(x, y, true);
                    detail = string.Format("鼠标点击 ({0}, {1})", x, y);
                    return true;
                }
                catch (Exception ex)
                {
                    detail = "鼠标点击失败：" + ex.Message;
                }
            }
            return false;
        }
    }
}
