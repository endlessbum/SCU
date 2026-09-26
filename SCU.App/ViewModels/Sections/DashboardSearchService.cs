namespace SCU.ViewModels.Sections;

// П. 13 аудита: поиск по утилитам Dashboard — синонимы и матчинг вынесены из
// DashboardViewModel в чистый статический сервис (тестируется без UI).
internal static class DashboardSearchService
{
    // Словарь синонимов: слово пользователя → стемы для сопоставления с
    // заголовком и ключевыми словами утилиты. Русские и английские слова
    // работают при любом языке интерфейса.
    // Словарь синонимов: слово пользователя → стемы для сопоставления с
    // заголовком и ключевыми словами утилиты. Русские и английские слова
    // работают при любом языке интерфейса.
    internal static readonly Dictionary<string, string[]> SearchSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- очистка и мусор ---
        ["очист"] = ["очист", "clean", "чист", "temp", "кэш", "кеш", "cache", "корзин", "recycle", "мусор"],
        ["мусор"] = ["очист", "clean", "мусор", "bloat", "junk", "trash", "garbage"],
        ["чист"] = ["очист", "clean", "чист"],
        ["temp"] = ["temp", "временн", "очист"],
        ["кэш"] = ["кэш", "кеш", "cache", "очист"],
        ["cache"] = ["cache", "кэш", "кеш", "очист"],
        ["корзин"] = ["корзин", "recycle", "bin", "очист"],
        ["recycle"] = ["recycle", "корзин", "bin"],
        ["trash"] = ["trash", "мусор", "очист", "clean"],
        ["garbage"] = ["garbage", "мусор", "очист", "clean"],
        ["junk"] = ["junk", "мусор", "очист", "clean"],
        ["браузер"] = ["браузер", "browser", "chrome", "edge", "opera", "firefox", "яндекс", "кэш"],
        ["browser"] = ["browser", "браузер", "chrome", "edge", "opera", "firefox"],
        ["chrome"] = ["chrome", "браузер", "browser"],
        ["firefox"] = ["firefox", "браузер", "browser"],
        ["opera"] = ["opera", "браузер", "browser"],
        ["яндекс"] = ["яндекс", "браузер", "browser"],
        ["обновл"] = ["обновл", "update", "wu", "windows update", "softwaredistribution"],
        ["update"] = ["update", "обновл", "wu", "драйвер", "driver"],
        ["wu"] = ["wu", "update", "обновл"],
        ["драйвер"] = ["драйвер", "driver", "windows update", "обновл"],
        ["driver"] = ["driver", "драйвер", "windows update"],
        ["драв"] = ["драйвер", "driver"],
        ["пауза"] = ["пауза", "pause", "приостанов", "обновл"],
        ["pause"] = ["pause", "пауза", "приостанов"],
        ["приостанов"] = ["приостанов", "pause", "пауза"],
        ["блок"] = ["блок", "block", "запрет", "обновл"],
        ["block"] = ["block", "блок", "запрет"],
        ["запрет"] = ["запрет", "block", "блок"],

