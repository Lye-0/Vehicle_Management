using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

/// <summary>
/// 未確定トレイへ移動する直前の、書類の一時リンク状態です。
/// 元データではなく、ユーザー操作直前の画面上の状態をUndoするために保存します。
/// </summary>
public sealed record LegacyGraphDetachedDocumentState(
    string DocumentKey,
    string? ManualVehicleId,
    string? ManualCustomerGroupKey,
    string? LinkMethod,
    string? LinkReason,
    bool IsUnconnected,
    bool IsTray,
    bool IsExcluded);

/// <summary>
/// 未確定トレイへの移動をUndoするための構造化された直前状態です。
/// </summary>
public sealed record LegacyGraphDetachedUndoState(
    string Kind,
    string SubjectId,
    string? ManualVehicleCustomerId,
    bool IsTray,
    IReadOnlyList<LegacyGraphDetachedDocumentState> Documents);

/// <summary>
/// 手動リンクによって一時的に無効になったおすすめだけを再評価時に復帰させます。
/// ユーザーが明示的に却下したおすすめは復帰対象にしません。
/// </summary>
public static class LegacyGraphRecommendationLifecycleReconciler
{
    public const string TemporaryManualLinkObsoleteReason =
        "既存の手動紐付けにより、この候補は現在の判定対象から外れました。";

    public const string ExplicitRejectedObsoleteReason =
        "ユーザーが明示的に却下したため、この候補は判定対象外です。";

    public const string CustomerApprovalResolutionReason =
        "顧客確定に伴う一括処理で判定済みです。";

    public static bool IsTemporaryManualLinkObsolete(string? reason) =>
        string.Equals(reason, TemporaryManualLinkObsoleteReason, StringComparison.Ordinal);

    public static LegacyGraphRecommendationState MarkObsoleteAfterRebuild(
        LegacyGraphRecommendationState state,
        string fallbackReason,
        DateTimeOffset updatedAtUtc)
    {
        if (state.Lifecycle == LegacyGraphRecommendationLifecycle.Obsolete &&
            IsTemporaryManualLinkObsolete(state.ResolutionReason))
        {
            return state;
        }

        var reason = state.Lifecycle == LegacyGraphRecommendationLifecycle.Active &&
                     state.Decision == AbacusRecommendationDecisionValues.Rejected &&
                     string.IsNullOrWhiteSpace(state.ResolutionReason)
            ? ExplicitRejectedObsoleteReason
            : fallbackReason;
        return state with
        {
            Lifecycle = LegacyGraphRecommendationLifecycle.Obsolete,
            ResolutionReason = reason,
            UpdatedAtUtc = updatedAtUtc,
        };
    }

    public static LegacyGraphRecommendationState? ReconcileCurrentCandidate(
        LegacyGraphRecommendationState? state,
        DateTimeOffset updatedAtUtc)
    {
        if (state is null ||
            state.Lifecycle != LegacyGraphRecommendationLifecycle.Obsolete ||
            !IsTemporaryManualLinkObsolete(state.ResolutionReason))
        {
            return state;
        }

        return state with
        {
            Decision = AbacusRecommendationDecisionValues.Pending,
            Lifecycle = LegacyGraphRecommendationLifecycle.Active,
            ResolutionReason = null,
            UpdatedAtUtc = updatedAtUtc,
        };
    }
}
