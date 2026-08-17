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
    ("未処理の自動統合候補先は顧客巡回から除外され却下後に戻る", PendingAutomaticCustomerTargetsAreHidden),
    ("別顧客を接続先とする書類候補は現在顧客の範囲外になる", CrossCustomerDocumentTargetIsOutsideCurrentScope),
    ("統合候補をすべて拒否すると統合意思が残らない", AllRejectedCandidatesHaveNoMergeIntent),
    ("グラフ確定で残り候補を処理済みにすると未処理と保留が0になる", GraphApprovalCompletesRecommendationGate),
    ("手動リンク解除で一時Obsolete候補だけが再びActiveになる", RecommendationLifecycleReconciliation),
    ("顧客確定ゲートは統合時だけ採用プレビューを要求する", CustomerReviewGateRequiresCustomerPreview),
    ("顧客確定ゲートは保存済み採用内容で確定可能になる", CustomerReviewGateAllowsSavedMergeDraft),
    ("顧客確定ゲートは未反映統合だけでは確定を止めない", CustomerReviewGateIgnoresUnappliedMerge),
    ("顧客確定ゲートはマッチング候補の未処理と分離される", CustomerApprovalGateIgnoresMatchingQueue),
    ("マッチング候補ゲートは未処理と保留を数える", CustomerReviewGateCountsPendingAndHeld),
    ("インポート確定ゲートは顧客・統合候補・書類をすべて確認する", ImportFinalizationRequiresAllGates),
    ("最終パッケージは単独顧客も確認済みを要求する", StandaloneCustomerApprovalIsRequiredAtPackageBoundary),
    ("顧客統合判定は作業対象グループと外部候補顧客で維持される", CustomerRecommendationScopeSurvivesMemberExpansion),
    ("旧チェックポイントv1はおすすめ状態を空で補完して再開できる", LegacyCheckpointUpgrade),
    ("旧チェックポイントの顧客二重所属を正規化して再開できる", LegacyCheckpointMergeMembershipRecovery),
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

static void PendingAutomaticCustomerTargetsAreHidden()
{
    var relations = new[]
    {
        new LegacyMatchingCustomerMergeRelation(
            "customer-a",
            "customer-b",
            "automatic",
            AbacusRecommendationDecisionValues.Pending),
        new LegacyMatchingCustomerMergeRelation(
            "customer-b",
            "customer-c",
            "automatic",
            AbacusRecommendationDecisionValues.Hold),
        new LegacyMatchingCustomerMergeRelation(
            "customer-d",
            "customer-e",
            "automatic",
            AbacusRecommendationDecisionValues.Rejected),
    };

    var hidden = LegacyMatchingWorkflow.GetPendingAutomaticCustomerIdsToHide(relations);
    Assert(hidden.SetEquals(["customer-b", "customer-c"]),
        "未処理・保留の自動統合候補先だけが巡回から除外されていません。");

    var afterReject = relations
        .Select(relation => relation with
        {
            Decision = relation.SubjectCustomerId == "customer-a" &&
                       relation.TargetCustomerId == "customer-b"
                ? AbacusRecommendationDecisionValues.Rejected
                : relation.Decision,
        })
        .ToArray();
    var hiddenAfterReject = LegacyMatchingWorkflow.GetPendingAutomaticCustomerIdsToHide(afterReject);
    Assert(hiddenAfterReject.SetEquals(["customer-c"]),
        "自動統合候補を却下した後、候補先顧客が巡回対象へ戻る状態を判定できていません。");
}

