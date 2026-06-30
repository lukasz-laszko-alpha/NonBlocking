// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

//
// Int-key leaves of the seqlock value-inline table (DictionaryImplValSeqlock`3). Key handling
// mirrors DictionaryImplInt; only the value storage differs (inline struct guarded by a
// per-entry seqlock). Used for <int/uint, TValue> when TValue is an unmanaged struct not
// handled by Phase 1 (long/double/Guid/decimal/DateTime/custom).
//

using System.Threading;

namespace Alc.NonBlocking
{
    internal sealed class DictionaryImplIntValSeqlock<TValue>
                : DictionaryImplValSeqlock<int, int, TValue>
    {
        internal DictionaryImplIntValSeqlock(int capacity, ConcurrentDictionary<int, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplIntValSeqlock(int capacity, DictionaryImplIntValSeqlock<TValue> other)
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

        protected override DictionaryImplValSeqlock<int, int, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplIntValSeqlock<TValue>(capacity, this);
        }

        protected override int keyFromEntry(int entryKey)
        {
            return entryKey;
        }
    }

    internal sealed class DictionaryImplIntValSeqlockNoComparer<TValue>
            : DictionaryImplValSeqlock<int, int, TValue>
    {
        internal DictionaryImplIntValSeqlockNoComparer(int capacity, ConcurrentDictionary<int, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplIntValSeqlockNoComparer(int capacity, DictionaryImplIntValSeqlockNoComparer<TValue> other)
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

        internal override bool TryGetValueUnboxed(int key, out TValue value)
        {
            return base.TryGetValueUnboxed(key, out value);
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

        protected override DictionaryImplValSeqlock<int, int, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplIntValSeqlockNoComparer<TValue>(capacity, this);
        }

        protected override int keyFromEntry(int entryKey)
        {
            return entryKey;
        }
    }
}
