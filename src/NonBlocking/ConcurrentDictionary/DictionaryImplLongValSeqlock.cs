// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

//
// Long-key leaves of the value-inline table (DictionaryImplValSeqlock`3). Key handling
// mirrors DictionaryImplLong / DictionaryImplLongNoComparer; only the value slot differs
// (inline struct guarded by a per-entry seqlock instead of an object/Boxed value). Used for <long, TValue> and
// <ulong, TValue> (comparer == null) when TValue is an unmanaged struct not handled by Phase 1 (long/double/Guid/decimal/custom).
//

using System.Threading;

namespace Alc.NonBlocking
{
    internal sealed class DictionaryImplLongValSeqlock<TValue>
                : DictionaryImplValSeqlock<long, long, TValue>
    {
        internal DictionaryImplLongValSeqlock(int capacity, ConcurrentDictionary<long, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplLongValSeqlock(int capacity, DictionaryImplLongValSeqlock<TValue> other)
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

        protected override DictionaryImplValSeqlock<long, long, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplLongValSeqlock<TValue>(capacity, this);
        }

        protected override long keyFromEntry(long entryKey)
        {
            return entryKey;
        }
    }

    internal sealed class DictionaryImplLongValSeqlockNoComparer<TValue>
            : DictionaryImplValSeqlock<long, long, TValue>
    {
        internal DictionaryImplLongValSeqlockNoComparer(int capacity, ConcurrentDictionary<long, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplLongValSeqlockNoComparer(int capacity, DictionaryImplLongValSeqlockNoComparer<TValue> other)
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

        protected override DictionaryImplValSeqlock<long, long, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplLongValSeqlockNoComparer<TValue>(capacity, this);
        }

        protected override long keyFromEntry(long entryKey)
        {
            return entryKey;
        }
    }
}
