using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

/// <summary>
/// 1つの論理的な確認操作に対応するおすすめ候補のまとまりです。
/// 元の候補は監査・詳細表示用に保持し、UI上の操作単位だけをまとめます。
/// </summary>
public sealed record LegacyMatchingRecommendationGroup(
    string ActionKey,
    AbacusRecommendationCandidate Representative,
    IReadOnlyList<AbacusRecommendationCandidate> Candidates);

public static class LegacyMatchingRecommendationGrouping
{
    public static IReadOnlyList<LegacyMatchingRecommendationGroup> Group(
        IEnumerable<AbacusRecommendationCandidate> candidates,
        Func<AbacusRecommendationCandidate, string> actionKeySelector)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(actionKeySelector);

        return candidates
            .GroupBy(actionKeySelector, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LegacyMatchingRecommendationGroup(
                group.Key,
                group.First(),
                group.ToArray()))
            .ToArray();
    }
}
