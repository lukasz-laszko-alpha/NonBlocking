// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

//
// Value-inline specialization of the Cliff Click / Vladimir Sadov lock-free table.
//
// This is a structural port of DictionaryImpl`3.cs. The ONLY difference is how the
// value slot is represented: instead of an `object value` that points at a heap
// `Boxed<TValue>` (and sentinel/Prime marker objects), the slot is a single
// `long state` that encodes both a small state tag (high bits) and the value's raw
// bits (low 32). This keeps value-type values out of the GC-traced heap entirely:
// the `Entry[]` table is one array the collector treats as a single object, so a
// dictionary of N value-type entries holds ZERO per-element heap objects. The point
// is deterministic GC (no gen2 pressure that scales with element count), with
// "0 bytes allocated per put" as the observable proxy.
//
// Applies only when TValue is a non-nullable primitive whose payload fits in <= 32
// bits (bool/byte/sbyte/char/short/ushort/int/uint/float). Dispatch is gated by
// ConcurrentDictionary.s_valueIsAtomic32; all other value types keep the Boxed path
// in DictionaryImpl`3.cs, which is left completely untouched.
//
// Slot encoding (tag in high bits so the payload, incl. 0 and 0xFFFFFFFF, never
// collides with a tag):
//   EMPTY     = 0                              (mirrors value == null)
//   VALUE(v)  = TAG_VALUE     | (uint)v        (mirrors a live Boxed<TValue>)
//   TOMBSTONE = TAG_TOMBSTONE                  (mirrors TOMBSTONE)
//   PRIME(v)  = TAG_PRIME | VALUE(v)           (mirrors new Prime(oldval))
//   TOMBPRIME = TAG_PRIME | TAG_TOMBSTONE      (mirrors TOMBPRIME)
//
// "Prime this slot" is a single CAS that ORs in TAG_PRIME, so the lock-free
// resize/copy protocol maps 1:1 onto single-word CAS and allocates no Prime wrapper.
//
// 32-bit atomicity: plain `long` loads/stores are not atomic on 32-bit (ECMA-335
// 12.6.6), and this library targets netstandard2.0. Therefore EVERY slot access uses
// Interlocked (Read / CompareExchange / Exchange) - never Volatile.Read(ref long).
// Do not "optimize" a slot read back to Volatile.Read: it silently breaks 32-bit.

