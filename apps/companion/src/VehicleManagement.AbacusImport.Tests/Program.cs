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
    ("顧客の別人判定履歴は確認中顧客と候補を表示する", CustomerRejectedChangeDetailsUseCustomerRoles),
    ("別顧客を接続先とする書類候補は現在顧客の範囲外になる", CrossCustomerDocumentTargetIsOutsideCurrentScope),
    ("統合候補をすべて拒否すると統合意思が残らない", AllRejectedCandidatesHaveNoMergeIntent),
    ("グラフ確定で残り候補を処理済みにすると未処理と保留が0になる", GraphApprovalCompletesRecommendationGate),
    ("手動リンク解除で一時Obsolete候補だけが再びActiveになる", RecommendationLifecycleReconciliation),
    ("顧客統合時に変更履歴のUndoStateも新しいグループIDへ移行する", DetachedUndoStateGroupReferencesMigrate),
    ("確認済み顧客へ書類を新規接続すると再確認待ちになる", ApprovedCustomerDocumentConnectionNeedsReview),
    ("確認済み顧客へ車両を新規接続すると再確認待ちになる", ApprovedCustomerVehicleConnectionNeedsReview),
    ("元顧客所属車両のUndoは手動リンクを作らない", OriginalCustomerVehicleUndoDoesNotBecomeManualLink),
    ("車両解除時は手動書類リンクだけを解除対象にする", VehicleDetachOnlyClearsManualDocumentLink),
    ("AからBへ手動接続した書類のUndoはBを復元する", ManualDocumentUndoRestoresCurrentCustomer),
    ("ごみ箱内の顧客・車両・書類はおすすめ対象にならない", TrashRecommendationEndpointsAreInactive),
    ("Graph UIとMatching UIの顧客確定結果は同じドメイン状態になる", GraphAndMatchingApprovalShareDomainState),
    ("顧客確定に伴う一括解決は個別の別人履歴にならない", CustomerApprovalResolutionDoesNotBecomeIndividualHistory),
    ("作業再開の検証失敗は保存済みチェックポイントを変更しない", ResumeValidationFailureDoesNotMutateCheckpoint),
    ("手動移動後の書類所有者は元顧客ではなく現在の顧客を使う", ManualDocumentOwnerUsesCurrentCustomer),
    ("現在の書類車両解決は手動移動先を優先する", CurrentDocumentVehicleResolutionPrefersManualLink),
    ("最終パッケージの書類除外は現在の車両・顧客所属を使う", FinalPackageUsesCurrentDocumentOwnership),
    ("顧客なし車両への手動接続は現在顧客として解決される", ManualVehicleCustomerOwnershipIsCurrent),
    ("チェックポイント保存はBulk・最終出力・再開中に開始しない", CheckpointSaveDefersWhileBusy),
    ("確定済み論理グループを単独顧客へ戻すとNeedsReviewを現在キーへ移す", ConfirmedGroupSplitMovesReviewState),
    ("未確定の論理グループを単独顧客へ戻すとUnreviewedになる", UnapprovedGroupSplitMovesToUnreviewed),
    ("手動顧客候補追加はBusy中に対象状態を変更しない", ManualCustomerCandidateMutationGuard),
    ("グラフ仮統合は候補Reject後も所属構成を維持する", GraphStructuralMergeMembershipIgnoresMatchingReject),
    ("顧客確定ゲートは統合時だけ採用プレビューを要求する", CustomerReviewGateRequiresCustomerPreview),
    ("顧客確定ゲートは保存済み採用内容で確定可能になる", CustomerReviewGateAllowsSavedMergeDraft),
    ("顧客確定ゲートは未反映統合だけでは確定を止めない", CustomerReviewGateIgnoresUnappliedMerge),
    ("顧客確定ゲートはマッチング候補の未処理と分離される", CustomerApprovalGateIgnoresMatchingQueue),
    ("マッチング候補ゲートは未処理と保留を数える", CustomerReviewGateCountsPendingAndHeld),
    ("インポート確定ゲートは顧客・統合候補・書類をすべて確認する", ImportFinalizationRequiresAllGates),
    ("最終パッケージは単独顧客も確認済みを要求する", StandaloneCustomerApprovalIsRequiredAtPackageBoundary),
    ("ごみ箱の単独未確認顧客は最終パッケージを阻害しない", TrashStandaloneCustomerDoesNotBlockFinalPackage),
    ("有効顧客とごみ箱顧客の混在グループは最終出力で拒否する", TrashCustomerMixedGroupIsRejectedAtPackageBoundary),
    ("一括処理中・最終出力中・再開中はユーザー変更を禁止する", LegacyGraphMutationGuard),
    ("最終パッケージ完成には確定状態の再確認が必要", FinalPackageCompletionRequiresStableConfirmation),
    ("グラフ仮統合は推薦判定ではなく所属構成で決まる", GraphTemporaryMergeGroupUsesMembershipOnly),
    ("マッチングの構成顧客解除は基準顧客以外だけを許可する", MatchingMergeMemberSelectionIsScoped),
    ("Matching顧客検索は未追加顧客だけを追加可能にする", MatchingSearchAllowsOnlyUnrelatedCustomer),
    ("Matching顧客検索は未処理候補を再追加しない", MatchingSearchPendingRelationIsReadOnly),
    ("Matching顧客検索は保留候補を再追加しない", MatchingSearchHoldRelationIsReadOnly),
    ("Matching顧客検索は別人判定済み候補を再追加しない", MatchingSearchRejectedRelationIsReadOnly),
    ("Matching顧客検索は仮統合グループの構成顧客を再追加しない", MatchingSearchTemporaryMemberIsReadOnly),
    ("Matching顧客検索は論理顧客グループの構成顧客を再追加しない", MatchingSearchLogicalMemberIsReadOnly),
    ("Matching顧客検索はごみ箱顧客を追加しない", MatchingSearchTrashRelationIsReadOnly),
    ("Matching顧客検索は現在の対象顧客を追加しない", MatchingSearchCurrentCustomerIsReadOnly),
    ("Matching顧客検索の関係表示は検索だけでは状態を変更しない", MatchingSearchProjectionIsReadOnly),
    ("Matching手動顧客候補は逆向き関係を重複追加しない", MatchingManualCandidateRelationIsUndirected),
    ("代表候補承認で作られた重複Obsoleteは構成変更でPendingへ戻る", DuplicateRecommendationReconcilesAfterMembershipChange),
    ("同一グループ内の顧客Approvedは構成顧客追加で維持する", ApprovedCustomerRecommendationSurvivesMemberExpansion),
    ("同一グループ内の顧客Approvedは別顧客解除で維持する", ApprovedCustomerRecommendationSurvivesUnrelatedMemberRemoval),
    ("複数顧客解除でも残存顧客のApprovedを維持する", ApprovedCustomerRecommendationSurvivesMultipleMemberRemoval),
    ("Matching顧客統合は既存グループを維持して候補を順次追加する", MatchingCustomerMergeKeepsExistingGroupMembers),
    ("Matching顧客統合はRecommendationのSubjectとTargetの向きを正規化する", MatchingCustomerMergeNormalizesRecommendationDirection),
    ("再開失敗時は予定済みチェックポイント保存を再登録する", ResumeFailureReschedulesPendingCheckpoint),
    ("顧客統合判定は作業対象グループと外部候補顧客で維持される", CustomerRecommendationScopeSurvivesMemberExpansion),
    ("旧チェックポイントv1はおすすめ状態を空で補完して再開できる", LegacyCheckpointUpgrade),
    ("旧チェックポイントの顧客二重所属を正規化して再開できる", LegacyCheckpointMergeMembershipRecovery),
    ("旧チェックポイントの消滅グループ参照を有効な顧客スコープへ移行する", LegacyCheckpointGroupReferencesMigrate),
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