        // --- питание и железо ---
        ["гиберн"] = ["гиберн", "hibernat", "сон", "sleep", "hiberfil"],
        ["hibernat"] = ["hibernat", "гиберн", "hiberfil", "sleep"],
        ["сон"] = ["сон", "гиберн", "hibernat", "sleep"],
        ["sleep"] = ["sleep", "сон", "гиберн", "hibernat"],
        ["быстр"] = ["быстр", "fastboot", "fast startup", "запуск"],
        ["fastboot"] = ["fastboot", "быстр", "fast startup"],
        ["памят"] = ["памят", "memory", "озу", "ram", "compress", "сжат"],
        ["memory"] = ["memory", "памят", "ram", "compress"],
        ["озу"] = ["озу", "ram", "памят", "memory"],
        ["ram"] = ["ram", "озу", "памят", "memory"],
        ["сжат"] = ["сжат", "compress", "compact", "памят"],
        ["compress"] = ["compress", "сжат", "compact", "памят"],
        ["sysmain"] = ["sysmain", "superfetch"],
        ["superfetch"] = ["superfetch", "sysmain"],
        ["prefetch"] = ["prefetch", "prefetcher"],
        ["compact"] = ["compact", "сжат", "compactos"],
        ["compactos"] = ["compactos", "compact", "сжат"],
        ["коротк"] = ["коротк", "8.3", "short names"],
        ["доступ"] = ["доступ", "last access", "access"],
        ["last access"] = ["last access", "доступ"],
        ["план"] = ["план", "plan", "power", "питан", "производ"],
        ["power"] = ["power", "питан", "план", "plan"],
        ["питан"] = ["питан", "power", "план", "plan"],
        ["производ"] = ["производ", "performance", "план", "скорост"],
        ["performance"] = ["performance", "производ", "план"],
        ["скорост"] = ["скорост", "speed", "производ", "performance"],
        ["speed"] = ["speed", "скорост", "производ"],
        ["тормоз"] = ["тормоз", "производ", "performance", "скорост"],
        ["лаг"] = ["лаг", "fps", "производ", "игр", "game"],
        ["fps"] = ["fps", "лаг", "игр", "game"],
        ["подкач"] = ["подкач", "pagefile", "swap"],
        ["pagefile"] = ["pagefile", "подкач", "swap"],
        ["cpu"] = ["cpu", "процессор", "processor", "ядер", "numproc"],
        ["процессор"] = ["процессор", "cpu", "processor"],
        ["processor"] = ["processor", "cpu", "процессор"],
        ["numproc"] = ["numproc", "cpu", "bcd", "ограничен"],
        ["ограничен"] = ["ограничен", "numproc", "truncatememory", "cpu", "bcd"],

        // --- игры и ввод ---
        ["игр"] = ["игр", "game", "gaming", "fps", "dvr"],
        ["game"] = ["game", "игр", "gaming"],
        ["gaming"] = ["gaming", "game", "игр"],
        ["dvr"] = ["dvr", "запис", "record", "capture"],
        ["запис"] = ["запис", "dvr", "record", "capture"],
        ["record"] = ["record", "запис", "dvr"],
        ["capture"] = ["capture", "запис", "dvr"],
        ["мышь"] = ["мышь", "mouse", "указател", "курсор", "pointer", "accelerat", "ускорен"],
        ["mouse"] = ["mouse", "мышь", "pointer", "accelerat"],
        ["курсор"] = ["курсор", "pointer", "мышь", "mouse"],
        ["pointer"] = ["pointer", "указател", "mouse", "мышь"],
        ["указател"] = ["указател", "pointer", "мышь", "mouse"],
        ["ускорен"] = ["ускорен", "accelerat", "мышь", "mouse", "edge"],
        ["accelerat"] = ["accelerat", "ускорен", "mouse"],
        ["клавиатур"] = ["клавиатур", "keyboard", "залипан", "sticky"],
        ["keyboard"] = ["keyboard", "клавиатур", "sticky", "залипан"],
        ["залипан"] = ["залипан", "sticky", "клавиатур", "keyboard"],
        ["sticky"] = ["sticky", "залипан", "keys"],

