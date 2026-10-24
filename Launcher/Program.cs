using System.Security.Cryptography;
using System.Text;
using WebPrint.Infrastructure;

namespace WebPrint.Desktop;

static class Program {
    [STAThread]
    static void Main(string[] args) {
        RuntimeLayout.Initialize();
        ApplicationConfiguration.Initialize();
        if (args.Contains("--smoke-test")) { Application.Run(new SmokeTestForm()); return; }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RuntimeLayout.AppRoot.ToUpperInvariant())));
        using var instance = new Mutex(true, "Local\\WebPrint.Desktop." + key, out var created);
        if (!created) { MessageBox.Show("这个目录的启动器已打开，请查看任务栏。", "WebPrint"); return; }
        Application.Run(new MainForm());
    }
}
