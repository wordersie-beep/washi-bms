using System;
using System.Collections.Generic;
using System.Globalization;
using Quant.Core.Primitives;

namespace Quant.Core.State;

/// <summary>
/// Гарантия, что один сигнал приводит ровно к одному ордеру (разделы 54, 147).
///
/// Защищает от трёх разных сценариев, которые выглядят одинаково с точки зрения брокера:
///
///   * один и тот же бар обрабатывается дважды из-за повторного события платформы;
///   * робот перезапускается между отправкой ордера и получением подтверждения;
///   * несколько тиков в пределах одного бара порождают один и тот же сигнал.
///
/// Во всех трёх случаях результат без защиты один — двойная позиция двойного размера. Ключ
/// сигнала детерминирован и строится из символа, стратегии, направления и ВРЕМЕНИ ОТКРЫТИЯ
/// БАРА: два вычисления на одном баре обязаны дать один и тот же идентификатор, иначе защита
/// не работает.
/// </summary>
public sealed class IdempotencyGuard
{
    private readonly HashSet<string> _executed = new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<string> _order = new Queue<string>();
    private readonly int _capacity;

    public IdempotencyGuard(int capacity = 2000)
    {
        _capacity = Math.Max(100, capacity);
    }

    public int Count => _executed.Count;

    /// <summary>
    /// Детерминированный идентификатор сигнала.
    ///
    /// Намеренно НЕ включает цену, размер или что-либо ещё, меняющееся внутри бара: иначе
    /// два тика одного бара дали бы разные идентификаторы и защита пропустила бы дубль.
    /// </summary>
    public static string BuildSignalId(string symbol, string strategy, Side direction, DateTime barOpenTimeUtc) =>
        string.Concat(
            symbol, "|", strategy, "|", direction.ToString(), "|",
            barOpenTimeUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));

    public bool HasExecuted(string signalId) =>
        !string.IsNullOrEmpty(signalId) && _executed.Contains(signalId);

    /// <summary>
    /// Помечает сигнал как исполненный. Возвращает false, если он уже был помечен — вызывающая
    /// сторона обязана трактовать это как «ордер не отправлять».
    /// </summary>
    public bool TryMarkExecuted(string signalId)
    {
        if (string.IsNullOrEmpty(signalId)) return false;
        if (!_executed.Add(signalId)) return false;

        _order.Enqueue(signalId);
        while (_order.Count > _capacity)
        {
            _executed.Remove(_order.Dequeue());
        }

        return true;
    }

    /// <summary>
    /// Снимает отметку. Вызывается ТОЛЬКО когда точно известно, что ордер не был принят
    /// брокером. Снимать её при неопределённом исходе — значит разрешить дубль.
    /// </summary>
    public void Release(string signalId)
    {
        if (!string.IsNullOrEmpty(signalId)) _executed.Remove(signalId);
    }

    public IReadOnlyCollection<string> ExecutedIds => _executed;

    public void Restore(IEnumerable<string> signalIds)
    {
        if (signalIds == null) return;
        foreach (string id in signalIds)
        {
            if (!string.IsNullOrEmpty(id) && _executed.Add(id)) _order.Enqueue(id);
        }

        while (_order.Count > _capacity) _executed.Remove(_order.Dequeue());
    }

    public void Reset()
    {
        _executed.Clear();
        _order.Clear();
    }
}
