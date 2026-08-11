using System.Diagnostics;

namespace MuseRAM.Core;

public sealed class AppOverheadSampler
{
    private TimeSpan? _previousCpuTime;
    private long? _previousSampledTimestamp;
    private ulong? _previousIoBytes;
    private long? _previousIoSampledTimestamp;

    public AppOverheadSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        var sampledTimestamp = Stopwatch.GetTimestamp();
        var cpuTime = process.TotalProcessorTime;
        var cpuPercent = 0d;
        var cpuReliable = false;
        if (_previousCpuTime.HasValue && _previousSampledTimestamp.HasValue)
        {
            var elapsed = Stopwatch.GetElapsedTime(_previousSampledTimestamp.Value, sampledTimestamp);
            if (elapsed > TimeSpan.FromMilliseconds(100) && cpuTime >= _previousCpuTime.Value)
            {
                cpuPercent = (cpuTime - _previousCpuTime.Value).TotalMilliseconds /
                    (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
                cpuPercent = Math.Clamp(cpuPercent, 0, 100);
                cpuReliable = true;
            }
        }

        _previousCpuTime = cpuTime;
        _previousSampledTimestamp = sampledTimestamp;
        var ioBytesPerSecond = 0d;
        var ioReliable = false;
        if (NativeMethods.GetProcessIoCounters(process.Handle, out var counters))
        {
            var ioBytes = counters.ReadTransferCount + counters.WriteTransferCount;
            if (_previousIoBytes.HasValue && _previousIoSampledTimestamp.HasValue)
            {
                var elapsed = Stopwatch.GetElapsedTime(_previousIoSampledTimestamp.Value, sampledTimestamp);
                if (elapsed > TimeSpan.FromMilliseconds(100) && ioBytes >= _previousIoBytes.Value)
                {
                    ioBytesPerSecond = (ioBytes - _previousIoBytes.Value) / elapsed.TotalSeconds;
                    ioReliable = true;
                }
            }
            _previousIoBytes = ioBytes;
            _previousIoSampledTimestamp = sampledTimestamp;
        }

        return new AppOverheadSnapshot(
            process.WorkingSet64,
            process.PrivateMemorySize64,
            cpuPercent,
            ioBytesPerSecond,
            process.Threads.Count,
            process.HandleCount,
            cpuReliable,
            ioReliable);
    }
}