static void CustomerRejectedChangeDetailsUseCustomerRoles()
{
    var details = LegacyMatchingWorkflow.BuildCustomerRejectedChangeDetails(
        "上田 いずみ",
        "上田 茂",
        "未処理");

    Assert(details ==
           "確認中の顧客: 上田 いずみ\n" +
           "別人と判断した候補: 上田 茂\n" +
           "元の判定: 未処理 / 元に戻せます。",
        "顧客同士の別人判定履歴が対象・紐付け先の曖昧な表示になっています。");
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

    var held = new LegacyGraphRecommendationState(
        AbacusRecommendationDecisionValues.Hold,
        LegacyGraphRecommendationLifecycle.Obsolete,
        LegacyGraphRecommendationLifecycleReconciler.TemporaryManualLinkObsoleteReason,
        DateTimeOffset.UtcNow,
        "customer:c1",
        "customer:c2");
    var reconciledHold = LegacyGraphRecommendationLifecycleReconciler.ReconcileCurrentCandidate(
        held,
        DateTimeOffset.UtcNow);
    Assert(reconciledHold is not null &&
           reconciledHold.Lifecycle == LegacyGraphRecommendationLifecycle.Active &&
           reconciledHold.Decision == AbacusRecommendationDecisionValues.Hold,
        "保留から一時Obsoleteになった候補の保留判定を再成立時に失っています。");

    var temporaryRejected = new LegacyGraphRecommendationState(
        AbacusRecommendationDecisionValues.Rejected,
        LegacyGraphRecommendationLifecycle.Obsolete,
        LegacyGraphRecommendationLifecycleReconciler.TemporaryManualLinkObsoleteReason,
        DateTimeOffset.UtcNow,
        "customer:c1",
        "customer:c2");
    var keptTemporaryRejected = LegacyGraphRecommendationLifecycleReconciler.ReconcileCurrentCandidate(
        temporaryRejected,
        DateTimeOffset.UtcNow);
    Assert(keptTemporaryRejected is not null &&
           keptTemporaryRejected.Lifecycle == LegacyGraphRecommendationLifecycle.Obsolete &&
           keptTemporaryRejected.Decision == AbacusRecommendationDecisionValues.Rejected,
        "却下済み候補を一時Obsolete理由だけで再表示対象へ戻しています。");
}

static void DetachedUndoStateGroupReferencesMigrate()
{
    var undoState = new LegacyGraphDetachedUndoState(
        "document",
        "document-1",
        "customer:b",
        true,
        [
            new LegacyGraphDetachedDocumentState(
                "document-1",
                "vehicle-b",
                "logical:a",
                "manual-customer-only",
                "ユーザーが選択",
                false,
                true,
                false),
            new LegacyGraphDetachedDocumentState(
                "document-2",
                null,
                "logical:unrelated",
                "automatic",
                null,
                false,
                true,
                false),
        ]);

    var migrated = LegacyGraphDetachedUndoStateMigration.MigrateCustomerGroupReferences(
        undoState,
        new HashSet<string>(["logical:a", "customer:a"], StringComparer.Ordinal),
        "logical:b");

    Assert(migrated.Documents[0].ManualCustomerGroupKey == "logical:b" &&
           migrated.Documents[1].ManualCustomerGroupKey == "logical:unrelated" &&
           migrated.ManualVehicleCustomerId == "customer:b",
        "顧客統合後のUndoで、対象グループだけを新しい論理顧客へ移行できていません。");
}

static void ApprovedCustomerDocumentConnectionNeedsReview()
{
    var nextState = LegacyGraphCustomerReviewStateTransition.MarkNeedsReview(
        LegacyGraphCustomerReviewStateValues.Approved);
    Assert(nextState == LegacyGraphCustomerReviewStateValues.NeedsReview,
        "確認済み顧客へ書類を追加したとき、顧客確認状態を再確認待ちへ戻せません。");
}

static void ApprovedCustomerVehicleConnectionNeedsReview()
{
    var nextState = LegacyGraphCustomerReviewStateTransition.MarkNeedsReview(
        LegacyGraphCustomerReviewStateValues.Approved);
    Assert(nextState == LegacyGraphCustomerReviewStateValues.NeedsReview,
        "確認済み顧客へ車両を追加したとき、顧客確認状態を再確認待ちへ戻せません。");
}

static void OriginalCustomerVehicleUndoDoesNotBecomeManualLink()
{
    Assert(LegacyGraphVehicleUndoState.ResolveManualCustomerId(
                "customer:a",
                hasOriginalCustomer: true,
                originalCustomerId: "customer:a") is null,
        "元CSVで顧客Aに所属する車両をUndoしたとき、Aへの手動リンクを作成しています。");
    Assert(LegacyGraphVehicleUndoState.ResolveManualCustomerId(
                "customer:b",
                hasOriginalCustomer: true,
                originalCustomerId: "customer:a") == "customer:b",
        "顧客Aから顧客Bへ手動接続した車両のUndoで、Bへの手動リンクを復元できません。");
}

static void VehicleDetachOnlyClearsManualDocumentLink()
{
    Assert(LegacyGraphVehicleDetachState.IsManualDocumentLinkedToVehicle("vehicle-1", "vehicle-1"),
        "車両解除時に対象車両への手動書類リンクを解除対象として判定できていません。");
    Assert(!LegacyGraphVehicleDetachState.IsManualDocumentLinkedToVehicle("vehicle-2", "vehicle-1"),
        "別車両への手動書類リンクまで解除対象として判定しています。");
    Assert(!LegacyGraphVehicleDetachState.IsManualDocumentLinkedToVehicle(null, "vehicle-1"),
        "手動リンクのない書類を解除対象として判定しています。");
}

