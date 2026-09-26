using SCU.Common;
using Xunit;

namespace SCU.Tests;

// L.T зависит только от статического Current, а подмена XAML-словаря в тестах
// безопасна: Application.Current равен null. Класс один — xunit гонит его тесты
// последовательно, языковое состояние не гоняется с другими классами.
[Collection("LocalizationSequential")]
public class LocalizationTests
{
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
