using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.AppCore.AI;
using SCU.Infrastructure.DeepSeek;
using SCU.Models.AI;

namespace SCU.ViewModels.Sections;

// Окно «SCU AI Assistant» (раздел 24): агентный чат с карточками. Обычный чат
// DeepSeek не трогается (п. 18/27 ТЗ) — это отдельный режим: текст + cards.
//
// Сообщения — ScuAiChatMessage, UI рендерит карточки по Kind, а не по тексту
// модели (п. 31/32 ТЗ: кнопки рисует приложение по валидированным данным).
// API-ключ не хранится в сообщениях и не уходит в историю чата (п. 23 ТЗ):
// он берётся у DeepSeekChatViewModel на каждый ход и используется только для
// авторизации HTTP-запроса.
public partial class ScuAiAssistantViewModel : ObservableObject
{
    private readonly Logger _logger;
    private readonly ScuAiAssistant _assistant;
    private readonly DeepSeekChatViewModel _chat;

    // История текущего хода в формате API (system | user | assistant | tool):
    // tool-сообщения не показываются в чате, но нужны для продолжения диалога.
    private readonly List<ScuAiAgentMessage> _agentHistory = [];

    // Токен отмены текущего хода: «Стоп» прерывает и API-запрос, и tools.
    private CancellationTokenSource? _turnCancellation;

    // Соединение на каждый ход: к этому моменту раздел уже «подключён» —
    // ключ берётся уVM чата, в сообщения и историю он не пишется.
    private string? _apiKey;
    private string? _model;
    private DeepSeekProviderConfig? _provider;

    public ScuAiAssistantViewModel(Logger logger, ScuAiAssistant assistant, DeepSeekChatViewModel chat)
    {
        _logger = logger;
        _assistant = assistant;
        _chat = chat;
    }

    public ObservableCollection<ScuAiChatMessage> Messages { get; } = [];

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isWaiting;

    // Режим доступен только при реально подключённом API (п. 19 ТЗ):
    // fake connected state не показываем.
    [ObservableProperty]
    private bool _isApiConnected;

    // Подсказка для пустого режима: провайдер без tool calling работает только
    // как текстовый помощник по справке SCU (п. 26 ТЗ).
    [ObservableProperty]
    private string _modeHint = string.Empty;

    // Контекст текущего раздела передаётся в assistant на каждом ходе (п. 19):
    // «когда пользователь открыл AI из конкретного раздела — контекст автоматом».
    public void Configure(string apiKey, string model, DeepSeekProviderConfig provider)
    {
        _apiKey = apiKey;
        _model = model;
        _provider = provider;
        IsApiConnected = !string.IsNullOrEmpty(apiKey);
        UpdateModeHint();
    }