static void ManualDocumentUndoRestoresCurrentCustomer()
{
    var undoState = new LegacyGraphDetachedUndoState(
        "document",
        "document-1",
        null,
        true,
        [
            new LegacyGraphDetachedDocumentState(
                "document-1",
                null,
                "customer:b",
                "manual-customer-only",
                "ユーザーが選択",
                false,
                true,
                false),
        ]);

    var restored = LegacyGraphDetachedUndoStateMigration.MigrateCustomerGroupReferences(
        undoState,
        new HashSet<string>(["customer:a"], StringComparer.Ordinal),
        "logical:b");
    Assert(restored.Documents.Single().ManualCustomerGroupKey == "customer:b",
        "AからBへ手動接続した書類のUndoで、直前のBへの接続を保持できていません。");
}

static void TrashRecommendationEndpointsAreInactive()
{
    var emptyCustomers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var emptyVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var emptyDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var customerCandidate = EntityRecommendationCandidate(
        "trash-customer",
        AbacusRecommendationEntityKinds.Document,
        "document-1",
        AbacusRecommendationEntityKinds.Customer,
        "customer-1");
    var vehicleCandidate = EntityRecommendationCandidate(
        "trash-vehicle",
        AbacusRecommendationEntityKinds.Document,
        "document-1",
        AbacusRecommendationEntityKinds.Vehicle,
        "vehicle-1");
    var documentCandidate = EntityRecommendationCandidate(
        "trash-document",
        AbacusRecommendationEntityKinds.Document,
        "document-1",
        AbacusRecommendationEntityKinds.Customer,
        "customer-1");

    Assert(!LegacyGraphRecommendationAvailability.AreEndpointsActive(
            customerCandidate,
            new HashSet<string>(["customer-1"], StringComparer.OrdinalIgnoreCase),
            emptyVehicles,
            emptyDocuments),
        "ごみ箱内の顧客をRecommendationの接続先として有効扱いしています。");
    Assert(!LegacyGraphRecommendationAvailability.AreEndpointsActive(
            vehicleCandidate,
            emptyCustomers,
            new HashSet<string>(["vehicle-1"], StringComparer.OrdinalIgnoreCase),
            emptyDocuments),
        "ごみ箱内の車両をRecommendationの接続先として有効扱いしています。");
    Assert(!LegacyGraphRecommendationAvailability.AreEndpointsActive(
            documentCandidate,
            emptyCustomers,
            emptyVehicles,
            new HashSet<string>(["document-1"], StringComparer.OrdinalIgnoreCase)),
        "ごみ箱内の書類をRecommendationの対象として有効扱いしています。");
}

static void GraphAndMatchingApprovalShareDomainState()
{
    // Graph/Matchingの両UIは同じApproveLegacyGraphCustomer経路を通るため、
    // 最終的なドメイン状態は共通の状態遷移結果になる。
    var graphResult = LegacyGraphCustomerReviewStateTransition.MarkApproved();
    var matchingResult = LegacyGraphCustomerReviewStateTransition.MarkApproved();
    Assert(graphResult == LegacyGraphCustomerReviewStateValues.Approved &&
           matchingResult == graphResult,
        "Graph UIとMatching UIの顧客確定結果が共通のApproved状態になっていません。");
}

static void CustomerApprovalResolutionDoesNotBecomeIndividualHistory()
{
    var resolved = new LegacyGraphRecommendationState(
        AbacusRecommendationDecisionValues.Rejected,
        LegacyGraphRecommendationLifecycle.Active,
        LegacyGraphRecommendationLifecycleReconciler.CustomerApprovalResolutionReason,
        DateTimeOffset.UtcNow);
    Assert(resolved.Decision == AbacusRecommendationDecisionValues.Rejected &&
           resolved.ResolutionReason == LegacyGraphRecommendationLifecycleReconciler.CustomerApprovalResolutionReason &&
           resolved.ResolutionReason != "ユーザーが別人と判断しました。",
        "顧客確定に伴う一括解決を個別の別人判定理由として保存しています。");
}

