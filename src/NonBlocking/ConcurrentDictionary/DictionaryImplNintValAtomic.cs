// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

//
// Nint-key leaves of the value-inline table (DictionaryImplValAtomic`3). Key handling
// mirrors DictionaryImplNint / DictionaryImplNintNoComparer; only the value slot differs
// (inline encoded long instead of an object/Boxed value). Used for <nint, TValue> and
// <nuint, TValue> (comparer == null) when TValue is a <= 32-bit atomic primitive.
//

using System.Threading;

namespace Alc.NonBlocking
{
    internal sealed class DictionaryImplNintValAtomic<TValue>
                : DictionaryImplValAtomic<nint, nint, TValue>
    {
        internal DictionaryImplNintValAtomic(int capacity, ConcurrentDictionary<nint, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplNintValAtomic(int capacity, DictionaryImplNintValAtomic<TValue> other)
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

        protected override DictionaryImplValAtomic<nint, nint, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplNintValAtomic<TValue>(capacity, this);
        }

        protected override nint keyFromEntry(nint entryKey)
        {
            return entryKey;
        }
    }

    internal sealed class DictionaryImplNintValAtomicNoComparer<TValue>
            : DictionaryImplValAtomic<nint, nint, TValue>
    {
        internal DictionaryImplNintValAtomicNoComparer(int capacity, ConcurrentDictionary<nint, TValue> topDict)
            : base(capacity, topDict)
        {
        }

        internal DictionaryImplNintValAtomicNoComparer(int capacity, DictionaryImplNintValAtomicNoComparer<TValue> other)
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

        protected override DictionaryImplValAtomic<nint, nint, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplNintValAtomicNoComparer<TValue>(capacity, this);
        }

        protected override nint keyFromEntry(nint entryKey)
        {
            return entryKey;
        }
    }
}
