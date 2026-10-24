using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.DataProtection;
using WebPrint.Infrastructure;

// A hidden child process has no console code page; match the desktop log reader explicitly.
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
RuntimeLayout.Initialize();
if (args.Contains("--self-test")) { await SelfTests.Run(); return; }
// Published builds resolve config, static files and relative paths beside the executable.
var appRoot = Directory.Exists(Path.Combine(RuntimeLayout.AppRoot, "wwwroot"))
    ? RuntimeLayout.AppRoot : Directory.GetCurrentDirectory();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = appRoot });
builder.Configuration["WebPrint:AppRoot"] = builder.Environment.ContentRootPath;
// 普通账户运行不应依赖 Windows 事件日志写入权限。
builder.Logging.ClearProviders(); builder.Logging.AddConsole();
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) {
    var port = builder.Configuration.GetValue("Server:Port", 5080);
    if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException("Server:Port", "网页端口必须在 1～65535 之间。");
    var host = builder.Configuration.GetValue("Server:AllowLan", true) ? "0.0.0.0" : "127.0.0.1";
    builder.WebHost.UseUrls($"http://{host}:{port}");
}
var root = Path.GetFullPath(builder.Configuration["Storage:Path"] ?? "data", builder.Environment.ContentRootPath);
Directory.CreateDirectory(root);
using var instanceLock = new FileStream(Path.Combine(root, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
builder.Services.AddDataProtection().SetApplicationName("WebPrint").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 52 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 53 * 1024 * 1024);
builder.Services.AddSingleton(new Store(root));
builder.Services.AddSingleton<AdminPasswordDisplay>();
builder.Services.AddSingleton<PrintEngine>();
builder.Services.AddHostedService<PrintWorker>();
builder.Services.AddHostedService<DesktopControl>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => {
    o.Cookie.Name = "WebPrint.Session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o => o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(
    c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
        PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
    })));
