using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using PdfSharp.Pdf;

static class SelfTests {
    public static async Task Run() {
        var root=Path.GetFullPath(Path.Combine(".test-artifacts",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        var db=new Store(root);db.Initialize();var checks=0;
        void Check(bool value,string message) {if(!value)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);checks++;}
        void Reject(Action action,string message) {try{action();}catch(AppError){Check(true,message);return;}throw new Exception("FAIL: "+message);}
        db.CreateUser("tester","A-long-test-password","user");Check(db.Verify("tester","A-long-test-password")?.Role=="user","正确密码登录");
        Check(db.Verify("tester","wrong")==null,"错误密码拒绝");Reject(()=>db.CreateUser("tester","A-long-test-password","user"),"拒绝重复账户");
        Reject(()=>db.SetPassword("tester","short"),"拒绝弱密码");db.SetPassword("tester","A-new-test-password");Check(db.Verify("tester","A-long-test-password")==null&&db.Verify("tester","A-new-test-password")!=null,"修改密码生效");
        db.CreateUser("sixchar","abcdef","user");Check(db.Verify("sixchar","abcdef")!=null,"允许创建六字符密码账户");
        db.SetPassword("sixchar","123456");Check(db.Verify("sixchar","123456")!=null,"允许修改为六字符密码");
        Check(PrintEngine.ParsePages("1-3,2,5",5).SequenceEqual(new[]{1,2,3,5}),"范围解析去重排序");
        Check(PrintEngine.ParsePages("",5).Length==5,"空范围表示全部");
        foreach(var s in new[]{"0","6","3-1","1,","1-999999999","1;rm","1-2-3"})Reject(()=>PrintEngine.ParsePages(s,5),"拒绝非法范围 "+s);
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Printing:Enabled","false"}}).Build();
        var engine=new PrintEngine(db,cfg,NullLogger<PrintEngine>.Instance);
        var fixture=Path.Combine(root,"fixture.pdf");using(var pdf=new PdfDocument()){for(var n=0;n<5;n++)pdf.AddPage();pdf.Save(fixture);}
        DocumentInfo doc;await using(var stream=File.OpenRead(fixture))doc=await engine.Upload(new FormFile(stream,0,stream.Length,"file","fixture.pdf"),"tester",CancellationToken.None);
        Check(doc.Pages==5&&doc.Hash.Length==64&&File.Exists(db.PdfPath(doc.Id)),"PDF 上传预览页数与哈希");
        var input=new JobInput(doc.Id,"Test printer","1-3",3,"duplex",false,Guid.NewGuid().ToString());
        var j=db.Enqueue(doc,input,3,"tester","127.0.0.1");Check(j.PlannedPages==9&&j.EstimatedSheets==6,"双面奇数页按份计算纸张");
        var duplicate=db.Enqueue(doc,input,3,"tester","127.0.0.1");Check(duplicate.Id==j.Id&&db.Jobs(null).Count==1,"重复提交只有一个任务");
        Reject(()=>db.Enqueue(doc,input with{Copies=2},3,"tester",null),"同一提交标识不能更改打印参数");
        Check(db.Jobs("other").Count==0,"按用户过滤任务");
        Check(db.Claim()?.Id==j.Id&&db.Claim()==null,"队列原子领取");Check(!db.Cancel(j.Id,"tester"),"提交中任务不能伪取消");
        db.Recover();Check(db.Job(j.Id)?.Status=="unknown","中断恢复标记未知且不重打");
        db.Review(j.Id,"admin",7,"设备核查测试");Check(db.Job(j.Id)?.ActualPages==7&&db.Job(j.Id)?.CountSource=="manual","人工核查注明来源");
        var j2=db.Enqueue(doc,input with{RequestId=Guid.NewGuid().ToString()},3,"tester",null);Check(db.Cancel(j2.Id,"tester")&&db.Job(j2.Id)?.Status=="cancelled","排队任务取消");
        var j3=db.Enqueue(doc,input with{RequestId=Guid.NewGuid().ToString()},3,"tester",null);await engine.Dispatch(db.Claim()!,CancellationToken.None);
        Check(db.Job(j3.Id)?.Status=="failed"&&db.Job(j3.Id)?.ActualPages==null,"未启用实机时明确失败且无虚假页数");
        Check(db.Events().Count>=8,"关键操作产生审计事件");
        Directory.SetLastWriteTimeUtc(db.DocumentDir(doc.Id),DateTime.UtcNow.AddDays(-40));db.PurgeFiles(30);
        Check(!File.Exists(db.PdfPath(doc.Id))&&db.Document(doc.Id)!=null&&db.Jobs(null).Count==3,"清理内容保留任务与审计");
        db.CheckMachine("old-host");db.EnablePrinter("Test printer",true);
        var moving=db.Enqueue(doc,input with{RequestId=Guid.NewGuid().ToString()},3,"tester",null);
        db.CheckMachine("old-host");Check(db.Job(moving.Id)?.Status=="queued"&&db.EnabledPrinters().Contains("Test printer"),"同一主机重启保留排队任务与共享设置");
        db.CheckMachine("new-host");Check(db.Job(moving.Id)?.Status=="unknown"&&db.EnabledPrinters().Count==0,"迁移到新主机暂停原排队任务并停用共享打印机");
        var portableCfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"WebPrint:AppRoot",root},{"Printing:SumatraPath","tools/SumatraPDF/SumatraPDF.exe"}}).Build();
        var portableEngine=new PrintEngine(db,portableCfg,NullLogger<PrintEngine>.Instance);
        Check(portableEngine.Sumatra==Path.GetFullPath(Path.Combine(root,"tools","SumatraPDF","SumatraPDF.exe")),"组件相对路径基于程序目录解析");
        Check(engine.OfficeConverter == "libreoffice", "旧配置默认继续使用 LibreOffice");
        db.SetOfficeConverter("microsoft-office", "admin", null);
        Check(new Store(root).OfficeConverter() == "microsoft-office", "转换设置持久化到数据目录");
        Reject(() => db.SetOfficeConverter("invalid", "admin", null), "拒绝不支持的转换软件");
        Check(engine.OfficeConverter == "microsoft-office", "无效设置不会改变当前转换软件");
        db.CheckMachine("another-host");
        Check(db.OfficeConverter() == "microsoft-office", "迁移保留文档转换软件选择");
        Console.WriteLine($"{checks} checks passed. Test artifacts: {root}");
    }
}
