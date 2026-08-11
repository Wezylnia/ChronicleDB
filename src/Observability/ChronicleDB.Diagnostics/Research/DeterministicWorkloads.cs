namespace ChronicleDB.Diagnostics.Research;

public enum ResearchWorkloadFamily
{
    S0Control,
    S1OldThinBranch,
    S2OverlappingRoots,
    S3DeepInheritance,
    S4WideIndependentHistories,
    S5RecoveryHeavy,
    S6ErasureConflict,
    S7MixedAdversarialSoak,
}

public enum ResearchWorkloadOperationKind
{
    Read,
    Put,
    Delete,
    CreateBranch,
    CreateSnapshot,
    GarbageCollect,
    Compact,
    Crash,
    Recover,
}

/// <summary>
/// A logical workload input. It is intentionally not an engine command: runners map
/// these operations to the selected baseline or candidate implementation.
/// </summary>
public sealed record ResearchWorkloadOperation(
    int Step,
    ResearchWorkloadOperationKind Kind,
    int HistorySlot,
    int ParentHistorySlot,
    int KeyId,
    int ValueSize,
    bool RequestedHistory)
{
    public void Validate()
    {
        if (Step < 0 || HistorySlot < 0 || KeyId < 0 || ValueSize < 0)
        {
            throw new InvalidOperationException("Workload operation coordinates cannot be negative.");
        }

        if (ParentHistorySlot < -1)
        {
            throw new InvalidOperationException("ParentHistorySlot must be -1 or a non-negative slot.");
        }

        if (Kind == ResearchWorkloadOperationKind.CreateBranch && ParentHistorySlot < 0)
        {
            throw new InvalidOperationException("CreateBranch requires a parent history slot.");
        }
    }
}

/// <summary>
/// Version-one deterministic S0-S7 input generator. The local PRNG is deliberately
/// specified here instead of relying on System.Random's framework implementation.
/// </summary>
public static class DeterministicResearchWorkloadGenerator
{
    public const int GeneratorFormatVersion = 1;

    public static IReadOnlyList<ResearchWorkloadOperation> Generate(
        ResearchWorkloadFamily family,
        int seed,
        int operationCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(operationCount);

        var random = new DeterministicRandom(unchecked((uint)seed));
        var operations = new List<ResearchWorkloadOperation>(operationCount);
        for (var step = 0; step < operationCount; step++)
        {
            operations.Add(CreateOperation(family, step, random));
        }

        return operations;
    }

