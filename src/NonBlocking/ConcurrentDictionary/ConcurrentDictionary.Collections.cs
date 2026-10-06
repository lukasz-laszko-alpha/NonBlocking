// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

#nullable disable
#pragma warning disable CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.

using System;
using System.Collections;
using System.Collections.Generic;

namespace Alc.NonBlocking
{
    public partial class ConcurrentDictionary<TKey, TValue>
    {
        // Cached, lazily created collection views. One instance per dictionary, so repeated
        // access to Keys/Values does not allocate (unlike rebuilding a List<T> each call).
        private KeyCollection _keys;
        private ValueCollection _values;

        /// <summary>
        /// A live, allocation-free view of the keys in a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <remarks>
        /// Iterating this collection uses a struct enumerator and allocates nothing. The view is live:
        /// each enumeration observes the dictionary as of the moment iteration begins, consistent with
        /// <see cref="ConcurrentDictionary{TKey,TValue}.GetEnumerator"/>. The collection is read-only;
        /// mutating members throw <see cref="NotSupportedException"/>.
        /// </remarks>
        public sealed class KeyCollection : ICollection<TKey>, IReadOnlyCollection<TKey>, ICollection
        {
            private readonly ConcurrentDictionary<TKey, TValue> _dict;

            internal KeyCollection(ConcurrentDictionary<TKey, TValue> dict)
            {
                _dict = dict;
            }

            /// <summary>
            /// Gets the number of keys, as of the moment of access.
            /// </summary>
            public int Count => _dict.Count;

            /// <summary>
            /// Always true. The collection is a read-only view.
            /// </summary>
            public bool IsReadOnly => true;

            /// <summary>
            /// Returns an allocation-free struct enumerator over the keys.
            /// </summary>
            public Enumerator GetEnumerator() => new Enumerator(_dict.GetEnumerator());

            IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            /// <summary>
            /// Determines whether the dictionary contains the specified key.
            /// </summary>
            public bool Contains(TKey item) => _dict.ContainsKey(item);

            /// <summary>
            /// Copies the keys to <paramref name="array"/> starting at <paramref name="arrayIndex"/>.
            /// </summary>
            public void CopyTo(TKey[] array, int arrayIndex)
            {
                if (array is null)
                {
                    ThrowHelper.ThrowArgumentNullException(nameof(array));
                }

                if (arrayIndex < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(arrayIndex));
                }

                var length = array.Length;
                if (arrayIndex > length)
                {
                    throw new ArgumentException("array is too short");
                }

                foreach (var kv in _dict)
                {
                    if ((uint)arrayIndex < (uint)length)
                    {
                        array[arrayIndex++] = kv.Key;
                    }
                    else
                    {
                        throw new ArgumentException("array is too short");
                    }
                }
            }

            void ICollection.CopyTo(Array array, int index)
            {
                if (array is null)
                {
                    ThrowHelper.ThrowArgumentNullException(nameof(array));
                }

                if (array is TKey[] keys)
                {
                    CopyTo(keys, index);
                    return;
                }

                if (array is object[] objects)
                {
                    if (index < 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    var length = array.Length;
                    if (index > length)
                    {
                        throw new ArgumentException("array is too short");
                    }

                    foreach (var kv in _dict)
                    {
                        if ((uint)index < (uint)length)
                        {
                            objects[index++] = kv.Key;
                        }
                        else
                        {
                            throw new ArgumentException("array is too short");
                        }
                    }

                    return;
                }

                throw new ArgumentException("array type is incorrect", nameof(array));
            }

            void ICollection<TKey>.Add(TKey item) => throw new NotSupportedException();
            void ICollection<TKey>.Clear() => throw new NotSupportedException();
            bool ICollection<TKey>.Remove(TKey item) => throw new NotSupportedException();

            bool ICollection.IsSynchronized => false;
            object ICollection.SyncRoot => throw new NotSupportedException("SyncRoot not supported");

            /// <summary>
            /// An allocation-free enumerator over the keys.
            /// </summary>
            public struct Enumerator : IEnumerator<TKey>
            {
                private ConcurrentDictionary<TKey, TValue>.Enumerator _inner;

                internal Enumerator(ConcurrentDictionary<TKey, TValue>.Enumerator inner)
                {
                    _inner = inner;
                }

                /// <summary>
                /// Gets the key at the current position of the enumerator.
                /// </summary>
                public TKey Current => _inner.Current.Key;

                object IEnumerator.Current => _inner.Current.Key;

                /// <summary>
                /// Advances the enumerator to the next key.
                /// </summary>
                public bool MoveNext() => _inner.MoveNext();

                /// <summary>
                /// Sets the enumerator to its initial position.
                /// </summary>
                public void Reset() => _inner.Reset();

                /// <summary>
                /// Releases resources used by the enumerator.
                /// </summary>
                public void Dispose() { }
            }
        }

