using System.Windows;
using SCU.Infrastructure.Logging;
using SCU.ViewModels;

namespace SCU.Views.Cards;

// Фабрика закреплённых карточек для «Главной» (вынесена из лямбды
// MainWindow.CreateDashboardView): перед созданием дубликата лениво
// инициализирует раздел-владельца, чтобы карточка была рабочей сразу
// (п. ТЗ «Закрепление»), а при неизвестном id пишет warn в журнал.
public static class PinnedCardFactory
{
    internal const string UnknownIdLogPrefix = "PINCARDS | unknown pinned card id: ";

    public static FrameworkElement? Create(string cardId, MainViewModel? main, Logger logger)
    {
        var section = PinnedCardCatalog.SectionNumberOf(cardId);
        if (section is null && main is not null)
        {
            section = PinnedCardCatalog.ScriptSectionNumberOf(cardId, main.UserScripts);
        }

        if (section is not { } number)
        {
            // id из pinned-cards.json неизвестен каталогу (переименование
            // карточки, ручная правка файла): карточку не рисуем, но в лог
            // пишем — молча пропавшие закрепления отследить было бы нельзя.
            logger.Warn(UnknownIdLogPrefix + cardId);
            return null;
        }

        // main равен null только в тестах (MainViewModel тянет весь граф
        // сервисов): там проверяется лишь резолвинг раздела, без его инициализации.
        main?.EnsureSectionReady(number);
        return main is null ? null : PinnedCardCatalog.Create(cardId, main);
    }
}
