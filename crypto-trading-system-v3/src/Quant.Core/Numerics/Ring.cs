using System;
using System.Collections;
using System.Collections.Generic;

namespace Quant.Core.Numerics;

/// <summary>
/// Fixed-capacity circular buffer. Every history the bot keeps is bounded by one of these,
/// which is what keeps a 24/7 process from growing without limit (spec section 150).
/// Indexing is newest-first: <c>this[0]</c> is the most recent item.
/// </summary>
public sealed class Ring<T> : IReadOnlyCollection<T>
{
    private readonly T[] _items;
    private int _head;   // index where the next item will be written
    private int _count;

    public Ring(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count => _count;
    public bool IsFull => _count == _items.Length;

    /// <summary>Newest-first indexer: 0 is the latest item, 1 the one before it.</summary>
    public T this[int indexFromNewest]
    {
        get
        {
            if (indexFromNewest < 0 || indexFromNewest >= _count)
                throw new ArgumentOutOfRangeException(nameof(indexFromNewest));
            int idx = _head - 1 - indexFromNewest;
            if (idx < 0) idx += _items.Length;
            return _items[idx];
        }
    }

    /// <summary>Appends an item, evicting the oldest when full.</summary>
    public void Add(T item)
    {
        _items[_head] = item;
        _head = (_head + 1) % _items.Length;
        if (_count < _items.Length) _count++;
    }

    /// <summary>Overwrites the newest item. Used when a still-forming bar is revised.</summary>
    public void ReplaceNewest(T item)
    {
        if (_count == 0) { Add(item); return; }
        int idx = _head - 1;
        if (idx < 0) idx += _items.Length;
        _items[idx] = item;
    }

    public bool TryGet(int indexFromNewest, out T value)
    {
        if (indexFromNewest < 0 || indexFromNewest >= _count) { value = default; return false; }
        value = this[indexFromNewest];
        return true;
    }

    public T Newest => _count > 0 ? this[0] : throw new InvalidOperationException("Ring is empty.");
    public T Oldest => _count > 0 ? this[_count - 1] : throw new InvalidOperationException("Ring is empty.");

    public void Clear()
    {
        Array.Clear(_items, 0, _items.Length);
        _head = 0;
        _count = 0;
    }

    /// <summary>Enumerates newest-first.</summary>
    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
