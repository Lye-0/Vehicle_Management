using System.Security.Cryptography;
using System.Text;

namespace VehicleManagement.AbacusImport;

public static class AbacusRecommendationEntityKinds
{
    public const string Document = "document";
    public const string Vehicle = "vehicle";
    public const string Customer = "customer";
}

public static class AbacusRecommendationDecisionValues
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Hold = "hold";

    public static bool IsSupported(string value) => value is Pending or Approved or Rejected or Hold;
}

/// <summary>
/// おすすめ判定へ渡す値です。空欄は「比較できない」ことを表し、異なる値とは扱いません。
/// </summary>
public sealed record AbacusRecommendationProfile(
    string CustomerName = "",
    string NameKana = "",
    string PhoneNumber = "",
    string PostalCode = "",
    string Address = "",
    string Maker = "",
    string VehicleName = "",
    string Model = "",
    string RegistrationNumber = "",
    string ChassisNumber = "");

public sealed record AbacusRecommendationDocument(
    string DocumentId,
    AbacusRecommendationProfile Profile);

public sealed record AbacusRecommendationVehicle(
    string VehicleId,
    string CustomerId,
    AbacusRecommendationProfile Profile);

public sealed record AbacusRecommendationCustomer(
    string CustomerId,
    AbacusRecommendationProfile Profile);

public sealed record AbacusRecommendationInput(
    IReadOnlyList<AbacusRecommendationDocument> Documents,
    IReadOnlyList<AbacusRecommendationVehicle> Vehicles,
    IReadOnlyList<AbacusRecommendationCustomer> Customers);

public sealed record AbacusRecommendationEvidence(
    string Field,
    string Label,
    string SourceValue,
    string CandidateValue,
    string MatchKind,
    bool IsStrong);

public sealed record AbacusRecommendationDifference(
    string Field,
    string Label,
    string SourceValue,
    string CandidateValue);

public sealed record AbacusRecommendationMissingField(
    string Field,
    string Label,
    string SourceValue,
    string CandidateValue,
    string MissingSide);

public sealed record AbacusRecommendationCandidate(
    string CandidateId,
    string SubjectKind,
    string SubjectId,
    string TargetKind,
    string TargetId,
    string TargetCustomerId,
    IReadOnlyList<AbacusRecommendationEvidence> MatchedFields,
    IReadOnlyList<AbacusRecommendationDifference> Differences,
    IReadOnlyList<AbacusRecommendationMissingField> MissingFields,
    IReadOnlyList<string> Conflicts,
    string Reason)
{
    public bool HasStrongEvidence => MatchedFields.Any(evidence => evidence.IsStrong);

    public bool IsEligible => MatchedFields.Count > 0 && Conflicts.Count == 0;
}

/// <summary>
/// ABACUS候補のおすすめを、入力順に依存しない順序で計算します。
/// このサービスはリンクを変更せず、候補と根拠だけを返します。
/// </summary>
public sealed class AbacusRecommendationEngine
{
    private const int ShortFuzzyDistance = 1;
    private const int LongFuzzyDistance = 2;

