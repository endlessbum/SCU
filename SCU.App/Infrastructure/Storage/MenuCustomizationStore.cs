using System.Text.Json;
using SCU.Common;

namespace SCU.Infrastructure.Storage;

// Пользовательская вкладка, созданная в «Редактировании меню»: имя, группа
// в сайдбаре и список утилит (реестр больших переключателей SCU).
public sealed class CustomSectionData
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public List<string> Utils { get; set; } = [];
}

// Модель кастомизации меню: скрытие/переименование/перенос встроенных разделов,
// пользовательские вкладки и удалённые встроенные утилиты. Хранится в
// %AppData%\SCU\menu.json (атомарная запись, best-effort чтение — как у других
// хранилищ SCU). Отсутствующий файл = заводское меню.
public sealed class MenuCustomization
{
    public List<int> Hidden { get; set; } = [];
    public Dictionary<string, string> Titles { get; set; } = [];
    public Dictionary<string, string> Groups { get; set; } = [];
    public List<string> DeletedUtils { get; set; } = [];
    public List<CustomSectionData> CustomSections { get; set; } = [];

    public static MenuCustomization Empty { get; } = new();

    public MenuCustomization Clone() => new()
    {
        Hidden = [.. Hidden],
        Titles = new Dictionary<string, string>(Titles),
        Groups = new Dictionary<string, string>(Groups),
        DeletedUtils = [.. DeletedUtils],
        CustomSections = CustomSections
            .Select(section => new CustomSectionData
            {
                Id = section.Id,
                Title = section.Title,
                Group = section.Group,
                Utils = [.. section.Utils],
            })
            .ToList(),
    };
}

public sealed class MenuCustomizationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Logger _logger;
    private readonly string _path;

    public MenuCustomizationStore(Logger logger, string? filePath = null)
    {
        _logger = logger;
        _path = filePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCU", "menu.json");
    }

    public MenuCustomization Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return MenuCustomization.Empty;
            }

            return JsonSerializer.Deserialize<MenuCustomization>(File.ReadAllText(_path)) ?? MenuCustomization.Empty;
        }
        catch (Exception exception)
        {
            _logger.Warn("MENU | load failed | " + exception.Message);
            return MenuCustomization.Empty;
        }
    }

    public void Save(MenuCustomization menu)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(menu, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception exception)
        {
            _logger.Warn("MENU | save failed | " + exception.Message);
        }
    }
}