        // --- сеть ---
        ["сеть"] = ["сеть", "net", "network", "интернет", "inet", "tcp", "адаптер"],
        ["net"] = ["net", "сеть", "network", "tcp"],
        ["network"] = ["network", "сеть", "net", "tcp"],
        ["интернет"] = ["интернет", "inet", "сеть", "network"],
        ["inet"] = ["inet", "интернет", "сеть"],
        ["ping"] = ["ping", "пинг", "сеть", "tcp"],
        ["пинг"] = ["пинг", "ping", "сеть"],
        ["tcp"] = ["tcp", "сеть", "net", "autotun"],
        ["mtu"] = ["mtu", "сеть", "интерфейс"],
        ["qos"] = ["qos", "полос", "override"],
        ["netbios"] = ["netbios", "smb", "сеть"],
        ["autotun"] = ["autotun", "auto-tuning", "автотюнинг", "tcp"],
        ["auto-tuning"] = ["auto-tuning", "autotun", "tcp"],
        ["автотюнинг"] = ["автотюнинг", "autotun", "auto-tuning"],
        ["ecn"] = ["ecn", "перегруз", "tcp"],
        ["адаптер"] = ["адаптер", "adapter", "nic", "карт", "сеть"],
        ["adapter"] = ["adapter", "адаптер", "nic", "network"],
        ["карт"] = ["карт", "адаптер", "adapter", "сеть"],

        // --- приватность ---
        ["телеметри"] = ["телеметри", "telemetry", "шпион", "spy", "слеж", "приватн", "privacy", "ceip", "diagtrack"],
        ["telemetry"] = ["telemetry", "телеметри", "spy", "privacy"],
        ["шпион"] = ["шпион", "spy", "телеметри", "telemetry", "слеж"],
        ["spy"] = ["spy", "шпион", "телеметри", "telemetry"],
        ["слеж"] = ["слеж", "шпион", "spy", "телеметри", "telemetry"],
        ["приватн"] = ["приватн", "privacy", "телеметри", "telemetry"],
        ["privacy"] = ["privacy", "приватн", "телеметри", "telemetry"],
        ["реклам"] = ["реклам", "ads", "совет", "tips", "уведомлен", "notification"],
        ["ads"] = ["ads", "реклам", "совет", "tips"],
        ["совет"] = ["совет", "tips", "реклам", "уведомлен"],
        ["tips"] = ["tips", "совет", "реклам"],
        ["уведомлен"] = ["уведомлен", "notification", "совет"],
        ["notification"] = ["notification", "уведомлен", "совет"],
        ["uwp"] = ["uwp", "фонов", "background"],
        ["фонов"] = ["фонов", "background", "uwp"],
        ["background"] = ["background", "фонов", "uwp"],
        ["copilot"] = ["copilot", "фильтр", "bing"],

        // --- интерфейс ---
        ["проводник"] = ["проводник", "explorer", "file explorer"],
        ["explorer"] = ["explorer", "проводник"],
        ["контекст"] = ["контекст", "context menu", "меню"],
        ["context menu"] = ["context menu", "контекст", "меню"],
        ["меню"] = ["меню", "menu", "пуск", "start", "контекст", "задержк"],
        ["пуск"] = ["пуск", "start", "меню", "menu"],
        ["задержк"] = ["задержк", "delay", "menushowdelay", "меню"],
        ["delay"] = ["delay", "задержк", "menu"],
        ["панел"] = ["панел", "taskbar", "задач"],
        ["taskbar"] = ["taskbar", "панел", "задач"],
        ["галере"] = ["галере", "gallery"],
        ["gallery"] = ["gallery", "галере"],
        ["скрыт"] = ["скрыт", "hidden"],
        ["hidden"] = ["hidden", "скрыт"],
        ["расширен"] = ["расширен", "extension"],
        ["extension"] = ["extension", "расширен"],
        ["onedrive"] = ["onedrive"],
        ["прозрачн"] = ["прозрачн", "transparen", "aero"],
        ["transparen"] = ["transparen", "прозрачн"],
        ["эскиз"] = ["эскиз", "thumbnail", "значк", "icons"],
        ["thumbnail"] = ["thumbnail", "эскиз"],
        ["значк"] = ["значк", "icons", "эскиз"],
        ["тени"] = ["тени", "shadow"],
        ["shadow"] = ["shadow", "тени"],
        ["анимац"] = ["анимац", "animation", "эффект", "effect", "visual"],
        ["animation"] = ["animation", "анимац", "effect", "эффект"],
        ["эффект"] = ["эффект", "effect", "animation", "анимац"],
        ["effect"] = ["effect", "эффект", "animation"],
        ["перезапуск"] = ["перезапуск", "restart", "explorer", "проводник"],
        ["restart"] = ["restart", "перезапуск", "explorer"],

