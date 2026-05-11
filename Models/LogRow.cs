using System.Collections.Generic;

namespace BMSMonitor.Models;

/// <summary>One row in the live data stream table (values match enabled column order).</summary>
public sealed class LogRow
{
    public List<string> Values { get; } = new();
}
