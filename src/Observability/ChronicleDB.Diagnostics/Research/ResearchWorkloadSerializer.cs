using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

public static class ResearchWorkloadSerializer
{
    public static string SerializeCanonical(IEnumerable<ResearchWorkloadOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var ordered = operations.ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            ordered[index].Validate();
            if (ordered[index].Step != index)
            {
                throw new ArgumentException("Workload steps must be contiguous and start at zero.", nameof(operations));
            }
        }

        return JsonSerializer.Serialize(
            new WorkloadDocument(DeterministicResearchWorkloadGenerator.GeneratorFormatVersion, ordered),
            CanonicalJsonOptions);
    }

    public static string ComputeCanonicalSha256(IEnumerable<ResearchWorkloadOperation> operations)
    {
        var bytes = Encoding.UTF8.GetBytes(SerializeCanonical(operations));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record WorkloadDocument(
        int GeneratorFormatVersion,
        IReadOnlyList<ResearchWorkloadOperation> Operations);

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
