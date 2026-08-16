namespace VehicleManagement.Companion.Services;

public sealed record LegacyGraphWorkCheckpointMergeRecoveryResult(
    LegacyGraphWorkCheckpoint Checkpoint,
    bool Changed);

/// <summary>
/// 旧バージョンのグラフ作業チェックポイントに残る、顧客統合グループの
/// 二重所属だけを復元前に整理します。
///
/// 統合確定後の論理グループを優先し、仮グループに残った同じ顧客を除外します。
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
        }

        var normalizedGroupIds = normalizedGroups
            .Select(group => group.GroupId)
            .ToHashSet(StringComparer.Ordinal);
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
        var normalizedApprovalStates = NormalizeGroupStateDictionary(
            checkpoint.CustomerApprovalStates,
            sourceGroupIds,
            normalizedGroupIds);
        var normalizedReviewStates = NormalizeGroupStateDictionary(
            checkpoint.CustomerReviewStates,
            sourceGroupIds,
            normalizedGroupIds);

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
        };

        var changed = !GroupsEqual(checkpoint.CustomerMergeGroups, normalizedCheckpoint.CustomerMergeGroups) ||
                      !DictionaryEqual(checkpoint.CustomerMergeGroupByCustomerId, normalizedCheckpoint.CustomerMergeGroupByCustomerId) ||
                      !DictionaryEqual(checkpoint.CustomerMergeDrafts, normalizedCheckpoint.CustomerMergeDrafts) ||
                      !SequenceEqual(checkpoint.AppliedCustomerMergeKeys, normalizedCheckpoint.AppliedCustomerMergeKeys) ||
                      !DictionaryEqual(checkpoint.VirtualCustomerMergeKeys, normalizedCheckpoint.VirtualCustomerMergeKeys) ||
                      !DictionaryEqual(checkpoint.CustomerGroupExpanded, normalizedCheckpoint.CustomerGroupExpanded) ||
                      !DictionaryEqual(checkpoint.LogicalCustomerMergeGroupByCustomerId, normalizedCheckpoint.LogicalCustomerMergeGroupByCustomerId) ||
                      !DictionaryEqual(checkpoint.CustomerApprovalStates, normalizedCheckpoint.CustomerApprovalStates) ||
                      !DictionaryEqual(checkpoint.CustomerReviewStates, normalizedCheckpoint.CustomerReviewStates);

        return new(normalizedCheckpoint, changed);
    }

    private static Dictionary<string, TValue> NormalizeGroupStateDictionary<TValue>(
        IReadOnlyDictionary<string, TValue>? source,
        IReadOnlySet<string> sourceGroupIds,
        IReadOnlySet<string> normalizedGroupIds)
    {
        var result = new Dictionary<string, TValue>(StringComparer.Ordinal);
        foreach (var pair in source ?? new Dictionary<string, TValue>(StringComparer.Ordinal))
        {
            if (!sourceGroupIds.Contains(pair.Key) || normalizedGroupIds.Contains(pair.Key))
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
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
