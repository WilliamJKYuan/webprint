# 开发、配置与审计说明

日常使用和下载请参见[项目首页](../README.md)，换电脑使用请参见[迁移说明](../MIGRATION.md)。

## 本机启动

需要 Windows 和 .NET 9 SDK（当前开发环境已安装）。在此目录运行：

```powershell
dotnet restore
dotnet build --no-restore
.\start.ps1
```

访问 **http://localhost:5080**。首次启动会创建 `admin` 账户。每次服务启动都会在桌面运行日志（控制台模式为控制台）显示 admin 当前密码。所有账户的新密码须为 6～200 个字符，无字母、数字或符号组合要求。首次部署也可设置环境变量 `WEBPRINT_ADMIN_PASSWORD`；数据库已有账户时此变量不会重置密码。

为支持重复显示，admin 密码另外以 ASP.NET Data Protection 加密保存在本地数据库，解密密钥在 `data/keys/`。登录认证仍使用密码哈希。此副本仅用于本机启动日志，不通过网页 API 或审计事件返回；能够读取数据库和密钥的人可恢复它，能查看运行日志的人也能看到密码。迁移时保留完整数据目录。旧版本账户只存哈希，无法还原原密码；成功登录 admin 或修改一次密码后，后续启动即可显示，升级不会重置原密码。

局域网启动：

```powershell
.\start.ps1 -Lan
```

其他电脑访问 `http://服务器局域网IP:5080`。服务器需允许该端口的入站访问。脚本不会自动修改防火墙。在实际使用真实账户和文件前，应配置受信任的 HTTPS（IIS 反向代理或 Kestrel 证书），HTTP 启动方式用于本机和受控测试。

请只运行一个服务实例。程序对同一数据目录持有进程锁，避免多个实例同时恢复和提交任务。

## 配置打印和 Office 转换

