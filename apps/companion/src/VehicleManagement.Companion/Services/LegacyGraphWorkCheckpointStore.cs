using System.IO;
using System.Text;
using System.Text.Json;
using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public static class LegacyGraphWorkCheckpointSchema
{
    public const int CurrentVersion = 5;
    public const string Kind = "abacus-legacy-graph-work-checkpoint";
}

/// <summary>
/// 顧客単位の最終確認状態です。候補の判定状態とは別に保存します。
/// </summary>
public static class LegacyGraphCustomerReviewStateValues
{
    public const string Unreviewed = "unreviewed";
    public const string NeedsReview = "needs-review";
    public const string Approved = "approved";

    public static bool IsSupported(string value) => value is Unreviewed or NeedsReview or Approved;
}

public sealed record LegacyGraphCheckpointMergeGroup(
    string GroupId,
    string Origin,
    string[] CustomerIds);

public sealed record LegacyGraphCheckpointMergeDraft(
    string GroupKey,
    string[] CandidateCustomerIds,
    Dictionary<string, string> FieldSelections,
    Dictionary<string, string> SelectedValues,
    DateTimeOffset SavedAtUtc);

public sealed record LegacyGraphCheckpointDetailState(
    string DocumentKey,
    string DetailsJson,
    string DocumentType,
    string MaintenanceCategory,
    string ClassificationWarning);

public sealed record LegacyGraphCheckpointImageMapping(
    string OutputFolderPath,
    string ReportPath,
    string SourceFilePath,
    int InternalVehicleRecordCount,
    int VehicleCsvRowCount,
    int JpegImageCount,
    int GifPlaceholderCount,
    int MatchedImageCount,
    int NoImageCount,
    int ReviewCount,
    int UnmatchedCount,
    int MultipleCandidateCount,
    int UnknownImageReferenceCount,
    int DuplicateImageReferenceCount,
    int DuplicateImageSha256Count,
    int UnreferencedImageCount,
    LegacyGraphCheckpointImageMappingRow[] Mappings);

public sealed record LegacyGraphCheckpointImageMappingRow(
    int Index,
    string RecordIdHex,
    string? ImageIdHex,
    string? ImageRelativePath,
    string? ImageSha256,
    string? VehicleFileName,
    int? VehicleRowNumber,
    string Status,
    string Evidence);

public sealed record LegacyGraphCheckpointRecommendationState(
    string CandidateId,
    string Decision,
    string Lifecycle = LegacyGraphRecommendationLifecycle.Active,
    string? ResolutionReason = null,
    DateTimeOffset? UpdatedAtUtc = null,
    string? WorkTargetKey = null,
    string? ExternalCustomerId = null);

public sealed record LegacyGraphCheckpointMatchingChange(
    string ChangeId,
    string Kind,
    string SubjectId,
    string Title,
    string Details,
    DateTimeOffset CreatedAtUtc,
    string? CustomerId = null,
    string? WorkTargetKey = null,
    string? ExternalCustomerId = null);

public sealed record LegacyGraphWorkCheckpoint(
    string Kind,
    int Version,
    string PackageId,
    string SourcePath,
    string SourceFingerprint,
    string CandidatePackagePath,
    string CandidateManifestSha256,
    string VehicleExportPath,
    bool LegacyExportSubsetActive,
    string UiMode,
    string? SelectedItemType,
    string? SelectedItemId,
    bool ImportConfirmed,
    Dictionary<string, string> ManualDocumentVehicleLinks,
    Dictionary<string, string> ManualVehicleCustomerLinks,
    Dictionary<string, string> ManualDocumentCustomerGroupLinks,
    Dictionary<string, string> DocumentLinkMethods,
    Dictionary<string, string> DocumentLinkReasons,
    string[] UnconnectedDocumentKeys,
    string[] TrayDocumentKeys,
    string[] ExcludedDocumentKeys,
    string[] TrayVehicleIds,
    string[] TrashCustomerIds,
    string[] TrashVehicleIds,
    string[] TrashDocumentKeys,
    LegacyGraphCheckpointMergeGroup[] CustomerMergeGroups,
    Dictionary<string, string> CustomerMergeGroupByCustomerId,
    Dictionary<string, LegacyGraphCheckpointMergeDraft> CustomerMergeDrafts,
    string[] AppliedCustomerMergeKeys,
    Dictionary<string, string> VirtualCustomerMergeKeys,
    Dictionary<string, bool> CustomerGroupExpanded,
    LegacyGraphCheckpointDetailState[] DetailStates,
    LegacyGraphCheckpointImageMapping? ImageMapping,
    DateTimeOffset SavedAtUtc,
    LegacyGraphCheckpointRecommendationState[]? RecommendationStates = null,
    Dictionary<string, string>? CustomerNameOverrides = null,
    string? MatchingCategory = null,
    int MatchingRecommendationIndex = -1,
    LegacyGraphCheckpointMatchingChange[]? MatchingChanges = null,
    Dictionary<string, string>? LogicalCustomerMergeGroupByCustomerId = null,
    Dictionary<string, string[]>? MatchingManualCustomerCandidateTargets = null,
    Dictionary<string, bool>? CustomerApprovalStates = null,
    Dictionary<string, string>? CustomerReviewStates = null);

