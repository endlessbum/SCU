using System.ComponentModel;
using SCU.Infrastructure.Logging;
using SCU.Infrastructure.Storage;

namespace SCU.Common;

// Состояние закрепления карточек на главной («скрепка» на карточках разделов).
// Singleton: карточки в разделах и область закреплённых на главной должны видеть
// одно и то же состояние. Скрепки (PinGlyph) синхронизируются вручную через
// CardPinChanged → SyncChecked и читают индексатор this[cardId]; XAML-биндингов
// по индексатору нет. Персистится в %AppData%\SCU\state\pinned-cards.json
// (PinnedCardsStore); если хранилище недоступно (диск, лог) — работа
// продолжается в памяти.
public sealed class PinState : INotifyPropertyChanged
{
    public static PinState Instance { get; } = new();

    private readonly object _gate = new();
    // Список, а не множество: область на «Главной» показывает карточки в
    // порядке закрепления. Сравнение идентификаторов — OrdinalIgnoreCase.
    private readonly List<string> _pinned = [];
    private PinnedCardsStore? _store;
    private bool _loaded;

    // Читающий API для глифов (PinGlyph.SyncChecked); подписки на XAML нет.
    public bool this[string cardId]
    {
        get
        {
            lock (_gate)
            {
                return IndexOf(cardId) >= 0;
            }
        }
    }

    public IReadOnlyList<string> PinnedIds
    {
        get
        {
            lock (_gate)
            {
                return _pinned.ToList();
            }
        }
    }

    // Изменение набора закреплённых целиком (для пересборки области на главной).
    public event Action? Changed;

    // Изменение одной карточки (для синхронизации скрепок одного id в разных местах).
    public event Action<string>? CardPinChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Гарантирует чтение pinned-cards.json при старте (вызывается один раз с UI-потока,
    // до первой отрисовки области закреплённых). Геттер индексатора сознательно не
    // читает диск — он вызывается на каждый рендер скрепки.
    public void EnsureLoaded()
    {
        _ = Store;
    }

    public void Set(string cardId, bool pinned)
    {
        List<string>? snapshot;
        lock (_gate)
        {
            var index = IndexOf(cardId);
            if (pinned == (index >= 0))
            {
                // Состояние не изменилось, но визуал глифа мог разойтись с ним:
                // принудительно пересинхронизируем все скрепки этого id.
                CardPinChanged?.Invoke(cardId);
                return;
            }

            if (pinned)
            {
                _pinned.Add(cardId);
            }
            else
            {
                _pinned.RemoveAt(index);
            }

            snapshot = _pinned.ToList();
        }

        Save(snapshot);
        CardPinChanged?.Invoke(cardId);
        OnItemsChanged();
    }

    // Загрузка другого набора (тесты/восстановление); в стор не пишется.
    public void Replace(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            _pinned.Clear();
            foreach (var id in ids)
            {
                if (IndexOf(id) < 0)
                {
                    _pinned.Add(id);
                }
            }
        }

        OnItemsChanged();
    }

    // Поиск без аллокаций: сравнение регистронезависимое, как раньше у HashSet.
    private int IndexOf(string cardId)
    {
        for (var i = 0; i < _pinned.Count; i++)
        {
            if (string.Equals(_pinned[i], cardId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // Тестовый крюк: подмена фабрики хранилища (временный файл вместо %AppData%).
    internal Func<PinnedCardsStore>? StoreFactory { get; set; }

    // Тестовый крюк: полный сброс singleton (набор, кэш хранилища).
    internal void ResetForTests()
    {
        lock (_gate)
        {
            _pinned.Clear();
            _loaded = false;
            _store = null;
        }
    }

    private List<string> Load()
    {
        var store = Store;
        if (store is null)
        {
            return [];
        }

        try
        {
            return store.Load();
        }
        catch
        {
            return [];
        }
    }

    private void Save(IReadOnlyList<string> ids)
    {
        try
        {
            Store?.Save(ids);
        }
        catch
        {
            // Персистенция best-effort: закрепление работает и без записи на диск.
        }
    }

    private PinnedCardsStore? Store
    {
        get
        {
            if (_loaded)
            {
                return _store;
            }

            _loaded = true;
            try
            {
                _store = (StoreFactory ?? (() => new PinnedCardsStore(Logger.CurrentRun)))();
                foreach (var id in _store.Load())
                {
                    if (IndexOf(id) < 0)
                    {
                        _pinned.Add(id);
                    }
                }
            }
            catch
            {
                _store = null; // режим «только память»
            }

            return _store;
        }
    }

    private void OnItemsChanged()
    {
        // XAML-биндингов по Item[] сейчас нет (глифы синхронизируются через
        // CardPinChanged) — уведомление оставлено как безвредный канал для
        // возможных будущих биндингов.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }
}
