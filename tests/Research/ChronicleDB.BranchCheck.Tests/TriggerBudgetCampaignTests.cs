using ChronicleDB.BranchCheck;

namespace ChronicleDB.BranchCheck.Tests;

public sealed class TriggerBudgetCampaignTests
{
    [Fact]
    public void FiveRecipeSpaceHasAllOneHundredTwentyOrderings()
    {
        MatrixOneIdentityMutationRecipe[] recipes = Enum.GetValues<MatrixOneIdentityMutationRecipe>();
        IReadOnlyList<MatrixOneIdentityMutationRecipe[]> permutations =
            MatrixOneTriggerBudgetCampaign.GeneratePermutations(recipes);

        Assert.Equal(120, permutations.Count);
        Assert.Equal(
            120,
            permutations.Select(static ordering => string.Join(',', ordering)).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryRecipeAppearsAtEveryPositionEquallyOften()
    {
        MatrixOneIdentityMutationRecipe[] recipes = Enum.GetValues<MatrixOneIdentityMutationRecipe>();
        IReadOnlyList<MatrixOneIdentityMutationRecipe[]> permutations =
            MatrixOneTriggerBudgetCampaign.GeneratePermutations(recipes);

        foreach (MatrixOneIdentityMutationRecipe recipe in recipes)
        {
            for (int position = 0; position < recipes.Length; position++)
            {
                Assert.Equal(24, permutations.Count(ordering => ordering[position] == recipe));
            }
        }
    }
}
