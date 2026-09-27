using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SCU.Common;

// Перекраска фирменных знаков приложения под цвет иконки (ThemeManager.IconAccent,
// отдельная настройка, не зависящая от акцента интерфейса). Исходная графика
// монохромная (SCU.png, SCU.ico), поэтому перекраска — это замена RGB на цвет
// с сохранением альфа-канала. Результаты кэшируются по цвету.
public static class AccentIconManager
{
    private const string TitleIconUri = "pack://application:,,,/Assets/SCU.png";
    private const string WindowIconUri = "pack://application:,,,/SCU.ico";

    // Знак в заголовке рисуется высотой 20 px; декодируем с запасом на HiDPI.
    private const int TitleIconDecodeWidth = 512;

    private static BitmapSource? _titleIconBase;
    private static BitmapSource? _windowIconBase;
    private static readonly Dictionary<Color, BitmapSource> TitleIconCache = new();
    private static readonly Dictionary<Color, BitmapSource> WindowIconCache = new();

    // Знак «SCU» для заголовка окна в заданном цвете.
    public static ImageSource GetTitleBarIcon(Color color)
    {
        if (!TitleIconCache.TryGetValue(color, out var icon))
        {
            icon = Tint(GetTitleIconBase(), color);
            TitleIconCache[color] = icon;
        }

        return icon;
    }

    // Квадратный знак-шестерёнка для Icon окна (панель задач, Alt+Tab) в заданном цвете.
    public static ImageSource GetWindowIcon(Color color)
    {
        if (!WindowIconCache.TryGetValue(color, out var icon))
        {
            icon = Tint(GetWindowIconBase(), color);
            WindowIconCache[color] = icon;
        }

        return icon;
    }

    private static BitmapSource GetTitleIconBase()
    {
        if (_titleIconBase is not null)
        {
            return _titleIconBase;
        }

        var source = new BitmapImage();
        source.BeginInit();
        source.UriSource = new Uri(TitleIconUri, UriKind.Absolute);
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.DecodePixelWidth = TitleIconDecodeWidth;
        source.EndInit();
        source.Freeze();
        return _titleIconBase = source;
    }

    private static BitmapSource GetWindowIconBase()
    {
        if (_windowIconBase is not null)
        {
            return _windowIconBase;
        }

        // В .ico несколько кадров (256/48/32/16) — берём самый крупный.
        var decoder = new IconBitmapDecoder(
            new Uri(WindowIconUri, UriKind.Absolute),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
        var best = decoder.Frames
            .OrderByDescending(frame => frame.PixelWidth)
            .First();
        best.Freeze();
        return _windowIconBase = best;
    }

    // Замена цвета при сохранении альфы. Кадр приводится к Pbgra32, где цветовые
    // каналы премультиплицированы на альфу, поэтому каждый канал перекрашенного
    // пикселя умножается на альфу исходного.
    private static BitmapSource Tint(BitmapSource source, Color color)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            pixels[i] = (byte)(color.B * alpha / 255);
            pixels[i + 1] = (byte)(color.G * alpha / 255);
            pixels[i + 2] = (byte)(color.R * alpha / 255);
        }

        var result = new WriteableBitmap(
            width, height, converted.DpiX, converted.DpiY, PixelFormats.Pbgra32, null);
        result.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        result.Freeze();
        return result;
    }
}
