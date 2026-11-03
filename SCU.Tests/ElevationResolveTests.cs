using SCU.Common;
using Xunit;

namespace SCU.Tests;

public sealed class ElevationResolveTests
{
    [Fact]
    public void IsAdmin_DoesNotThrow()
    {
        // Результат зависит от сессии; важно отсутствие исключения и bool-ответ.
        var _ = Elevation.IsAdmin();
    }

    [Fact]
    public void ResolveExecutablePath_PrefersScuExeInBaseDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SCU.exe");
        var created = false;
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, [0x4D, 0x5A]); // MZ stub
                created = true;
            }

            Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(Elevation.ResolveExecutablePath()!));
        }
        finally
        {
            if (created)
            {
                try { File.Delete(path); } catch { /* temp stub */ }
            }
        }
    }

    [Fact]
    public void RelaunchElevated_WhenAlreadyAdmin_ReturnsSuccess()
    {
        if (!Elevation.IsAdmin())
        {
            // Без UAC-диалога в CI: ветка «уже admin» — единственная безопасная.
            return;
        }

        var result = Elevation.RelaunchElevated();
        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains("администратора", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
