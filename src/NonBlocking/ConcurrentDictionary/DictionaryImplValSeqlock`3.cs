// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

//
// Phase 2: inline storage for larger / arbitrary UNMANAGED value types (Guid, decimal,
// DateTime, long, double, custom blittable structs) - anything that does not fit Phase 1's
// 32-bit encoded slot. The struct value is stored directly inside the Entry (so a dictionary
// of N such entries holds zero per-entry heap objects - the goal is deterministic GC), and a
// per-entry seqlock makes multi-word reads/writes tear-free.
//
// IMPORTANT trade-off (chosen explicitly by the team): this path is NOT strictly lock-free.
// A writer holds a per-slot lock for the duration of one struct copy; a writer preempted
// mid-copy briefly blocks readers/writers of THAT ONE slot. That stall is bounded and a few
// instructions long - orders of magnitude more deterministic than the gen2 GC pause this is
// designed to eliminate. The rest of the table (other slots) is unaffected.
//
// Per-entry control word `ctrl` (one CAS-able long):
//   bits 61..63 : state tag   EMPTY=0, VALUE, TOMBSTONE, PRIME(flag), TOMBPRIME=PRIME|TOMBSTONE
//   bits  0..60 : seqlock     even = stable, odd = a writer holds the slot lock; +2 per write
//   EMPTY is the all-zero word (tag 0, seq 0). A slot never returns to EMPTY once written.
//
// Seqlock read:  read ctrl (must be unlocked, not dead/prime) -> read value -> re-read ctrl;
//                if unchanged the value is consistent. Writer: CAS ctrl to odd (acquire) ->
//                write value -> Interlocked.Exchange ctrl to VALUE|seq+2 (release).
// Delete/prime are single CAS on ctrl (no value write, no lock needed); priming freezes the
// value (writers then divert to the new table), so the resize copy reads it safely.
//
// All ctrl access uses Volatile.Read(ref long) (atomic on 32-bit: the BCL falls back to
// Interlocked.CompareExchange there; a plain acquire load on 64-bit) or Interlocked
// (CompareExchange/Exchange) - never a plain or C#-volatile read. The version re-check
// after a payload read additionally requires a full fence; see TryReadValue.

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
    internal abstract class DictionaryImplValSeqlock<TKey, TKeyStore, TValue>
        : DictionaryImpl<TKey, TValue>
    {
        private readonly Entry[] _entries;
        internal DictionaryImplValSeqlock<TKey, TKeyStore, TValue> _newTable;

        protected readonly ConcurrentDictionary<TKey, TValue> _topDict;
        protected readonly Counter32 allocatedSlotCount = new Counter32();
        private Counter32 _size;

        private int _resizers;
        private int _claimedChunk;
        private int _copyDone;

        // control-word encoding ----------------------------------------------
        private const int TAG_SHIFT = 61;
        private const long SEQ_MASK = (1L << TAG_SHIFT) - 1;     // bits 0..60
        private const long TAG_MASK = ~SEQ_MASK;                 // bits 61..63
        private const long TAG_VALUE = 1L << TAG_SHIFT;
        private const long TAG_TOMBSTONE = 2L << TAG_SHIFT;
        private const long TAG_PRIME = 4L << TAG_SHIFT;
        private const long TAG_TOMBPRIME = TAG_PRIME | TAG_TOMBSTONE;
        // EMPTY == 0

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsEmpty(long c) => c == 0L;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsLocked(long c) => (c & 1L) != 0L;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsPrime(long c) => (c & TAG_PRIME) != 0L;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsTombPrime(long c) => (c & TAG_MASK) == TAG_TOMBPRIME;

        // mirrors EntryValueNullOrDead: empty slot or a (non-primed) tombstone.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool StateNullOrDead(long c) => c == 0L || (c & TAG_MASK) == TAG_TOMBSTONE;
        // ---------------------------------------------------------------------

        [DebuggerDisplay("key = {key}; hash = {hash}; ctrl = {ctrl}; value = {value};")]
        [StructLayout(LayoutKind.Sequential)]
        public struct Entry
        {
            internal int hash;
            internal TKeyStore key;
            internal long ctrl;
            internal TValue value;
        }

        private const int MIN_SIZE = 8;
        private const uint RESIZE_MILLIS_TARGET = 1000;

        protected abstract DictionaryImplValSeqlock<TKey, TKeyStore, TValue> CreateNew(int capacity);
        protected abstract TKey keyFromEntry(TKeyStore entryKey);
        protected abstract bool keyEqual(TKey key, TKeyStore entryKey);
        protected abstract bool TryClaimSlotForPut(ref TKeyStore entryKey, TKey key);
        protected abstract bool TryClaimSlotForCopy(ref TKeyStore entryKey, TKeyStore key);

        internal DictionaryImplValSeqlock(int capacity, ConcurrentDictionary<TKey, TValue> topDict)
        {
            capacity = Math.Max(capacity, MIN_SIZE);
            capacity = Util.AlignToPowerOfTwo(capacity);
            this._entries = new Entry[capacity];
            this._size = new Counter32();
            this._topDict = topDict;

#if NETSTANDARD2_1_OR_GREATER || NET
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
#else
            if (!typeof(TKeyStore).IsValueType)
#endif
            {
                topDict._sweeperInstance = NULLVALUE;
            }
        }

        protected DictionaryImplValSeqlock(int capacity, DictionaryImplValSeqlock<TKey, TKeyStore, TValue> other)
        {
            capacity = Util.AlignToPowerOfTwo(capacity);
            this._entries = new Entry[capacity];
            this._size = other._size;
            this._topDict = other._topDict;
            this._keyComparer = other._keyComparer;
        }

        private static uint CurrentTickMillis() => (uint)Environment.TickCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected virtual int hash(TKey key)
        {
            Debug.Assert(key is not null);
            int h = _keyComparer.GetHashCode(key);
            return h | (SPECIAL_HASH_BITS | (1 << 29));
        }

        internal sealed override int Count => this.Size;

        internal sealed override void Clear()
        {
            var newTable = CreateNew(MIN_SIZE);
            newTable._size = new Counter32();
            _topDict._table = newTable;
        }

        internal override object TryGetValue(TKey key)
        {
            throw new NotSupportedException(
                "DictionaryImplValSeqlock stores values inline; use TryGetValueUnboxed.");
        }

        // Reads `entry.value` consistently against the observed unlocked control word `c`.
        // Returns false if a concurrent writer changed the slot during the read (caller retries).
        //
        // MEMORY ORDERING (do not weaken):
        //  - the version-BEFORE read (the `c` the caller passes in) only needs an acquire load;
        //    Volatile.Read(ref long) is atomic even on 32-bit (the BCL implements it via
        //    Interlocked.CompareExchange there) and on 64-bit it is a plain acquire load, so hot
        //    readers do not take exclusive ownership of the slot's cache line.
        //  - the version-AFTER re-check below REQUIRES a full fence between the payload read and
        //    the re-read of `ctrl` (load-load: an acquire on the re-read alone does NOT order the
        //    preceding payload loads before it on ARM64). Thread.MemoryBarrier fences on a
        //    thread-private location, so concurrent readers still share the slot's cache line.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadValue(ref Entry entry, long c, out TValue value)
        {
            value = entry.value;
            Thread.MemoryBarrier();
            return Volatile.Read(ref entry.ctrl) == c;
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

                if (fullHash == entry.hash && keyEqual(key, entry.key))
                {
                    var sw = new SpinWait();
                    while (true)
                    {
                        long c = Volatile.Read(ref entry.ctrl);
                        if (StateNullOrDead(c))
                        {
                            value = default(TValue);
                            return false;
                        }

                        if (IsPrime(c))
                        {
                            curTable = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                            goto tryWithNewTable;
                        }

                        if (IsLocked(c))
                        {
                            sw.SpinOnce();
                            continue;
                        }

                        if (TryReadValue(ref entry, c, out value))
                        {
                            return true;
                        }

                        sw.SpinOnce();
                    }
                }

                if (entry.hash == 0)
                {
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
                if (fullHash == entryHash && curTable.keyEqual(key, entry.key))
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

            long c = Volatile.Read(ref entry.ctrl);
            var newTable = curTable._newTable;

            if (newTable != null || IsTombPrime(c))
            {
                var newTable1 = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                Debug.Assert(newTable == newTable1 || (newTable == null && newTable1 == this));
                curTable = newTable1;
                goto tryWithNewTable;
            }

            var sw = new SpinWait();
            while (true)
            {
                if (IsPrime(c))
                {
                    curTable = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                    goto tryWithNewTable;
                }

                if (IsLocked(c))
                {
                    sw.SpinOnce();
                    c = Volatile.Read(ref entry.ctrl);
                    continue;
                }

                if (StateNullOrDead(c))
                {
                    oldVal = default;
                    goto FAILED;
                }

                // live slot. Read the current value consistently (needed for OldValue compare
                // and NotNullOrDead reporting) before committing the tombstone.
                TValue removed = default;
                if (match != ValueMatch.Any)
                {
                    if (!TryReadValue(ref entry, c, out removed))
                    {
                        c = Volatile.Read(ref entry.ctrl);
                        continue;
                    }

                    if (match == ValueMatch.OldValue &&
                        !EqualityComparer<TValue>.Default.Equals(oldVal, removed))
                    {
                        oldVal = removed;
                        goto FAILED;
                    }
                }

                // single-CAS transition live -> TOMBSTONE (expects the same unlocked control word,
                // so any racing write/delete/prime since the read makes this fail and retry).
                long tomb = TAG_TOMBSTONE | ((c + 2) & SEQ_MASK);
                if (Interlocked.CompareExchange(ref entry.ctrl, tomb, c) == c)
                {
                    if (match == ValueMatch.NotNullOrDead)
                    {
                        oldVal = removed;
                    }

                    curTable._size.Decrement();
                    SweepCheck();
                    return true;
                }

                c = Volatile.Read(ref entry.ctrl);
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
                    if (curTable.TryClaimSlotForPut(ref entry.key, key))
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

            long c = Volatile.Read(ref entry.ctrl);
            var newTable = curTable._newTable;

            if (newTable == null && IsEmpty(c) && curTable.TableIsCrowded())
            {
                newTable = curTable.Resize(isForReprobe: false);
                Debug.Assert(curTable._newTable != null && newTable == curTable._newTable);
            }

            if (newTable != null || IsTombPrime(c))
            {
                var newTable1 = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                Debug.Assert(newTable == newTable1 || (newTable == null && newTable1 == this));
                curTable = newTable1;
                goto tryWithNewTable;
            }

            var sw = new SpinWait();
            while (true)
            {
                if (IsPrime(c))
                {
                    curTable = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                    goto tryWithNewTable;
                }

                if (IsLocked(c))
                {
                    sw.SpinOnce();
                    c = Volatile.Read(ref entry.ctrl);
                    continue;
                }

                bool dead = StateNullOrDead(c);

                // pre-lock checks that fail without writing
                if (match == ValueMatch.OldValue && dead)
                {
                    goto FAILED;
                }

                if (match == ValueMatch.NullOrDead && !dead)
                {
                    // report the existing value and fail (consistent read, no lock)
                    if (TryReadValue(ref entry, c, out oldVal))
                    {
                        goto FAILED;
                    }

                    c = Volatile.Read(ref entry.ctrl);
                    continue;
                }

                // pre-lock: compare on a consistent read (dead was handled above, so the slot is
                // live). The lock CAS below is keyed on the same observed `c`, so CAS success
                // proves the value has not changed since this compare - no user code (the value
                // comparer) ever runs while the per-slot lock is held. A throwing comparer would
                // otherwise leave `ctrl` odd forever, hanging every reader/writer of the slot.
                // This is the same pattern RemoveIfMatch uses.
                if (match == ValueMatch.OldValue)
                {
                    if (!TryReadValue(ref entry, c, out var currentVal))
                    {
                        c = Volatile.Read(ref entry.ctrl);
                        continue;
                    }

                    if (!EqualityComparer<TValue>.Default.Equals(oldVal, currentVal))
                    {
                        oldVal = currentVal;
                        goto FAILED;
                    }
                }

                // acquire the per-slot write lock (c is unlocked here)
                long locked = c | 1L;
                if (Interlocked.CompareExchange(ref entry.ctrl, locked, c) != c)
                {
                    c = Volatile.Read(ref entry.ctrl);
                    continue;
                }

                // === we own the slot ===
                entry.value = newVal;
                Interlocked.Exchange(ref entry.ctrl, TAG_VALUE | ((c + 2) & SEQ_MASK));
                if (dead)
                {
                    curTable._size.Increment();
                }

                return true;
            }

        FAILED:
            return false;
        }

        internal sealed override TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            TValue made = default(TValue);
            bool haveMade = false;

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
                    if (curTable.TryClaimSlotForPut(ref entry.key, key))
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

            long c = Volatile.Read(ref entry.ctrl);
            var newTable = curTable._newTable;

            if (newTable == null && IsEmpty(c) && curTable.TableIsCrowded())
            {
                newTable = curTable.Resize(isForReprobe: false);
                Debug.Assert(curTable._newTable != null && curTable._newTable == newTable);
            }

            if (newTable != null || IsTombPrime(c))
            {
                var newTable1 = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                Debug.Assert(newTable == newTable1 || (newTable == null && newTable1 == this));
                curTable = newTable1;
                goto TRY_WITH_NEW_TABLE;
            }

            var sw = new SpinWait();
            while (true)
            {
                if (IsPrime(c))
                {
                    curTable = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: true);
                    goto TRY_WITH_NEW_TABLE;
                }

                if (IsLocked(c))
                {
                    sw.SpinOnce();
                    c = Volatile.Read(ref entry.ctrl);
                    continue;
                }

                if (!StateNullOrDead(c))
                {
                    // present: return the existing value (consistent read)
                    if (TryReadValue(ref entry, c, out var existing))
                    {
                        return existing;
                    }

                    c = Volatile.Read(ref entry.ctrl);
                    continue;
                }

                if (!haveMade)
                {
                    made = valueFactory(key);
                    haveMade = true;
                }

                long locked = c | 1L;
                if (Interlocked.CompareExchange(ref entry.ctrl, locked, c) != c)
                {
                    c = Volatile.Read(ref entry.ctrl);
                    continue;
                }

                entry.value = made;
                Interlocked.Exchange(ref entry.ctrl, TAG_VALUE | ((c + 2) & SEQ_MASK));
                curTable._size.Increment();
                return made;
            }
        }

        private bool PutSlotCopy(TKeyStore key, TValue value, int fullHash)
        {
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
                    if (curTable.TryClaimSlotForCopy(ref entry.key, key))
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

            long c = Volatile.Read(ref entry.ctrl);
            var newTable = curTable._newTable;

            if (newTable == null && IsEmpty(c) && curTable.TableIsCrowded())
            {
                newTable = curTable.Resize(isForReprobe: false);
                Debug.Assert(curTable._newTable != null && curTable._newTable == newTable);
            }

            if (newTable != null || IsTombPrime(c))
            {
                var newTable1 = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: false);
                Debug.Assert(newTable == newTable1 || (newTable == null && newTable1 == this));
                curTable = newTable1;
                goto TRY_WITH_NEW_TABLE;
            }

            var sw = new SpinWait();
            while (true)
            {
                if (IsPrime(c))
                {
                    curTable = curTable.CopySlotAndGetNewTable(ref entry, shouldHelp: false);
                    goto TRY_WITH_NEW_TABLE;
                }

                if (IsLocked(c))
                {
                    sw.SpinOnce();
                    c = Volatile.Read(ref entry.ctrl);
                    continue;
                }

                // install only over an empty slot; if a real put already wrote a (newer) value,
                // leave it - that write happened-after this copy.
                if (!IsEmpty(c))
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref entry.ctrl, 1L /* 0 | lock */, 0L) != 0L)
                {
                    c = Volatile.Read(ref entry.ctrl);
                    continue;
                }

                entry.value = value;
                Interlocked.Exchange(ref entry.ctrl, TAG_VALUE | 2L);
                return true;
            }
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

        internal int EstimatedSlotsUsed => (int)allocatedSlotCount.EstimatedValue;

        internal bool TableIsCrowded() => EstimatedSlotsUsed >= (_entries.Length / 4 * 3);

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

        internal DictionaryImplValSeqlock<TKey, TKeyStore, TValue> CopySlotAndGetNewTable(ref Entry entry, bool shouldHelp)
        {
            var newTable = this._newTable;

            Debug.Assert(IsPrime(Volatile.Read(ref entry.ctrl)) || newTable != null);

            if (newTable == null)
            {
                Debug.Assert(!typeof(TKeyStore).GetTypeInfo().IsValueType);
                entry.hash = SPECIAL_HASH_BITS;
                entry.key = default(TKeyStore);
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

        private static bool CopySlot(ref Entry oldEntry, DictionaryImplValSeqlock<TKey, TKeyStore, TValue> newTable)
        {
            Debug.Assert(newTable != null);

            var hash = oldEntry.hash;
            if (hash == 0)
            {
                hash = Interlocked.CompareExchange(ref oldEntry.hash, TOMBPRIMEHASH, 0);
                if (hash == 0)
                {
                    return true;
                }
            }

            if (hash == TOMBPRIMEHASH)
            {
                return false;
            }

            long c = Volatile.Read(ref oldEntry.ctrl);
            var sw = new SpinWait();

            if (!IsPrime(c))
            {
                while (true)
                {
                    // a writer holds the slot lock - wait for release before priming, so the
                    // value is fully written and then frozen.
                    if (IsLocked(c))
                    {
                        sw.SpinOnce();
                        c = Volatile.Read(ref oldEntry.ctrl);
                        continue;
                    }

                    long primed = StateNullOrDead(c)
                        ? (TAG_TOMBPRIME | (c & SEQ_MASK))
                        : (c | TAG_PRIME);

                    long prev = Interlocked.CompareExchange(ref oldEntry.ctrl, primed, c);
                    if (prev == c)
                    {
                        if (StateNullOrDead(c))
                        {
                            return true;
                        }

                        c = primed;
                        break;
                    }

                    c = prev;
                    if (IsPrime(c))
                    {
                        break;
                    }
                }
            }

            if (IsTombPrime(c))
            {
                return false;
            }

            // c is a PRIMEd live VALUE. Priming only succeeds on an unlocked slot, after which no
            // writer can acquire the lock (they observe PRIME and divert), so the value is frozen
            // and this plain read is safe.
            TValue v = oldEntry.value;
            var key = oldEntry.key;
            bool copiedIntoNew = newTable.PutSlotCopy(key, v, hash);

            if (!IsTombPrime(Volatile.Read(ref oldEntry.ctrl)))
            {
                Interlocked.Exchange(ref oldEntry.ctrl, TAG_TOMBPRIME | (c & SEQ_MASK));
            }

            return copiedIntoNew;
        }

        private DictionaryImplValSeqlock<TKey, TKeyStore, TValue> Resize(bool isForReprobe)
        {
            return this._newTable ?? ResizeImpl(isForReprobe);
        }

        private DictionaryImplValSeqlock<TKey, TKeyStore, TValue> ResizeImpl(bool isForReprobe)
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

            return prev ?? newTable;
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
            private DictionaryImplValSeqlock<TKey, TKeyStore, TValue> _dict;

            public static void TryRearm(DictionaryImplValSeqlock<TKey, TKeyStore, TValue> dict)
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
                    long c = Volatile.Read(ref e.ctrl);
                    if (!IsLocked(c) && (c & TAG_MASK) == TAG_TOMBSTONE)
                    {
                        if (Interlocked.CompareExchange(ref e.ctrl, TAG_TOMBPRIME | (c & SEQ_MASK), c) == c)
                        {
                            e.hash = SPECIAL_HASH_BITS;
                            e.key = default(TKeyStore);
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
                table = (DictionaryImplValSeqlock<TKey, TKeyStore, TValue>)table._topDict._table;
            }

            return table;
        }

        internal override bool MoveNextSnapshot(ref int idx, out TKey key, out TValue value)
        {
            var entries = this._entries;
            while (idx < entries.Length)
            {
                ref var nextEntry = ref entries[idx++];

                if (!IsEmpty(Volatile.Read(ref nextEntry.ctrl)))
                {
                    var nextKstore = nextEntry.key;
                    if (nextKstore == null)
                    {
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

            public SnapshotImpl(DictionaryImplValSeqlock<TKey, TKeyStore, TValue> dict)
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
