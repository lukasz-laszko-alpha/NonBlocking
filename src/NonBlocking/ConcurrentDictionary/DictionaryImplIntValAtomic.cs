// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

//
// Int-key leaves of the value-inline table (DictionaryImplValAtomic`3). Key handling
// is identical to DictionaryImplInt / DictionaryImplIntNoComparer; only the base
// class differs (inline long value slot instead of an object/Boxed value slot).
// Used for <int, TValue> and <uint, TValue> (comparer == null) when TValue is a
// <= 32-bit atomic primitive (ConcurrentDictionary.s_valueIsAtomic32).
//

using System.Threading;

namespace Alc.NonBlocking
{
    internal sealed class DictionaryImplIntValAtomic<TValue>
                : DictionaryImplValAtomic<int, int, TValue>
    {
        internal DictionaryImplIntValAtomic(int capacity, ConcurrentDictionary<int, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplIntValAtomic(int capacity, DictionaryImplIntValAtomic<TValue> other)
            : base(capacity, other)
        {
        }

        protected override bool TryClaimSlotForPut(ref int entryKey, int key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        protected override bool TryClaimSlotForCopy(ref int entryKey, int key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        private bool TryClaimSlot(ref int entryKey, int key)
        {
            var entryKeyValue = entryKey;
            //zero keys are claimed via hash
            if (entryKeyValue == 0 & key != 0)
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

        protected override int hash(int key)
        {
            if (key == 0)
            {
                return ZEROHASH;
            }

            return base.hash(key);
        }

        protected override bool keyEqual(int key, int entryKey)
        {
            return key == entryKey || _keyComparer.Equals(key, entryKey);
        }

        protected override DictionaryImplValAtomic<int, int, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplIntValAtomic<TValue>(capacity, this);
        }

        protected override int keyFromEntry(int entryKey)
        {
            return entryKey;
        }
    }

    internal sealed class DictionaryImplIntValAtomicNoComparer<TValue>
            : DictionaryImplValAtomic<int, int, TValue>
    {
        internal DictionaryImplIntValAtomicNoComparer(int capacity, ConcurrentDictionary<int, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplIntValAtomicNoComparer(int capacity, DictionaryImplIntValAtomicNoComparer<TValue> other)
            : base(capacity, other)
        {
        }

        protected override bool TryClaimSlotForPut(ref int entryKey, int key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        protected override bool TryClaimSlotForCopy(ref int entryKey, int key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        private bool TryClaimSlot(ref int entryKey, int key)
        {
            var entryKeyValue = entryKey;
            //zero keys are claimed via hash
            if (entryKeyValue == 0 & key != 0)
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

        protected override int hash(int key)
        {
            return (key == 0) ?
                ZEROHASH :
                key | SPECIAL_HASH_BITS;
        }

        protected override bool keyEqual(int key, int entryKey)
        {
            return key == entryKey;
        }

        protected override DictionaryImplValAtomic<int, int, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplIntValAtomicNoComparer<TValue>(capacity, this);
        }

        protected override int keyFromEntry(int entryKey)
        {
            return entryKey;
        }
    }
}
