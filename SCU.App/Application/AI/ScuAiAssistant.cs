using System.Text.Json;
using SCU.AppCore.Help;
using SCU.Infrastructure.DeepSeek;
using SCU.Models.AI;

namespace SCU.AppCore.AI;

// Оркестратор агентного цикла (п. 12 ТЗ). Один экземпляр на приложение (п. 29):
// держит клиент, registry tools и хранилище планов; история конкретного диалога
// принадлежит вызывающей ViewModel, а не сюда.
//
// Цикл: messages + system + context → ответ модели → если tool_calls, валидация
// и выполнение tools на стороне приложения → результат обратно в модель → …
// Иначе — финальный текст. Лимит итераций (ScuAiPolicy.MaxAgentIterations)
// защищает от бесконечного цикла; каждая итерация идёт под внешним токеном отмены.
//
// API-ключ передаётся вызывающим кодом и используется только для авторизации
// HTTP-запроса: в историю, в контекст и в логи он не попадает (п. 23/34 ТЗ).
public sealed class ScuAiAssistant
{
    private readonly Logger _logger;
    private readonly IScuAiAgentStepClient _client;
    private readonly ScuAiToolRegistry _tools;
    private readonly IScuAiContextBuilder _contextBuilder;
    private readonly ScuAiPlanExecutor _plans;
    private readonly IScuAiEnvironment _environment;
    private readonly IScuHelpService _help;

    // Не более одной AI-операции одновременно: пользователь не может запустить
    // второй ход, пока первый ещё крутится (п. 25 ТЗ — concurrency).
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    public ScuAiAssistant(
        Logger logger,
        IScuAiAgentStepClient client,
        ScuAiToolRegistry tools,
        IScuAiContextBuilder contextBuilder,
        ScuAiPlanExecutor plans,
        IScuAiEnvironment environment,
        IScuHelpService help)
    {
        _logger = logger;
        _client = client;
        _tools = tools;
        _contextBuilder = contextBuilder;
        _plans = plans;
        _environment = environment;
        _help = help;
    }

    // Открытие ссылки на внутреннюю справку (п. 17/30 ТЗ): навигация — через
    // существующий механизм MainViewModel.SelectSectionByNumber + подсветка
    // строки утилиты. Координат/кликов от модели нет — только номер раздела
    // и заголовок, валидированные приложением.
    public void OpenHelpReference(ScuAiHelpReference reference)
    {
        if (reference.SectionNumber <= 0)
        {
            return;
        }

        _environment.NavigateToSection(reference.SectionNumber);
        _environment.HighlightUtility(reference.SectionNumber, reference.Title);
    }

    // Результат одного хода: финальный текст модели + UI-события, собранные
    // выполненными tools (карточки справки, планы, результаты, ошибки).
    public sealed record TurnResult
    {
        public required string Text { get; init; }

        public bool IsError { get; init; }

        public IReadOnlyList<ScuAiHelpReference> HelpReferences { get; init; } = [];

        public IReadOnlyList<ScuAiActionPlanSnapshot> Plans { get; init; } = [];

        public IReadOnlyList<ScuAiActionResultSnapshot> Results { get; init; } = [];

        public IReadOnlyList<ScuAiToolFailure> Failures { get; init; } = [];

        public static TurnResult Error(string text) => new() { Text = text, IsError = true };
    }

    // Вопрос пользователя: локальный scope guard → агентный цикл → финальный текст.
    // history — диалог этой сессии в формате API; метод добавляет в него и
    // сообщение пользователя, и ответы модели/tool (п. 24 ТЗ).
    public async Task<TurnResult> AskAsync(
        string apiKey,
        string model,
        DeepSeekProviderConfig provider,
        string question,
        List<ScuAiAgentMessage> history,
        CancellationToken cancellationToken)
    {
        // Локальная эвристика (п. 15 ТЗ): явный out-of-scope не уходит на API —
        // отвечаем canned-текстом. Остальные намерения определяет модель с tools.
        var intent = ScuAiPolicy.ClassifyIntent(question, query => _help.Search(query));
        if (intent == ScuAiIntent.Unsupported)
        {
            _logger.Info("SCU_AI | scope | out-of-scope query answered locally");
            return new TurnResult { Text = L.T(ScuAiPolicy.OutOfScopeMessage) };
        }

        history.Add(ScuAiAgentMessage.User(question));

        return await RunLoopAsync(apiKey, model, provider, history, cancellationToken)
            .ConfigureAwait(true);
    }

