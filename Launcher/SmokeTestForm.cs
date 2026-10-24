using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http.Json;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Reflection;
using WebPrint.Infrastructure;

namespace WebPrint.Desktop;

// Opt-in regression harness. Always isolates credentials, data and launcher settings.
sealed class SmokeTestForm : Form {
    [DllImport("user32.dll")] static extern nint SendMessage(nint window, uint message, nint parameter, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(nint window);
    public SmokeTestForm() { ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Shown += async (_, _) => await Run(); }
    static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    static IEnumerable<Control> All(Control parent) { foreach (Control c in parent.Controls) { yield return c; foreach (var child in All(c)) yield return child; } }
    static void SaveScreenshot(MainForm form, string path) {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path);
    }
    static bool FitsAncestors(Control control) {
        var bounds = control.RectangleToScreen(control.ClientRectangle);
        for (var parent = control.Parent; parent != null; parent = parent.Parent)
            if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds)) return false;
        return true;
    }
    static void DoubleClickTray(MainForm form) => typeof(NotifyIcon)
        .GetMethod("OnDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(form.TrayIcon, new object[] { EventArgs.Empty });
    static async Task Until(Func<bool> condition) { var deadline = DateTime.UtcNow.AddSeconds(40); while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("UI condition timed out"); await Task.Delay(100); } }
    async Task Run() {
        var root = Environment.GetEnvironmentVariable("WEBPRINT_DESKTOP_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(root)) { Environment.ExitCode = 1; Close(); return; }
        Directory.CreateDirectory(root); MainForm? form = null;
        var checks = new List<string>();
        void Check(bool value, string description) { if (!value) throw new Exception(description); checks.Add(description); }
        try {
            Environment.SetEnvironmentVariable("Storage__Path", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("Printing__Enabled", "false");
            Environment.SetEnvironmentVariable("WEBPRINT_ADMIN_PASSWORD", Guid.NewGuid().ToString("N"));
            var settingsPath = Path.Combine(root, "launcher.json");
            var firstPort = FreePort(); new LauncherSettings(firstPort, false).Save(settingsPath);
            Check(LauncherSettings.Load(settingsPath).Port == firstPort, "Custom port persists");
            foreach (var invalid in new[] { 0, -1, 65536 }) {
                try { LauncherSettings.ValidatePort(invalid); throw new Exception("Accepted invalid port"); }
                catch (ArgumentException) { checks.Add("Reject invalid port " + invalid); }
            }
            using (var occupied = new TcpListener(IPAddress.Loopback, 0)) {
                occupied.Start(); using var controller = new ServerController(RuntimeLayout.AppRoot);
                try { await controller.StartAsync(new(((IPEndPoint)occupied.LocalEndpoint).Port, false)); throw new Exception("Accepted occupied port"); }
                catch (InvalidOperationException e) when (e.Message.Contains("端口")) { checks.Add("Occupied port rejected before spawning"); }
            }
            var openedUrl=string.Empty;
            form = new MainForm(settingsPath,url=>openedUrl=url); form.Show();
            var controls = All(form).ToArray(); var port = controls.OfType<NumericUpDown>().Single();
            var start = controls.OfType<Button>().Single(b => b.Text == "启动服务");
            var stop = controls.OfType<Button>().Single(b => b.Text == "停止服务");
            var open = controls.OfType<Button>().Single(b => b.Text == "打开网页");
            var physical = controls.OfType<CheckBox>().Single(b=>b.Text=="实机打印");
            SaveScreenshot(form, Path.Combine(root, "desktop-stopped.png"));
            var normalSize = form.Size; form.Size = form.MinimumSize; await Task.Delay(150);
            Check(controls.Where(c => c is Button or CheckBox or NumericUpDown).All(FitsAncestors), "All settings and actions fit at the minimum window size");
            SaveScreenshot(form, Path.Combine(root, "desktop-small.png")); form.Size = normalSize;
            physical.Checked=true;await Until(()=>start.Enabled);
            Check(LauncherSettings.Load(settingsPath).PhysicalPrinting==true,"Physical-print setting persists without starting service");
            Check((int)port.Value == firstPort, "UI restores custom port");
            start.PerformClick(); await Until(() => stop.Enabled);
            Check(!port.Enabled && open.Enabled && !start.Enabled, "Running UI locks settings and enables browser");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var response = await http.GetAsync($"http://127.0.0.1:{firstPort}/");
            Check(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("WebPrint"), "UI starts real server on selected non-default port");
            http.DefaultRequestHeaders.Add("X-WebPrint","1");
            var login=await http.PostAsJsonAsync($"http://127.0.0.1:{firstPort}/api/login",new {username="admin",password=Environment.GetEnvironmentVariable("WEBPRINT_ADMIN_PASSWORD")});
            login.EnsureSuccessStatusCode();
            async Task<bool> PrintingState() {using var state=JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{firstPort}/api/system"));return state.RootElement.GetProperty("printingEnabled").GetBoolean();}
            Check(await PrintingState(),"Desktop switch enables the real server policy (no job submitted)");
            physical.Checked=false;await Until(()=>stop.Enabled);
            Check(!await PrintingState() && LauncherSettings.Load(settingsPath).PhysicalPrinting==false,"Running switch disables and persists policy without restart");
            var windowHandle = form.Handle; var windowBounds = form.Bounds;
            // Use the Windows title-bar minimize command, then allow queued size messages to run.
            SendMessage(form.Handle, 0x0112, 0xF020, 0); await Until(()=>!form.Visible);
            Check(form.TrayIconVisible && (await http.GetAsync($"http://127.0.0.1:{firstPort}/")).IsSuccessStatusCode,"Minimize hides window to tray and keeps HTTP service running");
            ((ToolStripMenuItem)form.TrayMenu.Items[1]).PerformClick();
            Check(openedUrl==$"http://localhost:{firstPort}","Tray open-web action works while the main window is hidden");
            ((ToolStripMenuItem)form.TrayMenu.Items[0]).PerformClick();await Until(()=>form.Visible);
            Check(form.WindowState==FormWindowState.Normal,"Tray menu restores main window");
            await Task.Delay(350);
            Check(IsWindowVisible(form.Handle) && !IsIconic(form.Handle) && form.ShowInTaskbar, "Restored window remains visible to Windows after queued messages");
            Check(form.Handle == windowHandle, "Tray hide and restore do not recreate the native window");
            Check(form.Bounds == windowBounds, "Tray restore preserves window size and position");
            for (var i = 0; i < 5; i++) {
                SendMessage(form.Handle, 0x0112, 0xF020, 0); await Until(()=>!form.Visible);
                if (i % 2 == 0) DoubleClickTray(form);
                else ((ToolStripMenuItem)form.TrayMenu.Items[0]).PerformClick();
                await Task.Delay(200);
                Check(form.Visible && IsWindowVisible(form.Handle) && !IsIconic(form.Handle) && form.Handle == windowHandle,
                    "Repeated native minimize and tray restore stays visible, cycle " + (i + 1));
            }
            form.WindowState = FormWindowState.Maximized; await Task.Delay(150);
            SendMessage(form.Handle, 0x0112, 0xF020, 0); await Until(()=>!form.Visible);
            ((ToolStripMenuItem)form.TrayMenu.Items[0]).PerformClick(); await Task.Delay(200);
            Check(form.Visible && IsWindowVisible(form.Handle) && !IsIconic(form.Handle) && form.WindowState == FormWindowState.Maximized,
                "Tray restore preserves a maximized window");
            form.WindowState = FormWindowState.Normal;
            form.WindowState = FormWindowState.Minimized;
            // Restore before the posted hide callback executes; it must not hide the restored window.
            ((ToolStripMenuItem)form.TrayMenu.Items[0]).PerformClick(); await Task.Delay(250);
            Check(form.Visible && IsWindowVisible(form.Handle) && !IsIconic(form.Handle), "Immediate tray restore is not hidden by a pending minimize callback");
            var output = controls.OfType<RichTextBox>().Single();
            await Until(() => output.Text.Contains("admin 密码："));
            Check(output.Text.Contains("admin 密码："), "Admin password displayed on startup");
            output.Text = Regex.Replace(output.Text, @"admin 密码：[^\r\n]+", "admin 密码：[redacted]");
            SaveScreenshot(form, Path.Combine(root, "desktop.png"));
            stop.PerformClick(); await Until(() => start.Enabled);
            Check(port.Enabled && !open.Enabled, "Graceful stop restores editable port");
            try { await http.GetAsync($"http://127.0.0.1:{firstPort}/"); throw new Exception("Server remained alive after stop"); }
            catch (HttpRequestException) { checks.Add("Stop closes HTTP endpoint"); }
            var secondPort = FreePort(); port.Value = secondPort;
            start.PerformClick(); await Until(() => stop.Enabled);
            Check((await http.GetAsync($"http://127.0.0.1:{secondPort}/")).IsSuccessStatusCode, "Restart applies changed port");
            await Until(() => output.Text.Contains("admin 密码："+Environment.GetEnvironmentVariable("WEBPRINT_ADMIN_PASSWORD")));
            Check(output.Text.Contains("admin 密码："+Environment.GetEnvironmentVariable("WEBPRINT_ADMIN_PASSWORD")),"Admin password displayed again after restart");
            stop.PerformClick(); await Until(() => start.Enabled);
            Check(LauncherSettings.Load(settingsPath).Port == secondPort, "Changed port saved for next launch");
            if(Directory.Exists(Path.Combine(RuntimeLayout.AppRoot,"lang","fr"))) {
                var resource=typeof(Form).Assembly.GetSatelliteAssembly(CultureInfo.GetCultureInfo("fr"));
                Check(resource.Location.StartsWith(Path.Combine(RuntimeLayout.AppRoot,"lang"),StringComparison.OrdinalIgnoreCase),"WinForms localized satellite loads from lang folder");
            }
            start.PerformClick();await Until(()=>stop.Enabled);
            form.Close();await Until(()=>!form.Visible);
            Check(form.TrayIconVisible && (await http.GetAsync($"http://127.0.0.1:{secondPort}/")).IsSuccessStatusCode,"Closing window also preserves service in tray");
            ((ToolStripMenuItem)form.TrayMenu.Items[0]).PerformClick(); await Task.Delay(250);
            Check(form.Visible && IsWindowVisible(form.Handle) && !IsIconic(form.Handle), "Tray restores the window after closing with X");
            form.Close(); await Until(()=>!form.Visible);
            ((ToolStripMenuItem)form.TrayMenu.Items[3]).PerformClick();await Until(()=>form.IsDisposed);
            try {await http.GetAsync($"http://127.0.0.1:{secondPort}/");throw new Exception("Service remained alive after tray exit");}
            catch(HttpRequestException){checks.Add("Tray exit closes window and gracefully stops HTTP service");}
            form = null;
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
        } catch (Exception e) {
            Environment.ExitCode = 1;
            if(form!=null) {
                var text=All(form).OfType<RichTextBox>().Single().Text;
                var secret=Environment.GetEnvironmentVariable("WEBPRINT_ADMIN_PASSWORD");
                if(!string.IsNullOrEmpty(secret))text=text.Replace(secret,"[redacted]");
                File.WriteAllText(Path.Combine(root,"desktop-error-log.txt"),text);
            }
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed = false, checks, error = e.ToString() }));
        } finally {
            // The external harness also bounds runtime and kills its own process tree on failure.
            if (form != null) { await form.StopForTest(); form.Hide(); }
            Application.ExitThread();
        }
    }
}
