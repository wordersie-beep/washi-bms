using System;

namespace Quant.Core.State;

/// <summary>
/// Хранилище состояния. Абстракция нужна затем, чтобы ядро не зависело от платформенного
/// API, а тесты могли проверять восстановление без cTrader.
/// </summary>
public interface IStateStore
{
    /// <summary>Читает состояние. Возвращает null, если его нет или оно непригодно.</summary>
    BotState Load();

    /// <summary>Сохраняет состояние. Не должен бросать исключений: сбой записи не должен ронять торговлю.</summary>
    bool Save(BotState state);

    void Clear();
}

/// <summary>Хранилище в памяти — для тестов и для режимов, где персистентность не нужна.</summary>
public sealed class InMemoryStateStore : IStateStore
{
    private string _serialized;

    public int SaveCount { get; private set; }
    public int LoadCount { get; private set; }

    /// <summary>Если true, запись проваливается — для проверки устойчивости к сбоям хранилища.</summary>
    public bool FailWrites { get; set; }

    public BotState Load()
    {
        LoadCount++;
        return _serialized == null ? null : StateSerializer.Deserialize(_serialized);
    }

    public bool Save(BotState state)
    {
        SaveCount++;
        if (FailWrites) return false;

        _serialized = StateSerializer.Serialize(state);
        return _serialized != null;
    }

    public void Clear() => _serialized = null;
}
