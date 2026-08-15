using VehicleManagement.AbacusImport;
using VehicleManagement.Companion.Services;
using System.Text.Json;

var tests = new (string Name, Action Test)[]
{
    ("車台番号完全一致は車両おすすめになる", ExactChassisMatch),
    ("顧客名の軽微な誤字は顧客おすすめになる", FuzzyCustomerNameMatch),
    ("同姓同名は承認可能候補にならない", SameNameConflict),
    ("顧客同士の一部一致は統合おすすめになる", CustomerIntegrationRecommendation),
    ("識別子競合は承認可能候補にならない", IdentifierConflict),
    ("顧客なし車両は顧客おすすめになる", UnconnectedVehicleMatch),
    ("入力順を変えても候補順と根拠が変わらない", DeterministicOutput),
    ("不足情報は差異と分離して保存される", MissingInformationIsStructured),
    ("顧客単位カテゴリは顧客統合を先頭にする", MatchingCategoriesAreOrdered),
    ("同一論理書類の候補は1操作に集約される", SameLogicalDocumentRecommendationsAreGrouped),
    ("旧チェックポイントv1はおすすめ状態を空で補完して再開できる", LegacyCheckpointUpgrade),
};

foreach (var (name, test) in tests)
{
    test();
    Console.WriteLine($"PASS {name}");
}

Console.WriteLine($"Gate28 tests passed: {tests.Length}");
return 0;

static void ExactChassisMatch()
{
    var customer = Customer("c1", "山田太郎");
    var vehicle = Vehicle("v1", "c1", "山田太郎", chassis: "ABC-123", registration: "品川 500 あ 12-34");
    var document = Document("d1", new AbacusRecommendationProfile(
        CustomerName: "山田太郎",
        ChassisNumber: "ABC123"));

    var candidates = Build([document], [vehicle], [customer]);
    var match = candidates.Single(candidate =>
        candidate.SubjectId == "d1" &&
        candidate.TargetKind == AbacusRecommendationEntityKinds.Vehicle &&
        candidate.TargetId == "v1");
    Assert(match.IsEligible, "車台番号完全一致候補が承認可能になっていません。");
    Assert(match.MatchedFields.Any(field => field.Field == "chassisNumber" && field.MatchKind == "exact" && field.IsStrong),
        "車台番号完全一致の強い根拠がありません。");
}

static void FuzzyCustomerNameMatch()
{
    var customer = Customer("c1", "山田太朗");
    var document = Document("d1", new AbacusRecommendationProfile(CustomerName: "山田太郎"));

    var candidates = Build([document], [], [customer]);
    var match = candidates.Single();
    Assert(match.TargetKind == AbacusRecommendationEntityKinds.Customer && match.IsEligible,
        "顧客名の軽微な誤字が顧客おすすめになっていません。");
    Assert(match.MatchedFields.Any(field => field.Field == "customerName" && field.MatchKind == "fuzzy"),
        "顧客名の軽微な誤字がfuzzy根拠として保存されていません。");
}

static void SameNameConflict()
{
    var customers = new[]
    {
        Customer("c1", "山田太郎"),
        Customer("c2", "山田太郎"),
    };
    var document = Document("d1", new AbacusRecommendationProfile(CustomerName: "山田太郎"));

    var candidates = Build([document], [], customers);
    Assert(candidates.Count == 2, "同姓同名の候補数が想定と異なります。");
    Assert(candidates.All(candidate =>
        !candidate.IsEligible &&
        candidate.Conflicts.Any(conflict => conflict.Contains("複数の顧客", StringComparison.Ordinal))),
        "同姓同名が競合として扱われていません。");
}

static void CustomerIntegrationRecommendation()
{
    var customers = new[]
    {
        Customer("c1", "山田太郎", phone: "03-1234-5678"),
        Customer("c2", "山田太朗", phone: "03-1234-5678"),
    };

    var match = Build([], [], customers).Single(candidate =>
        candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
        candidate.TargetKind == AbacusRecommendationEntityKinds.Customer);
    Assert(match.IsEligible, "顧客同士の一部一致が承認可能な統合おすすめになっていません。");
    Assert(match.MatchedFields.Any(field => field.Field == "phoneNumber"),
        "顧客同士の統合おすすめに電話番号の一致根拠がありません。");
}