static void CrossCustomerDocumentTargetIsOutsideCurrentScope()
{
    var currentCustomerIds = new HashSet<string>(["customer-a"], StringComparer.Ordinal);
    var crossCustomerCandidate = RecommendationCandidate("candidate-cross", "customer-b");
    var currentCustomerCandidate = RecommendationCandidate("candidate-current", "customer-a");

    Assert(!LegacyMatchingWorkflow.IsRelatedToCustomer(crossCustomerCandidate, currentCustomerIds),
        "別顧客をTargetCustomerIdに持つ書類候補が現在顧客の範囲に含まれています。");
    Assert(LegacyMatchingWorkflow.IsRelatedToCustomer(currentCustomerCandidate, currentCustomerIds),
        "現在顧客をTargetCustomerIdに持つ書類候補が現在顧客の範囲から除外されています。");
}

static void CustomerReviewGateRequiresCustomerPreview()
{
    Assert(!LegacyMatchingWorkflow.RequiresCustomerPreview(false, false),
        "統合候補のない普通の顧客に採用プレビューを要求しています。");
    Assert(LegacyMatchingWorkflow.RequiresCustomerPreview(true, false),
        "採用プレビュー未設定の統合候補を確定可能として扱っています。");

    var blocked = LegacyMatchingWorkflow.EvaluateCustomerReviewGate(
        [],
        new Dictionary<string, string>(),
        requiresCustomerPreview: true);
    Assert(blocked.RequiresCustomerPreview && !blocked.CanApprove,
        "採用プレビュー未設定の顧客を確定可能として扱っています。");

    var ordinary = LegacyMatchingWorkflow.EvaluateCustomerReviewGate(
        [],
        new Dictionary<string, string>(),
        requiresCustomerPreview: false);
    Assert(ordinary.CanApprove,
        "候補も採用プレビューもない普通の顧客を確定可能として判定できていません。");
}

static void AllRejectedCandidatesHaveNoMergeIntent()
{
    var candidates = new[]
    {
        RecommendationCandidate("candidate-rejected-a", "customer-a"),
        RecommendationCandidate("candidate-rejected-b", "customer-a"),
    };
    var decisions = new Dictionary<string, string>
    {
        ["candidate-rejected-a"] = AbacusRecommendationDecisionValues.Rejected,
        ["candidate-rejected-b"] = AbacusRecommendationDecisionValues.Rejected,
    };

    Assert(LegacyMatchingWorkflow.AreAllCandidatesRejected(candidates, decisions),
        "すべて拒否した顧客統合候補を未解決の統合意思として扱っています。");

    decisions["candidate-rejected-b"] = AbacusRecommendationDecisionValues.Pending;
    Assert(!LegacyMatchingWorkflow.AreAllCandidatesRejected(candidates, decisions),
        "未処理の顧客統合候補まで拒否済みとして扱っています。");
}

static void GraphApprovalCompletesRecommendationGate()
{
    var candidates = new[]
    {
        RecommendationCandidate("candidate-graph-pending", "customer-a"),
        RecommendationCandidate("candidate-graph-held", "customer-a"),
    };
    var decisions = new Dictionary<string, string>
    {
        ["candidate-graph-pending"] = AbacusRecommendationDecisionValues.Rejected,
        ["candidate-graph-held"] = AbacusRecommendationDecisionValues.Rejected,
    };

    var gate = LegacyMatchingWorkflow.EvaluateCustomerReviewGate(
        candidates,
        decisions,
        requiresCustomerPreview: false);
    Assert(gate.PendingCount == 0 && gate.HeldCount == 0 && gate.CanApprove,
        "グラフ確定後も候補ゲートに未処理または保留が残っています。");
}

