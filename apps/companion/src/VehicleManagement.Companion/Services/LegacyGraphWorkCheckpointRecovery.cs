namespace VehicleManagement.Companion.Services;

public sealed record LegacyGraphWorkCheckpointMergeRecoveryResult(
    LegacyGraphWorkCheckpoint Checkpoint,
    bool Changed);

/// <summary>
/// 旧バージョンのグラフ作業チェックポイントに残る、顧客統合グループの
/// 二重所属と、消滅したグループを参照する状態を復元前に整理します。
///
/// 統合確定後の論理グループを優先し、仮グループに残った同じ顧客を除外します。
/// グループが単独顧客へ解消された場合は、その顧客スコープへ参照を移します。
/// それ以外の候補・承認・トレイ状態はこのクラスでは作成・削除しません。
/// </summary>
public static class LegacyGraphWorkCheckpointRecovery
{
    public static LegacyGraphWorkCheckpointMergeRecoveryResult NormalizeMergeMembership(
        LegacyGraphWorkCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var sourceGroups = checkpoint.CustomerMergeGroups ?? [];
        var sourceGroupIds = sourceGroups
            .Select(group => group.GroupId)
            .ToHashSet(StringComparer.Ordinal);
        var sourceLogicalMap = checkpoint.LogicalCustomerMergeGroupByCustomerId ??
                               new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceAppliedKeys = checkpoint.AppliedCustomerMergeKeys ?? [];
        var formalGroupIds = sourceGroups
            .Where(group => string.Equals(group.Origin, "logical", StringComparison.Ordinal) ||
                            sourceAppliedKeys.Contains(group.GroupId, StringComparer.Ordinal) ||
                            sourceLogicalMap.Values.Contains(group.GroupId, StringComparer.Ordinal))
            .Select(group => group.GroupId)
            .ToHashSet(StringComparer.Ordinal);

        var formalOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in sourceLogicalMap)
        {
            if (formalGroupIds.Contains(pair.Value) && !formalOwners.ContainsKey(pair.Key))
            {
                formalOwners[pair.Key] = pair.Value;
            }
        }

        foreach (var group in sourceGroups.Where(group => formalGroupIds.Contains(group.GroupId)))
        {
            foreach (var customerId in group.CustomerIds ?? [])
            {
                if (!formalOwners.ContainsKey(customerId))
                {
                    formalOwners[customerId] = group.GroupId;
                }
            }
        }

