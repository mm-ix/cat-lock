using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

static class CatLock
{
    const int WH_KEYBOARD_LL = 13;
    const int WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x0100;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_MOUSEMOVE = 0x0200;
    const int WM_DISPLAYCHANGE = 0x007E;
    const int WM_DPICHANGED = 0x02E0;
    const int WM_HOTKEY = 0x0312;
    const string VER = "1.6 (2026-11-10)";
    const string MUTEX_NAME = "CatLock_Mutex_9f3a2c";
    const string MUTEX_NAME_TEST = "CatLock_Mutex_selftest_9f3a2c";
    const string TASK_NAME = "CatLock";

    const string DEF_TEXT = "已锁定 · 连点两下 / 右键+左键解锁";
    const string CONFIG_NAME = "catlock.ini";
    const string LOG_NAME = "catlock.log";
    const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RUN_NAME = "CatLock";

    // RegisterHotKey 备用通道：低层钩子被系统摘掉（或装不上）时的第二条解锁路径。
    // 正常情况钩子会吞掉这些组合键，热键不会触发；钩子一死热键立刻接管。
    const int HKID_L = 0xC701, HKID_U = 0xC702, HKID_Q = 0xC703;
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;

    // 锁定门槛
    const double FALLBACK_UNLOCK_MINUTES = 30;   // 保底自动解锁
    const double ARMED_SECONDS = 8;              // 右键后等待左键的窗口
    const double DOUBLECLICK_MS = 1200;          // 连点两下判定窗口
    const double HOOK_SILENT_SECONDS = 180;      // 钩子 180s 无事件 -> 探活重装
    const double HOOK_PERIODIC_MINUTES = 60;     // 未锁定时定期重装
    const double TOPMOST_SECONDS = 2;            // 锁定时重申置顶周期
    const double WATCHDOG_STALL_SECONDS = 90;    // UI 卡死多久后强制退出
    const double HOTKEY_DEBOUNCE_MS = 120;       // 钩子/RegisterHotKey 双通道去抖（防同一按键双触发；短到不挡连按）
    const int HOOK_EX_LIMIT = 10;                // 单次 pump 周期内钩子异常上限