        /// <summary>
        /// A live, allocation-free view of the values in a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <remarks>
        /// Iterating this collection uses a struct enumerator and allocates nothing. The view is live:
        /// each enumeration observes the dictionary as of the moment iteration begins, consistent with
        /// <see cref="ConcurrentDictionary{TKey,TValue}.GetEnumerator"/>. The collection is read-only;
        /// mutating members throw <see cref="NotSupportedException"/>.
        /// </remarks>
        public sealed class ValueCollection : ICollection<TValue>, IReadOnlyCollection<TValue>, ICollection
        {
            private readonly ConcurrentDictionary<TKey, TValue> _dict;

            internal ValueCollection(ConcurrentDictionary<TKey, TValue> dict)
            {
                _dict = dict;
            }

            /// <summary>
            /// Gets the number of values, as of the moment of access.
            /// </summary>
            public int Count => _dict.Count;

            /// <summary>
            /// Always true. The collection is a read-only view.
            /// </summary>
            public bool IsReadOnly => true;

            /// <summary>
            /// Returns an allocation-free struct enumerator over the values.
            /// </summary>
            public Enumerator GetEnumerator() => new Enumerator(_dict.GetEnumerator());

            IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            /// <summary>
            /// Determines whether the dictionary contains the specified value (linear scan).
            /// </summary>
            public bool Contains(TValue item)
            {
                var comparer = EqualityComparer<TValue>.Default;
                foreach (var kv in _dict)
                {
                    if (comparer.Equals(kv.Value, item))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>
            /// Copies the values to <paramref name="array"/> starting at <paramref name="arrayIndex"/>.
            /// </summary>
            public void CopyTo(TValue[] array, int arrayIndex)
            {
                if (array is null)
                {
                    ThrowHelper.ThrowArgumentNullException(nameof(array));
                }

                if (arrayIndex < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(arrayIndex));
                }

                var length = array.Length;
                if (arrayIndex > length)
                {
                    throw new ArgumentException("array is too short");
                }

                foreach (var kv in _dict)
                {
                    if ((uint)arrayIndex < (uint)length)
                    {
                        array[arrayIndex++] = kv.Value;
                    }
                    else
                    {
                        throw new ArgumentException("array is too short");
                    }
                }
            }

            void ICollection.CopyTo(Array array, int index)
            {
                if (array is null)
                {
                    ThrowHelper.ThrowArgumentNullException(nameof(array));
                }

                if (array is TValue[] values)
                {
                    CopyTo(values, index);
                    return;
                }

                if (array is object[] objects)
                {
                    if (index < 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    var length = array.Length;
                    if (index > length)
                    {
                        throw new ArgumentException("array is too short");
                    }

                    foreach (var kv in _dict)
                    {
                        if ((uint)index < (uint)length)
                        {
                            objects[index++] = kv.Value;
                        }
                        else
                        {
                            throw new ArgumentException("array is too short");
                        }
                    }

                    return;
                }

                throw new ArgumentException("array type is incorrect", nameof(array));
            }

            void ICollection<TValue>.Add(TValue item) => throw new NotSupportedException();
            void ICollection<TValue>.Clear() => throw new NotSupportedException();
            bool ICollection<TValue>.Remove(TValue item) => throw new NotSupportedException();

            bool ICollection.IsSynchronized => false;
            object ICollection.SyncRoot => throw new NotSupportedException("SyncRoot not supported");

            /// <summary>
            /// An allocation-free enumerator over the values.
            /// </summary>
            public struct Enumerator : IEnumerator<TValue>
            {
                private ConcurrentDictionary<TKey, TValue>.Enumerator _inner;

                internal Enumerator(ConcurrentDictionary<TKey, TValue>.Enumerator inner)
                {
                    _inner = inner;
                }

                /// <summary>
                /// Gets the value at the current position of the enumerator.
                /// </summary>
                public TValue Current => _inner.Current.Value;

                object IEnumerator.Current => _inner.Current.Value;

                /// <summary>
                /// Advances the enumerator to the next value.
                /// </summary>
                public bool MoveNext() => _inner.MoveNext();

                /// <summary>
                /// Sets the enumerator to its initial position.
                /// </summary>
                public void Reset() => _inner.Reset();

                /// <summary>
                /// Releases resources used by the enumerator.
                /// </summary>
                public void Dispose() { }
            }
        }
    }
}
