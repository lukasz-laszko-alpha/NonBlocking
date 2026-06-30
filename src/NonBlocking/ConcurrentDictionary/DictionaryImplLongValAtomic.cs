// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

//
// Long-key leaves of the value-inline table (DictionaryImplValAtomic`3). Key handling
// mirrors DictionaryImplLong / DictionaryImplLongNoComparer; only the value slot differs
// (inline encoded long instead of an object/Boxed value). Used for <long, TValue> and
// <ulong, TValue> (comparer == null) when TValue is a <= 32-bit atomic primitive.
//

using System.Threading;

namespace Alc.NonBlocking
{
    internal sealed class DictionaryImplLongValAtomic<TValue>
                : DictionaryImplValAtomic<long, long, TValue>
    {
        internal DictionaryImplLongValAtomic(int capacity, ConcurrentDictionary<long, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplLongValAtomic(int capacity, DictionaryImplLongValAtomic<TValue> other)
            : base(capacity, other)
        {
        }

        protected override bool TryClaimSlotForPut(ref long entryKey, long key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        protected override bool TryClaimSlotForCopy(ref long entryKey, long key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        private bool TryClaimSlot(ref long entryKey, long key)
        {
            var entryKeyValue = entryKey;
            //zero keys are claimed via hash
            if (entryKeyValue == 0 && key != 0)
            {
                entryKeyValue = Interlocked.CompareExchange(ref entryKey, key, 0);
                if (entryKeyValue == 0)
                {
                    // claimed a new slot
                    this.allocatedSlotCount.Increment();
                    return true;
                }
            }

            return key == entryKeyValue || _keyComparer.Equals(key, entryKeyValue);
        }

        protected override int hash(long key)
        {
            if (key == 0)
            {
                return ZEROHASH;
            }

            return base.hash(key);
        }

        protected override bool keyEqual(long key, long entryKey)
        {
            return key == entryKey || _keyComparer.Equals(key, entryKey);
        }

        protected override DictionaryImplValAtomic<long, long, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplLongValAtomic<TValue>(capacity, this);
        }

        protected override long keyFromEntry(long entryKey)
        {
            return entryKey;
        }
    }

    internal sealed class DictionaryImplLongValAtomicNoComparer<TValue>
            : DictionaryImplValAtomic<long, long, TValue>
    {
        internal DictionaryImplLongValAtomicNoComparer(int capacity, ConcurrentDictionary<long, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplLongValAtomicNoComparer(int capacity, DictionaryImplLongValAtomicNoComparer<TValue> other)
            : base(capacity, other)
        {
        }

        protected override bool TryClaimSlotForPut(ref long entryKey, long key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        protected override bool TryClaimSlotForCopy(ref long entryKey, long key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        private bool TryClaimSlot(ref long entryKey, long key)
        {
            var entryKeyValue = entryKey;
            //zero keys are claimed via hash
            if (entryKeyValue == 0 && key != 0)
            {
                entryKeyValue = Interlocked.CompareExchange(ref entryKey, key, 0);
                if (entryKeyValue == 0)
                {
                    // claimed a new slot
                    this.allocatedSlotCount.Increment();
                    return true;
                }
            }

            return key == entryKeyValue;
        }

        // force a devirtualized, per-leaf copy of the hot lookup (inlines hash/keyEqual)
        internal override bool TryGetValueUnboxed(long key, out TValue value)
        {
            return base.TryGetValueUnboxed(key, out value);
        }

        protected override int hash(long key)
        {
            return (key == 0) ?
                ZEROHASH :
                key.GetHashCode() | SPECIAL_HASH_BITS;
        }

        protected override bool keyEqual(long key, long entryKey)
        {
            return key == entryKey;
        }

        protected override DictionaryImplValAtomic<long, long, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplLongValAtomicNoComparer<TValue>(capacity, this);
        }

        protected override long keyFromEntry(long entryKey)
        {
            return entryKey;
        }
    }
}
