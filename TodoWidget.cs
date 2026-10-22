// 桌面待办卡片 — WinForms 版
// 基于 panan3562-svg/desktop-todo-widget（MIT，commit 7756ae6）扩展：
// 截止时间、重复任务、到点提醒、开机启动、按截止时间排序、外部编辑轮询修复。
// 编译：build.bat（使用 Windows 自带的 .NET Framework csc，无需装 SDK）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Web.Script.Serialization;

class TodoWidget : Form
{
    const int WIN_W = 380;
    const int HEADER_H = 60;
    const int BOTTOM_PAD = 16;
    const int PAD = 18;
    const int TEXT_X = 46;
    const int MAX_H = 1000;

    // 数据与配置都放在 exe 同目录，可整目录拷走
    static string BASE = Application.StartupPath;
    static string DATA = Path.Combine(BASE, "todo.json");
    static string CONF = Path.Combine(BASE, "widget_conf.json");
    static string LOG  = Path.Combine(BASE, "error.log");
    static string ICO  = Path.Combine(BASE, "tray.ico");

    static readonly string[] REPEAT_KEYS   = { "", "daily", "weekly", "monthly", "yearly" };
    static readonly string[] REPEAT_LABELS = { "不重复", "每天", "每周", "每月", "每年" };

    static readonly Color BG       = Color.FromArgb(251, 247, 239);
    static readonly Color FG       = Color.FromArgb(58, 52, 42);
    static readonly Color DIM      = Color.FromArgb(138, 128, 112);
    static readonly Color ACCENT   = Color.FromArgb(180, 83, 9);
    static readonly Color OKC      = Color.FromArgb(91, 140, 90);
    static readonly Color HOVER    = Color.FromArgb(243, 235, 219);
    static readonly Color DONE_FG  = Color.FromArgb(168, 160, 146);
    static readonly Color OVERDUE  = Color.FromArgb(192, 57, 43);
    static readonly Color WARN     = Color.FromArgb(183, 121, 31);
    static readonly Color RECUR    = Color.FromArgb(74, 111, 165);
    static readonly Color BORDER   = Color.FromArgb(226, 216, 198);

