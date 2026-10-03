using System.Windows;
using System.Windows.Controls;
using SCU.ViewModels;
using SCU.ViewModels.Sections;
using SCU.Views.Controls;

namespace SCU.Views.Cards;

// Реестр закрепляемых карточек (п. ТЗ «Закрепление карточек»): по CardId
// возвращает номер раздела-владельца (для ленивой инициализации) и создаёт
// дубликат карточки для области закреплённых на «Главной». DataContext
// дубликата — тот же ViewModel, что у карточки в разделе, поэтому все кнопки
// и поля дубликата работают в полном объёме. Оригинал из раздела не убирается,
// а в поиске утилит дубликаты не участвуют (реестр утилит не меняется).
public static class PinnedCardCatalog
{
    // Единственная таблица «id → (раздел-владелец, фабрика дубликата)»: оба
    // потребителя (SectionNumberOf и Create) читают из неё, поэтому карточка
    // не может попасть в один метод и потеряться в другом. Раздел нужен для
    // ленивой инициализации перед созданием дубликата на «Главной».
    private static readonly IReadOnlyDictionary<string, (int Section, Func<MainViewModel, FrameworkElement> Factory)> Cards =
        new Dictionary<string, (int, Func<MainViewModel, FrameworkElement>)>(StringComparer.OrdinalIgnoreCase)
        {
            ["net.gaming"] = (9, main => new NetworkGamingCard { DataContext = main.Network }),
            ["net.tcp"] = (9, main => new NetworkTcpCard { DataContext = main.Network }),
            ["net.mtu"] = (9, main => new NetworkMtuCard { DataContext = main.Network }),
            ["net.qos"] = (9, main => new NetworkQosCard { DataContext = main.Network }),
            ["net.netbios"] = (9, main => new NetworkNetBiosCard { DataContext = main.Network }),
            ["net.dns"] = (9, main => new NetworkDnsCard { DataContext = main.Network }),
            ["maint.integrity"] = (12, main => new MaintenanceIntegrityCard { DataContext = main.Maintenance }),
            ["maint.winsxs"] = (12, main => new MaintenanceWinSxsCard { DataContext = main.Maintenance }),
            ["update.services"] = (16, main => new UpdateServicesCard { DataContext = main.Update }),
            ["update.pause"] = (16, main => new UpdatePauseCard { DataContext = main.Update }),
            ["drivers.state"] = (25, main => new DriversStateCard { DataContext = main.Drivers }),
            ["ai.assistant"] = (24, main => new ScuAiCard { DataContext = main.DeepSeek }),
            ["settings.addscript"] = (17, main => new SettingsAddScriptCard { DataContext = main }),
        };

    // Для тестов: известные каталогу id (защита от случайного удаления строки).
    internal static IEnumerable<string> KnownCardIds => Cards.Keys;

    // Раздел, чей контекст нужен карточке; null — id неизвестен.
    // Сравнение регистронезависимое: PinState сравнивает id без учёта регистра,
    // каталог обязан соглашаться, иначе закрепление из pinned-cards.json,
    // записанное в другом регистре, не нашло бы карточку-владельца.
    public static int? SectionNumberOf(string cardId) =>
        Cards.TryGetValue(cardId, out var entry) ? entry.Section
            : cardId.StartsWith("input.", StringComparison.OrdinalIgnoreCase) ? 11
            : null;

    // Раздел-владелец карточки скрипта («uscript.<scriptId>»); null — id не
    // скриптовый или скрипт не найден (удалён). Раздел у скрипта любой, включая
    // кастомные вкладки ≥100.
    public static int? ScriptSectionNumberOf(string cardId,
        IReadOnlyList<MainViewModel.UserScriptCard> scripts)
    {
        if (!cardId.StartsWith("uscript.", StringComparison.Ordinal))
        {
            return null;
        }

        var scriptId = cardId["uscript.".Length..];
        return scripts.FirstOrDefault(script => script.Id == scriptId)?.SectionNumber;
    }

    public static FrameworkElement? Create(string cardId, MainViewModel main)
    {
        var id = cardId.ToLowerInvariant();
        if (Cards.TryGetValue(id, out var entry))
        {
            return entry.Factory(main);
        }

        if (id.StartsWith("uscript.", StringComparison.Ordinal))
        {
            var scriptId = id["uscript.".Length..];
            var script = main.UserScripts.FirstOrDefault(item => item.Id == scriptId);
            if (script is null)
            {
                return null;
            }

            // Дубликат привязан к оригиналу UserScriptCard: IsRunning/ResultText
            // обновляются там же, где и в полосе раздела.
            var card = new UserScriptCard { DataContext = script };
            card.RunRequested += async (_, _) =>
            {
                // RunUserScriptAsync гасит исключения внутри (диалог + запуск под try).
                await main.RunUserScriptAsync(script);
            };
            return card;
        }

        if (id.StartsWith("input.", StringComparison.Ordinal))
        {
            return CreateInputCard(cardId, main.Input);
        }

        return null;
    }

    // Тумблерная карточка ввода («input.<rowId>»): internal — сеам для тестов
    // обёртки, там не нужен MainViewModel. null — строки с таким id нет.
    internal static FrameworkElement? CreateInputCard(string cardId, InputViewModel input)
    {
        var rowId = cardId["input.".Length..];
        var row = input.Rows.FirstOrDefault(r => r.Id == rowId);
        return row is null ? null : new PinnedSwitchCardHost(input, row);
    }

    // Обёртка-UserControl для дубликата тумблерной карточки ввода: стиль
    // SwitchRowCard берёт команды раздела у ближайшего UserControl
    // (DataContext.IsInteractive / ToggleSwitchCommand), поэтому контекст
    // обёртки — InputViewModel, а у самой карточки — её SwitchRow.
    private sealed class PinnedSwitchCardHost : UserControl
    {
        public PinnedSwitchCardHost(InputViewModel input, SwitchRow row)
        {
            DataContext = input;
            var card = new ContentControl { DataContext = row };
            card.SetResourceReference(StyleProperty, "SwitchRowCard");
            PinCard.SetCardId(card, row.PinCardId);
            Content = card;
        }
    }
}
