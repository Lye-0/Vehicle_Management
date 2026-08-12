using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using VehicleManagement.AbacusImport;

namespace VehicleManagement.Companion.Services;

public sealed record AbacusBulkImageCandidate(
    string CandidateId,
    string SourceRelativePath,
    string PackageImageFileName,
    string Status,
    string StatusLabel,
    string MatchStrategy,
    string Identifier,
    string CustomerName,
    string VehicleName,
    string ChassisNumber,
    string RegistrationNumber,
    long FileSize,
    int PixelWidth,
    int PixelHeight,
    string ImageSha256,
    string Reason);

public sealed record AbacusBulkImagePreparationResult(
    string SourceFolder,
    string VehicleExportFolder,
    string PackagePath,
    string ManifestPath,
    string ReportPath,
    string ManifestSha256,
    int SourceImageCount,
    int EmbeddedImageCount,
    int MatchedCount,
    int ReviewCount,
    int NotFoundCount,
    int RejectedCount,
    int DuplicateCount,
    int ImageCount,
    IReadOnlyList<AbacusBulkImageCandidate> Candidates,
    IReadOnlyList<string> Warnings);

/// <summary>
/// ABACUSフォルダー内の標準画像と、読み取り対象のabx-*.ucsに埋め込まれた
/// JPEG候補を一括で検査し、ファイル名またはUCS周辺の車台番号を車両一覧CSVへ
/// 読み取り専用で照合します。照合が一意な画像だけを既存の画像登録前パッケージへ
/// まとめ、UCS原本は推測分割・書換えしません。
/// </summary>
public sealed class AbacusBulkImagePreparationStore
{
    private const long MaximumImageBytes = 256L * 1024 * 1024;
    private const long MaximumTotalImageBytes = 1L * 1024 * 1024 * 1024;
    private const long MaximumPixels = 50_000_000;
    private const int MaximumSourceImages = 5_000;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumReportBytes = 16 * 1024 * 1024;
    private const long MaximumEmbeddedContainerBytes = 1L * 1024 * 1024 * 1024;
    private const int MaximumEmbeddedSegments = 5_000;
    private const int EmbeddedIdentifierWindowBytes = 2 * 1024 * 1024;
    private const int MaximumEmbeddedRejectedReports = 5_000;
    private const int MaximumJpegMarkerPaddingBytes = 32;
    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding AbacusEncoding = CreateAbacusEncoding();

    private readonly AbacusVehicleExportReader vehicleExportReader = new();

