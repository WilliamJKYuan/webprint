using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

// Explicit local operator feature. Never include these values in an HTTP response or audit event.
class AdminPasswordDisplay(Store db, IDataProtectionProvider provider) {
    readonly IDataProtector protector=provider.CreateProtector("WebPrint.AdminStartupPassword.v1");
    public void RememberVerifiedPassword(string password) => db.RememberAdminPassword(password,protector.Protect(password));
    public void ChangePassword(string name,string password) => db.SetPassword(name,password,name=="admin"?protector.Protect(password):null);
    public void WriteTo(TextWriter output) {
        var value=db.AdminDisplayPassword();
        if(value!=null) {
            try {
                var password=protector.Unprotect(value);
                if(db.Verify("admin",password)!=null) {output.WriteLine($"admin 密码：{password}");return;}
            } catch(Exception e) when(e is CryptographicException or FormatException) { }
        }
        output.WriteLine("admin 密码尚未保存或无法解密；请成功登录 admin 或修改一次密码，后续启动将显示。原密码未被重置。");
    }
}
