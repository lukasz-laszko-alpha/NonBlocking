// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using NB = Alc.NonBlocking;

namespace NonBlockingTests
{
    // Phase 3b: value-type keys (Guid/decimal/DateTime/byte/short/float/custom structs and
    // byte/short-backed enums) stored INLINE via the write-once commit protocol
    // (DictionaryImplStructKey), eliminating Boxed<TKey>. Matching is comparer-based (not bit-based),
    // so aliasing cases like DateTime.Kind behave exactly like the framework comparer.
    public class InlineStructKeyTests
    {
        private struct Key24 : IEquatable<Key24>
        {
            public long A;
            public long B;
            public int C;
            public bool Equals(Key24 o) => A == o.A && B == o.B && C == o.C;
            public override bool Equals(object o) => o is Key24 k && Equals(k);
            public override int GetHashCode() => (A, B, C).GetHashCode();
        }

        [Fact]
        public static void GuidKey_CorrectAndKeyAllocationFree()
        {
            const int N = 20000;
            var keys = new Guid[N];
            for (int i = 0; i < N; i++) keys[i] = new Guid(i, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8);
            var shared = "v"; // reference value -> no value box; isolates the key side

            for (int w = 0; w < 8; w++)
            {
                var warm = new NB.ConcurrentDictionary<Guid, string>(1, N, null);
                for (int i = 0; i < N; i++) warm.TryAdd(keys[i], shared);
            }

            var d = new NB.ConcurrentDictionary<Guid, string>(1, N, null);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++) d.TryAdd(keys[i], shared);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // key stored inline (no Boxed<Guid>); value is a shared ref. Expect no per-entry box.
            Assert.True(allocated < N * 8, $"<Guid,string> insert of {N} allocated {allocated} bytes (expected < {N * 8}; boxed key would allocate ~{N * 32})");
            Assert.Equal(N, d.Count);
            for (int i = 0; i < N; i++) Assert.Equal(shared, d[keys[i]]);

            // remove + re-add
            for (int i = 0; i < N; i += 2) Assert.True(d.TryRemove(keys[i], out _));
            Assert.Equal(N / 2, d.Count);
            for (int i = 0; i < N; i += 2) Assert.True(d.TryAdd(keys[i], shared));
            Assert.Equal(N, d.Count);
        }

        [Fact]
        public static void DefaultGuidKey_Works()
        {
            // default(Guid) == Guid.Empty: an all-zero key must be a normal entry (the commit flag,
            // not key bits, marks a slot used - so there is no zero-key special case here).
            var d = new NB.ConcurrentDictionary<Guid, int>();
            Assert.True(d.TryAdd(Guid.Empty, 42));
            Assert.True(d.ContainsKey(Guid.Empty));
            Assert.Equal(42, d[Guid.Empty]);
            Assert.True(d.TryRemove(Guid.Empty, out var v) && v == 42);
            Assert.False(d.ContainsKey(Guid.Empty));
        }

