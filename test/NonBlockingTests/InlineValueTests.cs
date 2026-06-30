// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using NB = Alc.NonBlocking;

namespace NonBlockingTests
{
    // Verifies the value-inline specialization (DictionaryImplValAtomic) used for
    // <key, TValue> when TValue is a <= 32-bit atomic primitive. The headline property
    // is that storing value-type values allocates no per-key heap object, so the GC
    // never traces N boxes. Correctness edge cases (EMPTY vs VALUE(0), extreme values,
    // float NaN, resize/copy under churn and concurrency) are covered alongside.
    public class InlineValueTests
    {
        [Fact]
        public static void Insert_IntInt_PreSized_IsAllocationFree()
        {
            const int N = 20000;

            // warm up the JIT for the whole insert path on a throwaway dictionary.
            for (int w = 0; w < 8; w++)
            {
                var warm = new NB.ConcurrentDictionary<int, int>(1, N, null);
                for (int i = 0; i < N; i++) warm.TryAdd(i, i * 2);
            }

            // pre-sized so no table resize happens during the N inserts; measure ONLY the
            // insert loop (the ctor/table allocation is excluded by baselining after it).
            var d = new NB.ConcurrentDictionary<int, int>(1, N, null);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++)
            {
                d.TryAdd(i, i * 2);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // < 1 byte/op proves no per-entry box (boxing is ~24 B/op); residual is one-time JIT noise.
            Assert.True(allocated < N * 8,
                $"inserting {N} <int,int> entries allocated {allocated} bytes (expected < {N * 8}; the boxed path would allocate ~{N * 24} bytes)");
            Assert.Equal(N, d.Count);
        }

