using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusFp5VehicleImageMapping(
    int Index,
    string RecordIdHex,
    string? ImageIdHex,
    string? ImageRelativePath,
    string? ImageSha256,
    string? VehicleFileName,
    int? VehicleRowNumber,
    string Status,
    string Evidence,
    [property: JsonIgnore] string? CustomerName,
    [property: JsonIgnore] string? VehicleName,
    [property: JsonIgnore] string? RegistrationNumber,
    [property: JsonIgnore] string? ChassisNumber);

public sealed record AbacusFp5VehicleImageMappingResult(
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
    IReadOnlyList<AbacusFp5VehicleImageMapping> Mappings)
{
    public int OutOfScopeRecordCount =>
        Mappings.Count(mapping => mapping.Status == "out-of-scope");

    public int InScopeVehicleRecordCount =>
        InternalVehicleRecordCount - OutOfScopeRecordCount;

    public bool IsValid =>
        InternalVehicleRecordCount > 0 &&
        InternalVehicleRecordCount == Mappings.Count &&
        MatchedImageCount + NoImageCount + ReviewCount + UnmatchedCount + MultipleCandidateCount +
            UnknownImageReferenceCount + OutOfScopeRecordCount == InternalVehicleRecordCount &&
        DuplicateImageReferenceCount == 0 &&
        DuplicateImageSha256Count == 0 &&
        UnreferencedImageCount == 0;

    public bool IsFullyMatched =>
        IsValid &&
        MatchedImageCount + NoImageCount == InScopeVehicleRecordCount &&
        ReviewCount == 0 &&
        UnmatchedCount == 0 &&
        MultipleCandidateCount == 0 &&
        UnknownImageReferenceCount == 0;
}

/// <summary>
/// FP5の同一車両レコードに格納された登録番号(field 0x10)、画像参照(field 0x25)、
/// 車台番号(field 0x37)を読み、1F/05/&lt;image id&gt;/JPEGと車両CSVを対応付けます。
/// 行順や画像ノードの近接関係は対応付け根拠に使用しません。
/// </summary>
public sealed class AbacusFp5VehicleImageMapper
{
    private const string Matched = "matched";
    private const string NoImage = "no-image";
    private const string Review = "review-registration-only";
    private const string Unmatched = "unmatched-vehicle";
    private const string Multiple = "multiple-vehicle-candidates";
    private const string UnknownImage = "unknown-image-reference";
    private const string OutOfScope = "out-of-scope";