        var formalGroupMembers = sourceGroups
            .Where(group => formalGroupIds.Contains(group.GroupId))
            .ToDictionary(
                group => group.GroupId,
                group => group.CustomerIds
                    .Where(customerId => formalOwners.TryGetValue(customerId, out var owner) &&
                                         string.Equals(owner, group.GroupId, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var survivingFormalGroupIds = formalGroupMembers
            .Where(pair => pair.Value.Length > 1)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        var survivingFormalCustomerIds = formalGroupMembers
            .Where(pair => survivingFormalGroupIds.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .ToHashSet(StringComparer.Ordinal);

        var candidateGroupIds = sourceGroups
            .Where(group => !formalGroupIds.Contains(group.GroupId))
            .Select(group => group.GroupId)
            .ToHashSet(StringComparer.Ordinal);
        var preferredCandidateOwners = (checkpoint.CustomerMergeGroupByCustomerId ?? [])
            .Where(pair => !survivingFormalCustomerIds.Contains(pair.Key) &&
                           candidateGroupIds.Contains(pair.Value))
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
        var candidateOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalizedGroups = new List<LegacyGraphCheckpointMergeGroup>();
        var normalizedMembersBySourceGroupId = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var group in sourceGroups)
        {
            var isFormal = survivingFormalGroupIds.Contains(group.GroupId);
            var candidateIds = isFormal
                ? formalGroupMembers[group.GroupId]
                : group.CustomerIds
                    .Where(customerId => !survivingFormalCustomerIds.Contains(customerId))
                    .Where(customerId =>
                    {
                        if (preferredCandidateOwners.TryGetValue(customerId, out var preferredOwner))
                        {
                            if (!string.Equals(preferredOwner, group.GroupId, StringComparison.Ordinal))
                            {
                                return false;
                            }
                        }
                        else if (candidateOwners.TryGetValue(customerId, out var existingOwner) &&
                                 !string.Equals(existingOwner, group.GroupId, StringComparison.Ordinal))
                        {
                            return false;
                        }

                        candidateOwners[customerId] = group.GroupId;
                        return true;
                    })
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            // 構成顧客が1件になったグループは、通常の単独顧客へ戻します。
            if (candidateIds.Length > 1)
            {
                normalizedGroups.Add(new LegacyGraphCheckpointMergeGroup(
                    group.GroupId,
                    group.Origin,
                    candidateIds));
            }

            normalizedMembersBySourceGroupId[group.GroupId] = candidateIds;
        }

        var normalizedGroupIds = normalizedGroups
            .Select(group => group.GroupId)
            .ToHashSet(StringComparer.Ordinal);
        var knownCustomerIds = sourceGroups
            .SelectMany(group => group.CustomerIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        void RememberCustomerScope(string? groupKey)
        {
            const string customerPrefix = "customer:";
            if (groupKey is not null &&
                groupKey.StartsWith(customerPrefix, StringComparison.Ordinal) &&
                groupKey.Length > customerPrefix.Length)
            {
                knownCustomerIds.Add(groupKey[customerPrefix.Length..]);
            }
        }

        foreach (var groupKey in (checkpoint.CustomerApprovalStates ?? []).Keys)
        {
            RememberCustomerScope(groupKey);
        }

        foreach (var groupKey in (checkpoint.CustomerReviewStates ?? []).Keys)
        {
            RememberCustomerScope(groupKey);
        }

        foreach (var groupKey in (checkpoint.ManualDocumentCustomerGroupLinks ?? []).Values)
        {
            RememberCustomerScope(groupKey);
        }

        foreach (var groupKey in (checkpoint.RecommendationStates ?? []).Select(state => state.WorkTargetKey)
                     .Concat((checkpoint.MatchingChanges ?? []).Select(change => change.WorkTargetKey))
                     .Concat((checkpoint.MatchingChanges ?? []).SelectMany(change => change.UndoState?.Documents
                         .Select(document => document.ManualCustomerGroupKey) ?? [])))
        {
            RememberCustomerScope(groupKey);
        }

        foreach (var key in (checkpoint.CustomerNameOverrides ?? []).Keys)
        {
            RememberCustomerScope(key.StartsWith("merge:", StringComparison.Ordinal)
                ? key["merge:".Length..]
                : null);
        }

        var groupReferenceReplacements = sourceGroupIds
            .ToDictionary(
                groupId => groupId,
                groupId => normalizedGroupIds.Contains(groupId)
                    ? groupId
                    : normalizedMembersBySourceGroupId.TryGetValue(groupId, out var members) && members.Length == 1
                        ? $"customer:{members[0]}"
                        : null,
                StringComparer.Ordinal);
        var normalizedLogicalMap = normalizedGroups
            .Where(group => formalGroupIds.Contains(group.GroupId))
            .SelectMany(group => group.CustomerIds.Select(customerId =>
                new KeyValuePair<string, string>(customerId, group.GroupId)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var normalizedCandidateMap = normalizedGroups
            .Where(group => !formalGroupIds.Contains(group.GroupId))
            .SelectMany(group => group.CustomerIds.Select(customerId =>
                new KeyValuePair<string, string>(customerId, group.GroupId)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var normalizedDrafts = (checkpoint.CustomerMergeDrafts ?? [])
            .Where(pair => normalizedGroupIds.Contains(pair.Key) &&
                          normalizedGroups.First(group =>
                              string.Equals(group.GroupId, pair.Key, StringComparison.Ordinal)).CustomerIds
                              .ToHashSet(StringComparer.Ordinal)
                              .SetEquals(pair.Value.CandidateCustomerIds))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var normalizedAppliedKeys = sourceAppliedKeys
            .Where(normalizedGroupIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var normalizedExpandedGroups = (checkpoint.CustomerGroupExpanded ?? [])
            .Where(pair => normalizedGroupIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var normalizedVirtualKeys = (checkpoint.VirtualCustomerMergeKeys ?? [])
            .Where(pair => normalizedGroupIds.Contains(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var normalizedUnconnectedDocumentKeys = (checkpoint.UnconnectedDocumentKeys ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedManualDocumentCustomerGroupLinks = NormalizeManualDocumentCustomerGroupLinks(
            checkpoint.ManualDocumentCustomerGroupLinks,
            groupReferenceReplacements,
            normalizedGroupIds,
            knownCustomerIds,
            normalizedUnconnectedDocumentKeys);
        var normalizedRecommendationStates = (checkpoint.RecommendationStates ?? [])
            .Select(state => NormalizeRecommendationState(
                state,
                groupReferenceReplacements,
                normalizedGroupIds,
                knownCustomerIds))
            .ToArray();
        var normalizedMatchingChanges = (checkpoint.MatchingChanges ?? [])
            .Select(change => NormalizeMatchingChange(
                change,
                groupReferenceReplacements,
                normalizedGroupIds,
                knownCustomerIds))
            .ToArray();
        var normalizedNameOverrides = NormalizeNameOverrides(
            checkpoint.CustomerNameOverrides,
            groupReferenceReplacements,
            normalizedGroupIds,
            knownCustomerIds);
        var normalizedApprovalStates = NormalizeApprovalStates(
            checkpoint.CustomerApprovalStates,
            groupReferenceReplacements,
            normalizedGroupIds,
            knownCustomerIds);
        var normalizedReviewStates = NormalizeReviewStates(
            checkpoint.CustomerReviewStates,
            groupReferenceReplacements,
            normalizedGroupIds,
            knownCustomerIds);

        var normalizedCheckpoint = checkpoint with
        {
            CustomerMergeGroups = normalizedGroups.ToArray(),
            CustomerMergeGroupByCustomerId = normalizedCandidateMap,
            CustomerMergeDrafts = normalizedDrafts,
            AppliedCustomerMergeKeys = normalizedAppliedKeys,
            VirtualCustomerMergeKeys = normalizedVirtualKeys,
            CustomerGroupExpanded = normalizedExpandedGroups,
            LogicalCustomerMergeGroupByCustomerId = normalizedLogicalMap,
            CustomerApprovalStates = normalizedApprovalStates,
            CustomerReviewStates = normalizedReviewStates,
            ManualDocumentCustomerGroupLinks = normalizedManualDocumentCustomerGroupLinks,
            UnconnectedDocumentKeys = normalizedUnconnectedDocumentKeys.ToArray(),
            RecommendationStates = normalizedRecommendationStates,
            MatchingChanges = normalizedMatchingChanges,
            CustomerNameOverrides = normalizedNameOverrides,
        };

        var changed = !GroupsEqual(checkpoint.CustomerMergeGroups, normalizedCheckpoint.CustomerMergeGroups) ||
                      !DictionaryEqual(checkpoint.CustomerMergeGroupByCustomerId, normalizedCheckpoint.CustomerMergeGroupByCustomerId) ||
                      !DictionaryEqual(checkpoint.CustomerMergeDrafts, normalizedCheckpoint.CustomerMergeDrafts) ||
                      !SequenceEqual(checkpoint.AppliedCustomerMergeKeys, normalizedCheckpoint.AppliedCustomerMergeKeys) ||
                      !DictionaryEqual(checkpoint.VirtualCustomerMergeKeys, normalizedCheckpoint.VirtualCustomerMergeKeys) ||
                      !DictionaryEqual(checkpoint.CustomerGroupExpanded, normalizedCheckpoint.CustomerGroupExpanded) ||
                      !DictionaryEqual(checkpoint.LogicalCustomerMergeGroupByCustomerId, normalizedCheckpoint.LogicalCustomerMergeGroupByCustomerId) ||
                      !DictionaryEqual(checkpoint.CustomerApprovalStates, normalizedCheckpoint.CustomerApprovalStates) ||
                      !DictionaryEqual(checkpoint.CustomerReviewStates, normalizedCheckpoint.CustomerReviewStates) ||
                      !DictionaryEqual(checkpoint.ManualDocumentCustomerGroupLinks, normalizedCheckpoint.ManualDocumentCustomerGroupLinks) ||
                      !SequenceEqual(checkpoint.UnconnectedDocumentKeys, normalizedCheckpoint.UnconnectedDocumentKeys) ||
                      !SequenceEqual(checkpoint.RecommendationStates, normalizedCheckpoint.RecommendationStates) ||
                      !SequenceEqual(checkpoint.MatchingChanges, normalizedCheckpoint.MatchingChanges) ||
                      !DictionaryEqual(checkpoint.CustomerNameOverrides, normalizedCheckpoint.CustomerNameOverrides);

        return new(normalizedCheckpoint, changed);
    }

    private static Dictionary<string, string> NormalizeManualDocumentCustomerGroupLinks(
        IReadOnlyDictionary<string, string>? source,
        IReadOnlyDictionary<string, string?> replacements,
        IReadOnlySet<string> normalizedGroupIds,
        IReadOnlySet<string> knownCustomerIds,
        ISet<string> unconnectedDocumentKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
            var replacement = ResolveGroupReference(pair.Value, replacements, normalizedGroupIds, knownCustomerIds);
            if (replacement is not null)
            {
                result[pair.Key] = replacement;
            }
            else
            {
                unconnectedDocumentKeys.Add(pair.Key);
            }
        }

        return result;
    }

    private static LegacyGraphCheckpointRecommendationState NormalizeRecommendationState(
        LegacyGraphCheckpointRecommendationState state,
        IReadOnlyDictionary<string, string?> replacements,
        IReadOnlySet<string> normalizedGroupIds,
        IReadOnlySet<string> knownCustomerIds)
    {
        var workTargetKey = ResolveGroupReference(
            state.WorkTargetKey,
            replacements,
            normalizedGroupIds,
            knownCustomerIds);
        return state with
        {
            WorkTargetKey = workTargetKey,
            ExternalCustomerId = workTargetKey is null ? null : state.ExternalCustomerId,
        };
    }

    private static LegacyGraphCheckpointMatchingChange NormalizeMatchingChange(
        LegacyGraphCheckpointMatchingChange change,
        IReadOnlyDictionary<string, string?> replacements,
        IReadOnlySet<string> normalizedGroupIds,
        IReadOnlySet<string> knownCustomerIds)
    {
        var workTargetKey = ResolveGroupReference(
            change.WorkTargetKey,
            replacements,
            normalizedGroupIds,
            knownCustomerIds);
        var undoState = change.UndoState is null
            ? null
            : NormalizeUndoState(
                change.UndoState,
                replacements,
                normalizedGroupIds,
                knownCustomerIds);
        return change with
        {
            ChangeId = NormalizeMatchingChangeId(
                change.ChangeId,
                workTargetKey,
                change.ExternalCustomerId),
            WorkTargetKey = workTargetKey,
            ExternalCustomerId = workTargetKey is null ? null : change.ExternalCustomerId,
            UndoState = undoState,
        };
    }

    private static string NormalizeMatchingChangeId(
        string changeId,
        string? workTargetKey,
        string? externalCustomerId)
    {
        var parts = changeId.Split('\u001F');
        if (parts.Length != 3 ||
            !string.Equals(parts[0], "recommendation-scope", StringComparison.Ordinal))
        {
            return changeId;
        }

        // 復元後に存在しないWorkTargetKeyを履歴IDへ残さないため、
        // 有効なスコープへ再構成します。移行先がない履歴は、作業対象を
        // 参照しない履歴IDへ退避し、古いグループIDを保持しません。
        return workTargetKey is not null
            ? string.Join("\u001F", "recommendation-scope", workTargetKey, externalCustomerId ?? "")
            : string.Join("\u001F", "matching-change", parts[2], "unresolved");
    }

    private static LegacyGraphDetachedUndoState NormalizeUndoState(
        LegacyGraphDetachedUndoState state,
        IReadOnlyDictionary<string, string?> replacements,
        IReadOnlySet<string> normalizedGroupIds,
        IReadOnlySet<string> knownCustomerIds)
    {
        var documents = state.Documents
            .Select(document =>
            {
                var groupKey = ResolveGroupReference(
                    document.ManualCustomerGroupKey,
                    replacements,
                    normalizedGroupIds,
                    knownCustomerIds);
                return groupKey is null && document.ManualCustomerGroupKey is not null
                    ? document with { ManualCustomerGroupKey = null, IsUnconnected = true }
                    : document with { ManualCustomerGroupKey = groupKey };
            })
            .ToArray();
        return state with { Documents = documents };
    }

    private static Dictionary<string, string> NormalizeNameOverrides(
        IReadOnlyDictionary<string, string>? source,
        IReadOnlyDictionary<string, string?> replacements,
        IReadOnlySet<string> normalizedGroupIds,
        IReadOnlySet<string> knownCustomerIds)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in source ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!pair.Key.StartsWith("merge:", StringComparison.Ordinal))
            {
                result[pair.Key] = pair.Value;
                continue;
            }

            var groupKey = ResolveGroupReference(
                pair.Key["merge:".Length..],
                replacements,
                normalizedGroupIds,
                knownCustomerIds);
            if (groupKey is not null)
            {
                result[$"merge:{groupKey}"] = pair.Value;
            }
        }

        return result;
    }

    private static Dictionary<string, bool> NormalizeApprovalStates(
        IReadOnlyDictionary<string, bool>? source,
        IReadOnlyDictionary<string, string?> replacements,
        IReadOnlySet<string> normalizedGroupIds,
        IReadOnlySet<string> knownCustomerIds)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var pair in source ?? new Dictionary<string, bool>(StringComparer.Ordinal))
        {
            var replacement = ResolveGroupReference(pair.Key, replacements, normalizedGroupIds, knownCustomerIds);
            if (replacement is null)
            {
                continue;
            }

            result[replacement] = result.GetValueOrDefault(replacement) || pair.Value;
        }

        return result;
    }

    private static Dictionary<string, string> NormalizeReviewStates(
        IReadOnlyDictionary<string, string>? source,
        IReadOnlyDictionary<string, string?> replacements,
        IReadOnlySet<string> normalizedGroupIds,
        IReadOnlySet<string> knownCustomerIds)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in source ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            var replacement = ResolveGroupReference(pair.Key, replacements, normalizedGroupIds, knownCustomerIds);
            if (replacement is null)
            {
                continue;
            }

            var state = pair.Value;
            if (!string.Equals(pair.Key, replacement, StringComparison.Ordinal) &&
                replacement.StartsWith("customer:", StringComparison.Ordinal) &&
                state == LegacyGraphCustomerReviewStateValues.Approved)
            {
                state = LegacyGraphCustomerReviewStateValues.NeedsReview;
            }

            if (!result.TryGetValue(replacement, out var existing) ||
                ReviewStateRank(state) > ReviewStateRank(existing))
            {
                result[replacement] = state;
            }
        }

        return result;
    }

    private static int ReviewStateRank(string state) => state switch
    {
        LegacyGraphCustomerReviewStateValues.NeedsReview => 3,
        LegacyGraphCustomerReviewStateValues.Approved => 2,
        LegacyGraphCustomerReviewStateValues.Unreviewed => 1,
        _ => 0,
    };

    private static string? ResolveGroupReference(
        string? groupKey,
        IReadOnlyDictionary<string, string?> replacements,
        IReadOnlySet<string> normalizedGroupIds,
        IReadOnlySet<string> knownCustomerIds)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            return null;
        }

        if (normalizedGroupIds.Contains(groupKey))
        {
            return groupKey;
        }

        if (replacements.TryGetValue(groupKey, out var replacement))
        {
            return replacement;
        }

        const string customerPrefix = "customer:";
        // `customer:<id>` はグループ消滅後も有効な単独顧客スコープです。
        // チェックポイントには顧客マスター全体が含まれないため、復元済みの
        // 単独顧客参照を knownCustomerIds の再計算だけで落とさないようにします。
        return groupKey.StartsWith(customerPrefix, StringComparison.Ordinal) &&
               groupKey.Length > customerPrefix.Length
            ? groupKey
            : null;
    }

    private static bool GroupsEqual(
        IReadOnlyList<LegacyGraphCheckpointMergeGroup>? first,
        IReadOnlyList<LegacyGraphCheckpointMergeGroup>? second)
    {
        var firstArray = first ?? [];
        var secondArray = second ?? [];
        return firstArray.Count == secondArray.Count &&
               firstArray.Zip(secondArray).All(pair =>
                   string.Equals(pair.First.GroupId, pair.Second.GroupId, StringComparison.Ordinal) &&
                   string.Equals(pair.First.Origin, pair.Second.Origin, StringComparison.Ordinal) &&
                   SequenceEqual(pair.First.CustomerIds, pair.Second.CustomerIds));
    }

    private static bool SequenceEqual<T>(
        IEnumerable<T>? first,
        IEnumerable<T>? second) =>
        (first ?? []).SequenceEqual(second ?? []);

    private static bool DictionaryEqual<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue>? first,
        IReadOnlyDictionary<TKey, TValue>? second)
        where TKey : notnull
    {
        var firstDictionary = first ?? new Dictionary<TKey, TValue>();
        var secondDictionary = second ?? new Dictionary<TKey, TValue>();
        return firstDictionary.Count == secondDictionary.Count &&
               firstDictionary.All(pair =>
                   secondDictionary.TryGetValue(pair.Key, out var value) &&
                   EqualityComparer<TValue>.Default.Equals(pair.Value, value));
    }
}