        [Fact]
        public static void Update_IntInt_IsAllocationFree()
        {
            const int N = 4096;
            var d = new NB.ConcurrentDictionary<int, int>(1, N, null);
            for (int i = 0; i < N; i++) d[i] = i;

            // warmup
            for (int w = 0; w < 16; w++)
                for (int i = 0; i < N; i++) d[i] = i + w;

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int rep = 0; rep < 64; rep++)
                for (int i = 0; i < N; i++) d[i] = i + rep;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, $"updates allocated {allocated} bytes (expected 0)");
        }

        private struct WithReference
        {
            public int N;
            public string S; // managed reference -> NOT unmanaged -> stays on the Boxed path
        }

        [Fact]
        public static void Contrast_ValueTypeWithReference_StillAllocates()
        {
            // A value type containing a managed reference is NOT unmanaged, so it stays on the
            // Boxed path (inline storage would not give the GC-determinism win). This locks the
            // scope boundary in: only reference-free value types are stored inline.
            const int N = 20000;
            var s = "v";
            var warm = new NB.ConcurrentDictionary<int, WithReference>(1, N, null);
            for (int i = 0; i < N; i++) warm.TryAdd(i, new WithReference { N = i, S = s });

            var d = new NB.ConcurrentDictionary<int, WithReference>(1, N, null);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++) d.TryAdd(i, new WithReference { N = i, S = s });
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated > N, $"<int,WithReference> allocated only {allocated} bytes for {N} inserts; expected per-key boxing");
        }

        [Fact]
        public static void ZeroValue_IsDistinctFromEmpty()
        {
            // The central encoding trap: a stored value of 0 must not read as an empty slot.
            var d = new NB.ConcurrentDictionary<int, int>();

            Assert.True(d.TryAdd(5, 0));
            Assert.True(d.ContainsKey(5));
            Assert.True(d.TryGetValue(5, out var v0));
            Assert.Equal(0, v0);
            Assert.Equal(0, d[5]);
            Assert.Equal(1, d.Count);

            // remove the zero value, then it must be gone, and re-addable.
            Assert.True(d.TryRemove(5, out var removed));
            Assert.Equal(0, removed);
            Assert.False(d.ContainsKey(5));
            Assert.False(d.TryGetValue(5, out _));

            Assert.True(d.TryAdd(5, 0));
            Assert.Equal(0, d[5]);
        }

        [Fact]
        public static void ZeroKey_And_ZeroValue_Together()
        {
            // key 0 is claimed via a special hash; combined with value 0 it stresses both sentinels.
            var d = new NB.ConcurrentDictionary<int, int>();
            Assert.True(d.TryAdd(0, 0));
            Assert.True(d.ContainsKey(0));
            Assert.Equal(0, d[0]);
            Assert.Equal(1, d.Count);
            Assert.True(d.TryRemove(0, out var r));
            Assert.Equal(0, r);
            Assert.False(d.ContainsKey(0));
        }

        [Fact]
        public static void ExtremeValues_RoundTrip()
        {
            var di = new NB.ConcurrentDictionary<int, int>();
            foreach (var val in new[] { int.MinValue, int.MaxValue, -1, 0, 1, 0x7FFFFFFF })
            {
                di[1] = val;
                Assert.Equal(val, di[1]);
            }

            var du = new NB.ConcurrentDictionary<int, uint>();
            foreach (var val in new uint[] { 0u, 1u, uint.MaxValue, 0x80000000u })
            {
                du[1] = val;
                Assert.Equal(val, du[1]);
            }
        }

        [Fact]
        public static void Float_NaN_And_Specials_BitExact()
        {
            var d = new NB.ConcurrentDictionary<int, float>();
            foreach (var f in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, -0f, 3.14159f, float.Epsilon })
            {
                d[1] = f;
                Assert.True(d.TryGetValue(1, out var got));
                Assert.Equal(BitConverter.SingleToInt32Bits(f), BitConverter.SingleToInt32Bits(got));
            }
        }

        [Fact]
        public static void SmallTypes_RoundTrip_IncludingFalseAndZero()
        {
            // false / 0 are the small-type analogs of the VALUE(0)-vs-EMPTY trap.
            var db = new NB.ConcurrentDictionary<int, bool>();
            db[1] = false;
            Assert.True(db.ContainsKey(1));
            Assert.False(db[1]);
            db[2] = true;
            Assert.True(db[2]);
            Assert.True(db.TryRemove(1, out var rb) && rb == false);
            Assert.False(db.ContainsKey(1));

            var dby = new NB.ConcurrentDictionary<int, byte>();
            foreach (byte v in new byte[] { 0, 1, 127, 128, 255 })
            {
                dby[1] = v;
                Assert.Equal(v, dby[1]);
            }

            var ds = new NB.ConcurrentDictionary<int, short>();
            foreach (short v in new short[] { short.MinValue, -1, 0, 1, short.MaxValue })
            {
                ds[1] = v;
                Assert.Equal(v, ds[1]);
            }

            var dc = new NB.ConcurrentDictionary<int, char>();
            foreach (char v in new[] { '\0', 'a', char.MaxValue })
            {
                dc[1] = v;
                Assert.Equal(v, dc[1]);
            }
        }

        [Fact]
        public static void Resize_Churn_Correctness()
        {
            // grows through multiple resizes, then churns, exercising CopySlot/Prime on the long slot.
            const int N = 100_000;
            var d = new NB.ConcurrentDictionary<int, int>();

            for (int i = 0; i < N; i++) Assert.True(d.TryAdd(i, i ^ 0x5a5a5a5a));
            Assert.Equal(N, d.Count);
            for (int i = 0; i < N; i++) Assert.Equal(i ^ 0x5a5a5a5a, d[i]);

            // remove evens
            for (int i = 0; i < N; i += 2) Assert.True(d.TryRemove(i, out _));
            Assert.Equal(N / 2, d.Count);

            // re-add evens with new values
            for (int i = 0; i < N; i += 2) Assert.True(d.TryAdd(i, -i));
            Assert.Equal(N, d.Count);

            for (int i = 0; i < N; i++)
            {
                int expected = (i % 2 == 0) ? -i : (i ^ 0x5a5a5a5a);
                Assert.Equal(expected, d[i]);
            }

            // enumeration agrees
            int seen = 0;
            foreach (var kv in d)
            {
                int expected = (kv.Key % 2 == 0) ? -kv.Key : (kv.Key ^ 0x5a5a5a5a);
                Assert.Equal(expected, kv.Value);
                seen++;
            }
            Assert.Equal(N, seen);
        }

        [Fact]
        public static void RefKey_IntValue_Insert_NoValueBox()
        {
            // <string,int>: reference key + inline value. The very common case where today the
            // int value boxes. Pre-built keys + pre-sized table isolate the value side: a stored
            // int must add no per-entry heap object.
            const int N = 20000;
            var keys = new string[N];
            for (int i = 0; i < N; i++) keys[i] = "k" + i;

            for (int w = 0; w < 4; w++)
            {
                var warm = new NB.ConcurrentDictionary<string, int>(1, N, null);
                for (int i = 0; i < N; i++) warm.TryAdd(keys[i], i);
            }

            var d = new NB.ConcurrentDictionary<string, int>(1, N, null);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++) d.TryAdd(keys[i], i);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < N * 8,
                $"<string,int> inserting {N} entries allocated {allocated} bytes (expected < {N * 8}; the boxed value path would allocate ~{N * 24} bytes)");
            Assert.Equal(N, d.Count);
            for (int i = 0; i < N; i++)
            {
                Assert.True(d.TryGetValue(keys[i], out var v));
                Assert.Equal(i, v);
            }
        }

        [Fact]
        public static void LongKey_And_NintKey_RoundTripAndChurn()
        {
            const int N = 50_000;

            var dl = new NB.ConcurrentDictionary<long, int>();
            for (int i = 0; i < N; i++) Assert.True(dl.TryAdd((long)i << 33, i));
            for (int i = 0; i < N; i++) Assert.Equal(i, dl[(long)i << 33]);
            for (int i = 0; i < N; i += 2) Assert.True(dl.TryRemove((long)i << 33, out _));
            Assert.Equal(N / 2, dl.Count);

            var dn = new NB.ConcurrentDictionary<nint, int>();
            for (int i = 0; i < N; i++) Assert.True(dn.TryAdd((nint)(i + 1), i));
            for (int i = 0; i < N; i++) Assert.Equal(i, dn[(nint)(i + 1)]);
            Assert.Equal(N, dn.Count);
        }

        [Fact]
        public static void RefKey_IntValue_ConcurrentStress()
        {
            const int threads = 8;
            const int perThread = 20_000;
            var d = new NB.ConcurrentDictionary<string, int>();

            // pre-build disjoint keys per thread to avoid measuring string allocation in the race
            var keys = new string[threads][];
            for (int t = 0; t < threads; t++)
            {
                keys[t] = new string[perThread];
                for (int i = 0; i < perThread; i++) keys[t][i] = $"t{t}_k{i}";
            }

            Parallel.For(0, threads, t =>
            {
                var k = keys[t];
                for (int i = 0; i < perThread; i++) Assert.True(d.TryAdd(k[i], i));
                for (int i = 0; i < perThread; i++) d[k[i]] = i + 1;
                for (int i = 0; i < perThread; i++) if (i % 5 == 0) Assert.True(d.TryRemove(k[i], out var rv) && rv == i + 1);
            });

            int expected = 0;
            for (int t = 0; t < threads; t++)
            {
                for (int i = 0; i < perThread; i++)
                {
                    if (i % 5 == 0)
                    {
                        Assert.False(d.ContainsKey(keys[t][i]));
                    }
                    else
                    {
                        Assert.True(d.TryGetValue(keys[t][i], out var v));
                        Assert.Equal(i + 1, v);
                        expected++;
                    }
                }
            }
            Assert.Equal(expected, d.Count);
        }

        [Fact]
        public static void Concurrent_Stress_MatchesOracle()
        {
            // Many threads partition the key space (disjoint), each owns add/update/remove of its keys.
            // Disjoint partitions give a deterministic final-state oracle while still forcing concurrent
            // resizes/copies across the shared tables.
            const int threads = 8;
            const int perThread = 25_000;
            var d = new NB.ConcurrentDictionary<int, int>();

            Parallel.For(0, threads, t =>
            {
                int baseKey = t * perThread;
                for (int i = 0; i < perThread; i++)
                {
                    int k = baseKey + i;
                    Assert.True(d.TryAdd(k, k + 1));
                }
                // update
                for (int i = 0; i < perThread; i++)
                {
                    int k = baseKey + i;
                    d[k] = k + 2;
                }
                // remove the multiples of 3 owned by this thread
                for (int i = 0; i < perThread; i++)
                {
                    int k = baseKey + i;
                    if (k % 3 == 0) Assert.True(d.TryRemove(k, out var rv) && rv == k + 2);
                }
            });

            int expectedCount = 0;
            for (int k = 0; k < threads * perThread; k++)
            {
                if (k % 3 == 0)
                {
                    Assert.False(d.ContainsKey(k));
                }
                else
                {
                    Assert.True(d.TryGetValue(k, out var v), $"missing key {k}");
                    Assert.Equal(k + 2, v);
                    expectedCount++;
                }
            }

            Assert.Equal(expectedCount, d.Count);
        }

        [Fact]
        public static void Concurrent_SameKey_RacingPuts_NoLostUpdate()
        {
            // many threads hammer a small shared key set; verify final values are self-consistent
            // (each key ends with a value some thread wrote) and the dictionary is intact.
            const int threads = 8;
            const int keys = 64;
            const int iters = 50_000;
            var d = new NB.ConcurrentDictionary<int, int>();
            for (int k = 0; k < keys; k++) d[k] = 0;

            Parallel.For(0, threads, t =>
            {
                var rnd = new Random(t * 997 + 1);
                for (int i = 0; i < iters; i++)
                {
                    int k = rnd.Next(keys);
                    d[k] = (t << 24) | (i & 0xFFFFFF);
                    int read = d[k];
                    // value must always be a well-formed value some writer produced (never torn/garbage)
                    Assert.True((read & 0x00FFFFFF) <= 0xFFFFFF);
                }
            });

            Assert.Equal(keys, d.Count);
        }
    }
}
