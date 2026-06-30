// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

#nullable disable

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Alc.NonBlocking
{
    // Write-once commit protocol for a value-type key stored INLINE in an entry. Shared by every
    // inline-struct-key impl (boxed-value, atomic-value, seqlock-value) so the protocol lives in one
    // place.
    //
    // A multi-word value-type key cannot be written atomically, but a slot is keyed exactly once
    // (never re-keyed), so a simple per-entry commit flag is enough - no seqlock/version. After the
    // slot's hash is claimed (== fullHash), exactly one writer transitions keyState
    // UNCLAIMED -> WRITING, writes the key, then publishes COMMITTED with a release store. Readers and
    // competing writers observe an acquire-ordered keyState:
    //   - a reader treats a non-committed slot as "not this key" (the insert has not linearized);
    //   - a competing writer for the same slot spins to COMMITTED, then compares via the comparer.
    //
    // Matching is comparer-based, NOT bit-based, so aliasing types (e.g. DateTime ticks-equal but
    // Kind-different, decimal 0m == 0.0m) behave exactly like the framework comparer. "Slot used" is
    // the keyState/hash, never the key bits, so default(TKey) (all-zero) is an ordinary key.
    internal static class InlineStructKey
    {
        internal const int KEY_UNCLAIMED = 0;
        internal const int KEY_WRITING = 1;
        internal const int KEY_COMMITTED = 2;

        // Reader/lookup match: true only if the inline key is committed AND equals 'key'.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Equal<TKey>(ref int keyState, ref TKey keyField, TKey key, IEqualityComparer<TKey> comparer)
        {
            // acquire: orders the key read after the committed observation.
            if (Volatile.Read(ref keyState) != KEY_COMMITTED)
            {
                return false;
            }

            return comparer.Equals(key, keyField);
        }

        // Claim this (hash-claimed) slot for 'key' by writing the key once, or report whether it
        // already holds 'key'. Returns true if the slot is now this key's slot.
        internal static bool TryClaim<TKey>(ref int keyState, ref TKey keyField, TKey key, IEqualityComparer<TKey> comparer, Counter32 slotCount)
        {
            int ks = Volatile.Read(ref keyState);
            if (ks == KEY_UNCLAIMED &&
                Interlocked.CompareExchange(ref keyState, KEY_WRITING, KEY_UNCLAIMED) == KEY_UNCLAIMED)
            {
                // we own the slot: write the key, then publish (release).
                keyField = key;
                Volatile.Write(ref keyState, KEY_COMMITTED);
                slotCount.Increment();
                return true;
            }

            // another writer is mid-write (or just committed); wait for commit, then compare.
            if (ks != KEY_COMMITTED)
            {
                var sw = new SpinWait();
                while (Volatile.Read(ref keyState) != KEY_COMMITTED)
                {
                    sw.SpinOnce();
                }
            }

            return comparer.Equals(key, keyField);
        }
    }
}
