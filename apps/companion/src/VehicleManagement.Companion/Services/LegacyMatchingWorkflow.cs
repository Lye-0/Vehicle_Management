using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public static class LegacyGraphRecommendationLifecycle
{
    public const string Active = "active";
    public const string Obsolete = "obsolete";

    public static bool IsSupported(string? value) => value is Active or Obsolete;
}

public sealed record LegacyGraphRecommendationState(
    string Decision,
    string Lifecycle = LegacyGraphRecommendationLifecycle.Active,
    string? ResolutionReason = null,
    DateTimeOffset? UpdatedAtUtc = null);

public static class LegacyMatchingCategoryKinds
{
    public const string Customer = "customer";
    public const string Vehicle = "vehicle";
    public const string Document = "document";

    public static string GetLabel(string kind) => kind switch
    {
        Customer => "顧客統合",
        Vehicle => "車両",
        Document => "書類",
        _ => "その他",
    };

    public static string GetKind(AbacusRecommendationCandidate candidate) =>
        candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
        candidate.TargetKind == AbacusRecommendationEntityKinds.Customer
            ? Customer
            : candidate.SubjectKind == AbacusRecommendationEntityKinds.Vehicle
                ? Vehicle
                : Document;
}

public sealed record LegacyMatchingCategorySummary(
    string Kind,
    string Label,
    int Total,
    int Pending,
    int Held,
    int Completed)
{
    public string ProgressText => $"{Completed}/{Total}";
}

/// <summary>
/// 顧客単位UIの表示順・カテゴリ集計を、WPFの状態から切り離して決めます。
/// リンクや判定を変更しないため、保存再開とUI切り替えの両方から安全に利用できます。
/// </summary>
public static class LegacyMatchingWorkflow
{
    private static readonly string[] CategoryOrder =
    [
        LegacyMatchingCategoryKinds.Customer,
        LegacyMatchingCategoryKinds.Vehicle,
        LegacyMatchingCategoryKinds.Document,
    ];

    public static IReadOnlyList<LegacyMatchingCategorySummary> BuildCategorySummaries(
        IEnumerable<AbacusRecommendationCandidate> candidates,
        IReadOnlyDictionary<string, string> decisions) =>
        CategoryOrder
            .Select(kind =>
            {
                var categoryCandidates = candidates
                    .Where(candidate => LegacyMatchingCategoryKinds.GetKind(candidate) == kind)
                    .ToArray();
                var pending = categoryCandidates.Count(candidate =>
                    GetDecision(decisions, candidate) == AbacusRecommendationDecisionValues.Pending);
                var held = categoryCandidates.Count(candidate =>
                    GetDecision(decisions, candidate) == AbacusRecommendationDecisionValues.Hold);
                var completed = categoryCandidates.Count(candidate =>
                    GetDecision(decisions, candidate) is AbacusRecommendationDecisionValues.Approved or
                        AbacusRecommendationDecisionValues.Rejected);
                return new LegacyMatchingCategorySummary(
                    kind,
                    LegacyMatchingCategoryKinds.GetLabel(kind),
                    categoryCandidates.Length,
                    pending,
                    held,
                    completed);
            })
            .ToArray();

    public static IReadOnlyList<AbacusRecommendationCandidate> OrderCandidates(
        IEnumerable<AbacusRecommendationCandidate> candidates,
        string categoryKind,
        IReadOnlyDictionary<string, string> decisions,
        bool includeCompleted = true) =>
        candidates
            .Where(candidate => LegacyMatchingCategoryKinds.GetKind(candidate) == categoryKind)
            .Where(candidate => includeCompleted ||
                                GetDecision(decisions, candidate) is AbacusRecommendationDecisionValues.Pending or
                                    AbacusRecommendationDecisionValues.Hold)
            .OrderBy(candidate => GetDecision(decisions, candidate) == AbacusRecommendationDecisionValues.Pending ? 0 : 1)
            .ThenByDescending(candidate => candidate.IsEligible)
            .ThenByDescending(candidate => candidate.HasStrongEvidence)
            .ThenByDescending(candidate => candidate.MatchedFields.Count)
            .ThenBy(candidate => candidate.SubjectKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SubjectId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetId, StringComparer.Ordinal)
            .ToArray();

    public static bool IsNormalQueueComplete(
        IEnumerable<AbacusRecommendationCandidate> candidates,
        IReadOnlyDictionary<string, string> decisions) =>
        candidates.All(candidate =>
        {
            var decision = GetDecision(decisions, candidate);
            return decision is AbacusRecommendationDecisionValues.Approved or
                AbacusRecommendationDecisionValues.Rejected or
                AbacusRecommendationDecisionValues.Hold;
        });

    public static bool IsRelatedToCustomer(
        AbacusRecommendationCandidate candidate,
        IReadOnlySet<string> customerIds) =>
        candidate.TargetCustomerId is not null && customerIds.Contains(candidate.TargetCustomerId) ||
        candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer && customerIds.Contains(candidate.SubjectId) ||
        candidate.TargetKind == AbacusRecommendationEntityKinds.Customer && customerIds.Contains(candidate.TargetId);

    private static string GetDecision(
        IReadOnlyDictionary<string, string> decisions,
        AbacusRecommendationCandidate candidate) =>
        decisions.TryGetValue(candidate.CandidateId, out var decision) &&
        AbacusRecommendationDecisionValues.IsSupported(decision)
            ? decision
            : AbacusRecommendationDecisionValues.Pending;
}
