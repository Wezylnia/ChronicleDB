using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

public sealed record ResearchCrashInjection(
    int OperationStep,
    int HistorySlot,
    string FaultPoint)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(OperationStep);
        ArgumentOutOfRangeException.ThrowIfNegative(HistorySlot);
        ArgumentException.ThrowIfNullOrWhiteSpace(FaultPoint);
    }
}

public sealed record ResearchCrashPlan(
    int FormatVersion,
    int Seed,
    IReadOnlyList<ResearchCrashInjection> Injections)
{
    public void Validate()
    {
        if (FormatVersion <= 0)
        {
            throw new InvalidOperationException("Crash plan format version must be positive.");
        }

        ArgumentNullException.ThrowIfNull(Injections);
        var previousStep = -1;
        foreach (var injection in Injections)
        {
            ArgumentNullException.ThrowIfNull(injection);
            injection.Validate();
            if (injection.OperationStep <= previousStep)
            {
                throw new InvalidOperationException("Crash plan operation steps must be strictly increasing.");
            }

            previousStep = injection.OperationStep;
        }
    }
}

public static class ResearchCrashPlanFactory
{
    private static readonly string[] FaultPoints =
    [
        "BeforeWalAppend",
        "AfterWalAppend",
        "BeforeWalFlush",
        "AfterWalFlush",
        "BeforePhysicalPublication",
        "AfterPhysicalPublication",
        "BeforeAcknowledgement",
    ];

    public static ResearchCrashPlan Create(IEnumerable<ResearchWorkloadOperation> operations, int seed)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var ordered = operations.ToArray();
        var injections = ordered
            .Where(operation => operation.Kind == ResearchWorkloadOperationKind.Crash)
            .Select(operation => new ResearchCrashInjection(
                operation.Step,
                operation.HistorySlot,
                FaultPoints[SelectFaultPoint(operation.Step, seed)]))
            .ToArray();
        var plan = new ResearchCrashPlan(1, seed, injections);
        plan.Validate();
        return plan;
    }

    private static int SelectFaultPoint(int step, int seed)
    {
        var value = unchecked((uint)seed) ^ unchecked((uint)step * 0x9E3779B9u);
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        return (int)(value % (uint)FaultPoints.Length);
    }
}

public static class ResearchCrashPlanSerializer
{
    public static string SerializeCanonical(ResearchCrashPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        return JsonSerializer.Serialize(plan, CanonicalJsonOptions);
    }

    public static string ComputeCanonicalSha256(ResearchCrashPlan plan)
    {
        var bytes = Encoding.UTF8.GetBytes(SerializeCanonical(plan));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