    // Пользователь подтвердил план в карточке (п. 9.5): проверяем жизнь плана,
    // подтверждение и целостность состояния, выполняем существующую команду SCU,
    // перечитываем состояние — и отдаём модели реальный результат (п. 9.9).
    public async Task<TurnResult> ApplyConfirmedPlanAsync(
        string apiKey,
        string model,
        DeepSeekProviderConfig provider,
        string planId,
        List<ScuAiAgentMessage> history,
        CancellationToken cancellationToken)
    {
        // Нажатие «Применить» — это и есть подтверждение пользователя (п. 9.5):
        // план помечается подтверждённым, и только потом исполняется. Подтверждение
        // относится к конкретному PlanId, а не к тексту модели.
        if (!_plans.Confirm(planId))
        {
            return TurnResult.Error(L.T("Изменение не найдено или истекло — подготовьте его заново."));
        }

        var outcome = await _plans.ApplyAsync(planId, cancellationToken).ConfigureAwait(true);
        if (!outcome.Success || outcome.Result is not { } result)
        {
            return TurnResult.Error(outcome.Message);
        }

        // Результат идёт в историю как tool-сообщение: модель продолжает диалог,
        // зная реальный итог операции (п. 13 ТЗ — {success, changed, before, after}).
        history.Add(ScuAiAgentMessage.Tool("plan_" + planId, "apply_scu_change", JsonSerializer.Serialize(new
        {
            success = true,
            changed = true,
            plan_id = planId,
            utility = result.UtilityTitle,
            before = result.BeforeState,
            after = result.AfterState,
            status = result.Status,
        })));

        _logger.Info($"SCU_AI | apply ui | plan={planId} | continuing turn");
        var continuation = await RunLoopAsync(apiKey, model, provider, history, cancellationToken)
            .ConfigureAwait(true);

        // Карточку результата показываем даже если продолжение не удалось.
        return new TurnResult
        {
            Text = continuation.Text,
            IsError = continuation.IsError,
            HelpReferences = continuation.HelpReferences,
            Plans = continuation.Plans,
            Results = [result, .. continuation.Results],
            Failures = continuation.Failures,
        };
    }

    // Отмена плана пользователем (п. 9 ТЗ): план удаляется из store — повторно
    // применить его уже невозможно, а модель получает отказ при попытке.
    public void CancelPlan(string planId)
    {
        _plans.Cancel(planId);
    }

    // ===================== Внутреннее =====================

    // Агентный цикл с лимитом итераций: модель → tools → модель. Каждая итерация
    // выполняется только под внешним токеном отмены (п. 25 ТЗ).
    private async Task<TurnResult> RunLoopAsync(
        string apiKey,
        string model,
        DeepSeekProviderConfig provider,
        List<ScuAiAgentMessage> history,
        CancellationToken cancellationToken)
    {
        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var context = _contextBuilder.Build();
            var systemPrompt = BuildSystemPrompt(context);
            var schemas = _tools.GetSchemas(context.SupportsToolCalling);

            // Один контекст выполнения на весь цикл: карточки, собранные tools на
            // любой итерации (план, справка, результат, ошибка), доезжают до UI
            // вместе с финальным ответом, а не только с последнего шага.
            var executionContext = new ScuAiExecutionContext
            {
                Context = context,
                CancellationToken = cancellationToken,
            };

            for (var iteration = 1; iteration <= ScuAiPolicy.MaxAgentIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = await _client
                    .SendAgentStepAsync(apiKey, model, systemPrompt, history, schemas, provider,
                        context.SupportsToolCalling, cancellationToken)
                    .ConfigureAwait(true);

                if (!step.IsSuccess)
                {
                    var failure = ToTurnResult(L.T(step.Error, step.ErrorArgs), executionContext);
                    return failure with { IsError = true };
                }

                // Финальный ответ — цикл закончен. Карточки от earlier-итераций
                // идут вместе с текстом.
                if (!step.HasToolCalls)
                {
                    if (!string.IsNullOrWhiteSpace(step.Content))
                    {
                        history.Add(ScuAiAgentMessage.Assistant(step.Content, null));
                    }

                    return ToTurnResult(step.Content, executionContext);
                }

                // Шаг с tool_calls: assistant-сообщение (с вызовами) остаётся в
                // истории — провайдер требует его для продолжения диалога.
                history.Add(ScuAiAgentMessage.Assistant(step.Content, step.ToolCalls));

                foreach (var call in step.ToolCalls)
                {
                    var result = await _tools
                        .ExecuteAsync(call.Name, call.ArgumentsJson, executionContext)
                        .ConfigureAwait(true);
                    history.Add(ScuAiAgentMessage.Tool(call.Id, call.Name, result.ToModelJson()));
                }
            }

            _logger.Warn($"SCU_AI | iteration limit reached ({ScuAiPolicy.MaxAgentIterations})");
            var limit = ToTurnResult(L.T(
                "Превышен лимит шагов AI-операции — попробуйте переформулировать запрос или разбить его на части."),
                executionContext);
            return limit with { IsError = true };
        }
        finally
        {
            _turnLock.Release();
        }
    }

    // Системный промпт + контекст: модель знает, где находится пользователь
    // (раздел, функция, права, провайдер/модель), но не секреты (п. 11 ТЗ).
    private static string BuildSystemPrompt(ScuAiContext context) =>
        ScuAiPolicy.SystemPrompt + "\n\nКонтекст пользователя (JSON):\n" + JsonSerializer.Serialize(new
        {
            application = context.Application,
            version = context.Version,
            language = context.Language,
            current_section = context.CurrentSectionNumber is { } section
                ? new { number = section, title = context.CurrentSectionTitle }
                : null,
            current_utility = context.CurrentUtilityId is { Length: > 0 }
                ? new { id = context.CurrentUtilityId, title = context.CurrentUtilityTitle }
                : null,
            is_admin = context.IsAdmin,
            provider = context.ProviderId,
            model = context.Model,
            supports_tool_calling = context.SupportsToolCalling,
        });

    private static TurnResult ToTurnResult(string text, ScuAiExecutionContext context) => new()
    {
        Text = text,
        HelpReferences = context.HelpReferences,
        Plans = context.Plans,
        Results = context.Results,
        Failures = context.Failures,
    };
}
