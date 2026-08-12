using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronicleDB.Diagnostics.Research;

internal static class ResearchGateRunner
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        return args[0].ToUpperInvariant() switch
        {
            "DECISION" => WriteDecision(args[1..]),
            "REPORT" => WriteReport(args[1..]),
            _ => Unknown(args[0]),
        };
    }

    private static int WriteDecision(string[] args)
    {
        if (args.Length < 6
            || !TryParseDisposition(args[1], out var disposition))
        {
            Console.Error.WriteLine(
                "Usage: gate decision <candidate-id> <supported|weakened|falsified|inconclusive|blocked-by-novelty|blocked-by-semantics> " +
                "<narrow-claim-version> <rationale-file> <output-file> <evidence-file>...");
            return 2;
        }

        try
        {
            var candidateId = args[0];
            var narrowClaimVersion = args[2];
            var rationalePath = Path.GetFullPath(args[3]);
            var outputPath = Path.GetFullPath(args[4]);
            var evidencePaths = args[5..].Select(Path.GetFullPath).ToArray();
            if (!File.Exists(rationalePath))
            {
                throw new FileNotFoundException("Rationale file not found.", rationalePath);
            }
            if (evidencePaths.Length == 0 || evidencePaths.Any(path => !File.Exists(path)))
            {
                throw new InvalidOperationException("Every evidence file must exist and at least one evidence file is required.");
            }

            var decision = new ResearchCandidateGateDecision
            {
                FormatVersion = ResearchCandidateGateDecision.CurrentFormatVersion,
                CandidateId = candidateId,
                Disposition = disposition,
                NarrowClaimVersion = narrowClaimVersion,
                Rationale = File.ReadAllText(rationalePath).Trim(),
                UtcRecordedAt = DateTimeOffset.UtcNow,
                EvidenceSha256 = evidencePaths.Select(ComputeSha256).ToArray(),
            };
            var canonical = decision.SerializeCanonical();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
            WriteNewFile(outputPath, canonical + Environment.NewLine);
            WriteNewFile(outputPath + ".sha256", decision.ComputeCanonicalSha256() + Environment.NewLine);
            Console.WriteLine(
                $"GATE DECISION candidate={candidateId} disposition={disposition} evidence={evidencePaths.Length} " +
                $"sha256={decision.ComputeCanonicalSha256()} output={outputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"GATE DECISION FAIL: {exception.Message}");
            return 1;
        }
    }

    private static int WriteReport(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: gate report <release> <output-directory> <decision-file>...");
            return 2;
        }

        try
        {
            var release = args[0];
            var outputDirectory = Path.GetFullPath(args[1]);
            var decisionFiles = args[2..].Select(Path.GetFullPath).ToArray();
            var decisions = decisionFiles.Select(ReadDecision).ToArray();
            var report = new ResearchGateReport
            {
                FormatVersion = ResearchGateReport.CurrentFormatVersion,
                Release = release,
                UtcGeneratedAt = DateTimeOffset.UtcNow,
                Decisions = decisions,
            };
            var canonical = report.SerializeCanonical();
            Directory.CreateDirectory(outputDirectory);
            var jsonPath = Path.Combine(outputDirectory, "research-gate-report.json");
            var markdownPath = Path.Combine(outputDirectory, "research-gate-report.md");
            WriteNewFile(jsonPath, canonical + Environment.NewLine);
            WriteNewFile(jsonPath + ".sha256", report.ComputeCanonicalSha256() + Environment.NewLine);
            WriteNewFile(markdownPath, BuildMarkdown(report));
            Console.WriteLine(
                $"GATE REPORT release={release} candidates={decisions.Length} sha256={report.ComputeCanonicalSha256()} " +
                $"output={outputDirectory}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"GATE REPORT FAIL: {exception.Message}");
            return 1;
        }
    }

    private static ResearchCandidateGateDecision ReadDecision(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Candidate decision file not found.", path);
        }
        var decision = JsonSerializer.Deserialize<ResearchCandidateGateDecision>(File.ReadAllText(path), ReadJsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize candidate decision '{path}'.");
        decision.Validate();
        return decision;
    }

    private static string BuildMarkdown(ResearchGateReport report)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(report.Release).AppendLine(" Research Gate Report");
        builder.AppendLine();
        builder.Append("Generated: ").Append(report.UtcGeneratedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        builder.AppendLine();
        builder.AppendLine("| Candidate | Disposition | Narrow claim | Evidence artifacts |");
        builder.AppendLine("| --- | --- | --- | ---: |");
        foreach (var decision in report.Decisions.OrderBy(item => item.CandidateId, StringComparer.Ordinal))
        {
            builder.Append("| ").Append(Escape(decision.CandidateId))
                .Append(" | ").Append(decision.Disposition)
                .Append(" | ").Append(Escape(decision.NarrowClaimVersion))
                .Append(" | ").Append(decision.EvidenceSha256.Count).AppendLine(" |");
        }
        builder.AppendLine();
        builder.AppendLine("## Recorded rationales");
        builder.AppendLine();
        foreach (var decision in report.Decisions.OrderBy(item => item.CandidateId, StringComparer.Ordinal))
        {
            builder.Append("### ").AppendLine(decision.CandidateId);
            builder.AppendLine();
            builder.AppendLine(decision.Rationale.Trim());
            builder.AppendLine();
            builder.AppendLine("Evidence SHA-256:");
            builder.AppendLine();
            foreach (var hash in decision.EvidenceSha256.Order(StringComparer.Ordinal))
            {
                builder.Append("- `").Append(hash).AppendLine("`");
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string Escape(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteNewFile(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static bool TryParseDisposition(string value, out ResearchCandidateDisposition disposition)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var candidate in Enum.GetValues<ResearchCandidateDisposition>())
        {
            if (candidate.ToString().Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                disposition = candidate;
                return true;
            }
        }
        disposition = default;
        return false;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown gate command '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Gate usage:");
        Console.Error.WriteLine("  gate decision <candidate-id> <disposition> <narrow-claim-version> <rationale-file> <output-file> <evidence-file>...");
        Console.Error.WriteLine("  gate report <release> <output-directory> <decision-file>...");
    }
}