/// <summary>
/// グラフ操作のチェックポイントを、作業フォルダー内へ原子的に保存します。
/// ここでは候補データを再生成せず、ユーザーが行った操作状態とおすすめ判定状態だけを保持します。
/// </summary>
public sealed class LegacyGraphWorkCheckpointStore
{
    private const string FileName = "graph-state.json";
    private const long MaximumCheckpointBytes = 64L * 1024 * 1024;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public string GetCheckpointPath(string checkpointsPath) =>
        Path.Combine(ValidateDirectory(checkpointsPath), FileName);

    public async Task SaveAsync(
        string checkpointsPath,
        LegacyGraphWorkCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Version != LegacyGraphWorkCheckpointSchema.CurrentVersion ||
            !string.Equals(checkpoint.Kind, LegacyGraphWorkCheckpointSchema.Kind, StringComparison.Ordinal))
        {
            throw new InvalidDataException("作業チェックポイントのスキーマバージョンが不正です。");
        }

        var directory = ValidateDirectory(checkpointsPath);
        var checkpointPath = Path.Combine(directory, FileName);
        var temporaryPath = $"{checkpointPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(checkpoint, JsonOptions);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(stream, Utf8NoBom, 64 * 1024))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, checkpointPath, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    public async Task<LegacyGraphWorkCheckpoint> ReadAsync(
        string checkpointsPath,
        CancellationToken cancellationToken = default)
    {
        var directory = ValidateDirectory(checkpointsPath);
        var checkpointPath = Path.Combine(directory, FileName);
        var info = new FileInfo(checkpointPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length > MaximumCheckpointBytes)
        {
            throw new InvalidDataException("グラフ作業チェックポイントが見つからないか、サイズが不正です。");
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(checkpointPath, cancellationToken);
            // 旧バージョンがUTF-8 BOM付きで保存したチェックポイントも再開できるようにする。
            var checkpoint = JsonSerializer.Deserialize<LegacyGraphWorkCheckpoint>(
                RemoveUtf8Bom(bytes),
                JsonOptions);
            if (checkpoint is null)
            {
                throw new InvalidDataException("グラフ作業チェックポイントが空です。");
            }

            checkpoint = UpgradeCheckpoint(checkpoint);
            ValidateCheckpoint(checkpoint);
            return checkpoint;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("グラフ作業チェックポイントのJSONが不正です。", exception);
        }
    }

    private static byte[] RemoveUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;

    private static LegacyGraphWorkCheckpoint UpgradeCheckpoint(LegacyGraphWorkCheckpoint checkpoint)
    {
        if (checkpoint.Version is not 1 and not 2 and not 3 and not 4 and not LegacyGraphWorkCheckpointSchema.CurrentVersion)
        {
            return checkpoint;
        }

        return checkpoint with
        {
            Version = LegacyGraphWorkCheckpointSchema.CurrentVersion,
            RecommendationStates = NormalizeRecommendationStates(checkpoint.RecommendationStates),
            CustomerNameOverrides = checkpoint.CustomerNameOverrides ?? new Dictionary<string, string>(),
            MatchingCategory = string.IsNullOrWhiteSpace(checkpoint.MatchingCategory)
                ? LegacyMatchingCategoryKinds.Customer
                : checkpoint.MatchingCategory,
            MatchingRecommendationIndex = checkpoint.MatchingRecommendationIndex,
            MatchingChanges = checkpoint.MatchingChanges ?? [],
            LogicalCustomerMergeGroupByCustomerId = checkpoint.LogicalCustomerMergeGroupByCustomerId ??
                                                    new Dictionary<string, string>(),
            MatchingManualCustomerCandidateTargets = checkpoint.MatchingManualCustomerCandidateTargets ??
                                                    new Dictionary<string, string[]>(),
            CustomerApprovalStates = checkpoint.CustomerApprovalStates ?? new Dictionary<string, bool>(),
            CustomerReviewStates = checkpoint.CustomerReviewStates ??
                                   (checkpoint.CustomerApprovalStates ?? new Dictionary<string, bool>())
                                       .ToDictionary(
                                           pair => pair.Key,
                                           pair => pair.Value
                                               ? LegacyGraphCustomerReviewStateValues.Approved
                                               : LegacyGraphCustomerReviewStateValues.Unreviewed,
                                           StringComparer.Ordinal),
        };
    }

    private static LegacyGraphCheckpointRecommendationState[] NormalizeRecommendationStates(
        IEnumerable<LegacyGraphCheckpointRecommendationState>? states) =>
        (states ?? [])
        .Select(state => state with
        {
            Lifecycle = string.IsNullOrWhiteSpace(state.Lifecycle)
                ? LegacyGraphRecommendationLifecycle.Active
                : state.Lifecycle,
        })
        .ToArray();

    private static void ValidateCheckpoint(LegacyGraphWorkCheckpoint checkpoint)
    {
        if (!string.Equals(checkpoint.Kind, LegacyGraphWorkCheckpointSchema.Kind, StringComparison.Ordinal))
        {
            throw new InvalidDataException("グラフ作業チェックポイントの種類が不正です。");
        }

        if (checkpoint.Version != LegacyGraphWorkCheckpointSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"グラフ作業チェックポイントのバージョン{checkpoint.Version}には対応していません。"
                + "候補パッケージを再作成してください。");
        }

        if (string.IsNullOrWhiteSpace(checkpoint.PackageId) ||
            string.IsNullOrWhiteSpace(checkpoint.SourcePath) ||
            string.IsNullOrWhiteSpace(checkpoint.SourceFingerprint) ||
            checkpoint.SourceFingerprint.Length != 64 ||
            !checkpoint.SourceFingerprint.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(checkpoint.CandidatePackagePath) ||
            string.IsNullOrWhiteSpace(checkpoint.CandidateManifestSha256) ||
            checkpoint.CandidateManifestSha256.Length != 64 ||
            !checkpoint.CandidateManifestSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("グラフ作業チェックポイントの入力指紋または候補パスが不正です。");
        }

        if (checkpoint.ManualDocumentVehicleLinks is null ||
            checkpoint.ManualVehicleCustomerLinks is null ||
            checkpoint.ManualDocumentCustomerGroupLinks is null ||
            checkpoint.DocumentLinkMethods is null ||
            checkpoint.DocumentLinkReasons is null ||
            checkpoint.UnconnectedDocumentKeys is null ||
            checkpoint.TrayDocumentKeys is null ||
            checkpoint.ExcludedDocumentKeys is null ||
            checkpoint.TrayVehicleIds is null ||
            checkpoint.TrashCustomerIds is null ||
            checkpoint.TrashVehicleIds is null ||
            checkpoint.TrashDocumentKeys is null ||
            checkpoint.CustomerMergeGroups is null ||
            checkpoint.CustomerMergeGroupByCustomerId is null ||
            checkpoint.CustomerMergeDrafts is null ||
            checkpoint.AppliedCustomerMergeKeys is null ||
            checkpoint.VirtualCustomerMergeKeys is null ||
            checkpoint.CustomerGroupExpanded is null ||
            checkpoint.DetailStates is null ||
            checkpoint.RecommendationStates is null ||
            checkpoint.CustomerNameOverrides is null ||
            checkpoint.MatchingChanges is null ||
            checkpoint.LogicalCustomerMergeGroupByCustomerId is null ||
            checkpoint.MatchingManualCustomerCandidateTargets is null ||
            checkpoint.CustomerApprovalStates is null ||
            checkpoint.CustomerReviewStates is null)
        {
            throw new InvalidDataException("グラフ作業チェックポイントの必須状態が欠落しています。");
        }

        if (checkpoint.CustomerMergeGroups.Any(group =>
                group is null || string.IsNullOrWhiteSpace(group.GroupId) ||
                group.CustomerIds is null || group.CustomerIds.Length == 0) ||
            checkpoint.DetailStates.Any(detail =>
                detail is null || string.IsNullOrWhiteSpace(detail.DocumentKey)) ||
            checkpoint.RecommendationStates.Any(state =>
                state is null ||
                string.IsNullOrWhiteSpace(state.CandidateId) ||
                !AbacusRecommendationDecisionValues.IsSupported(state.Decision) ||
                !LegacyGraphRecommendationLifecycle.IsSupported(state.Lifecycle)) ||
            checkpoint.RecommendationStates
                .GroupBy(state => state.CandidateId, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1) ||
            checkpoint.RecommendationStates.Any(state =>
                (state.WorkTargetKey is null) != (state.ExternalCustomerId is null) ||
                state.WorkTargetKey is not null && string.IsNullOrWhiteSpace(state.WorkTargetKey) ||
                state.ExternalCustomerId is not null && string.IsNullOrWhiteSpace(state.ExternalCustomerId)) ||
            checkpoint.CustomerNameOverrides.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            checkpoint.MatchingCategory is not null &&
                checkpoint.MatchingCategory is not (LegacyMatchingCategoryKinds.Customer or
                    LegacyMatchingCategoryKinds.Vehicle or LegacyMatchingCategoryKinds.Document) ||
            checkpoint.MatchingChanges.Any(change =>
                change is null || string.IsNullOrWhiteSpace(change.ChangeId) ||
                string.IsNullOrWhiteSpace(change.Kind) || string.IsNullOrWhiteSpace(change.SubjectId) ||
                (change.WorkTargetKey is null) != (change.ExternalCustomerId is null) ||
                change.WorkTargetKey is not null && string.IsNullOrWhiteSpace(change.WorkTargetKey) ||
                change.ExternalCustomerId is not null && string.IsNullOrWhiteSpace(change.ExternalCustomerId)) ||
            checkpoint.LogicalCustomerMergeGroupByCustomerId.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            checkpoint.MatchingManualCustomerCandidateTargets.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                pair.Value.Any(string.IsNullOrWhiteSpace)) ||
            checkpoint.CustomerApprovalStates.Any(pair => string.IsNullOrWhiteSpace(pair.Key)) ||
            checkpoint.CustomerReviewStates.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
                !LegacyGraphCustomerReviewStateValues.IsSupported(pair.Value)))
        {
            throw new InvalidDataException("グラフ作業チェックポイントの候補IDが不正です。");
        }

        if (checkpoint.ManualDocumentVehicleLinks.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            checkpoint.ManualVehicleCustomerLinks.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            checkpoint.ManualDocumentCustomerGroupLinks.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            checkpoint.DocumentLinkMethods.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            checkpoint.DocumentLinkReasons.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            checkpoint.CustomerMergeGroupByCustomerId.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            checkpoint.VirtualCustomerMergeKeys.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) ||
            checkpoint.CustomerGroupExpanded.Any(pair => string.IsNullOrWhiteSpace(pair.Key)) ||
            checkpoint.CustomerMergeDrafts.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null ||
                string.IsNullOrWhiteSpace(pair.Value.GroupKey) ||
                pair.Value.CandidateCustomerIds is null ||
                pair.Value.FieldSelections is null ||
                pair.Value.SelectedValues is null))
        {
            throw new InvalidDataException("グラフ作業チェックポイントの辞書項目が不正です。");
        }

        if (checkpoint.ImageMapping is not null &&
            (string.IsNullOrWhiteSpace(checkpoint.ImageMapping.OutputFolderPath) ||
             string.IsNullOrWhiteSpace(checkpoint.ImageMapping.ReportPath) ||
             string.IsNullOrWhiteSpace(checkpoint.ImageMapping.SourceFilePath) ||
             checkpoint.ImageMapping.Mappings is null ||
             checkpoint.ImageMapping.Mappings.Any(mapping => mapping is null)))
        {
            throw new InvalidDataException("グラフ作業チェックポイントの画像対応付けが不正です。");
        }
    }

    private static string ValidateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("チェックポイントフォルダーが空です。", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"チェックポイントフォルダーが見つかりません: {fullPath}");
        }

        var info = new DirectoryInfo(fullPath);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("チェックポイントフォルダーにシンボリックリンクは使用できません。");
        }

        return fullPath;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 保存本体の例外を優先し、残った一時ファイルは次回保存時に置き換えます。
        }
    }
}