static void RecommendationLifecycleReconciliation()
{
    var temporary = new LegacyGraphRecommendationState(
        AbacusRecommendationDecisionValues.Approved,
        LegacyGraphRecommendationLifecycle.Obsolete,
        LegacyGraphRecommendationLifecycleReconciler.TemporaryManualLinkObsoleteReason,
        DateTimeOffset.UtcNow,
        "customer:c1",
        "customer:c2");
    var reconciled = LegacyGraphRecommendationLifecycleReconciler.ReconcileCurrentCandidate(
        temporary,
        DateTimeOffset.UtcNow);
    Assert(reconciled is not null &&
           reconciled.Lifecycle == LegacyGraphRecommendationLifecycle.Active &&
           reconciled.Decision == AbacusRecommendationDecisionValues.Pending &&
           reconciled.ResolutionReason is null &&
           reconciled.WorkTargetKey == "customer:c1" &&
           reconciled.ExternalCustomerId == "customer:c2",
        "手動リンク解除後に再成立した候補をPending/Activeへ戻せていません。");

    var explicitRejected = new LegacyGraphRecommendationState(
        AbacusRecommendationDecisionValues.Rejected,
        LegacyGraphRecommendationLifecycle.Obsolete,
        LegacyGraphRecommendationLifecycleReconciler.ExplicitRejectedObsoleteReason,
        DateTimeOffset.UtcNow,
        "customer:c1",
        "customer:c2");
    var keptRejected = LegacyGraphRecommendationLifecycleReconciler.ReconcileCurrentCandidate(
        explicitRejected,
        DateTimeOffset.UtcNow);
    Assert(keptRejected is not null &&
           keptRejected.Lifecycle == LegacyGraphRecommendationLifecycle.Obsolete &&
           keptRejected.Decision == AbacusRecommendationDecisionValues.Rejected,
        "ユーザーが明示的に却下した候補を自動で再表示対象へ戻しています。");

    var marked = LegacyGraphRecommendationLifecycleReconciler.MarkObsoleteAfterRebuild(
        new LegacyGraphRecommendationState(
            AbacusRecommendationDecisionValues.Rejected,
            LegacyGraphRecommendationLifecycle.Active),
        "候補の再評価で外れました。",
        DateTimeOffset.UtcNow);
    Assert(marked.ResolutionReason == LegacyGraphRecommendationLifecycleReconciler.ExplicitRejectedObsoleteReason,
        "明示却下の候補を再評価時に一時Obsoleteとして扱っています。");
}

static void CustomerReviewGateAllowsSavedMergeDraft()
{
    var requiresPreview = LegacyMatchingWorkflow.RequiresCustomerPreview(
        hasMergeCandidates: true,
        hasCompleteMergeDraft: true);
    var gate = LegacyMatchingWorkflow.EvaluateCustomerReviewGate(
        [],
        new Dictionary<string, string>(),
        requiresPreview);

    Assert(!requiresPreview && gate.CanApprove,
        "統合プレビューを保存した顧客を確定可能として判定できていません。");
}

static void CustomerReviewGateIgnoresUnappliedMerge()
{
    var gate = LegacyMatchingWorkflow.EvaluateCustomerReviewGate(
        [],
        new Dictionary<string, string>(),
        requiresCustomerPreview: false);

    Assert(gate.CanApprove,
        "未反映の統合が残っていることだけで顧客確定を無効にしています。");
}

static void CustomerReviewGateCountsPendingAndHeld()
{
    var candidates = new[]
    {
        RecommendationCandidate("candidate-pending", "customer-a"),
        RecommendationCandidate("candidate-held", "customer-a"),
    };
    var gate = LegacyMatchingWorkflow.EvaluateCustomerReviewGate(
        candidates,
        new Dictionary<string, string>
        {
            ["candidate-held"] = AbacusRecommendationDecisionValues.Hold,
        },
        requiresCustomerPreview: false);

    Assert(gate.PendingCount == 1 && gate.HeldCount == 1 && !gate.CanApprove,
        "マッチング候補キューが未処理・保留候補を正しく数えていません。");
}

static void CustomerApprovalGateIgnoresMatchingQueue()
{
    var gate = LegacyMatchingWorkflow.EvaluateCustomerApprovalGate(
        requiresCustomerPreview: false);

    Assert(gate.PendingCount == 0 && gate.HeldCount == 0 && gate.CanApprove,
        "顧客最終確定がマッチング候補キューの状態に依存しています。");

    var mergeBlocked = LegacyMatchingWorkflow.EvaluateCustomerApprovalGate(
        requiresCustomerPreview: true);
    Assert(!mergeBlocked.CanApprove && mergeBlocked.RequiresCustomerPreview,
        "統合顧客の採用内容未設定を顧客最終確定の条件として扱えていません。");
}