var app = builder.Build();
var db = app.Services.GetRequiredService<Store>(); db.Initialize();
var adminPasswordDisplay=app.Services.GetRequiredService<AdminPasswordDisplay>();
if (!db.HasUsers()) {
    var password = Environment.GetEnvironmentVariable("WEBPRINT_ADMIN_PASSWORD") ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    if (password.Length < 6 || password.Length > 200) throw new Exception("WEBPRINT_ADMIN_PASSWORD 须为 6～200 个字符。");
    db.CreateUser("admin", password, "admin");
    adminPasswordDisplay.RememberVerifiedPassword(password);
}
Console.WriteLine("管理员登录名：admin（admin）");
adminPasswordDisplay.WriteTo(Console.Out);
db.Recover();
db.CheckMachine(Environment.MachineName);
app.Use(async (ctx, next) => {
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self'; script-src 'self'; img-src 'self' data:; frame-src 'self' blob:; object-src 'none'; base-uri 'self'; frame-ancestors 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api")) {
        ctx.Response.Headers.CacheControl = "no-store";
        if (ctx.Request.Method is not ("GET" or "HEAD") && ctx.Request.Headers["X-WebPrint"] != "1") {
            ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "缺少请求校验标头。" }); return;
        }
    }
    try { await next(); }
    catch (AppError e) { ctx.Response.StatusCode = e.Status; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (Exception e) {
        app.Logger.LogError(e, "请求失败 {Path}", ctx.Request.Path);
        if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "服务器处理失败，请查看服务器日志。" }); }
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
string User(HttpContext c) => c.User.Identity!.Name!;
bool Admin(HttpContext c) => c.User.IsInRole("admin");
void RequireAdmin(HttpContext c) { if (!Admin(c)) throw new AppError("需要管理员权限。", 403); }
void Own(HttpContext c, string owner) { if (!Admin(c) && User(c) != owner) throw new AppError("无权访问此文件。", 403); }
app.MapPost("/api/login", async (LoginInput input, HttpContext ctx) => {
    var u = db.Verify(input.Username, input.Password);
    if (u == null) return Results.Json(new { error = "用户名或密码错误。" }, statusCode: 401);
    if(u.Name=="admin") adminPasswordDisplay.RememberVerifiedPassword(input.Password);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, u.Name), new Claim(ClaimTypes.Role, u.Role) }, CookieAuthenticationDefaults.AuthenticationScheme)));
    db.Event(u.Name, null, "login", "登录", ctx.Connection.RemoteIpAddress?.ToString());
    return Results.Ok(new { username = u.Name, role = u.Role });
}).RequireRateLimiting("login");
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/me", (HttpContext c) => new { username = User(c), role = Admin(c) ? "admin" : "user" });
api.MapPost("/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.Ok(); });
api.MapPost("/password", (PasswordInput i, HttpContext c) => {
    if (db.Verify(User(c), i.Current) == null) throw new AppError("当前密码错误。");
    adminPasswordDisplay.ChangePassword(User(c), i.Password); db.Event(User(c), null, "password_changed", "修改密码"); return Results.Ok();
});
api.MapGet("/system", async (HttpContext c, PrintEngine engine) => { RequireAdmin(c); return await engine.Diagnostics(); });
api.MapGet("/printers", async (HttpContext c, PrintEngine engine) => { var p = await engine.Printers(); return Admin(c) ? p : p.Where(x => x.Enabled); });
api.MapPost("/printers", async (PrinterInput i, HttpContext c, PrintEngine engine) => {
    RequireAdmin(c); if (!(await engine.Printers()).Any(x => x.Name == i.Name)) throw new AppError("打印机未安装或不可访问。");
    db.EnablePrinter(i.Name, i.Enabled); db.Event(User(c), null, "printer_changed", $"{i.Name}: {i.Enabled}"); return Results.Ok();
});
api.MapGet("/users", (HttpContext c) => { RequireAdmin(c); return db.Users(); });
api.MapPost("/users", (NewUserInput i, HttpContext c) => {
    RequireAdmin(c); db.CreateUser(i.Username, i.Password, i.Role); db.Event(User(c), null, "user_created", i.Username); return Results.Ok();
});
api.MapPost("/documents", async (IFormFile file, HttpContext c, PrintEngine engine) => {
    var doc = await engine.Upload(file, User(c), c.RequestAborted);
    db.Event(User(c), null, "upload", $"{doc.Name} / {doc.Pages} 页", c.Connection.RemoteIpAddress?.ToString()); return Results.Ok(doc);
}).DisableAntiforgery();
api.MapGet("/documents/{id}/pdf", (string id, HttpContext c) => {
    var doc = db.Document(id) ?? throw new AppError("文件不存在。", 404); Own(c, doc.Owner);
    if (!File.Exists(db.PdfPath(id))) throw new AppError("文件内容已到期清理。", 410);
    return Results.File(db.PdfPath(id), "application/pdf", enableRangeProcessing: true);
});
api.MapPost("/jobs", async (JobInput i, HttpContext c, PrintEngine engine) => {
    var doc = db.Document(i.DocumentId) ?? throw new AppError("文件不存在。", 404); Own(c, doc.Owner);
    if (doc.Owner != User(c)) throw new AppError("请上传自己的文件后提交打印。", 403);
    if (!(await engine.Printers()).Any(x => x.Name == i.Printer && x.Enabled)) throw new AppError("打印机未启用或不可访问。");
    if (i.Copies < 1 || i.Copies > 100 || !new[] { "simplex", "duplex", "duplexshort" }.Contains(i.Duplex)) throw new AppError("打印参数无效。");
    if (!File.Exists(db.PdfPath(doc.Id))) throw new AppError("文件已到期，请重新上传。", 410);
    var pages = PrintEngine.ParsePages(i.Pages, doc.Pages);
    return Results.Ok(db.Enqueue(doc, i, pages.Length, User(c), c.Connection.RemoteIpAddress?.ToString()));
});
api.MapGet("/jobs", (HttpContext c) => db.Jobs(Admin(c) ? null : User(c)));
api.MapPost("/jobs/{id}/cancel", (string id, HttpContext c) => {
    var job = db.Job(id) ?? throw new AppError("任务不存在。", 404); Own(c, job.Owner);
    if (!db.Cancel(id, User(c))) throw new AppError("仅可取消尚未开始提交的排队任务。已提交任务请在服务器打印队列中处理。", 409);
    return Results.Ok();
});
api.MapPost("/jobs/{id}/review", (string id, ReviewInput i, HttpContext c) => {
    RequireAdmin(c); if (string.IsNullOrWhiteSpace(i.Note) || i.Note.Length > 1000 || i.ActualPages is < 0) throw new AppError("请填写核查依据，页数须为非负整数或留空。");
    db.Review(id, User(c), i.ActualPages, i.Note); return Results.Ok();
});
api.MapGet("/events", (HttpContext c) => { RequireAdmin(c); return db.Events(); });
api.MapGet("/reports.csv", (HttpContext c) => {
    RequireAdmin(c);
    string Q(object? v) { var s = v?.ToString() ?? ""; if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
    var rows = new List<string> { "任务编号,用户,文件,打印机,文档页数,选中页数,份数,计划页面数,预计纸张数,状态,人工核查页数,数量来源,提交时间,来源IP,备注" };
    rows.AddRange(db.Jobs(null).Select(j => string.Join(",", new object?[] {j.Id,j.Owner,j.Name,j.Printer,j.DocumentPages,j.SelectedPages,j.Copies,j.PlannedPages,j.EstimatedSheets,j.Status,j.ActualPages,j.CountSource,j.Created,j.Ip,j.Note}.Select(Q))));
    return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", rows))).ToArray(), "text/csv; charset=utf-8", "webprint-audit.csv");
});
app.Run();
record LoginInput(string Username, string Password);
record PasswordInput(string Current, string Password);
record NewUserInput(string Username, string Password, string Role);
record PrinterInput(string Name, bool Enabled);
record ReviewInput(int? ActualPages, string Note);
record JobInput(string DocumentId, string Printer, string Pages, int Copies, string Duplex, bool Color, string RequestId);
class AppError(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }
