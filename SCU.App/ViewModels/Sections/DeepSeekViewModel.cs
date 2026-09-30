using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SCU.AppCore.AI;
using SCU.Common;
using SCU.Infrastructure.DeepSeek;
using SCU.Models.AI;
using SCU.Models.DeepSeek;

namespace SCU.ViewModels.Sections;

// Состояние пунктирного контура вокруг поля ввода API-ключа (слушает DeepSeekView):
// None — контур снят; Valid — зелёный статичный; Invalid — красный с анимацией.
public enum DeepSeekKeyOutlineState
{
    None,
    Valid,
    Invalid,
}

// Раздел 24 «DeepSeek»: карточка подключения по API + карточка чата.
// Провайдер выбирается в карточке (DeepSeek API / OpenRouter с бесплатными
// моделями); ключ проверяется в два шага: формат — локально на лету,
// реальная работоспособность — запросом к API провайдера по «Подключить».
public partial class DeepSeekViewModel : ObservableObject
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly DeepSeekClient _client;
    private readonly DeepSeekSettingsService _settingsService;

    // Кэш загруженных настроек: переключение провайдера и подключение
    // мутируют его и сохраняют целиком.
    private DeepSeekSettingsModel _settings = new();

    private bool _initializing = true;

    [ObservableProperty]
    private int _providerIndex;

    [ObservableProperty]
    private string _apiKeyText = string.Empty;

    // Account ID Cloudflare (нужен только для провайдера Cloudflare).
    [ObservableProperty]
    private string _accountIdText = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private DeepSeekKeyOutlineState _keyOutlineState;

    public DeepSeekChatViewModel Chat { get; }

    // Окно «SCU AI Assistant»: агентный режим с tools и карточками (п. 27 ТЗ).
    // Создаётся после AI-графа MainViewModel — assistant приходит снаружи,
    // чтобы остался один экземпляр на приложение (п. 29 ТЗ).
    private ScuAiAssistantViewModel? _assistantVm;

    // Открытие окна чата (слушает DeepSeekView: создаёт и показывает окно).
    public event Action? OpenChatRequested;

    // Открытие окна AI-помощника (слушает DeepSeekView).
    public event Action? OpenAiAssistantRequested;

    public DeepSeekViewModel(
        Logger logger,
        IConfirmDialogService dialogs,
        DeepSeekClient client,
        DeepSeekSettingsService settingsService,
        DeepSeekChatsService chatsService,
        ScuAiAssistant? assistant = null)
    {
        _logger = logger;
        _dialogs = dialogs;
        _client = client;
        _settingsService = settingsService;

        Chat = new DeepSeekChatViewModel(logger, dialogs, client, settingsService, chatsService);

        if (assistant is not null)
        {
            _assistantVm = new ScuAiAssistantViewModel(logger, assistant, Chat);
        }

        _settings = settingsService.Load();
        ProviderIndex = _settings.ActiveProvider switch
        {
            "openrouter" => 1,
            "cloudflare" => 2,
            _ => 0,
        };
        AccountIdText = _settings.CloudflareAccountId;
        ApplyProviderState();
        _initializing = false;
    }

    // AI-граф собран в MainViewModel после создания этого раздела: VM помощника
    // привязывается сюда, соединение (без ключа в сообщения — п. 23 ТЗ) у общее.
    public void AttachAssistant(ScuAiAssistant assistant)
    {
        _assistantVm = new ScuAiAssistantViewModel(_logger, assistant, Chat);
        ApplyConnectionToAssistant();
    }

    // Конфиг активного провайдера (маршрутизация запросов и формат ключа).
    // Для Cloudflare Account ID берётся из сохранённых настроек, пока раздел
    // в состоянии «подключено», и из поля ввода — пока идёт подключение.
    public DeepSeekProviderConfig ActiveProviderConfig => ProviderIndex switch
    {
        1 => DeepSeekProviderConfig.OpenRouter,
        2 => DeepSeekProviderConfig.Cloudflare(ResolvedCloudflareAccountId),
        _ => DeepSeekProviderConfig.DeepSeek,
    };

    private string ResolvedCloudflareAccountId =>
        IsConnected ? _settings.CloudflareAccountId : AccountIdText.Trim();

    private bool IsCloudflareAccountIdEntered =>
        ProviderIndex != 2 || DeepSeekClient.IsCloudflareAccountIdValid(ResolvedCloudflareAccountId);

    // Локальная проверка формата ключа активного провайдера.
    public bool IsKeyFormatValid => DeepSeekClient.IsKeyFormatValid(ApiKeyText, ActiveProviderConfig);

    // Переключение провайдера: каждый хранит свой ключ — состояние раздела
    // пересчитывается, настройки сохраняются.
    partial void OnProviderIndexChanged(int value)
    {
        if (_initializing)
        {
            return;
        }

        _settings.ActiveProvider = value switch
        {
            1 => "openrouter",
            2 => "cloudflare",
            _ => "deepseek",
        };
        _settingsService.Save(_settings);
        _logger.Info("DEEPSEEK | provider switched | " + _settings.ActiveProvider);
        ApiKeyText = string.Empty;
        AccountIdText = _settings.GetActiveAccountId();
        ApplyProviderState();
    }

    partial void OnAccountIdTextChanged(string value) => ConnectCommand.NotifyCanExecuteChanged();

    // Пересчёт состояния раздела под активного провайдера: подключение,
    // модель чата, поле ввода, контур.
    private void ApplyProviderState()
    {
        var (apiKey, chatModel) = _settings.GetActive();
        IsConnected = !string.IsNullOrEmpty(apiKey);
        if (IsConnected)
        {
            Chat.Configure(apiKey, chatModel, ActiveProviderConfig);
            StatusText = L.T("Подключено. Модель чата: {0}", chatModel);
            KeyOutlineState = DeepSeekKeyOutlineState.None;
        }
        else
        {
            Chat.Configure(string.Empty, string.Empty, ActiveProviderConfig);
            StatusText = string.Empty;
            KeyOutlineState = string.IsNullOrEmpty(ApiKeyText)
                ? DeepSeekKeyOutlineState.None
                : IsKeyFormatValid
                    ? DeepSeekKeyOutlineState.Valid
                    : DeepSeekKeyOutlineState.Invalid;
        }

        ApplyConnectionToAssistant();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    // Соединение передаётся в VM помощника тем же вызовом, что и в чат: ключ
    // используется только для авторизации запроса и не попадает в сообщения.
    private void ApplyConnectionToAssistant()
    {
        if (_assistantVm is null)
        {
            return;
        }

        var (apiKey, chatModel) = _settings.GetActive();
        _assistantVm.Configure(apiKey, chatModel, ActiveProviderConfig);
    }

    partial void OnApiKeyTextChanged(string value)
    {
        // «Удалить» не трогаем поле: контур управляется только в состоянии без ключа.
        if (!IsConnected)
        {
            KeyOutlineState = string.IsNullOrEmpty(value)
                ? DeepSeekKeyOutlineState.None
                : IsKeyFormatValid
                    ? DeepSeekKeyOutlineState.Valid
                    : DeepSeekKeyOutlineState.Invalid;
        }

        ConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectedChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();

    partial void OnIsConnectingChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();

    private bool CanConnect() =>
        IsKeyFormatValid && IsCloudflareAccountIdEntered && !IsConnected && !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        // Формат уже зелёный: на время реальной проверки контур убирается.
        KeyOutlineState = DeepSeekKeyOutlineState.None;
        IsConnecting = true;
        StatusText = L.T("Проверка ключа API на сервере…");
        try
        {
            var check = await _client
                .ValidateKeyAsync(ApiKeyText.Trim(), ActiveProviderConfig, cancellationToken)
                .ConfigureAwait(true);
            if (check.IsValid)
            {
                var model = DeepSeekClient.PickChatModel(check.Models, ActiveProviderConfig);
                _settings.SetActive(ApiKeyText.Trim(), model, AccountIdText.Trim());
                _settingsService.Save(_settings);
                IsConnected = true;
                Chat.Configure(ApiKeyText.Trim(), model, ActiveProviderConfig);
                // Помощник получает соединение тем же ходом, что и чат: иначе
                // подключение после запуска приложения до него не доходит.
                ApplyConnectionToAssistant();
                StatusText = L.T("Подключено. Модель чата: {0}", model);
                _logger.Info($"DEEPSEEK | connected | provider={ActiveProviderConfig.Id} | model={model}");
            }
            else
            {
                StatusText = L.T(check.Error, check.ErrorArgs);
                // Формат ключа не менялся — зелёный пунктир возвращается.
                KeyOutlineState = DeepSeekKeyOutlineState.Valid;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = L.T("Проверка ключа отменена.");
            _logger.Info("DEEPSEEK | key validation cancelled");
            KeyOutlineState = DeepSeekKeyOutlineState.Valid;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private void RemoveKey()
    {
        if (!_dialogs.Ask(
                L.T("AI"),
                L.T("Удалить сохранённый API-ключ? История чатов останется, но чат потребует повторного подключения."),
                L.T("Удалить")))
        {
            return;
        }

        _settingsService.ClearActive(_settings);
        ApiKeyText = string.Empty;
        ApplyProviderState();
        StatusText = L.T("API-ключ удалён.");
    }

    [RelayCommand]
    private void OpenChat()
    {
        if (!IsConnected)
        {
            return;
        }

        OpenChatRequested?.Invoke();
    }

    // Открытие SCU AI Assistant (п. 27 ТЗ): агентный режим с tools. Доступен
    // только при реально подключённом API — без подключения окно показывает
    // понятное состояние, а не fake UI (п. 19 ТЗ).
    [RelayCommand]
    private void OpenAiAssistant()
    {
        if (!IsConnected)
        {
            return;
        }

        OpenAiAssistantRequested?.Invoke();
    }

    // VM помощника для окна: null, если AI-граф ещё не собран.
    public ScuAiAssistantViewModel? Assistant => _assistantVm;
}

// Элемент списка истории чатов: обёртка с наблюдаемым заголовком (меняется,
// когда первый вопрос становится названием чата).
public partial class DeepSeekChatSessionItem : ObservableObject
{
    public DeepSeekChatSessionItem(DeepSeekChatSession session)
    {
        Session = session;
        Messages = [.. session.Messages];
    }

    public DeepSeekChatSession Session { get; }

    public string Id => Session.Id;

    public ObservableCollection<DeepSeekChatMessage> Messages { get; }

    public string Title => string.IsNullOrEmpty(Session.Title)
        ? L.T("Новый чат")
        : Session.Title;

    public string CreatedText => Session.CreatedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

// Окно чата (раздел 24): переписка, история, добавление/удаление чатов.
// Отвечает целиком после ожидания (не-стриминговый режим API).
public partial class DeepSeekChatViewModel : ObservableObject
{
    private readonly Logger _logger;
    private readonly IConfirmDialogService _dialogs;
    private readonly DeepSeekClient _client;
    private readonly DeepSeekSettingsService _settingsService;
    private readonly DeepSeekChatsService _chatsService;

    // Ключ, модель и провайдер передаются родительским DeepSeekViewModel
    // (при старте, подключении и переключении) — без чтения settings.json на ввод.
    private string _apiKey = string.Empty;
    private string _model = string.Empty;
    private DeepSeekProviderConfig _provider = DeepSeekProviderConfig.DeepSeek;

    // Модель активного подключения для контекста AI (без ключа — секреты
    // в контекст не попадают, п. 23 ТЗ).
    public string ActiveModel => _model;

    // Провайдер активного подключения (для capability detection tool calling).
    public DeepSeekProviderConfig ActiveProvider => _provider;

    public DeepSeekChatViewModel(
        Logger logger,
        IConfirmDialogService dialogs,
        DeepSeekClient client,
        DeepSeekSettingsService settingsService,
        DeepSeekChatsService chatsService)
    {
        _logger = logger;
        _dialogs = dialogs;
        _client = client;
        _settingsService = settingsService;
        _chatsService = chatsService;

        Sessions =
        [
            .. chatsService.Sessions.Select(session => new DeepSeekChatSessionItem(session)),
        ];
        ActiveSession = Sessions.FirstOrDefault();
    }

    public ObservableCollection<DeepSeekChatSessionItem> Sessions { get; }

    [ObservableProperty]
    private DeepSeekChatSessionItem? _activeSession;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isWaiting;

    partial void OnActiveSessionChanged(DeepSeekChatSessionItem? value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnIsWaitingChanged(bool value) => SendCommand.NotifyCanExecuteChanged();

    public void Configure(string apiKey, string model, DeepSeekProviderConfig provider)
    {
        _apiKey = apiKey;
        _model = model;
        _provider = provider;
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NewChat()
    {
        var session = new DeepSeekChatSession();
        var item = new DeepSeekChatSessionItem(session);
        Sessions.Insert(0, item);
        ActiveSession = item;
        _logger.Info("DEEPSEEK | new chat created");
    }

    [RelayCommand]
    private void DeleteSession(DeepSeekChatSessionItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!_dialogs.Ask(
                L.T("Удалить чат"),
                L.T("Удалить чат «{0}» из истории? Восстановить его будет нельзя.", item.Title),
                L.T("Удалить")))
        {
            return;
        }

        Sessions.Remove(item);
        _chatsService.Remove(item.Id);
        _logger.Info("DEEPSEEK | chat deleted | " + item.Id);

        if (ReferenceEquals(ActiveSession, item))
        {
            ActiveSession = Sessions.FirstOrDefault();
        }
    }

    // Остановка генерации: отменяет текущий AsyncRelayCommand-запрос (как в ScuAiAssistant).
    [RelayCommand]
    private void Stop() => SendCommand.Cancel();

    private bool CanSend() =>
        !IsWaiting
        && !string.IsNullOrWhiteSpace(InputText)
        && ActiveSession is not null
        && !string.IsNullOrEmpty(_apiKey);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_apiKey) || ActiveSession is not { } sessionItem)
        {
            return;
        }

        var question = InputText.Trim();
        InputText = string.Empty;

        var session = sessionItem.Session;
        var userMessage = new DeepSeekChatMessage { Role = "user", Content = question };
        session.Messages.Add(userMessage);
        sessionItem.Messages.Add(userMessage);

        // Первый вопрос становится названием чата (сохраняется и в истории).
        if (string.IsNullOrEmpty(session.Title))
        {
            session.Title = question.Length <= 40 ? question : question[..40].TrimEnd() + "…";
            sessionItem.RefreshTitle();
            Sessions.Move(Sessions.IndexOf(sessionItem), 0);
        }

        IsWaiting = true;
        try
        {
            var reply = await _client
                .SendChatAsync(_apiKey, _model, session.Messages, _provider, cancellationToken)
                .ConfigureAwait(true);

            var assistantMessage = reply.IsSuccess
                ? new DeepSeekChatMessage { Role = "assistant", Content = reply.Content }
                : new DeepSeekChatMessage { Role = "assistant", Content = L.T(reply.Error, reply.ErrorArgs), IsError = true };
            session.Messages.Add(assistantMessage);
            sessionItem.Messages.Add(assistantMessage);
        }
        catch (OperationCanceledException)
        {
            var cancelMessage = new DeepSeekChatMessage
            {
                Role = "assistant",
                Content = L.T("Запрос отменён."),
                IsError = true,
            };
            session.Messages.Add(cancelMessage);
            sessionItem.Messages.Add(cancelMessage);
        }
        finally
        {
            // Пустой чат не пишем: история — только с реальной перепиской.
            if (session.Messages.Count > 0)
            {
                _chatsService.SaveSession(session);
            }

            IsWaiting = false;
        }
    }
}