static void ResumeValidationFailureDoesNotMutateCheckpoint()
{
    var directory = Path.Combine(Path.GetTempPath(), $"gate28-invalid-checkpoint-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "graph-state.json");
    var invalidJson = "{\"kind\":\"invalid\"";
    File.WriteAllText(path, invalidJson);
    try
    {
        var before = File.ReadAllBytes(path);
        var failed = false;
        try
        {
            _ = new LegacyGraphWorkCheckpointStore().ReadAsync(directory).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            failed = true;
        }

        Assert(failed, "不正な作業チェックポイントを検証失敗として扱っていません。");
        Assert(before.SequenceEqual(File.ReadAllBytes(path)),
            "作業再開の検証失敗で保存済みチェックポイントを書き換えています。");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void ManualDocumentOwnerUsesCurrentCustomer()
{
    var directOwner = LegacyGraphDocumentOwnership.ResolveCurrentCustomerId(
        null,
        "customer-b",
        "customer-a",
        "customer-a");
    var vehicleOwner = LegacyGraphDocumentOwnership.ResolveCurrentCustomerId(
        "customer-c",
        "customer-b",
        "customer-a",
        "customer-a");
    Assert(directOwner == "customer-b" && vehicleOwner == "customer-c",
        "手動移動後の書類所有者を現在の接続先より元顧客へ戻しています。");
}

static void CurrentDocumentVehicleResolutionPrefersManualLink()
{
    Assert(LegacyGraphDocumentOwnership.ResolveCurrentVehicleId(
                "vehicle-2",
                "vehicle-1",
                "vehicle-1") == "vehicle-2",
        "手動で車両V2へ移した書類を、元の車両V1の所属として扱っています。");
    Assert(LegacyGraphDocumentOwnership.ResolveCurrentVehicleId(
                null,
                "vehicle-2",
                "vehicle-1") == "vehicle-2",
        "現在の書類車両リンクを元の車両より優先できていません。");
    Assert(LegacyGraphDocumentOwnership.ResolveCurrentVehicleId(
                null,
                null,
                "vehicle-1") == "vehicle-1",
        "手動・現在リンクがない書類の元車両を解決できていません。");
    Assert(LegacyGraphDocumentOwnership.ResolveCurrentVehicleId(
                null,
                "vehicle-1",
                "vehicle-1",
                hasManualCustomerOnlyLink: true) is null,
        "顧客直結へ変更した書類を、元車両の現在所属として扱っています。");
}

static void FinalPackageUsesCurrentDocumentOwnership()
{
    var root = Path.Combine(Path.GetTempPath(), $"gate28-current-ownership-{Guid.NewGuid():N}");
    var sourceRoot = Path.Combine(root, "source");
    var destinationRoot = Path.Combine(root, "destination");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(destinationRoot);
    File.WriteAllText(Path.Combine(sourceRoot, "manifest.json"), "{}");
    try
    {
        const string documentKey = "販売書類|sales.csv|1|D1";
        var document = new AbacusLegacyExportCandidateGraphDocument(
            "販売書類",
            "sales.csv",
            1,
            "D1",
            "顧客A",
            "V1",
            "",
            "2024-01-01",
            "1000",
            "一意一致",
            "",
            "v1",
            [],
            []);
        var vehicle1 = new AbacusLegacyExportCandidateGraphVehicle(
            "v1", "a", "顧客A", "", "V1", "", "", "", "", "", [document]);
        var vehicle2 = new AbacusLegacyExportCandidateGraphVehicle(
            "v2", "b", "顧客B", "", "V2", "", "", "", "", "", []);
        var customerA = new AbacusLegacyExportCandidateGraphCustomer(
            "a", "A", "顧客A", "えー", "", "", "", "", "", [vehicle1], []);
        var customerB = new AbacusLegacyExportCandidateGraphCustomer(
            "b", "B", "顧客B", "びー", "", "", "", "", "", [vehicle2], []);
        var graph = new AbacusLegacyExportCandidateGraphResult(
            sourceRoot,
            [customerA, customerB],
            [document],
            [],
            [],
            1,
            0,
            0,
            []);
        var groups = new[]
        {
            new AbacusLegacyGraphFinalCustomerGroup(
                "customer:a", "single", true, ["a"], "a", "A", "顧客A", "えー", "", "", "", "", ""),
            new AbacusLegacyGraphFinalCustomerGroup(
                "customer:b", "single", true, ["b"], "b", "B", "顧客B", "びー", "", "", "", "", ""),
        };

        AbacusLegacyGraphFinalizationSnapshot Snapshot(
            IReadOnlyCollection<string>? trayVehicles = null,
            IReadOnlyCollection<string>? excludedVehicles = null,
            IReadOnlyCollection<string>? excludedCustomers = null) =>
            new(
                groups,
                new Dictionary<string, string> { [documentKey] = "v2" },
                new Dictionary<string, string>(),
                [],
                true,
                TrayVehicleIds: trayVehicles,
                ExplicitExcludedVehicleIds: excludedVehicles,
                ExplicitExcludedCustomerIds: excludedCustomers);

        var store = new AbacusLegacyGraphFinalPackageStore();
        var originalVehicleTrashed = store.CreateAsync(
                graph,
                Snapshot(trayVehicles: ["v1"]),
                destinationRoot)
            .GetAwaiter()
            .GetResult();
        Assert(originalVehicleTrashed.Documents is [{ VehicleId: "v2" }] &&
               originalVehicleTrashed.ExcludedDocumentCount == 0,
            "元車両V1をトレイへ移しただけで、現在V2の書類を除外しています。");

        var currentVehicleTrashed = store.CreateAsync(
                graph,
                Snapshot(excludedVehicles: ["v2"]),
                destinationRoot)
            .GetAwaiter()
            .GetResult();
        Assert(currentVehicleTrashed.Documents.Count == 0 &&
               currentVehicleTrashed.ExcludedDocumentCount == 1,
            "現在所属する車両V2のごみ箱移動で、書類を除外できていません。");

        var originalCustomerTrashed = store.CreateAsync(
                graph,
                Snapshot(excludedCustomers: ["a"]),
                destinationRoot)
            .GetAwaiter()
            .GetResult();
        Assert(originalCustomerTrashed.Documents is [{ VehicleId: "v2" }] &&
               originalCustomerTrashed.ExcludedDocumentCount == 0,
            "元顧客Aを除外しただけで、現在顧客Bの書類を除外しています。");

        var currentCustomerTrashed = store.CreateAsync(
                graph,
                Snapshot(excludedCustomers: ["b"]),
                destinationRoot)
            .GetAwaiter()
            .GetResult();
        Assert(currentCustomerTrashed.Documents.Count == 0 &&
               currentCustomerTrashed.ExcludedDocumentCount == 1,
            "現在顧客Bのごみ箱移動で、書類を除外できていません。");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void ManualVehicleCustomerOwnershipIsCurrent()
{
    var currentCustomer = LegacyGraphDocumentOwnership.ResolveCurrentCustomerId(
        "customer:b",
        null,
        null,
        "customer:a");
    Assert(currentCustomer == "customer:b",
        "顧客なし車両をBへ手動接続した書類を、現在顧客Bとして解決できていません。");
    Assert(LegacyGraphDocumentOwnership.ResolveCurrentCustomerId(
                "customer:b",
                null,
                null,
                "customer:a") is not null,
        "顧客なし車両への手動接続を、Recommendation適用可能な現在顧客として扱えていません。");
}

static void CheckpointSaveDefersWhileBusy()
{
    Assert(!LegacyGraphCheckpointSaveState.CanStart(
                resumeInProgress: false,
                bulkMergeBusy: true,
                finalPackageBusy: false) &&
           !LegacyGraphCheckpointSaveState.CanStart(
                resumeInProgress: false,
                bulkMergeBusy: false,
                finalPackageBusy: true) &&
           !LegacyGraphCheckpointSaveState.CanStart(
                resumeInProgress: true,
                bulkMergeBusy: false,
                finalPackageBusy: false) &&
           LegacyGraphCheckpointSaveState.CanStart(false, false, false),
        "Bulk・最終出力・再開中のチェックポイント保存開始ゲートが機能していません。");
}

static void ConfirmedGroupSplitMovesReviewState()
{
    var reviewStates = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["logical:x"] = LegacyGraphCustomerReviewStateValues.Approved,
    };
    var approvalStates = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["logical:x"] = true,
    };

    var standaloneKey = LegacyGraphCustomerReviewStateTransition.MoveGroupToStandaloneCustomer(
        reviewStates,
        approvalStates,
        "logical:x",
        "a",
        wasApproved: true);

    Assert(standaloneKey == "customer:a" &&
           !reviewStates.ContainsKey("logical:x") &&
           !approvalStates.ContainsKey("logical:x") &&
           reviewStates["customer:a"] == LegacyGraphCustomerReviewStateValues.NeedsReview &&
           approvalStates["customer:a"] == false,
        "論理グループを構成顧客1件へ戻した後、現在の単独顧客キーへ再確認状態を移せていません。");
    Assert(LegacyGraphTemporaryMergeGroupState.HasActiveMembership(2),
        "構成顧客が2件残るグループまで単独顧客へ解消扱いしています。");
}

static void UnapprovedGroupSplitMovesToUnreviewed()
{
    var reviewStates = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["logical:x"] = LegacyGraphCustomerReviewStateValues.Unreviewed,
    };
    var approvalStates = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["logical:x"] = false,
    };

    LegacyGraphCustomerReviewStateTransition.MoveGroupToStandaloneCustomer(
        reviewStates,
        approvalStates,
        "logical:x",
        "a",
        wasApproved: false);

    Assert(reviewStates["customer:a"] == LegacyGraphCustomerReviewStateValues.Unreviewed &&
           approvalStates["customer:a"] == false,
        "未承認の論理グループ分解を再確認待ちへ誤って変更しています。");
}

static void ManualCustomerCandidateMutationGuard()
{
    var targets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
    {
        ["source"] = new HashSet<string>(["existing"], StringComparer.Ordinal),
    };
    foreach (var flags in new[]
             {
                 (Bulk: true, FinalPackage: false, Resume: false),
                 (Bulk: false, FinalPackage: true, Resume: false),
                 (Bulk: false, FinalPackage: false, Resume: true),
             })
    {
        var added = LegacyGraphMutationState.TryAddManualCustomerCandidate(
            targets,
            "source",
            "blocked",
            flags.Bulk,
            flags.FinalPackage,
            flags.Resume);
        Assert(!added && !targets["source"].Contains("blocked") && targets.Count == 1,
            "Busy中の手動顧客候補追加が対象Dictionaryを変更しています。");
    }

    Assert(LegacyGraphMutationState.TryAddManualCustomerCandidate(
                targets,
                "source",
                "target",
                false,
                false,
                false) &&
           targets["source"].Contains("target"),
        "通常時の手動顧客候補追加を実行できません。");
    Assert(!LegacyGraphMutationState.TryAddManualCustomerCandidate(
                targets,
                "source",
                "target",
                false,
                false,
                false),
        "同じ手動顧客候補を重複追加できてしまいます。");
}

static void MatchingSearchAllowsOnlyUnrelatedCustomer()
{
    var relation = LegacyMatchingCustomerSearchRelationState.Resolve(
        isTrash: false,
        isCurrentCustomer: false,
        isLogicalMember: false,
        isTemporaryMember: false,
        recommendationDecision: null);

    Assert(relation == LegacyMatchingCustomerSearchRelation.None &&
           LegacyMatchingCustomerSearchRelationState.CanAdd(relation),
        "関係のない顧客をMatching検索から追加できません。");
}

static void MatchingSearchPendingRelationIsReadOnly()
{
    var relation = LegacyMatchingCustomerSearchRelationState.Resolve(
        false,
        false,
        false,
        false,
        AbacusRecommendationDecisionValues.Pending);

    Assert(relation == LegacyMatchingCustomerSearchRelation.Pending &&
           !LegacyMatchingCustomerSearchRelationState.CanAdd(relation),
        "未処理候補をMatching検索から再追加できます。");
}

static void MatchingSearchHoldRelationIsReadOnly()
{
    var relation = LegacyMatchingCustomerSearchRelationState.Resolve(
        false,
        false,
        false,
        false,
        AbacusRecommendationDecisionValues.Hold);

    Assert(relation == LegacyMatchingCustomerSearchRelation.Hold &&
           !LegacyMatchingCustomerSearchRelationState.CanAdd(relation),
        "保留候補をMatching検索から再追加できます。");
}

static void MatchingSearchRejectedRelationIsReadOnly()
{
    var relation = LegacyMatchingCustomerSearchRelationState.Resolve(
        false,
        false,
        false,
        false,
        AbacusRecommendationDecisionValues.Rejected);

    Assert(relation == LegacyMatchingCustomerSearchRelation.Rejected &&
           !LegacyMatchingCustomerSearchRelationState.CanAdd(relation),
        "別人判定済み候補をMatching検索から再追加できます。");
}

static void MatchingSearchTemporaryMemberIsReadOnly()
{
    var relation = LegacyMatchingCustomerSearchRelationState.Resolve(
        false,
        false,
        false,
        true,
        AbacusRecommendationDecisionValues.Pending);

    Assert(relation == LegacyMatchingCustomerSearchRelation.TemporaryMember &&
           !LegacyMatchingCustomerSearchRelationState.CanAdd(relation),
        "仮統合グループの構成顧客を未処理候補として再追加できます。");
}

static void MatchingSearchLogicalMemberIsReadOnly()
{
    var relation = LegacyMatchingCustomerSearchRelationState.Resolve(
        false,
        false,
        true,
        false,
        AbacusRecommendationDecisionValues.Approved);

    Assert(relation == LegacyMatchingCustomerSearchRelation.LogicalMember &&
           !LegacyMatchingCustomerSearchRelationState.CanAdd(relation),
        "論理顧客グループの構成顧客を未処理候補として再追加できます。");
}

static void MatchingSearchTrashRelationIsReadOnly()
{
    var relation = LegacyMatchingCustomerSearchRelationState.Resolve(
        true,
        false,
        false,
        false,
        null);

    Assert(relation == LegacyMatchingCustomerSearchRelation.Trash &&
           !LegacyMatchingCustomerSearchRelationState.CanAdd(relation),
        "ごみ箱顧客をMatching検索から追加できます。");
}

static void MatchingSearchCurrentCustomerIsReadOnly()
{
    var relation = LegacyMatchingCustomerSearchRelationState.Resolve(
        false,
        true,
        false,
        false,
        null);

    Assert(relation == LegacyMatchingCustomerSearchRelation.CurrentCustomer &&
           !LegacyMatchingCustomerSearchRelationState.CanAdd(relation),
        "現在のMatching対象顧客を候補へ追加できます。");
}

static void MatchingSearchProjectionIsReadOnly()
{
    var targets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
    {
        ["current"] = new HashSet<string>(["candidate"], StringComparer.Ordinal),
    };
    var before = targets["current"].ToArray();
    var relation = LegacyMatchingCustomerSearchRelationState.Resolve(
        false,
        false,
        false,
        false,
        AbacusRecommendationDecisionValues.Pending);

    Assert(relation == LegacyMatchingCustomerSearchRelation.Pending &&
           targets["current"].SequenceEqual(before, StringComparer.Ordinal),
        "Matching検索の関係表示だけで候補状態が変更されています。");
}

static void MatchingManualCandidateRelationIsUndirected()
{
    var targets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
    {
        ["candidate"] = new HashSet<string>(["current"], StringComparer.Ordinal),
    };

    Assert(!LegacyGraphMutationState.TryAddManualCustomerCandidate(
                targets,
                "current",
                "candidate",
                false,
                false,
                false) &&
           !LegacyGraphMutationState.TryAddManualCustomerCandidate(
               targets,
               "current",
               "current",
               false,
               false,
               false),
        "手動顧客候補の逆向きまたは自己関係を重複登録できます。");

    Assert(LegacyGraphMutationState.HasUndirectedManualCustomerCandidate(
               targets,
               ["current", "other-member"],
               "candidate"),
        "論理顧客の作業対象から既存の手動候補関係を検出できません。");
}

static void GraphStructuralMergeMembershipIgnoresMatchingReject()
{
    var candidate = RecommendationCandidate("candidate-rejected", "customer-a");
    var decisions = new Dictionary<string, string>
    {
        [candidate.CandidateId] = AbacusRecommendationDecisionValues.Rejected,
    };

    Assert(LegacyMatchingWorkflow.AreAllCandidatesRejected([candidate], decisions),
        "マッチング側の個別Reject判定を確認できません。");
    Assert(LegacyGraphTemporaryMergeGroupState.IsPending(2, false, false),
        "同じ候補がReject済みでも、構成顧客2件のグラフ仮統合を維持できません。");
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

static void TrashStandaloneCustomerDoesNotBlockFinalPackage()
{
    var root = Path.Combine(Path.GetTempPath(), $"gate28-trash-customer-{Guid.NewGuid():N}");
    var sourceRoot = Path.Combine(root, "source");
    var destinationRoot = Path.Combine(root, "destination");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(destinationRoot);
    File.WriteAllText(Path.Combine(sourceRoot, "manifest.json"), "{}");
    try
    {
        var activeCustomer = new AbacusLegacyExportCandidateGraphCustomer(
            "c1", "number-1", "山田太郎", "やまだたろう", "", "", "", "", "", [], []);
        var trashCustomer = new AbacusLegacyExportCandidateGraphCustomer(
            "c2", "number-2", "佐藤花子", "さとうはなこ", "", "", "", "", "", [], []);
        var graph = new AbacusLegacyExportCandidateGraphResult(
            sourceRoot,
            [activeCustomer, trashCustomer],
            [],
            [],
            [],
            0,
            0,
            0,
            []);
        var activeGroup = new AbacusLegacyGraphFinalCustomerGroup(
            "customer:c1", "single", true, ["c1"], "c1", "number-1", "山田太郎", "やまだたろう", "", "", "", "", "");
        var trashGroup = new AbacusLegacyGraphFinalCustomerGroup(
            "customer:c2", "single", false, ["c2"], "c2", "number-2", "佐藤花子", "さとうはなこ", "", "", "", "", "");
        var snapshot = new AbacusLegacyGraphFinalizationSnapshot(
            [activeGroup, trashGroup],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [],
            true,
            ExplicitExcludedCustomerIds: ["c2"]);

        var result = new AbacusLegacyGraphFinalPackageStore()
            .CreateAsync(graph, snapshot, destinationRoot)
            .GetAwaiter()
            .GetResult();
        Assert(result.CustomerRowCount == 1 && result.ExcludedCustomerCount == 1,
            "ごみ箱へ移した単独顧客を除外し、有効顧客だけの最終パッケージを作成できていません。");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void TrashCustomerMixedGroupIsRejectedAtPackageBoundary()
{
    var root = Path.Combine(Path.GetTempPath(), $"gate28-trash-mixed-group-{Guid.NewGuid():N}");
    var sourceRoot = Path.Combine(root, "source");
    var destinationRoot = Path.Combine(root, "destination");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(destinationRoot);
    File.WriteAllText(Path.Combine(sourceRoot, "manifest.json"), "{}");
    try
    {
        var customers = new[]
        {
            new AbacusLegacyExportCandidateGraphCustomer(
                "c1", "number-1", "山田太郎", "やまだたろう", "", "", "", "", "", [], []),
            new AbacusLegacyExportCandidateGraphCustomer(
                "c2", "number-2", "佐藤花子", "さとうはなこ", "", "", "", "", "", [], []),
        };
        var graph = new AbacusLegacyExportCandidateGraphResult(
            sourceRoot, customers, [], [], [], 0, 0, 0, []);
        var mixedGroup = new AbacusLegacyGraphFinalCustomerGroup(
            "logical:mixed", "logical", true, ["c1", "c2"], "c1", "number-1", "山田太郎", "やまだたろう", "", "", "", "", "");
        var snapshot = new AbacusLegacyGraphFinalizationSnapshot(
            [mixedGroup],
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            [],
            true,
            ExplicitExcludedCustomerIds: ["c2"]);

        var rejected = false;
        try
        {
            new AbacusLegacyGraphFinalPackageStore()
                .CreateAsync(graph, snapshot, destinationRoot)
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Assert(rejected,
            "有効顧客とごみ箱顧客が同じ論理顧客グループに残った状態を最終出力で許可しています。");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void LegacyGraphMutationGuard()
{
    Assert(LegacyGraphMutationState.CanMutate(false, false, false),
        "通常時のグラフ操作をMutation Guardが禁止しています。");
    Assert(!LegacyGraphMutationState.CanMutate(true, false, false),
        "一括確定中にユーザー操作を受け付ける状態です。");
    Assert(!LegacyGraphMutationState.CanMutate(false, true, false),
        "最終パッケージ作成中にユーザー操作を受け付ける状態です。");
    Assert(!LegacyGraphMutationState.CanMutate(false, false, true),
        "作業再開中にユーザー操作を受け付ける状態です。");
}

static void FinalPackageCompletionRequiresStableConfirmation()
{
    Assert(LegacyGraphFinalPackageState.CanComplete(true, true),
        "確定状態が維持された場合に最終パッケージを完成可能と判定できていません。");
    Assert(!LegacyGraphFinalPackageState.CanComplete(true, false),
        "パッケージ作成中に確定状態が解除された場合も完成可能になっています。");
    Assert(!LegacyGraphFinalPackageState.CanComplete(false, true),
        "未確定スナップショットを最終パッケージとして完成可能にしています。");
}

static void GraphTemporaryMergeGroupUsesMembershipOnly()
{
    Assert(LegacyGraphTemporaryMergeGroupState.IsPending(2, false, false),
        "2件の有効な非論理顧客グループをグラフ仮統合として扱えていません。");
    Assert(!LegacyGraphTemporaryMergeGroupState.IsPending(1, false, false),
        "構成顧客が1件だけのグループをグラフ仮統合として扱っています。");
    Assert(!LegacyGraphTemporaryMergeGroupState.IsPending(2, true, false),
        "論理顧客グループをグラフ仮統合として扱っています。");
    Assert(!LegacyGraphTemporaryMergeGroupState.IsPending(2, false, true),
        "適用済みグループをグラフ仮統合として扱っています。");
}

static void MatchingMergeMemberSelectionIsScoped()
{
    var group = new[] { "customer-a", "customer-b" };
    Assert(LegacyGraphMatchingSelectionState.CanRemoveMergeMember(
            "customer-b",
            "customer-a",
            group,
            canMutate: true),
        "基準顧客以外の構成顧客を解除可能と判定できていません。");
    Assert(!LegacyGraphMatchingSelectionState.CanRemoveMergeMember(
            "customer-a",
            "customer-a",
            group,
            canMutate: true),
        "マッチングの基準顧客を統合から外せる状態です。");
    Assert(!LegacyGraphMatchingSelectionState.CanRemoveMergeMember(
            "customer-c",
            "customer-a",
            group,
            canMutate: true),
        "現在のグループにいない顧客を解除可能と判定しています。");
}

static void DuplicateRecommendationReconcilesAfterMembershipChange()
{
    var obsolete = new LegacyGraphRecommendationState(
        AbacusRecommendationDecisionValues.Rejected,
        LegacyGraphRecommendationLifecycle.Obsolete,
        LegacyGraphRecommendationLifecycleReconciler.DuplicateCustomerRecommendationObsoleteReason,
        DateTimeOffset.UtcNow);
    var restored = LegacyGraphRecommendationLifecycleReconciler.ReconcileAfterMergeMembershipChange(
        obsolete,
        DateTimeOffset.UtcNow.AddSeconds(1));

    Assert(restored.Lifecycle == LegacyGraphRecommendationLifecycle.Active &&
           restored.Decision == AbacusRecommendationDecisionValues.Pending &&
           restored.ResolutionReason is null,
        "構成変更後に内部duplicate候補を再評価してPendingへ戻せていません。");

    var held = obsolete with { Decision = AbacusRecommendationDecisionValues.Hold };
    var heldAfterReconcile = LegacyGraphRecommendationLifecycleReconciler.ReconcileAfterMergeMembershipChange(
        held,
        DateTimeOffset.UtcNow.AddSeconds(1));
    Assert(heldAfterReconcile.Lifecycle == LegacyGraphRecommendationLifecycle.Obsolete &&
           heldAfterReconcile.Decision == AbacusRecommendationDecisionValues.Hold,
        "保留中の候補を構成変更の内部duplicate復帰でPendingへ戻しています。");
}

static void ApprovedCustomerRecommendationSurvivesMemberExpansion()
{
    var group = new HashSet<string>(["F", "U"], StringComparer.Ordinal);
    var decisions = new Dictionary<string, string>
    {
        ["F-U"] = AbacusRecommendationDecisionValues.Approved,
    };

    group.Add("S");
    ReconcileCustomerRecommendationDecisions(decisions, group);

    Assert(decisions["F-U"] == AbacusRecommendationDecisionValues.Approved,
        "別の顧客を仮グループへ追加しただけで、既存の顧客統合Approvedが解除されています。");
}

static void ApprovedCustomerRecommendationSurvivesUnrelatedMemberRemoval()
{
    var group = new HashSet<string>(["F", "U", "S"], StringComparer.Ordinal);
    var decisions = new Dictionary<string, string>
    {
        ["F-U"] = AbacusRecommendationDecisionValues.Approved,
        ["F-S"] = AbacusRecommendationDecisionValues.Approved,
    };

    group.Remove("S");
    ReconcileCustomerRecommendationDecisions(decisions, group);

    Assert(decisions["F-U"] == AbacusRecommendationDecisionValues.Approved &&
           decisions["F-S"] == AbacusRecommendationDecisionValues.Pending,
        "別顧客を外したとき、残存顧客のApproved維持と外した顧客の再評価を分離できていません。");
}

static void ApprovedCustomerRecommendationSurvivesMultipleMemberRemoval()
{
    var group = new HashSet<string>(["F", "U", "S1", "S2"], StringComparer.Ordinal);
    var decisions = new Dictionary<string, string>
    {
        ["F-U"] = AbacusRecommendationDecisionValues.Approved,
        ["F-S1"] = AbacusRecommendationDecisionValues.Approved,
        ["F-S2"] = AbacusRecommendationDecisionValues.Approved,
    };

    group.Remove("S1");
    ReconcileCustomerRecommendationDecisions(decisions, group);
    group.Remove("S2");
    ReconcileCustomerRecommendationDecisions(decisions, group);

    Assert(decisions["F-U"] == AbacusRecommendationDecisionValues.Approved &&
           decisions["F-S1"] == AbacusRecommendationDecisionValues.Pending &&
           decisions["F-S2"] == AbacusRecommendationDecisionValues.Pending,
        "複数顧客を順に外したとき、残存する顧客統合Approvedを維持できていません。");
}

static void ReconcileCustomerRecommendationDecisions(
    IDictionary<string, string> decisions,
    IReadOnlySet<string> groupCustomerIds)
{
    foreach (var pair in decisions.ToArray())
    {
        var endpoints = pair.Key.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var endpointsInSameGroup = endpoints.Length == 2 &&
                                   groupCustomerIds.Contains(endpoints[0]) &&
                                   groupCustomerIds.Contains(endpoints[1]);
        if (pair.Value == AbacusRecommendationDecisionValues.Approved &&
            !LegacyGraphCustomerRecommendationMembership.ShouldKeepApproved(endpointsInSameGroup))
        {
            decisions[pair.Key] = AbacusRecommendationDecisionValues.Pending;
        }
    }
}

static void ResumeFailureReschedulesPendingCheckpoint()
{
    Assert(LegacyGraphCheckpointSaveState.ShouldRescheduleAfterResumeFailure(true, true),
        "再開前に予定されていたチェックポイント保存を、再開失敗後に再登録できる判定になっていません。");
    Assert(!LegacyGraphCheckpointSaveState.ShouldRescheduleAfterResumeFailure(false, true),
        "再開成功時に不要なチェックポイント保存を再登録する判定です。");
    Assert(!LegacyGraphCheckpointSaveState.ShouldRescheduleAfterResumeFailure(true, false),
        "再開前に予定されていないチェックポイント保存を再登録する判定です。");
}

static void MatchingCustomerMergeKeepsExistingGroupMembers()
{
    var currentGroup = new HashSet<string>(["F"], StringComparer.Ordinal);
    var pendingCandidates = new HashSet<string>(["U", "S1", "S2", "S3", "S4"], StringComparer.Ordinal);
    var approvals = new[]
    {
        CustomerRecommendationCandidate("candidate-f-u", "F", "U"),
        CustomerRecommendationCandidate("candidate-f-s1", "F", "S1"),
        CustomerRecommendationCandidate("candidate-s2-u", "S2", "U"),
        CustomerRecommendationCandidate("candidate-u-s3", "U", "S3"),
    };

    foreach (var candidate in approvals)
    {
        Assert(LegacyGraphMatchingCustomerMergeDirection.TryResolve(
                candidate,
                currentGroup,
                out var mergeSourceCustomerId,
                out var mergeTargetCustomerId),
            $"{candidate.CandidateId}の統合方向を解決できていません。");
        Assert(currentGroup.Contains(mergeTargetCustomerId),
            $"{candidate.CandidateId}の統合先が現在の作業対象グループではありません。");
        Assert(!currentGroup.Contains(mergeSourceCustomerId),
            $"{candidate.CandidateId}の追加候補が既存グループ側として扱われています。");

        currentGroup.Add(mergeSourceCustomerId);
        pendingCandidates.Remove(mergeSourceCustomerId);
    }

    Assert(currentGroup.SetEquals(["F", "U", "S1", "S2", "S3"]),
        "顧客統合を順番に承認しても既存の構成顧客を維持できていません。");
    Assert(pendingCandidates.SetEquals(["S4"]),
        "順番に承認した後の未処理候補が追加候補1件だけになっていません。");
}

static void MatchingCustomerMergeNormalizesRecommendationDirection()
{
    foreach (var (subjectCustomerId, targetCustomerId) in new[]
             {
                 ("F", "S1"),
                 ("S1", "F"),
             })
    {
        var candidate = CustomerRecommendationCandidate(
            $"candidate-direction-{subjectCustomerId}-{targetCustomerId}",
            subjectCustomerId,
            targetCustomerId);
        var currentGroup = new HashSet<string>(["F", "U"], StringComparer.Ordinal);

        Assert(LegacyGraphMatchingCustomerMergeDirection.TryResolve(
                candidate,
                currentGroup,
                out var mergeSourceCustomerId,
                out var mergeTargetCustomerId),
            $"{subjectCustomerId}→{targetCustomerId}の統合方向を解決できていません。");
        Assert(mergeSourceCustomerId == "S1" && mergeTargetCustomerId == "F",
            $"{subjectCustomerId}→{targetCustomerId}が外部候補→現在グループへ正規化されていません。");

        currentGroup.Add(mergeSourceCustomerId);
        Assert(currentGroup.SetEquals(["F", "U", "S1"]),
            $"{subjectCustomerId}→{targetCustomerId}で既存グループを維持できていません。");
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

static void LegacyCheckpointGroupReferencesMigrate()
{
    var logicalGroupId = "logical:formal";
    var candidateGroupId = "same-name:消滅候補";
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
        new Dictionary<string, string>
        {
            ["document-dangling"] = candidateGroupId,
        },
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
            new LegacyGraphCheckpointMergeGroup(logicalGroupId, "logical", ["a", "b"]),
            new LegacyGraphCheckpointMergeGroup(candidateGroupId, "same-name", ["b", "c"]),
        ],
        new Dictionary<string, string>
        {
            ["b"] = candidateGroupId,
            ["c"] = candidateGroupId,
        },
        new Dictionary<string, LegacyGraphCheckpointMergeDraft>(),
        [logicalGroupId],
        new Dictionary<string, string>(),
        new Dictionary<string, bool>(),
        [],
        null,
        DateTimeOffset.UtcNow,
        RecommendationStates:
        [
            new LegacyGraphCheckpointRecommendationState(
                "candidate-regenerated",
                AbacusRecommendationDecisionValues.Hold,
                LegacyGraphRecommendationLifecycle.Active,
                null,
                DateTimeOffset.UtcNow,
                candidateGroupId,
                "customer:c"),
        ],
        MatchingChanges:
        [
            new LegacyGraphCheckpointMatchingChange(
                $"recommendation-scope\u001F{candidateGroupId}\u001Fcustomer:c",
                "recommendation",
                "candidate-regenerated",
                "保留",
                "テスト",
                DateTimeOffset.UtcNow,
                "customer:b",
                candidateGroupId,
                "customer:c",
                new LegacyGraphDetachedUndoState(
                    "document",
                    "document-dangling",
                    "customer:c",
                    true,
                    [
                        new LegacyGraphDetachedDocumentState(
                            "document-dangling",
                            null,
                            candidateGroupId,
                            "manual-customer-only",
                            "ユーザーが選択",
                            false,
                            false,
                            true),
                    ])),
        ],
        LogicalCustomerMergeGroupByCustomerId: new Dictionary<string, string>
        {
            ["a"] = logicalGroupId,
            ["b"] = logicalGroupId,
        },
        CustomerApprovalStates: new Dictionary<string, bool>
        {
            [logicalGroupId] = true,
        },
        CustomerReviewStates: new Dictionary<string, string>
        {
            [logicalGroupId] = LegacyGraphCustomerReviewStateValues.Approved,
        });

    var recovered = LegacyGraphWorkCheckpointRecovery.NormalizeMergeMembership(checkpoint).Checkpoint;
    Assert(recovered.MatchingChanges is { Length: 1 },
        "復元後の変更履歴がありません。");
    var matchingChange = recovered.MatchingChanges![0];
    Assert(matchingChange.UndoState?.Documents is { Count: 1 },
        "復元後のUndo書類がありません。");
    var undoDocument = matchingChange.UndoState!.Documents[0];
    Assert(recovered.CustomerMergeGroups.Length == 1 &&
           recovered.CustomerMergeGroups[0].GroupId == logicalGroupId &&
           recovered.ManualDocumentCustomerGroupLinks["document-dangling"] == "customer:c" &&
           recovered.RecommendationStates is [{ WorkTargetKey: "customer:c" }] &&
           matchingChange.WorkTargetKey == "customer:c" &&
           undoDocument.ManualCustomerGroupKey == "customer:c" &&
           !matchingChange.ChangeId.Contains(candidateGroupId, StringComparison.Ordinal),
        "消滅した候補グループを参照する状態が、現在の顧客スコープへ移行されていません。");
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

static AbacusRecommendationCandidate EntityRecommendationCandidate(
    string candidateId,
    string subjectKind,
    string subjectId,
    string targetKind,
    string targetId) =>
    new(
        candidateId,
        subjectKind,
        subjectId,
        targetKind,
        targetId,
        targetId,
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
            MatchingCustomerId = "customer:c1",
        };
        File.WriteAllText(Path.Combine(directory, "graph-state.json"), JsonSerializer.Serialize(
            reviewCheckpoint,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var reviewed = new LegacyGraphWorkCheckpointStore().ReadAsync(directory).GetAwaiter().GetResult();
        Assert(reviewed.CustomerReviewStates is not null &&
               reviewed.CustomerReviewStates.TryGetValue("customer:c1", out var reviewState) &&
               reviewState == LegacyGraphCustomerReviewStateValues.NeedsReview &&
               reviewed.MatchingCustomerId == "customer:c1",
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