    private void UpdateModeHint()
    {
        if (_provider is null)
        {
            ModeHint = string.Empty;
            return;
        }

        ModeHint = DeepSeekClient.ProviderSupportsToolCalling(_provider)
            ? L.T("Полный режим: ответы по справке SCU, чтение состояния, навигация и изменение настроек с подтверждением.")
            : L.T("Этот провайдер не поддерживает вызов функций: ответы по справке и текстовые подсказки. Изменения настроек недоступны.");
    }

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnIsApiConnectedChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    private bool CanSend() =>
        IsApiConnected && !IsWaiting && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_apiKey) || _model is null || _provider is null)
        {
            return;
        }

        var question = InputText.Trim();
        InputText = string.Empty;

        Messages.Add(new ScuAiChatMessage("user", question, ScuAiMessageKind.Text));

        _turnCancellation?.Cancel();
        _turnCancellation = new CancellationTokenSource();

        IsWaiting = true;
        try
        {
            var turn = await _assistant
                .AskAsync(_apiKey, _model, _provider, question, _agentHistory,
                    _turnCancellation.Token)
                .ConfigureAwait(true);

            AddTurnMessages(turn);
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new ScuAiChatMessage("assistant", L.T("Запрос отменён."), ScuAiMessageKind.ToolError));
        }
        catch (Exception exception)
        {
            // Сбой хода не роняет окно: пользователь видит причину локально.
            _logger.Error("SCU_AI | turn failed | " + exception.Message);
            Messages.Add(new ScuAiChatMessage(
                "assistant", L.T("Сбой запроса к AI: {0}", exception.Message), ScuAiMessageKind.ToolError));
        }
        finally
        {
            IsWaiting = false;
        }
    }

    // «Применить» в карточке подтверждения (п. 9.5): проверить PlanId, выполнить
    // существующую команду SCU, перечитать состояние, отдать модели результат.
    [RelayCommand]
    private async Task ApplyPlanAsync(string planId)
    {
        if (string.IsNullOrEmpty(_apiKey) || _model is null || _provider is null)
        {
            return;
        }

        _turnCancellation?.Cancel();
        _turnCancellation = new CancellationTokenSource();

        IsWaiting = true;
        try
        {
            var turn = await _assistant
                .ApplyConfirmedPlanAsync(_apiKey, _model, _provider, planId, _agentHistory,
                    _turnCancellation.Token)
                .ConfigureAwait(true);

            // Карточка помечается применённой только по факту успеха: при ошибке
            // (состояние изменилось, операция не удалась) план ещё жив и кнопки
            // остаются доступны (п. 33 ТЗ — ошибка не маскируется успехом).
            if (!turn.IsError)
            {
                MarkPlanCard(planId, applied: true);
            }

            AddTurnMessages(turn);
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new ScuAiChatMessage("assistant", L.T("Применение отменено."), ScuAiMessageKind.ToolError));
        }
        catch (Exception exception)
        {
            _logger.Error("SCU_AI | apply failed | " + exception.Message);
            Messages.Add(new ScuAiChatMessage(
                "assistant", L.T("Не удалось применить изменение: {0}", exception.Message), ScuAiMessageKind.ToolError));
        }
        finally
        {
            IsWaiting = false;
        }
    }

    // «Отмена» в карточке подтверждения: план удаляется из store — модель не
    // сможет его применить, даже если попытается (п. 9 ТЗ).
    [RelayCommand]
    private void CancelPlan(string planId)
    {
        MarkPlanCard(planId, applied: false, cancelled: true);
        _assistant.CancelPlan(planId);
        Messages.Add(new ScuAiChatMessage(
            "assistant", L.T("Изменение отменено — настройки не затронуты."), ScuAiMessageKind.Text));
    }

    // Превращение результата хода в сообщения чата: карточки — до текста, чтобы
    // пользователь сначала увидел план/результат, а потом пояснение модели.
    private void AddTurnMessages(ScuAiAssistant.TurnResult turn)
    {
        foreach (var failure in turn.Failures)
        {
            Messages.Add(new ScuAiChatMessage(
                "assistant",
                L.T("Ошибка «{0}»: {1}", failure.ToolName, failure.Message),
                ScuAiMessageKind.ToolError));
        }

        foreach (var reference in turn.HelpReferences)
        {
            var message = new ScuAiChatMessage(
                "assistant",
                L.T("Нашёл в справке SCU:"),
                ScuAiMessageKind.HelpReference);
            message.HelpReferences.Add(reference);
            Messages.Add(message);
        }

        foreach (var plan in turn.Plans)
        {
            var message = new ScuAiChatMessage(
                "assistant", plan.Summary, ScuAiMessageKind.ActionPlan)
            {
                ActionPlan = plan,
            };
            Messages.Add(message);
        }

        foreach (var result in turn.Results)
        {
            var message = new ScuAiChatMessage(
                "assistant",
                L.T("Применено: {0}", result.UtilityTitle),
                ScuAiMessageKind.ActionResult)
            {
                BeforeState = result.BeforeState,
                AfterState = result.AfterState,
            };
            Messages.Add(message);
        }

        if (!string.IsNullOrWhiteSpace(turn.Text))
        {
            Messages.Add(new ScuAiChatMessage("assistant", turn.Text,
                turn.IsError ? ScuAiMessageKind.ToolError : ScuAiMessageKind.Text));
        }
    }

    // Карточка плана теряет кнопки после любого исхода: план одноразовый.
    private void MarkPlanCard(string planId, bool applied = false, bool cancelled = false)
    {
        var card = Messages.FirstOrDefault(message => message.ActionPlan?.PlanId == planId);
        if (card is null)
        {
            return;
        }

        card.IsPlanApplied = applied;
        card.IsPlanCancelled = cancelled;
    }

    // Остановка текущего хода (п. 25 ТЗ): отмена распространяется на API-запрос
    // и на выполняющийся tool.
    [RelayCommand]
    private void Stop()
    {
        _turnCancellation?.Cancel();
    }

    // Клик по ссылке на внутреннюю справку (п. 17/30 ТЗ): открывает раздел SCU
    // и подсвечивает строку утилиты существующим механизмом навигации.
    [RelayCommand]
    private void OpenHelp(ScuAiHelpReference reference) => _assistant.OpenHelpReference(reference);
}
