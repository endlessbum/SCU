using SCU.Infrastructure.DeepSeek;
using SCU.Models.AI;
using SCU.ViewModels.Sections;

namespace SCU.AppCore.AI;

// Сборка минимального безопасного контекста (п. 11 ТЗ). В контекст попадает
// только то, что нужно модели понять, где находится пользователь:
// приложение, версия, язык, раздел, выбранная утилита, права, провайдер/модель
// и признак поддержки tool calling. API-ключ, настройки, токены, пути
// пользователя и личные данные — никогда не попадают (п. 23 ТЗ).
public sealed class ScuAiContextBuilder : IScuAiContextBuilder
{
    private readonly IScuAiEnvironment _environment;
    private readonly DeepSeekViewModel _deepSeek;

    public ScuAiContextBuilder(IScuAiEnvironment environment, DeepSeekViewModel deepSeek)
    {
        _environment = environment;
        _deepSeek = deepSeek;
    }

    public ScuAiContext Build()
    {
        var provider = _deepSeek.ActiveProviderConfig;
        return new ScuAiContext(
            Application: "SCU",
            Version: ThisAssemblyVersion,
            Language: L.Current.ToString().ToLowerInvariant(),
            IsAdmin: _environment.IsAdmin,
            CurrentSectionNumber: _environment.CurrentSectionNumber,
            CurrentSectionTitle: _environment.CurrentSectionTitle,
            CurrentUtilityId: _environment.CurrentUtilityId,
            CurrentUtilityTitle: _environment.CurrentUtilityTitle,
            ProviderId: provider.Id,
            Model: _deepSeek.Chat.ActiveModel,
            SupportsToolCalling: DeepSeekClient.ProviderSupportsToolCalling(provider));
    }

    // Версия сборки берётся из самой SCU.dll (AssemblyInformationalVersion),
    // без хардкода — для ответов «какая версия SCU» и контекста.
    private static string ThisAssemblyVersion =>
        typeof(ScuAiContextBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

// Контракт сборщика контекста: ScuAiAssistant зависит от интерфейса, а не от
// конкретной реализации — подменной в тестах заглушкой проверяется, что в
// системный промпт и контекст не попадают секреты (п. 34F.28).
public interface IScuAiContextBuilder
{
    ScuAiContext Build();
}
