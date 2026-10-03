using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Генерация окрашенных .ico для ярлыков приложения: базовый SCU.ico перекрашивается
// в светлые варианты пресетов ThemeManager и записывается набором кадров
// 16/24/32/48/64/128 (DIB, прямая альфа) + 256 (PNG). Файл один на пресет
// (SCU-<пресет>-light.ico) — в тёмной и светлой теме используется он же. Запуск:
//   dotnet run ▸ project tools/AccentIconGenerator ▸ args: SCU.ico SCU.App/Assets/Icons
// Палитра зеркалит ThemeManager.AccentPresets — при её изменении перегенерировать.

var sourcePath = args.ElementAtOrDefault(0) ?? "SCU.ico";
var outputDir = args.ElementAtOrDefault(1) ?? "SCU.App/Assets/Icons";

// (имя пресета, светлый вариант цвета)
var presets = new (string Name, Color Light)[]
{
    ("blue", Color.FromRgb(0x00, 0x7A, 0xFF)),
    ("skyblue", Color.FromRgb(0x32, 0xAD, 0xE6)),
    ("purple", Color.FromRgb(0xAF, 0x52, 0xDE)),
    ("green", Color.FromRgb(0x34, 0xC7, 0x59)),
    ("orange", Color.FromRgb(0xFF, 0x95, 0x00))
};

Directory.CreateDirectory(outputDir);
foreach (var preset in presets)
{
    // Один вариант на пресет: «light» используется и в тёмной, и в светлой теме.
    WriteIcon(Path.Combine(outputDir, $"SCU-{preset.Name}-light.ico"), sourcePath, preset.Light);
    Console.WriteLine($"{preset.Name}: light");
}

static void WriteIcon(string outputPath, string sourcePath, Color color)
{
    var baseFrame = GetLargestFrame(sourcePath);
    var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
    using var stream = File.Create(outputPath);
    using var writer = new BinaryWriter(stream);

    writer.Write((ushort)0); // reserved
    writer.Write((ushort)1); // type: icon
    writer.Write((ushort)sizes.Length);

    // Сначала считаем кадры в память, чтобы знать смещения в каталоге.
    var frames = sizes.Select(size => (Size: size, Data: EncodeFrame(baseFrame, size, color))).ToArray();
    var offset = 6 + 16 * frames.Length;
    foreach (var (size, data) in frames)
    {
        writer.Write((byte)(size == 256 ? 0 : size)); // width: 0 = 256
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)0); // палитра не используется
        writer.Write((byte)0); // reserved
        writer.Write((ushort)1); // planes
        writer.Write((ushort)32); // bit count
        writer.Write((uint)data.Length);
        writer.Write((uint)offset);
        offset += data.Length;
    }

    foreach (var (_, data) in frames)
    {
        writer.Write(data);
    }
}

static BitmapSource GetLargestFrame(string sourcePath)
{
    var decoder = new IconBitmapDecoder(
        new Uri(Path.GetFullPath(sourcePath)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    frame.Freeze();
    return frame;
}

static byte[] EncodeFrame(BitmapSource baseFrame, int size, Color color)
{
    var scaled = new TransformedBitmap(baseFrame, new ScaleTransform(size / 256.0, size / 256.0));
    RenderOptions.SetBitmapScalingMode(scaled, BitmapScalingMode.HighQuality);
    var tinted = Tint(scaled, color);

    if (size == 256)
    {
        // PNG-кадр: 256 px как DIB раздувает файл, а PNG оболочка читает с Vista.
        using var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(tinted));
        encoder.Save(png);
        return png.ToArray();
    }

    return EncodeDib(tinted);
}

// Замена цвета при сохранении альфы — та же логика, что AccentIconManager.Tint
// (Pbgra32, каналы премультиплицированы на альфу).
static BitmapSource Tint(BitmapSource source, Color color)
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

    var result = new WriteableBitmap(width, height, converted.DpiX, converted.DpiY, PixelFormats.Pbgra32, null);
    result.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
    result.Freeze();
    return result;
}

// DIB-кадр ICO: BITMAPINFOHEADER + пиксели (снизу вверх, BGRA) + пустая AND-маска
// (альфа в самих пикселях). Альфа в DIB прямая, а Pbgra32 премультиплицирована —
// делим каналы на альфу.
static byte[] EncodeDib(BitmapSource source)
{
    var width = source.PixelWidth;
    var height = source.PixelHeight;
    var stride = width * 4;
    var pixels = new byte[stride * height];
    source.CopyPixels(pixels, stride, 0);

    var maskStride = (width + 31) / 32 * 4;
    using var dib = new MemoryStream();
    using var writer = new BinaryWriter(dib);

    writer.Write(40); // biSize
    writer.Write(width);
    writer.Write(height * 2); // XOR + AND
    writer.Write((short)1); // planes
    writer.Write((short)32); // bit count
    writer.Write(0u); // BI_RGB
    writer.Write((uint)(stride * height + maskStride * height));
    writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); // метры/палитра

    for (var y = height - 1; y >= 0; y--)
    {
        for (var x = 0; x < width; x++)
        {
            var i = y * stride + x * 4;
            var alpha = pixels[i + 3];
            writer.Write(alpha == 0
                ? 0u
                // Little-endian: байты на выходе B, G, R, A.
                : (uint)(alpha << 24 | pixels[i + 2] << 16 | pixels[i + 1] << 8 | pixels[i]));
        }
    }

    for (var i = 0; i < maskStride * height; i++)
    {
        writer.Write((byte)0);
    }

    return dib.ToArray();
}