        [Fact]
        public static void DateTimeKey_MatchesFrameworkComparer()
        {
            // DateTime equality is ticks-only (ignores Kind), so the same ticks with different Kind
            // are equal under the default comparer but have different bits. The commit protocol uses
            // the comparer, so behavior must match a framework Dictionary oracle exactly.
            var nb = new NB.ConcurrentDictionary<DateTime, int>();
            var oracle = new Dictionary<DateTime, int>();

            var samples = new[]
            {
                new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(new DateTime(2020, 1, 1).Ticks, DateTimeKind.Unspecified),
                new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local),
                DateTime.MinValue,
                DateTime.MaxValue,
                new DateTime(2021, 6, 15, 12, 30, 0, DateTimeKind.Utc),
            };

            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    nb[samples[i]] = i + round * 100;
                    oracle[samples[i]] = i + round * 100;
                }
            }

            Assert.Equal(oracle.Count, nb.Count);
            foreach (var kv in oracle)
            {
                Assert.True(nb.TryGetValue(kv.Key, out var v));
                Assert.Equal(kv.Value, v);
            }
        }

        [Fact]
        public static void DecimalKey_RoundTrip()
        {
            var d = new NB.ConcurrentDictionary<decimal, string>();
            foreach (var k in new[] { decimal.MinValue, -1.5m, 0m, 0.0m, 1m, 123.456m, decimal.MaxValue })
            {
                d[k] = k.ToString();
                Assert.Equal(k.ToString(), d[k]);
            }
            // 0m and 0.0m are equal by the decimal comparer
            Assert.True(d.ContainsKey(0m));
        }

        [Fact]
        public static void CustomStructKey_24Bytes_Correct()
        {
            const int N = 50_000;
            var d = new NB.ConcurrentDictionary<Key24, int>();
            Key24 K(int i) => new Key24 { A = i, B = ~(long)i, C = i * 7 };

            for (int i = 0; i < N; i++) Assert.True(d.TryAdd(K(i), i));
            Assert.Equal(N, d.Count);
            for (int i = 0; i < N; i++) Assert.Equal(i, d[K(i)]);
            Assert.True(d.TryAdd(default, -1)); // all-zero struct key
            Assert.Equal(-1, d[default]);
        }

        [Fact]
        public static void GuidKey_Concurrent_DisjointPartitions()
        {
            const int threads = 8;
            const int perThread = 20_000;
            var d = new NB.ConcurrentDictionary<Guid, int>();

            Parallel.For(0, threads, t =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    var k = new Guid(t * perThread + i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                    Assert.True(d.TryAdd(k, t * perThread + i));
                }
            });

            Assert.Equal(threads * perThread, d.Count);
            for (int x = 0; x < threads * perThread; x++)
            {
                var k = new Guid(x, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                Assert.True(d.TryGetValue(k, out var v));
                Assert.Equal(x, v);
            }
        }

        [Fact]
        public static void StructKey_Concurrent_SameKeys_NoDuplicatesNoTorn()
        {
            // Every thread inserts the SAME set of distinct struct keys. The write-once commit
            // protocol must let exactly one writer create each key slot (no duplicates) and never
            // expose a half-written key (which would corrupt matching). Final count must equal the
            // distinct key count and every value must be well-formed.
            const int threads = 8;
            const int keyCount = 30_000;
            var d = new NB.ConcurrentDictionary<Key24, int>();

            Key24 K(int i) => new Key24 { A = i, B = (long)i << 20, C = i * 7 };

            Parallel.For(0, threads, t =>
            {
                for (int i = 0; i < keyCount; i++)
                {
                    d[K(i)] = i;            // racing writers to the same key
                    Assert.True(d.TryGetValue(K(i), out var v));
                    Assert.Equal(i, v);     // must always read a well-formed value for this key
                }
            });

            Assert.Equal(keyCount, d.Count); // no duplicates created by the race
            for (int i = 0; i < keyCount; i++)
            {
                Assert.True(d.TryGetValue(K(i), out var v));
                Assert.Equal(i, v);
            }
        }

        [Fact]
        public static void StructKey_Concurrent_AddRemove_AcrossResizes()
        {
            const int threads = 8;
            const int perThread = 20_000;
            var d = new NB.ConcurrentDictionary<Key24, int>();
            Key24 K(int x) => new Key24 { A = x, B = -x, C = x };

            Parallel.For(0, threads, t =>
            {
                int baseK = t * perThread;
                for (int i = 0; i < perThread; i++) Assert.True(d.TryAdd(K(baseK + i), baseK + i));
                for (int i = 0; i < perThread; i++) if ((baseK + i) % 4 == 0) Assert.True(d.TryRemove(K(baseK + i), out _));
            });

            int expected = 0;
            for (int x = 0; x < threads * perThread; x++)
            {
                if (x % 4 == 0) Assert.False(d.ContainsKey(K(x)));
                else { Assert.True(d.TryGetValue(K(x), out var v)); Assert.Equal(x, v); expected++; }
            }
            Assert.Equal(expected, d.Count);
        }
    }
}