        // --- обслуживание и прочее ---
        ["служб"] = ["служб", "service", "бэкап", "backup"],
        ["service"] = ["service", "служб", "backup"],
        ["бэкап"] = ["бэкап", "backup", "резерв", "restore", "откат"],
        ["backup"] = ["backup", "бэкап", "резерв", "restore"],
        ["резерв"] = ["резерв", "backup", "бэкап", "restore", "откат"],
        ["restore"] = ["restore", "откат", "восстанов", "резерв", "backup"],
        ["откат"] = ["откат", "restore", "восстанов", "резерв"],
        ["восстанов"] = ["восстанов", "restore", "точк", "резерв"],
        ["задач"] = ["задач", "task", "планировщ", "scheduler"],
        ["task"] = ["task", "задач", "планировщ", "scheduler"],
        ["планировщ"] = ["планировщ", "scheduler", "task", "задач"],
        ["scheduler"] = ["scheduler", "планировщ", "task"],
        ["точк"] = ["точк", "restore point", "восстанов"],
        ["dism"] = ["dism", "sfc", "целостн", "integrity"],
        ["sfc"] = ["sfc", "dism", "целостн"],
        ["целостн"] = ["целостн", "integrity", "dism", "sfc"],
        ["integrity"] = ["integrity", "целостн", "dism", "sfc"],
        ["winsxs"] = ["winsxs", "хранилищ", "component store", "resetbase"],
        ["хранилищ"] = ["хранилищ", "winsxs", "component store"],
        ["resetbase"] = ["resetbase", "winsxs", "хранилищ"],
        ["индексац"] = ["индексац", "index", "поиск", "search"],
        ["index"] = ["index", "индексац", "поиск"],
        ["поиск"] = ["поиск", "search", "индексац", "index"],
        ["search"] = ["search", "поиск", "index"],
        ["дамп"] = ["дамп", "dump", "wer", "ошибк"],
        ["dump"] = ["dump", "дамп", "wer"],
        ["wer"] = ["wer", "дамп", "dump"],
        ["uac"] = ["uac", "контроль", "учётных", "учетных", "защит", "admin"],
        ["контроль"] = ["контроль", "uac", "учётных", "учетных"],
        ["учётных"] = ["учётных", "uac", "контроль"],
        ["учетных"] = ["учетных", "uac", "контроль"],
        ["защит"] = ["защит", "uac", "secure", "контроль"],
        ["журнал"] = ["журнал", "log", "лог"],
        ["лог"] = ["лог", "log", "журнал"],
        ["edge"] = ["edge", "браузер", "browser", "ускорен"],
        ["версия"] = ["версия", "version", "about", "о приложении"],
        ["about"] = ["about", "о приложении", "версия"],
        ["о приложении"] = ["о приложении", "about", "версия"],
    };

    // Разбор запроса на слова.
    internal static string[] SplitTokens(string query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Слово совпало, если оно есть в тексте утилиты либо через словарь синонимов.
    internal static bool MatchesToken(string token, string searchText)
    {
        if (searchText.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var (key, stems) in SearchSynonyms)
        {
            var keyMatch = token.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                || (token.Length >= 3 && key.StartsWith(token, StringComparison.OrdinalIgnoreCase));
            if (!keyMatch)
            {
                continue;
            }

            foreach (var stem in stems)
            {
                if (searchText.Contains(stem, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Совпадение строки утилиты со всеми токенами запроса.
    internal static bool MatchesText(string searchText, string[] tokens) =>
        tokens.Length == 0 || tokens.All(token => MatchesToken(token, searchText));
}