static void ImportFinalizationRequiresAllGates()
{
    Assert(LegacyMatchingWorkflow.CanFinalizeImport(0, 0, 0),
        "未完了項目がない状態をインポート確定可能と判定できていません。");
    Assert(!LegacyMatchingWorkflow.CanFinalizeImport(1, 0, 0),
        "未承認の統合候補を残したままインポート確定可能になっています。");
    Assert(!LegacyMatchingWorkflow.CanFinalizeImport(0, 1, 0),
        "ノード未接続書類を残したままインポート確定可能になっています。");
    Assert(!LegacyMatchingWorkflow.CanFinalizeImport(0, 0, 1),
        "未確認顧客を残したままインポート確定可能になっています。");
}

static void StandaloneCustomerApprovalIsRequiredAtPackageBoundary()
{
    var root = Path.Combine(Path.GetTempPath(), $"gate28-final-package-{Guid.NewGuid():N}");
    var sourceRoot = Path.Combine(root, "source");
    var destinationRoot = Path.Combine(root, "destination");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(destinationRoot);
    File.WriteAllText(Path.Combine(sourceRoot, "manifest.json"), "{}");
    try
    {
        var customer = new AbacusLegacyExportCandidateGraphCustomer(
            "c1",
            "number-1",
            "山田太郎",
            "やまだたろう",
            "",
            "",
            "",
            "",
            "",
            [],
            []);
        var graph = new AbacusLegacyExportCandidateGraphResult(
            sourceRoot,
            [customer],
            [],
            [],
            [],
            0,
            0,
            0,
            []);
        var group = new AbacusLegacyGraphFinalCustomerGroup(
            "customer:c1",
            "single",
            false,
            ["c1"],
            "c1",
            "number-1",
            "山田太郎",
            "やまだたろう",
            "",
            "",
            "",
            "",
            "");
        var snapshot = new AbacusLegacyGraphFinalizationSnapshot(
            [group],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [],
            true);
        var store = new AbacusLegacyGraphFinalPackageStore();
        var rejected = false;
        try
        {
            store.CreateAsync(graph, snapshot, destinationRoot).GetAwaiter().GetResult();
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Assert(rejected, "未確認の単独顧客を含む最終パッケージを生成できてしまいます。");
        var result = store.CreateAsync(
                graph,
                snapshot with { CustomerGroups = [group with { Approved = true }] },
                destinationRoot)
            .GetAwaiter()
            .GetResult();
        Assert(File.Exists(result.ManifestPath),
            "確認済みの単独顧客から最終パッケージを生成できていません。");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void CustomerRecommendationScopeSurvivesMemberExpansion()
{
    var initial = CustomerRecommendationCandidate("candidate-a-b", "customer-a", "customer-b");
    var regenerated = CustomerRecommendationCandidate("candidate-c-b", "customer-c", "customer-b");
    var unrelated = CustomerRecommendationCandidate("candidate-y-b", "customer-y", "customer-b");

    Assert(LegacyCustomerRecommendationScope.TryCreate(
            initial,
            "customer:a",
            new HashSet<string>(["customer-a"], StringComparer.Ordinal),
            out var initialScope),
        "統合前の作業対象と外部候補顧客の判定スコープを作成できていません。");
    Assert(LegacyCustomerRecommendationScope.TryCreate(
            regenerated,
            "logical:x",
            new HashSet<string>(["customer-a", "customer-c"], StringComparer.Ordinal),
            out var regeneratedScope),
        "構成顧客追加後の作業対象と外部候補顧客の判定スコープを作成できていません。");
    Assert(initialScope.ExternalCustomerId == "customer-b" &&
           regeneratedScope.ExternalCustomerId == "customer-b",
        "構成顧客が変わっても外部候補顧客を同じ顧客として識別できていません。");
    Assert(!initialScope.Matches(regeneratedScope),
        "統合前後のグループIDを暗黙に同一と扱っています。実装側で明示的な引き継ぎが必要です。");

    var migratedScope = initialScope with { WorkTargetKey = "logical:x" };
    Assert(migratedScope.Matches(regeneratedScope),
        "作業対象グループの明示的な引き継ぎ後に、別CandidateIdの候補へ判定を適用できません。");
    Assert(LegacyCustomerRecommendationScope.TryCreate(
            unrelated,
            "logical:y",
            new HashSet<string>(["customer-y"], StringComparer.Ordinal),
            out var unrelatedScope) &&
           !migratedScope.Matches(unrelatedScope),
        "別の作業対象グループへ判定が波及しています。");
}

static void LegacyCheckpointMergeMembershipRecovery()
{
    var logicalGroupId = "logical:merged-yamada";
    var candidateGroupId = "same-name:山田";
    var otherCandidateGroupId = "same-name:佐藤";
    var singleCustomerGroupId = "manual:single";
    var checkpoint = new LegacyGraphWorkCheckpoint(
        LegacyGraphWorkCheckpointSchema.Kind,
        LegacyGraphWorkCheckpointSchema.CurrentVersion,
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
        [
            new LegacyGraphCheckpointMergeGroup(candidateGroupId, "same-name", ["c1", "c2"]),
            new LegacyGraphCheckpointMergeGroup(logicalGroupId, "logical", ["c1", "c2"]),
            new LegacyGraphCheckpointMergeGroup(otherCandidateGroupId, "same-name", ["c3", "c4"]),
            new LegacyGraphCheckpointMergeGroup(singleCustomerGroupId, "manual", ["c5"]),
        ],
        new Dictionary<string, string>
        {
            ["c1"] = candidateGroupId,
            ["c2"] = candidateGroupId,
            ["c3"] = otherCandidateGroupId,
            ["c4"] = otherCandidateGroupId,
            ["c5"] = singleCustomerGroupId,
        },
        new Dictionary<string, LegacyGraphCheckpointMergeDraft>
        {
            [logicalGroupId] = new LegacyGraphCheckpointMergeDraft(
                logicalGroupId,
                ["c1", "c2"],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow),
        },
        [logicalGroupId],
        new Dictionary<string, string>(),
        new Dictionary<string, bool>
        {
            [candidateGroupId] = true,
            [logicalGroupId] = false,
        },
        [],
        null,
        DateTimeOffset.UtcNow,
        CustomerApprovalStates: new Dictionary<string, bool>
        {
            [candidateGroupId] = false,
            [logicalGroupId] = true,
            ["customer:c5"] = false,
        },
        CustomerReviewStates: new Dictionary<string, string>
        {
            [candidateGroupId] = LegacyGraphCustomerReviewStateValues.NeedsReview,
            [logicalGroupId] = LegacyGraphCustomerReviewStateValues.Approved,
            ["customer:c5"] = LegacyGraphCustomerReviewStateValues.Unreviewed,
        });

    var recovered = LegacyGraphWorkCheckpointRecovery.NormalizeMergeMembership(checkpoint);
    var groups = recovered.Checkpoint.CustomerMergeGroups;
    Assert(recovered.Changed, "二重所属チェックポイントが正規化済みと判定されています。");
    Assert(groups.Length == 2 &&
           groups.Any(group => group.GroupId == logicalGroupId && group.CustomerIds.SequenceEqual(["c1", "c2"])) &&
           groups.Any(group => group.GroupId == otherCandidateGroupId && group.CustomerIds.SequenceEqual(["c3", "c4"])),
        "論理グループを優先した顧客構成へ正規化できていません。");
    Assert(!recovered.Checkpoint.CustomerMergeGroupByCustomerId.ContainsKey("c1") &&
           recovered.Checkpoint.CustomerMergeGroupByCustomerId["c3"] == otherCandidateGroupId,
        "仮グループの顧客所属マップが正規化されていません。");
    var logicalMap = recovered.Checkpoint.LogicalCustomerMergeGroupByCustomerId ?? [];
    var reviewStates = recovered.Checkpoint.CustomerReviewStates ?? [];
    Assert(logicalMap["c1"] == logicalGroupId &&
           logicalMap["c2"] == logicalGroupId,
        "論理顧客グループの所属マップが保持されていません。");
    Assert(reviewStates.ContainsKey("customer:c5") &&
           !reviewStates.ContainsKey(candidateGroupId),
        "通常顧客の確認状態を残したまま古い候補グループ状態だけを整理できていません。");

    var alreadyNormalized = LegacyGraphWorkCheckpointRecovery.NormalizeMergeMembership(
        recovered.Checkpoint);
    Assert(!alreadyNormalized.Changed,
        "重複所属を整理したチェックポイントを再度変更しています。");
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

static AbacusRecommendationCandidate CustomerRecommendationCandidate(
    string candidateId,
    string subjectCustomerId,
    string targetCustomerId) =>
    new(
        candidateId,
        AbacusRecommendationEntityKinds.Customer,
        subjectCustomerId,
        AbacusRecommendationEntityKinds.Customer,
        targetCustomerId,
        targetCustomerId,
        [],
        [],
        [],
        [],
        "テスト用の顧客統合おすすめ",
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

        var scopedCheckpoint = reviewCheckpoint with
        {
            RecommendationStates =
            [
                new LegacyGraphCheckpointRecommendationState(
                    "candidate-regenerated",
                    AbacusRecommendationDecisionValues.Rejected,
                    LegacyGraphRecommendationLifecycle.Active,
                    "ユーザーが別人と判断しました。",
                    DateTimeOffset.UtcNow,
                    "logical:work-target",
                    "customer:external"),
            ],
            MatchingChanges =
            [
                new LegacyGraphCheckpointMatchingChange(
                    "recommendation-scope\u001Flogical:work-target\u001Fcustomer:external",
                    "recommendation",
                    "candidate-regenerated",
                    "別人と判断",
                    "元に戻せます。",
                    DateTimeOffset.UtcNow,
                    "customer:c1",
                    "logical:work-target",
                    "customer:external",
                    new LegacyGraphDetachedUndoState(
                        "document",
                        "document-1",
                        "customer:b",
                        true,
                        [
                            new LegacyGraphDetachedDocumentState(
                                "document-1",
                                "vehicle-b",
                                "logical:work-target",
                                "manual-vehicle",
                                "ユーザーが選択",
                                false,
                                true,
                                false),
                        ])),
            ],
        };
        File.WriteAllText(Path.Combine(directory, "graph-state.json"), JsonSerializer.Serialize(
            scopedCheckpoint,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var scoped = new LegacyGraphWorkCheckpointStore().ReadAsync(directory).GetAwaiter().GetResult();
        Assert(scoped.RecommendationStates is { Length: 1 } &&
               scoped.RecommendationStates[0].WorkTargetKey == "logical:work-target" &&
               scoped.RecommendationStates[0].ExternalCustomerId == "customer:external" &&
               scoped.MatchingChanges is { Length: 1 } &&
               scoped.MatchingChanges[0].WorkTargetKey == "logical:work-target" &&
               scoped.MatchingChanges[0].ExternalCustomerId == "customer:external" &&
               scoped.MatchingChanges[0].UndoState is { } undoState &&
               undoState.ManualVehicleCustomerId == "customer:b" &&
               undoState.Documents is [{ ManualVehicleId: "vehicle-b", IsTray: true }],
            "顧客統合判定の作業対象スコープがチェックポイント往復で失われています。");
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