    Font fTitle = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold);
    Font fItem  = new Font("Microsoft YaHei UI", 10.5f);
    Font fSub   = new Font("Microsoft YaHei UI", 8.5f);
    Font fBtn   = new Font("Microsoft YaHei UI", 15f);
    Font fSmall = new Font("Microsoft YaHei UI", 8.5f);
    Font fDone  = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Strikeout);

    class Item
    {
        public string id = "";
        public string text = "";
        public string detail = "";
        public bool done = false;
        public string due = "";       // yyyy-MM-dd HH:mm，空表示无截止时间
        public string repeat = "";    // "", daily, weekly, monthly, yearly
    }

    List<Item> items = new List<Item>();
    bool topMost = false;
    int opacityPct = 96;
    int[] rowY = new int[0];
    int[] rowH = new int[0];
    int hoverIdx = -1;
    int scrollY = 0;
    bool dragging = false;
    Point dragOff;
    DateTime lastWrite = DateTime.MinValue;
    Timer poll;
    Timer reminderTimer;
    NotifyIcon tray;
    ContextMenuStrip appMenu;
    ToolStripMenuItem topItem;
    ToolStripMenuItem startupItem;
    HashSet<string> notified = new HashSet<string>();

    // ---------- Win32 ----------
    [DllImport("shcore.dll")]
    static extern int SetProcessDpiAwareness(int value);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    static readonly IntPtr HWND_TOP = new IntPtr(0);
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOACTIVATE = 0x0010;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
            return cp;
        }
    }

    // ================= 初始化 =================
    public TodoWidget()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        AutoScaleMode = AutoScaleMode.None;   // 坐标一律用物理像素，避免被再缩放一次
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = BG;
        DoubleBuffered = true;
        Text = "待办";

        var conf = LoadConf();
        int sw = Screen.PrimaryScreen.Bounds.Width;
        int x = sw - WIN_W - 32, y = 80;
        if (conf.ContainsKey("x")) x = conf["x"];
        if (conf.ContainsKey("y")) y = conf["y"];
        topMost = conf.ContainsKey("topmost") && conf["topmost"] != 0;
        TopMost = topMost;
        if (conf.ContainsKey("opacity")) opacityPct = conf["opacity"];
        if (opacityPct < 60) opacityPct = 60;
        if (opacityPct > 100) opacityPct = 100;
        Opacity = opacityPct / 100.0;
        Bounds = new Rectangle(x, y, WIN_W, 200);

        LoadItems();
        SortItems();
        LayoutRows();
        AdjustHeight();
        lastWrite = File.Exists(DATA) ? File.GetLastWriteTimeUtc(DATA) : DateTime.MinValue;

        poll = new Timer();
        poll.Interval = 2000;
        poll.Tick += delegate { PollFile(); };
        poll.Start();

        BuildTray();
        BuildReminderTimer();
        ApplyRounded();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int pref = 2; // DWMWCP_ROUND
        try { DwmSetWindowAttribute(Handle, 33, ref pref, 4); } catch { }
    }

    void ApplyRounded()
    {
        // Win10 回退：用圆角 Region
        try
        {
            var path = RoundRect(new Rectangle(0, 0, Width, Height), 16);
            Region = new Region(path);
        }
        catch { }
    }

    static GraphicsPath RoundRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ================= 日期与重复规则 =================
    static DateTime? ParseDue(string s)
    {
        DateTime d;
        if (!string.IsNullOrEmpty(s) &&
            DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out d))
            return d;
        return null;
    }

    static string FmtDue(DateTime d)
    {
        return d.ToString("yyyy-MM-dd HH:mm");
    }

    static DateTime Advance(DateTime d, string repeat)
    {
        switch (repeat)
        {
            case "daily":   return d.AddDays(1);
            case "weekly":  return d.AddDays(7);
            case "monthly": return d.AddMonths(1);
            case "yearly":  return d.AddYears(1);
            default:        return d;
        }
    }

    static string WeekdayCN(DateTime d)
    {
        string[] names = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
        return names[(int)d.DayOfWeek];
    }

    static string RepeatLabel(string repeat, DateTime d)
    {
        switch (repeat)
        {
            case "daily":   return "每天 " + d.ToString("HH:mm");
            case "weekly":  return "每周" + WeekdayCN(d).Substring(1) + " " + d.ToString("HH:mm");
            case "monthly": return "每月" + d.Day + "日 " + d.ToString("HH:mm");
            case "yearly":  return "每年" + d.ToString("MM-dd") + " " + d.ToString("HH:mm");
            default:        return "";
        }
    }

    // 周期任务永远指向下一次，避免开着几天后显示成“逾期”
    static void NormalizeRecurring(Item it)
    {
        if (string.IsNullOrEmpty(it.repeat)) return;
        DateTime? d = ParseDue(it.due);
        if (!d.HasValue)
        {
            it.due = FmtDue(DateTime.Today.AddDays(1).AddHours(18));
            it.done = false;
            return;
        }
        DateTime n = d.Value;
        int guard = 0;
        while (n <= DateTime.Now && guard++ < 10000) n = Advance(n, it.repeat);
        if (n != d.Value) it.due = FmtDue(n);
        if (it.done) it.done = false;
    }

    string DueText(Item it)
    {
        DateTime? d = ParseDue(it.due);
        if (!d.HasValue) return "";

        DateTime now = DateTime.Now;
        string when;

        bool recurring = !string.IsNullOrEmpty(it.repeat);
        if (!recurring && d.Value < now)
        {
            TimeSpan span = now - d.Value;
            if (span.TotalDays >= 1) when = "逾期 " + (int)span.TotalDays + " 天";
            else if (span.TotalHours >= 1) when = "逾期 " + (int)span.TotalHours + " 小时";
            else when = "逾期 " + Math.Max(1, (int)span.TotalMinutes) + " 分钟";
        }
        else if (d.Value.Date == now.Date)
        {
            when = "今天 " + d.Value.ToString("HH:mm");
        }
        else if (d.Value.Date == now.Date.AddDays(1))
        {
            when = "明天 " + d.Value.ToString("HH:mm");
        }
        else
        {
            int days = (int)(d.Value.Date - now.Date).TotalDays;
            if (days > 1 && days < 7) when = WeekdayCN(d.Value) + " " + d.Value.ToString("HH:mm");
            else when = d.Value.ToString("MM-dd HH:mm");
        }

        string rep = RepeatLabel(it.repeat, d.Value);
        if (rep.Length > 0)
        {
            int nd = (int)(d.Value.Date - now.Date).TotalDays;
            string next;
            if (nd == 0) next = "今天";
            else if (nd == 1) next = "明天";
            else next = d.Value.ToString("MM-dd");
            return rep + " · 下次 " + next;
        }
        return when;
    }

    string BuildSubText(Item it)
    {
        string due = DueText(it);
        if (due.Length == 0) return it.detail;
        if (it.detail.Length == 0) return due;
        return due + " · " + it.detail;
    }

    Color SubColor(Item it)
    {
        if (it.done) return DONE_FG;
        DateTime? d = ParseDue(it.due);
        if (!d.HasValue) return DIM;
        if (!string.IsNullOrEmpty(it.repeat)) return RECUR;
        if (d.Value < DateTime.Now) return OVERDUE;
        if (d.Value.Date == DateTime.Today) return WARN;
        return DIM;
    }

    // ================= 数据 =================
    static void Log(string s)
    {
        try { File.AppendAllText(LOG, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + "\r\n", Encoding.UTF8); }
        catch { }
    }

    void LoadItems()
    {
        var loaded = new List<Item>();
        try
        {
            if (!File.Exists(DATA)) { items = loaded; return; }
            string json = File.ReadAllText(DATA, Encoding.UTF8);
            var ser = new JavaScriptSerializer();
            var root = ser.Deserialize<Dictionary<string, object>>(json);
            if (root == null || !root.ContainsKey("items")) { Log("no items key"); items = loaded; return; }
            var seq = root["items"] as System.Collections.IEnumerable;
            if (seq == null) { Log("items type=" + (root["items"] == null ? "null" : root["items"].GetType().FullName)); items = loaded; return; }
            foreach (var o in seq)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var it = new Item();
                if (d.ContainsKey("id")) it.id = Convert.ToString(d["id"]);
                if (d.ContainsKey("text")) it.text = Convert.ToString(d["text"]);
                if (d.ContainsKey("detail")) it.detail = Convert.ToString(d["detail"]);
                if (d.ContainsKey("done")) it.done = Convert.ToBoolean(d["done"]);
                if (d.ContainsKey("due")) it.due = Convert.ToString(d["due"]);
                if (d.ContainsKey("repeat")) it.repeat = Convert.ToString(d["repeat"]);
                if (it.id == null) it.id = "";
                if (it.text == null) it.text = "";
                if (it.detail == null) it.detail = "";
                if (it.due == null) it.due = "";
                if (it.repeat == null) it.repeat = "";
                NormalizeRecurring(it);
                loaded.Add(it);
            }
        }
        catch (Exception ex) { Log("LoadItems: " + ex.ToString()); }
        items = loaded;
    }

    void SaveItems()
    {
        try
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var it in items)
            {
                var d = new Dictionary<string, object>();
                d["id"] = it.id;
                d["text"] = it.text;
                d["detail"] = it.detail;
                d["done"] = it.done;
                d["due"] = it.due;
                d["repeat"] = it.repeat;
                list.Add(d);
            }
            var root = new Dictionary<string, object>();
            root["updated"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            root["items"] = list.ToArray();
            var ser = new JavaScriptSerializer();
            string json = ser.Serialize(root);
            File.WriteAllText(DATA, json, new UTF8Encoding(false));
            lastWrite = File.GetLastWriteTimeUtc(DATA);
        }
        catch (Exception ex) { Log("SaveItems: " + ex.Message); }
    }

    Dictionary<string, int> LoadConf()
    {
        var d = new Dictionary<string, int>();
        try
        {
            if (!File.Exists(CONF)) return d;
            var ser = new JavaScriptSerializer();
            var root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(CONF, Encoding.UTF8));
            if (root != null)
            {
                if (root.ContainsKey("x")) d["x"] = Convert.ToInt32(root["x"]);
                if (root.ContainsKey("y")) d["y"] = Convert.ToInt32(root["y"]);
                if (root.ContainsKey("topmost")) d["topmost"] = Convert.ToInt32(root["topmost"]);
                if (root.ContainsKey("opacity")) d["opacity"] = Convert.ToInt32(root["opacity"]);
            }
        }
        catch { }
        return d;
    }

    void SaveConf()
    {
        try
        {
            var root = new Dictionary<string, object>();
            root["x"] = Left;
            root["y"] = Top;
            root["topmost"] = topMost ? 1 : 0;
            root["opacity"] = opacityPct;
            var ser = new JavaScriptSerializer();
            File.WriteAllText(CONF, ser.Serialize(root), new UTF8Encoding(false));
        }
        catch (Exception ex) { Log("SaveConf: " + ex.Message); }
    }

    void PollFile()
    {
        try
        {
            if (!File.Exists(DATA)) return;
            var t = File.GetLastWriteTimeUtc(DATA);
            if (t != lastWrite)
            {
                LoadItems();
                SortItems();
                LayoutRows();
                AdjustHeight();
                lastWrite = t;
                Invalidate();
            }
        }
        catch { }
    }

    void SortItems()
    {
        items.Sort(delegate (Item a, Item b)
        {
            if (a.done != b.done) return a.done ? 1 : -1;
            DateTime? da = ParseDue(a.due);
            DateTime? db = ParseDue(b.due);
            if (da.HasValue != db.HasValue) return da.HasValue ? -1 : 1;
            if (da.HasValue)
            {
                int c = da.Value.CompareTo(db.Value);
                if (c != 0) return c;
            }
            return string.Compare(a.text, b.text, StringComparison.CurrentCulture);
        });
    }

    Item FindById(string id)
    {
        for (int i = 0; i < items.Count; i++)
            if (items[i].id == id) return items[i];
        return null;
    }

    void ClearNotified(string id)
    {
        notified.RemoveWhere(delegate (string s) { return s.StartsWith(id + "|"); });
    }

    // ================= 布局 =================
    int ContentWidth { get { return WIN_W - TEXT_X - PAD; } }

    void LayoutRows()
    {
        int n = items.Count;
        rowY = new int[n];
        rowH = new int[n];
        int y = HEADER_H + 6;
        int w = ContentWidth;
        for (int i = 0; i < n; i++)
        {
            int th = TextRenderer.MeasureText(items[i].text, fItem,
                new Size(w, 0), TextFormatFlags.WordBreak).Height;
            string sub = BuildSubText(items[i]);
            int sh = sub.Length == 0 ? 0 :
                TextRenderer.MeasureText(sub, fSub,
                new Size(w, 0), TextFormatFlags.WordBreak).Height;
            rowY[i] = y;
            rowH[i] = th + sh + 16;
            y += rowH[i];
        }
    }

    void AdjustHeight()
    {
        int need = HEADER_H + 6 + BOTTOM_PAD;
        for (int i = 0; i < rowH.Length; i++) need += rowH[i];
        need = Math.Max(need, 150);
        int maxH = (int)(Screen.FromControl(this).WorkingArea.Height * 0.72);
        need = Math.Min(need, Math.Min(MAX_H, maxH));
        Height = need;
        ClampScroll();
        ApplyRounded();
    }

    int MaxScroll()
    {
        if (rowY.Length == 0) return 0;
        int contentBottom = rowY[rowY.Length - 1] + rowH[rowH.Length - 1];
        return Math.Max(0, contentBottom - (Height - BOTTOM_PAD));
    }

    void ClampScroll()
    {
        int max = MaxScroll();
        if (scrollY > max) scrollY = max;
        if (scrollY < 0) scrollY = 0;
    }

    // ================= 绘制 =================
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BG);

        int left = 0;
        for (int i = 0; i < items.Count; i++) if (!items[i].done) left++;

        // 日期头（没有窗口标题栏）
        string date = DateTime.Now.ToString("M月d日") + " " + WeekdayCN(DateTime.Now);
        TextRenderer.DrawText(g, date, fTitle, new Point(PAD, 12), FG, TextFormatFlags.NoPrefix);
        string count = items.Count == 0 ? "点右上角 + 添加" : (items.Count + " 项 · 剩 " + left);
        TextRenderer.DrawText(g, count, fSmall, new Point(PAD, 37), DIM, TextFormatFlags.NoPrefix);

        // 右上角添加按钮
        int bx = Width - PAD - 30, by = 15;
        using (var b = new SolidBrush(ACCENT))
            g.FillEllipse(b, bx, by, 30, 30);
        TextRenderer.DrawText(g, "+", fBtn, new Rectangle(bx, by - 2, 30, 30), Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // 列表
        int listBottom = Height - BOTTOM_PAD;
        Region oldClip = g.Clip;
        g.SetClip(new Rectangle(0, HEADER_H, Width, listBottom - HEADER_H), CombineMode.Intersect);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            int y = rowY[i] - scrollY;
            int h = rowH[i];
            if (y + h < HEADER_H || y > listBottom) continue;

            if (i == hoverIdx)
                FillRoundRect(g, new Rectangle(8, y + 2, Width - 16, h - 4), 10, HOVER);

            DrawCheck(g, PAD, y + 5, it.done, i == hoverIdx);

            var font = it.done ? fDone : fItem;
            var color = it.done ? DONE_FG : FG;
            int th = TextRenderer.MeasureText(g, it.text, font,
                new Size(ContentWidth, 0), TextFormatFlags.WordBreak).Height;
            TextRenderer.DrawText(g, it.text, font,
                new Rectangle(TEXT_X, y + 2, ContentWidth, th),
                color, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            string sub = BuildSubText(it);
            if (sub.Length > 0)
            {
                TextRenderer.DrawText(g, sub, fSub,
                    new Rectangle(TEXT_X, y + th + 3, ContentWidth, h - th - 4),
                    SubColor(it), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }
        g.SetClip(oldClip, CombineMode.Replace);
        oldClip.Dispose();

        if (items.Count == 0)
        {
            TextRenderer.DrawText(g, "今天没有待办", fItem,
                new Rectangle(0, HEADER_H + 24, Width, 24), DIM,
                TextFormatFlags.HorizontalCenter);
        }

        // 细边框，让卡片有边界但不抢眼
        using (var p = new Pen(BORDER, 1f))
        using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 18))
            g.DrawPath(p, path);
    }

    static void FillRoundRect(Graphics g, Rectangle r, int rad, Color c)
    {
        using (var path = RoundRect(r, rad))
        using (var b = new SolidBrush(c))
            g.FillPath(b, path);
    }

    static void DrawCheck(Graphics g, int x, int y, bool done, bool hover)
    {
        var r = new Rectangle(x, y, 16, 16);
        if (done)
        {
            using (var b = new SolidBrush(OKC))
                g.FillEllipse(b, r);
            using (var p = new Pen(Color.White, 1.8f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLine(p, x + 4, y + 8, x + 7, y + 11);
                g.DrawLine(p, x + 7, y + 11, x + 12, y + 5);
            }
        }
        else
        {
            using (var p = new Pen(hover ? ACCENT : Color.FromArgb(196, 185, 166), 1.4f))
                g.DrawEllipse(p, r);
        }
    }

    // ================= 鼠标 =================
    int HitItem(int y)
    {
        int cy = y + scrollY;
        for (int i = 0; i < rowY.Length; i++)
            if (cy >= rowY[i] && cy < rowY[i] + rowH[i]) return i;
        return -1;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Y < HEADER_H || e.Y > Height - BOTTOM_PAD) return;
        int old = scrollY;
        scrollY += (e.Delta > 0 ? -60 : 60);
        ClampScroll();
        if (old != scrollY) Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Y < HEADER_H)
        {
            if (e.Button == MouseButtons.Right) { ShowAppMenu(); return; }
            if (e.X >= Width - PAD - 36) { AddItem(); return; }
            dragging = true;
            dragOff = new Point(e.X, e.Y);
            return;
        }

        int idx = HitItem(e.Y);
        if (idx < 0)
        {
            if (e.Button == MouseButtons.Right) ShowAppMenu();
            return;
        }

        if (e.Button == MouseButtons.Right) ShowItemMenu(items[idx]);
        else if (e.X <= PAD + 20) ToggleDone(items[idx]);   // 点圆点 = 勾选
        else EditItem(items[idx]);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
        {
            var p = PointToScreen(e.Location);
            Location = new Point(p.X - dragOff.X, p.Y - dragOff.Y);
            return;
        }
        int idx = (e.Y >= HEADER_H && e.Y < Height - BOTTOM_PAD) ? HitItem(e.Y) : -1;
        if (idx != hoverIdx) { hoverIdx = idx; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragging)
        {
            dragging = false;
            SaveConf();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverIdx != -1) { hoverIdx = -1; Invalidate(); }
    }

    // ================= 菜单 / 操作 =================
    void ShowItemMenu(Item item)
    {
        var it = FindById(item.id);
        if (it == null) return;

        var m = new ContextMenuStrip();
        if (!string.IsNullOrEmpty(it.repeat))
            m.Items.Add("完成本次（顺延）", null, delegate { ToggleDone(it); });
        else
            m.Items.Add(it.done ? "标记为未完成" : "完成", null, delegate { ToggleDone(it); });
        m.Items.Add("编辑", null, delegate { EditItem(it); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("删除", null, delegate { DeleteItem(it); });
        m.Show(this, PointToClient(Cursor.Position));
    }

    void EditItem(Item item)
    {
        var it = FindById(item.id);
        if (it == null) return;

        string id = it.id;
        var dlg = new EditDialog("编辑待办", it.text, it.detail, it.due, it.repeat);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var target = FindById(id);
        if (target == null) return;
        target.text = dlg.ValueText;
        target.detail = dlg.ValueDetail;
        target.due = dlg.ValueDue;
        target.repeat = dlg.ValueRepeat;
        if (target.repeat.Length > 0) target.done = false;
        ClearNotified(target.id);

        SaveItems();
        SortItems();
        LayoutRows();
        AdjustHeight();
        Invalidate();
    }

    void AddItem()
    {
        var dlg = new EditDialog("添加待办", "", "", "", "");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var it = new Item();
        it.id = "t" + DateTime.Now.Ticks;
        it.text = dlg.ValueText;
        it.detail = dlg.ValueDetail;
        it.due = dlg.ValueDue;
        it.repeat = dlg.ValueRepeat;
        items.Add(it);
        scrollY = 0;

        SaveItems();
        SortItems();
        LayoutRows();
        AdjustHeight();
        Invalidate();
    }

    void ToggleDone(Item item)
    {
        var it = FindById(item.id);
        if (it == null) return;

        if (!string.IsNullOrEmpty(it.repeat))
        {
            DateTime? d = ParseDue(it.due);
            DateTime n = d.HasValue ? d.Value : DateTime.Now;
            n = Advance(n, it.repeat);
            int guard = 0;
            while (n <= DateTime.Now && guard++ < 10000) n = Advance(n, it.repeat);
            it.due = FmtDue(n);
            it.done = false;
            ClearNotified(it.id);
        }
        else
        {
            it.done = !it.done;
        }

        SaveItems();
        SortItems();
        LayoutRows();
        AdjustHeight();
        Invalidate();
    }

    void DeleteItem(Item item)
    {
        var it = FindById(item.id);
        if (it == null) return;
        if (MessageBox.Show(this, "删除「" + it.text + "」？", "确认删除",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        items.Remove(it);
        ClearNotified(it.id);
        SaveItems();
        LayoutRows();
        AdjustHeight();
        Invalidate();
    }

    void ClearCompleted()
    {
        int n = items.FindAll(delegate (Item it) { return it.done; }).Count;
        if (n == 0) return;
        if (MessageBox.Show(this, "删除 " + n + " 条已完成待办？", "确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        items.RemoveAll(delegate (Item it) { return it.done; });
        SaveItems();
        LayoutRows();
        AdjustHeight();
        Invalidate();
    }

    // ================= 提醒 =================
    void BuildReminderTimer()
    {
        reminderTimer = new Timer();
        reminderTimer.Interval = 30000;
        reminderTimer.Tick += delegate { CheckReminders(); };
        reminderTimer.Start();
    }

    void CheckReminders()
    {
        if (tray == null) return;
        DateTime now = DateTime.Now;
        foreach (var it in items)
        {
            if (it.done) continue;
            DateTime? d = ParseDue(it.due);
            if (!d.HasValue) continue;
            // 提前 10 分钟提醒；超过 12 小时的旧账不再弹窗轰炸
            if (d.Value > now.AddMinutes(10)) continue;
            if (d.Value < now.AddHours(-12)) continue;

            string key = it.id + "|" + it.due;
            if (notified.Contains(key)) continue;
            notified.Add(key);

            string msg = it.text;
            if (it.detail.Length > 0) msg += "\n" + it.detail;
            msg += "\n" + DueText(it);
            if (msg.Length > 250) msg = msg.Substring(0, 247) + "...";
            try { tray.ShowBalloonTip(8000, "待办提醒", msg, ToolTipIcon.Info); }
            catch { }
        }
        Invalidate();   // 让“今天/明天/逾期”的文案跟着时间更新
    }

    // ================= 托盘 / 桌面模式 =================
    void BuildTray()
    {
        try
        {
            tray = new NotifyIcon();
            tray.Text = "待办";
            try { tray.Icon = new Icon(ICO); }
            catch
            {
                Icon appIcon = null;
                try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { }
                tray.Icon = appIcon != null ? appIcon : SystemIcons.Application;
            }

            BuildAppMenu();
            tray.ContextMenuStrip = appMenu;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.Visible = true;
        }
        catch (Exception ex) { Log("Tray: " + ex.Message); }
    }

    void BuildAppMenu()
    {
        appMenu = new ContextMenuStrip();
        appMenu.Items.Add("添加待办", null, delegate { ShowFromTray(); AddItem(); });
        appMenu.Items.Add("清除已完成", null, delegate { ClearCompleted(); });
        appMenu.Items.Add(new ToolStripSeparator());

        topItem = new ToolStripMenuItem("置顶显示");
        topItem.Checked = topMost;
        topItem.Click += delegate
        {
            topMost = !topMost;
            topItem.Checked = topMost;
            TopMost = topMost;
            if (!topMost) SendToBottom();
            SaveConf();
        };
        appMenu.Items.Add(topItem);

        var opacityMenu = new ToolStripMenuItem("透明度");
        int[] pcts = { 80, 88, 94, 98, 100 };
        foreach (int pct in pcts)
        {
            var mi = new ToolStripMenuItem(pct + "%");
            mi.Tag = pct;
            mi.Click += delegate { SetOpacity((int)mi.Tag); };
            opacityMenu.DropDownItems.Add(mi);
        }
        appMenu.Items.Add(opacityMenu);

        startupItem = new ToolStripMenuItem("开机自动启动");
        startupItem.Checked = IsStartupEnabled();
        startupItem.Click += delegate
        {
            SetStartup(!IsStartupEnabled());
            startupItem.Checked = IsStartupEnabled();
        };
        appMenu.Items.Add(startupItem);

        appMenu.Items.Add(new ToolStripSeparator());
        appMenu.Items.Add("显示/隐藏", null, delegate
        {
            if (Visible) HideToTray(); else ShowFromTray();
        });
        appMenu.Items.Add("退出", null, delegate
        {
            tray.Visible = false;
            SaveConf();
            Application.Exit();
        });

        appMenu.Opening += delegate
        {
            topItem.Checked = topMost;
            startupItem.Checked = IsStartupEnabled();
            foreach (ToolStripItem x in opacityMenu.DropDownItems)
            {
                var mi = x as ToolStripMenuItem;
                if (mi != null) mi.Checked = ((int)mi.Tag == opacityPct);
            }
        };
    }

    void SetOpacity(int pct)
    {
        if (pct < 60) pct = 60;
        if (pct > 100) pct = 100;
        opacityPct = pct;
        Opacity = opacityPct / 100.0;
        SaveConf();
    }

    void ShowAppMenu()
    {
        if (appMenu == null) return;
        appMenu.Show(this, PointToClient(Cursor.Position));
    }

    void ApplyTopMost()
    {
        TopMost = topMost;
    }

    void SendToBottom()
    {
        try
        {
            SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyTopMost();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!topMost) SendToBottom();
    }

    void HideToTray()
    {
        Hide();
    }

    void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        if (topMost) TopMost = true;
        else
        {
            try
            {
                SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }
        Activate();
        Invalidate();
    }

    // ================= 开机启动 =================
    static string StartupLnk
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "桌面待办.lnk");
        }
    }

    static bool IsStartupEnabled()
    {
        try { return File.Exists(StartupLnk); }
        catch { return false; }
    }

    static string PsQuote(string s)
    {
        return s.Replace("'", "''");
    }

    void SetStartup(bool on)
    {
        try
        {
            if (!on)
            {
                if (File.Exists(StartupLnk)) File.Delete(StartupLnk);
                return;
            }

            string ps =
                "$w=New-Object -ComObject WScript.Shell;" +
                "$s=$w.CreateShortcut('" + PsQuote(StartupLnk) + "');" +
                "$s.TargetPath='" + PsQuote(Application.ExecutablePath) + "';" +
                "$s.WorkingDirectory='" + PsQuote(Application.StartupPath) + "';" +
                "$s.Description='桌面待办';" +
                "$s.Save()";
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + enc);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            var p = Process.Start(psi);
            if (p != null) p.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Log("Startup: " + ex.Message);
            MessageBox.Show(this, "设置开机启动失败：" + ex.Message, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            SaveConf();
            HideToTray();
            return;
        }
        SaveConf();
        base.OnFormClosing(e);
    }

    // ================= 编辑对话框 =================
    class EditDialog : Form
    {
        public string ValueText = "";
        public string ValueDetail = "";
        public string ValueDue = "";
        public string ValueRepeat = "";

        TextBox txtText;
        TextBox txtDetail;
        CheckBox chkDue;
        DateTimePicker dtDate;
        DateTimePicker dtTime;
        ComboBox cbRepeat;

        public EditDialog(string title, string text, string detail, string due, string repeat)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 310);
            BackColor = BG;
            ForeColor = FG;
            Font = new Font("Microsoft YaHei UI", 9.5f);

            var l1 = MakeLabel("内容", 16, 14);
            Controls.Add(l1);
            txtText = MakeText(false, 16, 36, 388, 26);
            Controls.Add(txtText);

            var l2 = MakeLabel("备注", 16, 72);
            Controls.Add(l2);
            txtDetail = MakeText(true, 16, 94, 388, 44);
            Controls.Add(txtDetail);

            chkDue = new CheckBox();
            chkDue.Text = "截止时间";
            chkDue.Location = new Point(16, 152);
            chkDue.Size = new Size(130, 26);
            chkDue.ForeColor = ForeColor;
            chkDue.CheckedChanged += delegate { SyncEnabled(); };
            Controls.Add(chkDue);

            dtDate = new DateTimePicker();
            dtDate.Format = DateTimePickerFormat.Custom;
            dtDate.CustomFormat = "yyyy-MM-dd";
            dtDate.Location = new Point(150, 150);
            dtDate.Size = new Size(130, 26);
            Controls.Add(dtDate);

            dtTime = new DateTimePicker();
            dtTime.Format = DateTimePickerFormat.Custom;
            dtTime.CustomFormat = "HH:mm";
            dtTime.ShowUpDown = true;
            dtTime.Location = new Point(286, 150);
            dtTime.Size = new Size(118, 26);
            Controls.Add(dtTime);

            var l3 = MakeLabel("重复", 16, 190);
            Controls.Add(l3);

            cbRepeat = new ComboBox();
            cbRepeat.DropDownStyle = ComboBoxStyle.DropDownList;
            cbRepeat.Location = new Point(150, 186);
            cbRepeat.Size = new Size(180, 26);
            cbRepeat.Items.AddRange(REPEAT_LABELS);
            cbRepeat.SelectedIndexChanged += delegate
            {
                if (cbRepeat.SelectedIndex > 0 && !chkDue.Checked) chkDue.Checked = true;
            };
            Controls.Add(cbRepeat);

            var hint = new Label();
            hint.Text = "例：每周日 18:00 组会";
            hint.ForeColor = DIM;
            hint.Font = new Font("Microsoft YaHei UI", 8.5f);
            hint.Location = new Point(150, 222);
            hint.Size = new Size(254, 22);
            Controls.Add(hint);

            var cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(230, 264);
            cancel.Size = new Size(80, 30);
            Controls.Add(cancel);

            var save = new Button();
            save.Text = "保存";
            save.Location = new Point(320, 264);
            save.Size = new Size(84, 30);
            save.Click += delegate { OnSave(); };
            Controls.Add(save);

            AcceptButton = save;
            CancelButton = cancel;

            txtText.Text = text;
            txtDetail.Text = detail;

            DateTime? d = ParseDue(due);
            if (d.HasValue)
            {
                chkDue.Checked = true;
                dtDate.Value = d.Value.Date;
                dtTime.Value = d.Value;
            }
            else
            {
                chkDue.Checked = false;
                DateTime def = DateTime.Today.AddHours(18);
                dtDate.Value = def;
                dtTime.Value = def;
            }

            int ri = Array.IndexOf(REPEAT_KEYS, repeat);
            cbRepeat.SelectedIndex = ri < 0 ? 0 : ri;
            SyncEnabled();
            ActiveControl = txtText;
            txtText.SelectAll();
        }

        static Label MakeLabel(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.Size = new Size(100, 22);
            l.ForeColor = FG;
            return l;
        }

        static TextBox MakeText(bool multiline, int x, int y, int w, int h)
        {
            var t = new TextBox();
            t.Multiline = multiline;
            t.Location = new Point(x, y);
            t.Size = new Size(w, h);
            t.BackColor = Color.White;
            t.ForeColor = FG;
            t.BorderStyle = BorderStyle.FixedSingle;
            if (!multiline) t.MaxLength = 500;
            return t;
        }

        void SyncEnabled()
        {
            dtDate.Enabled = chkDue.Checked;
            dtTime.Enabled = chkDue.Checked;
        }

        void OnSave()
        {
            string text = txtText.Text.Trim();
            if (text.Length == 0)
            {
                MessageBox.Show(this, "内容不能为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                txtText.Focus();
                return;
            }

            string rep = REPEAT_KEYS[cbRepeat.SelectedIndex];
            DateTime? due = null;
            if (chkDue.Checked) due = dtDate.Value.Date.Add(dtTime.Value.TimeOfDay);

            if (rep.Length > 0 && !due.HasValue)
            {
                MessageBox.Show(this, "周期任务需要设置截止时间，例如每周日 18:00。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                chkDue.Checked = true;
                return;
            }

            // 周期任务的起始时间已过，就自动顺延到下一次
            if (rep.Length > 0 && due.HasValue)
            {
                int guard = 0;
                while (due.Value <= DateTime.Now && guard++ < 10000) due = Advance(due.Value, rep);
            }

            ValueText = text;
            ValueDetail = txtDetail.Text.Trim();
            ValueRepeat = rep;
            ValueDue = due.HasValue ? FmtDue(due.Value) : "";
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ================= 入口 =================
    [STAThread]
    static void Main()
    {
        try { SetProcessDpiAwareness(2); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Application.Run(new TodoWidget());
        }
        catch (Exception ex)
        {
            Log("Fatal: " + ex.ToString());
        }
    }
}
