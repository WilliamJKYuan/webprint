using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

// The web server never hosts COM. Each conversion has a separate, bounded STA worker.
static class MicrosoftOfficeConversion {
    public static async Task Convert(string source, string output, ILogger logger, CancellationToken ct) {
        var word = Path.GetExtension(source) is ".doc" or ".docx";
        var label = word ? "Microsoft Word" : "Microsoft Excel";
        if (!MicrosoftOfficeWorker.IsInstalled(word ? "Word.Application" : "Excel.Application"))
            throw new AppError($"服务电脑上没找到 {label}。请安装桌面版 Office，或在系统管理中改用 LibreOffice。", 503);
        var executable = Environment.ProcessPath ?? throw new AppError("无法启动 Office 转换程序。", 503);
        var info = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(source)!,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(PrintEngine).Assembly.Location);
        foreach (var argument in new[] { "--office-convert", source, output }) info.ArgumentList.Add(argument);
        using var worker = new Process { StartInfo = info };
        var existing = OfficeProcessIdentity.Existing(word ? "WINWORD" : "EXCEL");
        OfficeProcessIdentity? office = null;
        worker.Start();
        var error = worker.StandardError.ReadToEndAsync();
        var stdout = Task.Run(async () => {
            while (await worker.StandardOutput.ReadLineAsync() is { } line) {
                var identity = OfficeProcessIdentity.Parse(line);
                if (identity != null && !existing.Contains(identity.Id) && identity.Name == (word ? "WINWORD" : "EXCEL"))
                    office = identity;
            }
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try {
            try { await worker.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                throw new AppError($"{label} 转换超过 90 秒。请检查服务电脑上的 Office 是否在等待登录或弹窗，也可以先另存为 PDF 再上传。", 504);
            }
            await stdout;
            if (worker.ExitCode != 0 || !File.Exists(output)) {
                logger.LogWarning("{Application} conversion failed: {Details}", label, await error);
                throw new AppError($"{label} 没能转换这份文件。请确认文件未加密，并在服务电脑上打开一次 Office 完成登录或首次设置。也可以先另存为 PDF 再上传。");
            }
        } finally {
            try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); await stdout.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) { logger.LogWarning(e, "Office worker cleanup did not complete"); }
            // PID, process creation time and application name must all match our new instance.
            office?.Stop();
        }
    }
}

sealed record OfficeProcessIdentity(int Id, long StartTicks, string Name) {
    const string Prefix = "WEBPRINT_OFFICE|";
    public static HashSet<int> Existing(string name) {
        var result = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(name)) { using (p) result.Add(p.Id); }
        return result;
    }
    public static OfficeProcessIdentity? Parse(string line) {
        var parts = line.Split('|');
        return parts.Length == 4 && line.StartsWith(Prefix, StringComparison.Ordinal) &&
            int.TryParse(parts[1], out var id) && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) &&
            parts[3] is "WINWORD" or "EXCEL" ? new(id, ticks, parts[3]) : null;
    }
    public void Publish() { Console.WriteLine($"{Prefix}{Id}|{StartTicks.ToString(CultureInfo.InvariantCulture)}|{Name}"); Console.Out.Flush(); }
    public void Stop() {
        try {
            using var process = Process.GetProcessById(Id);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == StartTicks && process.ProcessName.Equals(Name, StringComparison.OrdinalIgnoreCase))
                { process.Kill(); process.WaitForExit(5000); }
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

static class MicrosoftOfficeWorker {
    public static bool IsInstalled(string progId) {
        if (!OperatingSystem.IsWindows()) return false;
        try { return Type.GetTypeFromProgID(progId) != null; } catch (COMException) { return false; }
    }
    public static int Run(string[] args) {
        if (!OperatingSystem.IsWindows() || args.Length != 2) return 2;
        OfficeProcessIdentity? owned = null;
        var code = 1;
        // Independent watchdog also bounds a worker whose parent has unexpectedly exited.
        using var watchdog = new Timer(_ => { Volatile.Read(ref owned)?.Stop(); Environment.Exit(124); }, null, TimeSpan.FromSeconds(95), Timeout.InfiniteTimeSpan);
        var thread = new Thread(() => {
            if (!OperatingSystem.IsWindows()) return;
            try { Convert(args[0], args[1], identity => { Volatile.Write(ref owned, identity); identity.Publish(); }); code = 0; }
            catch (Exception e) { Console.Error.WriteLine(e.ToString()); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        return code;
    }
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [SupportedOSPlatform("windows")]
    static OfficeProcessIdentity Identify(nint window, string name, HashSet<int> existing) {
        GetWindowThreadProcessId(window, out var pid);
        using var process = Process.GetProcessById(checked((int)pid));
        if (existing.Contains(process.Id) || !process.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Office 返回了已有窗口，本次转换已停止，以免影响正在编辑的文件。");
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, name);
    }
    [SupportedOSPlatform("windows")]
    static void Convert(string input, string output, Action<OfficeProcessIdentity> started) {
        input = Path.GetFullPath(input); output = Path.GetFullPath(output);
        if (!File.Exists(input) || Path.GetExtension(input) is not (".doc" or ".docx" or ".xls" or ".xlsx"))
            throw new InvalidOperationException("Office 转换文件无效。");
        var word = Path.GetExtension(input) is ".doc" or ".docx";
        var existing = OfficeProcessIdentity.Existing(word ? "WINWORD" : "EXCEL");
        dynamic? application = null, documents = null, document = null, marker = null, window = null, options = null;
        OfficeProcessIdentity? owned = null;
        bool? oldUpdateLinks = null;
        try {
            application = Activator.CreateInstance(Type.GetTypeFromProgID(word ? "Word.Application" : "Excel.Application", true)!);
            if (word) {
                // Word exposes HWND on a document window, not Application. Create only a blank
                // document to identify the new process before opening any uploaded content.
                documents = application!.Documents;
                marker = documents.Add(Visible: false);
                window = marker.ActiveWindow;
                owned = Identify(new nint((int)window.Hwnd), "WINWORD", existing);
            } else {
                owned = Identify(new nint((int)application!.Hwnd), "EXCEL", existing);
                documents = application.Workbooks;
            }
            started(owned);
            application!.Visible = false;
            application.AutomationSecurity = 3; // msoAutomationSecurityForceDisable (VBA).
            if (word) application.DisplayAlerts = 0;
            else application.DisplayAlerts = false;
            var noPassword = "wp-" + Guid.NewGuid().ToString("N")[..10]; // Stay within legacy Office password limits; fail rather than prompt.
            if (word) {
                options = application.Options;
                oldUpdateLinks = (bool)options.UpdateLinksAtOpen;
                options.UpdateLinksAtOpen = false;
                marker!.Close(SaveChanges: 0); Release(marker); marker = null;
                document = documents!.Open(FileName: input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                    PasswordDocument: noPassword, PasswordTemplate: noPassword, WritePasswordDocument: noPassword,
                    WritePasswordTemplate: noPassword, Visible: false, OpenAndRepair: false, NoEncodingDialog: true);
                document.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17, OpenAfterExport: false);
            } else {
                application.EnableEvents = false;
                application.AskToUpdateLinks = false;
                document = documents!.Open(Filename: input, UpdateLinks: 0, ReadOnly: true, Password: noPassword,
                    WriteResPassword: noPassword, IgnoreReadOnlyRecommended: true, Notify: false, AddToMru: false);
                document.ExportAsFixedFormat(Type: 0, Filename: output, Quality: 0, IgnorePrintAreas: false, OpenAfterPublish: false);
            }
        } finally {
            try { if (document != null) { if (word) document.Close(SaveChanges: 0); else document.Close(SaveChanges: false); } } catch (COMException) { }
            try { if (marker != null) marker.Close(SaveChanges: 0); } catch (COMException) { }
            try { if (oldUpdateLinks is bool previous && (object?)options is not null) options!.UpdateLinksAtOpen = previous; } catch (COMException) { }
            try { if ((object?)application is not null && owned is not null) { if (word) application!.Quit(SaveChanges: 0); else application!.Quit(); } } catch (COMException) { }
            Release(window); Release(document); Release(marker); Release(documents); Release(options); Release(application);
        }
    }
    [SupportedOSPlatform("windows")]
    static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