static void IdentifierConflict()
{
    var customer = Customer("c1", "山田太郎");
    var vehicles = new[]
    {
        Vehicle("v1", "c1", "山田太郎", chassis: "ABC123"),
        Vehicle("v2", "c1", "山田太郎", chassis: "ABC123"),
    };
    var document = Document("d1", new AbacusRecommendationProfile(
        CustomerName: "山田太郎",
        ChassisNumber: "ABC123"));

    var candidates = Build([document], vehicles, [customer])
        .Where(candidate => candidate.TargetKind == AbacusRecommendationEntityKinds.Vehicle)
        .ToArray();
    Assert(candidates.Length == 2 && candidates.All(candidate => !candidate.IsEligible),
        "重複車台番号が承認可能候補になっています。");
    Assert(candidates.All(candidate =>
        candidate.Conflicts.Any(conflict => conflict.Contains("複数の車両", StringComparison.Ordinal))),
        "重複車台番号の競合理由がありません。");
}

static void UnconnectedVehicleMatch()
{
    var customer = Customer("c1", "佐藤花子");
    var vehicle = Vehicle("v1", "", "佐藤花子");

    var candidates = Build([], [vehicle], [customer]);
    var match = candidates.Single();
    Assert(match.SubjectKind == AbacusRecommendationEntityKinds.Vehicle &&
           match.TargetKind == AbacusRecommendationEntityKinds.Customer &&
           match.IsEligible,
        "顧客なし車両の顧客おすすめが生成されていません。");
}

static void DeterministicOutput()
{
    var customers = new[]
    {
        Customer("c2", "佐藤花子"),
        Customer("c1", "山田太郎"),
    };
    var vehicles = new[]
    {
        Vehicle("v2", "c2", "佐藤花子", registration: "練馬 500 か 2-2"),
        Vehicle("v1", "c1", "山田太郎", registration: "品川 500 あ 1-1"),
    };
    var documents = new[]
    {
        Document("d2", new AbacusRecommendationProfile(CustomerName: "佐藤花子", RegistrationNumber: "練馬500か22")),
        Document("d1", new AbacusRecommendationProfile(CustomerName: "山田太郎", RegistrationNumber: "品川500あ11")),
    };

    var first = Build(documents, vehicles, customers);
    var second = Build(documents.Reverse().ToArray(), vehicles.Reverse().ToArray(), customers.Reverse().ToArray());
    Assert(first.Select(candidate => candidate.CandidateId)
            .SequenceEqual(second.Select(candidate => candidate.CandidateId), StringComparer.Ordinal),
        "候補順が入力順の影響を受けています。");
    Assert(first.Select(candidate => candidate.Reason)
            .SequenceEqual(second.Select(candidate => candidate.Reason), StringComparer.Ordinal),
        "候補根拠が入力順の影響を受けています。");
}

static void MissingInformationIsStructured()
{
    var customer = Customer("c1", "山田太郎");
    var document = Document("d1", new AbacusRecommendationProfile(
        CustomerName: "山田太郎",
        PhoneNumber: "03-1234-5678"));

    var match = Build([document], [], [customer]).Single();
    Assert(match.MissingFields.Any(field => field.Field == "phoneNumber" && field.MissingSide == "candidate"),
        "候補側の不足情報が構造化されていません。");
    Assert(match.Differences.All(field => field.Field != "phoneNumber"),
        "不足情報が差異として重複保存されています。");
}

static void MatchingCategoriesAreOrdered()
{
    var customers = new[]
    {
        Customer("c1", "山田太郎", phone: "03-1234-5678"),
        Customer("c2", "山田太朗", phone: "03-1234-5678"),
    };
    var candidates = Build(
        [Document("d1", new AbacusRecommendationProfile(CustomerName: "山田太郎"))],
        [Vehicle("v1", "", "山田太郎")],
        customers);

    var summaries = LegacyMatchingWorkflow.BuildCategorySummaries(
        candidates,
        new Dictionary<string, string>());
    Assert(summaries.Select(summary => summary.Kind).SequenceEqual(
            [LegacyMatchingCategoryKinds.Customer, LegacyMatchingCategoryKinds.Vehicle, LegacyMatchingCategoryKinds.Document]),
        "顧客単位カテゴリの順序が仕様どおりではありません。");
    Assert(summaries[0].Total > 0, "顧客統合カテゴリが集計されていません。");

    var heldCustomer = candidates.First(candidate =>
        LegacyMatchingCategoryKinds.GetKind(candidate) == LegacyMatchingCategoryKinds.Customer);
    var heldSummaries = LegacyMatchingWorkflow.BuildCategorySummaries(
        candidates,
        new Dictionary<string, string>
        {
            [heldCustomer.CandidateId] = AbacusRecommendationDecisionValues.Hold,
        });
    var customerSummary = heldSummaries.First(summary =>
        summary.Kind == LegacyMatchingCategoryKinds.Customer);
    Assert(customerSummary.Held == 1 && customerSummary.Completed == 0,
        "保留中のおすすめを完了件数として扱っています。");
}

