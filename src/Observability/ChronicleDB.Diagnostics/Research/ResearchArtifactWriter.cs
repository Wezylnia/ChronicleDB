using System.Security.Cryptography;
using System.Text;

namespace ChronicleDB.Diagnostics.Research;

/// <summary>
/// Writes immutable, content-addressed research artifacts beside an experiment run.
/// This class is deliberately outside engine authority: a failed artifact write must
/// never change ChronicleDB semantics or durability decisions.
/// </summary>
public sealed class ResearchArtifactWriter
{
    public const string ManifestFileName = "manifest.json";
    public const string ManifestHashFileName = "manifest.sha256";

    public ResearchArtifactWriter(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public ResearchManifestArtifact WriteManifest(ExperimentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var canonicalJson = manifest.SerializeCanonical();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)))
            .ToLowerInvariant();

        WriteImmutable(ManifestFileName, canonicalJson);
        WriteImmutable(ManifestHashFileName, hash);

        return new ResearchManifestArtifact(
            Path.Combine(DirectoryPath, ManifestFileName),
            Path.Combine(DirectoryPath, ManifestHashFileName),
            hash);
    }

    private void WriteImmutable(string fileName, string content)
    {
        var destination = Path.Combine(DirectoryPath, fileName);

        if (File.Exists(destination))
        {
            EnsureSameContent(destination, content);
            return;
        }

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                EnsureSameContent(destination, content);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void EnsureSameContent(string destination, string expected)
    {
        var actual = File.ReadAllText(destination, Encoding.UTF8);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new IOException($"Research artifact already exists with different content: {destination}");
        }
    }
}

public sealed record ResearchManifestArtifact(
    string ManifestPath,
    string ManifestHashPath,
    string Sha256);
