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
    // The headline is the [MemoryDiagnoser] "Allocated" column, compared across four backings:
    //   - Insert_SystemDictionary    : System.Collections.Generic.Dictionary (single-thread baseline,
    //                                   not thread-safe; backing arrays only, no per-entry object)
    //   - Insert_SystemConcurrent    : System.Collections.Concurrent.ConcurrentDictionary
    //                                   (thread-safe peer; allocates a Node object per entry)
    //   - Insert_NonBlocking_Boxed   : Alc.NonBlocking <int,long> - out-of-scope 64-bit value, so it
    //                                   stays on the Boxed path and allocates a box per entry. This is
    //                                   the in-library stand-in for "the value-type path before this change".
    //   - Insert_NonBlocking_Inline  : Alc.NonBlocking <int,int> - the new inline path; only the Entry[]
    //                                   backing arrays are allocated, ZERO per-entry heap object.
    //
    // Insert allocation = backing-array growth (all impls) + per-entry object (everyone except Inline).
    // The Inline-vs-Boxed gap isolates the eliminated box; Update is steady-state and should be 0 B/op
    // for Inline. ns/op shows the inline path is no slower than the boxed path it replaces.
    [MemoryDiagnoser]
    [MarkdownExporterAttribute.GitHub]
    [JsonExporterAttribute.FullCompressed]
    public class PutBenchmarks
    {
        [Params(1000, 100000)]
        public int Count;

        private NB.ConcurrentDictionary<int, int> _nbInline;
        private NB.ConcurrentDictionary<int, long> _nbBoxed;
        private SC.ConcurrentDictionary<int, int> _sysConcurrent;

        [GlobalSetup]
        public void Setup()
        {
            _nbInline = new NB.ConcurrentDictionary<int, int>();
            _nbBoxed = new NB.ConcurrentDictionary<int, long>();
            _sysConcurrent = new SC.ConcurrentDictionary<int, int>();
            for (int i = 0; i < Count; i++)
            {
                _nbInline.TryAdd(i, i);
                _nbBoxed.TryAdd(i, i);
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
            var d = new NB.ConcurrentDictionary<int, long>();
            for (int i = 0; i < Count; i++) d.TryAdd(i, i);
            return d;
        }

        [Benchmark]
        public object Insert_NonBlocking_Inline()
        {
            var d = new NB.ConcurrentDictionary<int, int>();
            for (int i = 0; i < Count; i++) d.TryAdd(i, i);
            return d;
        }

        // ---- Update: overwrite existing keys (steady state, no resize) ----

        [Benchmark]
        public void Update_SystemConcurrent()
        {
            for (int i = 0; i < Count; i++) _sysConcurrent[i] = i + 1;
        }

        [Benchmark]
        public void Update_NonBlocking_Boxed()
        {
            for (int i = 0; i < Count; i++) _nbBoxed[i] = i + 1;
        }

        [Benchmark]
        public void Update_NonBlocking_Inline()
        {
            for (int i = 0; i < Count; i++) _nbInline[i] = i + 1;
        }

        // ---- Lookup (all hits) ----

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

        [Benchmark]
        public long Lookup_NonBlocking_Inline()
        {
            long s = 0;
            for (int i = 0; i < Count; i++)
            {
                _nbInline.TryGetValue(i, out var v);
                s += v;
            }
            return s;
        }
    }
}
