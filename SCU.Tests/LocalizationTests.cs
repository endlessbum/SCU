using SCU.Common;
using Xunit;

namespace SCU.Tests;

// L.T зависит только от статического Current, а подмена XAML-словаря в тестах
// безопасна: Application.Current равен null. Класс один — xunit гонит его тесты
// последовательно, языковое состояние не гоняется с другими классами.
[Collection("LocalizationSequential")]
public class LocalizationTests
{
    [Theory]
    [InlineData("Закрепить карточку на главной", "Pin card to the dashboard")]
    [InlineData("Открепить карточку", "Unpin card")]
    public void T_EnglishMode_PinCardStrings_Translated(string key, string expected)
    {
        // Guard от удаления строк скрепки из EN-словаря: тогда L.T вернул бы
        // ключ как есть, и тултип глифа закрепления показал бы русский текст.
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.En, notify: false);

            Assert.Equal(expected, L.T(key));
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    [Fact]
    public void T_RussianMode_ReturnsKeyAsIs()
    {
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.Ru, notify: false);

            Assert.Equal("Отмена операции…", L.T("Отмена операции…"));
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    [Fact]
    public void T_EnglishMode_TranslatesKnownKey()
    {
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.En, notify: false);

            Assert.Equal("Cancelling the operation…", L.T("Отмена операции…"));
            Assert.Equal("Remove", L.T("Удалить"));
            Assert.Equal("yes", L.T("есть"));
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    // Аудит, п. 18: в английском режиме пользовательские строки не содержат
    // кириллицы. Раньше точных ключей не было в EN-словаре, а фрагментная
    // подстановка переводила только префикс («Не удалось прочитать »), и
    // английский диалог заканчивался русскими словами.
    [Theory]
    [InlineData("Модель вернула пустой ответ — повторите запрос.")]
    [InlineData("Не удалось прочитать текущее состояние: {0}")]
    [InlineData("Проверка завершена, но журнал не сохранён на диск — подробности в журнале.")]
    [InlineData("Проверка завершена, но снимок и журнал не сохранены на диск — подробности в журнале.")]
    [InlineData("Настройки не сохранены на диск — подробности в журнале.")]
    [InlineData("Пропущено состояний: {0}.")]
    [InlineData("системный том")]
    [InlineData("На системе несколько файлов подкачки. Размер будет применён ко ВСЕМ перечисленным томам.")]
    [InlineData("Дамп памяти")]
    [InlineData("Уменьшение файла подкачки может повлиять на возможность создания дампа памяти после BSOD.")]
    [InlineData("SHA-256: — (файл не читается)")]
    [InlineData("SHA-256: — (ошибка вычисления)")]
    [InlineData("Дамп памяти: {0}")]
    [InlineData("Дамп памяти: не удалось прочитать")]
    [InlineData("отключён")]
    [InlineData("полный (Complete)")]
    [InlineData("ядра (Kernel)")]
    [InlineData("малый (Small)")]
    [InlineData("автоматический (Automatic)")]
    [InlineData("Внимание: текущая конфигурация дампа памяти может требовать достаточный pagefile.")]
    [InlineData("Размер файла подкачки должен быть в диапазоне 16..262144 МБ.")]
    [InlineData("Настройка файла подкачки не подтвердилась чтением реестра.")]
    [InlineData("Бэкап файла подкачки не был создан.")]
    [InlineData("Файл подкачки уже управляется Windows.")]
    [InlineData("Управление файлом подкачки передано Windows. Вступит в силу после перезагрузки.")]
    public void T_EnglishMode_Audit18Strings_HaveNoCyrillic(string key)
    {
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.En, notify: false);

            var text = L.T(key, "X");

            Assert.DoesNotContain(text, character => character is >= 'Ѐ' and <= 'ӿ');
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    // Аудит, п. 18 (остаток): сводка crash dump собирается в PowerService и
    // попадает в диалог изменения pagefile и PageFileSummaryText без
    // повторного перевода — проверяем сборку целиком в английском режиме.
    [Fact]
    public void T_EnglishMode_CrashDumpSummaryAndPageFileResults_AreTranslated()
    {
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.En, notify: false);