#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Alc.NonBlocking
{
    internal sealed class DictionaryImplStructKeyValAtomic<TKey, TValue>
        : DictionaryImpl<TKey, TValue>
    {
        private readonly Entry[] _entries;
        internal DictionaryImplStructKeyValAtomic<TKey, TValue> _newTable;

        private readonly ConcurrentDictionary<TKey, TValue> _topDict;
        private readonly Counter32 allocatedSlotCount = new Counter32();
        private Counter32 _size;

        private int _resizers;
        private int _claimedChunk;
        private int _copyDone;

        // value-slot encoding -------------------------------------------------
        private const long PAYLOAD_MASK  = 0xFFFF_FFFFL;
        private const long TAG_VALUE     = 1L << 32;
        private const long TAG_TOMBSTONE = 2L << 32;
        private const long TAG_PRIME     = 4L << 32;
        private const long TAG_TOMBPRIME = TAG_PRIME | TAG_TOMBSTONE; // 6L << 32
        // EMPTY is the all-zero word.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long EncodeValue(TValue value)
        {
            // sizeof(TValue) <= 4 is guaranteed by the dispatch whitelist
            // (ConcurrentDictionary.s_valueIsAtomic32). Writing into a zeroed uint
            // copies exactly sizeof(TValue) bytes; the rest stay 0.
            uint payload = 0;
            Unsafe.As<uint, TValue>(ref payload) = value;
            return TAG_VALUE | (long)payload;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TValue DecodeValue(long state)
        {
            uint payload = (uint)(state & PAYLOAD_MASK);
            return Unsafe.As<uint, TValue>(ref payload);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPrime(long state) => (state & TAG_PRIME) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsEmpty(long state) => state == 0L;

        // mirrors EntryValueNullOrDead: empty slot or a tombstone.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool StateNullOrDead(long state) => state == 0L || state == TAG_TOMBSTONE;
        // ---------------------------------------------------------------------

        [DebuggerDisplay("key = {key}; hash = {hash}; keyState = {keyState}; state = {state};")]
        [StructLayout(LayoutKind.Sequential)]
        public struct Entry
        {
            internal int hash;
            internal TKey key;
            // write-once commit state for the inline struct key (see InlineStructKey).
            internal int keyState;
            // encoded value slot (see DictionaryImplValAtomic): tag in high bits, value in low 32.
            internal long state;
        }

        private const int MIN_SIZE = 8;
        private const uint RESIZE_MILLIS_TARGET = 1000;

        // Inline struct key (write-once commit protocol) + inline encoded value: both sides are
        // stored in the entry with no per-entry heap object.
        private DictionaryImplStructKeyValAtomic<TKey, TValue> CreateNew(int capacity)
        {
            return new DictionaryImplStructKeyValAtomic<TKey, TValue>(capacity, this);
        }

        private TKey keyFromEntry(TKey entryKey)
        {
            return entryKey;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool keyEqual(TKey key, ref Entry entry)
        {
            return InlineStructKey.Equal(ref entry.keyState, ref entry.key, key, _keyComparer);
        }

        private bool TryClaimSlotForPut(ref Entry entry, TKey key)
        {
            return InlineStructKey.TryClaim(ref entry.keyState, ref entry.key, key, _keyComparer, this.allocatedSlotCount);
        }

        private bool TryClaimSlotForCopy(ref Entry entry, TKey key)
        {
            return InlineStructKey.TryClaim(ref entry.keyState, ref entry.key, key, _keyComparer, this.allocatedSlotCount);
        }

        internal DictionaryImplStructKeyValAtomic(int capacity, ConcurrentDictionary<TKey, TValue> topDict)
        {
            capacity = Math.Max(capacity, MIN_SIZE);
            capacity = Util.AlignToPowerOfTwo(capacity);
            this._entries = new Entry[capacity];
            this._size = new Counter32();
            this._topDict = topDict;

#if NETSTANDARD2_1_OR_GREATER || NET
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
#else
            if (!typeof(TKey).IsValueType)
#endif
            {
                // do not create a real sweeper just yet. Often it is not needed.
                topDict._sweeperInstance = NULLVALUE;
            }
        }

        private DictionaryImplStructKeyValAtomic(int capacity, DictionaryImplStructKeyValAtomic<TKey, TValue> other)
        {
            capacity = Util.AlignToPowerOfTwo(capacity);
            this._entries = new Entry[capacity];
            this._size = other._size;
            this._topDict = other._topDict;
            this._keyComparer = other._keyComparer;
        }

        private static uint CurrentTickMillis()
        {
            return (uint)Environment.TickCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int hash(TKey key)
        {
            Debug.Assert(key is not null);

            int h = _keyComparer.GetHashCode(key);

            // ensure that hash never matches TOMBPRIMEHASH, ZEROHASH, SPECIAL_HASH_BITS or 0
            return h | (SPECIAL_HASH_BITS | (1 << 29));
        }

        internal sealed override int Count
        {
            get { return this.Size; }
        }

        internal sealed override void Clear()
        {
            var newTable = CreateNew(MIN_SIZE);
            newTable._size = new Counter32();
            _topDict._table = newTable;
        }

        // This impl stores values inline; the object-returning lookup would have to
        // re-box. All real lookups go through TryGetValueUnboxed (and the rewired
        // ConcurrentDictionary read paths). Fail loudly if anything reaches here.
        internal override object TryGetValue(TKey key)
        {
            throw new NotSupportedException(
                "DictionaryImplValAtomic stores values inline; use TryGetValueUnboxed.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool TryGetValueUnboxed(TKey key, out TValue value)
        {
            int fullHash = this.hash(key);
            var curTable = this;

        tryWithNewTable:

            var lenMask = curTable._entries.Length - 1;
            int idx = ReduceHashToIndex(fullHash, lenMask);

            int reprobeCount = 0;
            while (true)
            {
                ref var entry = ref curTable._entries[idx];

                // is this our slot?
                if (fullHash == entry.hash && keyEqual(key, ref entry))
                {
                    // read the value before reading _newTable (acquire, 32-bit-safe).
                    long entryState = Interlocked.Read(ref entry.state);
                    if (StateNullOrDead(entryState))
                    {
                        break;
                    }

                    // if no new table, no need to check for primed value,
                    // but TOMBPRIME is possible when sweeping, check for that
                    if ((curTable._newTable == null && entryState != TAG_TOMBPRIME) ||
                        !IsPrime(entryState))
                    {
                        value = DecodeValue(entryState);
                        return true;
                    }

                    // found a prime; copying/sweeping has started, help and retry.
                    curTable = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                    goto tryWithNewTable;
                }

                if (entry.hash == 0)
                {
                    // the slot has not been claimed - the rest of the bucket is empty
                    break;
                }

                if (entry.hash == TOMBPRIMEHASH || reprobeCount >= ReprobeLimit(lenMask))
                {
                    if (curTable._newTable != null)
                    {
                        curTable.HelpCopy();
                        curTable = curTable._newTable;
                        goto tryWithNewTable;
                    }

                    break;
                }

                reprobeCount++;
                curTable.ResizeOnReprobeCheck(reprobeCount);
                idx = (idx + reprobeCount) & lenMask;
            }

            value = default(TValue);
            return false;
        }

        internal sealed override bool RemoveIfMatch(TKey key, ref TValue oldVal, ValueMatch match)
        {
            Debug.Assert(
                match == ValueMatch.NotNullOrDead ||
                match == ValueMatch.OldValue ||
                match == ValueMatch.Any);

            int fullHash = this.hash(key);
            var curTable = this;

        tryWithNewTable:

            var lenMask = curTable._entries.Length - 1;
            int idx = ReduceHashToIndex(fullHash, lenMask);
            ref Entry entry = ref curTable._entries[idx];

            int reprobeCount = 0;
            while (true)
            {
                var entryHash = entry.hash;

                if (fullHash == entryHash && curTable.keyEqual(key, ref entry))
                {
                    break;
                }

                if (entryHash == 0)
                {
                    oldVal = default;
                    goto FAILED;
                }

                if (entryHash == TOMBPRIMEHASH || reprobeCount >= ReprobeLimit(lenMask))
                {
                    if (curTable._newTable != null)
                    {
                        curTable.HelpCopy();
                        curTable = curTable._newTable;
                        goto tryWithNewTable;
                    }

                    oldVal = default;
                    goto FAILED;
                }

                reprobeCount++;
                curTable.ResizeOnReprobeCheck(reprobeCount);
                idx = (idx + reprobeCount) & lenMask;
                entry = ref curTable._entries[idx];
            }

            long entryState = Interlocked.Read(ref entry.state);
            var newTable = curTable._newTable;

            if (newTable != null || entryState == TAG_TOMBPRIME)
            {
                var newTable1 = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                Debug.Assert(newTable == newTable1 || (newTable == null && newTable1 == this));
                curTable = newTable1;
                goto tryWithNewTable;
            }

            while (true)
            {
                Debug.Assert(!IsPrime(entryState));

                if (StateNullOrDead(entryState))
                {
                    oldVal = default;
                    goto FAILED;
                }

                if (match == ValueMatch.OldValue)
                {
                    TValue cur = DecodeValue(entryState);
                    if (!EqualityComparer<TValue>.Default.Equals(oldVal, cur))
                    {
                        oldVal = cur;
                        goto FAILED;
                    }
                }

                var prev = Interlocked.CompareExchange(ref entry.state, TAG_TOMBSTONE, entryState);
                if (prev == entryState)
                {
                    if (match == ValueMatch.NotNullOrDead)
                    {
                        oldVal = DecodeValue(prev);
                    }

                    curTable._size.Decrement();
                    SweepCheck();
                    return true;
                }

                if (IsPrime(prev))
                {
                    curTable = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                    goto tryWithNewTable;
                }

                entryState = prev;
            }

        FAILED:
            return false;
        }

        internal sealed override bool PutIfMatch(TKey key, TValue newVal, ref TValue oldVal, ValueMatch match)
        {
            Debug.Assert(
                match == ValueMatch.NullOrDead ||
                match == ValueMatch.OldValue ||
                match == ValueMatch.Any);

            int fullHash = this.hash(key);
            var curTable = this;

        tryWithNewTable:

            var lenMask = curTable._entries.Length - 1;
            int idx = ReduceHashToIndex(fullHash, lenMask);
            ref Entry entry = ref curTable._entries[idx];

            int reprobeCount = 0;
            while (true)
            {
                var entryHash = entry.hash;
                if (entryHash == 0)
                {
                    Debug.Assert(fullHash != 0);
                    entryHash = Interlocked.CompareExchange(ref entry.hash, fullHash, 0);
                    if (entryHash == 0)
                    {
                        entryHash = fullHash;
                        if (entryHash == ZEROHASH)
                        {
                            curTable.allocatedSlotCount.Increment();
                            break;
                        }
                    }
                }

                if (entryHash == fullHash)
                {
                    if (curTable.TryClaimSlotForPut(ref entry, key))
                    {
                        break;
                    }
                }

                if (entryHash == TOMBPRIMEHASH || reprobeCount >= ReprobeLimit(lenMask))
                {
                    var newTable1 = curTable.Resize(isForReprobe: true);
                    curTable.HelpCopy();
                    curTable = newTable1;
                    goto tryWithNewTable;
                }

                reprobeCount++;
                curTable.ResizeOnReprobeCheck(reprobeCount);
                idx = (idx + reprobeCount) & lenMask;
                entry = ref curTable._entries[idx];
            }

            // Found the proper Key slot, now update the Value.
            long entryState = Interlocked.Read(ref entry.state);

            var newTable = curTable._newTable;

            // both "null": no new table and an empty value slot
            if (newTable == null && IsEmpty(entryState) && curTable.TableIsCrowded())
            {
                newTable = curTable.Resize(isForReprobe: false);
                Debug.Assert(curTable._newTable != null && newTable == curTable._newTable);
            }

            if (newTable != null || entryState == TAG_TOMBPRIME)
            {
                var newTable1 = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                Debug.Assert(newTable == newTable1 || (newTable == null && newTable1 == this));
                curTable = newTable1;
                goto tryWithNewTable;
            }

            while (true)
            {
                Debug.Assert(!IsPrime(entryState));
                var entryStateNullOrDead = StateNullOrDead(entryState);

                switch (match)
                {
                    case ValueMatch.Any:
                        // no box to mutate in place: the CAS below IS the write.
                        break;

                    case ValueMatch.OldValue:
                        if (entryStateNullOrDead)
                        {
                            goto FAILED;
                        }

                        if (!EqualityComparer<TValue>.Default.Equals(oldVal, DecodeValue(entryState)))
                        {
                            goto FAILED;
                        }
                        break;

                    default:
                        Debug.Assert(match == ValueMatch.NullOrDead);
                        if (!entryStateNullOrDead)
                        {
                            oldVal = DecodeValue(entryState);
                            goto FAILED;
                        }
                        break;
                }

                long newState = EncodeValue(newVal);
                var prev = Interlocked.CompareExchange(ref entry.state, newState, entryState);
                if (prev == entryState)
                {
                    if (entryStateNullOrDead)
                    {
                        curTable._size.Increment();
                    }

                    return true;
                }

                // If a Prime got installed, re-run the put on the new table.
                if (IsPrime(prev))
                {
                    curTable = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                    goto tryWithNewTable;
                }

                // lost the CAS to another racing put; retry.
                entryState = prev;
            }

        FAILED:
            return false;
        }

        internal sealed override TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            long newState = 0;
            bool haveNewState = false;
            TValue result = default(TValue);

            int fullHash = this.hash(key);
            var curTable = this;

        TRY_WITH_NEW_TABLE:

            var lenMask = curTable._entries.Length - 1;
            int idx = ReduceHashToIndex(fullHash, lenMask);
            ref Entry entry = ref curTable._entries[idx];

            int reprobeCount = 0;
            while (true)
            {
                var entryHash = entry.hash;
                if (entryHash == 0)
                {
                    Debug.Assert(fullHash != 0);
                    entryHash = Interlocked.CompareExchange(ref entry.hash, fullHash, 0);
                    if (entryHash == 0)
                    {
                        entryHash = fullHash;
                        if (entryHash == ZEROHASH)
                        {
                            curTable.allocatedSlotCount.Increment();
                            break;
                        }
                    }
                }

                if (entryHash == fullHash)
                {
                    if (curTable.TryClaimSlotForPut(ref entry, key))
                    {
                        break;
                    }
                }

                if (entryHash == TOMBPRIMEHASH || reprobeCount >= ReprobeLimit(lenMask))
                {
                    var newTable1 = curTable.Resize(isForReprobe: true);
                    curTable.HelpCopy();
                    curTable = newTable1;
                    goto TRY_WITH_NEW_TABLE;
                }

                reprobeCount++;
                curTable.ResizeOnReprobeCheck(reprobeCount);
                idx = (idx + reprobeCount) & lenMask;
                entry = ref curTable._entries[idx];
            }

            long entryState = Interlocked.Read(ref entry.state);

            var newTable = curTable._newTable;

            if (newTable == null && IsEmpty(entryState) && curTable.TableIsCrowded())
            {
                newTable = curTable.Resize(isForReprobe: false);
                Debug.Assert(curTable._newTable != null && curTable._newTable == newTable);
            }

            if (newTable != null || entryState == TAG_TOMBPRIME)
            {
                var newTable1 = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                Debug.Assert(newTable == newTable1);
                curTable = newTable;
                goto TRY_WITH_NEW_TABLE;
            }

            if (!StateNullOrDead(entryState))
            {
                goto GOT_PREV_VALUE;
            }

            if (!haveNewState)
            {
                result = valueFactory(key);
                newState = EncodeValue(result);
                haveNewState = true;
            }

            while (true)
            {
                Debug.Assert(!IsPrime(entryState));

                var prev = Interlocked.CompareExchange(ref entry.state, newState, entryState);
                if (prev == entryState)
                {
                    curTable._size.Increment();
                    goto DONE;
                }

                if (IsPrime(prev))
                {
                    curTable = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                    goto TRY_WITH_NEW_TABLE;
                }

                entryState = prev;
                if (entryState != TAG_TOMBSTONE)
                {
                    goto GOT_PREV_VALUE;
                }
            }

        GOT_PREV_VALUE:
            result = DecodeValue(entryState);

        DONE:
            return result;
        }

        private bool PutSlotCopy(TKey key, long valueState, int fullHash)
        {
            Debug.Assert(!IsEmpty(valueState));
            Debug.Assert(valueState != TAG_TOMBSTONE);
            Debug.Assert(!IsPrime(valueState));

            var curTable = this;

        TRY_WITH_NEW_TABLE:

            var lenMask = curTable._entries.Length - 1;
            int idx = ReduceHashToIndex(fullHash, lenMask);
            ref Entry entry = ref curTable._entries[idx];

            int reprobeCount = 0;
            while (true)
            {
                var entryHash = entry.hash;
                if (entryHash == 0)
                {
                    Debug.Assert(fullHash != 0);
                    entryHash = Interlocked.CompareExchange(ref entry.hash, fullHash, 0);
                    if (entryHash == 0)
                    {
                        entryHash = fullHash;
                        if (entryHash == ZEROHASH)
                        {
                            curTable.allocatedSlotCount.Increment();
                            break;
                        }
                    }
                }

                if (entryHash == fullHash)
                {
                    if (curTable.TryClaimSlotForCopy(ref entry, key))
                    {
                        break;
                    }
                }

                if (entryHash == TOMBPRIMEHASH || reprobeCount >= ReprobeLimit(lenMask))
                {
                    var resized = curTable.Resize(isForReprobe: true);
                    curTable = resized;
                    goto TRY_WITH_NEW_TABLE;
                }

                reprobeCount++;
                idx = (idx + reprobeCount) & lenMask;
                entry = ref curTable._entries[idx];
            }

            long entryState = Interlocked.Read(ref entry.state);

            var newTable = curTable._newTable;

            if (newTable == null && IsEmpty(entryState) && curTable.TableIsCrowded())
            {
                newTable = curTable.Resize(isForReprobe: false);
                Debug.Assert(curTable._newTable != null && curTable._newTable == newTable);
            }

            if (newTable != null || entryState == TAG_TOMBPRIME)
            {
                var newTable1 = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: false);
                Debug.Assert(newTable == newTable1);
                curTable = newTable;
                goto TRY_WITH_NEW_TABLE;
            }

            // copy installs only over an empty slot; if anything is already there,
            // a racing put in the new table happened-after and must win.
            return IsEmpty(entryState) &&
                   Interlocked.CompareExchange(ref entry.state, valueState, 0L) == 0L;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ResizeOnReprobeCheck(int reprobeCount)
        {
            const int reprobeCheckPeriod = 16 - 1;
            if ((reprobeCount & reprobeCheckPeriod) == 0)
            {
                ReprobeResizeCheckSlow();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReprobeResizeCheckSlow()
        {
            if (this.TableIsCrowded())
            {
                this.Resize(isForReprobe: false);
                this.HelpCopy();
            }
        }

        ///////////////////////////////////////////////////////////
        // Resize support
        ///////////////////////////////////////////////////////////

        internal int Size
        {
            get
            {
                var size = _size.Value;
                var negMask = ~(size >> 31);
                return size & negMask;
            }
        }

        internal int EstimatedSlotsUsed
        {
            get { return (int)allocatedSlotCount.EstimatedValue; }
        }

        internal bool TableIsCrowded()
        {
            // 75% utilization, switch to a bigger table
            return EstimatedSlotsUsed >= (_entries.Length / 4 * 3);
        }

        internal void HelpCopy(bool copy_all = false)
        {
            var newTable = this._newTable;
            var oldEntries = this._entries;
            int toCopy = oldEntries.Length;

#if DEBUG
            const int CHUNK_SIZE = 16;
#else
            const int CHUNK_SIZE = 1024;
#endif
            int MIN_COPY_WORK = Math.Min(toCopy, CHUNK_SIZE);

            bool panic = false;
            int claimedChunk = -1;

            while (this._copyDone < toCopy)
            {
                if (!panic)
                {
                    claimedChunk = this._claimedChunk;

                    while (true)
                    {
                        if (claimedChunk > (toCopy / (CHUNK_SIZE / 2)))
                        {
                            panic = true;
                            break;
                        }

                        var alreadyClaimed = Interlocked.CompareExchange(ref this._claimedChunk, claimedChunk + 1, claimedChunk);
                        if (alreadyClaimed == claimedChunk)
                        {
                            break;
                        }

                        claimedChunk = alreadyClaimed;
                    }
                }
                else
                {
                    if (claimedChunk > ((toCopy / (CHUNK_SIZE / 2)) + toCopy / CHUNK_SIZE))
                    {
                        _copyDone = toCopy;
                        PromoteNewTable();
                        return;
                    }

                    claimedChunk++;
                }

                int workdone = 0;
                int copyStart = claimedChunk * CHUNK_SIZE;
                for (int i = 0; i < MIN_COPY_WORK; i++)
                {
                    if (this._copyDone >= toCopy)
                    {
                        PromoteNewTable();
                        return;
                    }

                    if (CopySlot(ref oldEntries[(copyStart + i) & (toCopy - 1)], newTable))
                    {
                        workdone++;
                    }
                }

                if (workdone > 0)
                {
                    var copyDone = Interlocked.Add(ref this._copyDone, workdone);
                    if (copyDone >= toCopy)
                    {
                        PromoteNewTable();
                    }
                }

                if (!(copy_all | panic))
                {
                    return;
                }
            }

            PromoteNewTable();
        }

        private void PromoteNewTable()
        {
            if (_topDict._table == this)
            {
                if (Interlocked.CompareExchange(ref _topDict._table, this._newTable, this) == this)
                {
                    _topDict._lastResizeTickMillis = CurrentTickMillis();
                }
            }
        }

        internal DictionaryImplStructKeyValAtomic<TKey, TValue> CopySlotAndGetNewTable(ref Entry entry, bool shouldHelp)
        {
            var newTable = this._newTable;

            Debug.Assert(IsPrime(Interlocked.Read(ref entry.state)) || newTable != null);

            if (newTable == null)
            {
                Debug.Assert(!typeof(TKey).GetTypeInfo().IsValueType);
                // help with sweeping in case the sweeper thread is stuck.
                entry.hash = SPECIAL_HASH_BITS;
                entry.key = default(TKey);

                return this;
            }

            if (CopySlot(ref entry, newTable))
            {
                var copyDone = Interlocked.Increment(ref this._copyDone);
                if (copyDone >= this._entries.Length)
                {
                    PromoteNewTable();
                    return newTable;
                }
            }

            if (shouldHelp)
            {
                this.HelpCopy();
            }

            return newTable;
        }

        // Copy one K/V pair from old table to new table. Returns true if we did the copy.
        private static bool CopySlot(ref Entry oldEntry, DictionaryImplStructKeyValAtomic<TKey, TValue> newTable)
        {
            Debug.Assert(newTable != null);

            var hash = oldEntry.hash;
            if (hash == 0)
            {
                hash = Interlocked.CompareExchange(ref oldEntry.hash, TOMBPRIMEHASH, 0);
                if (hash == 0)
                {
                    // slot was not claimed, copy is done here
                    return true;
                }
            }

            if (hash == TOMBPRIMEHASH)
            {
                // slot was trivially copied, but not by us
                return false;
            }

            // NOTE: the read of the value below must happen-before the read of the key.
            // Interlocked.Read is an acquire load (and 32-bit atomic), and the priming CAS
            // below is a full fence, so the later key read is correctly ordered. This is the
            // analog of DictionaryImpl`3.CopySlot's Volatile.Read(ref box.originalValue).
            long oldState = Interlocked.Read(ref oldEntry.state);

            if (IsPrime(oldState))
            {
                // already primed by someone else; re-read as an acquire fence before the key read.
                oldState = Interlocked.Read(ref oldEntry.state);
            }
            else
            {
                while (true)
                {
                    long primed = StateNullOrDead(oldState) ? TAG_TOMBPRIME : (oldState | TAG_PRIME);

                    // CAS down a primed version of oldState. Also a full fence.
                    long prev = Interlocked.CompareExchange(ref oldEntry.state, primed, oldState);

                    if (prev == oldState)
                    {
                        if (primed == TAG_TOMBPRIME)
                        {
                            // we vacuously copied an absent value; readers may skip to the new table.
                            return true;
                        }

                        // oldState is now primed by us; still needs copying into the new table.
                        oldState = primed;
                        break;
                    }

                    oldState = prev;
                    if (IsPrime(oldState))
                    {
                        break;
                    }
                }
            }

            if (oldState == TAG_TOMBPRIME)
            {
                // Copy already complete here, but not by us.
                return false;
            }

            // oldState is PRIME(value); recover the live VALUE encoding (clear the prime bit).
            long valueState = (oldState & PAYLOAD_MASK) | TAG_VALUE;

            // value was CASed/primed before the key, and we fenced above, so this read is ok.
            var key = oldEntry.key;
            bool copiedIntoNew = newTable.PutSlotCopy(key, valueState, hash);

            // Now hide the old value with TOMBPRIME to stop other threads re-copying.
            if (Interlocked.Read(ref oldEntry.state) != TAG_TOMBPRIME)
            {
                Interlocked.Exchange(ref oldEntry.state, TAG_TOMBPRIME);
            }

            return copiedIntoNew;
        }

        private DictionaryImplStructKeyValAtomic<TKey, TValue> Resize(bool isForReprobe)
        {
            return this._newTable ?? ResizeImpl(isForReprobe);
        }

        private DictionaryImplStructKeyValAtomic<TKey, TValue> ResizeImpl(bool isForReprobe)
        {
            const int MAX_SIZE = 1 << 30;
            const int MAX_CHURN_SIZE = 1 << 20;

            int oldlen = this._entries.Length;

            int oldsz = Size;
            int newsz = oldsz <= (MAX_SIZE / 4) ?
                                        oldsz * 4 :
                                        oldsz <= (MAX_SIZE / 2) ?
                                            oldsz * 2 :
                                            oldsz;

            newsz = Math.Max(newsz, MIN_SIZE);

            if (isForReprobe)
            {
                if (allocatedSlotCount.Value < oldsz * 2)
                {
                    if (oldlen <= (MAX_SIZE / 2))
                    {
                        newsz = Math.Max(newsz, oldlen * 2);
                    }
                }
            }

            if (newsz <= oldlen)
            {
                var resizeSpan = CurrentTickMillis() - _topDict._lastResizeTickMillis;

                if (resizeSpan < RESIZE_MILLIS_TARGET)
                {
                    newsz = oldlen < MAX_CHURN_SIZE ? oldlen << 1 : oldlen;
                }
                else
                {
                    newsz = Math.Max(newsz, (int)((long)oldlen * RESIZE_MILLIS_TARGET / resizeSpan));
                }
            }

            newsz = Util.AlignToPowerOfTwo(newsz);

            int kBs = newsz >> (10 - 4);

            var newTable = this._newTable;

            if (kBs > 0 && Interlocked.Increment(ref _resizers) >= 2)
            {
                if (newTable != null)
                {
                    return newTable;
                }

                SpinWait.SpinUntil(() => this._newTable != null, kBs >> 5);
            }

            newTable = this._newTable;
            if (newTable != null)
            {
                return newTable;
            }

            newTable = this.CreateNew(newsz);

            var prev = this._newTable ??
                        Interlocked.CompareExchange(ref this._newTable, newTable, null);

            if (prev != null)
            {
                return prev;
            }
            else
            {
                return newTable;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SweepCheck()
        {
            if (_topDict._sweepRequests == 0)
            {
                _topDict._sweepRequests = 1;
                Sweeper.TryRearm(this);
            }
        }

        private class Sweeper
        {
            private DictionaryImplStructKeyValAtomic<TKey, TValue> _dict;

            public static void TryRearm(DictionaryImplStructKeyValAtomic<TKey, TValue> dict)
            {
                ref var sweeperLocation = ref dict._topDict._sweeperInstance;
                var obj = sweeperLocation;
                if (obj != null && Interlocked.CompareExchange(ref sweeperLocation, null, obj) == obj)
                {
                    Sweeper sweeper = obj as Sweeper ?? new Sweeper();
                    sweeper._dict = dict;
                    GC.ReRegisterForFinalize(sweeper);
                }
            }

            ~Sweeper()
            {
                Task.Run(() => Sweep());
            }

            private void Sweep()
            {
                var dict = _dict;
                this._dict = null;

                if (dict == null)
                {
                    return;
                }

                Interlocked.Exchange(ref dict._topDict._sweepRequests, 0);

                var entries = dict._entries;
                for (int i = 0; i < entries.Length; i++)
                {
                    if (dict._newTable != null)
                    {
                        do
                        {
                            dict.HelpCopy(copy_all: true);
                            dict = dict._newTable;
                        } while (dict._newTable != null);
                        break;
                    }

                    ref var e = ref entries[i];
                    if (Interlocked.Read(ref e.state) == TAG_TOMBSTONE)
                    {
                        if (Interlocked.CompareExchange(ref e.state, TAG_TOMBPRIME, TAG_TOMBSTONE) == TAG_TOMBSTONE)
                        {
                            e.hash = SPECIAL_HASH_BITS;
                            e.key = default(TKey);
                            Interlocked.Increment(ref dict._copyDone);
                        }
                    }
                }

                dict._topDict._sweeperInstance = this;
                if (dict._topDict._sweepRequests != 0)
                {
                    TryRearm(dict);
                }
            }
        }

        ///////////////////////////////////////////////////////////
        // Snapshot / enumeration
        ///////////////////////////////////////////////////////////

        internal override Snapshot GetSnapshot()
        {
            return new SnapshotImpl(this);
        }

        internal override DictionaryImpl<TKey, TValue> GetTableForSnapshot()
        {
            var table = this;
            while (table._newTable != null)
            {
                table.HelpCopy(copy_all: true);
                table = (DictionaryImplStructKeyValAtomic<TKey, TValue>)table._topDict._table;
            }

            return table;
        }

        internal override bool MoveNextSnapshot(ref int idx, out TKey key, out TValue value)
        {
            var entries = this._entries;
            while (idx < entries.Length)
            {
                ref var nextEntry = ref entries[idx++];

                if (!IsEmpty(Interlocked.Read(ref nextEntry.state)))
                {
                    var nextKstore = nextEntry.key;
                    if (nextKstore == null)
                    {
                        // ref-key slot was deleted (compile-time false for value keystores)
                        continue;
                    }

                    var curKey = keyFromEntry(nextKstore);
                    if (TryGetValueUnboxed(curKey, out var curValue))
                    {
                        key = curKey;
                        value = curValue;
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

            public SnapshotImpl(DictionaryImplStructKeyValAtomic<TKey, TValue> dict)
            {
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
