using System.Diagnostics;
using PCGauger.Metrics;

namespace PCGauger.Metrics.Providers;

/// <summary>
/// Stretch feature (chunk 1, item 7): top process by CPU and by RAM.
/// Uses unprivileged Process enumeration — WorkingSet64 for RAM and a
/// TotalProcessorTime delta for CPU%. Cheap because we already poll on a timer.
/// </summary>
public sealed class TopProcessProvider : IMetricProvider
{
    private readonly Dictionary<int, (string Name, TimeSpan Cpu)> _prev = new();
    private string _topCpuName = "-";
    private double _topCpuPct;
    private string _topRamName = "-";
    private ulong _topRamBytes;

    public void Update(TimeSpan elapsed)
    {
        double elapsedSec = elapsed.TotalSeconds;
        if (elapsedSec <= 0) elapsedSec = 0.25;

        Process[] processes = Process.GetProcesses();
        string topCpu = "-";
        double topCpuPct = 0;
        string topRam = "-";
        ulong topRamBytes = 0;

        foreach (var p in processes)
        {
            try
            {
                ulong ws = (ulong)p.WorkingSet64;
                if (ws > topRamBytes)
                {
                    topRamBytes = ws;
                    topRam = p.ProcessName;
                }

                var cpu = p.TotalProcessorTime;
                if (_prev.TryGetValue(p.Id, out var prev))
                {
                    double deltaSec = (cpu - prev.Cpu).TotalSeconds;
                    double pct = (deltaSec / elapsedSec) * 100.0 / Environment.ProcessorCount;
                    if (pct > topCpuPct)
                    {
                        topCpuPct = pct;
                        topCpu = p.ProcessName;
                    }
                }
                _prev[p.Id] = (p.ProcessName, cpu);
            }
            catch
            {
                // Access denied / exited mid-enumeration — skip.
            }
        }

        _topCpuName = topCpu;
        _topCpuPct = topCpuPct;
        _topRamName = topRam;
        _topRamBytes = topRamBytes;
    }

    public IEnumerable<Metric> GetMetrics()
    {
        yield return Metric.Text("proc.topcpu.name", "Top CPU", 0, _topCpuName);
        yield return Metric.Text("proc.topcpu.pct", "Top CPU %", _topCpuPct, "%");
        yield return Metric.Text("proc.topram.name", "Top RAM", 0, _topRamName);
        yield return Metric.Text("proc.topram.bytes", "Top RAM", _topRamBytes, "B");
    }

    public string TopCpuName => _topCpuName;
    public double TopCpuPct => _topCpuPct;
    public string TopRamName => _topRamName;
    public ulong TopRamBytes => _topRamBytes;
}
