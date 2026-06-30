// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

//
// Reference-key leaf of the seqlock value-inline table (DictionaryImplValSeqlock`3). Key
// handling mirrors DictionaryImplRef; only the value slot differs (inline struct guarded by a
// per-entry seqlock instead of an object/Boxed value). Used for <refkey, TValue> (e.g.
// <string,Guid>, <object,long>) when TValue is an unmanaged struct not handled by Phase 1.
//

#nullable disable

using System.Threading;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace Alc.NonBlocking
{
    internal sealed class DictionaryImplRefValSeqlock<TKey, TKeyStore, TValue>
            : DictionaryImplValSeqlock<TKey, TKey, TValue>
    {
        internal DictionaryImplRefValSeqlock(int capacity, ConcurrentDictionary<TKey, TValue> topDict)
            : base(capacity, topDict)
        {
            Debug.Assert(!typeof(TKey).IsValueType);
        }

        internal DictionaryImplRefValSeqlock(int capacity, DictionaryImplRefValSeqlock<TKey, TKeyStore, TValue> other)
            : base(capacity, other)
        {
            Debug.Assert(!typeof(TKey).IsValueType);
        }

        protected override bool TryClaimSlotForPut(ref TKey entryKey, TKey key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        protected override bool TryClaimSlotForCopy(ref TKey entryKey, TKey key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        private bool TryClaimSlot(ref TKey entryKey, TKey key)
        {
            ref object keyLocation = ref Unsafe.As<TKey, object>(ref entryKey);
            object entryKeyValue = keyLocation;
            if (entryKeyValue == null)
            {
                entryKeyValue = Interlocked.CompareExchange(ref keyLocation, key, null);
                if (entryKeyValue == null)
                {
                    // claimed a new slot
                    this.allocatedSlotCount.Increment();
                    return true;
                }
            }

            return (object)key == entryKeyValue ||
                _keyComparer.Equals(key, Unsafe.As<object, TKey>(ref entryKeyValue));
        }

        // force a devirtualized, per-leaf copy of the hot lookup (inlines hash/keyEqual)
        internal override bool TryGetValueUnboxed(TKey key, out TValue value)
        {
            return base.TryGetValueUnboxed(key, out value);
        }

        protected override int hash(TKey key)
        {
            return base.hash(key);
        }

        protected override bool keyEqual(TKey key, TKey entryKey)
        {
            if ((object)key == (object)entryKey)
            {
                return true;
            }

            //NOTE: slots are claimed in two stages - claim a hash, then set a key
            //      it is possible to observe a slot with a null key, but with hash already set
            //      that is not a match since the key is not yet in the table
            if (entryKey == null)
            {
                return false;
            }

            return _keyComparer.Equals(entryKey, key);
        }

        protected override DictionaryImplValSeqlock<TKey, TKey, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplRefValSeqlock<TKey, TKeyStore, TValue>(capacity, this);
        }

        protected override TKey keyFromEntry(TKey entryKey)
        {
            return entryKey;
        }
    }
}
