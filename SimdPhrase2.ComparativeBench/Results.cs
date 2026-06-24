using System.Collections.Generic;

namespace SimdPhrase2.ComparativeBench;

/// <summary>Indexing throughput, reported the way luceneutil does (docs/sec and content GB/hour).</summary>
internal sealed record IndexStats(int DocCount, double IndexSeconds, double ForceMergeSeconds, long ContentBytes)
{
    public double DocsPerSecond => IndexSeconds > 0 ? DocCount / IndexSeconds : 0;
    public double MegabytesPerSecond => IndexSeconds > 0 ? ContentBytes / (1024.0 * 1024.0) / IndexSeconds : 0;
    public double GigabytesPerHour => MegabytesPerSecond * 3600.0 / 1024.0;
}

/// <summary>Aggregate concurrent-search throughput and parallel speed-up.</summary>
internal sealed record ConcurrentResult(int Threads, double AggregateQps, double SingleThreadQps)
{
    public double Speedup => SingleThreadQps > 0 ? AggregateQps / SingleThreadQps : 0;
    public double Efficiency => Threads > 0 ? Speedup / Threads : 0;
}

/// <summary>Faceting throughput.</summary>
internal sealed record FacetsResult(double CountsPerSecond, int DimValues)
{
    public double CountLatencyMs => CountsPerSecond > 0 ? 1000.0 / CountsPerSecond : 0;
}

/// <summary>All measured results for one engine (SimdPhrase2 or legacy Lucene.NET).</summary>
internal sealed class EngineResults
{
    public required string Engine { get; init; }
    public IndexStats? Indexing { get; set; }
    public Dictionary<string, double> SearchQps { get; } = new();
    public ConcurrentResult? Concurrent { get; set; }
    public FacetsResult? Facets { get; set; }
}