1. 在服务器安装打印机驱动，并用运行 WebPrint 的同一账户确认能够访问目标打印机。共享打印机的 Windows 凭据和驱动访问也由这个账户承担。
2. 从 [LibreOffice 官方网站](https://www.libreoffice.org/download/) 安装 LibreOffice，用于 Word/Excel 转 PDF。
3. 从 [SumatraPDF 官方网站](https://www.sumatrapdfreader.org/download-free-pdf-viewer) 安装或解压便携版，用于无人值守 PDF 打印。
4. 在 `appsettings.json` 配置实际程序路径，然后重启：

```json
"Printing": {
  "Enabled": true,
  "LibreOfficePath": "C:\\Program Files\\LibreOffice\\program\\soffice.exe",
  "SumatraPath": "C:\\Program Files\\SumatraPDF\\SumatraPDF.exe"
}
```

5. 在桌面勾选“实机打印”，管理员登录网页“系统管理”，确认组件就绪并启用要共享的打印机。桌面保存值优先于基础配置，并可在运行中应用；关闭它不取消设备已接收的任务。
6. 创建普通用户账户；先使用一页测试文件验收，再使用真实文档。

实机打印默认关闭。未开启时，任务会明确记录“提交前失败”，不会虚构成功。PDF 和图片预览不依赖上述两个外部程序；Office 文件缺少 LibreOffice 时会明确提示。

可以通过环境变量覆盖配置，例如 `Printing__Enabled`、`Printing__SumatraPath`、`Printing__LibreOfficePath`、`Storage__Path`、`Storage__RetentionDays`。存储和组件相对路径基于程序内容目录解析。组件路径留空时优先检测程序旁的 `tools/`，再检测本机常规安装目录。

## 审计口径

文档页数来自转换后的 PDF。计划打印页面数 = 选中页数 × 份数；双面预计纸张数 = 每份选中页数向上除以 2，再乘份数。这是估算，受驱动实际执行、分页、补打和卡纸等影响。

当前版本的状态含义：

| 状态 | 含义 |
| --- | --- |
| 等待打印 | 尚未开始提交，可在网页取消 |
| 正在发送 | 已由后台领取并准备/执行打印提交 |
| 已发送，待确认 | 打印程序返回成功；尚无设备出纸确认 |
| 结果待确认 | 提交超时、中断或打印程序报错；可能已部分打印 |
| 发送失败 | 未进入打印程序提交阶段 |
| 已取消 | 排队期间已取消 |
| 已核对 | 管理员填写核查依据，页面数可确认或保留未知 |

**当前不自动确认实际出纸数量，不把 SumatraPDF 成功返回当作物理完成。** 实际数量来源显示为“人工核查”，缺省为未知。设备级自动计数、Windows 后台队列任务编号关联、IPP/SNMP 设备反馈属于后续适配工作，需要对具体打印机验证。Windows 打印队列计数本身也可能缺失，见 [微软接口说明](https://learn.microsoft.com/en-us/windows/win32/printdocs/job-info-2)。

任务统计中的计划打印量排除提交前失败与已取消任务；筛选只影响页面展示和统计，CSV 导出全部记录。普通用户只能看自己的任务和文件，管理员可以核查全部任务和文件。

审计范围仅覆盖经过 WebPrint 提交的任务。绕过网页直接打印的任务尚未接管。已提交的任务需要在 Windows 打印队列或设备上取消，网页不会把取消请求直接当作停止出纸。

## 文件和运行账户

- `data/webprint.db`：账户、文件元数据、队列、审计事件。
- `data/documents/<随机标识>/`：原文件、预览 PDF、按选择页码生成的打印 PDF。
- `data/keys/`：登录 Cookie 的持久化密钥；应与数据目录一起限制 NTFS 访问权限。
- `data/server.lock`：单实例进程锁，文件存在本身不表示服务仍运行。

生产部署使用专用低权限账户和数据目录，禁止其他普通账户直接访问。备份时停服务后复制整个数据目录；在线备份应使用 SQLite backup API，不能仅复制正在写入的单个 DB 文件。

Office 转换使用独立进程和独立 LibreOffice 用户配置目录，串行执行并设置超时，宏安全等级设为最高、链接更新设为不更新。**这不等于操作系统级沙箱**；不应将本版直接开放到公网或接受不可信任人群的文件。后续应将转换工作进程迁移到独立的受限账户/虚拟机。PDF 页数解析、图片转换和 SQLite 均使用本地依赖，无外部在线转换 API。

本版的打印 worker 是 ASP.NET 的后台任务，跟随网页服务进程运行；LibreOffice/SumatraPDF 是子进程。尚未注册独立 Windows 服务或开机启动。便携包目前验证的是桌面启动和控制台自托管；IIS 托管及其他服务管理器部署需要单独验证，并确保运行账户可访问打印机。审计事件在应用内只追加，但没有数据库管理员防篡改或外部签名保证。

## 验证

不使用实机出纸的验证：

```powershell
dotnet build --no-restore
dotnet bin/Debug/net9.0/WebPrint.dll --self-test
python tests/http_smoke.py
node --check wwwroot/app.js
```

`--self-test` 验证密码、页码边界、PDF 页数、双面纸张估算、队列去重、恢复、取消、人工核查和清理。HTTP 测试启动隔离服务及数据目录，验证登录、权限、请求校验、文件解析、文件所有权和导出。测试资料保留在 `.test-artifacts/`，不触碰正式数据，不发送任何打印任务。

浏览器测试见 `tests/browser_smoke.py`，使用项目内 Playwright 和本机 Edge；产生桌面/手机截图。测试依赖独立于应用运行依赖。

`python tests/portable_smoke.py` 验证最新 ZIP：移除 PATH 中的 .NET，从其他工作目录运行原生 EXE，再复制到带中文和空格的新目录，确认账户、文件、审计保留，原队列暂停。

`python tests/desktop_smoke.py` 验证最终发布包中的 Windows 桌面界面：实际点击启动和停止，检查自定义端口、端口冲突、正常退出及重启后设置保留。测试使用独立数据，不出纸；截图对初始密码作脱敏处理。

`python tests/password_display_smoke.py` 验证 6 字符密码、重启显示当前 admin 密码、改密后的同步、旧数据库登录后升级、加密副本损坏后的恢复，以及 HTTP 响应不泄露密码。测试日志包含临时凭据，只供本地排查，不能随发布包分发。

实机验收应包含：PDF、Word 和 Excel 的中文字体/分页，一页和奇数页文档，多份双面、黑白/彩色，缺纸/离线/取消及重启期间结果核查。设备计数未验证前，统计必须继续区分计划量与人工核查量。

## 发布

```powershell
dotnet publish -c Release -o publish
```

将发布目录放在服务器，在发布目录运行 `dotnet WebPrint.dll --urls http://0.0.0.0:5080`。服务器需要匹配的 ASP.NET Core Runtime。`data/` 不作为发布静态资源，升级时保留数据和密钥。后台账户如果使用不同工作目录，应显式指定绝对存储路径。

打印命令选项依据 [SumatraPDF 官方说明](https://www.sumatrapdfreader.org/docs/Command-line-arguments)，Office 转换依据 [LibreOffice 官方说明](https://help.libreoffice.org/latest/en-US/text/shared/guide/pdf_params.html)。依赖的许可证和维护由各上游项目提供。
