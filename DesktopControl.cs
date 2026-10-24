using System.IO.Pipes;

// Per-launch private control channel. No shutdown endpoint is exposed over HTTP.
class DesktopControl(IConfiguration config, IHostApplicationLifetime lifetime, Store db) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var name = config["Desktop:ControlPipe"];
        if (string.IsNullOrWhiteSpace(name)) return;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        while (!stoppingToken.IsCancellationRequested) {
            try {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken);
                // A client that connects without sending data must not block shutdown forever.
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var command = await reader.ReadLineAsync(requestTimeout.Token);
                if(command is "printing:on" or "printing:off") {
                    var enabled=command=="printing:on";
                    db.Event("desktop",null,"printing_changed",enabled?"桌面启用实机打印":"桌面关闭实机打印");
                    config["Printing:Enabled"]=enabled.ToString();
                    await writer.WriteLineAsync(command); continue;
                }
                if (command == "stop") { await writer.WriteLineAsync("stopping"); lifetime.StopApplication(); return; }
                if (command == "ping") {
                    await started.Task.WaitAsync(requestTimeout.Token);
                    await writer.WriteLineAsync("ready");
                }
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
}
