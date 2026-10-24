using Microsoft.NET.HostModel.AppHost;
using System.Globalization;

if(args.Length!=2)throw new ArgumentException("Usage: OrganizePublish <publish-folder> <unbound-apphost-template>");
var root=Path.GetFullPath(args[0]);var template=Path.GetFullPath(args[1]);
if(!File.Exists(Path.Combine(root,"WebPrint.dll")) || !File.Exists(template))throw new Exception("Publish output or apphost template missing.");
var dll=Path.Combine(root,"dll");var lang=Path.Combine(root,"lang");Directory.CreateDirectory(dll);Directory.CreateDirectory(lang);
// Only organize the application's publish output, never the internals of third-party tools.
foreach(var dir in Directory.GetDirectories(root)) {
    var name=Path.GetFileName(dir);
    if(name is "dll" or "lang" or "tools")continue;
    if(!Directory.GetFiles(dir,"*.resources.dll",SearchOption.TopDirectoryOnly).Any())continue;
    try{CultureInfo.GetCultureInfo(name);}catch(CultureNotFoundException){continue;}
    var destination=Path.GetFullPath(Path.Combine(lang,name));
    if(!destination.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Language target escaped publish directory.");
    if(Directory.Exists(destination))throw new Exception("Duplicate language directory: "+name);
    Directory.Move(dir,destination);
}
foreach(var file in Directory.GetFiles(root)) {
    var name=Path.GetFileName(file);
    if(name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".pdb",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".deps.json",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".runtimeconfig.json",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".staticwebassets.endpoints.json",StringComparison.OrdinalIgnoreCase)||name=="createdump.exe") {
        File.Move(file,Path.Combine(dll,name));
    }
}
foreach(var name in new[]{"WebPrint","WebPrint.Desktop"}) {
    HostWriter.CreateAppHost(template,Path.Combine(root,name+".exe"),"dll/"+name+".dll",windowsGraphicalUserInterface:name.EndsWith(".Desktop"),assemblyToCopyResourcesFrom:Path.Combine(dll,name+".dll"));
}
Console.WriteLine("Organized native/managed DLLs under dll/ and satellite resources under lang/.");
