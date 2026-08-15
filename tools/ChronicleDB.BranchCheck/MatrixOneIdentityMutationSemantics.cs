using System.Security.Cryptography;
using System.Text;

namespace ChronicleDB.BranchCheck;

/// <summary>
/// Predeclared semantic classification for the MatrixOne historical-identity campaign.
/// The classifier is based on whether an operation can change or exercise the source
/// object's identity/generation while preserving the historical branch capability;
/// it does not inspect observed failures or issue identifiers.
/// </summary>
public static class MatrixOneIdentityMutationSemantics
{
    public static bool IsIdentityStateRelevant(MatrixOneIdentityMutationRecipe recipe)
        => recipe is MatrixOneIdentityMutationRecipe.RenameSourceRoundTrip
            or MatrixOneIdentityMutationRecipe.RecreateSourceSameName
            or MatrixOneIdentityMutationRecipe.RecreateSourceSameNameSchemaVariant;

    public static string Fingerprint()
    {
        string canonical = string.Join(
            '\n',
            Enum.GetValues<MatrixOneIdentityMutationRecipe>().Select(recipe =>
                $"{recipe}|identity-relevant={IsIdentityStateRelevant(recipe).ToString().ToLowerInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