    // 设计尺寸（按 96 DPI 编写，运行时按 uiScale 缩放）
    const int BTN_W = 260, BTN_H = 48, BTN_MARGIN = 12;
    const int HINT_W = 560, HINT_MARGIN = 14, HINT_BODY_H = 32, HINT_RADIUS = 14;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    // 每显示器 DPI 感知（v2）：不走 DPI 虚拟化，屏幕坐标/鼠标钩子坐标永远同一套物理像素
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    static extern uint RegisterWindowMessage(string lpString);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr tok, int cls, out int info, int len, out int ret);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [DllImport("wtsapi32.dll")]
    static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    // ================= 计时（单调时钟，不受系统时间调整/DST 影响） =================

    const long NEVER = long.MinValue;
    static readonly Stopwatch uptime = Stopwatch.StartNew();

    static long NowMs() { return uptime.ElapsedMilliseconds; }

    // 距 t 已过 >= seconds（t == NEVER 视为"从未发生" -> true）
    static bool SinceAtLeast(long t, double seconds)
    {
        if (t == NEVER) return true;
        return (uptime.ElapsedMilliseconds - t) >= (long)(seconds * 1000.0);
    }

    static double SecondsSince(long t)
    {
        if (t == NEVER) return double.MaxValue;
        return (uptime.ElapsedMilliseconds - t) / 1000.0;
    }

    static bool BeforeDeadline(long deadlineMs)
    {
        return deadlineMs != NEVER && uptime.ElapsedMilliseconds < deadlineMs;
    }

    // ================= 状态 =================

    static IntPtr kHook = IntPtr.Zero;
    static IntPtr mHook = IntPtr.Zero;
    static HookProc kProc;
    static HookProc mProc;

    static volatile bool locked = false;
    static volatile bool paused = false;
    static bool ctrlDown = false;
    static bool altDown = false;
    static bool armed = false;
    static bool settingsOpen = false;
    static long lockStartMs = NEVER;
    static long countdownEndMs = NEVER;
    static long armedAtMs = NEVER;
    static long statusAtMs = NEVER;
    static long lastLeftClickMs = NEVER;
    static bool sessionLocked = false;
    static bool sysL = false, sysR = false, sysM = false, sysX = false;
    static bool hotkeyLatch = false;
    static long hotkeyActedMs = NEVER;
    static long hookEventAtMs = 0;
    static long hooksInstalledAtMs = 0;
    static long lastHookReinstallAtMs = NEVER;
    static long idleSuppressUntilMs = NEVER;
    static long lastPumpMs = 0;
    static long lastTopmostAtMs = NEVER;
    static int hookExCount = 0;
    static uint taskbarCreatedMsg = 0;
    static System.Threading.Timer watchdog;
    static System.Threading.Mutex mtxHold;
    static readonly List<string> logQueue = new List<string>();
    static readonly object logFileLock = new object();

    static float uiScale = 1f;
    static bool perMonitorV2 = false;
    static bool displayChangeQueued = false;
    static string lastScreenSig = "";
    static long lastScreenCheckMs = 0;
    static NoActivateForm hint;
    static string hintTitle = "键鼠已锁定（防猫模式）";
    static string hintLine1 = "";
    static string hintLine2 = "";
    static System.Windows.Forms.Timer pump;
    static NotifyIcon tray;
    static Icon trayIcon;
    static ToolStripMenuItem trayPauseItem;
    static ToolStripMenuItem trayLockItem;

    // 每个屏幕一个解锁按钮（多屏时每块屏顶部居中各一个）
    class LockUi
    {
        public Screen Scr;
        public NoActivateForm Win;
        public Button Btn;
        public int ScreenIndex = 0;
    }
    static readonly List<LockUi> uis = new List<LockUi>();

    // ---- 设置项（配置文件） ----
    static bool sHotkey = true;
    static bool sButton = true;
    static bool sHint = true;
    static bool sQuitHotkey = true;
    static bool sAutoStart = false;
    static bool sIdleLock = false;
    static int sIdleMinutes = 5;
    static bool sTest = false;

    // 按 96 DPI 设计值缩放
    static int S(int designPx)
    {
        return (int)Math.Round(designPx * uiScale);
    }

    class NoActivateForm : Form
    {
        public bool ClickThrough = false;
        public NoActivateForm(bool clickThrough)
        {
            DoubleBuffered = true;
            ClickThrough = clickThrough;
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02B1) // WM_WTSSESSION_CHANGE
            {
                OnSessionChange(m.WParam.ToInt32());
            }
            else if (m.Msg == WM_HOTKEY)
            {
                OnHotkeyMessage(m.WParam.ToInt32());
            }
            else if (taskbarCreatedMsg != 0 && m.Msg == taskbarCreatedMsg)
            {
                OnTaskbarCreated(this);
            }
            else if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DPICHANGED)
            {
                // 先让 WinForms 处理（它可能按新 DPI 自动缩放窗口），消息处理完后再统一重排，
                // 否则框架的自动缩放会把我们算好的位置/尺寸再覆盖掉
                base.WndProc(ref m);
                QueueDisplayChange();
                return;
            }
            base.WndProc(ref m);
        }
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;
                cp.ExStyle |= 0x00000080;
                if (ClickThrough) cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT：提示卡不挡点击
                return cp;
            }
        }
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
        {
            Log("thread ex: " + e.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            Log("domain ex: " + (ex != null ? ex.Message : "unknown"));
        };
        try
        {
            MainInner(args);
        }
        catch (Exception ex)
        {
            Log("main ex: " + ex.Message);
            MessageBox.Show("小猫锁启动失败：" + ex.Message, "小猫锁", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        FlushLog();
    }

    static void MainInner(string[] args)
    {
        // 优先"每显示器 DPI 感知 v2"：进程不再被 DPI 虚拟化，Screen.Bounds / 窗口矩形 /
        // 鼠标钩子坐标始终是同一套物理像素（远程连接、改缩放比例后依然对齐）。
        // 拿不到就退回老的系统级 DPI 感知（此时靠 InUnlockZone 的顶部解锁区兜底）。
        bool dpiAware;
        try { perMonitorV2 = SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch { perMonitorV2 = false; }
        if (perMonitorV2) dpiAware = true;
        else dpiAware = SetProcessDPIAware();
        uiScale = dpiAware ? DetectScale() : 1f;

        bool autostartArg = false;
        bool elevatedArg = false;
        foreach (string a in args)
        {
            if (string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase)) autostartArg = true;
            if (string.Equals(a, "--elevated", StringComparison.OrdinalIgnoreCase)) elevatedArg = true;
            if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) sTest = true;
        }

        // 单实例互斥（selftest 用独立名，便于与正式实例并存调试）
        bool isNew = false;
        string mutexName = sTest ? MUTEX_NAME_TEST : MUTEX_NAME;
        int tries = elevatedArg ? 20 : 1;
        for (int i = 0; i < tries; i++)
        {
            try
            {
                System.Threading.Mutex m = new System.Threading.Mutex(true, mutexName, out isNew);
                if (isNew) { mtxHold = m; break; }
                m.Dispose();
            }
            catch (UnauthorizedAccessException) { isNew = false; }
            catch { isNew = false; }
            if (i < tries - 1) System.Threading.Thread.Sleep(300);
        }
        if (!isNew)
        {
            if (!autostartArg && !elevatedArg && !sTest)
                MessageBox.Show("小猫锁已在运行（请查看通知区域/托盘图标）。", "小猫锁");
            return;
        }

        // 配置文件：没有就首次向导，有就直接读
        if (!File.Exists(ConfigPath))
        {
            using (SettingsForm f = new SettingsForm(true, true, true, true, true, false, false, 5))
            {
                f.ShowDialog();
                sHotkey = f.HotkeyEnabled;
                sButton = f.ButtonEnabled;
                sHint = f.HintEnabled;
                sQuitHotkey = f.QuitEnabled;
                sAutoStart = f.AutoStartEnabled;
                sIdleLock = f.IdleEnabled;
                sIdleMinutes = f.IdleMinutes;
            }
            SaveConfig();
        }
        else
        {
            LoadConfig();
        }
        if (!sHotkey && !sButton)
        {
            sHotkey = true;
            Log("config: hotkey/button 全关 -> 强制启用快捷键（防呆）");
        }
        ApplyAutoStartSetting();
        bool elevated = IsElevated();
        Log("start: ver=" + VER + " test=" + sTest + " elevated=" + elevated
            + " dpiAware=" + dpiAware + " pmv2=" + perMonitorV2
            + " scale=" + uiScale.ToString("0.##")
            + " screens=" + DescribeScreens()
            + " args=[" + string.Join(" ", args) + "] hotkey=" + sHotkey + " button=" + sButton
            + " hint=" + sHint + " quit=" + sQuitHotkey + " autostart=" + sAutoStart
            + " idle=" + sIdleLock + "/" + sIdleMinutes);

        // ---- 提示卡先建（同时作为 WTS / 热键的常驻消息窗口；永不销毁）----
        hint = new NoActivateForm(true);
        hint.FormBorderStyle = FormBorderStyle.None;
        hint.ShowInTaskbar = false;
        hint.TopMost = true;
        hint.StartPosition = FormStartPosition.Manual;
        hint.Size = new Size(S(HINT_W), S(130));
        hint.Opacity = 0.92;
        hint.Paint += HintPaint;
        SetRounded(hint, S(HINT_RADIUS));
        IntPtr hintHandle = hint.Handle;

        // ---- 每个屏幕一个解锁按钮 ----
        EnsureUi();
        RepositionWindows();
        lastScreenSig = ScreenSignature();

        taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        bool wtsOk = WTSRegisterSessionNotification(hintHandle, 0);
        Log("wts register=" + wtsOk);

        tray = new NotifyIcon();
        ContextMenuStrip menu = new ContextMenuStrip();
        trayLockItem = new ToolStripMenuItem("立即锁定");
        trayLockItem.Click += delegate(object s, EventArgs e) { if (!locked) LockNow("托盘菜单"); };
        menu.Items.Add(trayLockItem);
        trayPauseItem = new ToolStripMenuItem("暂停运行");
        trayPauseItem.Click += delegate(object s, EventArgs e) { SetPaused(!paused, "托盘菜单"); };
        menu.Items.Add(trayPauseItem);
        menu.Items.Add("设置…", null, delegate(object s, EventArgs e) { ShowSettings(); });
        if (!elevated)
            menu.Items.Add("以管理员身份重启", null, delegate(object s, EventArgs e) { RestartElevated(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate(object s, EventArgs e) { ExitApp(); });
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        UpdateTray();
        if (!wtsOk)
        {
            try { tray.ShowBalloonTip(6000, "小猫锁", "锁屏联动注册失败（Win+L 联动不可用）", ToolTipIcon.Warning); } catch { }
        }
        if (!elevated && !sTest)
        {
            try { tray.ShowBalloonTip(8000, "小猫锁", "当前未以管理员运行：管理员程序窗口中的热键/拦截可能失效。建议通过开机自启（计划任务）方式启动。", ToolTipIcon.Info); } catch { }
        }

        // ---- 安装全局钩子（UI 就绪后）----
        kProc = KeyboardProc;
        mProc = MouseProc;
        IntPtr hMod = GetModuleHandle(null);
        kHook = SetWindowsHookEx(WH_KEYBOARD_LL, kProc, hMod, 0);
        int errK = Marshal.GetLastWin32Error();
        mHook = SetWindowsHookEx(WH_MOUSE_LL, mProc, hMod, 0);
        int errM = Marshal.GetLastWin32Error();
        if (kHook == IntPtr.Zero || mHook == IntPtr.Zero)
        {
            Log("HOOK INSTALL FAILED kHook=" + kHook + " errK=" + errK + " mHook=" + mHook + " errM=" + errM);
            MessageBox.Show("无法安装输入钩子（可能被安全软件拦截），未启动。", "小猫锁",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        hooksInstalledAtMs = NowMs();
        hookEventAtMs = NowMs();
        Log("hooks installed ok");

        // ---- RegisterHotKey 备用通道（钩子被摘掉时的第二条解锁路径）----
        ApplyHotkeys();

        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerMode;

        if (autostartArg || elevatedArg || sTest)
        {
            countdownEndMs = NEVER;
        }
        else
        {
            countdownEndMs = NowMs() + 3000;
            SetAllBtnText("小猫锁：3 秒后锁定…");
            ShowAll(true);
        }

        pump = new System.Windows.Forms.Timer();
        pump.Interval = 300;
        pump.Tick += TimerTick;
        pump.Start();

        watchdog = new System.Threading.Timer(WatchdogTick, null, 30000, 30000);

        Application.Run(new ApplicationContext());

        pump.Stop();
        if (watchdog != null) { watchdog.Dispose(); watchdog = null; }
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        try { WTSUnRegisterSessionNotification(hintHandle); } catch { }
        try
        {
            UnregisterHotKey(hintHandle, HKID_L);
            UnregisterHotKey(hintHandle, HKID_U);
            UnregisterHotKey(hintHandle, HKID_Q);
        }
        catch { }
        if (kHook != IntPtr.Zero) UnhookWindowsHookEx(kHook);
        if (mHook != IntPtr.Zero) UnhookWindowsHookEx(mHook);
        foreach (LockUi u in uis) { try { u.Win.Dispose(); } catch { } }
        uis.Clear();
        Log("exit: 消息循环结束");
        if (mtxHold != null) GC.KeepAlive(mtxHold);
    }

    // ================= DPI / 屏幕 =================

    // 主屏的有效 DPI -> 缩放比。显示器变化/DPI 变化后重新调用即可跟上新的缩放。
    static float DetectScale()
    {
        try
        {
            Screen ps = Screen.PrimaryScreen;
            if (ps != null)
            {
                POINT p;
                p.x = ps.Bounds.Left + ps.Bounds.Width / 2;
                p.y = ps.Bounds.Top + ps.Bounds.Height / 2;
                IntPtr mon = MonitorFromPoint(p, 1); // MONITOR_DEFAULTTOPRIMARY
                uint dx = 0, dy = 0;
                if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx >= 48 && dx <= 768)
                    return dx / 96f; // MDT_EFFECTIVE_DPI
            }
        }
        catch { }
        try
        {
            uint dpi = GetDpiForSystem();
            if (dpi >= 48 && dpi <= 768) return dpi / 96f;
        }
        catch { }
        try
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                float d = g.DpiX / 96f;
                if (d >= 0.5f && d <= 8f) return d;
            }
        }
        catch { }
        return 1f;
    }

    static void RefreshScale()
    {
        try
        {
            float s = DetectScale();
            if (s >= 0.5f && s <= 8f) uiScale = s;
        }
        catch { }
    }

    static string DescribeScreens()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            foreach (Screen s in Screen.AllScreens)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(s.DeviceName).Append(s.Primary ? "[P]" : "").Append("=")
                  .Append(s.Bounds.Width).Append("x").Append(s.Bounds.Height)
                  .Append("@").Append(s.Bounds.Left).Append(",").Append(s.Bounds.Top);
                i++;
            }
            return sb.ToString();
        }
        catch { return "?"; }
    }

    // 让按钮列表与当前显示器集合保持一致（热插拔/分辨率变化时调用）
    static void EnsureUi()
    {
        Screen[] scr = Screen.AllScreens;
        for (int i = uis.Count - 1; i >= 0; i--)
        {
            bool alive = false;
            foreach (Screen s in scr)
            {
                if (s.DeviceName == uis[i].Scr.DeviceName) { alive = true; break; }
            }
            if (!alive)
            {
                try { uis[i].Win.Dispose(); } catch { }
                uis.RemoveAt(i);
            }
        }
        int idx = 0;
        foreach (Screen s in scr)
        {
            LockUi found = null;
            foreach (LockUi u in uis)
            {
                if (u.Scr.DeviceName == s.DeviceName) { found = u; break; }
            }
            if (found == null) uis.Add(MakeUi(s));
            else { found.Scr = s; found.ScreenIndex = idx; }
            idx++;
        }
    }

    static LockUi MakeUi(Screen s)
    {
        LockUi u = new LockUi();
        u.Scr = s;

        u.Win = new NoActivateForm(false);
        u.Win.FormBorderStyle = FormBorderStyle.None;
        u.Win.ShowInTaskbar = false;
        u.Win.TopMost = true;
        u.Win.StartPosition = FormStartPosition.Manual;
        u.Win.Size = new Size(S(BTN_W), S(BTN_H));
        u.Win.BackColor = Color.FromArgb(20, 20, 20);
        u.Win.Opacity = 0.88;

        Button b = new Button();
        b.Dock = DockStyle.Fill;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = Color.FromArgb(178, 34, 34);
        b.ForeColor = Color.White;
        b.Font = new Font("Microsoft YaHei", 10f, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
        b.TabStop = false;
        b.Text = DEF_TEXT;
        b.MouseDown += BtnMouseDown;
        u.Win.Controls.Add(b);
        u.Btn = b;
        return u;
    }

    static void SetAllBtnText(string t)
    {
        foreach (LockUi u in uis)
        {
            if (u.Btn != null && !u.Btn.IsDisposed) u.Btn.Text = t;
        }
    }

    static void SetAllBtnColor(Color c)
    {
        foreach (LockUi u in uis)
        {
            if (u.Btn != null && !u.Btn.IsDisposed) u.Btn.BackColor = c;
        }
    }

    static void ShowAll(bool show)
    {
        foreach (LockUi u in uis)
        {
            try
            {
                if (u.Win == null || u.Win.IsDisposed) continue;
                if (show) u.Win.Show(); else u.Win.Hide();
            }
            catch { }
        }
    }

    // ================= 配置文件 =================

    static string ConfigPath
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), CONFIG_NAME); }
    }

    static string LogPath
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), LOG_NAME); }
    }

    static void Log(string s)
    {
        // 只入内存队列（可在钩子回调内安全调用）；由 FlushLog 批量落盘
        lock (logQueue)
        {
            logQueue.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + s);
            if (logQueue.Count > 5000) logQueue.RemoveRange(0, 2000);
        }
    }

    static void FlushLog()
    {
        // 可能在 UI 线程与看门狗线程同时调用 -> 串行化文件操作
        lock (logFileLock)
        {
            try
            {
                List<string> batch;
                lock (logQueue)
                {
                    if (logQueue.Count == 0) return;
                    batch = new List<string>(logQueue);
                    logQueue.Clear();
                }
                string p = LogPath;
                FileInfo fi = new FileInfo(p);
                if (fi.Exists && fi.Length > 1048576)
                {
                    string bak = p + ".1";
                    try { if (File.Exists(bak)) File.Delete(bak); File.Move(p, bak); }
                    catch { }
                }
                StringBuilder sb = new StringBuilder();
                foreach (string line in batch) sb.Append(line).Append("\r\n");
                File.AppendAllText(p, sb.ToString());
            }
            catch { }
        }
    }

    static void LoadConfig()
    {
        try
        {
            foreach (string line in File.ReadAllLines(ConfigPath))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim().ToLowerInvariant();
                string v = line.Substring(i + 1).Trim();
                int n;
                if (!int.TryParse(v, out n)) continue;
                switch (k)
                {
                    case "hotkey": sHotkey = n != 0; break;
                    case "button": sButton = n != 0; break;
                    case "hint": sHint = n != 0; break;
                    case "quithotkey": sQuitHotkey = n != 0; break;
                    case "autostart": sAutoStart = n != 0; break;
                    case "idlelock": sIdleLock = n != 0; break;
                    case "idleminutes": if (n >= 1 && n <= 600) sIdleMinutes = n; break;
                }
            }
        }
        catch { }
    }

    static void SaveConfig()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 小猫锁配置（0=关闭，1=开启）");
            sb.AppendLine("hotkey=" + (sHotkey ? 1 : 0));
            sb.AppendLine("button=" + (sButton ? 1 : 0));
            sb.AppendLine("hint=" + (sHint ? 1 : 0));
            sb.AppendLine("quithotkey=" + (sQuitHotkey ? 1 : 0));
            sb.AppendLine("autostart=" + (sAutoStart ? 1 : 0));
            sb.AppendLine("idlelock=" + (sIdleLock ? 1 : 0));
            sb.AppendLine("idleminutes=" + sIdleMinutes);
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
            else File.Move(tmp, ConfigPath);
            Log("settings saved: hotkey=" + sHotkey + " button=" + sButton + " hint=" + sHint + " quit=" + sQuitHotkey + " autostart=" + sAutoStart + " idle=" + sIdleLock + "/" + sIdleMinutes);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存配置失败：" + ex.Message, "小猫锁");
        }
    }

    static bool OwnsRunValue(string cur)
    {
        if (cur == null) return true;
        return cur.IndexOf(Path.GetFileName(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static void ApplyAutoStartSetting()
    {
        if (sTest)
        {
            Log("autostart: 跳过（selftest 不碰注册表/计划任务）");
            return;
        }
        try
        {
            string want = "\"" + Application.ExecutablePath + "\" --autostart";
            string cur = null;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY))
            {
                if (k != null) cur = k.GetValue(RUN_NAME) as string;
            }
            bool task = TaskExists();
            bool elevated = IsElevated();
            if (sAutoStart)
            {
                if (task)
                {
                    if (cur != null && OwnsRunValue(cur)) { SetAutoStart(false); Log("autostart: 已有计划任务，清理重复的 Run 项"); }
                }
                else if (elevated)
                {
                    if (CreateTask())
                    {
                        if (cur != null && OwnsRunValue(cur)) SetAutoStart(false);
                        Log("autostart: 计划任务已创建（ONLOGON /RL HIGHEST）");
                    }
                    else
                    {
                        if (cur != want && OwnsRunValue(cur)) SetAutoStart(true);
                        Log("autostart: 计划任务创建失败，回退注册表方式");
                    }
                }
                else
                {
                    if (cur != want && OwnsRunValue(cur)) SetAutoStart(true);
                    Log("autostart: 未提权，使用注册表方式（管理员窗口下热键可能失效）");
                }
            }
            else
            {
                if (cur != null && OwnsRunValue(cur)) SetAutoStart(false);
                if (task)
                {
                    if (DeleteTask()) Log("autostart: 计划任务已删除");
                    else Log("autostart: 计划任务删除失败（可能未提权）");
                }
            }
        }
        catch (Exception ex)
        {
            Log("autostart ex: " + ex.Message);
        }
    }

    static void SetAutoStart(bool on)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RUN_KEY))
            {
                if (on) k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\" --autostart");
                else k.DeleteValue(RUN_NAME, false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("设置开机自启失败：" + ex.Message, "小猫锁");
        }
    }

    static void ShowSettings()
    {
        if (settingsOpen) return;
        settingsOpen = true;
        RefreshScale(); // 设置窗口按当前 DPI 重新算尺寸（远程连接后缩放可能变了）
        try
        {
            using (SettingsForm f = new SettingsForm(false, sHotkey, sButton, sHint, sQuitHotkey, sAutoStart, sIdleLock, sIdleMinutes))
            {
                if (f.ShowDialog() == DialogResult.OK)
                {
                    sHotkey = f.HotkeyEnabled;
                    sButton = f.ButtonEnabled;
                    sHint = f.HintEnabled;
                    sQuitHotkey = f.QuitEnabled;
                    sAutoStart = f.AutoStartEnabled;
                    sIdleLock = f.IdleEnabled;
                    sIdleMinutes = f.IdleMinutes;
                    ApplyAutoStartSetting();
                    SaveConfig();
                    ApplyHotkeys();
                }
            }
        }
        finally
        {
            settingsOpen = false;
        }
        ApplyLiveSettings();
    }

    static void ApplyLiveSettings()
    {
        if (locked)
        {
            if (!sHint) hint.Hide();
            else { BuildHintText(); hint.Show(); }
            if (!sButton) ShowAll(false);
            else { ShowAll(true); if (!armed) SetAllBtnText(DEF_TEXT); }
        }
        UpdateTray();
    }

    // ================= 全局热键备用通道 =================

    static void ApplyHotkeys()
    {
        if (sTest)
        {
            Log("hotkeys: 跳过（selftest）");
            return;
        }
        if (hint == null || !hint.IsHandleCreated) return;
        IntPtr h = hint.Handle;
        try
        {
            UnregisterHotKey(h, HKID_L);
            UnregisterHotKey(h, HKID_U);
            UnregisterHotKey(h, HKID_Q);
        }
        catch { }
        try
        {
            if (sHotkey)
            {
                bool a = RegisterHotKey(h, HKID_L, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x4C);
                int ea = Marshal.GetLastWin32Error();
                bool b = RegisterHotKey(h, HKID_U, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x55);
                int eb = Marshal.GetLastWin32Error();
                Log("hotkeys backup: Ctrl+Alt+L=" + a + (a ? "" : "(err=" + ea + ")")
                    + " Ctrl+Alt+U=" + b + (b ? "" : "(err=" + eb + ")"));
            }
            if (sQuitHotkey)
            {
                bool c = RegisterHotKey(h, HKID_Q, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x51);
                int ec = Marshal.GetLastWin32Error();
                if (!c) Log("hotkeys backup: Ctrl+Alt+Q 注册失败(err=" + ec + ")");
            }
        }
        catch (Exception ex)
        {
            Log("hotkeys backup ex: " + ex.Message);
        }
    }

    static void OnHotkeyMessage(int id)
    {
        if (id == HKID_L) OnHotkeyAction(0x4C, "RegisterHotKey");
        else if (id == HKID_U) OnHotkeyAction(0x55, "RegisterHotKey");
        else if (id == HKID_Q) OnHotkeyAction(0x51, "RegisterHotKey");
    }

    // 钩子通道与 RegisterHotKey 通道共用的动作入口（带时间去抖，防双通道各触发一次）
    static void OnHotkeyAction(int vk, string src)
    {
        long now = NowMs();
        if (hotkeyActedMs != NEVER && (now - hotkeyActedMs) < (long)HOTKEY_DEBOUNCE_MS) return;
        hotkeyActedMs = now;
        Log("hotkey Ctrl+Alt+" + ((char)vk) + " matched, locked=" + locked + " via " + src + (sTest ? " (selftest)" : ""));
        if (vk == 0x55)
        {
            if (locked) Post(delegate { Unlock("已解锁  |  Ctrl+Alt+U"); });
        }
        else if (vk == 0x4C)
        {
            if (locked) Post(delegate { Unlock("已解锁  |  Ctrl+Alt+L 重新上锁"); });
            else Post(delegate { LockNow("快捷键"); });
        }
        else if (vk == 0x51)
        {
            Post(ExitApp);
        }
    }

    // ================= 托盘 =================

    // 托盘/窗口图标：小猫头剪影 + 钥匙孔（绿=未锁 / 红=已锁 / 灰=已暂停）
    static Icon MakeCatLock(Color c, int size)
    {
        int sz = size;
        if (sz <= 0)
        {
            sz = 16;
            try { sz = Math.Max(16, SystemInformation.SmallIconSize.Width); } catch { }
        }
        using (Bitmap bmp = new Bitmap(sz, sz))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.ScaleTransform(sz / 256f, sz / 256f);
                using (Brush b = new SolidBrush(c))
                {
                    g.FillEllipse(b, 46, 68, 164, 164);
                    g.FillPolygon(b, new PointF[] {
                        new PointF(58.5f, 106.5f), new PointF(110.9f, 69.8f), new PointF(58f, 18f)
                    });
                    g.FillPolygon(b, new PointF[] {
                        new PointF(197.5f, 106.5f), new PointF(145.1f, 69.8f), new PointF(198f, 18f)
                    });
                }
                using (Brush w = new SolidBrush(Color.White))
                {
                    g.FillEllipse(w, 103, 121, 50, 50);
                    g.FillPolygon(w, new PointF[] {
                        new PointF(116.75f, 158.5f), new PointF(139.25f, 158.5f),
                        new PointF(149.25f, 203.5f), new PointF(106.75f, 203.5f)
                    });
                }
                g.ResetTransform();
            }
            IntPtr h = bmp.GetHicon();
            Icon ic = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return ic;
        }
    }

    static Icon MakeCatLock(Color c) { return MakeCatLock(c, 0); }

    // 品牌色小图标（设置窗口 / 任务栏图标）
    static Icon brandIcon;
    static Icon BrandIcon
    {
        get
        {
            if (brandIcon == null) brandIcon = MakeCatLock(Color.FromArgb(242, 160, 61), 32);
            return brandIcon;
        }
    }

    static void UpdateTray()
    {
        if (tray == null) return;
        if (trayPauseItem != null) trayPauseItem.Text = paused ? "恢复运行" : "暂停运行";
        if (trayLockItem != null) trayLockItem.Enabled = !paused;
        Icon old = trayIcon;
        if (locked) trayIcon = MakeCatLock(Color.FromArgb(200, 40, 40));
        else if (paused) trayIcon = MakeCatLock(Color.FromArgb(130, 130, 130));
        else trayIcon = MakeCatLock(Color.FromArgb(60, 160, 70));
        tray.Icon = trayIcon;
        string t;
        if (locked)
        {
            double left = FALLBACK_UNLOCK_MINUTES - SecondsSince(lockStartMs) / 60.0;
            if (left < 0) left = 0;
            t = "小猫锁 · 已锁定（保底 " + Math.Ceiling(left) + " 分钟后自动解锁）";
        }
        else if (paused) t = "小猫锁 · 已暂停运行（自动锁与快捷键锁定已停用）";
        else t = "小猫锁 · 未锁定";
        if (t.Length > 62) t = t.Substring(0, 62);
        tray.Text = t;
        if (old != null) old.Dispose();
    }

    // 托盘「暂停运行 / 恢复运行」：暂停期间所有锁定入口（闲置自动锁、快捷键、托盘立即锁定）都不生效。
    // 仅当前运行周期有效，重启程序后恢复运行。
    static void SetPaused(bool value, string src)
    {
        if (paused == value) return;
        paused = value;
        if (paused)
        {
            if (!locked)
            {
                countdownEndMs = NEVER;
                ShowAll(false);
            }
            Log("PAUSE (" + src + "): 自动锁 / 快捷键锁定 / 托盘立即锁定 已暂停");
        }
        else
        {
            // 托盘点击本身算输入；再给 5 秒宽限，避免恢复运行瞬间因历史闲置时长立刻锁定
            idleSuppressUntilMs = NowMs() + 5000;
            Log("RESUME (" + src + "): 自动锁 / 快捷键锁定 / 托盘立即锁定 已恢复");
        }
        UpdateTray();
        ApplyUiState();
    }

    // ================= 锁定 / 解锁 =================

    static void TimerTick(object sender, EventArgs e)
    {
        try { TickInner(); }
        catch (Exception ex) { Log("timer ex: " + ex.Message); }
    }

    static void OnSessionChange(int code)
    {
        if (code == 0x7) // WTS_SESSION_LOCK
        {
            sessionLocked = true;
            Log("windows session LOCKED");
            if (locked) Unlock("Windows 已锁屏，自动解除小猫锁");
        }
        else if (code == 0x8) // WTS_SESSION_UNLOCK
        {
            sessionLocked = false;
            idleSuppressUntilMs = NowMs() + 10000;
            ctrlDown = false;
            altDown = false;
            hotkeyLatch = false;
            hotkeyActedMs = NEVER;
            Log("windows session UNLOCKED");
            // 唤醒即放行：若实例是在 Windows 已锁屏期间启动/被闲置锁上（见不到锁屏事件），
            // 用户解锁 Windows 时应顺带解除小猫锁，避免回来即遇到键盘被锁。
            if (locked) Unlock("Windows 已解锁，自动解除小猫锁");
        }
        else
        {
            Log("windows session event code=" + code);
        }
    }

    static void OnTaskbarCreated(Form src)
    {
        if (src != hint) return;
        try
        {
            if (tray != null) { tray.Visible = true; Log("taskbar created -> tray re-shown"); }
        }
        catch { }
    }

    // 显示器指纹：排序后再拼，避免 AllScreens 枚举顺序变化被误判成"显示器变了"
    // （误判会导致每 2 秒重排一次，把连点两下的计时状态清掉）
    static string ScreenSignature()
    {
        List<string> parts = new List<string>();
        try
        {
            foreach (Screen s in Screen.AllScreens)
            {
                parts.Add(s.DeviceName + ":" + s.Bounds.Left + "," + s.Bounds.Top + ","
                    + s.Bounds.Width + "x" + s.Bounds.Height + (s.Primary ? "P" : "n"));
            }
        }
        catch { }
        if (parts.Count == 0) return "";
        parts.Sort(StringComparer.Ordinal);
        StringBuilder sb = new StringBuilder();
        foreach (string p in parts) sb.Append(p).Append(';');
        return sb.ToString();
    }

    // 一次显示器/DPI 变化通常会广播给多个窗口，这里合并成一次重排
    static void QueueDisplayChange()
    {
        if (displayChangeQueued) return;
        displayChangeQueued = true;
        Post(delegate { displayChangeQueued = false; OnDisplayChanged(); });
    }

    static void OnDisplayChanged()
    {
        try
        {
            float oldScale = uiScale;
            RefreshScale();
            EnsureUi();
            RepositionWindows();
            // 关键：分辨率/缩放变化后 EnsureUi 可能刚重建了按钮窗口（新建的默认隐藏），
            // 必须按当前状态重新应用一次，否则会出现"按钮不见了 / 点不动"
            ApplyUiState();
            lastScreenSig = ScreenSignature();
            Log("display changed -> scale=" + uiScale.ToString("0.##")
                + (Math.Abs(oldScale - uiScale) > 0.001f ? " (was " + oldScale.ToString("0.##") + ")" : "")
                + " screens=" + DescribeScreens()
                + " locked=" + locked
                + " btn=" + DescribeButtons());
        }
        catch (Exception ex) { Log("display change ex: " + ex.Message); }
    }

    // 把"当前状态"重新落到 UI 上（锁定 / 倒计时 / 空闲）
    static void ApplyUiState()
    {
        if (locked)
        {
            armed = false;
            lastLeftClickMs = NEVER;
            if (sButton)
            {
                SetAllBtnColor(Color.FromArgb(178, 34, 34));
                SetAllBtnText(DEF_TEXT);
                ShowAll(true);
            }
            else ShowAll(false);
            if (sHint)
            {
                BuildHintText();
                RepositionWindows();
                hint.Show();
            }
            else hint.Hide();
        }
        else if (countdownEndMs != NEVER)
        {
            ShowAll(true);
            hint.Hide();
        }
        else
        {
            ShowAll(false);
            hint.Hide();
        }
    }

    static string DescribeButtons()
    {
        StringBuilder sb = new StringBuilder();
        foreach (LockUi u in uis)
        {
            if (u.Win == null || u.Win.IsDisposed) continue;
            Rectangle r;
            try { r = u.Win.RectangleToScreen(u.Win.ClientRectangle); }
            catch { continue; }
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(u.Scr.DeviceName).Append("=")
              .Append(r.Left).Append(",").Append(r.Top).Append(" ")
              .Append(r.Width).Append("x").Append(r.Height)
              .Append(" vis=").Append(u.Win.Visible ? 1 : 0);
        }
        return sb.ToString();
    }

    // 按钮：每块屏顶部居中；提示卡：鼠标所在那块的左上角（与该屏按钮重叠时下移）
    static void RepositionWindows()
    {
        int w = S(BTN_W), h = S(BTN_H);
        foreach (LockUi u in uis)
        {
            if (u.Win == null || u.Win.IsDisposed) continue;
            Rectangle b = u.Scr.Bounds;
            // DPI 变了的话尺寸也要跟着重设（否则按钮大小还是启动时那套）
            if (u.Win.Width != w || u.Win.Height != h) u.Win.Size = new Size(w, h);
            u.Win.Location = new Point(b.Left + (b.Width - u.Win.Width) / 2, b.Top + S(BTN_MARGIN));
        }
        if (hint == null || hint.IsDisposed) return;
        Screen hs = Screen.PrimaryScreen;
        try { hs = Screen.FromPoint(Cursor.Position); } catch { }
        if (hs == null) hs = Screen.PrimaryScreen;
        Rectangle hb = hs.Bounds;
        int hx = hb.Left + S(HINT_MARGIN);
        int hy = hb.Top + S(HINT_MARGIN);
        foreach (LockUi u in uis)
        {
            if (u.Win == null || u.Win.IsDisposed) continue;
            if (u.Scr.DeviceName != hs.DeviceName) continue;
            Rectangle br = new Rectangle(u.Win.Left, u.Win.Top, u.Win.Width, u.Win.Height);
            Rectangle hr = new Rectangle(hx, hy, hint.Width, hint.Height);
            if (hr.IntersectsWith(br)) hy = br.Bottom + S(8);
        }
        hint.Location = new Point(hx, hy);
    }

    static void Post(Action a)
    {
        try
        {
            if (hint != null && hint.IsHandleCreated) hint.BeginInvoke(a);
            else a();
        }
        catch { }
    }

    static void WatchdogTick(object state)
    {
        try
        {
            if (locked && SinceAtLeast(lastPumpMs, WATCHDOG_STALL_SECONDS))
            {
                Log("WATCHDOG: UI 线程疑似卡死且处于锁定，强制退出（保底解锁）");
                FlushLog();
                Environment.Exit(2);
            }
        }
        catch { }
    }

    static uint GetSystemIdleMs()
    {
        LASTINPUTINFO lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref lii)) return 0;
        return unchecked((uint)Environment.TickCount) - lii.dwTime;
    }

    // 先装新钩子、成功后再摘旧的：任何情况下都不会出现"手上一个钩子都没有"的窗口
    static void ReinstallHooks(string why)
    {
        try
        {
            IntPtr hMod = GetModuleHandle(null);
            IntPtr nk = SetWindowsHookEx(WH_KEYBOARD_LL, kProc, hMod, 0);
            int errK = Marshal.GetLastWin32Error();
            IntPtr nm = SetWindowsHookEx(WH_MOUSE_LL, mProc, hMod, 0);
            int errM = Marshal.GetLastWin32Error();
            lastHookReinstallAtMs = NowMs();
            if (nk != IntPtr.Zero && nm != IntPtr.Zero)
            {
                if (kHook != IntPtr.Zero) UnhookWindowsHookEx(kHook);
                if (mHook != IntPtr.Zero) UnhookWindowsHookEx(mHook);
                kHook = nk;
                mHook = nm;
                hooksInstalledAtMs = NowMs();
                hookEventAtMs = NowMs();
                Log("hooks reinstalled (" + why + ")");
                return;
            }
            if (nk != IntPtr.Zero) UnhookWindowsHookEx(nk);
            if (nm != IntPtr.Zero) UnhookWindowsHookEx(nm);
            Log("HOOK REINSTALL FAILED (" + why + ") k=" + (nk != IntPtr.Zero) + " errK=" + errK
                + " m=" + (nm != IntPtr.Zero) + " errM=" + errM + " (保留旧钩子)");
            if (locked)
            {
                // 锁定中且钩子已经不工作：既拦不住输入、也用不了钩子通道的解锁快捷键。
                // 按 fail-open 立即放行，避免把用户关在锁定状态里。
                Log("HOOK 失效且处于锁定 -> 强制解锁（fail-open）");
                Post(delegate { Unlock("钩子失效，已自动解锁（保底）"); });
            }
            lastHookReinstallAtMs = NowMs() - 540000; // 60 秒后重试
        }
        catch (Exception ex) { Log("hook reinstall ex: " + ex.Message); }
    }

    static bool IsElevated()
    {
        try
        {
            IntPtr tok;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out tok)) return false;
            int elev = 0; int ret = 0;
            bool ok = GetTokenInformation(tok, 20, out elev, 4, out ret);
            CloseHandle(tok);
            return ok && elev != 0;
        }
        catch { return false; }
    }

    static bool RunHidden(string exe, string args, int timeoutMs)
    {
        string ignored;
        return RunCapture(exe, args, timeoutMs, out ignored);
    }

    static bool RunCapture(string exe, string args, int timeoutMs, out string output)
    {
        output = "";
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                if (p == null) return false;
                StringBuilder sb = new StringBuilder();
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) sb.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return false; }
                output = sb.ToString();
                return p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    static bool TaskExists()
    {
        string outp;
        bool ok = RunCapture("schtasks.exe", "/Query /TN " + TASK_NAME + " /FO LIST", 8000, out outp);
        if (ok) return true;
        // 权限受限时退出码可能非 0，但输出里仍会打印任务名（\CatLock）
        if (outp != null && outp.IndexOf("\\" + TASK_NAME, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    static bool CreateTask()
    {
        string tr = "\"\\\"" + Application.ExecutablePath + "\\\" --autostart\"";
        return RunHidden("schtasks.exe", "/Create /TN " + TASK_NAME + " /TR " + tr + " /SC ONLOGON /RL HIGHEST /F", 15000);
    }

    static bool DeleteTask()
    {
        return RunHidden("schtasks.exe", "/Delete /TN " + TASK_NAME + " /F", 8000);
    }

    static void RestartElevated()
    {
        try
        {
            Log("restart elevated requested");
            ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--elevated");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Process.Start(psi);
            ExitApp();
        }
        catch (Exception ex)
        {
            Log("restart elevated failed: " + ex.Message);
            MessageBox.Show("以管理员身份重启失败：" + ex.Message, "小猫锁");
        }
    }

    static void OnPowerMode(object s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            idleSuppressUntilMs = NowMs() + 30000;
            Log("power resume (闲置锁抑制 30 秒)");
        }
    }

    static void TickInner()
    {
        lastPumpMs = NowMs();
        FlushLog();
        hookExCount = 0; // 每个 pump 周期重置：只统计"本周期内"的钩子异常

        // 显示器/缩放自检：远程连接（RDP/远程控制）时 WM_DISPLAYCHANGE 可能收不到，
        // 或收到时系统还没刷新屏幕信息。这里每 2 秒比对一次实际显示器指纹，对不上就重排。
        // （2026-10-19 事故：远程后按钮还停在旧分辨率的位置上，导致双击解锁点不到）
        if (SinceAtLeast(lastScreenCheckMs, 2))
        {
            lastScreenCheckMs = NowMs();
            string sig = ScreenSignature();
            if (sig.Length > 0 && sig != lastScreenSig) OnDisplayChanged();
        }

        // 修饰键状态周期重同步（防 UIPI/安全桌面漏事件导致状态卡死）
        // 注意：仅在未锁定时执行——锁定期间修饰键按下会被本钩子吞掉，
        // 系统键态表不再更新（恒为"抬起"），重同步会把正确的跟踪态清掉（2026-10-05 事故）
        if (!locked)
        {
            ctrlDown = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            altDown = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        }

        // 钩子看护：系统有输入但我们长时间收不到任何事件 -> 可能被静默摘钩，重装
        if (SinceAtLeast(lastHookReinstallAtMs, 600))
        {
            if (GetSystemIdleMs() < 60000 && SinceAtLeast(hookEventAtMs, HOOK_SILENT_SECONDS))
            {
                ReinstallHooks("180s 无事件但系统活跃");
            }
            else if (!locked && SinceAtLeast(hooksInstalledAtMs, HOOK_PERIODIC_MINUTES * 60))
            {
                ReinstallHooks("60min 定期");
            }
        }

        // 锁定时周期性重申置顶（防按钮被其他窗口盖住）
        if (locked && SinceAtLeast(lastTopmostAtMs, TOPMOST_SECONDS))
        {
            lastTopmostAtMs = NowMs();
            foreach (LockUi u in uis)
            {
                try
                {
                    if (u.Win != null && !u.Win.IsDisposed && u.Win.Visible)
                        SetWindowPos(u.Win.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                catch { }
            }
        }

        if (!locked && countdownEndMs != NEVER)
        {
            if (settingsOpen)
            {
                countdownEndMs = NowMs() + 1000;
                return;
            }
            if (NowMs() >= countdownEndMs)
            {
                if (sessionLocked)
                {
                    countdownEndMs = NEVER;
                    ShowAll(false);
                    Log("countdown skipped (windows locked)");
                }
                else
                {
                    LockNow("启动倒计时");
                }
            }
            else
            {
                int left = (int)Math.Ceiling((countdownEndMs - NowMs()) / 1000.0);
                SetAllBtnText("小猫锁：" + left + " 秒后锁定…");
            }
            return;
        }
        if (!locked && sIdleLock && !paused && !sTest && !settingsOpen && !sessionLocked && countdownEndMs == NEVER)
        {
            if (!BeforeDeadline(idleSuppressUntilMs))
            {
                uint idleMs = GetSystemIdleMs();
                if (idleMs >= (uint)sIdleMinutes * 60000u)
                {
                    LockNow("闲置超时(" + sIdleMinutes + "分钟)");
                    return;
                }
            }
        }
        if (locked)
        {
            if (statusAtMs != NEVER && SecondsSince(statusAtMs) > 1.5)
            {
                statusAtMs = NEVER;
                if (!armed && sButton) SetAllBtnText(DEF_TEXT);
            }
            if (armed && SecondsSince(armedAtMs) > ARMED_SECONDS)
            {
                armed = false;
                if (sButton) SetAllBtnText(DEF_TEXT);
            }
            if (SecondsSince(lockStartMs) / 60.0 >= FALLBACK_UNLOCK_MINUTES)
            {
                Unlock("已自动解锁（30 分钟保底）");
            }
        }
    }

    static void LockNow(string reason)
    {
        if (sTest)
        {
            Log("selftest: suppress lock (" + reason + ")");
            return;
        }
        if (paused)
        {
            if (!locked)
            {
                countdownEndMs = NEVER;
                ShowAll(false);
            }
            Log("lock skipped (paused): " + reason);
            return;
        }
        locked = true;
        armed = false;
        lastLeftClickMs = NEVER;
        lockStartMs = NowMs();
        countdownEndMs = NEVER;
        lastTopmostAtMs = NEVER;
        Log("LOCK (" + reason + ")");
        if (sButton)
        {
            SetAllBtnColor(Color.FromArgb(178, 34, 34));
            SetAllBtnText(DEF_TEXT);
            ShowAll(true);
        }
        else
        {
            ShowAll(false);
        }
        if (sHint)
        {
            BuildHintText();
            RepositionWindows();
            hint.Show();
        }
        else
        {
            hint.Hide();
        }
        UpdateTray();
        // 记下按钮的真实屏幕矩形：远程连接/缩放异常后对着日志就能看清"程序以为按钮在哪"
        Log("lock ui: scale=" + uiScale.ToString("0.##") + " screens=" + DescribeScreens()
            + " btn=" + DescribeButtons());
    }

    static void BuildHintText()
    {
        hintLine1 = "";
        hintLine2 = "";
        if (sButton) hintLine1 = "解锁方法：连点上方按钮两下（或右键后左键单击）";
        if (sHotkey) hintLine2 = "快捷键：Ctrl+Alt+L（或 Ctrl+Alt+U）直接解锁";
        if (!sButton && !sHotkey) hintLine1 = "30 分钟后自动解除锁定";
        int body = (hintLine1 != "" ? 1 : 0) + (hintLine2 != "" ? 1 : 0);
        if (body == 0) body = 1;
        int h = S(52 + body * HINT_BODY_H + 14);
        hint.Size = new Size(S(HINT_W), h);
        SetRounded(hint, S(HINT_RADIUS));
        hint.Invalidate();
    }

    static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        GraphicsPath gp = new GraphicsPath();
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseAllFigures();
        return gp;
    }

    static void SetRounded(Form f, int radius)
    {
        Rectangle r = new Rectangle(0, 0, f.Width, f.Height);
        using (GraphicsPath gp = RoundedPath(r, radius))
        {
            Region old = f.Region;
            f.Region = new Region(gp);
            if (old != null) old.Dispose();
        }
    }

    static void HintPaint(object sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle rect = new Rectangle(0, 0, hint.Width - 1, hint.Height - 1);
        using (GraphicsPath gp = RoundedPath(rect, S(HINT_RADIUS)))
        {
            using (LinearGradientBrush bg = new LinearGradientBrush(rect,
                Color.FromArgb(255, 46, 46, 54), Color.FromArgb(255, 20, 20, 25), 90f))
            {
                g.FillPath(bg, gp);
            }
            using (Pen pen = new Pen(Color.FromArgb(85, 255, 255, 255), 1f))
            {
                g.DrawPath(pen, gp);
            }
        }
        using (Brush accent = new SolidBrush(Color.FromArgb(255, 235, 90, 90)))
        {
            g.FillRectangle(accent, 0, S(12), S(6), hint.Height - S(24));
        }
        DrawLock(g, S(26), S(16));
        using (Font fTitle = new Font("Microsoft YaHei", 14f, FontStyle.Bold))
        using (Font fBody = new Font("Microsoft YaHei", 11f))
        using (Brush bTitle = new SolidBrush(Color.White))
        using (Brush bBody = new SolidBrush(Color.FromArgb(255, 226, 229, 236)))
        using (Brush bBlue = new SolidBrush(Color.FromArgb(255, 140, 190, 255)))
        {
            g.DrawString(hintTitle, fTitle, bTitle, S(58), S(13));
            float y = S(56);
            if (hintLine1 != "")
            {
                g.DrawString(hintLine1, fBody, bBody, S(58), y);
                y += S(HINT_BODY_H);
            }
            if (hintLine2 != "")
            {
                g.DrawString(hintLine2, fBody, bBlue, S(58), y);
            }
        }
    }

    static void DrawLock(Graphics g, int x, int y)
    {
        Color c = Color.FromArgb(255, 235, 90, 90);
        using (Pen p = new Pen(c, 3f * uiScale))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            g.DrawArc(p, x + S(2), y, S(13), S(14), 180, 180);
        }
        using (Brush b = new SolidBrush(c))
        using (GraphicsPath gp = RoundedPath(new Rectangle(x, y + S(9), S(17), S(14)), S(3)))
        {
            g.FillPath(b, gp);
        }
    }

    static void Unlock(string msg)
    {
        locked = false;
        armed = false;
        lastLeftClickMs = NEVER;
        lockStartMs = NEVER;
        idleSuppressUntilMs = NowMs() + 5000;
        hookExCount = 0;
        Log("UNLOCK: " + msg);
        hint.Hide();
        SetAllBtnColor(Color.FromArgb(30, 110, 60));
        SetAllBtnText(msg);
        ShowAll(true); // 即使关掉了屏幕按钮，也短暂显示一下解锁结果
        System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer();
        hide.Interval = 2500;
        hide.Tick += delegate(object s, EventArgs a)
        {
            hide.Stop();
            hide.Dispose();
            if (!locked) ShowAll(false);
        };
        hide.Start();
        UpdateTray();
    }

    static void ExitApp()
    {
        Log("exit: 用户退出");
        FlushLog();
        if (tray != null) tray.Visible = false;
        foreach (LockUi u in uis) { try { if (u.Win != null && !u.Win.IsDisposed) u.Win.Hide(); } catch { } }
        hint.Hide();
        Application.ExitThread();
    }

    static void BtnMouseDown(object sender, MouseEventArgs e)
    {
        Log("btn click " + e.Button + " locked=" + locked + " armed=" + armed);
        if (!locked && countdownEndMs != NEVER && e.Button == MouseButtons.Left)
        {
            // 倒计时期间点按钮 = 取消本次自动锁定
            countdownEndMs = NEVER;
            Log("countdown cancelled (btn)");
            ShowAll(false);
            return;
        }
        HandleUnlockButton(e.Button, "");
    }

    // 屏幕顶部"解锁区"：远程连接/缩放比例不一致时鼠标坐标可能整体偏移，点不到那个小按钮。
    // 锁定期间，点任意屏幕顶部这一条都按"点了解锁按钮"处理（反正此时点击本来就被吞掉）。
    static bool InUnlockZone(int x, int y, out string why)
    {
        why = "";
        int zoneH = Math.Max(S(BTN_H) + S(80), 150);
        try
        {
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle b = s.Bounds;
                if (y < b.Top || y > b.Top + zoneH) continue;
                why = s.DeviceName + " top+" + (y - b.Top) + " (zoneH=" + zoneH + ")";
                return true;
            }
        }
        catch { }
        return false;
    }

    // 解锁按钮的点击判定（物理按钮点击 与 顶部解锁区点击 共用同一套状态机）
    static void HandleUnlockButton(MouseButtons button, string src)
    {
        if (!locked || !sButton) return;
        if (button == MouseButtons.Right)
        {
            armed = true;
            armedAtMs = NowMs();
            SetAllBtnText("已就绪 → 左键单击（或连点两下）解锁");
            Log("unlock button armed" + src);
            return;
        }
        if (button != MouseButtons.Left) return;
        if (armed && !SinceAtLeast(armedAtMs, ARMED_SECONDS))
        {
            Unlock("已解锁（右键+左键）");
            return;
        }
        if (lastLeftClickMs != NEVER
            && (NowMs() - lastLeftClickMs) <= (long)DOUBLECLICK_MS)
        {
            Unlock("已解锁（连点两下）");
            return;
        }
        lastLeftClickMs = NowMs();
        statusAtMs = NowMs();
        SetAllBtnText("再点一下解锁（或右键后左键）");
    }

    // ================= 钩子 =================

    // 钩子里的未处理异常：锁定期间保守吞掉（fail-closed，宁可多拦不可漏拦）
    static IntPtr HookFail(string which, Exception ex)
    {
        Log(which + " hook ex: " + ex.Message);
        hookExCount++;
        if (hookExCount >= HOOK_EX_LIMIT && locked)
        {
            hookExCount = 0;
            Log("钩子连续异常 -> 兜底解锁");
            Post(delegate { Unlock("钩子异常，已自动解锁（保底）"); });
        }
        if (locked) return (IntPtr)1;
        return IntPtr.Zero; // 调用方转为 CallNextHookEx
    }

    static IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try { return KeyboardInner(nCode, wParam, lParam); }
        catch (Exception ex)
        {
            IntPtr r = HookFail("kb", ex);
            if (r != IntPtr.Zero) return r;
        }
        return CallNextHookEx(kHook, nCode, wParam, lParam);
    }

    static IntPtr KeyboardInner(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            hookEventAtMs = NowMs();
            KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            bool down = (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN);
            uint vk = k.vkCode;

            if (vk == 0x11 || vk == 0xA2 || vk == 0xA3) ctrlDown = down;
            else if (vk == 0x12 || vk == 0xA4 || vk == 0xA5) altDown = down;
            if (!down && (vk == 0x4C || vk == 0x55 || vk == 0x51)) hotkeyLatch = false;

            // 组合键判定：未锁定时以物理键态为准（防安全桌面漏事件导致的幽灵热键）；
            // 锁定时叠加事件跟踪态——锁定吞掉修饰键按下后物理键态恒为"抬起"，
            // 只靠物理键态会导致解锁热键失灵（2026-10-05 实测事故）
            bool ctrlPhys = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool altPhys = (GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool ctrlOK = ctrlPhys || (locked && ctrlDown);
            bool altOK = altPhys || (locked && altDown);

            if (sHotkey && ctrlOK && altOK && down && (vk == 0x4C || vk == 0x55))
            {
                if (!hotkeyLatch)
                {
                    hotkeyLatch = true;
                    OnHotkeyAction((int)vk, "hook");
                }
                return (IntPtr)1;
            }

            if (sQuitHotkey && ctrlOK && altOK && down && vk == 0x51)
            {
                if (!hotkeyLatch)
                {
                    hotkeyLatch = true;
                    OnHotkeyAction(0x51, "hook");
                }
                return (IntPtr)1;
            }

            if (locked)
            {
                if (down) return (IntPtr)1;
                return CallNextHookEx(kHook, nCode, wParam, lParam);
            }
        }
        return CallNextHookEx(kHook, nCode, wParam, lParam);
    }

    static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try { return MouseInner(nCode, wParam, lParam); }
        catch (Exception ex)
        {
            IntPtr r = HookFail("mouse", ex);
            if (r != IntPtr.Zero) return r;
        }
        return CallNextHookEx(mHook, nCode, wParam, lParam);
    }

    // 系统当前"已知按下"的鼠标键状态（用于锁定时安全放行抬起事件）
    static bool SysDown(int msg)
    {
        if (msg == 0x0202) return sysL;
        if (msg == 0x0205) return sysR;
        if (msg == 0x0208) return sysM;
        if (msg == 0x020C) return sysX;
        return false;
    }

    static void SysSet(int msg)
    {
        if (msg == 0x0201 || msg == 0x0203) sysL = true;
        else if (msg == 0x0202) sysL = false;
        else if (msg == 0x0204 || msg == 0x0206) sysR = true;
        else if (msg == 0x0205) sysR = false;
        else if (msg == 0x0207 || msg == 0x0209) sysM = true;
        else if (msg == 0x0208) sysM = false;
        else if (msg == 0x020B || msg == 0x020D) sysX = true;
        else if (msg == 0x020C) sysX = false;
    }

    static bool ButtonHit(IntPtr lParam)
    {
        MSLLHOOKSTRUCT m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
        POINT p;
        p.x = m.x;
        p.y = m.y;
        IntPtr h = WindowFromPoint(p);
        foreach (LockUi u in uis)
        {
            if (u.Win == null || u.Win.IsDisposed || u.Btn == null || u.Btn.IsDisposed) continue;
            if (!u.Win.Visible) continue;
            if (h != u.Win.Handle && h != u.Btn.Handle) continue;
            Rectangle r = u.Win.RectangleToScreen(u.Win.ClientRectangle);
            if (m.x < r.Left || m.x >= r.Right || m.y < r.Top || m.y >= r.Bottom) continue;
            return true;
        }
        return false;
    }

    static IntPtr MouseInner(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            hookEventAtMs = NowMs();
            int msg = (int)wParam;
            bool isDown = (msg == 0x0201 || msg == 0x0204 || msg == 0x0207 || msg == 0x020B
                        || msg == 0x0203 || msg == 0x0206 || msg == 0x0209 || msg == 0x020D);
            bool isUp = (msg == 0x0202 || msg == 0x0205 || msg == 0x0208 || msg == 0x020C);

            if (!locked)
            {
                if (isDown || isUp) SysSet(msg);
                return CallNextHookEx(mHook, nCode, wParam, lParam);
            }

            if (msg == WM_MOUSEMOVE)
                return CallNextHookEx(mHook, nCode, wParam, lParam);

            if (msg == 0x020A || msg == 0x020E) // 滚轮/横滚：锁定期间一律吞
                return (IntPtr)1;

            bool pass;
            bool zone = false;
            if (isUp)
            {
                if (SysDown(msg))
                {
                    SysSet(msg);
                    pass = true;
                }
                else
                {
                    pass = false;
                }
            }
            else
            {
                pass = (sButton && ButtonHit(lParam));
                if (pass && isDown) SysSet(msg);
                else if (isDown && sButton)
                {
                    // 没落在按钮窗口上：再看是否落在屏幕顶部"解锁区"里。
                    // 远程桌面/缩放比例变化会让鼠标坐标与窗口矩形对不上，这条兜底保证还能双击解锁。
                    bool isLeftMsg = (msg == 0x0201 || msg == 0x0203);
                    bool isRightMsg = (msg == 0x0204 || msg == 0x0206);
                    if (isLeftMsg || isRightMsg)
                    {
                        MSLLHOOKSTRUCT z = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        string why;
                        if (InUnlockZone(z.x, z.y, out why))
                        {
                            zone = true;
                            MouseButtons mb = isRightMsg ? MouseButtons.Right : MouseButtons.Left;
                            Log("unlock zone hit: " + why + " btn=" + mb + " at " + z.x + "," + z.y);
                            Post(delegate { HandleUnlockButton(mb, " (zone)"); });
                        }
                    }
                }
            }
            if (msg != 0x020A && msg != 0x020E)
            {
                MSLLHOOKSTRUCT mm = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                Log("click msg=" + msg + " at " + mm.x + "," + mm.y + " -> " + (pass ? "PASS" : "SWALLOW")
                    + (zone ? " [zone]" : "")
                    + " btn=" + DescribeButtons());
            }
            if (!pass) return (IntPtr)1;
        }
        return CallNextHookEx(mHook, nCode, wParam, lParam);
    }

    // ================= 设置窗口 =================

    class SettingsForm : Form
    {
        public bool HotkeyEnabled, ButtonEnabled, HintEnabled, QuitEnabled, AutoStartEnabled, IdleEnabled;
        public int IdleMinutes;

        CheckBox cbHotkey, cbButton, cbHint, cbQuit, cbAuto, cbIdle;
        NumericUpDown numIdle;
        Label desc;

        public SettingsForm(bool firstRun, bool hk, bool bt, bool hn, bool qt, bool au, bool il, int ilmin)
        {
            Text = firstRun ? "小猫锁 · 首次使用设置" : "小猫锁 · 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9.5f);
            TopMost = true;
            Icon = BrandIcon;

            int rowH = S(30);
            int padX = S(14);
            int btnW = S(96), btnH = S(32);

            desc = new Label();
            desc.Text = "首次使用：请选择需要的功能（之后可在托盘菜单→设置修改）";
            desc.ForeColor = Color.FromArgb(90, 90, 90);
            desc.Location = new Point(padX, S(10));
            desc.AutoSize = true;
            desc.Visible = firstRun;
            Controls.Add(desc);

            int y = firstRun ? S(42) : S(14);
            cbHotkey = MakeCheck("启用快捷键 Ctrl+Alt+L（解锁 / 上锁）", y, hk); y += rowH;
            cbButton = MakeCheck("启用屏幕按钮解锁（右键 + 左键单击）", y, bt); y += rowH;
            cbHint = MakeCheck("上锁后显示左上角解锁说明", y, hn); y += rowH;
            cbQuit = MakeCheck("启用快捷键 Ctrl+Alt+Q（退出程序）", y, qt); y += rowH;
            cbAuto = MakeCheck("开机自动启动（登录后静默运行）", y, au); y += S(38);

            cbIdle = new CheckBox();
            cbIdle.Text = "无操作自动上锁：";
            cbIdle.Location = new Point(padX, y);
            cbIdle.AutoSize = true;
            cbIdle.Checked = il;
            Controls.Add(cbIdle);

            numIdle = new NumericUpDown();
            numIdle.Minimum = 1;
            numIdle.Maximum = 600;
            numIdle.Value = ilmin;
            numIdle.Location = new Point(S(178), y - S(2));
            numIdle.Size = new Size(S(70), S(26));
            numIdle.Enabled = il;
            Controls.Add(numIdle);

            Label minLbl = new Label();
            minLbl.Text = "分钟（键盘鼠标都不动这么久后自动锁定）";
            minLbl.ForeColor = Color.FromArgb(90, 90, 90);
            minLbl.Location = new Point(S(256), y);
            minLbl.Size = new Size(S(140), S(44));
            Controls.Add(minLbl);

            cbIdle.CheckedChanged += delegate { numIdle.Enabled = cbIdle.Checked; };

            cbHotkey.CheckedChanged += delegate
            {
                if (!cbHotkey.Checked && !cbButton.Checked)
                {
                    cbHotkey.Checked = true;
                    MessageBox.Show("至少保留一种解锁方式：快捷键或屏幕按钮。", "小猫锁");
                }
            };
            cbButton.CheckedChanged += delegate
            {
                if (!cbButton.Checked && !cbHotkey.Checked)
                {
                    cbButton.Checked = true;
                    MessageBox.Show("至少保留一种解锁方式：快捷键或屏幕按钮。", "小猫锁");
                }
            };

            y += S(52);

            // ---- 状态行 ----
            Label stat = new Label();
            stat.Text = "当前状态：" + (locked ? "已锁定" : "未锁定")
                + "    权限：" + (IsElevated() ? "管理员" : "普通")
                + "    版本 " + VER.Split(' ')[0];
            stat.ForeColor = Color.FromArgb(90, 90, 90);
            stat.Location = new Point(padX, y);
            stat.AutoSize = true;
            Controls.Add(stat);

            y += S(30);

            Button logBtn = new Button();
            logBtn.Text = "查看日志";
            logBtn.Location = new Point(padX, y);
            logBtn.Size = new Size(btnW, btnH);
            logBtn.Click += delegate
            {
                try { Process.Start(LogPath); }
                catch (Exception ex) { MessageBox.Show("无法打开日志：" + ex.Message, "小猫锁"); }
            };
            Controls.Add(logBtn);

            Button ok = new Button();
            ok.Text = firstRun ? "开始使用" : "确定";
            ok.Location = new Point(S(400) - S(14) - btnW * 2 - S(10), y);
            ok.Size = new Size(btnW, btnH);
            ok.Click += delegate
            {
                CaptureSettings();
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(ok);

            if (!firstRun)
            {
                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.Location = new Point(S(400) - S(14) - btnW, y);
                cancel.Size = new Size(btnW, btnH);
                cancel.Click += delegate { Close(); };
                Controls.Add(cancel);
            }

            ClientSize = new Size(S(400), y + btnH + S(16));

            Controls.Add(cbHotkey);
            Controls.Add(cbButton);
            Controls.Add(cbHint);
            Controls.Add(cbQuit);
            Controls.Add(cbAuto);
        }

        void CaptureSettings()
        {
            HotkeyEnabled = cbHotkey.Checked;
            ButtonEnabled = cbButton.Checked;
            HintEnabled = cbHint.Checked;
            QuitEnabled = cbQuit.Checked;
            AutoStartEnabled = cbAuto.Checked;
            IdleEnabled = cbIdle.Checked;
            IdleMinutes = (int)numIdle.Value;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CaptureSettings();
            base.OnFormClosing(e);
        }

        CheckBox MakeCheck(string text, int y, bool val)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Location = new Point(S(14), y);
            c.AutoSize = true;
            c.Checked = val;
            return c;
        }
    }
}