    public async Task<AbacusBulkImagePreparationResult> CreateAsync(
        string sourceFolder,
        string vehicleExportFolder,
        string destinationParent,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = ValidateFolder(sourceFolder, "ABACUSフォルダー");
        var vehicleRoot = ValidateFolder(vehicleExportFolder, "車両一覧CSVフォルダー");
        var destinationRoot = ValidateFolder(destinationParent, "一括画像パッケージ保存先");
        ValidateDestination(sourceRoot, vehicleRoot, destinationRoot);

        var vehicleExport = await vehicleExportReader.ReadAsync(vehicleRoot, cancellationToken);
        if (!vehicleExport.IsValid)
        {
            var details = string.Join(
                "\n",
                vehicleExport.Errors.Take(20).Select(error =>
                    $"{error.RowNumber?.ToString() ?? "ファイル"}: {error.Message}"));
            throw new InvalidDataException(
                $"車両一覧CSVの再検証に失敗しました。{(details.Length == 0 ? string.Empty : $"\n{details}")}");
        }

        var sourceFiles = EnumerateImageFiles(sourceRoot)
            .Take(MaximumSourceImages + 1)
            .ToList();
        if (sourceFiles.Count > MaximumSourceImages)
        {
            throw new InvalidDataException($"一括画像の件数が上限{MaximumSourceImages:N0}件を超えています。");
        }

        var candidates = new List<AbacusBulkImageCandidate>(sourceFiles.Count);
        var warnings = new List<string>();
        var packagedImages = new List<PreparedImage>();
        var accepted = new List<PreparedImage>();
        var totalBytes = 0L;
        var standardRejectedCount = 0;
        for (var index = 0; index < sourceFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = sourceFiles[index];
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath).Replace('\\', '/');
            try
            {
                var validated = await ValidateImageAsync(sourcePath, cancellationToken);
                totalBytes = checked(totalBytes + validated.FileSize);
                if (totalBytes > MaximumTotalImageBytes)
                {
                    throw new InvalidDataException(
                        $"一括画像の合計サイズが上限{MaximumTotalImageBytes:N0} bytesを超えています。");
                }

                var matches = FindVehicleMatches(relativePath, vehicleExport.Rows);
                var status = matches.Count switch
                {
                    1 => "matched",
                    0 => "not-found",
                    _ => "review",
                };
                var statusLabel = status switch
                {
                    "matched" => "一意に照合",
                    "review" => "要確認（複数候補）",
                    _ => "未照合",
                };
                var match = matches.Count == 1 ? matches[0] : null;
                var candidateId = CreateCandidateId(relativePath, validated.ImageSha256);
                var packageImagePath = $"images/{candidateId}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
                var reason = status switch
                {
                    "matched" => $"ファイル名から{(match!.UsedChassis ? "車台番号" : "登録番号")}を検出し、車両一覧の1行へ一致しました。",
                    "review" => $"ファイル名から{matches.Count:N0}件の車両候補が見つかりました。自動登録せず要確認として残しました。",
                    _ => "ファイル名に車台番号または登録番号が含まれていないため、自動照合しませんでした。",
                };
                var candidate = new AbacusBulkImageCandidate(
                    candidateId,
                    relativePath,
                    packageImagePath,
                    status,
                    statusLabel,
                    match is null ? "none" : match.UsedChassis ? "filename-chassis" : "filename-registration",
                    match?.Identifier ?? string.Empty,
                    match?.Row.CustomerName ?? string.Empty,
                    match?.Row.VehicleName ?? string.Empty,
                    match?.Row.ChassisNumber ?? string.Empty,
                    match?.Row.RegistrationNumber ?? string.Empty,
                    validated.FileSize,
                    validated.PixelWidth,
                    validated.PixelHeight,
                    validated.ImageSha256,
                    reason);
                candidates.Add(candidate);
                packagedImages.Add(new PreparedImage(sourcePath, null, 0, 0, candidate));
                if (match is not null)
                {
                    accepted.Add(packagedImages[^1]);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or NotSupportedException)
            {
                standardRejectedCount++;
                var candidateId = CreateCandidateId(relativePath, index.ToString());
                candidates.Add(new AbacusBulkImageCandidate(
                    candidateId,
                    relativePath,
                    string.Empty,
                    "rejected",
                    "抽出不可",
                    "none",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    0,
                    string.Empty,
                    exception.Message));
            }
        }

        var embeddedImageCount = 0;
        var embeddedRejectedCount = 0;
        var embeddedContainers = EnumerateEmbeddedContainers(sourceRoot).ToList();
        foreach (var containerPath in embeddedContainers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containerRelativePath = Path.GetRelativePath(sourceRoot, containerPath).Replace('\\', '/');
            var segments = await FindJpegSegmentsAsync(containerPath, cancellationToken);
            var previousImageEnd = 0L;
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentImageEnd = checked(segment.Offset + segment.Length);
                if (embeddedImageCount >= MaximumSourceImages)
                {
                    warnings.Add($"UCS内部JPEGの検証件数が上限{MaximumSourceImages:N0}件に達したため、残りは要確認として扱っていません。");
                    break;
                }

                ValidatedImage validated;
                try
                {
                    validated = await ValidateEmbeddedImageAsync(containerPath, segment, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or
                                                   NotSupportedException or ArgumentException or
                                                   System.Runtime.InteropServices.COMException or
                                                   System.IO.FileFormatException)
                {
                    embeddedRejectedCount++;
                    if (embeddedRejectedCount <= MaximumEmbeddedRejectedReports)
                    {
                        var rejectedId = CreateCandidateId(
                            $"{containerRelativePath}@{segment.Offset:N0}",
                            exception.Message);
                        candidates.Add(new AbacusBulkImageCandidate(
                            rejectedId,
                            $"{containerRelativePath}@{segment.Offset:N0}",
                            string.Empty,
                            "rejected",
                            "抽出不可",
                            "ucs-jpeg",
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            segment.Length,
                            0,
                            0,
                            string.Empty,
                            $"UCS内部JPEG候補を画像として検証できませんでした: {exception.Message}"));
                    }

                    continue;
                }

                embeddedImageCount++;
                totalBytes = checked(totalBytes + validated.FileSize);
                if (totalBytes > MaximumTotalImageBytes)
                {
                    throw new InvalidDataException(
                        $"一括画像の合計サイズが上限{MaximumTotalImageBytes:N0} bytesを超えています。");
                }
                var imageRelativePath = $"{containerRelativePath}@{segment.Offset:N0}";
                var embeddedMatches = await FindEmbeddedVehicleMatchesAsync(
                    containerPath,
                    segment.Offset,
                    previousImageEnd,
                    vehicleExport.Rows,
                    cancellationToken);
                if (segment.Offset >= previousImageEnd)
                {
                    previousImageEnd = currentImageEnd;
                }
                var match = embeddedMatches.Count == 1 && embeddedMatches[0].UsedChassis
                    ? embeddedMatches[0]
                    : null;
                var status = match is not null
                    ? "matched"
                    : embeddedMatches.Count > 0 ? "review" : "not-found";
                var statusLabel = status switch
                {
                    "matched" => "一意に照合",
                    "review" => "要確認（複数候補）",
                    _ => "未照合",
                };
                var candidateId = CreateCandidateId(imageRelativePath, validated.ImageSha256);
                var packageImagePath = $"images/{candidateId}.jpg";
                var reason = status switch
                {
                    "matched" => $"{containerRelativePath}からJPEGを読み取り、直前画像以降のUCSレコード領域にある車台番号が車両一覧の1行へ一意に一致しました。",
                    "review" => $"{containerRelativePath}からJPEGを読み取りましたが、同じUCSレコード領域に{embeddedMatches.Count:N0}件の車両候補があるため自動登録しません。",
                    _ => $"{containerRelativePath}からJPEGを読み取りましたが、同じUCSレコード領域に車両一覧と一致する強い識別子がありません。",
                };
                var candidate = new AbacusBulkImageCandidate(
                    candidateId,
                    imageRelativePath,
                    packageImagePath,
                    status,
                    statusLabel,
                    match is null ? "ucs-record-identifier" : "ucs-record-chassis",
                    match?.Identifier ?? string.Empty,
                    match?.Row.CustomerName ?? string.Empty,
                    match?.Row.VehicleName ?? string.Empty,
                    match?.Row.ChassisNumber ?? string.Empty,
                    match?.Row.RegistrationNumber ?? string.Empty,
                    validated.FileSize,
                    validated.PixelWidth,
                    validated.PixelHeight,
                    validated.ImageSha256,
                    reason);
                candidates.Add(candidate);
                packagedImages.Add(new PreparedImage(null, containerPath, segment.Offset, segment.Length, candidate));
                if (match is not null)
                {
                    accepted.Add(packagedImages[^1]);
                }
            }
        }

        var deduplicated = DeduplicatePackagedImages(candidates, packagedImages);
        packagedImages = deduplicated.PackagedImages.ToList();
        accepted = packagedImages
            .Where(prepared => prepared.Candidate.Status == "matched")
            .ToList();
        var duplicateCount = deduplicated.DuplicateCount;
        AddContainerWarning(sourceRoot, sourceFiles.Count, embeddedContainers.Count, embeddedImageCount, embeddedRejectedCount, warnings);
        var packagePath = CreateUniquePackageDirectory(destinationRoot);
        var imagesPath = Path.Combine(packagePath, "images");
        Directory.CreateDirectory(imagesPath);
        try
        {
            foreach (var prepared in packagedImages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationPath = Path.Combine(
                    packagePath,
                    prepared.Candidate.PackageImageFileName.Replace('/', Path.DirectorySeparatorChar));
                if (prepared.ContainerPath is not null)
                {
                    await CopySegmentAndVerifyAsync(
                        prepared.ContainerPath,
                        prepared.SourceOffset,
                        prepared.SourceLength,
                        destinationPath,
                        prepared.Candidate.ImageSha256,
                        cancellationToken);
                }
                else
                {
                    await CopyAndVerifyAsync(
                        prepared.SourcePath!,
                        destinationPath,
                        prepared.Candidate.ImageSha256,
                        cancellationToken);
                }
            }

            var manifest = new BulkPreviewManifest(
                1,
                "abacus-image-registration-preview",
                "preview-only",
                DateTime.UtcNow,
                new BulkPreviewSource(sourceRoot, vehicleRoot, "filename-and-ucs-record-identifier-bulk-match"),
                new BulkPreviewSummary(
                    accepted.Count,
                    candidates.Count(item => item.Status == "review"),
                    candidates.Count(item => item.Status == "not-found"),
                    standardRejectedCount + embeddedRejectedCount,
                    "標準画像のファイル名、またはUCSレコード領域の車台番号が車両一覧CSVの1行へ一意に一致した画像だけを登録前候補に含めています。"),
                accepted.Select(prepared => new BulkPreviewCandidate(
                    prepared.Candidate.CandidateId,
                    prepared.Candidate.PackageImageFileName,
                    prepared.Candidate.ImageSha256,
                    prepared.Candidate.CustomerName,
                    prepared.Candidate.VehicleName,
                    prepared.Candidate.ChassisNumber,
                    prepared.Candidate.RegistrationNumber)).ToArray());
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            if (manifestBytes.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("一括画像マニフェストのサイズが上限を超えています。");
            }

            var manifestPath = Path.Combine(packagePath, "manifest.json");
            await WriteAtomicallyAsync(manifestPath, manifestBytes, cancellationToken);

            var report = new BulkReport(
                1,
                "abacus-bulk-image-preparation-report",
                "review-only",
                DateTime.UtcNow,
                sourceRoot,
                vehicleRoot,
                candidates);
            var reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            if (reportBytes.Length > MaximumReportBytes)
            {
                throw new InvalidDataException("一括画像の照合レポートが大きすぎます。");
            }

            var reportPath = Path.Combine(packagePath, "image-batch-report.json");
            await WriteAtomicallyAsync(reportPath, reportBytes, cancellationToken);
            if (sourceFiles.Count == 0 && embeddedImageCount == 0)
            {
                warnings.Add("ABACUSフォルダー内に標準PNG/JPEG、または検証可能なUCS内部JPEGがありません。原本を変更せず、画像表示キャプチャまたは別形式の解析が必要です。");
            }
            else if (embeddedImageCount > 0)
            {
                warnings.Add($"UCSコンテナから検証可能なJPEGを{embeddedImageCount:N0}件読み取りました。原本は変更していません。識別子が一意な画像だけを登録前候補へ含めています。");
            }

            if (embeddedRejectedCount > 0)
            {
                warnings.Add(
                    $"UCS内部JPEG候補のうち{embeddedRejectedCount:N0}件は実画像として検証できず、登録前候補へ含めていません。" +
                    "灰色・均一色の仮画像を保存しないための安全措置です。実画像が必要な場合は画像表示画面のキャプチャ、または正式なFileMakerコンテナ解析を使用してください。");
            }

            if (duplicateCount > 0)
            {
                warnings.Add($"SHA-256が一致する重複画像を{duplicateCount:N0}件除外しました。最も確度の高い1件だけをパッケージへ保存し、詳細はimage-batch-report.jsonに記録しています。");
            }

            if (candidates.Any(item => item.Status is "review" or "not-found" or "rejected" or "duplicate"))
            {
                warnings.Add("要確認・未照合の検証済み画像は一括パッケージへ保存しますが、登録前候補へは含めていません。image-batch-report.jsonで一覧を確認し、必要な画像だけ既存の確認済み経路へ回してください。抽出不可画像は保存していません。");
            }

            return new AbacusBulkImagePreparationResult(
                sourceRoot,
                vehicleRoot,
                packagePath,
                manifestPath,
                reportPath,
                Convert.ToHexString(SHA256.HashData(manifestBytes)),
                sourceFiles.Count,
                embeddedImageCount,
                candidates.Count(item => item.Status == "matched"),
                candidates.Count(item => item.Status == "review"),
                candidates.Count(item => item.Status == "not-found"),
                standardRejectedCount + embeddedRejectedCount,
                duplicateCount,
                accepted.Count,
                candidates,
                warnings);
        }
        catch
        {
            if (Directory.Exists(packagePath) && IsSameOrSubPath(packagePath, destinationRoot))
            {
                Directory.Delete(packagePath, recursive: true);
            }

            throw;
        }
    }

