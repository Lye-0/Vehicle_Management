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
    DateTimeOffset? UpdatedAtUtc = null,
    string? WorkTargetKey = null,
    string? ExternalCustomerId = null);

public static class LegacyMatchingCategoryKinds
{
    public const string Customer = "customer";
    public const string Vehicle = "vehicle";
    public const string Document = "document";

    public static IReadOnlyList<string> All { get; } = [Customer, Vehicle, Document];

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
    int Completed,
    int Obsolete = 0)
{
    /// <summary>
    /// 通常のおすすめ巡回で確認対象にする件数です。
    /// 処理済み・無効候補は含めません。
    /// </summary>
    public int Active => Pending + Held;

    public string ProgressText => $"{Pending}/{Active}";
}

/// <summary>
/// 顧客単位の確認で使う、保存対象ではない純粋な判定結果です。
/// Pending/Hold はマッチング候補キューの集計値であり、顧客最終確定の可否とは分離して扱います。
/// </summary>
public sealed record LegacyCustomerReviewGate(
    int PendingCount,
    int HeldCount,
    bool RequiresCustomerPreview)
{
    public bool CanApprove => PendingCount == 0 && HeldCount == 0 && !RequiresCustomerPreview;
}

/// <summary>
/// 顧客巡回から除外する未処理の自動統合候補を表します。
/// 顧客単位UIでは、候補として表示される側を独立顧客としてもう一度巡回しません。
/// </summary>
public sealed record LegacyMatchingCustomerMergeRelation(
    string SubjectCustomerId,
    string TargetCustomerId,
    string Origin,
    string Decision);

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
                var active = pending + held;
                return new LegacyMatchingCategorySummary(
                    kind,
                    LegacyMatchingCategoryKinds.GetLabel(kind),
                    active,
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

    /// <summary>
    /// 候補が存在することと、顧客統合の意思が残っていることを区別します。
    /// 候補をすべて拒否した場合は、顧客情報の統合プレビューを要求しません。
    /// </summary>
    public static bool AreAllCandidatesRejected(
        IEnumerable<AbacusRecommendationCandidate> candidates,
        IReadOnlyDictionary<string, string> decisions)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(decisions);

        var candidateList = candidates.ToArray();
        return candidateList.Length > 0 &&
               candidateList.All(candidate =>
                   GetDecision(decisions, candidate) == AbacusRecommendationDecisionValues.Rejected);
    }

    /// <summary>
    /// 顧客情報プレビューが必要なのは、複数顧客を統合する場合で、
    /// かつ採用内容がまだ保存されていない場合だけです。
    /// 統合の反映済み・未反映はこの判定に含めません。
    /// </summary>
    public static bool RequiresCustomerPreview(
        bool hasMergeCandidates,
        bool hasCompleteMergeDraft) =>
        hasMergeCandidates && !hasCompleteMergeDraft;

    /// <summary>
    /// 顧客の最終確定ゲートを、マッチング候補キューから切り離して評価します。
    /// 候補の承認・却下・保留は別の作業状態であり、顧客の現在内容を確定する操作をブロックしません。
    /// </summary>
    public static LegacyCustomerReviewGate EvaluateCustomerApprovalGate(
        bool requiresCustomerPreview) =>
        new(0, 0, requiresCustomerPreview);

    /// <summary>
    /// インポート全体の最終確定が可能かを、画面表示とクリック時検証で共通利用します。
    /// 未確定トレイやごみ箱の件数は、確定時に除外できるためこの判定には含めません。
    /// </summary>
    public static bool CanFinalizeImport(
        int pendingMergeGroupCount,
        int pendingDocumentCount,
        int unapprovedCustomerCount) =>
        pendingMergeGroupCount == 0 &&
        pendingDocumentCount == 0 &&
        unapprovedCustomerCount == 0;

    /// <summary>
    /// マッチング候補キューの未処理・保留件数を集計します。
    /// 顧客最終確定ゲートとは別に、候補巡回の表示・進捗で利用します。
    /// </summary>
    public static LegacyCustomerReviewGate EvaluateCustomerReviewGate(
        IEnumerable<AbacusRecommendationCandidate> candidates,
        IReadOnlyDictionary<string, string> decisions,
        bool requiresCustomerPreview)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(decisions);

        var candidateList = candidates.ToArray();
        var pendingCount = candidateList.Count(candidate =>
            GetDecision(decisions, candidate) == AbacusRecommendationDecisionValues.Pending);
        var heldCount = candidateList.Count(candidate =>
            GetDecision(decisions, candidate) == AbacusRecommendationDecisionValues.Hold);
        return new LegacyCustomerReviewGate(pendingCount, heldCount, requiresCustomerPreview);
    }

    public static bool IsRelatedToCustomer(
        AbacusRecommendationCandidate candidate,
        IReadOnlySet<string> customerIds) =>
        candidate.TargetCustomerId is not null && customerIds.Contains(candidate.TargetCustomerId) ||
        candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer && customerIds.Contains(candidate.SubjectId) ||
        candidate.TargetKind == AbacusRecommendationEntityKinds.Customer && customerIds.Contains(candidate.TargetId);

    public static IReadOnlySet<string> GetPendingAutomaticCustomerIdsToHide(
        IEnumerable<LegacyMatchingCustomerMergeRelation> relations)
    {
        var hiddenCustomerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in relations)
        {
            if (!string.Equals(relation.Origin, "automatic", StringComparison.OrdinalIgnoreCase) ||
                relation.Decision is not AbacusRecommendationDecisionValues.Pending and
                    not AbacusRecommendationDecisionValues.Hold ||
                string.IsNullOrWhiteSpace(relation.TargetCustomerId))
            {
                continue;
            }

            hiddenCustomerIds.Add(relation.TargetCustomerId);
        }

        return hiddenCustomerIds;
    }

    private static string GetDecision(
        IReadOnlyDictionary<string, string> decisions,
        AbacusRecommendationCandidate candidate) =>
        decisions.TryGetValue(candidate.CandidateId, out var decision) &&
        AbacusRecommendationDecisionValues.IsSupported(decision)
            ? decision
            : AbacusRecommendationDecisionValues.Pending;
}
