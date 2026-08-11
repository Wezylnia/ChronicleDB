namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Checks trace protocol invariants before a research oracle consumes an event stream.
/// It is intentionally a tool-side validator and never participates in engine state.
/// </summary>
public static class ResearchTraceValidator
{
    public static void Validate(IEnumerable<ResearchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var ordered = events.ToArray();
        var seen = new HashSet<long>();
        var operationStates = new Dictionary<Guid, OperationState>();
        foreach (var researchEvent in ordered)
        {
            ArgumentNullException.ThrowIfNull(researchEvent);
            if (!seen.Add(researchEvent.LogicalEventId))
            {
                throw new InvalidOperationException("A research trace contains a duplicate logical event ID.");
            }

            if (seen.Count > 1 && researchEvent.LogicalEventId <= ordered[seen.Count - 2].LogicalEventId)
            {
                throw new InvalidOperationException("Research trace logical event IDs must be strictly increasing.");
            }

            foreach (var dependency in researchEvent.DependencyEventIds)
            {
                if (!seen.Contains(dependency))
                {
                    throw new InvalidOperationException(
                        $"Event {researchEvent.LogicalEventId} depends on an event that was not emitted earlier: {dependency}.");
                }
            }

            var state = operationStates.TryGetValue(researchEvent.OperationId, out var existing)
                ? existing
                : default;
            switch (researchEvent.EventKind)
            {
                case ResearchEventKind.OperationStarted when state.Started:
                    throw new InvalidOperationException("An operation cannot start twice.");
                case ResearchEventKind.OperationStarted:
                    state = state with { Started = true };
                    break;
                case ResearchEventKind.OperationCompleted when !state.Started:
                    throw new InvalidOperationException("An operation cannot complete before it starts.");
                case ResearchEventKind.OperationCompleted when state.Completed:
                    throw new InvalidOperationException("An operation cannot complete twice.");
                case ResearchEventKind.OperationCompleted:
                    state = state with { Completed = true };
                    break;
                case ResearchEventKind.DurabilityBarrier when !state.Started:
                    throw new InvalidOperationException("A durability barrier requires a started operation.");
            }

            operationStates[researchEvent.OperationId] = state;
        }
    }

    private readonly record struct OperationState(bool Started, bool Completed);
}