    private static ResearchWorkloadOperation CreateOperation(
        ResearchWorkloadFamily family,
        int step,
        DeterministicRandom random)
    {
        var key = random.Next(64);
        var valueSize = 32 + random.Next(1024);
        var operation = family switch
        {
            ResearchWorkloadFamily.S0Control => CreateControl(step, key, valueSize),
            ResearchWorkloadFamily.S1OldThinBranch => CreateOldThinBranch(step, key, valueSize),
            ResearchWorkloadFamily.S2OverlappingRoots => CreateOverlappingRoots(step, key, valueSize),
            ResearchWorkloadFamily.S3DeepInheritance => CreateDeepInheritance(step, key, valueSize),
            ResearchWorkloadFamily.S4WideIndependentHistories => CreateWideHistories(step, key, valueSize, random),
            ResearchWorkloadFamily.S5RecoveryHeavy => CreateRecoveryHeavy(step, key, valueSize, random),
            ResearchWorkloadFamily.S6ErasureConflict => CreateErasureConflict(step, key, valueSize),
            ResearchWorkloadFamily.S7MixedAdversarialSoak => CreateMixed(step, key, valueSize, random),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

        operation.Validate();
        return operation;
    }

    private static ResearchWorkloadOperation CreateControl(int step, int key, int valueSize)
        => step == 0
            ? Branch(step, 1, 0, key, valueSize)
            : Data(step, step % 2 == 0 ? ResearchWorkloadOperationKind.Put : ResearchWorkloadOperationKind.Read, 0, key, valueSize);

    private static ResearchWorkloadOperation CreateOldThinBranch(int step, int key, int valueSize)
        => step == 0
            ? Branch(step, 1, 0, key, valueSize)
            : Data(step, step % 5 == 0 ? ResearchWorkloadOperationKind.Delete : ResearchWorkloadOperationKind.Put, step % 3 == 0 ? 1 : 0, key, valueSize);

    private static ResearchWorkloadOperation CreateOverlappingRoots(int step, int key, int valueSize)
        => step < 4
            ? Branch(step, step + 1, 0, key, valueSize)
            : step % 7 == 0
                ? Data(step, ResearchWorkloadOperationKind.CreateSnapshot, 1 + step % 4, key, valueSize)
                : Data(step, ResearchWorkloadOperationKind.Put, 1 + step % 4, key, valueSize);

    private static ResearchWorkloadOperation CreateDeepInheritance(int step, int key, int valueSize)
    {
        var depth = Math.Min(16, 1 + step / 2);
        return step % 2 == 0 && step / 2 < 16
            ? Branch(step, depth, depth - 1, key, valueSize)
            : Data(step, ResearchWorkloadOperationKind.Read, depth, key, valueSize);
    }

    private static ResearchWorkloadOperation CreateWideHistories(int step, int key, int valueSize, DeterministicRandom random)
        => step < 16
            ? Branch(step, step + 1, 0, key, valueSize)
            : Data(step, ResearchWorkloadOperationKind.Put, 1 + random.Next(16), key, valueSize);

    private static ResearchWorkloadOperation CreateRecoveryHeavy(int step, int key, int valueSize, DeterministicRandom random)
    {
        if (step < 8)
        {
            return Branch(step, step + 1, 0, key, valueSize);
        }

        var history = 1 + random.Next(8);
        return (step % 9) switch
        {
            0 => new ResearchWorkloadOperation(step, ResearchWorkloadOperationKind.Crash, history, -1, key, 0, RequestedHistory: false),
            1 => new ResearchWorkloadOperation(step, ResearchWorkloadOperationKind.Recover, history, -1, key, 0, RequestedHistory: true),
            _ => Data(step, ResearchWorkloadOperationKind.Put, history, key, valueSize, requested: step % 3 == 0),
        };
    }

    private static ResearchWorkloadOperation CreateErasureConflict(int step, int key, int valueSize)
        => step switch
        {
            0 => Data(step, ResearchWorkloadOperationKind.Put, 0, key, valueSize),
            1 => Data(step, ResearchWorkloadOperationKind.CreateSnapshot, 0, key, valueSize),
            2 => Branch(step, 1, 0, key, valueSize),
            3 => Data(step, ResearchWorkloadOperationKind.Put, 1, key, valueSize),
            4 => Data(step, ResearchWorkloadOperationKind.Delete, 1, key, 0),
            5 => Branch(step, 2, 1, key, valueSize),
            6 => Data(step, ResearchWorkloadOperationKind.Put, 2, key, valueSize),
            7 => Data(step, ResearchWorkloadOperationKind.Delete, 2, key, 0),
            8 => Data(step, ResearchWorkloadOperationKind.CreateSnapshot, 1, key, valueSize),
            _ => Data(step, ResearchWorkloadOperationKind.Read, 2, key, valueSize),
        };

    private static ResearchWorkloadOperation CreateMixed(int step, int key, int valueSize, DeterministicRandom random)
    {
        if (step < 8)
        {
            return Branch(step, step + 1, 0, key, valueSize);
        }

        var history = random.Next(9);
        return (step % 13) switch
        {
            0 => Data(step, ResearchWorkloadOperationKind.CreateSnapshot, history, key, valueSize),
            1 => Data(step, ResearchWorkloadOperationKind.GarbageCollect, history, key, 0),
            2 => Data(step, ResearchWorkloadOperationKind.Compact, history, key, 0),
            3 => new ResearchWorkloadOperation(step, ResearchWorkloadOperationKind.Crash, history, -1, key, 0, false),
            _ => Data(step, step % 4 == 0 ? ResearchWorkloadOperationKind.Delete : ResearchWorkloadOperationKind.Put, history, key, valueSize),
        };
    }

    private static ResearchWorkloadOperation Branch(int step, int history, int parent, int key, int valueSize)
        => new(step, ResearchWorkloadOperationKind.CreateBranch, history, parent, key, valueSize, false);

    private static ResearchWorkloadOperation Data(
        int step,
        ResearchWorkloadOperationKind kind,
        int history,
        int key,
        int valueSize,
        bool requested = false)
        => new(step, kind, history, -1, key, valueSize, requested);

    private sealed class DeterministicRandom
    {
        private uint _state;

        public DeterministicRandom(uint seed)
        {
            _state = seed == 0 ? 0x9E3779B9u : seed;
        }

        public int Next(int exclusiveMax)
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return (int)(_state % (uint)exclusiveMax);
        }
    }
}