    public async Task<AbacusFp5VehicleImageMappingResult> MapAsync(
        string sourceFilePath,
        string vehicleExportFolder,
        string outputParentFolder,
        bool allowPartialScope = false,
        CancellationToken cancellationToken = default)
    {
        var vehicleExport = await new AbacusVehicleExportReader().ReadAsync(
            vehicleExportFolder,
            cancellationToken);
        if (!vehicleExport.IsValid)
        {
            throw new InvalidDataException("車両CSVに解析エラーがあるため画像を対応付けできません。");
        }

        var source = await new AbacusFp5ImageRestorer().RestoreForVehicleMappingAsync(
            sourceFilePath,
            outputParentFolder,
            cancellationToken);
        if (source.VehicleRecords.Count == 0)
        {
            throw new InvalidDataException("FP5に画像参照を持つ車両レコードがありません。");
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var strictShiftJis = Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        ValidateFieldContract(source, strictShiftJis);
        var rowsByPair = vehicleExport.Rows
            .GroupBy(row => (row.RegistrationNumber, row.ChassisNumber))
            .ToDictionary(group => group.Key, group => group.ToList());
        var rowsByRegistration = vehicleExport.Rows
            .GroupBy(row => row.RegistrationNumber)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var rowsByChassis = vehicleExport.Rows
            .GroupBy(row => row.ChassisNumber)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var imagesById = source.Restoration.Images.ToDictionary(
            image => image.ImageIdHex,
            StringComparer.Ordinal);
        var imageReferenceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var mappings = new List<AbacusFp5VehicleImageMapping>(source.VehicleRecords.Count);

        foreach (var record in source.VehicleRecords.OrderBy(record => record.RecordIdHex, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string registrationNumber;
            string chassisNumber;
            try
            {
                registrationNumber = strictShiftJis.GetString(record.RegistrationNumberBytes);
                chassisNumber = strictShiftJis.GetString(record.ChassisNumberBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"FP5車両レコード{record.RecordIdHex}の識別子をShift-JISとして解釈できません。",
                    exception);
            }

            var imageIdHex = DecodeImageReference(record);
            imagesById.TryGetValue(imageIdHex, out var image);
            var isGifPlaceholder = source.GifImageIds.Contains(imageIdHex);
            if (image is not null)
            {
                imageReferenceCounts.TryGetValue(imageIdHex, out var count);
                imageReferenceCounts[imageIdHex] = count + 1;
            }

            rowsByPair.TryGetValue((registrationNumber, chassisNumber), out var pairCandidates);
            pairCandidates ??= [];
            rowsByRegistration.TryGetValue(registrationNumber, out var registrationCandidates);
            registrationCandidates ??= [];
            rowsByChassis.TryGetValue(chassisNumber, out var chassisCandidates);
            chassisCandidates ??= [];
            var inScope = pairCandidates.Count > 0 || registrationCandidates.Count > 0 || chassisCandidates.Count > 0;

            if (allowPartialScope && !inScope)
            {
                mappings.Add(new AbacusFp5VehicleImageMapping(
                    mappings.Count + 1,
                    record.RecordIdHex,
                    imageIdHex,
                    image?.RelativePath,
                    image?.Sha256,
                    null,
                    null,
                    OutOfScope,
                    "vehicle-not-present-in-selected-customer-subset",
                    null,
                    null,
                    null,
                    null));
                continue;
            }

            var status = UnknownImage;
            var evidence = "internal-image-reference-not-found";
            AbacusVehicleExportRow? vehicle = null;
            if (image is null && !isGifPlaceholder)
            {
                // Unknown references must never be downgraded to a registration-only review.
            }
            else if (image is not null && pairCandidates.Count == 1)
            {
                status = Matched;
                evidence = "fp5-record-image-reference+exact-chassis-registration";
                vehicle = pairCandidates[0];
            }
            else if (isGifPlaceholder && pairCandidates.Count == 1)
            {
                status = NoImage;
                evidence = "fp5-record-gif-placeholder+exact-chassis-registration";
                vehicle = pairCandidates[0];
            }
            else if (pairCandidates.Count > 1)
            {
                status = Multiple;
                evidence = "exact-chassis-registration-not-unique";
            }
            else if (registrationCandidates.Count == 1)
            {
                status = Review;
                evidence = "registration-only-unique-review-required";
                vehicle = registrationCandidates[0];
            }
            else if (image is not null || isGifPlaceholder)
            {
                status = Unmatched;
                evidence = "no-exact-chassis-registration-row";
            }

            mappings.Add(new AbacusFp5VehicleImageMapping(
                mappings.Count + 1,
                record.RecordIdHex,
                imageIdHex,
                image?.RelativePath,
                image?.Sha256,
                vehicle?.FileName,
                vehicle?.RowNumber,
                status,
                evidence,
                vehicle?.CustomerName,
                vehicle?.VehicleName,
                vehicle?.RegistrationNumber,
                vehicle?.ChassisNumber));
        }

        var duplicateReferenceCount = imageReferenceCounts.Values.Count(count => count > 1);
        var referencedImageIds = imageReferenceCounts.Keys.ToHashSet(StringComparer.Ordinal);
        var unreferencedImageCount = imagesById.Keys.Count(imageId => !referencedImageIds.Contains(imageId));
        var duplicateSha256Count = source.Restoration.Images
            .GroupBy(image => image.Sha256, StringComparer.Ordinal)
            .Count(group => group.Count() > 1);
        var result = new AbacusFp5VehicleImageMappingResult(
            source.Restoration.OutputFolderPath,
            Path.Combine(source.Restoration.OutputFolderPath, "fp5-vehicle-image-mapping-report.json"),
            source.Restoration.SourceFilePath,
            source.VehicleRecords.Count,
            vehicleExport.Rows.Count,
            source.Restoration.Images.Count,
            source.GifImageIds.Count,
            mappings.Count(mapping => mapping.Status == Matched),
            mappings.Count(mapping => mapping.Status == NoImage),
            mappings.Count(mapping => mapping.Status == Review),
            mappings.Count(mapping => mapping.Status == Unmatched),
            mappings.Count(mapping => mapping.Status == Multiple),
            mappings.Count(mapping => mapping.Status == UnknownImage),
            duplicateReferenceCount,
            duplicateSha256Count,
            unreferencedImageCount,
            mappings);
        WriteReport(result, source.Restoration, vehicleExport);
        return result;
    }

    private static string DecodeImageReference(AbacusFp5InternalVehicleRecord record)
    {
        var reference = record.ImageReferenceBytes;
        if (reference.Length < 2 || reference[0] != reference.Length - 1 || reference[0] > 5)
        {
            throw new InvalidDataException(
                $"FP5車両レコード{record.RecordIdHex}の画像参照形式が不正です。");
        }

        return Convert.ToHexString(reference.AsSpan(1));
    }

    private static void ValidateFieldContract(
        AbacusFp5VehicleImageSource source,
        Encoding strictShiftJis)
    {
        var expectedNames = new Dictionary<byte, string>
        {
            [0x10] = "登録番号",
            [0x25] = "車検証画像",
            [0x37] = "車体番号",
        };
        foreach (var expected in expectedNames)
        {
            if (!source.VehicleFieldNames.TryGetValue(expected.Key, out var nameBytes))
            {
                throw new InvalidDataException($"FP5 field 0x{expected.Key:X2}の定義がありません。");
            }

            string actualName;
            try
            {
                actualName = strictShiftJis.GetString(nameBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"FP5 field 0x{expected.Key:X2}の名前をShift-JISとして解釈できません。",
                    exception);
            }

            if (!actualName.Equals(expected.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"FP5 field 0x{expected.Key:X2}がABACUS車検証データの定義と一致しません。");
            }
        }
    }

    private static void WriteReport(
        AbacusFp5VehicleImageMappingResult result,
        AbacusFp5ImageRestoration restoration,
        AbacusVehicleExportReadResult vehicleExport)
    {
        var report = new
        {
            formatVersion = 1,
            status = result.IsFullyMatched
                ? result.OutOfScopeRecordCount > 0 ? "gate14-partial-verified" : "gate14-verified"
                : "gate14-review-required",
            source = new
            {
                fileName = Path.GetFileName(restoration.SourceFilePath),
                fileSize = restoration.SourceFileSize,
                sha256 = restoration.SourceSha256,
                vehicleCsv = vehicleExport.Files.Select(file => new
                {
                    file.FileName,
                    file.TotalRows,
                    file.ValidRows,
                    file.Sha256,
                }),
            },
            contract = new
            {
                registrationField = "0x10",
                imageReferenceField = "0x25",
                chassisField = "0x37",
                jpegPath = "1F/05/<image-id>/JPEG",
                placeholderPath = "1F/05/<image-id>/GIFf",
                automaticMatch = "same FP5 record image reference + exact chassis and registration pair",
                prohibited = "row-order matching",
            },
            verification = new
            {
                result.InternalVehicleRecordCount,
                result.VehicleCsvRowCount,
                result.JpegImageCount,
                result.GifPlaceholderCount,
                result.MatchedImageCount,
                result.NoImageCount,
                result.ReviewCount,
                result.UnmatchedCount,
                result.MultipleCandidateCount,
                result.UnknownImageReferenceCount,
                result.DuplicateImageReferenceCount,
                result.DuplicateImageSha256Count,
                result.UnreferencedImageCount,
                result.InScopeVehicleRecordCount,
                result.OutOfScopeRecordCount,
                result.IsValid,
                result.IsFullyMatched,
            },
            mappings = result.Mappings,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = result.ReportPath + ".tmp";
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, result.ReportPath);
    }
}
