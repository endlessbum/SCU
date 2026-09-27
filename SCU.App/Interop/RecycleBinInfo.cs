using System.Runtime.InteropServices;

namespace SCU.Interop;

// Размер и число объектов во всех корзинах системы (SHQueryRecycleBin без указания
// диска = сводка по всем томам). Ошибка API трактуется как «не удалось измерить».
public static class RecycleBinInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

    // (байты, объектов); null — запрос не удался.
    public static (long Bytes, long Items)? Query()
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        try
        {
            return SHQueryRecycleBin(null, ref info) == 0
                ? (info.i64Size, info.i64NumItems)
                : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }
}
