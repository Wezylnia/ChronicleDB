using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronicleDB.Diagnostics.Research;

public static class ResearchTraceSerializer
{
    public const int CurrentFormatVersion = 1;

    public static string SerializeCanonical(IEnumerable<ResearchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var ordered = events.ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].LogicalEventId >= ordered[index].LogicalEventId)
            {
                throw new ArgumentException(
                    "Research trace events must have strictly increasing logical event IDs.",
                    nameof(events));
            }
        }

        return JsonSerializer.Serialize(
            new TraceDocument(CurrentFormatVersion, ordered),
            CanonicalJsonOptions);
    }

    public static string ComputeCanonicalSha256(IEnumerable<ResearchEvent> events)
    {
        var bytes = Encoding.UTF8.GetBytes(SerializeCanonical(events));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record TraceDocument(int TraceFormatVersion, IReadOnlyList<ResearchEvent> Events);

    private static JsonSerializerOptions CanonicalJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
