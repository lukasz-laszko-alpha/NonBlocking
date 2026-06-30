// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

#nullable disable

using System.Runtime.CompilerServices;

namespace Alc.NonBlocking
{
    internal sealed partial class DictionaryImplStructKey<TKey, TValue>
        : DictionaryImpl<TKey, TValue>
    {
        internal override Snapshot GetSnapshot()
        {
            return new SnapshotImpl(this);
        }

        // linearization point.
        // if the table is quiescent and has no copy in progress, we can simply iterate over it.
        // Otherwise we finish any in-progress copy and retry, then return the resulting table.
        internal override DictionaryImpl<TKey, TValue> GetTableForSnapshot()
        {
            var table = this;
            while (table._newTable != null)
            {
                // there is a copy in progress, finish it and try again
                table.HelpCopy(copy_all: true);
                table = (DictionaryImplStructKey<TKey, TValue>)table._topDict._table;
            }

            return table;
        }

        // Single shared traversal. 'this' is the resolved table from GetTableForSnapshot.
        // The per-element work (keyFromEntry, the TryGetValue re-lookup that keeps the view
        // linearizable, FromObjectValue) is identical to the original SnapshotImpl.MoveNext.
        internal override bool MoveNextSnapshot(ref int idx, out TKey key, out TValue value)
        {
            var entries = this._entries;
            while (idx < entries.Length)
            {
                var nextEntry = entries[idx++];

                if (nextEntry.value != null)
                {
                    var nextKstore = nextEntry.key;
                    if (nextKstore == null)
                    {
                        // slot was deleted.
                        continue;
                    }

                    var curKey = keyFromEntry(nextKstore);
                    object nextV = TryGetValue(curKey);
                    if (nextV != null)
                    {
                        key = curKey;
                        value = FromObjectValue(nextV);
                        return true;
                    }
                }
            }

            key = default;
            value = default;
            return false;
        }

        private class SnapshotImpl : Snapshot
        {
            private readonly DictionaryImpl<TKey, TValue> _table;

            public SnapshotImpl(DictionaryImplStructKey<TKey, TValue> dict)
            {
                // linearization point.
                this._table = dict.GetTableForSnapshot();
            }

            public override int Count => _table.Count;

            public override bool MoveNext()
            {
                return _table.MoveNextSnapshot(ref _idx, out _curKey, out _curValue);
            }

            public override void Reset()
            {
                _idx = 0;
                _curKey = default;
                _curValue = default;
            }
        }
    }
}