            Assert.Equal("Memory dump: Kernel, file: C:\\WINDOWS\\MEMORY.DMP",
                L.T("Дамп памяти: {0}, файл: {1}", L.T("ядра (Kernel)"), @"C:\WINDOWS\MEMORY.DMP"));
            Assert.Equal("Memory dump: disabled", L.T("Дамп памяти: {0}", L.T("отключён")));
            Assert.Equal("Memory dump: code 42", L.T("Дамп памяти: {0}", L.T("код {0}", 42)));
            Assert.Equal("Page file: 4096 MB on volumes: C:. Takes effect after reboot.",
                L.T("Файл подкачки: {0} МБ на томах: {1}. Вступит в силу после перезагрузки.", 4096, "C:"));
            Assert.Equal("Page file: 4096 MB (C:\\pagefile.sys). Takes effect after reboot.",
                L.T("Файл подкачки: {0} МБ ({1}). Вступит в силу после перезагрузки.", 4096, @"C:\pagefile.sys"));
            Assert.Equal("Backup saved: pagefile_backup.json",
                L.T("Бэкап сохранён: {0}", "pagefile_backup.json"));
            Assert.Equal("Failed to enable the System Managed page file: access denied",
                L.T("Не удалось включить System Managed pagefile: {0}", "access denied"));
            Assert.Equal("Failed to save the page file backup: disk full",
                L.T("Не удалось сохранить бэкап файла подкачки: {0}", "disk full"));
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    // ===================== Обновление показанных статусов (дизайн, п. 9) =====================

    // При смене языка разделы переподписывают свой статус: раньше обновлялись
    // только XAML-ресурсы, а уже выведенные статусы оставались на старом языке.
    [Fact]
    public void SetLanguage_RaisesStatusRefresh()
    {
        var saved = L.Current;
        var calls = 0;
        Action refresh = () => calls++;
        L.RegisterStatusRefresh(refresh);
        try
        {
            L.SetLanguage(saved == AppLanguage.En ? AppLanguage.Ru : AppLanguage.En, notify: false);

            Assert.Equal(1, calls);
        }
        finally
        {
            L.UnregisterStatusRefresh(refresh);
            L.SetLanguage(saved, notify: false);
        }
    }

    [Fact]
    public void UnregisterStatusRefresh_StopsCallback()
    {
        var saved = L.Current;
        var calls = 0;
        Action refresh = () => calls++;
        L.RegisterStatusRefresh(refresh);
        L.UnregisterStatusRefresh(refresh);

        try
        {
            L.SetLanguage(saved == AppLanguage.En ? AppLanguage.Ru : AppLanguage.En, notify: false);

            Assert.Equal(0, calls);
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    [Theory]
    [InlineData("Работает", "Running")]
    [InlineData("Остановлена", "Stopped")]
    [InlineData("Отключена", "Disabled")]
    [InlineData("Нет службы", "No service")]
    [InlineData("Готова", "Ready")]
    [InlineData("Выполняется", "Running")]
    [InlineData("Нет задачи", "No task")]
    public void T_EnglishMode_TranslatesServiceAndTaskStates(string key, string expected)
    {
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.En, notify: false);

            Assert.Equal(expected, L.T(key));
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    [Fact]
    public void T_EnglishMode_UnknownKeyFallsBackToItself()
    {
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.En, notify: false);

            var key = "Такой строки в словаре нет";
            Assert.Equal(key, L.T(key));
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    [Theory]
    [InlineData("Ошибка (код {0}): {1}", 5, "access denied", "Error (code 5): access denied")]
    [InlineData("Загружено компонентов: {0}.", 12, null, "Loaded components: 12.")]
    [InlineData("Удаление — {0}", "KMS", null, "Removal — KMS")]
    public void T_EnglishMode_FormatsArguments(string key, object? arg0, object? arg1, string expected)
    {
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.En, notify: false);

            Assert.Equal(expected, L.T(key, arg0, arg1));
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    [Fact]
    public void T_RussianMode_WithArgs_FormatsKeyDirectly()
    {
        var saved = L.Current;
        try
        {
            L.SetLanguage(AppLanguage.Ru, notify: false);

            Assert.Equal("Ошибка (код 3): тест", L.T("Ошибка (код {0}): {1}", 3, "тест"));
        }
        finally
        {
            L.SetLanguage(saved, notify: false);
        }
    }

    [Fact]
    public void SetLanguage_SameLanguage_DoesNotRaiseEvent()
    {
        var saved = L.Current;
        var raised = 0;
        void Handler() => raised++;
        L.LanguageChanged += Handler;
        try
        {
            L.SetLanguage(saved, notify: false);
            L.SetLanguage(saved);

            Assert.Equal(0, raised);
        }
        finally
        {
            L.LanguageChanged -= Handler;
            L.SetLanguage(saved, notify: false);
        }
    }

    [Fact]
    public void SetLanguage_DifferentLanguage_RaisesEvent()
    {
        var target = L.Current == AppLanguage.Ru ? AppLanguage.En : AppLanguage.Ru;
        var saved = L.Current;
        var raised = 0;
        void Handler() => raised++;
        L.LanguageChanged += Handler;
        try
        {
            L.SetLanguage(target);

            Assert.Equal(1, raised);
            Assert.Equal(target, L.Current);
        }
        finally
        {
            L.LanguageChanged -= Handler;
            L.SetLanguage(saved, notify: false);
        }
    }
}
