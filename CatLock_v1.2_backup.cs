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
    const string VER = "1.2 (2026-10-05)";
    const string MUTEX_NAME = "CatLock_Mutex_9f3a2c";
    const string MUTEX_NAME_TEST = "CatLock_Mutex_selftest_9f3a2c";
    const string TASK_NAME = "CatLock";

    const string DEF_TEXT = "已锁定 · 连点两下 / 右键+左键解锁";
    const string CONFIG_NAME = "catlock.ini";
    const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RUN_NAME = "CatLock";

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

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

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

    static IntPtr kHook = IntPtr.Zero;
    static IntPtr mHook = IntPtr.Zero;
    static HookProc kProc;
    static HookProc mProc;

    static volatile bool locked = false;
    static bool ctrlDown = false;
    static bool altDown = false;
    static bool armed = false;
    static bool settingsOpen = false;
    static DateTime lockStart = DateTime.MinValue;
    static DateTime countdownEnd = DateTime.MinValue;
    static DateTime armedAt = DateTime.MinValue;
    static DateTime statusAt = DateTime.MinValue;
    static bool sessionLocked = false;
    static bool sysL = false, sysR = false, sysM = false, sysX = false;
    static bool hotkeyLatch = false;
    static DateTime hookEventAt = DateTime.Now;
    static DateTime hooksInstalledAt = DateTime.MinValue;
    static DateTime lastHookReinstallAt = DateTime.MinValue;
    static DateTime idleSuppressUntil = DateTime.MinValue;
    static long lastPumpTicks = DateTime.Now.Ticks;
    static DateTime lastTopmostAt = DateTime.MinValue;
    static uint taskbarCreatedMsg = 0;
    static System.Threading.Timer watchdog;
    static System.Threading.Mutex mtxHold;
    static readonly List<string> logQueue = new List<string>();

    static NoActivateForm win;
    static NoActivateForm hint;
    static Button btn;
    static string hintTitle = "键鼠已锁定（防猫模式）";
    static string hintLine1 = "";
    static string hintLine2 = "";
    static System.Windows.Forms.Timer pump;
    static NotifyIcon tray;
    static Icon trayIcon;

    // ---- 设置项（配置文件） ----
    static bool sHotkey = true;
    static bool sButton = true;
    static bool sHint = true;
    static bool sQuitHotkey = true;
    static bool sAutoStart = false;
    static bool sIdleLock = false;
    static int sIdleMinutes = 5;
    static bool sTest = false;
    static DateTime lastLeftClick = DateTime.MinValue;

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
            else if (taskbarCreatedMsg != 0 && m.Msg == taskbarCreatedMsg)
            {
                OnTaskbarCreated(this);
            }
            else if (m.Msg == WM_DISPLAYCHANGE)
            {
                OnDisplayChanged();
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
        SetProcessDPIAware();

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
        Log("start: ver=" + VER + " test=" + sTest + " elevated=" + elevated + " args=[" + string.Join(" ", args) + "] hotkey=" + sHotkey + " button=" + sButton + " hint=" + sHint + " quit=" + sQuitHotkey + " autostart=" + sAutoStart + " idle=" + sIdleLock + "/" + sIdleMinutes);

        // ---- UI 先就绪，再装钩子（避免回调访问未初始化状态）----
        win = new NoActivateForm(false);
        win.FormBorderStyle = FormBorderStyle.None;
        win.ShowInTaskbar = false;
        win.TopMost = true;
        win.StartPosition = FormStartPosition.Manual;
        win.Size = new Size(260, 48);
        win.BackColor = Color.FromArgb(20, 20, 20);
        win.Opacity = 0.88;

        btn = new Button();
        btn.Dock = DockStyle.Fill;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderSize = 0;
        btn.BackColor = Color.FromArgb(178, 34, 34);
        btn.ForeColor = Color.White;
        btn.Font = new Font("Microsoft YaHei", 10f, FontStyle.Bold);
        btn.Cursor = Cursors.Hand;
        btn.TabStop = false;
        btn.MouseDown += BtnMouseDown;
        win.Controls.Add(btn);

        hint = new NoActivateForm(true);
        hint.FormBorderStyle = FormBorderStyle.None;
        hint.ShowInTaskbar = false;
        hint.TopMost = true;
        hint.StartPosition = FormStartPosition.Manual;
        hint.Size = new Size(560, 130);
        hint.Opacity = 0.92;
        hint.Paint += HintPaint;
        SetRounded(hint, 14);

        RepositionWindows();

        taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        IntPtr winHandle = win.Handle;
        bool wtsOk = WTSRegisterSessionNotification(winHandle, 0);
        Log("wts register=" + wtsOk);

        tray = new NotifyIcon();
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("立即锁定", null, delegate(object s, EventArgs e) { if (!locked) LockNow("托盘菜单"); });
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
        hooksInstalledAt = DateTime.Now;
        hookEventAt = DateTime.Now;
        Log("hooks installed ok");

        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerMode;

        if (autostartArg || elevatedArg || sTest)
        {
            countdownEnd = DateTime.MinValue;
        }
        else
        {
            countdownEnd = DateTime.Now.AddSeconds(3);
            btn.Text = "小猫锁：3 秒后锁定…";
            win.Show();
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
        try { WTSUnRegisterSessionNotification(winHandle); } catch { }
        if (kHook != IntPtr.Zero) UnhookWindowsHookEx(kHook);
        if (mHook != IntPtr.Zero) UnhookWindowsHookEx(mHook);
        Log("exit: 消息循环结束");
        if (mtxHold != null) GC.KeepAlive(mtxHold);
    }

    // ================= 配置文件 =================

    static string ConfigPath
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), CONFIG_NAME); }
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
        try
        {
            List<string> batch;
            lock (logQueue)
            {
                if (logQueue.Count == 0) return;
                batch = new List<string>(logQueue);
                logQueue.Clear();
            }
            string p = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "catlock.log");
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
            if (!sButton) win.Hide();
            else { win.Show(); if (!armed) btn.Text = DEF_TEXT; }
        }
        UpdateTray();
    }

    // ================= 托盘 =================

    static Icon MakeDot(Color c)
    {
        using (Bitmap bmp = new Bitmap(16, 16))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush b = new SolidBrush(c)) g.FillEllipse(b, 2, 2, 12, 12);
                using (Pen p = new Pen(Color.White, 2f)) g.DrawEllipse(p, 2, 2, 12, 12);
            }
            IntPtr h = bmp.GetHicon();
            Icon ic = (Icon)Icon.FromHandle(h).Clone();
            DestroyIcon(h);
            return ic;
        }
    }

    static void UpdateTray()
    {
        if (tray == null) return;
        Icon old = trayIcon;
        trayIcon = MakeDot(locked ? Color.FromArgb(200, 40, 40) : Color.FromArgb(60, 160, 70));
        tray.Icon = trayIcon;
        tray.Text = locked ? "小猫锁 · 已锁定" : "小猫锁 · 未锁定";
        if (old != null) old.Dispose();
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
            idleSuppressUntil = DateTime.Now.AddSeconds(10);
            ctrlDown = false;
            altDown = false;
            hotkeyLatch = false;
            Log("windows session UNLOCKED");
        }
        else
        {
            Log("windows session event code=" + code);
        }
    }

    static void OnTaskbarCreated(Form src)
    {
        if (src != win) return;
        try
        {
            if (tray != null) { tray.Visible = true; Log("taskbar created -> tray re-shown"); }
        }
        catch { }
    }

    static void OnDisplayChanged()
    {
        try
        {
            RepositionWindows();
            Log("display changed -> windows repositioned");
        }
        catch { }
    }

    static void RepositionWindows()
    {
        Rectangle scr = Screen.PrimaryScreen.Bounds;
        if (win != null) win.Location = new Point(scr.Left + (scr.Width - win.Width) / 2, scr.Top + 12);
        if (hint != null) hint.Location = new Point(scr.Left + 14, scr.Top + 14);
    }

    static void Post(Action a)
    {
        try
        {
            if (win != null && win.IsHandleCreated) win.BeginInvoke(a);
            else a();
        }
        catch { }
    }

    static void WatchdogTick(object state)
    {
        try
        {
            if (locked && (DateTime.Now.Ticks - lastPumpTicks) > 90L * 10000000L)
            {
                try
                {
                    string p = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "catlock.log");
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  WATCHDOG: UI 线程疑似卡死且处于锁定，强制退出（保底解锁）\r\n");
                }
                catch { }
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

    static void ReinstallHooks(string why)
    {
        try
        {
            if (kHook != IntPtr.Zero) UnhookWindowsHookEx(kHook);
            if (mHook != IntPtr.Zero) UnhookWindowsHookEx(mHook);
            IntPtr hMod = GetModuleHandle(null);
            kHook = SetWindowsHookEx(WH_KEYBOARD_LL, kProc, hMod, 0);
            mHook = SetWindowsHookEx(WH_MOUSE_LL, mProc, hMod, 0);
            hooksInstalledAt = DateTime.Now;
            hookEventAt = DateTime.Now;
            lastHookReinstallAt = DateTime.Now;
            Log("hooks reinstalled (" + why + ") k=" + (kHook != IntPtr.Zero) + " m=" + (mHook != IntPtr.Zero));
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
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process p = Process.Start(psi))
            {
                if (p == null) return false;
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return false; }
                return p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    static bool TaskExists()
    {
        return RunHidden("schtasks.exe", "/Query /TN " + TASK_NAME, 8000);
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
            idleSuppressUntil = DateTime.Now.AddSeconds(30);
            Log("power resume (闲置锁抑制 30 秒)");
        }
    }

    static void TickInner()
    {
        DateTime now = DateTime.Now;
        lastPumpTicks = now.Ticks;
        FlushLog();

        // 修饰键状态周期重同步（防 UIPI/安全桌面漏事件导致状态卡死）
        // 注意：仅在未锁定时执行——锁定期间修饰键按下会被本钩子吞掉，
        // 系统键态表不再更新（恒为"抬起"），重同步会把正确的跟踪态清掉（2026-10-05 事故）
        if (!locked)
        {
            ctrlDown = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            altDown = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        }

        // 钩子看护：系统有输入但我们长时间收不到任何事件 -> 可能被静默摘钩，重装
        if ((now - lastHookReinstallAt).TotalMinutes >= 10)
        {
            if (GetSystemIdleMs() < 60000 && (now - hookEventAt).TotalSeconds > 180)
            {
                ReinstallHooks("180s 无事件但系统活跃");
            }
            else if (!locked && (now - hooksInstalledAt).TotalMinutes >= 60)
            {
                ReinstallHooks("60min 定期");
            }
        }

        // 锁定时周期性重申置顶（防按钮被其他窗口盖住）
        if (locked && (now - lastTopmostAt).TotalSeconds >= 2)
        {
            lastTopmostAt = now;
            if (win != null && win.Visible)
                SetWindowPos(win.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        if (!locked && countdownEnd != DateTime.MinValue)
        {
            if (settingsOpen)
            {
                countdownEnd = DateTime.Now.AddSeconds(1);
                return;
            }
            if (now >= countdownEnd)
            {
                if (sessionLocked)
                {
                    countdownEnd = DateTime.MinValue;
                    win.Hide();
                    Log("countdown skipped (windows locked)");
                }
                else
                {
                    LockNow("启动倒计时");
                }
            }
            else
            {
                int left = (int)Math.Ceiling((countdownEnd - now).TotalSeconds);
                btn.Text = "小猫锁：" + left + " 秒后锁定…";
            }
            return;
        }
        if (!locked && sIdleLock && !sTest && !settingsOpen && !sessionLocked && countdownEnd == DateTime.MinValue)
        {
            if (idleSuppressUntil == DateTime.MinValue || now >= idleSuppressUntil)
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
            if (statusAt != DateTime.MinValue && (now - statusAt).TotalMilliseconds > 1500)
            {
                statusAt = DateTime.MinValue;
                if (!armed && sButton) btn.Text = DEF_TEXT;
            }
            if (armed && (now - armedAt).TotalSeconds > 8)
            {
                armed = false;
                if (sButton) btn.Text = DEF_TEXT;
            }
            if ((now - lockStart).TotalMinutes >= 30)
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
        locked = true;
        armed = false;
        lastLeftClick = DateTime.MinValue;
        lockStart = DateTime.Now;
        countdownEnd = DateTime.MinValue;
        Log("LOCK (" + reason + ")");
        Log("win loc=" + win.Location + " size=" + win.Size + " primary=" + Screen.PrimaryScreen.Bounds.Width + "x" + Screen.PrimaryScreen.Bounds.Height);
        if (sButton)
        {
            btn.BackColor = Color.FromArgb(178, 34, 34);
            btn.Text = DEF_TEXT;
            win.Show();
        }
        else
        {
            win.Hide();
        }
        if (sHint)
        {
            BuildHintText();
            hint.Show();
        }
        else
        {
            hint.Hide();
        }
        UpdateTray();
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
        int h = 52 + body * 32 + 14;
        hint.Size = new Size(560, h);
        SetRounded(hint, 14);
        hint.Invalidate();
    }

    static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        int d = radius * 2;
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
        using (GraphicsPath gp = RoundedPath(rect, 14))
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
            g.FillRectangle(accent, 0, 12, 6, hint.Height - 24);
        }
        DrawLock(g, 26, 16);
        using (Font fTitle = new Font("Microsoft YaHei", 14f, FontStyle.Bold))
        using (Font fBody = new Font("Microsoft YaHei", 11f))
        using (Brush bTitle = new SolidBrush(Color.White))
        using (Brush bBody = new SolidBrush(Color.FromArgb(255, 226, 229, 236)))
        using (Brush bBlue = new SolidBrush(Color.FromArgb(255, 140, 190, 255)))
        {
            g.DrawString(hintTitle, fTitle, bTitle, 58, 13);
            float y = 56;
            if (hintLine1 != "")
            {
                g.DrawString(hintLine1, fBody, bBody, 58, y);
                y += 32;
            }
            if (hintLine2 != "")
            {
                g.DrawString(hintLine2, fBody, bBlue, 58, y);
            }
        }
    }

    static void DrawLock(Graphics g, int x, int y)
    {
        Color c = Color.FromArgb(255, 235, 90, 90);
        using (Pen p = new Pen(c, 3f))
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            g.DrawArc(p, x + 2, y, 13, 14, 180, 180);
        }
        using (Brush b = new SolidBrush(c))
        using (GraphicsPath gp = RoundedPath(new Rectangle(x, y + 9, 17, 14), 3))
        {
            g.FillPath(b, gp);
        }
    }

    static void Unlock(string msg)
    {
        locked = false;
        armed = false;
        lastLeftClick = DateTime.MinValue;
        lockStart = DateTime.MinValue;
        idleSuppressUntil = DateTime.Now.AddSeconds(5);
        Log("UNLOCK: " + msg);
        hint.Hide();
        btn.BackColor = Color.FromArgb(30, 110, 60);
        btn.Text = msg;
        System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer();
        hide.Interval = 2500;
        hide.Tick += delegate(object s, EventArgs a)
        {
            hide.Stop();
            hide.Dispose();
            if (!locked) win.Hide();
        };
        hide.Start();
        UpdateTray();
    }

    static void ExitApp()
    {
        Log("exit: 用户退出");
        FlushLog();
        if (tray != null) tray.Visible = false;
        win.Hide();
        hint.Hide();
        Application.ExitThread();
    }

    static void BtnMouseDown(object sender, MouseEventArgs e)
    {
        Log("btn click " + e.Button + " locked=" + locked + " armed=" + armed);
        if (!locked && countdownEnd != DateTime.MinValue && e.Button == MouseButtons.Left)
        {
            // 倒计时期间点按钮 = 取消本次自动锁定
            countdownEnd = DateTime.MinValue;
            Log("countdown cancelled (btn)");
            win.Hide();
            return;
        }
        if (!locked || !sButton) return;
        DateTime now = DateTime.Now;
        if (e.Button == MouseButtons.Right)
        {
            armed = true;
            armedAt = now;
            btn.Text = "已就绪 → 左键单击（或连点两下）解锁";
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            if (armed && (now - armedAt).TotalSeconds <= 8)
            {
                Unlock("已解锁（右键+左键）");
                return;
            }
            if (lastLeftClick != DateTime.MinValue && (now - lastLeftClick).TotalMilliseconds <= 1200)
            {
                Unlock("已解锁（连点两下）");
                return;
            }
            lastLeftClick = now;
            statusAt = now;
            btn.Text = "再点一下解锁（或右键后左键）";
        }
    }

    // ================= 钩子 =================

    static IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try { return KeyboardInner(nCode, wParam, lParam); }
        catch (Exception ex) { Log("kb hook ex: " + ex.Message); }
        return CallNextHookEx(kHook, nCode, wParam, lParam);
    }

    static IntPtr KeyboardInner(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            hookEventAt = DateTime.Now;
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
                    Log("hotkey Ctrl+Alt+" + ((char)vk) + " matched, locked=" + locked + (sTest ? " (selftest)" : ""));
                    if (vk == 0x55)
                    {
                        // Ctrl+Alt+U：仅作备用解锁
                        if (locked) Post(delegate { Unlock("已解锁  |  Ctrl+Alt+U"); });
                    }
                    else
                    {
                        if (locked) Post(delegate { Unlock("已解锁  |  Ctrl+Alt+L 重新上锁"); });
                        else Post(delegate { LockNow("快捷键"); });
                    }
                }
                return (IntPtr)1;
            }

            if (sQuitHotkey && ctrlOK && altOK && down && vk == 0x51)
            {
                if (!hotkeyLatch)
                {
                    hotkeyLatch = true;
                    Log("quit hotkey Ctrl+Alt+Q matched");
                    Post(ExitApp);
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
        catch (Exception ex) { Log("mouse hook ex: " + ex.Message); }
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
        if (win == null || !win.Visible) return false;
        MSLLHOOKSTRUCT m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
        Rectangle r = win.RectangleToScreen(win.ClientRectangle);
        if (m.x < r.Left || m.x >= r.Right || m.y < r.Top || m.y >= r.Bottom) return false;
        POINT p;
        p.x = m.x;
        p.y = m.y;
        IntPtr h = WindowFromPoint(p);
        return (h == win.Handle || h == btn.Handle);
    }

    static IntPtr MouseInner(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            hookEventAt = DateTime.Now;
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
            }
            if (msg != 0x020A && msg != 0x020E)
            {
                MSLLHOOKSTRUCT mm = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                Log("click msg=" + msg + " at " + mm.x + "," + mm.y + " -> " + (pass ? "PASS" : "SWALLOW"));
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
            ClientSize = new Size(400, 340);
            Font = new Font("Microsoft YaHei", 9.5f);
            TopMost = true;

            desc = new Label();
            desc.Text = "首次使用：请选择需要的功能（之后可在托盘菜单 → 设置里修改）";
            desc.ForeColor = Color.FromArgb(90, 90, 90);
            desc.Location = new Point(14, 10);
            desc.Size = new Size(372, 22);
            desc.Visible = firstRun;
            Controls.Add(desc);

            int y = firstRun ? 40 : 14;
            cbHotkey = MakeCheck("启用快捷键 Ctrl+Alt+L（解锁 / 上锁）", y, hk); y += 32;
            cbButton = MakeCheck("启用屏幕按钮解锁（右键 + 左键单击）", y, bt); y += 32;
            cbHint = MakeCheck("上锁后显示左上角解锁说明", y, hn); y += 32;
            cbQuit = MakeCheck("启用快捷键 Ctrl+Alt+Q（退出程序）", y, qt); y += 32;
            cbAuto = MakeCheck("开机自动启动（登录后静默运行）", y, au); y += 40;

            cbIdle = new CheckBox();
            cbIdle.Text = "无操作自动上锁：";
            cbIdle.Location = new Point(14, y);
            cbIdle.Size = new Size(160, 24);
            cbIdle.Checked = il;
            Controls.Add(cbIdle);

            numIdle = new NumericUpDown();
            numIdle.Minimum = 1;
            numIdle.Maximum = 600;
            numIdle.Value = ilmin;
            numIdle.Location = new Point(178, y + 1);
            numIdle.Size = new Size(70, 24);
            numIdle.Enabled = il;
            Controls.Add(numIdle);

            Label minLbl = new Label();
            minLbl.Text = "分钟（键盘鼠标都不动这么久后自动锁定）";
            minLbl.ForeColor = Color.FromArgb(90, 90, 90);
            minLbl.Location = new Point(254, y + 3);
            minLbl.Size = new Size(140, 40);
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

            Button ok = new Button();
            ok.Text = firstRun ? "开始使用" : "确定";
            ok.Location = new Point(190, 292);
            ok.Size = new Size(90, 32);
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
                cancel.Location = new Point(290, 292);
                cancel.Size = new Size(90, 32);
                cancel.Click += delegate { Close(); };
                Controls.Add(cancel);
            }

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
            c.Location = new Point(14, y);
            c.Size = new Size(350, 24);
            c.Checked = val;
            return c;
        }
    }
}
