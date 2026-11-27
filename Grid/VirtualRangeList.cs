namespace Fx.ControlKit.Grid;

using System.Collections;

/// <summary>
/// High-performance read-only virtual list that computes rows on demand by index.
/// Enables binding 1,000,000+ virtual rows to FlexGrid with zero upfront memory allocation.
/// </summary>
/// <typeparam name="T">Row record type.</typeparam>
public sealed class VirtualRangeList<T> : IList<T>, IReadOnlyList<T>
{
    private readonly int _count;
    private readonly Func<int, T> _generator;

    public VirtualRangeList(int count, Func<int, T> generator)
    {
        _count = Math.Max(0, count);
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    public int Count => _count;
    public bool IsReadOnly => true;

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be between 0 and {_count - 1}.");
            return _generator(index);
        }
        set => throw new NotSupportedException("VirtualRangeList is read-only.");
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return _generator(i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(T item) => -1;
    public bool Contains(T item) => false;

    public void CopyTo(T[] array, int arrayIndex)
    {
        if (array == null) throw new ArgumentNullException(nameof(array));
        if (arrayIndex < 0) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        for (int i = 0; i < _count; i++)
        {
            if (arrayIndex + i >= array.Length) break;
            array[arrayIndex + i] = _generator(i);
        }
    }

    public void Add(T item) => throw new NotSupportedException("VirtualRangeList is read-only.");
    public void Clear() => throw new NotSupportedException("VirtualRangeList is read-only.");
    public bool Remove(T item) => throw new NotSupportedException("VirtualRangeList is read-only.");
    public void Insert(int index, T item) => throw new NotSupportedException("VirtualRangeList is read-only.");
    public void RemoveAt(int index) => throw new NotSupportedException("VirtualRangeList is read-only.");
}
