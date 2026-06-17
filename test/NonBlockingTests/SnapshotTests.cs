// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using NB = Alc.NonBlocking;

namespace NonBlockingTests
{
    public class SnapshotTests
    {
        private static NB.ConcurrentDictionary<int, int> MakeIntDict(int n)
        {
            var d = new NB.ConcurrentDictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                d.TryAdd(i, i * 2);
            }

            return d;
        }

        [Fact]
        public static void Snapshot_IsStableAcrossMutation()
        {
            var d = MakeIntDict(100);
            using var snap = d.TakeSnapshot();

            // Mutate the dictionary after snapshotting.
            for (int i = 100; i < 200; i++)
            {
                d.TryAdd(i, i * 2);
            }

            d.TryRemove(0, out _);
            d[1] = -1;

            // The snapshot must be unchanged.
            Assert.Equal(100, snap.Count);

            var keys = new HashSet<int>();
            foreach (var kv in snap)
            {
                Assert.Equal(kv.Key * 2, kv.Value);
                Assert.True(keys.Add(kv.Key));
            }

            Assert.True(new HashSet<int>(Enumerable.Range(0, 100)).SetEquals(keys));
        }

        [Fact]
        public static void Snapshot_IsRepeatable()
        {
            var d = MakeIntDict(50);
            using var snap = d.TakeSnapshot();

            var first = new List<int>();
            foreach (var kv in snap)
            {
                first.Add(kv.Key);
            }

            var second = new List<int>();
            foreach (var kv in snap)
            {
                second.Add(kv.Key);
            }

            Assert.Equal(first, second);
        }

        [Fact]
        public static void Snapshot_KeysAndValues_Match()
        {
            var d = MakeIntDict(300);
            using var snap = d.TakeSnapshot();

            Assert.Equal(300, snap.Keys.Count);
            Assert.Equal(300, snap.Values.Count);

            var keys = new HashSet<int>();
            foreach (var k in snap.Keys)
            {
                keys.Add(k);
            }

            var values = new HashSet<int>();
            foreach (var v in snap.Values)
            {
                values.Add(v);
            }

            Assert.True(new HashSet<int>(Enumerable.Range(0, 300)).SetEquals(keys));
            Assert.True(new HashSet<int>(Enumerable.Range(0, 300).Select(i => i * 2)).SetEquals(values));
        }

        [Fact]
        public static void Snapshot_Indexer_Works()
        {
            var d = MakeIntDict(10);
            using var snap = d.TakeSnapshot();

            int sum = 0;
            for (int i = 0; i < snap.Count; i++)
            {
                Assert.Equal(snap[i].Key * 2, snap[i].Value);
                Assert.Equal(snap.Keys[i] * 2, snap.Values[i]);
                sum += snap[i].Key;
            }

            Assert.Equal(Enumerable.Range(0, 10).Sum(), sum);
        }

        [Fact]
        public static void Snapshot_EmptyDictionary()
        {
            var d = new NB.ConcurrentDictionary<int, int>();
            using var snap = d.TakeSnapshot();

            Assert.Equal(0, snap.Count);
            foreach (var _ in snap)
            {
                throw new Exception("empty snapshot should not enumerate any element");
            }
        }

        [Fact]
        public static void Snapshot_DoubleDispose_IsSafe()
        {
            var d = MakeIntDict(10);
            var snap = d.TakeSnapshot();
            snap.Dispose();
            snap.Dispose();
        }

        [Fact]
        public static void Snapshot_Enumeration_IsAllocationFree()
        {
            var d = MakeIntDict(1000);
            using var snap = d.TakeSnapshot();
            long sum = 0;

            // Enumerating a captured snapshot touches only struct enumerators over the
            // already-rented buffer, so it allocates nothing. (The rent/return cycle itself
            // reuses pooled buffers but is not asserted here, since ArrayPool.Shared releases
            // cached buffers on Gen2 GC, which other tests can trigger concurrently.)
            for (int w = 0; w < 16; w++)
            {
                foreach (var kv in snap) sum += kv.Value;
                foreach (var k in snap.Keys) sum += k;
                foreach (var v in snap.Values) sum += v;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int rep = 0; rep < 64; rep++)
            {
                foreach (var kv in snap) sum += kv.Value;
                foreach (var k in snap.Keys) sum += k;
                foreach (var v in snap.Values) sum += v;
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            GC.KeepAlive(sum);
            Assert.Equal(0, after - before);
        }

        [Fact]
        public static void TakeSnapshot_HandlesGrowthAndShrinkBetweenCalls()
        {
            var d = MakeIntDict(10);

            using (var snap = d.TakeSnapshot())
            {
                Assert.Equal(10, snap.Count);
            }

            for (int i = 10; i < 1000; i++)
            {
                d.TryAdd(i, i * 2);
            }

            using (var snap = d.TakeSnapshot())
            {
                Assert.Equal(1000, snap.Count);
                var seen = new HashSet<int>();
                foreach (var kv in snap)
                {
                    Assert.Equal(kv.Key * 2, kv.Value);
                    seen.Add(kv.Key);
                }

                Assert.Equal(1000, seen.Count);
            }
        }

        [Fact]
        public static void TakeSnapshot_DoesNotDropEntries_UnderConcurrentInserts()
        {
            // Pre-size generously so the inserts below never trigger a resize. This isolates the
            // behavior under test (the snapshot's reliance on the approximate size counter) from
            // any resize/migration races.
            const int Target = 50_000;
            var d = new NB.ConcurrentDictionary<int, int>(Environment.ProcessorCount, Target * 4);

            // High-water mark: the largest key index the writer has fully committed. It is published
            // with a release write *after* the insert returns, so a reader that observes it with an
            // acquire read is guaranteed that every key in [0, hw] is present and (never being
            // removed) must therefore appear in any snapshot taken afterwards.
            int[] hwBox = { -1 };

            var done = new ManualResetEventSlim(false);
            var writer = new Thread(() =>
            {
                for (int k = 0; k < Target; k++)
                {
                    d[k] = k * 2;
                    Volatile.Write(ref hwBox[0], k);
                }

                done.Set();
            })
            { IsBackground = true };

            writer.Start();

            string failure = null;
            int iterations = 0;

            while (!done.IsSet && failure == null)
            {
                iterations++;

                int hwBefore = Volatile.Read(ref hwBox[0]);
                if (hwBefore < 0)
                {
                    continue;
                }

                using var snap = d.TakeSnapshot();

                var present = new HashSet<int>(snap.Count);
                foreach (var kv in snap)
                {
                    if (kv.Value != kv.Key * 2)
                    {
                        failure = $"snapshot exposed an inconsistent value for key {kv.Key}: {kv.Value}";
                        break;
                    }

                    present.Add(kv.Key);
                }

                for (int i = 0; i <= hwBefore && failure == null; i++)
                {
                    if (!present.Contains(i))
                    {
                        failure =
                            $"snapshot dropped key {i}, which was present before the snapshot was taken " +
                            $"(snapshot held {present.Count} entries; at least {hwBefore + 1} were present).";
                    }
                }
            }

            writer.Join();

            Assert.True(failure == null, $"{failure} (after {iterations} snapshot iterations)");
        }
    }
}
