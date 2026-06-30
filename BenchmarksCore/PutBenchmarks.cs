// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using BenchmarkDotNet.Attributes;
using NB = Alc.NonBlocking;
using SC = System.Collections.Concurrent;
using SG = System.Collections.Generic;

namespace NonBlockingTests
{
    // Run with: dotnet run -c Release -- bdn --filter *PutBenchmarks*
    //
    // The headline is the [MemoryDiagnoser] "Allocated" / Gen2 columns. NonBlocking value-inline
    // paths (Phase 1 atomic <int,int>, Phase 2 seqlock <int,long>) allocate only the Entry[]
    // backing arrays - ZERO per-entry heap object - so gen2 pressure does not scale with element
    // count. The Boxed row (<int, struct-with-reference>, which is not unmanaged so it keeps the
    // box) and System.Collections.Concurrent (a Node per entry) show the per-entry-object cost
    // this change eliminates.
    [MemoryDiagnoser]
    [MarkdownExporterAttribute.GitHub]
    [JsonExporterAttribute.FullCompressed]
    public class PutBenchmarks
    {
        // value type with a managed reference -> NOT unmanaged -> stays on the Boxed path.
        public struct Boxable
        {
            public long N;
            public object Ref;
        }

        [Params(1000, 100000)]
        public int Count;

        private NB.ConcurrentDictionary<int, int> _nbAtomic;
        private NB.ConcurrentDictionary<int, long> _nbSeqlock;
        private NB.ConcurrentDictionary<int, Boxable> _nbBoxed;
        private SC.ConcurrentDictionary<int, int> _sysConcurrent;

        [GlobalSetup]
        public void Setup()
        {
            _nbAtomic = new NB.ConcurrentDictionary<int, int>();
            _nbSeqlock = new NB.ConcurrentDictionary<int, long>();
            _nbBoxed = new NB.ConcurrentDictionary<int, Boxable>();
            _sysConcurrent = new SC.ConcurrentDictionary<int, int>();
            for (int i = 0; i < Count; i++)
            {
                _nbAtomic.TryAdd(i, i);
                _nbSeqlock.TryAdd(i, i);
                _nbBoxed.TryAdd(i, new Boxable { N = i });
                _sysConcurrent.TryAdd(i, i);
            }
        }

        // ---- Insert: build a fresh dictionary of Count distinct entries ----

        [Benchmark(Baseline = true)]
        public object Insert_SystemDictionary()
        {
            var d = new SG.Dictionary<int, int>();
            for (int i = 0; i < Count; i++) d[i] = i;
            return d;
        }

        [Benchmark]
        public object Insert_SystemConcurrent()
        {
            var d = new SC.ConcurrentDictionary<int, int>();
            for (int i = 0; i < Count; i++) d.TryAdd(i, i);
            return d;
        }

        [Benchmark]
        public object Insert_NonBlocking_Boxed()
        {
            var d = new NB.ConcurrentDictionary<int, Boxable>();
            for (int i = 0; i < Count; i++) d.TryAdd(i, new Boxable { N = i });
            return d;
        }

        [Benchmark]
        public object Insert_NonBlocking_Atomic()   // Phase 1: <int,int> inline encoded slot
        {
            var d = new NB.ConcurrentDictionary<int, int>();
            for (int i = 0; i < Count; i++) d.TryAdd(i, i);
            return d;
        }

        [Benchmark]
        public object Insert_NonBlocking_Seqlock()   // Phase 2: <int,long> inline seqlock slot
        {
            var d = new NB.ConcurrentDictionary<int, long>();
            for (int i = 0; i < Count; i++) d.TryAdd(i, i);
            return d;
        }

        // ---- Update: overwrite existing keys (steady state, no resize) ----

        [Benchmark]
        public void Update_NonBlocking_Atomic()
        {
            for (int i = 0; i < Count; i++) _nbAtomic[i] = i + 1;
        }

        [Benchmark]
        public void Update_NonBlocking_Seqlock()
        {
            for (int i = 0; i < Count; i++) _nbSeqlock[i] = i + 1;
        }

        [Benchmark]
        public void Update_NonBlocking_Boxed()
        {
            for (int i = 0; i < Count; i++) _nbBoxed[i] = new Boxable { N = i + 1 };
        }

        // ---- Lookup (all hits) ----

        [Benchmark]
        public long Lookup_NonBlocking_Atomic()
        {
            long s = 0;
            for (int i = 0; i < Count; i++)
            {
                _nbAtomic.TryGetValue(i, out var v);
                s += v;
            }
            return s;
        }

        [Benchmark]
        public long Lookup_NonBlocking_Seqlock()
        {
            long s = 0;
            for (int i = 0; i < Count; i++)
            {
                _nbSeqlock.TryGetValue(i, out var v);
                s += v;
            }
            return s;
        }

        [Benchmark]
        public long Lookup_SystemConcurrent()
        {
            long s = 0;
            for (int i = 0; i < Count; i++)
            {
                _sysConcurrent.TryGetValue(i, out var v);
                s += v;
            }
            return s;
        }
    }
}
