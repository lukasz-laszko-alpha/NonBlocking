// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NB = Alc.NonBlocking;

namespace NonBlockingTests
{
    // Regression tests pinning concurrency defects in the inline key/value storage protocols:
    //  - a user value comparer that throws inside seqlock PutIfMatch(OldValue) must not
    //    permanently poison the per-slot lock (readers/writers of that slot would hang);
    //  - the tombstone sweeper must not re-key inline struct-key slots (write-once protocol),
    //    otherwise enumeration produces phantom default(TKey) pairs after a sweep;
    //  - non-generic ICollection.CopyTo(DictionaryEntry[], 0) must accept an empty
    //    dictionary copied into an empty array (parity with CopyToPairs/CopyToObjects).
    public class InlineStorageRegressionTests
    {
        // 16-byte unmanaged value -> dispatches to the seqlock value path.
        private struct ThrowingEquatableVal : IEquatable<ThrowingEquatableVal>
        {
            public long A;
            public long B;

            public bool Equals(ThrowingEquatableVal other) => throw new InvalidOperationException("user comparer failure");
            public override bool Equals(object o) => throw new InvalidOperationException("user comparer failure");
            public override int GetHashCode() => A.GetHashCode();
        }

        // struct key containing a reference -> inline-struct-key path with (formerly) armed sweeper.
        private struct RefKey : IEquatable<RefKey>
        {
            public string S;

            public bool Equals(RefKey other) => string.Equals(S, other.S, StringComparison.Ordinal);
            public override bool Equals(object o) => o is RefKey k && Equals(k);
            public override int GetHashCode() => S?.GetHashCode() ?? 0;
        }

        private static void AssertReadCompletes<TKey, TValue>(NB.ConcurrentDictionary<TKey, TValue> dict, TKey key)
        {
            var read = Task.Run(() => dict.TryGetValue(key, out _));
            Assert.True(
                read.Wait(TimeSpan.FromSeconds(10)),
                "TryGetValue hung: the slot seqlock stayed locked after a throwing value comparer.");
        }

        [Fact]
        public static void SeqlockTryUpdate_ThrowingValueComparer_DoesNotPoisonSlot_IntKey()
        {
            var dict = new NB.ConcurrentDictionary<int, ThrowingEquatableVal>();
            Assert.True(dict.TryAdd(42, new ThrowingEquatableVal { A = 1, B = 2 }));

            Assert.Throws<InvalidOperationException>(
                () => dict.TryUpdate(42, new ThrowingEquatableVal { A = 3 }, new ThrowingEquatableVal { A = 1, B = 2 }));

            // the user exception must not leave the per-slot lock held
            AssertReadCompletes(dict, 42);

            var put = Task.Run(() => dict[42] = new ThrowingEquatableVal { A = 7 });
            Assert.True(put.Wait(TimeSpan.FromSeconds(10)), "indexer set hung: slot seqlock stayed locked.");
        }

        [Fact]
        public static void SeqlockTryUpdate_ThrowingValueComparer_DoesNotPoisonSlot_StructKey()
        {
            var key = new Guid(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
            var dict = new NB.ConcurrentDictionary<Guid, ThrowingEquatableVal>();
            Assert.True(dict.TryAdd(key, new ThrowingEquatableVal { A = 1, B = 2 }));

            Assert.Throws<InvalidOperationException>(
                () => dict.TryUpdate(key, new ThrowingEquatableVal { A = 3 }, new ThrowingEquatableVal { A = 1, B = 2 }));

            AssertReadCompletes(dict, key);

            var put = Task.Run(() => dict[key] = new ThrowingEquatableVal { A = 7 });
            Assert.True(put.Wait(TimeSpan.FromSeconds(10)), "indexer set hung: slot seqlock stayed locked.");
        }

        // Shared body: add a live default(RefKey), churn N other keys, provoke the sweeper
        // (GC -> finalizer -> Task.Run(Sweep)), then verify enumeration stays phantom-free.
        private static void SweepPhantomCheck<TValue>(NB.ConcurrentDictionary<RefKey, TValue> dict, Func<int, TValue> value)
        {
            const int N = 512;

            Assert.True(dict.TryAdd(default, value(0)));
            for (int i = 1; i <= N; i++)
            {
                Assert.True(dict.TryAdd(new RefKey { S = "k" + i }, value(i)));
            }

            for (int i = 1; i <= N; i++)
            {
                Assert.True(dict.TryRemove(new RefKey { S = "k" + i }, out _));
            }

            // the sweeper (when armed) runs from a finalizer via Task.Run; give it every chance
            var deadline = Environment.TickCount + 3000;
            while (Environment.TickCount < deadline)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);

                var pairs = dict.ToArray();
                Assert.True(dict.Count == 1, $"Count drifted to {dict.Count}; expected 1.");
                Assert.True(
                    pairs.Length == 1,
                    $"enumeration produced {pairs.Length} pairs for a 1-element dictionary (phantom default(TKey) duplicates from the sweeper).");
                Assert.Equal(default(RefKey), pairs[0].Key);

                int seen = 0;
                foreach (var p in dict) seen++;
                Assert.True(seen == 1, $"foreach produced {seen} pairs for a 1-element dictionary.");
            }
        }

        [Fact]
        public static void StructKeyWithRef_SweepIsPhantomFree_BoxedValue()
        {
            // string value -> DictionaryImplStructKey (boxed values)
            SweepPhantomCheck(new NB.ConcurrentDictionary<RefKey, string>(1, 2048, null), i => "v" + i);
        }

        [Fact]
        public static void StructKeyWithRef_SweepIsPhantomFree_AtomicValue()
        {
            // int value -> DictionaryImplStructKeyValAtomic
            SweepPhantomCheck(new NB.ConcurrentDictionary<RefKey, int>(1, 2048, null), i => i);
        }

        [Fact]
        public static void StructKeyWithRef_SweepIsPhantomFree_SeqlockValue()
        {
            // 16-byte unmanaged value -> DictionaryImplStructKeyValSeqlock
            SweepPhantomCheck(new NB.ConcurrentDictionary<RefKey, Guid>(1, 2048, null), i => new Guid(i, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8));
        }

        [Fact]
        public static void CopyTo_EmptyDictionaryToEmptyEntryArray_DoesNotThrow()
        {
            var dict = new NB.ConcurrentDictionary<int, int>();
            ((ICollection)dict).CopyTo(Array.Empty<DictionaryEntry>(), 0);
            ((ICollection)dict).CopyTo(Array.Empty<System.Collections.Generic.KeyValuePair<int, int>>(), 0);
            ((ICollection)dict).CopyTo(Array.Empty<object>(), 0);
        }
    }
}
