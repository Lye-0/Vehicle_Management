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

public static class LegacyGraphVehicleUndoState
{
    /// <summary>
    /// UndoStateに保存された顧客IDを、手動車両リンクとして復元できるか判定します。
    /// 元から顧客に所属する車両の実効顧客は手動リンクではないため、
    /// 旧チェックポイントに混入した同じ顧客IDも復元対象から除外します。
    /// </summary>
    public static string? ResolveManualCustomerId(
        string? storedManualCustomerId,
        bool hasOriginalCustomer,
        string? originalCustomerId)
    {
        if (string.IsNullOrWhiteSpace(storedManualCustomerId))
        {
            return null;
        }

        return hasOriginalCustomer &&
               string.Equals(storedManualCustomerId, originalCustomerId, StringComparison.OrdinalIgnoreCase)
            ? null
            : storedManualCustomerId;
    }
}

public static class LegacyGraphVehicleDetachState
{
    /// <summary>
    /// 車両解除時に、手動でその車両へ接続された書類のリンクを削除する対象か判定します。
    /// 元CSVの車両所属はこの判定では扱わず、画面上の手動リンクだけを対象にします。
    /// </summary>
    public static bool IsManualDocumentLinkedToVehicle(
        string? linkedVehicleId,
        string vehicleId) =>
        !string.IsNullOrWhiteSpace(linkedVehicleId) &&
        string.Equals(linkedVehicleId, vehicleId, StringComparison.Ordinal);
}

public static class LegacyGraphTemporaryMergeGroupState
{
    /// <summary>
    /// Graph UIの仮統合グループは、Recommendation Decisionではなく、
    /// 現在の所属顧客とグループ状態だけで判定します。
    /// </summary>
    public static bool IsPending(
        int activeMemberCount,
        bool isLogicalGroup,
        bool isApplied)
    {
        return HasActiveMembership(activeMemberCount) && !isLogicalGroup && !isApplied;
    }

    public static bool HasActiveMembership(int activeMemberCount) => activeMemberCount > 1;
}

public static class LegacyGraphMutationState
{
    public static bool CanMutate(
        bool bulkMergeBusy,
        bool finalPackageBusy,
        bool resumeInProgress) =>
        !bulkMergeBusy && !finalPackageBusy && !resumeInProgress;

    public static bool TryAddManualCustomerCandidate(
        IDictionary<string, HashSet<string>> candidateTargets,
        string sourceCustomerId,
        string targetCustomerId,
        bool bulkMergeBusy,
        bool finalPackageBusy,
        bool resumeInProgress)
    {
        ArgumentNullException.ThrowIfNull(candidateTargets);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCustomerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetCustomerId);

        if (!CanMutate(bulkMergeBusy, finalPackageBusy, resumeInProgress))
        {
            return false;
        }

        if (!candidateTargets.TryGetValue(sourceCustomerId, out var targetIds))
        {
            targetIds = new HashSet<string>(StringComparer.Ordinal);
            candidateTargets[sourceCustomerId] = targetIds;
        }

        return targetIds.Add(targetCustomerId);
    }
}

public static class LegacyGraphCheckpointSaveState
{
    public static bool CanStart(
        bool resumeInProgress,
        bool bulkMergeBusy,
        bool finalPackageBusy) =>
        !resumeInProgress && !bulkMergeBusy && !finalPackageBusy;

    public static bool ShouldRescheduleAfterResumeFailure(
        bool resumeFailed,
        bool hadPendingSave) =>
        resumeFailed && hadPendingSave;
}

public static class LegacyGraphFinalPackageState
{
    public static bool CanComplete(
        bool importConfirmedAtSnapshot,
        bool importConfirmedNow) =>
        importConfirmedAtSnapshot && importConfirmedNow;
}

public static class LegacyGraphCustomerReviewStateTransition
{
    public static string MarkApproved() => LegacyGraphCustomerReviewStateValues.Approved;

    public static string MarkNeedsReview(string? currentState)
    {
        if (!string.IsNullOrWhiteSpace(currentState) &&
            !LegacyGraphCustomerReviewStateValues.IsSupported(currentState))
        {
            throw new ArgumentException("顧客確認状態が不正です。", nameof(currentState));
        }

        return LegacyGraphCustomerReviewStateValues.NeedsReview;
    }

