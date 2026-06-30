// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

//
// Nint-key leaves of the value-inline table (DictionaryImplValSeqlock`3). Key handling
// mirrors DictionaryImplNint / DictionaryImplNintNoComparer; only the value slot differs
// (inline struct guarded by a per-entry seqlock instead of an object/Boxed value). Used for <nint, TValue> and
// <nuint, TValue> (comparer == null) when TValue is an unmanaged struct not handled by Phase 1 (long/double/Guid/decimal/custom).
//

using System.Threading;

namespace Alc.NonBlocking
{
    internal sealed class DictionaryImplNintValSeqlock<TValue>
                : DictionaryImplValSeqlock<nint, nint, TValue>
    {
        internal DictionaryImplNintValSeqlock(int capacity, ConcurrentDictionary<nint, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplNintValSeqlock(int capacity, DictionaryImplNintValSeqlock<TValue> other)
            : base(capacity, other)
        {
        }

        protected override bool TryClaimSlotForPut(ref nint entryKey, nint key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        protected override bool TryClaimSlotForCopy(ref nint entryKey, nint key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        private bool TryClaimSlot(ref nint entryKey, nint key)
        {
            var entryKeyValue = entryKey;
            //zero keys are claimed via hash
            if (entryKeyValue == 0 && key != 0)
            {
                entryKeyValue = Interlocked.CompareExchange(ref entryKey, key, (nint)0);
                if (entryKeyValue == 0)
                {
                    // claimed a new slot
                    this.allocatedSlotCount.Increment();
                    return true;
                }
            }

            return key == entryKeyValue || _keyComparer.Equals(key, entryKeyValue);
        }

        protected override int hash(nint key)
        {
            if (key == 0)
            {
                return ZEROHASH;
            }

            return base.hash(key);
        }

        protected override bool keyEqual(nint key, nint entryKey)
        {
            return key == entryKey || _keyComparer.Equals(key, entryKey);
        }

        protected override DictionaryImplValSeqlock<nint, nint, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplNintValSeqlock<TValue>(capacity, this);
        }

        protected override nint keyFromEntry(nint entryKey)
        {
            return entryKey;
        }
    }

    internal sealed class DictionaryImplNintValSeqlockNoComparer<TValue>
            : DictionaryImplValSeqlock<nint, nint, TValue>
    {
        internal DictionaryImplNintValSeqlockNoComparer(int capacity, ConcurrentDictionary<nint, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplNintValSeqlockNoComparer(int capacity, DictionaryImplNintValSeqlockNoComparer<TValue> other)
            : base(capacity, other)
        {
        }

        protected override bool TryClaimSlotForPut(ref nint entryKey, nint key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        protected override bool TryClaimSlotForCopy(ref nint entryKey, nint key)
        {
            return TryClaimSlot(ref entryKey, key);
        }

        private bool TryClaimSlot(ref nint entryKey, nint key)
        {
            var entryKeyValue = entryKey;
            //zero keys are claimed via hash
            if (entryKeyValue == 0 && key != 0)
            {
                entryKeyValue = Interlocked.CompareExchange(ref entryKey, key, (nint)0);
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
        internal override bool TryGetValueUnboxed(nint key, out TValue value)
        {
            return base.TryGetValueUnboxed(key, out value);
        }

        protected override int hash(nint key)
        {
            return (key == 0) ?
                ZEROHASH :
                key.GetHashCode() | SPECIAL_HASH_BITS;
        }

        protected override bool keyEqual(nint key, nint entryKey)
        {
            return key == entryKey;
        }

        protected override DictionaryImplValSeqlock<nint, nint, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplNintValSeqlockNoComparer<TValue>(capacity, this);
        }

        protected override nint keyFromEntry(nint entryKey)
        {
            return entryKey;
        }
    }
}
