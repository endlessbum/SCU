namespace SCU.Infrastructure.Updates;

using Microsoft.Win32;

// П. 13 аудита: определение установленных рантаймов — чистые проверки
// реестра/файловой системы, вынесены из RuntimeInstaller.
internal static class RuntimeDetector
{
    internal static bool IsInstalled(string id) => id switch
    {
        "directx" => IsDirectXLegacyRuntimeInstalled(),
        "vc-x64" => IsVcRuntimeInstalled("x64"),
        "vc-x86" => IsVcRuntimeInstalled("x86"),
        // Старые семейства VC++ определяем по CRT-DLL: установщики 2008–2013
        // не дают единого ключа реестра, а DLL — фактический признак рантайма.
        "vc2013-x64" => IsVcLegacyRuntimeInstalled("x64", "msvcr120.dll"),
        "vc2013-x86" => IsVcLegacyRuntimeInstalled("x86", "msvcr120.dll"),
        "vc2012-x64" => IsVcLegacyRuntimeInstalled("x64", "msvcr110.dll"),
        "vc2012-x86" => IsVcLegacyRuntimeInstalled("x86", "msvcr110.dll"),
        "vc2008-x64" => IsVcLegacyRuntimeInstalled("x64", "msvcr90.dll"),
        "vc2008-x86" => IsVcLegacyRuntimeInstalled("x86", "msvcr90.dll"),
        "netfx35" => IsNetFx35Installed(),
        "netfx481" => IsNetFx481Installed(),
        _ when id.StartsWith("dotnet-") =>
            IsDotnetDesktopInstalled(int.Parse(id["dotnet-".Length..])),
        _ => false
    };

    // CRT-DLL старых VC++: System32 — 64-бит, SysWOW64 — 32-бит (на 64-битной ОС).
    internal static bool IsVcLegacyRuntimeInstalled(string arch, string marker)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var directory = arch == "x64"
            ? Path.Combine(windows, "System32")
            : Path.Combine(windows, Environment.Is64BitOperatingSystem ? "SysWOW64" : "System32");
        return File.Exists(Path.Combine(directory, marker));
    }

    // .NET Framework 3.5: Install=1 в NDP\v3.5 — включённый компонент Windows.
    internal static bool IsNetFx35Installed()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5");
        return key?.GetValue("Install") is int installed && installed == 1;
    }

    // .NET Framework 4.x: Release в NDP\v4\Full. 533320 — первый релиз 4.8.1
    // (4.8 = 528040): считаем установленной только последнюю ветку 4.x.
    internal static bool IsNetFx481Installed()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
        return key?.GetValue("Release") is int release && release >= 533320;
    }

    // dxwebsetup не регистрируется как обычный ARP/MSI-продукт, поэтому состояние
    // определяем по DLL legacy DirectX, которые устанавливает End-User Runtime.
    internal static bool IsDirectXLegacyRuntimeInstalled()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var directories = Environment.Is64BitOperatingSystem
            ? new[] { Path.Combine(windows, "System32"), Path.Combine(windows, "SysWOW64") }
            : new[] { Path.Combine(windows, "System32") };

        var markers = new[] { "d3dx9_43.dll", "xinput1_3.dll", "xaudio2_7.dll" };
        // Один случайный DLL-маркер не доказывает установку End-User Runtime.
        // Требуем полный набор в одном из целевых каталогов (x64 или x86).
        return directories.Any(directory => markers.All(marker => File.Exists(Path.Combine(directory, marker))));
    }

    internal static bool IsVcRuntimeInstalled(string arch)
    {
        // Ключ создают установщики VC++ 2015–2022; WOW6432Node — для 32-бит рантайма на 64-бит ОС.
        var subKey = arch == "x64"
            ? @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
            : @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86";
        using var key = Registry.LocalMachine.OpenSubKey(subKey);
        return key?.GetValue("Installed") is int installed && installed != 0;
    }

    internal static bool IsDotnetDesktopInstalled(int major)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "shared",
            "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(root))
        {
            return false;
        }

        var prefix = major.ToString() + ".";
        return Directory.EnumerateDirectories(root, prefix + "*").Any();
    }
}