    /// <summary>
    /// 確定済み論理グループが構成顧客1件の単独顧客へ戻ったとき、
    /// 旧グループキーの確認状態を残存顧客の現在キーへ移します。
    /// </summary>
    public static string MoveGroupToStandaloneCustomer(
        IDictionary<string, string> reviewStates,
        IDictionary<string, bool> approvalStates,
        string groupKey,
        string remainingCustomerId,
        bool wasApproved)
    {
        ArgumentNullException.ThrowIfNull(reviewStates);
        ArgumentNullException.ThrowIfNull(approvalStates);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(remainingCustomerId);

        var standaloneKey = $"customer:{remainingCustomerId}";
        reviewStates.Remove(groupKey);
        approvalStates.Remove(groupKey);
        reviewStates[standaloneKey] = wasApproved
            ? LegacyGraphCustomerReviewStateValues.NeedsReview
            : LegacyGraphCustomerReviewStateValues.Unreviewed;
        approvalStates[standaloneKey] = false;
        return standaloneKey;
    }
}

public static class LegacyGraphRecommendationAvailability
{
    public static bool AreEndpointsActive(
        AbacusRecommendationCandidate candidate,
        IReadOnlySet<string> trashCustomerIds,
        IReadOnlySet<string> trashVehicleIds,
        IReadOnlySet<string> trashDocumentKeys)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(trashCustomerIds);
        ArgumentNullException.ThrowIfNull(trashVehicleIds);
        ArgumentNullException.ThrowIfNull(trashDocumentKeys);

        return IsActive(
                   candidate.SubjectKind,
                   candidate.SubjectId,
                   trashCustomerIds,
                   trashVehicleIds,
                   trashDocumentKeys) &&
               IsActive(
                   candidate.TargetKind,
                   candidate.TargetId,
                   trashCustomerIds,
                   trashVehicleIds,
                   trashDocumentKeys);
    }

    private static bool IsActive(
        string entityKind,
        string entityId,
        IReadOnlySet<string> trashCustomerIds,
        IReadOnlySet<string> trashVehicleIds,
        IReadOnlySet<string> trashDocumentKeys) =>
        entityKind switch
        {
            AbacusRecommendationEntityKinds.Customer => !trashCustomerIds.Contains(entityId),
            AbacusRecommendationEntityKinds.Vehicle => !trashVehicleIds.Contains(entityId),
            AbacusRecommendationEntityKinds.Document => !trashDocumentKeys.Contains(entityId),
            _ => true,
        };
}

public static class LegacyGraphDocumentOwnership
{
    public static string? ResolveCurrentVehicleId(
        string? manualVehicleId,
        string? linkedVehicleId,
        string? originalVehicleId,
        bool hasManualCustomerOnlyLink = false)
    {
        if (!string.IsNullOrWhiteSpace(manualVehicleId))
        {
            return manualVehicleId;
        }

        return hasManualCustomerOnlyLink
            ? null
            : new[]
            {
                linkedVehicleId,
                originalVehicleId,
            }.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
    }

    /// <summary>
    /// 書類の現在顧客を、手動車両・手動顧客直結・自動リンク・元データの順で解決します。
    /// </summary>
    public static string? ResolveCurrentCustomerId(
        string? manualVehicleCustomerId,
        string? manualCustomerGroupCustomerId,
        string? linkedVehicleCustomerId,
        string? originalCustomerId) =>
        new[]
        {
            manualVehicleCustomerId,
            manualCustomerGroupCustomerId,
            linkedVehicleCustomerId,
            originalCustomerId,
        }.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
}

public static class LegacyGraphDetachedUndoStateMigration
{
    /// <summary>
    /// 顧客統合でグループIDが変わったとき、変更履歴に保存された
    /// 「操作直前の顧客直結先」も同じ新しいグループへ移します。
    /// </summary>
    public static LegacyGraphDetachedUndoState MigrateCustomerGroupReferences(
        LegacyGraphDetachedUndoState state,
        IReadOnlySet<string> oldGroupKeys,
        string newGroupKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(oldGroupKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(newGroupKey);

        var changed = false;
        var documents = state.Documents
            .Select(document =>
            {
                if (document.ManualCustomerGroupKey is not { } oldGroupKey ||
                    !oldGroupKeys.Contains(oldGroupKey) ||
                    string.Equals(oldGroupKey, newGroupKey, StringComparison.Ordinal))
                {
                    return document;
                }

                changed = true;
                return document with { ManualCustomerGroupKey = newGroupKey };
            })
            .ToArray();

        return changed
            ? state with { Documents = documents }
            : state;
    }
}

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

        // 一時Obsolete化の前にユーザーが保留した候補は、再成立後も保留を維持します。
        // 明示却下は一時理由が混在しても自動復帰させません。
        if (state.Decision == AbacusRecommendationDecisionValues.Rejected)
        {
            return state;
        }

        return state with
        {
            Decision = state.Decision == AbacusRecommendationDecisionValues.Hold
                ? AbacusRecommendationDecisionValues.Hold
                : AbacusRecommendationDecisionValues.Pending,
            Lifecycle = LegacyGraphRecommendationLifecycle.Active,
            ResolutionReason = null,
            UpdatedAtUtc = updatedAtUtc,
        };
    }
}
