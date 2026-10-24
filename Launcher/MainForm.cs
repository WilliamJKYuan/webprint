using System.Diagnostics;
using WebPrint.Infrastructure;

namespace WebPrint.Desktop;

sealed class MainForm : Form {
    readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 5080, Width = 132, Font = new Font("Segoe UI", 12), BorderStyle = BorderStyle.FixedSingle, ForeColor = Theme.Ink };
    readonly CheckBox lan = new() { Text = "允许局域网访问", AutoSize = true, Checked = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 6, 0, 0) };
    readonly CheckBox physicalPrinting = new() { Text = "实机打印", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 6, 0, 0) };
    readonly Label status = new() { Text = "● 服务未启动", ForeColor = Theme.Muted, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(8, 0, 0, 0) };
    readonly Button start = Button("启动服务", true), stop = Button("停止服务"), open = Button("打开网页"), save = Button("保存设置"), force = Button("强制停止");
    readonly TextBox address = new() { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    readonly RichTextBox log = new() { ReadOnly = true, Dock = DockStyle.Fill, BackColor = Theme.Raised, ForeColor = Theme.Ink, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f), WordWrap = true, DetectUrls = false };
    readonly ServerController server = new(RuntimeLayout.AppRoot);
    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip trayMenu = new();
    readonly ToolStripMenuItem trayShow = new("显示主窗口"), trayOpen = new("打开网页"), trayExit = new("退出程序");
    readonly string settingsPath;
    readonly Action<string> openBrowser;
    bool busy, closing, loading = true;
    bool restoringWindow, minimizePending;
    FormWindowState restoreWindowState = FormWindowState.Normal;
    public MainForm(string? settingsFile = null, Action<string>? browserOpener = null) {
        settingsPath = settingsFile ?? Path.Combine(RuntimeLayout.AppRoot, "launcher.json");
        openBrowser=browserOpener ?? (url=>Process.Start(new ProcessStartInfo(url){UseShellExecute=true}));
        Text = "WebPrint · 本地打印服务"; Size = new Size(960, 840); MinimumSize = new Size(820, 740);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Ground; ForeColor = Theme.Ink;
        Font = new Font("Microsoft YaHei UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(28), BackColor = BackColor };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 134));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var header = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        header.Controls.Add(new PaperMark { Location = new Point(0, 3) });
        header.Controls.Add(new Label { Text = "WebPrint 服务管理", Font = new Font("Microsoft YaHei UI", 21, FontStyle.Bold), AutoSize = true, Location = new Point(42, 0) });
        header.Controls.Add(new Label { Text = "先启动服务，再打开网页上传文件、查看打印记录。", AutoSize = true, Location = new Point(1, 47), ForeColor = Theme.Muted });
        layout.Controls.Add(header, 0, 0);
        var settingsCard = Card();
        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var settingHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        settingHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); settingHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingHeading.Controls.Add(Heading("服务设置"), 0, 0); settingHeading.Controls.Add(status, 1, 0); settings.Controls.Add(settingHeading, 0, 0);
        var configRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
        configRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
        configRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); configRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        var portGroup = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        portGroup.Controls.Add(new Label { Text = "网页端口", AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        port.Margin = new Padding(0); portGroup.Controls.Add(port); configRow.Controls.Add(portGroup, 0, 0);
        configRow.Controls.Add(SettingGroup(lan, "其他电脑也能打开打印网页"), 1, 0);
        configRow.Controls.Add(SettingGroup(physicalPrinting, "自动保存，服务运行时也能切换"), 2, 0);
        settings.Controls.Add(configRow, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.AddRange(new Control[] { start, stop, open, save, force }); settings.Controls.Add(buttons, 0, 2);
        settingsCard.Controls.Add(settings); layout.Controls.Add(settingsCard, 0, 1);
        var addressesCard = Card();
        var addresses = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2, Margin = new Padding(0) };
        addresses.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); addresses.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        addresses.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); addresses.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        addresses.Controls.Add(Heading("打开打印网页"), 0, 0);
        var copy = Button("复制地址"); copy.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        copy.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(address.Text)) { Clipboard.SetText(address.Text); AddLog(lan.Checked ? "地址已复制。把局域网地址发给需要打印的人即可。" : "本机地址已复制。当前仅允许这台电脑访问。"); } };
        addresses.Controls.Add(copy, 1, 0); addresses.SetRowSpan(copy, 2); addresses.Controls.Add(address, 0, 1); addressesCard.Controls.Add(addresses); layout.Controls.Add(addressesCard, 0, 2);
        var logsCard = Card(); logsCard.Margin = new Padding(0);
        var logs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        logs.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); logs.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); logs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logs.Controls.Add(Heading("运行日志"), 0, 0);
        logs.Controls.Add(new Label { Text = "每次启动服务，admin 的登录密码都会显示在这里。", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0) }, 0, 1);
        var logInset = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Raised, Padding = new Padding(12), Margin = new Padding(0) };
        logInset.Controls.Add(log); logs.Controls.Add(logInset, 0, 2); logsCard.Controls.Add(logs); layout.Controls.Add(logsCard, 0, 3);
        layout.Controls.Add(new Label { Text = "关窗口后服务会留在托盘里。要完全退出，请右键托盘图标，选择“退出程序”。", Dock = DockStyle.Fill, ForeColor = Theme.Muted, Padding = new Padding(0, 12, 0, 0), Font = new Font(Font.FontFamily, 9), Margin = new Padding(0) }, 0, 4);
        Controls.Add(layout);
        try { var s = LauncherSettings.Load(settingsPath); port.Value = s.Port; lan.Checked = s.AllowLan; physicalPrinting.Checked = s.PhysicalPrinting ?? LauncherSettings.ReadPrintingDefault(RuntimeLayout.AppRoot); }
        catch (Exception e) { AddLog("读取启动设置失败，使用默认端口。" + e.Message); }
        start.Click += async (_, _) => await StartServer();
        stop.Click += async (_, _) => await StopServer();
        force.Click += async (_, _) => {
            if (MessageBox.Show(this, "强制停止可能中断正在提交的打印任务。是否继续？", "强制停止", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) await StopServer(true);
        };
        open.Click += (_, _) => OpenWeb();
        save.Click += (_, _) => { try { CurrentSettings().Save(settingsPath); AddLog("设置已保存，下次启动还会用这份设置。"); } catch (Exception e) { ShowError(e); } };
        port.ValueChanged += (_, _) => UpdateAddresses(); lan.CheckedChanged += (_, _) => UpdateAddresses();
        physicalPrinting.CheckedChanged += async (_, _) => { if(!loading) await ApplyPrintingSetting(); };
        trayMenu.Items.AddRange(new ToolStripItem[]{trayShow,trayOpen,new ToolStripSeparator(),trayExit});
        tray.Icon=(Icon)SystemIcons.Application.Clone();tray.Text="WebPrint · 服务已停止";tray.ContextMenuStrip=trayMenu;tray.Visible=true;
        tray.DoubleClick += (_,_) => RestoreFromTray();
        trayShow.Click += (_,_) => RestoreFromTray();
        trayOpen.Click += (_,_) => OpenWeb();
        trayExit.Click += async (_,_) => await ExitApplication();
        Resize += (_, _) => {
            if (closing || restoringWindow) return;
            if (WindowState != FormWindowState.Minimized) {
                if (Visible) restoreWindowState = WindowState;
                return;
            }
            if (minimizePending) return;
            minimizePending = true;
            // Finish processing Windows' minimize/size messages before hiding the form.
            BeginInvoke(() => {
                minimizePending = false;
                if (!IsDisposed && !closing && !restoringWindow && WindowState == FormWindowState.Minimized) HideToTray();
            });
        };
        server.Log += AddLog; server.Exited += () => OnUi(() => { if (!busy) RefreshState(); });
        FormClosing += async (_, e) => {
            if (closing) return;
            e.Cancel = true;
            if(e.CloseReason==CloseReason.UserClosing) { HideToTray(); return; }
            if(!busy) await ExitApplication();
        };
        FormClosed += (_, _) => {tray.Visible=false;tray.Icon?.Dispose();tray.Dispose();trayMenu.Dispose();server.Dispose();};
        loading=false;
        RefreshState(); UpdateAddresses(); AddLog("点击“启动服务”后，就能打开网页打印了。其他电脑要访问，请先允许此端口通过防火墙。");
    }
    LauncherSettings CurrentSettings() => new((int)port.Value, lan.Checked, physicalPrinting.Checked);
    internal Task StopForTest() => server.StopAsync(force: true);
    internal ContextMenuStrip TrayMenu => trayMenu;
    internal bool TrayIconVisible => tray.Visible;
    internal NotifyIcon TrayIcon => tray;
    void OpenWeb() {try {if(server.Running)openBrowser(server.Url);} catch(Exception e){ShowError(e);}}
    void HideToTray() {
        if (closing || restoringWindow) return;
        // Hide also removes the taskbar button. Changing ShowInTaskbar recreates the
        // native window and can re-enter minimize handling with a stale window state.
        Hide();
        tray.Visible = true;
    }
    void RestoreFromTray() {
        if (closing || IsDisposed || restoringWindow) return;
        var targetState = restoreWindowState;
        restoringWindow = true;
        try {
            Show();
            WindowState = targetState;
            BringToFront();
            Activate();
        } finally { restoringWindow = false; }
    }
    async Task ExitApplication() {
        if(busy||closing)return;
        if(server.Running && !await StopServer()) {RestoreFromTray();return;}
        closing=true;tray.Visible=false;BeginInvoke(()=>Close());
    }
    async Task ApplyPrintingSetting() {
        busy=true;status.Text="● 正在切换打印设置…";RefreshState();var saved=false;
        try {
            CurrentSettings().Save(settingsPath);saved=true;
            if(server.Running)await server.SetPhysicalPrintingAsync(physicalPrinting.Checked);
            AddLog(physicalPrinting.Checked?"实机打印已开启，接下来可以把文件发送到打印机。":"实机打印已关闭，新任务不会发送到打印机。已经发送的任务不会取消。");
        } catch(Exception e) {
            if(!saved) {loading=true;physicalPrinting.Checked=!physicalPrinting.Checked;loading=false;ShowError(e);}
            else ShowError(new Exception("新设置已保存，但还没确认服务是否收到。请停止服务后重新启动。"+e.Message));
        } finally {busy=false;RefreshState();}
    }
    static Button Button(string title, bool primary = false) => new ThemeButton(title, primary);
    static SurfacePanel Card() => new() { Dock = DockStyle.Fill, Padding = new Padding(22), Margin = new Padding(0, 0, 0, 16) };
    static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold), Margin = new Padding(0) };
    static Control SettingGroup(CheckBox check, string help) {
        var group = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        group.Controls.Add(check);
        group.Controls.Add(new Label { Text = help, AutoSize = true, ForeColor = Theme.Muted, Font = new Font("Microsoft YaHei UI", 9), Margin = new Padding(0, 9, 0, 0) });
        return group;
    }
    void OnUi(Action action) { if (IsDisposed || Disposing) return; if (InvokeRequired) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } else action(); }
    void AddLog(string value) => OnUi(() => { if (log.TextLength > 100000) log.Text = log.Text[^60000..]; log.AppendText(value + Environment.NewLine); log.SelectionStart = log.TextLength; log.ScrollToCaret(); });
    void ShowError(Exception e) { AddLog(e.Message); if(!Visible)RestoreFromTray(); MessageBox.Show(this, e.Message, "WebPrint", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    void RefreshState() {
        var running = server.Running;
        start.Enabled = !busy && !running; stop.Enabled = !busy && running; force.Enabled = !busy && running; open.Enabled = !busy && running;
        ((ThemeButton)start).Primary = !running; ((ThemeButton)open).Primary = running;
        save.Enabled = !busy; port.Enabled = !busy && !running; lan.Enabled = !busy && !running;physicalPrinting.Enabled=!busy;
        trayOpen.Enabled=running&&!busy;trayExit.Enabled=!busy;tray.Text=running?$"WebPrint · 运行中（端口 {server.Port}）":"WebPrint · 服务已停止";
        if (!busy) { status.Text = running ? "● 服务运行中" : "● 服务未启动"; status.ForeColor = running ? Theme.Positive : Theme.Muted; }
    }
    void UpdateAddresses() {
        var p = (int)port.Value;
        var lines = new List<string> { $"本机：http://localhost:{p}" };
        if (lan.Checked) { var addresses = ServerController.LanAddresses(p); lines.AddRange(addresses.Length == 0 ? new[] { $"局域网：http://服务器IP:{p}" } : addresses.Select(a => "局域网：" + a)); }
        else lines.Add("仅本机访问");
        address.Text = string.Join(Environment.NewLine, lines);
    }
    async Task StartServer() {
        busy = true; status.Text = "● 正在启动…"; RefreshState();
        try { var s = CurrentSettings(); s.Save(settingsPath); await server.StartAsync(s); AddLog("服务已启动，网页地址：" + server.Url); }
        catch (Exception e) { ShowError(e); }
        finally { busy = false; RefreshState(); }
    }
    async Task<bool> StopServer(bool forceStop = false) {
        busy = true; status.Text = "● 正在停止…"; RefreshState();
        try { await server.StopAsync(forceStop); AddLog("服务已停止。"); return true; }
        catch (Exception e) { ShowError(e); return false; }
        finally { busy = false; RefreshState(); }
    }
}
