using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Drawing;

record PrinterInfo(string Name, string Driver, string Port, string Status, bool Enabled);
class PrintEngine(Store db, IConfiguration config, ILogger<PrintEngine> logger) {
    readonly SemaphoreSlim conversion = new(1, 1);
    string ToolPath(string key, string relative, string installed) {
        var appRoot = config["WebPrint:AppRoot"] ?? Directory.GetCurrentDirectory();
        var configured = config[key];
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured, appRoot);
        var portable = Path.GetFullPath(relative, appRoot);
        return File.Exists(portable) ? portable : installed;
    }
    public string LibreOffice => ToolPath("Printing:LibreOfficePath", "tools/LibreOffice/program/soffice.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"));
    public string Sumatra => ToolPath("Printing:SumatraPath", "tools/SumatraPDF/SumatraPDF.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SumatraPDF", "SumatraPDF.exe"));
    public bool PrintingEnabled => config.GetValue<bool>("Printing:Enabled");
    public string OfficeConverter => db.OfficeConverter() ?? config["Printing:OfficeConverter"] ?? "libreoffice";
    public object ConversionStatus() => new {
        provider = OfficeConverter, libreOffice = File.Exists(LibreOffice),
        microsoftWord = MicrosoftOfficeWorker.IsInstalled("Word.Application"), microsoftExcel = MicrosoftOfficeWorker.IsInstalled("Excel.Application")
    };
    public async Task<object> Diagnostics() {
        string? error = null; var count = 0;
        try { count = (await Printers()).Count; } catch (AppError e) { error = e.Message; }
        return new { windows = OperatingSystem.IsWindows(), libreOffice = File.Exists(LibreOffice), sumatra = File.Exists(Sumatra),
            printingEnabled = PrintingEnabled, printers = count, printerError = error, retentionDays = config.GetValue("Storage:RetentionDays", 30), conversion = ConversionStatus() };
    }
    public async Task<List<PrinterInfo>> Printers() {
        if (!OperatingSystem.IsWindows()) throw new AppError("当前打印后端需要 Windows。");
        const string script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $ErrorActionPreference='Stop'; @(Get-Printer | Select-Object Name,DriverName,PortName,@{Name='Status';Expression={$_.PrinterStatus.ToString()}}) | ConvertTo-Json -Compress";
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        (int Code, string Output, string Error) result;
        try { result = await Run(powershell, new[] { "-NoProfile", "-NonInteractive", "-Command", script }, 20, CancellationToken.None); }
        catch (Exception e) { logger.LogWarning(e,"无法调用 Windows 打印机查询"); throw new AppError("Windows 打印机查询失败或超时，请检查系统组件和打印服务。",503); }
        if (result.Code != 0) { logger.LogWarning("打印机枚举失败: {Error}", result.Error); throw new AppError("无法读取 Windows 打印机，请检查打印后台处理服务和运行账户权限。", 503); }
        var enabled = db.EnabledPrinters(); var list = new List<PrinterInfo>();
        if (string.IsNullOrWhiteSpace(result.Output)) return list;
        using var json = JsonDocument.Parse(result.Output.TrimStart('\uFEFF'));
        if (json.RootElement.ValueKind == JsonValueKind.Null) return list;
        var items = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : new[] {json.RootElement};
        foreach (var x in items) {
            string Get(string k) => x.GetProperty(k).GetString() ?? "";
            var name=Get("Name");var port=Get("PortName");var driver=Get("DriverName");
            // 文件输出和交互式虚拟打印机无法用于无人值守打印。
            if (new[] {"PDF", "XPS", "OneNote", "Fax"}.Any(s=>name.Contains(s,StringComparison.OrdinalIgnoreCase)) || port.Contains("PROMPT",StringComparison.OrdinalIgnoreCase) || port=="FILE:") continue;
            list.Add(new(name,driver,port,Get("Status"),enabled.Contains(name)));
        }
        return list;
    }
    public static int[] ParsePages(string? text, int total) {
        if (total < 1 || total > 2000) throw new AppError("文档须为 1～2000 页。");
        if (string.IsNullOrWhiteSpace(text)) return Enumerable.Range(1,total).ToArray();
        if(text.Length>8000)throw new AppError("页码范围过长。");
        var pages=new SortedSet<int>();
        foreach(var token in text.Replace('，',',').Split(',')) {
            var pair=token.Trim().Split('-');
            if(pair.Length is < 1 or > 2 || !int.TryParse(pair[0],out var start))throw new AppError("页码格式应为 1-3,5。");
            var end=start;if(pair.Length==2&&!int.TryParse(pair[1],out end))throw new AppError("页码范围无效。");
            if(start<1||end<start||end>total)throw new AppError($"页码须在 1～{total} 之间，且起始页不大于结束页。");
            for(var n=start;n<=end;n++)pages.Add(n);
        }
        return pages.ToArray();
    }
    public async Task<DocumentInfo> Upload(IFormFile file,string owner,CancellationToken ct) {
        if(file.Length<1||file.Length>50*1024*1024)throw new AppError("文件大小须为 1 字节～50 MB。");
        var name=Path.GetFileName(file.FileName.Replace('\\','/'));
        if(name.Length>200||string.IsNullOrWhiteSpace(name))throw new AppError("文件名无效或过长。");
        var ext=Path.GetExtension(name).ToLowerInvariant();
        if(!new[]{".pdf",".doc",".docx",".xls",".xlsx",".jpg",".jpeg",".png"}.Contains(ext))throw new AppError("支持 PDF、Word、Excel、JPG 和 PNG 文件。");
        var converter = OfficeConverter; // One provider per upload, including time waiting for conversion.
        var id=Guid.NewGuid().ToString("N");var dir=db.DocumentDir(id);Directory.CreateDirectory(dir);
        var source=Path.Combine(dir,"source"+ext);var pdf=db.PdfPath(id);
        try {
            await using(var stream=new FileStream(source,FileMode.CreateNew))await file.CopyToAsync(stream,ct);
            await conversion.WaitAsync(ct);
            try {
                if(ext==".pdf") {
                    using var src=PdfReader.Open(source,PdfDocumentOpenMode.Import);
                    if(src.SecuritySettings.IsEncrypted || !src.SecuritySettings.PermitPrint)throw new AppError("请上传未加密且允许打印的 PDF。");
                    if(src.PageCount<1||src.PageCount>2000)throw new AppError("文档须为 1～2000 页。");
                    using var clean=new PdfDocument();foreach(var p in src.Pages)clean.AddPage(p);clean.Save(pdf);
                } else if(ext is ".jpg" or ".jpeg" or ".png") {
                    using var image=XImage.FromFile(source);
                    if((long)image.PixelWidth*image.PixelHeight>80_000_000)throw new AppError("图片像素过大，最多 8000 万像素。");
                    using var doc=new PdfDocument();var page=doc.AddPage();page.Size=PdfSharp.PageSize.A4;
                    using(var g=XGraphics.FromPdfPage(page)) {
                        var scale=Math.Min((page.Width.Point-40)/image.PixelWidth,(page.Height.Point-40)/image.PixelHeight);
                        var w=image.PixelWidth*scale;var h=image.PixelHeight*scale;
                        g.DrawImage(image,(page.Width.Point-w)/2,(page.Height.Point-h)/2,w,h);
                    }
                    doc.Save(pdf);
                } else {
                    var converted=Path.Combine(dir,"source.pdf");
                    if (converter == "microsoft-office") {
                        await MicrosoftOfficeConversion.Convert(source, converted, logger, ct);
                    } else if (converter == "libreoffice") {
                        if(!File.Exists(LibreOffice))throw new AppError("服务器尚未配置 LibreOffice，暂不能转换 Word/Excel。请先上传 PDF，或由管理员完成配置。",503);
                        var profile=Path.Combine(dir,"lo-profile");Directory.CreateDirectory(Path.Combine(profile,"user"));
                        await File.WriteAllTextAsync(Path.Combine(profile,"user","registrymodifications.xcu"), """
                            <?xml version="1.0" encoding="UTF-8"?>
                            <oor:items xmlns:oor="http://openoffice.org/2001/registry">
                            <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>
                            <item oor:path="/org.openoffice.Office.Calc/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>2</value></prop></item>
                            <item oor:path="/org.openoffice.Office.Writer/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>2</value></prop></item>
                            </oor:items>
                            """,ct);
                        var result=await Run(LibreOffice,new[]{"-env:UserInstallation="+new Uri(profile+Path.DirectorySeparatorChar).AbsoluteUri,"--headless","--nologo","--nodefault","--nofirststartwizard","--convert-to","pdf","--outdir",dir,source},90,ct);
                        if(result.Code!=0||!File.Exists(converted))throw new AppError("Office 转换失败；文件可能损坏、加密或不受支持。可先另存为 PDF 后上传。");
                        Directory.Delete(profile,true);
                    } else throw new AppError("文档转换方式无效，请管理员在系统管理中重新选择。", 503);
                    using(var src=PdfReader.Open(converted,PdfDocumentOpenMode.Import)) {
                        if(src.PageCount<1||src.PageCount>2000)throw new AppError("文档须为 1～2000 页。");
                        using var clean=new PdfDocument();foreach(var p in src.Pages)clean.AddPage(p);clean.Save(pdf);
                    }
                    File.Delete(converted);
                }
                int count;using(var parsed=PdfReader.Open(pdf,PdfDocumentOpenMode.Import))count=parsed.PageCount;
                using var input=File.OpenRead(source);var hash=Convert.ToHexString(await SHA256.HashDataAsync(input,ct));
                var info=new DocumentInfo(id,owner,name,ext,file.Length,hash,count,DateTime.UtcNow.ToString("O"));db.AddDocument(info);return info;
            } finally {conversion.Release();}
        } catch(Exception e) {
            if(Directory.Exists(dir))Directory.Delete(dir,true);
            if(e is AppError or OperationCanceledException)throw;
            logger.LogWarning(e,"文件转换失败 {Id}",id);throw new AppError("文件无法解析，请检查格式、加密状态或文件是否损坏。");
        }
    }
    public async Task Dispatch(JobInfo j,CancellationToken ct) {
        var submitted=false;
        try {
            if(!PrintingEnabled)throw new AppError("服务器尚未开启实机打印，请由管理员配置 Printing:Enabled。");
            if(!File.Exists(Sumatra))throw new AppError("服务器尚未配置 SumatraPDF 打印程序。");
            if(!(await Printers()).Any(p=>p.Name==j.Printer&&p.Enabled))throw new AppError("打印机已被禁用、删除或无法访问。");
            var selected=ParsePages(j.Pages,j.DocumentPages);var output=Path.Combine(db.DocumentDir(j.DocumentId),j.Id+".pdf");
            using(var src=PdfReader.Open(db.PdfPath(j.DocumentId),PdfDocumentOpenMode.Import)) {
                using var doc=new PdfDocument();foreach(var page in selected)doc.AddPage(src.Pages[page-1]);doc.Save(output);
            }
            ct.ThrowIfCancellationRequested();
            if(!PrintingEnabled)throw new AppError("实机打印已被关闭，任务未发送到打印程序。");
            // 此后任何异常都可能发生在部分页面已提交之后，不能自动重试。
            submitted=true;
            var result=await Run(Sumatra,new[]{"-new-window","-silent","-print-to",j.Printer,"-print-settings",$"{j.Copies}x,{j.Duplex},{(j.Color?"color":"monochrome")},paper=A4,fit",output},120,ct);
            if(result.Code!=0) {db.Status(j.Id,"unknown",$"打印程序退出码 {result.Code}，可能已部分提交，请核查 Windows 队列和设备；不会自动重打。");return;}
            db.Status(j.Id,"submitted_unverified","打印程序已返回成功；实际出纸页数尚未由设备确认。");
        } catch(Exception e) {
            logger.LogWarning(e,"打印任务 {Id} 处理异常",j.Id);
            db.Status(j.Id,submitted?"unknown":"failed",submitted?"提交过程被中断或超时，可能已经打印；请核查，系统不会自动重打。":e is AppError?e.Message:"提交前处理失败，请检查服务器日志。");
        }
    }
    static async Task<(int Code,string Output,string Error)> Run(string file,IEnumerable<string> args,int seconds,CancellationToken ct) {
        var info=new ProcessStartInfo(file){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        using var process=new Process{StartInfo=info};process.Start();
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try {await process.WaitForExitAsync(timeout.Token);}
        catch {try {process.Kill(entireProcessTree:true);} catch(InvalidOperationException) {} throw;}
        return(process.ExitCode,await stdout,await stderr);
    }
}
class PrintWorker(Store db,PrintEngine engine,IConfiguration config,ILogger<PrintWorker> logger):BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var purge=DateTime.MinValue;
        while(!stoppingToken.IsCancellationRequested) {
            try {
                if(DateTime.UtcNow>purge) {db.PurgeFiles(Math.Max(1,config.GetValue("Storage:RetentionDays",30)));purge=DateTime.UtcNow.AddHours(1);}
                var job=db.Claim();if(job!=null)await engine.Dispatch(job,stoppingToken);
            } catch(Exception e) {logger.LogError(e,"打印后台任务异常");}
            try {await Task.Delay(1500,stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
}
