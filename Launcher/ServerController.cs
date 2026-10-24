using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WebPrint.Desktop;

record LauncherSettings(int Port = 5080, bool AllowLan = true, bool? PhysicalPrinting = null) {
    public static bool ReadPrintingDefault(string root) {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"appsettings.json")), new JsonDocumentOptions { CommentHandling=JsonCommentHandling.Skip, AllowTrailingCommas=true });
        return settings.RootElement.TryGetProperty("Printing", out var printing) && printing.TryGetProperty("Enabled", out var enabled) && enabled.GetBoolean();
    }
    public static LauncherSettings Load(string path) {
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path)) ?? throw new Exception("启动器配置文件为空。");
        ValidatePort(settings.Port);
        return settings;
    }
    public static void ValidatePort(int port) { if (port is < 1 or > 65535) throw new ArgumentException("端口必须是 1～65535 之间的整数。"); }
    public void Save(string path) {
        ValidatePort(Port);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}

sealed class ServerController(string root) : IDisposable {
    Process? process;
    string? pipeName;
    public event Action<string>? Log;
    public event Action? Exited;
    public bool Running => process != null && !process.HasExited;
    public int Port { get; private set; }
    public bool AllowLan { get; private set; }
    public string Url => $"http://localhost:{Port}";
    public async Task StartAsync(LauncherSettings settings) {
        if (Running) throw new InvalidOperationException("服务已在运行，请先停止再修改设置。");
        LauncherSettings.ValidatePort(settings.Port);
        var executable = Path.Combine(root, "WebPrint.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("未找到 WebPrint.exe。请完整解压发布包，保留启动器与服务程序在同一目录。");
        // Fail clearly before launching when another application owns this endpoint.
        var probe = new TcpListener(settings.AllowLan ? IPAddress.Any : IPAddress.Loopback, settings.Port);
        try { probe.Start(); }
        catch (SocketException) { throw new InvalidOperationException($"端口 {settings.Port} 被占用或无法监听，请选择其他端口。"); }
        finally { probe.Stop(); }
        process?.Dispose();
        Port = settings.Port; AllowLan = settings.AllowLan; pipeName = "WebPrint-" + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(executable) {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("--urls"); info.ArgumentList.Add($"http://{(AllowLan ? "0.0.0.0" : "127.0.0.1")}:{Port}");
        info.ArgumentList.Add("--Desktop:ControlPipe"); info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add("--Printing:Enabled"); info.ArgumentList.Add((settings.PhysicalPrinting ?? LauncherSettings.ReadPrintingDefault(root)).ToString());
        // Launcher owns the child lifetime. Child uses the same file/credential permissions.
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke(e.Data); };
        process.Exited += (_, _) => Exited?.Invoke();
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(25)) {
            if (process.HasExited) throw new InvalidOperationException("服务启动失败，请查看下方日志；检查数据目录权限、已有实例或端口冲突。");
            try { if (await SendAsync("ping", 700) == "ready") return; }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            await Task.Delay(150);
        }
        // Do not leave an untracked child if initialization cannot complete.
        await StopAsync(force: true);
        throw new TimeoutException("服务启动超时，请检查日志后重试。");
    }
    async Task<string?> SendAsync(string command, int milliseconds) {
        using var timeout = new CancellationTokenSource(milliseconds);
        using var pipe = new NamedPipeClientStream(".", pipeName!, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
        return await reader.ReadLineAsync(timeout.Token);
    }
    public async Task SetPhysicalPrintingAsync(bool enabled) {
        if (!Running) throw new InvalidOperationException("服务尚未运行。");
        var command=enabled?"printing:on":"printing:off";
        if(await SendAsync(command,3000)!=command) throw new InvalidOperationException("实机打印开关未能确认生效，请检查服务状态。");
    }
    public async Task StopAsync(bool force = false) {
        if (!Running) return;
        if (force) { process!.Kill(entireProcessTree: true); await process.WaitForExitAsync(); return; }
        try { await SendAsync("stop", 3000); }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) {
            throw new InvalidOperationException("无法联系服务停止通道。可重试，或选择强制停止；提交中的打印任务需核查。", e);
        }
        try { await process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
        catch (TimeoutException) { throw new InvalidOperationException("服务尚未退出。请稍候重试；必要时可强制停止并核查打印任务。"); }
    }
    public static string[] LanAddresses(int port) {
        try {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
                .Select(a => $"http://{a.Address}:{port}").Distinct().ToArray();
        } catch { return Array.Empty<string>(); }
    }
    public void Dispose() => process?.Dispose();
}
