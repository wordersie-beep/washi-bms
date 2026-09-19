using System;
using cAlgo.API;
using Quant.Core.Journal;
using Quant.Core.State;

namespace Quant.Bot;

/// <summary>
/// Журнал через <c>Print</c> платформы.
///
/// В облаке это единственный доступный канал вывода: файловой системы нет, GUI нет,
/// внешние вызовы недоступны. Длинные блоки разбиваются построчно, потому что Print
/// обрезает слишком длинные строки, и обрезанный дашборд бесполезен именно тогда, когда его
/// читают — при разборе происшествия.
/// </summary>
public sealed class CTraderJournalSink : IJournalSink
{
    private readonly Robot _robot;

    public CTraderJournalSink(Robot robot)
    {
        _robot = robot ?? throw new ArgumentNullException(nameof(robot));
    }

    public void Write(string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        try
        {
            if (message.IndexOf('\n') < 0) { _robot.Print(message); return; }

            string[] lines = message.Split('\n');
            for (int i = 0; i < lines.Length; i++) _robot.Print(lines[i].TrimEnd('\r'));
        }
        catch (Exception)
        {
            // Сбой логирования не должен ронять торговлю.
        }
    }
}

/// <summary>
/// Хранилище состояния в <c>LocalStorage</c> платформы.
///
/// Область <see cref="LocalStorageScope.Instance"/> выбрана сознательно: состояние
/// принадлежит конкретному экземпляру робота. Общая область означала бы, что два экземпляра
/// на разных символах перетирают состояние друг друга — и каждый восстанавливается в чужое.
/// </summary>
public sealed class LocalStorageStateStore : IStateStore
{
    private const string Key = "QuantCryptoV3.State";

    private readonly Robot _robot;

    public LocalStorageStateStore(Robot robot)
    {
        _robot = robot ?? throw new ArgumentNullException(nameof(robot));
    }

    public BotState Load()
    {
        try
        {
            string json = _robot.LocalStorage.GetString(Key, LocalStorageScope.Instance);
            return StateSerializer.Deserialize(json);
        }
        catch (Exception)
        {
            // Нечитаемое состояние трактуется как его отсутствие: система стартует осторожно
            // и сверяется с брокером, вместо того чтобы действовать на основе обрывков.
            return null;
        }
    }

    public bool Save(BotState state)
    {
        try
        {
            string json = StateSerializer.Serialize(state);
            if (json == null) return false;

            _robot.LocalStorage.SetString(Key, json, LocalStorageScope.Instance);
            _robot.LocalStorage.Flush(LocalStorageScope.Instance);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            _robot.LocalStorage.Remove(Key, LocalStorageScope.Instance);
            _robot.LocalStorage.Flush(LocalStorageScope.Instance);
        }
        catch (Exception)
        {
        }
    }
}
