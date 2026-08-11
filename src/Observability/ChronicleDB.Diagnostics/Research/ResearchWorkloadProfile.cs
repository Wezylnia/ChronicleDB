namespace ChronicleDB.Diagnostics.Research;

public sealed record ResearchWorkloadProfile(
    int OperationCount,
    int BranchCount,
    int MaximumBranchDepth,
    int MaximumFanout,
    int SnapshotCount,
    int CrashCount,
    int RequestedRecoveryCount,
    int MaximumValueSize);

public static class ResearchWorkloadProfiler
{
    public static ResearchWorkloadProfile Analyze(IEnumerable<ResearchWorkloadOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var ordered = operations.ToArray();
        var depths = new Dictionary<int, int> { [0] = 0 };
        var fanout = new Dictionary<int, int>();
        var maximumValueSize = 0;
        var snapshots = 0;
        var crashes = 0;
        var requestedRecovery = 0;

        for (var index = 0; index < ordered.Length; index++)
        {
            var operation = ordered[index];
            operation.Validate();
            if (operation.Step != index)
            {
                throw new ArgumentException("Workload steps must be contiguous and start at zero.", nameof(operations));
            }

            maximumValueSize = Math.Max(maximumValueSize, operation.ValueSize);
            if (operation.RequestedHistory && operation.Kind == ResearchWorkloadOperationKind.Recover)
            {
                requestedRecovery++;
            }

            switch (operation.Kind)
            {
                case ResearchWorkloadOperationKind.CreateBranch:
                    if (depths.ContainsKey(operation.HistorySlot)
                        || !depths.TryGetValue(operation.ParentHistorySlot, out var parentDepth))
                    {
                        throw new ArgumentException("A branch must have one unique, already-created parent.", nameof(operations));
                    }

                    depths.Add(operation.HistorySlot, parentDepth + 1);
                    fanout[operation.ParentHistorySlot] = fanout.GetValueOrDefault(operation.ParentHistorySlot) + 1;
                    break;
                case ResearchWorkloadOperationKind.CreateSnapshot:
                    EnsureHistoryExists(depths, operation.HistorySlot, operations);
                    snapshots++;
                    break;
                case ResearchWorkloadOperationKind.Crash:
                    EnsureHistoryExists(depths, operation.HistorySlot, operations);
                    crashes++;
                    break;
                default:
                    EnsureHistoryExists(depths, operation.HistorySlot, operations);
                    break;
            }
        }

        return new ResearchWorkloadProfile(
            ordered.Length,
            Math.Max(0, depths.Count - 1),
            depths.Values.DefaultIfEmpty(0).Max(),
            fanout.Values.DefaultIfEmpty(0).Max(),
            snapshots,
            crashes,
            requestedRecovery,
            maximumValueSize);
    }

    private static void EnsureHistoryExists(
        Dictionary<int, int> depths,
        int historySlot,
        IEnumerable<ResearchWorkloadOperation> operations)
    {
        if (!depths.ContainsKey(historySlot))
        {
            throw new ArgumentException("An operation references a history that has not been created.", nameof(operations));
        }
    }
}