static void SameLogicalDocumentRecommendationsAreGrouped()
{
    var candidates = new[]
    {
        RecommendationCandidate("candidate-a", "a"),
        RecommendationCandidate("candidate-b", "b"),
        RecommendationCandidate("candidate-c", "c"),
    };

    var groups = LegacyMatchingRecommendationGrouping.Group(
        candidates,
        candidate => $"document:{candidate.SubjectId}:logical:merged-customer");

    Assert(groups.Count == 1, "同一論理書類が複数の操作単位に分かれています。");
    Assert(groups[0].Candidates.Count == 3,
        "論理書類に属する元候補がグループへ保持されていません。");
    Assert(groups[0].Representative.CandidateId == "candidate-a",
        "候補グループの代表が決定的に選ばれていません。");
}

static AbacusRecommendationCandidate RecommendationCandidate(
    string candidateId,
    string targetCustomerId) =>
    new(
        candidateId,
        AbacusRecommendationEntityKinds.Document,
        "document-1",
        AbacusRecommendationEntityKinds.Customer,
        targetCustomerId,
        targetCustomerId,
        [],
        [],
        [],
        [],
        "テスト用のおすすめ",
        "test");

static void LegacyCheckpointUpgrade()
{
    var directory = Path.Combine(Path.GetTempPath(), $"gate28-checkpoint-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var checkpoint = new LegacyGraphWorkCheckpoint(
            LegacyGraphWorkCheckpointSchema.Kind,
            1,
            "package",
            "source",
            new string('A', 64),
            "candidate",
            new string('B', 64),
            "vehicles.csv",
            false,
            "graph",
            null,
            null,
            false,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            new Dictionary<string, string>(),
            new Dictionary<string, LegacyGraphCheckpointMergeDraft>(),
            [],
            new Dictionary<string, string>(),
            new Dictionary<string, bool>(),
            [],
            null,
            DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        File.WriteAllText(Path.Combine(directory, "graph-state.json"), json);

        var restored = new LegacyGraphWorkCheckpointStore().ReadAsync(directory).GetAwaiter().GetResult();
        Assert(restored.Version == LegacyGraphWorkCheckpointSchema.CurrentVersion,
            "旧チェックポイントのスキーマバージョンが更新されていません。");
        Assert(restored.RecommendationStates is { Length: 0 },
            "旧チェックポイントのおすすめ状態が空配列へ補完されていません。");

        var reviewCheckpoint = checkpoint with
        {
            Version = LegacyGraphWorkCheckpointSchema.CurrentVersion,
            CustomerApprovalStates = new Dictionary<string, bool>
            {
                ["customer:c1"] = true,
            },
            CustomerReviewStates = new Dictionary<string, string>
            {
                ["customer:c1"] = LegacyGraphCustomerReviewStateValues.NeedsReview,
            },
        };
        File.WriteAllText(Path.Combine(directory, "graph-state.json"), JsonSerializer.Serialize(
            reviewCheckpoint,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var reviewed = new LegacyGraphWorkCheckpointStore().ReadAsync(directory).GetAwaiter().GetResult();
        Assert(reviewed.CustomerReviewStates is not null &&
               reviewed.CustomerReviewStates.TryGetValue("customer:c1", out var reviewState) &&
               reviewState == LegacyGraphCustomerReviewStateValues.NeedsReview,
            "顧客の再確認待ち状態がチェックポイントから復元されていません。");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static IReadOnlyList<AbacusRecommendationCandidate> Build(
    IReadOnlyList<AbacusRecommendationDocument> documents,
    IReadOnlyList<AbacusRecommendationVehicle> vehicles,
    IReadOnlyList<AbacusRecommendationCustomer> customers) =>
    new AbacusRecommendationEngine().Build(new AbacusRecommendationInput(documents, vehicles, customers));

static AbacusRecommendationCustomer Customer(string id, string name, string phone = "") =>
    new(id, new AbacusRecommendationProfile(CustomerName: name, PhoneNumber: phone));

static AbacusRecommendationVehicle Vehicle(
    string id,
    string customerId,
    string customerName,
    string chassis = "",
    string registration = "") =>
    new(
        id,
        customerId,
        new AbacusRecommendationProfile(
            CustomerName: customerName,
            ChassisNumber: chassis,
            RegistrationNumber: registration));

static AbacusRecommendationDocument Document(
    string id,
    AbacusRecommendationProfile profile) =>
    new(id, profile);

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
