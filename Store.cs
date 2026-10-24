using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

record Account(string Name, string Role);
record DocumentInfo(string Id, string Owner, string Name, string Extension, long Size, string Hash, int Pages, string Created);
record JobInfo(string Id, string Owner, string DocumentId, string Name, string Printer, string Pages, int DocumentPages,
    int SelectedPages, int Copies, string Duplex, bool Color, int PlannedPages, int EstimatedSheets, string Status,
    int? ActualPages, string CountSource, string Created, string? Ip, string Note, string RequestId);
class Store(string root) {
    public string Root { get; } = root;
    public string DocumentDir(string id) => Path.Combine(Root, "documents", Guid.Parse(id).ToString("N"));
    public string PdfPath(string id) => Path.Combine(DocumentDir(id), "preview.pdf");
    SqliteConnection Open() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "webprint.db"), ForeignKeys = true }.ToString()); c.Open(); return c; }
    static SqliteCommand Cmd(SqliteConnection c, string sql, params object?[] args) {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        for (var n = 0; n < args.Length; n++) cmd.Parameters.AddWithValue("$" + n, args[n] ?? DBNull.Value); return cmd;
    }
    public void Initialize() {
        using var c = Open(); using var cmd = Cmd(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS users(name TEXT PRIMARY KEY, role TEXT NOT NULL, salt BLOB NOT NULL, hash BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS documents(id TEXT PRIMARY KEY, owner TEXT NOT NULL, name TEXT NOT NULL, extension TEXT NOT NULL, size INTEGER NOT NULL, hash TEXT NOT NULL, pages INTEGER NOT NULL, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS printers(name TEXT PRIMARY KEY, enabled INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, owner TEXT NOT NULL, document_id TEXT NOT NULL REFERENCES documents(id), name TEXT NOT NULL, printer TEXT NOT NULL, pages TEXT NOT NULL, document_pages INTEGER NOT NULL, selected_pages INTEGER NOT NULL, copies INTEGER NOT NULL, duplex TEXT NOT NULL, color INTEGER NOT NULL, planned_pages INTEGER NOT NULL, estimated_sheets INTEGER NOT NULL, status TEXT NOT NULL, actual_pages INTEGER, count_source TEXT NOT NULL, created TEXT NOT NULL, ip TEXT, note TEXT NOT NULL, request_id TEXT NOT NULL, UNIQUE(owner,request_id));
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, actor TEXT NOT NULL, job_id TEXT, action TEXT NOT NULL, detail TEXT NOT NULL, ip TEXT, created TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS jobs_status ON jobs(status,created);
            CREATE TABLE IF NOT EXISTS settings(name TEXT PRIMARY KEY, value TEXT NOT NULL);
            """); cmd.ExecuteNonQuery();
    }
    public bool HasUsers() { using var c = Open(); using var q = Cmd(c, "SELECT count(*) FROM users"); return (long)q.ExecuteScalar()! > 0; }
    public string? OfficeConverter() { using var c = Open(); using var q = Cmd(c, "SELECT value FROM settings WHERE name='office_converter'"); return q.ExecuteScalar() as string; }
    public void SetOfficeConverter(string provider, string actor, string? ip) {
        if (provider is not ("libreoffice" or "microsoft-office")) throw new AppError("请选择 LibreOffice 或 Microsoft Office。");
        using var c = Open(); using var tx = c.BeginTransaction();
        using var q = Cmd(c, "INSERT INTO settings(name,value) VALUES('office_converter',$0) ON CONFLICT(name) DO UPDATE SET value=$0", provider);
        q.Transaction = tx; q.ExecuteNonQuery();
        WriteEvent(c, tx, actor, null, "converter_changed", provider == "libreoffice" ? "文档转换改用 LibreOffice" : "文档转换改用 Microsoft Office", ip);
        tx.Commit();
    }
    public void CreateUser(string name, string password, string role) {
        if (!Regex.IsMatch(name ?? "", "^[a-zA-Z0-9_.-]{3,40}$") || role is not ("admin" or "user")) throw new AppError("用户名须为 3～40 位字母、数字或 _.-，角色须有效。");
        CheckPassword(password); var salt = RandomNumberGenerator.GetBytes(32); var hash = Hash(password, salt);
        using var c = Open(); using var q = Cmd(c, "INSERT OR IGNORE INTO users VALUES($0,$1,$2,$3)", name, role, salt, hash);
        if (q.ExecuteNonQuery() == 0) throw new AppError("用户名已存在。", 409);
    }
    static byte[] Hash(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
    static void CheckPassword(string password) { if (password == null || password.Length < 6 || password.Length > 200) throw new AppError("密码须为 6～200 个字符。"); }
    public Account? Verify(string name, string password) {
        if (password == null || password.Length > 200) return null;
        using var c = Open(); using var q = Cmd(c, "SELECT name,role,salt,hash FROM users WHERE name=$0", name); using var r = q.ExecuteReader();
        if (!r.Read()) { Hash(password, new byte[32]); return null; }
        return CryptographicOperations.FixedTimeEquals(Hash(password, (byte[])r[2]), (byte[])r[3]) ? new(r.GetString(0), r.GetString(1)) : null;
    }
    public void SetPassword(string name, string password, string? adminDisplayPassword = null) {
        CheckPassword(password); var salt = RandomNumberGenerator.GetBytes(32);
        using var c = Open(); using var tx = c.BeginTransaction();
        using var q = Cmd(c, "UPDATE users SET salt=$1,hash=$2 WHERE name=$0", name, salt, Hash(password, salt)); q.Transaction = tx;
        if(q.ExecuteNonQuery()==0) throw new AppError("账户不存在。",404);
        if(name=="admin") {
            using var clear=Cmd(c,"DELETE FROM settings WHERE name='admin_startup_password'");clear.Transaction=tx;clear.ExecuteNonQuery();
            if(adminDisplayPassword!=null) {using var save=Cmd(c,"INSERT INTO settings VALUES('admin_startup_password',$0)",adminDisplayPassword);save.Transaction=tx;save.ExecuteNonQuery();}
        }
        tx.Commit();
    }
    public string? AdminDisplayPassword() {using var c=Open();using var q=Cmd(c,"SELECT value FROM settings WHERE name='admin_startup_password'");return q.ExecuteScalar() as string;}
    public void RememberAdminPassword(string password,string protectedPassword) {
        // Check the password again inside the write transaction to avoid saving a stale login.
        using var c=Open();using var tx=c.BeginTransaction();using var q=Cmd(c,"SELECT salt,hash FROM users WHERE name='admin'");q.Transaction=tx;
        using(var r=q.ExecuteReader()) {if(!r.Read() || !CryptographicOperations.FixedTimeEquals(Hash(password,(byte[])r[0]),(byte[])r[1]))return;}
        using var save=Cmd(c,"INSERT INTO settings VALUES('admin_startup_password',$0) ON CONFLICT(name) DO UPDATE SET value=$0",protectedPassword);save.Transaction=tx;save.ExecuteNonQuery();tx.Commit();
    }
    public List<Account> Users() { using var c = Open(); using var q = Cmd(c, "SELECT name,role FROM users ORDER BY name"); using var r = q.ExecuteReader(); var list = new List<Account>(); while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1))); return list; }
    public void AddDocument(DocumentInfo d) { using var c = Open(); using var q = Cmd(c, "INSERT INTO documents VALUES($0,$1,$2,$3,$4,$5,$6,$7)", d.Id,d.Owner,d.Name,d.Extension,d.Size,d.Hash,d.Pages,d.Created); q.ExecuteNonQuery(); }
    public DocumentInfo? Document(string id) {
        using var c = Open(); using var q = Cmd(c, "SELECT * FROM documents WHERE id=$0", id); using var r = q.ExecuteReader();
        return r.Read() ? new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetString(5),r.GetInt32(6),r.GetString(7)) : null;
    }
    public HashSet<string> EnabledPrinters() { using var c = Open(); using var q = Cmd(c, "SELECT name FROM printers WHERE enabled=1"); using var r = q.ExecuteReader(); var s = new HashSet<string>(); while (r.Read()) s.Add(r.GetString(0)); return s; }
    public void EnablePrinter(string name, bool enabled) { using var c = Open(); using var q = Cmd(c, "INSERT INTO printers VALUES($0,$1) ON CONFLICT(name) DO UPDATE SET enabled=$1",name,enabled); q.ExecuteNonQuery(); }
    public JobInfo Enqueue(DocumentInfo d, JobInput i, int count, string owner, string? ip) {
        if (!Guid.TryParse(i.RequestId, out _)) throw new AppError("无效的提交标识。");
        var id = Guid.NewGuid().ToString("N"); var now = DateTime.UtcNow.ToString("O");
        using var c = Open(); using var tx = c.BeginTransaction();
        using var q = Cmd(c, "INSERT OR IGNORE INTO jobs VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,'queued',NULL,'unconfirmed',$13,$14,'',$15)", id,owner,d.Id,d.Name,i.Printer,i.Pages ?? "",d.Pages,count,i.Copies,i.Duplex,i.Color,count*i.Copies,(i.Duplex=="simplex" ? count : (count+1)/2)*i.Copies,now,ip,i.RequestId);
        q.Transaction = tx; if (q.ExecuteNonQuery() > 0) WriteEvent(c,tx,owner,id,"queued","加入打印队列",ip);
        using var find = Cmd(c, "SELECT * FROM jobs WHERE owner=$0 AND request_id=$1",owner,i.RequestId); find.Transaction=tx;
        JobInfo j; using (var r = find.ExecuteReader()) { r.Read(); j = ReadJob(r); }
        if(j.DocumentId!=d.Id || j.Printer!=i.Printer || j.Pages!=(i.Pages??"") || j.Copies!=i.Copies || j.Duplex!=i.Duplex || j.Color!=i.Color)
            throw new AppError("该提交标识已用于不同参数的任务，请核查已有记录后重新上传。",409);
        tx.Commit(); return j;
    }
    static JobInfo ReadJob(SqliteDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),r.GetString(9),r.GetBoolean(10),r.GetInt32(11),r.GetInt32(12),r.GetString(13),r.IsDBNull(14)?null:r.GetInt32(14),r.GetString(15),r.GetString(16),r.IsDBNull(17)?null:r.GetString(17),r.GetString(18),r.GetString(19));
    public List<JobInfo> Jobs(string? owner) { using var c = Open(); using var q = Cmd(c, "SELECT * FROM jobs WHERE ($0 IS NULL OR owner=$0) ORDER BY created DESC",owner); using var r = q.ExecuteReader(); var l = new List<JobInfo>(); while(r.Read()) l.Add(ReadJob(r)); return l; }
    public JobInfo? Job(string id) { using var c = Open(); using var q = Cmd(c,"SELECT * FROM jobs WHERE id=$0",id); using var r=q.ExecuteReader(); return r.Read()?ReadJob(r):null; }
    public JobInfo? Claim() {
        using var c = Open(); using var tx=c.BeginTransaction();
        using var q=Cmd(c,"UPDATE jobs SET status='dispatching' WHERE id=(SELECT id FROM jobs WHERE status='queued' ORDER BY created LIMIT 1) AND status='queued' RETURNING *"); q.Transaction=tx;
        JobInfo? job; using(var r=q.ExecuteReader()) job=r.Read()?ReadJob(r):null;
        if(job!=null) WriteEvent(c,tx,"system",job.Id,"dispatching","开始准备提交",null); tx.Commit(); return job;
    }
    public void Status(string id,string status,string note) {
        using var c=Open(); using var tx=c.BeginTransaction(); using var q=Cmd(c,"UPDATE jobs SET status=$1,note=$2 WHERE id=$0",id,status,note); q.Transaction=tx;q.ExecuteNonQuery();
        WriteEvent(c,tx,"system",id,status,note,null);tx.Commit();
    }
    public bool Cancel(string id,string actor) {
        using var c=Open();using var tx=c.BeginTransaction();using var q=Cmd(c,"UPDATE jobs SET status='cancelled' WHERE id=$0 AND status='queued'",id);q.Transaction=tx;
        var ok=q.ExecuteNonQuery()>0;if(ok)WriteEvent(c,tx,actor,id,"cancelled","取消排队任务",null);tx.Commit();return ok;
    }
    public void Review(string id,string actor,int? pages,string note) {
        using var c=Open();using var tx=c.BeginTransaction();using var q=Cmd(c,"UPDATE jobs SET actual_pages=$1,count_source='manual',note=$2,status='reviewed' WHERE id=$0 AND status IN ('submitted_unverified','unknown','failed','reviewed')",id,pages,note);q.Transaction=tx;
        if(q.ExecuteNonQuery()==0)throw new AppError("任务不存在或仍在排队、提交中。",409);
        WriteEvent(c,tx,actor,id,"reviewed",$"人工核查页面数：{pages?.ToString()??"未知"}；依据：{note}",null);tx.Commit();
    }
    public void Recover() { foreach(var j in Jobs(null).Where(j=>j.Status=="dispatching")) Status(j.Id,"unknown","服务在提交期间退出，可能已经打印，请核查；系统不会自动重打。"); }
    public void CheckMachine(string machine) {
        using var c=Open();using var tx=c.BeginTransaction();
        using var q=Cmd(c,"SELECT value FROM settings WHERE name='machine'");q.Transaction=tx;
        var previous=q.ExecuteScalar() as string;
        // Existing databases without a marker also require explicit printer re-enablement.
        if(previous!=machine) {
            using var pause=Cmd(c,"UPDATE jobs SET status='unknown',note='数据首次部署或已迁移到新电脑；原排队任务暂停，请核查后重新提交。' WHERE status='queued'; UPDATE printers SET enabled=0;");pause.Transaction=tx;pause.ExecuteNonQuery();
            WriteEvent(c,tx,"system",null,"machine_changed","首次部署或迁移：停用共享打印机、暂停原排队任务，保留历史记录。",null);
        }
        using var save=Cmd(c,"INSERT INTO settings VALUES('machine',$0) ON CONFLICT(name) DO UPDATE SET value=$0",machine);save.Transaction=tx;save.ExecuteNonQuery();tx.Commit();
    }
    static void WriteEvent(SqliteConnection c,SqliteTransaction? tx,string actor,string? id,string action,string detail,string? ip) {
        using var q=Cmd(c,"INSERT INTO events(actor,job_id,action,detail,ip,created) VALUES($0,$1,$2,$3,$4,$5)",actor,id,action,detail,ip,DateTime.UtcNow.ToString("O"));q.Transaction=tx;q.ExecuteNonQuery();
    }
    public void Event(string actor,string? id,string action,string detail,string? ip=null) {using var c=Open();WriteEvent(c,null,actor,id,action,detail,ip);}
    public List<object> Events() {
        using var c=Open();using var q=Cmd(c,"SELECT id,actor,job_id,action,detail,ip,created FROM events ORDER BY id DESC LIMIT 1000");using var r=q.ExecuteReader();var l=new List<object>();
        while(r.Read())l.Add(new {id=r.GetInt64(0),actor=r.GetString(1),jobId=r.IsDBNull(2)?null:r.GetString(2),action=r.GetString(3),detail=r.GetString(4),ip=r.IsDBNull(5)?null:r.GetString(5),created=r.GetString(6)});return l;
    }
    public void PurgeFiles(int days) {
        var dir=Path.Combine(Root,"documents");if(!Directory.Exists(dir))return;
        var active=Jobs(null).Where(j=>j.Status is "queued" or "dispatching").Select(j=>j.DocumentId).ToHashSet();
        foreach(var path in Directory.EnumerateDirectories(dir)) {
            if(!Guid.TryParseExact(Path.GetFileName(path),"N",out _) || active.Contains(Path.GetFileName(path)))continue;
            if(Directory.GetLastWriteTimeUtc(path)<DateTime.UtcNow.AddDays(-days)) { Directory.Delete(path,true);Event("system",null,"content_purged",Path.GetFileName(path)); }
        }
    }
}
