using System.Reflection;
using System.Runtime.Loader;

namespace WebPrint.Infrastructure;

public static class RuntimeLayout {
    public static string AppRoot { get; } = ResolveRoot();
    static string ResolveRoot() {
        var binaryRoot = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (string.Equals(Path.GetFileName(binaryRoot), "dll", StringComparison.OrdinalIgnoreCase)) {
            var parent = Path.GetDirectoryName(binaryRoot)!;
            if (File.Exists(Path.Combine(parent, "WebPrint.exe"))) return parent;
        }
        return binaryRoot;
    }
    static bool initialized;
    public static void Initialize() {
        if (initialized) return;
        initialized = true;
        AssemblyLoadContext.Default.Resolving += ResolveSatellite;
    }
    static Assembly? ResolveSatellite(AssemblyLoadContext context, AssemblyName name) {
        if (name.Name == null || !name.Name.EndsWith(".resources", StringComparison.Ordinal) || string.IsNullOrEmpty(name.CultureName)) return null;
        if (Path.GetFileName(name.Name) != name.Name || Path.GetFileName(name.CultureName) != name.CultureName) return null;
        var file = Path.Combine(AppRoot, "lang", name.CultureName, name.Name + ".dll");
        return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
    }
}
