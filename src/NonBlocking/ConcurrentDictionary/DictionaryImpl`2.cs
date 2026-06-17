// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Alc.NonBlocking
{
    internal abstract class DictionaryImpl<TKey, TValue>
        : DictionaryImpl
    {
#if NETSTANDARD2_0
        private readonly bool _valueIsValueType = typeof(TValue).IsValueType;
#endif

        internal IEqualityComparer<TKey> _keyComparer;

        internal abstract void Clear();
        internal abstract int Count { get; }

        internal abstract object TryGetValue(TKey key);
        internal abstract bool PutIfMatch(TKey key, TValue newVal, ref TValue oldValue, ValueMatch match);
        internal abstract bool RemoveIfMatch(TKey key, ref TValue oldValue, ValueMatch match);
        internal abstract TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory);

        internal abstract Snapshot GetSnapshot();

        // Resolves a quiescent inner table to iterate over (the snapshot linearization point).
        // Used by the allocation-free struct enumerator so it can be created without allocating.
        internal abstract DictionaryImpl<TKey, TValue> GetTableForSnapshot();

        // Advances over the resolved table's entries starting at idx. Returns false when exhausted.
        // 'this' must be the table returned by GetTableForSnapshot. Shared by the struct enumerator
        // and SnapshotImpl so there is a single traversal implementation.
        internal abstract bool MoveNextSnapshot(ref int idx, out TKey key, out TValue value);

        internal abstract class Snapshot
        {
            protected int _idx;
            protected TKey _curKey;
            protected TValue _curValue;

            public abstract int Count { get; }
            public abstract bool MoveNext();
            public abstract void Reset();

            internal DictionaryEntry Entry
            {
                get
                {
                    return new DictionaryEntry(_curKey, _curValue);
                }
            }

            internal KeyValuePair<TKey, TValue> Current
            {
                get
                {
                    return new KeyValuePair<TKey, TValue>(this._curKey, _curValue);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected TValue FromObjectValue(object obj)
        {
            // regular value type
            if (default(TValue) != null)
            {
                return Unsafe.As<Boxed<TValue>>(obj).Value;
            }

            // null
            if (obj == NULLVALUE)
            {
                return default(TValue);
            }

            // ref type
#if NETSTANDARD2_0
            if (!_valueIsValueType)
#else
            if (!typeof(TValue).IsValueType)
#endif
            {
                return Unsafe.As<object, TValue>(ref obj);
            }

            // nullable
            return (TValue)obj;
        }
    }
}