    private static IEnumerable<string> EnumerateEmbeddedContainers(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.ucs", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith("abx-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var include = false;
            try
            {
                var info = new FileInfo(file);
                include = info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                          info.Length > 0 && info.Length <= MaximumEmbeddedContainerBytes;
            }
            catch (IOException)
            {
                // 読み取り中に消えたコンテナは安全側に無視します。
            }
            catch (UnauthorizedAccessException)
            {
                // 権限のないコンテナは安全側に無視します。
            }

            if (include)
            {
                yield return file;
            }
        }
    }

    private static async Task<List<EmbeddedJpegSegment>> FindJpegSegmentsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() => FindJpegSegments(path, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// UCSを単なるFF D8/FF D9の組み合わせとして切り出さず、JPEGのマーカー構造と
    /// エントロピー領域を解析して、外側の画像を1レコードとして返します。Exif内の
    /// サムネイルSOIはAPPセグメントとして読み飛ばされるため、別画像として抽出しません。
    /// </summary>
    private static List<EmbeddedJpegSegment> FindJpegSegments(
        string path,
        CancellationToken cancellationToken)
    {
        var result = new List<EmbeddedJpegSegment>();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        var fileLength = stream.Length;
        var buffer = new byte[1024 * 1024];
        var scanPosition = 0L;
        while (scanPosition < fileLength && result.Count < MaximumEmbeddedSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var soi = FindNextJpegStart(stream, scanPosition, fileLength, buffer, cancellationToken);
            if (soi < 0)
            {
                break;
            }

            var segment = TryParseJpegSegment(stream, soi, fileLength) ??
                          TryFindFirstJpegEnd(stream, soi, fileLength, buffer, cancellationToken);
            if (segment is not null)
            {
                result.Add(segment);
                // 構造解析だけではWPFデコーダーの受入可否を確定できません。
                // 解析に失敗する候補が後続の有効画像を覆い隠さないよう、次のSOIから
                // 再走査します。検証後の重複整理で外側画像を優先します。
                scanPosition = checked(soi + 1);
            }
            else
            {
                scanPosition = checked(soi + 1);
            }
        }

        return result;
    }

    private static long FindNextJpegStart(
        FileStream stream,
        long startPosition,
        long fileLength,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        stream.Position = startPosition;
        var absolutePosition = startPosition;
        var previous = (byte)0;
        var hasPrevious = false;
        while (absolutePosition < fileLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, fileLength - absolutePosition));
            if (read == 0)
            {
                return -1;
            }

            for (var index = 0; index < read; index++)
            {
                var current = buffer[index];
                var position = absolutePosition + index;
                if (hasPrevious && previous == 0xFF && current == 0xD8)
                {
                    return position - 1;
                }

                previous = current;
                hasPrevious = true;
            }

            absolutePosition += read;
        }

        return -1;
    }

    private static EmbeddedJpegSegment? TryFindFirstJpegEnd(
        FileStream stream,
        long start,
        long fileLength,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        // FileMakerの一部レコードにはJPEGセグメント間に4バイト程度の独自パディングが
        // あります。構造解析に失敗した場合も、最初のEOIまでを候補に戻し、後段のWPF
        // デコードとSHA-256検証を通過したものだけを採用します。
        stream.Position = checked(start + 2);
        var scanPosition = stream.Position;
        var previous = (byte)0;
        var hasPrevious = false;
        while (scanPosition < fileLength && scanPosition - start <= MaximumImageBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, fileLength - scanPosition));
            if (read == 0)
            {
                return null;
            }

            for (var index = 0; index < read; index++)
            {
                var current = buffer[index];
                var position = scanPosition + index;
                if (hasPrevious && previous == 0xFF && current == 0xD9)
                {
                    return new EmbeddedJpegSegment(start, checked(position - start + 1));
                }

                previous = current;
                hasPrevious = true;
            }

            scanPosition += read;
        }

        return null;
    }

    private static EmbeddedJpegSegment? TryParseJpegSegment(
        FileStream stream,
        long start,
        long fileLength)
    {
        try
        {
            stream.Position = start;
            if (ReadByte(stream) != 0xFF || ReadByte(stream) != 0xD8)
            {
                return null;
            }

            var sawFrame = false;
            var sawScan = false;
            while (stream.Position < fileLength)
            {
                var marker = ReadJpegMarker(stream);
                if (marker == 0xD9)
                {
                    return sawFrame && sawScan
                        ? new EmbeddedJpegSegment(start, checked(stream.Position - start))
                        : null;
                }

                if (marker == 0xD8 || marker is >= 0xD0 and <= 0xD7 || marker == 0x01)
                {
                    return null;
                }

                var segmentLength = ReadJpegSegmentLength(stream, fileLength);
                if (IsJpegFrameMarker(marker))
                {
                    if (sawFrame || segmentLength < 8)
                    {
                        return null;
                    }

                    sawFrame = true;
                }

                stream.Position = checked(stream.Position + segmentLength - 2);
                if (marker != 0xDA)
                {
                    continue;
                }

                if (!sawFrame)
                {
                    return null;
                }

                sawScan = true;
                var entropyMarker = ReadJpegEntropyMarker(stream, fileLength);
                if (entropyMarker == 0xD9)
                {
                    return new EmbeddedJpegSegment(start, checked(stream.Position - start));
                }

                // DHT/COMなどのマーカーがスキャン間に現れるプログレッシブJPEGも、
                // 外側のマーカーループで続けて検証します。
                stream.Position = checked(stream.Position - 2);
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException or IOException or OverflowException)
        {
            // ランダムなFF D8は候補として無視し、次のSOIから再試行します。
        }

        return null;
    }

    private static int ReadJpegMarker(FileStream stream)
    {
        var paddingBytes = 0;
        var first = ReadByte(stream);
        while (first != 0xFF)
        {
            paddingBytes++;
            if (paddingBytes > MaximumJpegMarkerPaddingBytes)
            {
                throw new InvalidDataException("JPEGマーカーの開始バイトがありません。");
            }

            first = ReadByte(stream);
        }

        var marker = ReadByte(stream);
        while (marker == 0xFF)
        {
            marker = ReadByte(stream);
        }

        if (marker == 0x00)
        {
            throw new InvalidDataException("JPEGマーカーがエスケープされています。");
        }

        return marker;
    }

    private static int ReadJpegEntropyMarker(FileStream stream, long fileLength)
    {
        while (stream.Position < fileLength)
        {
            var value = ReadByte(stream);
            if (value != 0xFF)
            {
                continue;
            }

            var marker = ReadByte(stream);
            while (marker == 0xFF)
            {
                marker = ReadByte(stream);
            }

            if (marker == 0x00 || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            return marker;
        }

        throw new EndOfStreamException();
    }

    private static int ReadJpegSegmentLength(FileStream stream, long fileLength)
    {
        var high = ReadByte(stream);
        var low = ReadByte(stream);
        var length = (high << 8) | low;
        if (length < 2 || stream.Position + length - 2 > fileLength)
        {
            throw new InvalidDataException("JPEGセグメント長が不正です。");
        }

        return length;
    }

    private static int ReadByte(FileStream stream)
    {
        var value = stream.ReadByte();
        return value < 0 ? throw new EndOfStreamException() : value;
    }

    private static bool IsJpegFrameMarker(int marker) =>
        marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
            0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static async Task<ValidatedImage> ValidateEmbeddedImageAsync(
        string path,
        EmbeddedJpegSegment segment,
        CancellationToken cancellationToken)
    {
        if (segment.Length <= 0 || segment.Length > MaximumImageBytes || segment.Offset < 0)
        {
            throw new InvalidDataException("UCS内部JPEGのサイズが許容範囲外です。");
        }

        var bytes = new byte[checked((int)segment.Length)];
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            stream.Position = segment.Offset;
            await stream.ReadExactlyAsync(bytes.AsMemory(), cancellationToken);
        }

        DecodedImageContentValidator.EnsureNotFileMakerBlock(bytes, "UCS内部JPEG");

        using var imageStream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            imageStream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
        {
            throw new InvalidDataException("UCS内部JPEGを1枚としてデコードできません。");
        }

        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
            (long)frame.PixelWidth * frame.PixelHeight > MaximumPixels)
        {
            throw new InvalidDataException("UCS内部JPEGの画素数が許容範囲を超えています。");
        }

        DecodedImageContentValidator.EnsureHasVisualContent(frame, "UCS内部JPEG");

        return new ValidatedImage(
            segment.Length,
            frame.PixelWidth,
            frame.PixelHeight,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static async Task<List<VehicleMatch>> FindEmbeddedVehicleMatchesAsync(
        string path,
        long imageOffset,
        long previousImageEnd,
        IReadOnlyList<AbacusVehicleExportRow> rows,
        CancellationToken cancellationToken)
    {
        // 構造解析候補が重なる場合は、重なった候補をレコード境界にしません。
        // 直前の有効な非重複画像、または最大2MiBの直前領域からだけ照合します。
        var recordStart = previousImageEnd < imageOffset ? previousImageEnd : 0;
        var windowStart = Math.Max(recordStart, imageOffset - EmbeddedIdentifierWindowBytes);
        var windowLength = checked((int)(imageOffset - windowStart));
        if (windowLength == 0)
        {
            return [];
        }

        var bytes = new byte[windowLength];
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            stream.Position = windowStart;
            await stream.ReadExactlyAsync(bytes.AsMemory(), cancellationToken);
        }

        var text = AbacusEncoding.GetString(bytes);
        var normalizedText = Normalize(text);
        var matches = new List<VehicleMatch>();
        foreach (var row in rows)
        {
            var chassis = row.ChassisNumber.Trim();
            var registration = row.RegistrationNumber.Trim();
            var chassisHit = FindEmbeddedIdentifier(text, normalizedText, chassis);
            var registrationHit = FindEmbeddedIdentifier(text, normalizedText, registration);
            if (chassisHit < 0 && registrationHit < 0)
            {
                continue;
            }

            if (chassisHit >= 0 && IsStrongChassis(Normalize(chassis)))
            {
                matches.Add(new VehicleMatch(row, chassis, true, windowStart + chassisHit));
            }
            else if (registrationHit >= 0 && IsStrongIdentifier(Normalize(registration)))
            {
                // 登録番号だけの近傍一致は候補には残しますが、自動紐付けの強い根拠にはしません。
                matches.Add(new VehicleMatch(row, registration, false, windowStart + registrationHit));
            }
        }

        var uniqueMatches = matches
            .GroupBy(match => $"{match.Row.FileName}:{match.Row.RowNumber}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        if (uniqueMatches.Count <= 1)
        {
            return uniqueMatches;
        }

        // 直前の画像終端をレコード境界として扱い、画像に最も近い識別子だけを
        // 採用します。近接距離がほぼ同じ場合は、誤紐付けを避けるため候補を残します。
        var ordered = uniqueMatches
            .OrderBy(match => imageOffset - match.IdentifierOffset)
            .ToList();
        var nearestDistance = imageOffset - ordered[0].IdentifierOffset;
        var nearest = ordered
            .Where(match => imageOffset - match.IdentifierOffset - nearestDistance <= 64 * 1024)
            .ToList();
        return nearest;
    }

    private static int FindEmbeddedIdentifier(string text, string normalizedText, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return -1;
        }

        var direct = text.IndexOf(trimmed, StringComparison.Ordinal);
        if (direct >= 0)
        {
            return direct;
        }

        var normalized = Normalize(trimmed);
        return normalized.Length >= 4
            ? normalizedText.IndexOf(normalized, StringComparison.Ordinal)
            : -1;
    }

    private static IEnumerable<string> EnumerateImageFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> directories;
            IEnumerable<string> files;
            try
            {
                directories = Directory.EnumerateDirectories(current);
                files = Directory.EnumerateFiles(current);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                try
                {
                    if (!new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(directory);
                    }
                }
                catch (IOException)
                {
                    // 読めない枝は安全側に無視します。
                }
                catch (UnauthorizedAccessException)
                {
                    // 読めない枝は安全側に無視します。
                }
            }

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var include = false;
                try
                {
                    var info = new FileInfo(file);
                    include = info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
                }
                catch (IOException)
                {
                    // 読めないファイルは候補にしません。
                }
                catch (UnauthorizedAccessException)
                {
                    // 読めないファイルは候補にしません。
                }

                if (include)
                {
                    yield return file;
                }
            }
        }
    }

    private static async Task<ValidatedImage> ValidateImageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            info.Length <= 0 || info.Length > MaximumImageBytes)
        {
            throw new InvalidDataException($"画像サイズが許容範囲外です: {Path.GetFileName(path)}");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
        {
            throw new InvalidDataException($"画像を1枚としてデコードできません: {Path.GetFileName(path)}");
        }

        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
            (long)frame.PixelWidth * frame.PixelHeight > MaximumPixels)
        {
            throw new InvalidDataException($"画像の画素数が許容範囲を超えています: {Path.GetFileName(path)}");
        }

        stream.Position = 0;
        var imageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        var afterLength = new FileInfo(path).Length;
        if (afterLength != info.Length)
        {
            throw new InvalidDataException($"画像が読み取り中に変更されました: {Path.GetFileName(path)}");
        }

        return new ValidatedImage(info.Length, frame.PixelWidth, frame.PixelHeight, imageSha256);
    }

    private static List<VehicleMatch> FindVehicleMatches(
        string relativePath,
        IReadOnlyList<AbacusVehicleExportRow> rows)
    {
        var searchable = Normalize(relativePath);
        var matches = new List<VehicleMatch>();
        foreach (var row in rows)
        {
            var chassis = Normalize(row.ChassisNumber);
            var registration = Normalize(row.RegistrationNumber);
            var chassisHit = IsStrongChassis(chassis) && searchable.Contains(chassis, StringComparison.Ordinal);
            var registrationHit = IsStrongIdentifier(registration) && searchable.Contains(registration, StringComparison.Ordinal);
            if (!chassisHit && !registrationHit)
            {
                continue;
            }

            var identifier = chassisHit ? row.ChassisNumber.Trim() : row.RegistrationNumber.Trim();
            matches.Add(new VehicleMatch(row, identifier, chassisHit, -1));
        }

        return matches
            .GroupBy(match => $"{match.Row.FileName}:{match.Row.RowNumber}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static DeduplicationResult DeduplicatePackagedImages(
        List<AbacusBulkImageCandidate> candidates,
        IReadOnlyList<PreparedImage> packagedImages)
    {
        var containedIds = new HashSet<string>(StringComparer.Ordinal);
        var containedReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var image in packagedImages.Where(image => image.ContainerPath is not null))
        {
            var imageEnd = checked(image.SourceOffset + image.SourceLength);
            foreach (var other in packagedImages.Where(other =>
                other.ContainerPath is not null &&
                other.Candidate.CandidateId != image.Candidate.CandidateId &&
                string.Equals(other.ContainerPath, image.ContainerPath, StringComparison.OrdinalIgnoreCase) &&
                other.SourceLength > image.SourceLength &&
                other.SourceOffset <= image.SourceOffset &&
                checked(other.SourceOffset + other.SourceLength) >= imageEnd))
            {
                var imagePixels = (long)image.Candidate.PixelWidth * image.Candidate.PixelHeight;
                var otherPixels = (long)other.Candidate.PixelWidth * other.Candidate.PixelHeight;
                if (imagePixels > otherPixels)
                {
                    containedIds.Add(other.Candidate.CandidateId);
                    containedReasons[other.Candidate.CandidateId] =
                        "より大きいUCS内部JPEGに内包される低解像度候補として除外しました。";
                }
                else if (imagePixels > 0 && imagePixels * 4 <= otherPixels)
                {
                    containedIds.Add(image.Candidate.CandidateId);
                    containedReasons[image.Candidate.CandidateId] =
                        "別のUCS内部JPEGに内包される低解像度画像（Exifサムネイル候補）として除外しました。";
                }
            }
        }

        var uniqueImages = new List<PreparedImage>();
        var duplicateCount = containedIds.Count;
        foreach (var contained in packagedImages.Where(image => containedIds.Contains(image.Candidate.CandidateId)))
        {
            var containedCandidate = contained.Candidate with
            {
                PackageImageFileName = string.Empty,
                Status = "duplicate",
                StatusLabel = "重複除外（内包候補）",
                MatchStrategy = "embedded-contained-jpeg",
                Identifier = string.Empty,
                CustomerName = string.Empty,
                VehicleName = string.Empty,
                ChassisNumber = string.Empty,
                RegistrationNumber = string.Empty,
                Reason = containedReasons.TryGetValue(
                    contained.Candidate.CandidateId,
                    out var containedReason)
                    ? containedReason
                    : "別のUCS内部JPEGの範囲に完全に含まれるため、内包された重複画像として除外しました。"
            };
            ReplaceCandidate(candidates, contained.Candidate.CandidateId, containedCandidate);
        }

        foreach (var group in packagedImages
                     .Where(image => !containedIds.Contains(image.Candidate.CandidateId))
                     .Where(image => !string.IsNullOrWhiteSpace(image.Candidate.ImageSha256))
                     .GroupBy(image => image.Candidate.ImageSha256, StringComparer.OrdinalIgnoreCase))
        {
            var groupImages = group.ToList();
            var matchedImages = groupImages
                .Where(image => image.Candidate.Status == "matched")
                .ToList();
            var distinctChassis = matchedImages
                .Select(image => Normalize(image.Candidate.ChassisNumber))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var hasAssociationConflict = distinctChassis.Count > 1;
            var canonical = groupImages
                .OrderByDescending(image => GetDeduplicationPriority(image.Candidate.Status))
                .First();

            if (hasAssociationConflict)
            {
                var conflictCandidate = canonical.Candidate with
                {
                    Status = "review",
                    StatusLabel = "要確認（同一画像・複数車両）",
                    MatchStrategy = "sha256-conflict",
                    Identifier = string.Empty,
                    CustomerName = string.Empty,
                    VehicleName = string.Empty,
                    ChassisNumber = string.Empty,
                    RegistrationNumber = string.Empty,
                    Reason = "同じSHA-256の画像が複数車両のUCSレコードから検出されたため、自動登録せず1件にまとめました。"
                };
                ReplaceCandidate(candidates, canonical.Candidate.CandidateId, conflictCandidate);
                canonical = canonical with { Candidate = conflictCandidate };
            }

            uniqueImages.Add(canonical);
            foreach (var duplicate in groupImages)
            {
                if (duplicate.Candidate.CandidateId.Equals(canonical.Candidate.CandidateId, StringComparison.Ordinal))
                {
                    continue;
                }

                duplicateCount++;
                var duplicateCandidate = duplicate.Candidate with
                {
                    PackageImageFileName = string.Empty,
                    Status = "duplicate",
                    StatusLabel = "重複除外",
                    MatchStrategy = "sha256-duplicate",
                    Identifier = string.Empty,
                    CustomerName = string.Empty,
                    VehicleName = string.Empty,
                    ChassisNumber = string.Empty,
                    RegistrationNumber = string.Empty,
                    Reason = $"SHA-256が{canonical.Candidate.CandidateId}と一致するため、同一画像の重複として除外しました。"
                };
                ReplaceCandidate(candidates, duplicate.Candidate.CandidateId, duplicateCandidate);
            }
        }

        return new DeduplicationResult(uniqueImages, duplicateCount);
    }

    private static int GetDeduplicationPriority(string status) =>
        status switch
        {
            "matched" => 3,
            "review" => 2,
            "not-found" => 1,
            _ => 0,
        };

    private static void ReplaceCandidate(
        List<AbacusBulkImageCandidate> candidates,
        string candidateId,
        AbacusBulkImageCandidate replacement)
    {
        var index = candidates.FindIndex(candidate =>
            candidate.CandidateId.Equals(candidateId, StringComparison.Ordinal));
        if (index >= 0)
        {
            candidates[index] = replacement;
        }
    }

    private static bool IsStrongChassis(string normalized) =>
        normalized.Length >= 5 && normalized.Any(char.IsLetter);

    private static bool IsStrongIdentifier(string normalized) => normalized.Length >= 4;

    private static string Normalize(string value) =>
        string.Concat((value ?? string.Empty).Normalize(NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit))
        .ToUpperInvariant();

    private static Encoding CreateAbacusEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            932,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    private static string CreateCandidateId(string relativePath, string value) =>
        $"bulk-image-{Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes($"{relativePath}\n{value}"))).ToLowerInvariant()[..24]}";

    private static void AddContainerWarning(
        string sourceRoot,
        int sourceImageCount,
        int embeddedContainerCount,
        int embeddedImageCount,
        int embeddedRejectedCount,
        ICollection<string> warnings)
    {
        var containerFiles = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".ucs", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".fp5", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (containerFiles.Length > 0 && sourceImageCount == 0 && embeddedContainerCount == 0)
        {
            warnings.Add("ABACUSのUCS/FP5コンテナを確認しましたが、読み取り対象のabx-*.ucsが見つかりません。原本を推測分割・書換えせず、画像表示キャプチャまたは専用形式解析へ分離しています。");
        }
        else if (embeddedContainerCount > 0 && embeddedImageCount == 0 && embeddedRejectedCount == 0)
        {
            warnings.Add("abx-*.ucsコンテナを確認しましたが、検証可能なJPEG終端を見つけられませんでした。原本は変更していません。");
        }
    }

    private static string ValidateFolder(string folderPath, string label)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{label}が存在しないか、リンクまたは再解析ポイントです。");
        }

        return root;
    }

    private static void ValidateDestination(string sourceRoot, string vehicleRoot, string destinationRoot)
    {
        if (IsSameOrSubPath(destinationRoot, sourceRoot) || IsSameOrSubPath(destinationRoot, vehicleRoot))
        {
            throw new InvalidDataException("一括画像パッケージはABACUSフォルダーまたは車両一覧CSVフォルダーの外側へ保存してください。");
        }
    }

    private static string CreateUniquePackageDirectory(string destinationRoot)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"-{attempt}";
            var name = $"ABACUS-Bulk-Image-Preparation-{DateTime.Now:yyyyMMdd-HHmmss}{suffix}";
            var path = Path.Combine(destinationRoot, name);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                return path;
            }
        }

        throw new IOException("一括画像パッケージの保存先を作成できませんでした。");
    }

    private static async Task CopyAndVerifyAsync(
        string sourcePath,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("画像コピー先を確認できません。");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = destinationPath + ".partial";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"画像のSHA-256が一致しません: {Path.GetFileName(sourcePath)}");
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task CopySegmentAndVerifyAsync(
        string sourcePath,
        long sourceOffset,
        long sourceLength,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("画像コピー先を確認できません。");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = destinationPath + ".partial";
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.RandomAccess))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                source.Position = sourceOffset;
                var buffer = new byte[1024 * 1024];
                var remaining = sourceLength;
                while (remaining > 0)
                {
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                    if (read == 0)
                    {
                        throw new InvalidDataException("UCS内部JPEGの読み取り中に終端へ到達しました。");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    remaining -= read;
                }

                await destination.FlushAsync(cancellationToken);
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("UCS内部JPEGのSHA-256が一致しません。");
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".partial";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsSameOrSubPath(string candidatePath, string rootPath) =>
        candidatePath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
        candidatePath.StartsWith($"{rootPath}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private sealed record ValidatedImage(long FileSize, int PixelWidth, int PixelHeight, string ImageSha256);

    private sealed record VehicleMatch(
        AbacusVehicleExportRow Row,
        string Identifier,
        bool UsedChassis,
        long IdentifierOffset);

    private sealed record EmbeddedJpegSegment(long Offset, long Length);

    private sealed record PreparedImage(
        string? SourcePath,
        string? ContainerPath,
        long SourceOffset,
        long SourceLength,
        AbacusBulkImageCandidate Candidate);

    private sealed record DeduplicationResult(
        IReadOnlyList<PreparedImage> PackagedImages,
        int DuplicateCount);

    private sealed record BulkPreviewManifest(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        BulkPreviewSource Source,
        BulkPreviewSummary Summary,
        IReadOnlyList<BulkPreviewCandidate> Candidates);

    private sealed record BulkPreviewSource(string SourceFolder, string VehicleExportFolder, string Strategy);

    private sealed record BulkPreviewSummary(
        int ImageCount,
        int ReviewCount,
        int NotFoundCount,
        int RejectedCount,
        string Note);

    private sealed record BulkPreviewCandidate(
        string CandidateId,
        string PackageImageFileName,
        string ImageSha256,
        string CustomerName,
        string VehicleName,
        string ChassisNumber,
        string RegistrationNumber);

    private sealed record BulkReport(
        int Version,
        string Kind,
        string Status,
        DateTime CreatedAtUtc,
        string SourceFolder,
        string VehicleExportFolder,
        IReadOnlyList<AbacusBulkImageCandidate> Candidates);
}
