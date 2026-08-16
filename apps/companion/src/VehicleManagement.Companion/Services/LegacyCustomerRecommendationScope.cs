using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

/// <summary>
/// 顧客統合おすすめに対するユーザー判断の安定した識別子です。
/// CandidateIdや推薦経路ではなく、作業対象グループと外部候補顧客で判断を識別します。
/// </summary>
public sealed record LegacyCustomerRecommendationScope(
    string WorkTargetKey,
    string ExternalCustomerId)
{
    public bool Matches(LegacyCustomerRecommendationScope other) =>
        string.Equals(WorkTargetKey, other.WorkTargetKey, StringComparison.Ordinal) &&
        string.Equals(ExternalCustomerId, other.ExternalCustomerId, StringComparison.Ordinal);

    public static bool TryCreate(
        AbacusRecommendationCandidate candidate,
        string workTargetKey,
        IReadOnlySet<string> workTargetCustomerIds,
        out LegacyCustomerRecommendationScope scope)
    {
        scope = null!;
        if (candidate.SubjectKind != AbacusRecommendationEntityKinds.Customer ||
            candidate.TargetKind != AbacusRecommendationEntityKinds.Customer ||
            string.IsNullOrWhiteSpace(workTargetKey) ||
            workTargetCustomerIds.Count == 0)
        {
            return false;
        }

        var subjectIsInWorkTarget = workTargetCustomerIds.Contains(candidate.SubjectId);
        var targetIsInWorkTarget = workTargetCustomerIds.Contains(candidate.TargetId);
        if (subjectIsInWorkTarget == targetIsInWorkTarget)
        {
            return false;
        }

        scope = new LegacyCustomerRecommendationScope(
            workTargetKey,
            subjectIsInWorkTarget ? candidate.TargetId : candidate.SubjectId);
        return true;
    }
}
