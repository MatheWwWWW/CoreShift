using System.Diagnostics;

namespace SmartResourceDispatcher.Core;

internal readonly record struct FocusObservation(long Timestamp, TimeSpan Cpu, long WorkingSet, ulong? CommitHeadroom);

internal sealed record FocusPhaseResult(double Seconds, int Intervals, double CpuPercent,
    long AverageWorkingSet, long PeakWorkingSet, ulong? MinimumCommitHeadroom);

internal sealed class FocusPhaseMeasurement(int logicalProcessors)
{
    private FocusObservation? previous;
    private double seconds;
    private double cpuSeconds;
    private double weightedMemory;
    private long peakMemory;
    private ulong? minimumHeadroom;
    private int intervals;

    internal double Seconds => seconds;

    internal void Observe(FocusObservation? observation)
    {
        var old = previous;
        previous = observation;
        if (old is not { } start || observation is not { } end)
            return;

        double elapsed = Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp).TotalSeconds;
        double work = (end.Cpu - start.Cpu).TotalSeconds;
        if (elapsed <= 0 || elapsed > 2.5 || work < 0 || start.WorkingSet < 0 || end.WorkingSet < 0)
            return;

        seconds += elapsed;
        cpuSeconds += Math.Min(work, elapsed * logicalProcessors);
        weightedMemory += (start.WorkingSet * .5 + end.WorkingSet * .5) * elapsed;
        peakMemory = Math.Max(peakMemory, Math.Max(start.WorkingSet, end.WorkingSet));
        IncludeHeadroom(start.CommitHeadroom);
        IncludeHeadroom(end.CommitHeadroom);
        intervals++;
    }

    private void IncludeHeadroom(ulong? headroom)
    {
        if (headroom is { } value && (minimumHeadroom is null || value < minimumHeadroom.Value))
            minimumHeadroom = value;
    }

    internal FocusPhaseResult? ReadResult() => seconds > 0 && logicalProcessors > 0
        ? new FocusPhaseResult(seconds, intervals, 100 * cpuSeconds / (seconds * logicalProcessors),
            (long)Math.Round(weightedMemory / seconds), peakMemory, minimumHeadroom)
        : null;
}
