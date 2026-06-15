// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

#nullable disable
#pragma warning disable CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Alc.NonBlocking
{
    public partial class ConcurrentDictionary<TKey, TValue>
    {
        /// <summary>
        /// Captures an immutable, moment-in-time snapshot of the dictionary into a pooled buffer.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="GetEnumerator"/>, <see cref="Keys"/>, and <see cref="Values"/> — which are live
        /// views — the returned <see cref="Snapshot"/> is fixed at the moment <see cref="TakeSnapshot"/> is
        /// called and never changes afterwards, so it can be enumerated repeatedly with identical results.
        ///
        /// The snapshot is backed by an array rented from <see cref="ArrayPool{T}"/>. It is a disposable
        /// <c>ref struct</c>: it must be used within a <c>using</c> scope so the buffer is returned to the pool
        /// on <see cref="Snapshot.Dispose"/>. Because it is a <c>ref struct</c>, it is stack-only — it cannot be
        /// stored in a field, captured by a lambda, used across <c>await</c>, or cast to an interface. After it
        /// is disposed the buffer may be reused, so the snapshot and any view obtained from it
        /// (<see cref="Snapshot.Keys"/>, <see cref="Snapshot.Values"/>) must not be used past the scope.
        ///
        /// Allocation: an empty dictionary allocates nothing; otherwise the only heap traffic is renting/returning
        /// the pooled buffer, so repeated captures of a similar size reuse buffers and allocate nothing.
        /// </remarks>
        [MustDisposeResource]
        public Snapshot TakeSnapshot()
        {
            // linearization point: resolve a quiescent table, then copy once.
            var table = _table.GetTableForSnapshot();

            int count = table.Count;
            if (count == 0)
            {
                return new Snapshot(Array.Empty<KeyValuePair<TKey, TValue>>(), 0, pooled: false);
            }

            // The pooled buffer is at least 'count' long (often larger); the real length is tracked separately.
            var array = ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Rent(count);
            int idx = 0;
            int cursor = 0;
            while (idx < count && table.MoveNextSnapshot(ref cursor, out var key, out var value))
            {
                array[idx++] = new KeyValuePair<TKey, TValue>(key, value);
            }

            return new Snapshot(array, idx, pooled: true);
        }

        /// <summary>
        /// An immutable, moment-in-time snapshot of a <see cref="ConcurrentDictionary{TKey,TValue}"/>, backed by
        /// a pooled buffer. Produced by <see cref="TakeSnapshot"/>. This is a disposable, stack-only
        /// <c>ref struct</c>: dispose it (use <c>using</c>) to return the buffer to the pool. Enumeration is
        /// allocation-free.
        /// </summary>
        public ref struct Snapshot
        {
            private KeyValuePair<TKey, TValue>[] _items;
            private readonly int _count;
            private readonly bool _pooled;

            internal Snapshot(KeyValuePair<TKey, TValue>[] items, int count, bool pooled)
            {
                _items = items;
                _count = count;
                _pooled = pooled;
            }

            /// <summary>The number of pairs captured in the snapshot.</summary>
            public readonly int Count => _count;

            /// <summary>Gets the pair at the specified index in the snapshot.</summary>
            public readonly KeyValuePair<TKey, TValue> this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)_count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    return _items[index];
                }
            }

            /// <summary>Gets the keys of the snapshot.</summary>
            public readonly KeyCollection Keys => new KeyCollection(_items, _count);

            /// <summary>Gets the values of the snapshot.</summary>
            public readonly ValueCollection Values => new ValueCollection(_items, _count);

            /// <summary>Returns an allocation-free struct enumerator over the snapshot.</summary>
            public readonly Enumerator GetEnumerator() => new Enumerator(_items, _count);

            /// <summary>Returns the pooled buffer to the <see cref="ArrayPool{T}"/>. Safe to call more than once.</summary>
            public void Dispose()
            {
                var items = _items;
                _items = null;
                if (_pooled && items != null)
                {
#if NETSTANDARD2_1_OR_GREATER || NET
                    ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Return(
                        items,
                        clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<KeyValuePair<TKey, TValue>>());
#else
                    ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Return(items, clearArray: true);
#endif
                }
            }

            /// <summary>An allocation-free enumerator over a snapshot.</summary>
            public ref struct Enumerator
            {
                private readonly KeyValuePair<TKey, TValue>[] _items;
                private readonly int _count;
                private int _index;

                internal Enumerator(KeyValuePair<TKey, TValue>[] items, int count)
                {
                    _items = items;
                    _count = count;
                    _index = -1;
                }

                /// <summary>Gets the element at the current position of the enumerator.</summary>
                public readonly KeyValuePair<TKey, TValue> Current => _items[_index];

                /// <summary>Advances the enumerator to the next element.</summary>
                public bool MoveNext() => ++_index < _count;

                /// <summary>Releases resources used by the enumerator.</summary>
                public readonly void Dispose() { }
            }

            /// <summary>The keys of a <see cref="Snapshot"/>.</summary>
            public ref struct KeyCollection
            {
                private readonly KeyValuePair<TKey, TValue>[] _items;
                private readonly int _count;

                internal KeyCollection(KeyValuePair<TKey, TValue>[] items, int count)
                {
                    _items = items;
                    _count = count;
                }

                /// <summary>The number of keys in the snapshot.</summary>
                public readonly int Count => _count;

                /// <summary>Gets the key at the specified index in the snapshot.</summary>
                public readonly TKey this[int index]
                {
                    get
                    {
                        if ((uint)index >= (uint)_count)
                        {
                            throw new ArgumentOutOfRangeException(nameof(index));
                        }

                        return _items[index].Key;
                    }
                }

                /// <summary>Returns an allocation-free struct enumerator over the keys.</summary>
                public readonly Enumerator GetEnumerator() => new Enumerator(_items, _count);

                /// <summary>An allocation-free enumerator over snapshot keys.</summary>
                public ref struct Enumerator
                {
                    private readonly KeyValuePair<TKey, TValue>[] _items;
                    private readonly int _count;
                    private int _index;

                    internal Enumerator(KeyValuePair<TKey, TValue>[] items, int count)
                    {
                        _items = items;
                        _count = count;
                        _index = -1;
                    }

                    /// <summary>Gets the key at the current position of the enumerator.</summary>
                    public readonly TKey Current => _items[_index].Key;

                    /// <summary>Advances the enumerator to the next key.</summary>
                    public bool MoveNext() => ++_index < _count;

                    /// <summary>Releases resources used by the enumerator.</summary>
                    public readonly void Dispose() { }
                }
            }

            /// <summary>The values of a <see cref="Snapshot"/>.</summary>
            public ref struct ValueCollection
            {
                private readonly KeyValuePair<TKey, TValue>[] _items;
                private readonly int _count;

                internal ValueCollection(KeyValuePair<TKey, TValue>[] items, int count)
                {
                    _items = items;
                    _count = count;
                }

                /// <summary>The number of values in the snapshot.</summary>
                public readonly int Count => _count;

                /// <summary>Gets the value at the specified index in the snapshot.</summary>
                public readonly TValue this[int index]
                {
                    get
                    {
                        if ((uint)index >= (uint)_count)
                        {
                            throw new ArgumentOutOfRangeException(nameof(index));
                        }

                        return _items[index].Value;
                    }
                }

                /// <summary>Returns an allocation-free struct enumerator over the values.</summary>
                public readonly Enumerator GetEnumerator() => new Enumerator(_items, _count);

                /// <summary>An allocation-free enumerator over snapshot values.</summary>
                public ref struct Enumerator
                {
                    private readonly KeyValuePair<TKey, TValue>[] _items;
                    private readonly int _count;
                    private int _index;

                    internal Enumerator(KeyValuePair<TKey, TValue>[] items, int count)
                    {
                        _items = items;
                        _count = count;
                        _index = -1;
                    }

                    /// <summary>Gets the value at the current position of the enumerator.</summary>
                    public readonly TValue Current => _items[_index].Value;

                    /// <summary>Advances the enumerator to the next value.</summary>
                    public bool MoveNext() => ++_index < _count;

                    /// <summary>Releases resources used by the enumerator.</summary>
                    public readonly void Dispose() { }
                }
            }
        }
    }
}