    public IReadOnlyList<AbacusRecommendationCandidate> Build(AbacusRecommendationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var documents = input.Documents
            .Where(document => !string.IsNullOrWhiteSpace(document.DocumentId))
            .OrderBy(document => document.DocumentId, StringComparer.Ordinal)
            .ToArray();
        var vehicles = input.Vehicles
            .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.VehicleId))
            .OrderBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .ToArray();
        var customers = input.Customers
            .Where(customer => !string.IsNullOrWhiteSpace(customer.CustomerId))
            .OrderBy(customer => customer.CustomerId, StringComparer.Ordinal)
            .ToArray();

        var candidates = new List<AbacusRecommendationCandidate>();
        foreach (var document in documents)
        {
            foreach (var vehicle in vehicles.Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.CustomerId)))
            {
                var targetCustomer = customers.FirstOrDefault(customer =>
                    string.Equals(customer.CustomerId, vehicle.CustomerId, StringComparison.Ordinal));
                if (targetCustomer is null)
                {
                    continue;
                }

                var targetProfile = MergeVehicleAndCustomerProfile(vehicle.Profile, targetCustomer.Profile);
                var evaluation = EvaluateProfiles(
                    document.Profile,
                    targetProfile,
                    includeVehicleFields: true);
                AddCustomerConflicts(
                    evaluation,
                    document.Profile,
                    targetCustomer.CustomerId,
                    customers);
                AddVehicleIdentifierConflicts(
                    evaluation,
                    document.Profile,
                    vehicle.VehicleId,
                    vehicles);

                AddCandidate(
                    candidates,
                    AbacusRecommendationEntityKinds.Document,
                    document.DocumentId,
                    AbacusRecommendationEntityKinds.Vehicle,
                    vehicle.VehicleId,
                    vehicle.CustomerId,
                    evaluation);
            }

            foreach (var customer in customers)
            {
                var evaluation = EvaluateProfiles(
                    document.Profile,
                    customer.Profile,
                    includeVehicleFields: false);
                AddCustomerConflicts(
                    evaluation,
                    document.Profile,
                    customer.CustomerId,
                    customers);
                AddVehicleOwnerConflicts(
                    evaluation,
                    document.Profile,
                    customer.CustomerId,
                    vehicles);

                AddCandidate(
                    candidates,
                    AbacusRecommendationEntityKinds.Document,
                    document.DocumentId,
                    AbacusRecommendationEntityKinds.Customer,
                    customer.CustomerId,
                    customer.CustomerId,
                    evaluation);
            }
        }

        foreach (var vehicle in vehicles.Where(vehicle => string.IsNullOrWhiteSpace(vehicle.CustomerId)))
        {
            foreach (var customer in customers)
            {
                var evaluation = EvaluateProfiles(
                    vehicle.Profile,
                    customer.Profile,
                    includeVehicleFields: false);
                AddCustomerConflicts(
                    evaluation,
                    vehicle.Profile,
                    customer.CustomerId,
                    customers);

                AddCandidate(
                    candidates,
                    AbacusRecommendationEntityKinds.Vehicle,
                    vehicle.VehicleId,
                    AbacusRecommendationEntityKinds.Customer,
                    customer.CustomerId,
                    customer.CustomerId,
                    evaluation);
            }
        }

        // 顧客同士の候補は、完全一致の同名グループとは別に、
        // 電話・住所・ふりがななどの一部一致をおすすめとして返します。
        // 顧客名が完全一致する組み合わせは、補助アプリ側の既存の強い統合候補へ
        // 委ね、同じ組み合わせを二重表示しません。
        for (var sourceIndex = 0; sourceIndex < customers.Length; sourceIndex++)
        {
            for (var targetIndex = sourceIndex + 1; targetIndex < customers.Length; targetIndex++)
            {
                var source = customers[sourceIndex];
                var target = customers[targetIndex];
                var sourceName = NormalizeText(source.Profile.CustomerName);
                var targetName = NormalizeText(target.Profile.CustomerName);
                if (sourceName.Length > 0 &&
                    string.Equals(sourceName, targetName, StringComparison.Ordinal))
                {
                    continue;
                }

                var evaluation = EvaluateProfiles(
                    source.Profile,
                    target.Profile,
                    includeVehicleFields: false);
                AddCandidate(
                    candidates,
                    AbacusRecommendationEntityKinds.Customer,
                    source.CustomerId,
                    AbacusRecommendationEntityKinds.Customer,
                    target.CustomerId,
                    target.CustomerId,
                    evaluation);
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.IsEligible)
            .ThenByDescending(candidate => candidate.HasStrongEvidence)
            .ThenByDescending(candidate => candidate.MatchedFields.Count)
            .ThenBy(candidate => candidate.SubjectKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SubjectId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddCandidate(
        ICollection<AbacusRecommendationCandidate> candidates,
        string subjectKind,
        string subjectId,
        string targetKind,
        string targetId,
        string targetCustomerId,
        Evaluation evaluation)
    {
        if (evaluation.Matches.Count == 0)
        {
            return;
        }

        candidates.Add(new AbacusRecommendationCandidate(
            BuildCandidateId(subjectKind, subjectId, targetKind, targetId),
            subjectKind,
            subjectId,
            targetKind,
            targetId,
            targetCustomerId,
            evaluation.Matches.ToArray(),
            evaluation.Differences.ToArray(),
            evaluation.MissingFields.ToArray(),
            evaluation.Conflicts.Distinct(StringComparer.Ordinal).ToArray(),
            BuildReason(evaluation)));
    }

    private static Evaluation EvaluateProfiles(
        AbacusRecommendationProfile source,
        AbacusRecommendationProfile candidate,
        bool includeVehicleFields)
    {
        var evaluation = new Evaluation();
        CompareText(evaluation, "chassisNumber", "車台番号", source.ChassisNumber, candidate.ChassisNumber, strong: true, identifier: true, allowFuzzy: false);
        CompareText(evaluation, "registrationNumber", "登録番号", source.RegistrationNumber, candidate.RegistrationNumber, strong: true, identifier: true, allowFuzzy: false);
        CompareText(evaluation, "phoneNumber", "電話番号", source.PhoneNumber, candidate.PhoneNumber, strong: true, identifier: true, allowFuzzy: false);
        CompareText(evaluation, "address", "住所", source.Address, candidate.Address, strong: true, identifier: false, allowFuzzy: true);
        CompareText(evaluation, "postalCode", "郵便番号", source.PostalCode, candidate.PostalCode, strong: false, identifier: true, allowFuzzy: false);
        CompareText(evaluation, "customerName", "顧客名", source.CustomerName, candidate.CustomerName, strong: false, identifier: false, allowFuzzy: true);
        CompareText(evaluation, "nameKana", "ふりがな", source.NameKana, candidate.NameKana, strong: false, identifier: false, allowFuzzy: true);
        if (includeVehicleFields)
        {
            CompareText(evaluation, "maker", "メーカー", source.Maker, candidate.Maker, strong: false, identifier: false, allowFuzzy: false);
            CompareText(evaluation, "vehicleName", "車名", source.VehicleName, candidate.VehicleName, strong: false, identifier: false, allowFuzzy: false);
            CompareText(evaluation, "model", "型式", source.Model, candidate.Model, strong: false, identifier: false, allowFuzzy: false);
        }

        return evaluation;
    }

    private static void CompareText(
        Evaluation evaluation,
        string field,
        string label,
        string sourceValue,
        string candidateValue,
        bool strong,
        bool identifier,
        bool allowFuzzy)
    {
        if (string.IsNullOrWhiteSpace(sourceValue) || string.IsNullOrWhiteSpace(candidateValue))
        {
            if (!string.IsNullOrWhiteSpace(sourceValue) || !string.IsNullOrWhiteSpace(candidateValue))
            {
                evaluation.MissingFields.Add(new AbacusRecommendationMissingField(
                    field,
                    label,
                    sourceValue.Trim(),
                    candidateValue.Trim(),
                    string.IsNullOrWhiteSpace(sourceValue) ? "source" : "candidate"));
            }
            return;
        }

        var sourceKey = identifier ? NormalizeIdentifier(sourceValue) : NormalizeText(sourceValue);
        var candidateKey = identifier ? NormalizeIdentifier(candidateValue) : NormalizeText(candidateValue);
        if (sourceKey.Length == 0 || candidateKey.Length == 0)
        {
            evaluation.MissingFields.Add(new AbacusRecommendationMissingField(
                field,
                label,
                sourceValue.Trim(),
                candidateValue.Trim(),
                sourceKey.Length == 0 ? "source" : "candidate"));
            return;
        }

        if (string.Equals(sourceKey, candidateKey, StringComparison.Ordinal))
        {
            evaluation.Matches.Add(new AbacusRecommendationEvidence(
                field,
                label,
                sourceValue.Trim(),
                candidateValue.Trim(),
                "exact",
                strong));
            return;
        }

        if (allowFuzzy && IsLightFuzzyMatch(sourceKey, candidateKey))
        {
            evaluation.Matches.Add(new AbacusRecommendationEvidence(
                field,
                label,
                sourceValue.Trim(),
                candidateValue.Trim(),
                "fuzzy",
                false));
            return;
        }

        evaluation.Differences.Add(new AbacusRecommendationDifference(
            field,
            label,
            sourceValue.Trim(),
            candidateValue.Trim()));
        if (identifier)
        {
            evaluation.Conflicts.Add($"{label}が一致しません。");
        }
    }

    private static void AddCustomerConflicts(
        Evaluation evaluation,
        AbacusRecommendationProfile source,
        string targetCustomerId,
        IReadOnlyList<AbacusRecommendationCustomer> customers)
    {
        AddOwnerConflict(
            evaluation,
            "顧客名",
            source.CustomerName,
            customers,
            customer => customer.Profile.CustomerName,
            targetCustomerId,
            fuzzy: true);
        AddOwnerConflict(
            evaluation,
            "ふりがな",
            source.NameKana,
            customers,
            customer => customer.Profile.NameKana,
            targetCustomerId,
            fuzzy: true);
        AddOwnerConflict(
            evaluation,
            "電話番号",
            source.PhoneNumber,
            customers,
            customer => customer.Profile.PhoneNumber,
            targetCustomerId,
            fuzzy: false);
        AddOwnerConflict(
            evaluation,
            "住所",
            source.Address,
            customers,
            customer => customer.Profile.Address,
            targetCustomerId,
            fuzzy: false);
    }

    private static void AddOwnerConflict(
        Evaluation evaluation,
        string label,
        string sourceValue,
        IEnumerable<AbacusRecommendationCustomer> customers,
        Func<AbacusRecommendationCustomer, string> valueSelector,
        string targetCustomerId,
        bool fuzzy)
    {
        if (string.IsNullOrWhiteSpace(sourceValue))
        {
            return;
        }

        var sourceKey = fuzzy ? NormalizeText(sourceValue) : NormalizeIdentifier(sourceValue);
        if (sourceKey.Length == 0)
        {
            return;
        }

        var matches = customers
            .Where(customer =>
            {
                var candidateValue = valueSelector(customer);
                if (string.IsNullOrWhiteSpace(candidateValue)) return false;
                var candidateKey = fuzzy ? NormalizeText(candidateValue) : NormalizeIdentifier(candidateValue);
                return string.Equals(sourceKey, candidateKey, StringComparison.Ordinal) ||
                       (fuzzy && IsLightFuzzyMatch(sourceKey, candidateKey));
            })
            .Select(customer => customer.CustomerId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length > 1)
        {
            evaluation.Conflicts.Add($"{label}が複数の顧客に一致します。");
        }
        else if (matches.Length == 1 && !string.Equals(matches[0], targetCustomerId, StringComparison.Ordinal))
        {
            evaluation.Conflicts.Add($"{label}が別の顧客に一致します。");
        }
    }

    private static void AddVehicleIdentifierConflicts(
        Evaluation evaluation,
        AbacusRecommendationProfile source,
        string targetVehicleId,
        IReadOnlyList<AbacusRecommendationVehicle> vehicles)
    {
        AddVehicleIdentifierConflict(evaluation, "車台番号", source.ChassisNumber, targetVehicleId, vehicles, vehicle => vehicle.Profile.ChassisNumber);
        AddVehicleIdentifierConflict(evaluation, "登録番号", source.RegistrationNumber, targetVehicleId, vehicles, vehicle => vehicle.Profile.RegistrationNumber);

        var matchedVehicleIds = new[]
            {
                FindVehicleIds(source.ChassisNumber, vehicles, vehicle => vehicle.Profile.ChassisNumber),
                FindVehicleIds(source.RegistrationNumber, vehicles, vehicle => vehicle.Profile.RegistrationNumber),
            }
            .SelectMany(ids => ids)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matchedVehicleIds.Length > 1)
        {
            evaluation.Conflicts.Add("車台番号と登録番号が異なる車両に一致します。");
        }
    }

    private static void AddVehicleIdentifierConflict(
        Evaluation evaluation,
        string label,
        string sourceValue,
        string targetVehicleId,
        IEnumerable<AbacusRecommendationVehicle> vehicles,
        Func<AbacusRecommendationVehicle, string> valueSelector)
    {
        var matches = FindVehicleIds(sourceValue, vehicles, valueSelector);
        if (matches.Length > 1)
        {
            evaluation.Conflicts.Add($"{label}が複数の車両に完全一致します。");
        }
        else if (matches.Length == 1 && !string.Equals(matches[0], targetVehicleId, StringComparison.Ordinal))
        {
            evaluation.Conflicts.Add($"{label}が別の車両に完全一致します。");
        }
    }

    private static void AddVehicleOwnerConflicts(
        Evaluation evaluation,
        AbacusRecommendationProfile source,
        string targetCustomerId,
        IEnumerable<AbacusRecommendationVehicle> vehicles)
    {
        AddVehicleOwnerConflict(evaluation, "車台番号", source.ChassisNumber, targetCustomerId, vehicles, vehicle => vehicle.Profile.ChassisNumber);
        AddVehicleOwnerConflict(evaluation, "登録番号", source.RegistrationNumber, targetCustomerId, vehicles, vehicle => vehicle.Profile.RegistrationNumber);
    }

    private static void AddVehicleOwnerConflict(
        Evaluation evaluation,
        string label,
        string sourceValue,
        string targetCustomerId,
        IEnumerable<AbacusRecommendationVehicle> vehicles,
        Func<AbacusRecommendationVehicle, string> valueSelector)
    {
        if (string.IsNullOrWhiteSpace(sourceValue))
        {
            return;
        }

        var matches = vehicles
            .Where(vehicle => !string.IsNullOrWhiteSpace(vehicle.CustomerId))
            .Where(vehicle => string.Equals(
                NormalizeIdentifier(sourceValue),
                NormalizeIdentifier(valueSelector(vehicle)),
                StringComparison.Ordinal))
            .Select(vehicle => vehicle.CustomerId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matches.Length > 1 || (matches.Length == 1 && !string.Equals(matches[0], targetCustomerId, StringComparison.Ordinal)))
        {
            evaluation.Conflicts.Add($"{label}が別顧客の車両に一致します。");
        }
    }

    private static string[] FindVehicleIds(
        string sourceValue,
        IEnumerable<AbacusRecommendationVehicle> vehicles,
        Func<AbacusRecommendationVehicle, string> valueSelector)
    {
        var sourceKey = NormalizeIdentifier(sourceValue);
        return sourceKey.Length == 0
            ? []
            : vehicles
                .Where(vehicle => string.Equals(sourceKey, NormalizeIdentifier(valueSelector(vehicle)), StringComparison.Ordinal))
                .Select(vehicle => vehicle.VehicleId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
    }

    private static AbacusRecommendationProfile MergeVehicleAndCustomerProfile(
        AbacusRecommendationProfile vehicle,
        AbacusRecommendationProfile customer) =>
        vehicle with
        {
            CustomerName = FirstNonEmpty(customer.CustomerName, vehicle.CustomerName),
            NameKana = FirstNonEmpty(customer.NameKana, vehicle.NameKana),
            PhoneNumber = FirstNonEmpty(customer.PhoneNumber, vehicle.PhoneNumber),
            PostalCode = FirstNonEmpty(customer.PostalCode, vehicle.PostalCode),
            Address = FirstNonEmpty(customer.Address, vehicle.Address),
        };

    private static string BuildCandidateId(
        string subjectKind,
        string subjectId,
        string targetKind,
        string targetId)
    {
        var input = string.Join("|", subjectKind, subjectId, targetKind, targetId);
        return "gate28-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private static string BuildReason(Evaluation evaluation)
    {
        var matches = evaluation.Matches
            .Select(match => match.MatchKind == "fuzzy"
                ? $"{match.Label}（軽微な誤字・表記揺れを許容）"
                : $"{match.Label}完全一致")
            .ToArray();
        var reason = matches.Length == 0
            ? "肯定的な一致根拠はありません。"
            : $"一致根拠: {string.Join("、", matches)}。";
        if (evaluation.Differences.Count > 0)
        {
            reason += $" 差異: {string.Join("、", evaluation.Differences.Select(difference => difference.Label))}。";
        }

        if (evaluation.MissingFields.Count > 0)
        {
            reason += $" 不足情報: {string.Join("、", evaluation.MissingFields.Select(field => field.Label))}。";
        }

        if (evaluation.Conflicts.Count > 0)
        {
            reason += $" 競合: {string.Join("、", evaluation.Conflicts.Distinct(StringComparer.Ordinal))} 承認前に確認が必要です。";
        }
        else
        {
            reason += "自動確定はせず、ユーザー承認待ちのおすすめです。";
        }

        return reason;
    }

    private static bool IsLightFuzzyMatch(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0 || left == right)
        {
            return false;
        }

        var maximumDistance = Math.Min(left.Length, right.Length) <= 8
            ? ShortFuzzyDistance
            : LongFuzzyDistance;
        if (Math.Abs(left.Length - right.Length) > maximumDistance)
        {
            return false;
        }

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var row = 1; row <= left.Length; row++)
        {
            var current = new int[right.Length + 1];
            current[0] = row;
            var rowMinimum = current[0];
            for (var column = 1; column <= right.Length; column++)
            {
                current[column] = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
                rowMinimum = Math.Min(rowMinimum, current[column]);
            }

            if (rowMinimum > maximumDistance)
            {
                return false;
            }

            previous = current;
        }

        return previous[^1] <= maximumDistance;
    }

    private static string NormalizeText(string value) =>
        value.Normalize(NormalizationForm.FormKC)
            .Trim()
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("　", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace("－", "", StringComparison.Ordinal)
            .Replace("ー", "", StringComparison.Ordinal)
            .Replace("‐", "", StringComparison.Ordinal)
            .Replace("〒", "", StringComparison.Ordinal)
            .ToUpperInvariant();

    private static string NormalizeIdentifier(string value) =>
        NormalizeText(value)
            .Replace(".", "", StringComparison.Ordinal)
            .Replace("/", "", StringComparison.Ordinal);

    private static string FirstNonEmpty(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;

    private sealed class Evaluation
    {
        public List<AbacusRecommendationEvidence> Matches { get; } = [];
        public List<AbacusRecommendationDifference> Differences { get; } = [];
        public List<AbacusRecommendationMissingField> MissingFields { get; } = [];
        public List<string> Conflicts { get; } = [];
    }
}
