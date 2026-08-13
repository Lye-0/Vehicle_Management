using System.Text;

namespace VehicleManagement.AbacusImport;

public sealed record AbacusSameNameConflictPreview(
    string CustomerName,
    int SeparateCustomerCandidates,
    int DocumentCount,
    string EvidenceSummary,
    string Reason);

public sealed record AbacusMultipleVehiclePreview(
    string CustomerName,
    int VehicleCount,
    int DocumentCount,
    string VehicleSummary);

public sealed record AbacusVehicleConflictPreview(
    string Identifier,
    int VehicleCandidates,
    int CustomerCandidates,
    int DocumentCount,
    string CustomerNames,
    string Reason);

public sealed record AbacusLinkagePlan(
    string FolderPath,
    int ImportCandidateDocuments,
    int SkippedBlankCustomerDocuments,
    int CustomerCandidates,
    int VehicleCandidates,
    int CustomersWithMultipleVehicles,
    int SameNameConflictGroups,
    int SameNameConflictDocuments,
    int VehicleIdentifierConflictGroups,
    int VehiclesLinkedToMultipleCustomers,
    int DocumentsWithoutVehicleInformation,
    int DocumentsWithVehicleButWithoutStrongIdentifier,
    IReadOnlyList<AbacusSameNameConflictPreview> SameNameConflicts,
    IReadOnlyList<AbacusMultipleVehiclePreview> MultipleVehicleCustomers,
    IReadOnlyList<AbacusVehicleConflictPreview> VehicleConflicts,
    IReadOnlyList<AbacusParseError> Errors,
    DateTime PlannedAtUtc)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class AbacusLinkagePlanner(AbacusTabParser parser)
{
    public async Task<AbacusLinkagePlan> PlanAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var salesTask = parser.ParseAsync(folderPath, AbacusTabSpecifications.Sales, cancellationToken);
        var maintenanceTask = parser.ParseAsync(folderPath, AbacusTabSpecifications.Maintenance, cancellationToken);
        await Task.WhenAll(salesTask, maintenanceTask);

        var errors = salesTask.Result.Errors.Concat(maintenanceTask.Result.Errors).ToList();
        var skippedBlankCustomer = 0;
        var records = new List<LinkageRecord>();
        AddRecords(salesTask.Result, records, ref skippedBlankCustomer);
        AddRecords(maintenanceTask.Result, records, ref skippedBlankCustomer);

        if (errors.Count > 0)
        {
            return new AbacusLinkagePlan(
                Path.GetFullPath(folderPath), records.Count, skippedBlankCustomer, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                [], [], [], errors, DateTime.UtcNow);
        }

        var vehicleUnion = new DisjointSet(records.Count);
        UnionByStrongIdentifier(records, vehicleUnion, record => record.ChassisKey);
        var conflictingRegistrations = UnionByRegistration(records, vehicleUnion);

        var customerUnion = new DisjointSet(records.Count);
        foreach (var sameNameRecords in records.GroupBy(record => record.NormalizedCustomerName))
        {
            UnionCustomerEvidence(sameNameRecords, customerUnion, conflictingRegistrations);
        }

        var customerRoots = records.ToDictionary(record => record.Id, record => customerUnion.Find(record.Id));
        var vehicleRoots = records
            .Where(record => record.HasVehicleInformation)
            .ToDictionary(record => record.Id, record => vehicleUnion.Find(record.Id));

        var customerGroups = records.GroupBy(record => customerRoots[record.Id]).ToList();
        var vehicleGroups = records
            .Where(record => record.HasVehicleInformation)
            .GroupBy(record => vehicleRoots[record.Id])
            .ToList();

        var sameNameConflicts = customerGroups
            .GroupBy(group => group.First().NormalizedCustomerName)
            .Where(group => group.Count() > 1)
            .Select(group => new AbacusSameNameConflictPreview(
                group.First().First().CustomerName,
                group.Count(),
                group.Sum(candidate => candidate.Count()),
                string.Join(" / ", group.Take(5).Select(DescribeCustomerEvidence)),
                "名前は同じですが、電話・住所・安全な車両識別子が一致しないため分離しています。"))
            .OrderByDescending(preview => preview.DocumentCount)
            .ThenBy(preview => preview.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var multipleVehicleCustomers = customerGroups
            .Select(group => new
            {
                Group = group,
                Vehicles = group.Where(record => vehicleRoots.ContainsKey(record.Id))
                    .GroupBy(record => vehicleRoots[record.Id])
                    .ToList(),
            })
            .Where(item => item.Vehicles.Count > 1)
            .Select(item => new AbacusMultipleVehiclePreview(
                item.Group.First().CustomerName,
                item.Vehicles.Count,
                item.Group.Count(),
                string.Join(" / ", item.Vehicles.Take(8).Select(DescribeVehicle))))
            .OrderByDescending(preview => preview.VehicleCount)
            .ThenByDescending(preview => preview.DocumentCount)
            .ToList();

        var registrationConflicts = conflictingRegistrations.Select(registration =>
        {
            var matching = records.Where(record => record.RegistrationNumber == registration).ToList();
            return new AbacusVehicleConflictPreview(
                registration,
                matching.Select(record => vehicleRoots[record.Id]).Distinct().Count(),
                matching.Select(record => customerRoots[record.Id]).Distinct().Count(),
                matching.Count,
                string.Join("、", matching.Select(record => record.CustomerName).Distinct().Take(8)),
                "同じ登録番号に異なる車台番号があるため、自動統合しません。登録番号の再利用や入力差異を確認してください。");
        }).ToList();

        var crossCustomerVehicles = vehicleGroups
            .Select(group => new
            {
                Group = group,
                Customers = group.Select(record => customerRoots[record.Id]).Distinct().Count(),
            })
            .Where(item => item.Customers > 1)
            .ToList();

        var crossCustomerPreviews = crossCustomerVehicles.Select(item =>
        {
            var first = item.Group.First();
            var identifier = !string.IsNullOrEmpty(first.ChassisNumber)
                ? first.ChassisNumber
                : first.RegistrationNumber;
            return new AbacusVehicleConflictPreview(
                identifier,
                1,
                item.Customers,
                item.Group.Count(),
                string.Join("、", item.Group.Select(record => record.CustomerName).Distinct().Take(8)),
                "同じ車両識別子が複数の顧客候補に現れます。名義変更・過去所有・入力差異を確認してください。");
        });

        var vehicleConflicts = registrationConflicts
            .Concat(crossCustomerPreviews)
            .OrderByDescending(preview => preview.DocumentCount)
            .ToList();

        return new AbacusLinkagePlan(
            Path.GetFullPath(folderPath),
            records.Count,
            skippedBlankCustomer,
            customerGroups.Count,
            vehicleGroups.Count,
            multipleVehicleCustomers.Count,
            sameNameConflicts.Count,
            sameNameConflicts.Sum(conflict => conflict.DocumentCount),
            conflictingRegistrations.Count,
            crossCustomerVehicles.Count,
            records.Count(record => !record.HasVehicleInformation),
            records.Count(record => record.HasVehicleInformation && !record.HasStrongVehicleIdentifier),
            sameNameConflicts,
            multipleVehicleCustomers,
            vehicleConflicts,
            errors,
            DateTime.UtcNow);
    }

    private static void AddRecords(
        AbacusTabParseResult parseResult,
        ICollection<LinkageRecord> destination,
        ref int skippedBlankCustomer)
    {
        var specification = parseResult.Specification;
        foreach (var record in parseResult.Records)
        {
            var customerName = NormalizeText(record.Fields[specification.CustomerNameColumn]);
            if (string.IsNullOrEmpty(customerName))
            {
                skippedBlankCustomer++;
                continue;
            }

            var phones = specification.PhoneColumns
                .Select(column => NormalizeDigits(record.Fields[column]))
                .Where(phone => phone.Length >= 10)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            destination.Add(new LinkageRecord(
                destination.Count,
                specification.DisplayName,
                record.RowNumber,
                NormalizeIdentifier(record.Fields[specification.DocumentNumberColumn]),
                customerName,
                NormalizeText(customerName).Normalize(NormalizationForm.FormKC).ToUpperInvariant(),
                NormalizeText(record.Fields[specification.AddressColumn]),
                specification.PostalCodeColumn is int postalColumn
                    ? NormalizeDigits(record.Fields[postalColumn])
                    : string.Empty,
                phones,
                NormalizeText(record.Fields[specification.MakerColumn]),
                NormalizeText(record.Fields[specification.VehicleNameColumn]),
                NormalizeText(record.Fields[specification.ModelColumn]),
                NormalizeIdentifier(record.Fields[specification.RegistrationNumberColumn]),
                NormalizeIdentifier(record.Fields[specification.ChassisNumberColumn])));
        }
    }

    private static void UnionByStrongIdentifier(
        IEnumerable<LinkageRecord> records,
        DisjointSet union,
        Func<LinkageRecord, string> selector)
    {
        var firstByIdentifier = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var identifier = selector(record);
            if (string.IsNullOrEmpty(identifier))
            {
                continue;
            }

            if (firstByIdentifier.TryGetValue(identifier, out var first))
            {
                union.Union(first, record.Id);
            }
            else
            {
                firstByIdentifier[identifier] = record.Id;
            }
        }
    }

    private static HashSet<string> UnionByRegistration(
        IReadOnlyCollection<LinkageRecord> records,
        DisjointSet union)
    {
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in records
                     .Where(record => !string.IsNullOrEmpty(record.RegistrationNumber))
                     .GroupBy(record => record.RegistrationNumber, StringComparer.OrdinalIgnoreCase))
        {
            var chassisNumbers = group.Select(record => record.ChassisKey)
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (chassisNumbers.Count > 1)
            {
                conflicts.Add(group.Key);
                continue;
            }

            var first = group.First().Id;
            foreach (var record in group.Skip(1))
            {
                union.Union(first, record.Id);
            }
        }

        return conflicts;
    }

    private static void UnionCustomerEvidence(
        IEnumerable<LinkageRecord> records,
        DisjointSet union,
        ISet<string> conflictingRegistrations)
    {
        var firstByEvidence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var evidence = new List<string>();
            evidence.AddRange(record.Phones.Select(phone => $"phone:{phone}"));
            if (record.Address.Length >= 4)
            {
                evidence.Add($"address:{NormalizeLoose(record.Address)}");
            }
            if (!string.IsNullOrEmpty(record.ChassisKey))
            {
                evidence.Add($"chassis:{record.ChassisKey}");
            }
            else if (!string.IsNullOrEmpty(record.RegistrationNumber) &&
                     !conflictingRegistrations.Contains(record.RegistrationNumber))
            {
                evidence.Add($"registration:{record.RegistrationNumber}");
            }

            foreach (var key in evidence)
            {
                if (firstByEvidence.TryGetValue(key, out var first))
                {
                    union.Union(first, record.Id);
                }
                else
                {
                    firstByEvidence[key] = record.Id;
                }
            }
        }
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value.Trim())
        {
            var normalized = character is '\u0004' or '\u000B' or '\u001D' ? ' ' : character;
            if (char.IsWhiteSpace(normalized) || normalized == '　')
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }
            }
            else
            {
                builder.Append(normalized);
                previousWasSpace = false;
            }
        }

        return builder.ToString();
    }

    private static string NormalizeIdentifier(string value) =>
        string.Concat(NormalizeText(value).Normalize(NormalizationForm.FormKC)
                .Where(character => !char.IsWhiteSpace(character) && character != '-'))
            .ToUpperInvariant();

    private static string NormalizeDigits(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormKC).Where(char.IsDigit));

    private static string NormalizeLoose(string value) =>
        string.Concat(NormalizeText(value).Normalize(NormalizationForm.FormKC)
                .Where(character => !char.IsWhiteSpace(character) && character != '-'))
            .ToUpperInvariant();

    private static string DescribeCustomerEvidence(IEnumerable<LinkageRecord> group)
    {
        var records = group.ToList();
        var first = records[0];
        var evidence = new List<string>();
        var phones = records.SelectMany(record => record.Phones).Distinct().Take(2).ToList();
        if (phones.Count > 0)
        {
            evidence.Add($"電話 {string.Join(',', phones)}");
        }
        if (!string.IsNullOrEmpty(first.Address))
        {
            evidence.Add($"住所 {Truncate(first.Address, 24)}");
        }
        var chassis = records.Select(record => record.ChassisNumber).FirstOrDefault(value => !string.IsNullOrEmpty(value));
        if (!string.IsNullOrEmpty(chassis))
        {
            evidence.Add($"車台 {chassis}");
        }

        return evidence.Count > 0 ? string.Join("・", evidence) : "一致根拠なし";
    }

    private static string DescribeVehicle(IEnumerable<LinkageRecord> group)
    {
        var record = group.First();
        var name = string.Join(' ', new[] { record.Maker, record.VehicleName, record.Model }
            .Where(value => !string.IsNullOrEmpty(value)));
        var identifier = !string.IsNullOrEmpty(record.ChassisNumber)
            ? $"車台:{record.ChassisNumber}"
            : !string.IsNullOrEmpty(record.RegistrationNumber)
                ? $"登録:{record.RegistrationNumber}"
                : "識別子なし";
        return $"{name} ({identifier})";
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : $"{value[..maximumLength]}…";

    private sealed record LinkageRecord(
        int Id,
        string Kind,
        int RowNumber,
        string DocumentNumber,
        string CustomerName,
        string NormalizedCustomerName,
        string Address,
        string PostalCode,
        IReadOnlyList<string> Phones,
        string Maker,
        string VehicleName,
        string Model,
        string RegistrationNumber,
        string ChassisNumber)
    {
        public bool HasVehicleInformation =>
            !string.IsNullOrEmpty(Maker) ||
            !string.IsNullOrEmpty(VehicleName) ||
            !string.IsNullOrEmpty(Model) ||
            HasStrongVehicleIdentifier;

        public bool HasStrongVehicleIdentifier =>
            !string.IsNullOrEmpty(ChassisKey) || !string.IsNullOrEmpty(RegistrationNumber);

        public string ChassisKey => string.IsNullOrEmpty(ChassisNumber)
            ? string.Empty
            : ChassisNumber.All(char.IsDigit) || ChassisNumber.Length < 8
                ? $"{NormalizeIdentifier(Model)}:{ChassisNumber}"
                : ChassisNumber;
    }

    private sealed class DisjointSet(int count)
    {
        private readonly int[] parent = Enumerable.Range(0, count).ToArray();
        private readonly byte[] rank = new byte[count];

        public int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }

            return value;
        }

        public void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot)
            {
                return;
            }

            if (rank[leftRoot] < rank[rightRoot])
            {
                parent[leftRoot] = rightRoot;
            }
            else if (rank[leftRoot] > rank[rightRoot])
            {
                parent[rightRoot] = leftRoot;
            }
            else
            {
                parent[rightRoot] = leftRoot;
                rank[leftRoot]++;
            }
        }
    }
}
