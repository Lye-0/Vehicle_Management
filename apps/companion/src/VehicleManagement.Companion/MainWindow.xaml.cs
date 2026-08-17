using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Line = System.Windows.Shapes.Line;
using Rectangle = System.Windows.Shapes.Rectangle;
using Microsoft.Win32;
using VehicleManagement.AbacusImport;
using VehicleManagement.Companion.Services;

namespace VehicleManagement.Companion;

public partial class MainWindow : Window
{
    private const string Gate14ImageAcquisitionMethod = "fp5-vehicle-record";
    private readonly LegacyHostSession session = new();
    private readonly AbacusFolderInspector folderInspector = new();
    private readonly AbacusWorkspaceService workspaceService;
    private readonly AbacusClipboardInspector clipboardInspector = new();
    private readonly AbacusClipboardImageExporter clipboardImageExporter = new();
    private readonly AbacusWindowCaptureService windowCaptureService = new();
    private readonly AbacusCaptureCropper captureCropper = new();
    private readonly AbacusNavigationStateDetector abacusNavigationStateDetector = new();
    private readonly AbacusScreenStateClassifier abacusScreenStateClassifier = new();
    private readonly AbacusDataAnalyzer dataAnalyzer = new(new AbacusTabParser());
    private readonly AbacusLinkagePlanner linkagePlanner = new(new AbacusTabParser());
    private readonly AbacusLegacyExportReader legacyExportReader = new();
    private readonly AbacusLegacyExportFolderDetector legacyExportFolderDetector = new();
    private readonly AbacusLegacyExportSubsetStore legacyExportSubsetStore = new();
    private readonly AbacusLegacyExportPreviewStore legacyExportPreviewStore = new();
    private readonly AbacusLegacyExportPreviewPackageReader legacyExportPackageReader = new();
    private readonly AbacusLegacyExportCandidateGraphService legacyExportCandidateGraphService = new();
    private readonly AbacusLegacyRecommendationEngine legacyRecommendationEngine = new();
    private readonly LegacyGraphWorkCheckpointStore legacyGraphWorkCheckpointStore = new();
    private readonly SemaphoreSlim legacyGraphCheckpointSaveGate = new(1, 1);
    private readonly AbacusFp5Inspector fp5Inspector = new();
    private readonly AbacusFp5CandidateExporter fp5CandidateExporter = new();
    private readonly AbacusImageLinkManifestStore imageLinkManifestStore = new();
    private readonly AbacusImageLinkMatcher imageLinkMatcher = new();
    private readonly AbacusImageLinkApprovalStore imageLinkApprovalStore = new();
    private readonly AbacusImageRegistrationPreviewStore imageRegistrationPreviewStore = new();
    private readonly AbacusBulkImagePreparationStore bulkImagePreparationStore = new();
    private readonly AbacusWebImportPreviewStore webImportPreviewStore = new();
    private readonly AbacusWebImportMappingStore webImportMappingStore = new();
    private readonly AbacusWebImportRegistrationPackageStore webImportRegistrationPackageStore = new();
    private readonly AbacusLegacyGraphFinalPackageStore legacyGraphFinalPackageStore = new();
    private readonly AbacusImportOutputPackageStore importOutputPackageStore = new();
    private readonly AbacusFp5VehicleImageMapper fp5VehicleImageMapper = new();
    private readonly IAbacusMigrationPreviewStore migrationPreviewStore;
    private CancellationTokenSource? operationCancellation;
    private AbacusFolderReport? sourceReport;
    private AbacusWorkspaceResult? workspaceResult;
    private string? verifiedWorkspacePath;
    private string? verifiedSourcePath;
    private string? verifiedOriginalFingerprint;
    private string? verifiedWorkspaceFingerprint;
    private uint? inspectedClipboardSequenceNumber;
    private long? diagnosedImageWindowHandle;
    private int? diagnosedAbacusProcessId;
    private AbacusImageExportResult? lastImageCaptureResult;
    private string? lastImageCaptureSourceFolder;
    private string? lastImageCaptureWorkspaceFolder;
    private string? lastImageCaptureSourceFingerprint;
    private string? lastImageCaptureWorkspaceFingerprint;
    private AbacusLinkagePlan? linkagePlan;
    private AbacusFp5Inspection? fp5Inspection;
    private AbacusFp5VehicleImageMappingResult? fp5VehicleImageMapping;
    private bool allowClose;
    private bool closeVerificationInProgress;
    private bool abacusMayBeRunning;
    private AbacusNavigationSnapshot? lastAbacusNavigationSnapshot;
    private bool imageLinkMatchBusy;
    private bool imageLinkApprovalBusy;
    private bool imageRegistrationPreviewBusy;
    private bool bulkImagePreparationBusy;
    private bool webImportPreviewBusy;
    private bool webImportMappingBusy;
    private bool webImportRegistrationBusy;
    private bool legacyGraphFinalPackageBusy;
    private bool legacyGraphBulkMergeBusy;
    private bool legacyGraphResumeInProgress;
    private bool legacyGraphCheckpointSaveScheduled;
    private bool legacyGraphCheckpointSaveDeferred;
    private bool CanMutateLegacyGraph => LegacyGraphMutationState.CanMutate(
        legacyGraphBulkMergeBusy,
        legacyGraphFinalPackageBusy,
        legacyGraphResumeInProgress);
    // パッケージ生成中に発生したエラーを、状態更新処理で既定メッセージへ
    // 上書きしないための画面状態です。
    private bool legacyGraphFinalPackageHasError;
    private string? legacyGraphFinalPackagePath;
    private AbacusImportOutputPackageSession? unifiedImportOutputSession;
    private AbacusBulkImagePreparationResult? bulkImagePreparationResult;
    private CancellationTokenSource? legacyExportDetectionCancellation;
    private CancellationTokenSource? unifiedImportOutputParentValidationCancellation;
    private string? unifiedImportValidatedOutputParentPath;
    private AbacusLegacyExportReadResult? legacyExportReadResult;
    private bool legacyExportSubsetActive;
    private AbacusWebImportMappingPackage? loadedWebImportMappingPackage;
    private List<WebImportMappingRow> webImportMappingRows = [];
    private AbacusLegacyExportCandidateGraphResult? legacyExportCandidateGraphResult;
    // Gate28の候補は既存のリンク辞書とは分離します。承認されるまで、最終パッケージの紐付けを変更しません。
    private IReadOnlyList<AbacusRecommendationCandidate> legacyGraphRecommendationCandidates = [];
    private readonly Dictionary<string, string> legacyGraphRecommendationDecisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LegacyGraphRecommendationState> legacyGraphRecommendationStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> legacyGraphManualDocumentLinks = new(StringComparer.OrdinalIgnoreCase);
    // 顧客IDが空欄だった車両を、ユーザーが顧客へ接続した一時状態です。
    // 最終パッケージではこの対応付けがある車両だけを出力します。
    private readonly Dictionary<string, string> legacyGraphManualVehicleCustomerLinks = new(StringComparer.OrdinalIgnoreCase);
    // 車両情報を持たない書類を、顧客だけへ一時的に紐付ける状態です。
    // 値は顧客IDではなく統合グループキーを保持し、統合前後でも同じ顧客グループを指します。
    private readonly Dictionary<string, string> legacyGraphManualDocumentCustomerLinks = new(StringComparer.OrdinalIgnoreCase);
    // 書類ごとの紐づけ方法と判断根拠を、作業状態から登録前パッケージへ引き継ぎます。
    private readonly Dictionary<string, string> legacyGraphDocumentLinkMethods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> legacyGraphDocumentLinkReasons = new(StringComparer.OrdinalIgnoreCase);
    // 書類の配置変更は画面上の一時状態としてのみ保持します。
    // 未接続はキャンバス上で確認中、トレイ移動済みはキャンバスから隠す状態です。
    private readonly HashSet<string> legacyGraphUnconnectedDocumentKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> legacyGraphTrayDocumentKeys = new(StringComparer.OrdinalIgnoreCase);
    // インポート全体の最終確定で除外確定した未確定トレイ書類です。
    // トレイからキャンバスへ戻した場合は最終確定状態とともに無効化します。
    private readonly HashSet<string> legacyGraphExcludedDocumentKeys = new(StringComparer.OrdinalIgnoreCase);
    // キャンバス上の車両を未確定トレイへ戻した一時状態です。
    // 元の顧客IDは保持したまま、最終パッケージでは車両と関連書類を除外します。
    private readonly HashSet<string> legacyGraphTrayVehicleIds = new(StringComparer.OrdinalIgnoreCase);
    // ユーザーが今回のインポートから明示的に除外した候補です。
    // 未確定トレイとは別管理し、復元時は移動前のリンク状態を再利用します。
    private readonly HashSet<string> legacyGraphTrashCustomerIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> legacyGraphTrashVehicleIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> legacyGraphTrashDocumentKeys = new(StringComparer.OrdinalIgnoreCase);
    // 顧客統合・書類紐付けを含む、グラフ全体の最終確定状態です。
    private bool legacyGraphImportConfirmed;
    private readonly Dictionary<string, LegacyGraphCustomerMergeDraft> legacyGraphCustomerMergeDrafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> legacyGraphAppliedCustomerMergeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> legacyGraphVirtualCustomerMergeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> legacyGraphCustomerNameOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> legacyGraphCustomerGroupExpanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LegacyGraphCustomerMergeGroup> legacyGraphCustomerMergeGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> legacyGraphCustomerMergeGroupByCustomerId = new(StringComparer.Ordinal);
    // 候補グループとは分離した、ユーザーが「統合する」と判断した論理顧客の構成です。
    // 候補グループを先に作っても、ここへ入るまでは統合済みとして扱いません。
    private readonly Dictionary<string, string> legacyGraphLogicalCustomerMergeGroupByCustomerId = new(StringComparer.Ordinal);
    // 検索から現在顧客へ手動追加した顧客候補です。キーは現在顧客の元顧客IDです。
    private readonly Dictionary<string, HashSet<string>> legacyGraphMatchingManualCustomerCandidateTargets = new(StringComparer.Ordinal);
    // 論理顧客ごとの最終確認状態です。統合候補の処理完了とは別の状態です。
    private readonly Dictionary<string, bool> legacyGraphCustomerApprovalStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> legacyGraphCustomerReviewStates = new(StringComparer.Ordinal);
    private object? legacyGraphSelectedItem;
    // 左一覧の子顧客を選択しても、作業対象グループは安定したグループIDで維持します。
    // 画面上の選択状態だけであり、共通ドメイン状態やチェックポイントには保存しません。
    private string? legacyGraphSelectedWorkGroupKey;
    private string legacyGraphUiMode = "graph";
    private string? legacyGraphMatchingCustomerId;
    private int legacyGraphMatchingCustomerIndex = -1;
    // Matching UIで統合グループ内から選択した解除対象です。
    // 基準顧客・おすすめ選択とは別の画面選択状態で、ドメイン状態には保存しません。
    private string? legacyGraphMatchingSelectedMergeMemberCustomerId;
    private string legacyGraphMatchingCategory = LegacyMatchingCategoryKinds.Customer;
    private int legacyGraphMatchingRecommendationIndex = -1;
    private bool legacyGraphRefreshingMatchingCustomerQueue;
    private bool legacyGraphWorkspaceHeightUpdatePending;
    private bool legacyGraphWorkspaceHeightUpdateInProgress;
    private const int LegacyMatchingUnresolvedPageSize = 50;
    private int legacyMatchingUnresolvedPageIndex;
    private bool legacyMatchingDetailsVisible;
    private readonly List<LegacyMatchingChangeItem> legacyGraphMatchingChanges = [];
    private LegacyMatchingUnresolvedItem? legacyMatchingUnresolvedDragItem;
    private Point legacyMatchingUnresolvedDragStartPoint;
    private AbacusRecommendationCandidate? legacyGraphSelectedRecommendation;
    private UIElement? legacyGraphDraggingElement;
    private Point legacyGraphDragOffset;
    private Point legacyGraphBlockDragStartPoint;
    private bool legacyGraphBlockDragStarted;
    private Cursor? legacyGraphBlockDragOriginalCursor;
    private bool legacyGraphPanning;
    private Point legacyGraphPanStartPoint;
    private double legacyGraphPanStartHorizontalOffset;
    private double legacyGraphPanStartVerticalOffset;
    private bool legacyGraphInspectorPanning;
    private bool legacyGraphInspectorPanningPage;
    private Point legacyGraphInspectorPanStartPoint;
    private double legacyGraphInspectorPanStartVerticalOffset;
    private double legacyGraphInspectorPanStartPageOffset;
    private AbacusLegacyExportCandidateGraphDocument? legacyGraphTrayDragDocument;
    private Point legacyGraphTrayDragStartPoint;
    private AbacusLegacyExportCandidateGraphVehicle? legacyGraphTrayDragVehicle;
    private Point legacyGraphTrayDragVehicleStartPoint;
    private AbacusLegacyExportCandidateGraphDocument? legacyGraphHandleDragDocument;
    private Point legacyGraphHandleDragStartPoint;
    private AbacusLegacyExportCandidateGraphDocument? legacyGraphDocumentCardDragDocument;
    private AbacusLegacyExportCandidateGraphVehicle? legacyGraphVehicleCardDragVehicle;
    private Border? legacyGraphBlockDragPreview;
    private FrameworkElement? legacyGraphBlockDragPreviewSource;
    private Line? legacyGraphActiveConnectionLine;
    private FrameworkElement? legacyGraphActiveConnectionSource;
    private FrameworkElement? legacyGraphNodeDragSource;
    private bool legacyGraphNodeDragStarted;
    private DispatcherTimer? legacyGraphNodeAutoScrollTimer;
    private AbacusLegacyExportCandidateGraphVehicle? legacyGraphNodeDragVehicle;
    private Point legacyGraphNodeDragStartPoint;
    private AbacusLegacyExportCandidateGraphCustomer? legacyGraphCustomerDragSource;
    private Point legacyGraphCustomerDragStartPoint;
    private ListBoxItem? legacyGraphCustomerDragTargetItem;
    private Border? legacyGraphCustomerUngroupDropHighlight;
    private Grid? legacyGraphDropHighlightTarget;
    private ListBox? legacyGraphTrayDropHighlightList;
    private ListBox? legacyGraphTrashDropHighlightList;
    private bool legacyGraphNativeDocumentDropTargetValid;
    private readonly List<LegacyGraphEdge> legacyGraphEdges = [];

    private const double LegacyGraphMinimumCanvasHeight = 620;
    private const double LegacyGraphDefaultWorkspaceHeight = 650;
    private const double LegacyGraphInspectorColumnWidth = 430;
    private const double LegacyMatchingInspectorColumnWidth = 520;
    private const double LegacyMatchingMinimumPageHeight = 900;

    public MainWindow()
    {
        InitializeComponent();
        workspaceService = new AbacusWorkspaceService(folderInspector);
        migrationPreviewStore = new AbacusMigrationPreviewStore(dataAnalyzer, linkagePlanner);
        session.StateChanged += Session_StateChanged;
        Closing += MainWindow_Closing;
        Render(session.Snapshot);
        MainTabControl.SelectedItem = UnifiedImportTab;
    }

    private void MainWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // マウスキャプチャ中は元の要素以外の領域でもWindowまでイベントが届くため、
        // ここでドロップ先判定とカーソルを最後に再適用します。
        if (legacyGraphBlockDragStarted &&
            (legacyGraphDocumentCardDragDocument is not null || legacyGraphVehicleCardDragVehicle is not null))
        {
            UpdateLegacyGraphBlockDropTarget(e.GetPosition(this));
        }

    }

    private void LegacyGraphInspectorLayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLegacyGraphTrashOverlaySize();
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleLegacyGraphWorkspaceHeightUpdate();
    }

    private void LegacyGraphPageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleLegacyGraphWorkspaceHeightUpdate();
    }

    private void LegacyGraphPageScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 高さ計算はビューポートの大きさに依存し、ページ内のスクロール位置には依存しません。
        // VerticalChangeにも反応すると、長い候補詳細をスクロールするたびに
        // 固定高さの解除・再設定が発生し、外側ScrollViewerの再レイアウトを連鎖させます。
        if (Math.Abs(e.ViewportHeightChange) > double.Epsilon ||
            Math.Abs(e.ViewportWidthChange) > double.Epsilon)
        {
            ScheduleLegacyGraphWorkspaceHeightUpdate();
        }
    }

    private void LegacyGraphPageScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        // 起動時にチェックポイントからマッチングUIを復元する場合も、
        // 読み込み後の有限なViewportを使って右側詳細のスクロール領域を確定します。
        ScheduleLegacyGraphWorkspaceHeightUpdate();
    }

    private void ScheduleLegacyGraphWorkspaceHeightUpdate()
    {
        if (LegacyGraphPageScrollViewer is null ||
            LegacyGraphWorkspaceGrid is null)
        {
            return;
        }

        if (legacyGraphWorkspaceHeightUpdatePending ||
            legacyGraphWorkspaceHeightUpdateInProgress)
        {
            return;
        }

        legacyGraphWorkspaceHeightUpdatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            legacyGraphWorkspaceHeightUpdatePending = false;
            if (legacyGraphWorkspaceHeightUpdateInProgress)
            {
                return;
            }

            legacyGraphWorkspaceHeightUpdateInProgress = true;
            try
            {
                UpdateLegacyGraphWorkspaceHeight();
            }
            finally
            {
                legacyGraphWorkspaceHeightUpdateInProgress = false;
            }
        }));
    }

    private void UpdateLegacyGraphWorkspaceHeight()
    {
        if (LegacyGraphMatchingPageGrid is null ||
            LegacyGraphWorkspaceGrid is null)
        {
            return;
        }

        if (!string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            LegacyGraphMatchingPageGrid.ClearValue(FrameworkElement.HeightProperty);
            LegacyGraphWorkspaceGrid.Height = LegacyGraphDefaultWorkspaceHeight;
            return;
        }

        if (LegacyGraphPageScrollViewer is null ||
            LegacyGraphPageContent is null ||
            LegacyGraphPageScrollViewer.ViewportHeight <= 0 ||
            LegacyGraphPageContent.ActualHeight <= 0 ||
            LegacyGraphMatchingPageGrid.ActualHeight <= 0)
        {
            return;
        }

        try
        {
            // グラフモードから切り替えた直後だけ残っている固定Heightを解除します。
            // 安定後の再計算で毎回解除すると、自然高さへの再測定と外側ScrollViewerの
            // スクロール変更が連鎖して、フォルダー読込後の切替時にUIを占有します。
            if (LegacyGraphWorkspaceGrid.ReadLocalValue(FrameworkElement.HeightProperty) !=
                DependencyProperty.UnsetValue)
            {
                LegacyGraphWorkspaceGrid.ClearValue(FrameworkElement.HeightProperty);
                LegacyGraphPageScrollViewer.UpdateLayout();
            }

            // マッチング領域は、ページ下部の「今回の変更」やごみ箱などの高さを
            // 差し引かず、候補判断に必要な最低高さを確保します。下部の内容は外側の
            // ScrollViewerでスクロールできるため、ここを縮める理由はありません。
            var availableMatchingPageHeight = Math.Max(
                LegacyGraphPageScrollViewer.ViewportHeight,
                LegacyMatchingMinimumPageHeight);
            if (double.IsNaN(availableMatchingPageHeight) ||
                double.IsInfinity(availableMatchingPageHeight) ||
                availableMatchingPageHeight <= 0)
            {
                return;
            }

            // LegacyGraphPageContent は縦 StackPanel のため、明示的な高さがない間は
            // 右側候補詳細の全文を含む自然高さでマッチング領域を測定します。
            // 自然高さが残り高さを上回る場合も、ここへ残り高さを適用しないと
            // 内側の候補 ScrollViewer が無限高さで測定され、スクロールバーが表示されません。
            if (double.IsNaN(LegacyGraphMatchingPageGrid.Height) ||
                Math.Abs(LegacyGraphMatchingPageGrid.Height - availableMatchingPageHeight) > 1)
            {
                LegacyGraphMatchingPageGrid.Height = availableMatchingPageHeight;
            }
        }
        catch (InvalidOperationException)
        {
            // レイアウト確定前は次のSizeChanged/更新時に再計算します。
        }
    }

    private void UpdateLegacyGraphTrashOverlaySize()
    {
        if (LegacyGraphInspectorLayer is null || LegacyGraphTrashOverlay is null)
        {
            return;
        }

        var width = LegacyGraphInspectorLayer.ActualWidth;
        if (width <= 0 || double.IsNaN(width))
        {
            return;
        }

        // 詳細表示欄と同じ幅・高さにし、右下へ重ねて表示します。
        LegacyGraphTrashOverlay.Width = width;
        LegacyGraphTrashOverlay.Height = width;
    }

    private void ShowLegacyGraphTrashOverlay()
    {
        UpdateLegacyGraphTrashOverlaySize();
        LegacyGraphTrashOverlay.Visibility = Visibility.Visible;
        SetLegacyGraphTrashOverlayHighlight(false);
    }

    private void HideLegacyGraphTrashOverlay()
    {
        SetLegacyGraphTrashOverlayHighlight(false);
        LegacyGraphTrashOverlay.Visibility = Visibility.Collapsed;
    }

    private void SetLegacyGraphTrashOverlayHighlight(bool highlighted)
    {
        LegacyGraphTrashOverlay.Background = ToBrush(highlighted ? "#FFFFE4E6" : "#E6FFF1F2");
        LegacyGraphTrashOverlay.BorderBrush = ToBrush(highlighted ? "#DC2626" : "#F87171");
        LegacyGraphTrashOverlay.BorderThickness = new Thickness(highlighted ? 3 : 2);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await session.StartAsync();
        }
        catch
        {
            // The session publishes a user-facing error state.
        }
    }

    private async void PingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await session.PingAsync();
        }
        catch
        {
            // The session publishes a user-facing error state.
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (abacusMayBeRunning)
        {
            MessageBox.Show(
                this,
                "先に「ABACUSを閉じる」でコピー側ABACUSを終了してください。",
                "ABACUSが起動中です",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            await session.StopAsync();
        }
        catch (Exception exception)
        {
            DetailText.Text = $"停止処理に失敗しました: {exception.Message}";
        }
    }

    private async void SelectSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "保存用ABACUSフォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ApplySelectedAbacusFolderAsync(dialog.FolderName);
        }
    }

    private async Task ApplySelectedAbacusFolderAsync(
        string folderPath,
        bool preserveSelectedLegacyExport = false)
    {
        var previousBulkSourcePath = BulkImageSourcePathTextBox.Text.Trim();
        var bulkSourceChanged = !string.Equals(previousBulkSourcePath, folderPath, StringComparison.OrdinalIgnoreCase);
        string? preservedLegacyExportPath = null;
        if (preserveSelectedLegacyExport && !string.IsNullOrWhiteSpace(LegacyExportPathTextBox.Text))
        {
            var candidateLegacyExportPath = LegacyExportPathTextBox.Text.Trim();
            if (await DirectoryExistsOnBackgroundAsync(candidateLegacyExportPath))
            {
                preservedLegacyExportPath = candidateLegacyExportPath;
            }
        }
        var preservedLegacyExportSubset = preservedLegacyExportPath is not null && legacyExportSubsetActive;
        UnifiedImportFolderPathTextBox.Text = folderPath;
        BulkImageSourcePathTextBox.Text = folderPath;
        SourcePathTextBox.Text = folderPath;
        AnalysisPathTextBox.Text = folderPath;
        LinkagePathTextBox.Text = folderPath;
        MigrationSourcePathTextBox.Text = folderPath;
        ResetLegacyExportImportState();
        fp5VehicleImageMapping = null;
        UnifiedImportImageMappingStatusText.Text =
            "解析を開始すると、FP5内の車検証画像を自動復元して車両へ対応付けます。";
        UnifiedImportImageMappingStatusText.Foreground = ToBrush("#52647A");
        UnifiedImportImageMappingSummaryText.Text = "";
        Fp5InspectionStatusText.Text = "未診断";
        Fp5InspectionResultText.Text = "";
        Fp5CandidatesGrid.ItemsSource = null;
        fp5Inspection = null;
        ExtractFp5CandidateButton.IsEnabled = false;
        Fp5CandidateExportStatusText.Text = "候補を選択すると、標準JPEG構造を再検証してから1件だけ出力します。内部ブロックと判定した場合は保存しません。";
        ResetImageLinkCapture();
        if (bulkSourceChanged)
        {
            ResetBulkImagePreparationState();
        }
        if (preservedLegacyExportPath is not null)
        {
            legacyExportSubsetActive = preservedLegacyExportSubset;
            LegacyExportPathTextBox.Text = preservedLegacyExportPath;
            LegacyExportAutoDetectStatusText.Text = "選択済みのCSVフォルダーを維持して固定列を再診断しています…";
            LegacyExportAutoDetectStatusText.Foreground = ToBrush("#52647A");
            await InspectLegacyExportsAsync(preservedLegacyExportPath, automatic: true, CancellationToken.None);
        }
        else
        {
            await AutoDetectAndInspectLegacyExportsAsync(folderPath);
        }
        UpdateUnifiedImportEntryState();
        UpdateBulkImagePreparationButtonState();
    }

    private async void UnifiedImportSelectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "保存用ABACUSフォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        UnifiedImportFolderPathTextBox.Text = dialog.FolderName;
        UnifiedImportStatusText.Text = "ABACUSフォルダーを読み取り、CSVを自動検出しています…";
        UnifiedImportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        UnifiedImportCreateCanvasButton.IsEnabled = false;
        await ApplySelectedAbacusFolderAsync(dialog.FolderName);
        UpdateUnifiedImportEntryState();
    }

    private void UnifiedImportSelectOutputParentButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "ABACUSインポート生成物の保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (legacyExportCandidateGraphResult is not null)
        {
            var result = MessageBox.Show(
                this,
                "生成物の保存先を変更すると、現在のキャンパス候補をいったん作り直します。変更しますか？",
                "保存先の変更",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            ResetLegacyExportImportState();
        }

        UnifiedImportOutputParentPathTextBox.Text = dialog.FolderName;
        UnifiedImportStatusText.Text = "生成物の保存先を選択しました。解析を開始できます。";
        UnifiedImportStatusText.Foreground = ToBrush("#52647A");
        UpdateUnifiedImportEntryState();
    }

    private void UnifiedImportOutputParentPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (UnifiedImportStatusText is null || UnifiedImportOutputParentPathTextBox is null)
        {
            return;
        }

        unifiedImportOutputParentValidationCancellation?.Cancel();
        unifiedImportOutputParentValidationCancellation = null;
        unifiedImportValidatedOutputParentPath = null;

        var path = UnifiedImportOutputParentPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            UnifiedImportStatusText.Text = "生成物の保存先を選択してください。";
            UnifiedImportStatusText.Foreground = ToBrush("#52647A");
            UpdateUnifiedImportEntryState();
            return;
        }

        UpdateUnifiedImportEntryState();

        // 入力中にネットワークフォルダーなどへ同期アクセスすると、
        // TextChangedがUIスレッドをブロックして画面全体が固まります。
        // 入力が止まってからバックグラウンドで1回だけ存在確認します。
        var cancellation = new CancellationTokenSource();
        unifiedImportOutputParentValidationCancellation = cancellation;
        _ = ValidateUnifiedImportOutputParentAsync(path, cancellation);
    }

    private async Task ValidateUnifiedImportOutputParentAsync(
        string path,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token);
            var exists = await Task.Run(
                () => Directory.Exists(path),
                cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(unifiedImportOutputParentValidationCancellation, cancellation) ||
                UnifiedImportOutputParentPathTextBox is null ||
                !string.Equals(UnifiedImportOutputParentPathTextBox.Text.Trim(), path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            unifiedImportValidatedOutputParentPath = exists ? path : null;
            UpdateUnifiedImportEntryState();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(unifiedImportOutputParentValidationCancellation, cancellation))
            {
                unifiedImportOutputParentValidationCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private static Task<bool> DirectoryExistsOnBackgroundAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Directory.Exists(path), cancellationToken);

    private async void UnifiedImportCreateCanvasButton_Click(object sender, RoutedEventArgs e)
    {
        var folderPath = UnifiedImportFolderPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folderPath) ||
            !await DirectoryExistsOnBackgroundAsync(folderPath))
        {
            MessageBox.Show(this, "保存用ABACUSフォルダーを選択してください。", "フォルダー未選択");
            return;
        }

        if (legacyExportCandidateGraphResult is not null && fp5VehicleImageMapping?.IsFullyMatched == true)
        {
            if (!await EnsureUnifiedImportOutputSessionAsync(folderPath))
            {
                return;
            }

            ShowUnifiedImportGraph();
            return;
        }

        UnifiedImportCreateCanvasButton.IsEnabled = false;
        UnifiedImportStatusText.Text = "CSVを診断し、FP5画像を復元・対応付けしてキャンバス表示用の候補を準備しています…";
        UnifiedImportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            await ApplySelectedAbacusFolderAsync(folderPath, preserveSelectedLegacyExport: true);
            if (!CreateLegacyExportPreviewButton.IsEnabled)
            {
                UnifiedImportStatusText.Text = "CSVの診断に合格していません。詳細診断で内容を確認してください。";
                UnifiedImportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
                return;
            }

            // ApplySelectedAbacusFolderAsync は既存の候補と保存セッションを初期化するため、
            // CSV診断が完了した後にこの実行単位の work/ready ルートを作成します。
            if (!await EnsureUnifiedImportOutputSessionAsync(folderPath))
            {
                return;
            }

            if (!await RunGate14ImageMappingAsync())
            {
                return;
            }

            await CreateLegacyExportPreviewAsync(unifiedImportOutputSession!.WorkIntermediatePath);
            if (legacyExportCandidateGraphResult is not null)
            {
                UnifiedImportStatusText.Text = "解析と画像対応付けが完了しました。キャンバスで顧客統合・書類紐付け・ノード操作を確認できます。";
                UnifiedImportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
                ShowUnifiedImportGraph();
            }
            else
            {
                UnifiedImportStatusText.Text = "候補パッケージを作成できませんでした。詳細診断のエラーを確認してください。";
                UnifiedImportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            }
        }
        finally
        {
            UpdateUnifiedImportEntryState();
        }
    }

    private async Task<bool> EnsureUnifiedImportOutputSessionAsync(string sourceFolder)
    {
        var destinationParent = UnifiedImportOutputParentPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(destinationParent) ||
            !await DirectoryExistsOnBackgroundAsync(destinationParent))
        {
            MessageBox.Show(this, "先に生成物の保存先を選択してください。保存先の下に作業用と完成品用のフォルダーを作成します。", "保存先未選択", MessageBoxButton.OK, MessageBoxImage.Warning);
            UnifiedImportStatusText.Text = "生成物の保存先を選択してください。";
            UnifiedImportStatusText.Foreground = ToBrush("#805B10");
            return false;
        }

        var method = Gate14ImageAcquisitionMethod;
        if (unifiedImportOutputSession is not null &&
            string.Equals(unifiedImportOutputSession.SourcePath, Path.GetFullPath(sourceFolder), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(unifiedImportOutputSession.ImageAcquisitionMethod, method, StringComparison.Ordinal) &&
            string.Equals(
                Path.GetDirectoryName(unifiedImportOutputSession.RootPath),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationParent)),
                StringComparison.OrdinalIgnoreCase) &&
            await DirectoryExistsOnBackgroundAsync(unifiedImportOutputSession.RootPath))
        {
            return true;
        }

        try
        {
            UnifiedImportStatusText.Text = "生成物ルート（work / ready）を準備しています…";
            UnifiedImportStatusText.Foreground = ToBrush("#52647A");
            unifiedImportOutputSession = await importOutputPackageStore.CreateAsync(
                destinationParent,
                sourceFolder,
                sourceReport?.FolderFingerprint,
                method);
            UnifiedImportSummaryText.Text =
                $"生成物ルート: {unifiedImportOutputSession.RootPath}\n" +
                "作業途中のファイルはwork、Webへ渡す完成品はreadyへ分離します。";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            unifiedImportOutputSession = null;
            UnifiedImportStatusText.Text = $"生成物ルートを作成できません: {exception.Message}";
            UnifiedImportStatusText.Foreground = ToBrush("#A61B1B");
            return false;
        }
    }

    private async Task<bool> RunGate14ImageMappingAsync()
    {
        if (unifiedImportOutputSession is null)
        {
            UnifiedImportImageMappingStatusText.Text = "生成物セッションがありません。先に保存先を選択してください。";
            UnifiedImportImageMappingStatusText.Foreground = ToBrush("#A61B1B");
            return false;
        }

        var sourceRoot = Path.GetFullPath(UnifiedImportFolderPathTextBox.Text.Trim());
        var vehicleExportPath = Path.GetFullPath(LegacyExportPathTextBox.Text.Trim());
        if (!Directory.Exists(vehicleExportPath))
        {
            UnifiedImportImageMappingStatusText.Text = "車両一覧CSVフォルダーを自動検出できませんでした。詳細調査で確認してください。";
            UnifiedImportImageMappingStatusText.Foreground = ToBrush("#A61B1B");
            return false;
        }

        var workspaceVehicleExportPath = IsSameOrSubPath(vehicleExportPath, sourceRoot)
            ? Path.Combine(
                unifiedImportOutputSession.WorkAbacusCopyPath,
                Path.GetRelativePath(sourceRoot, vehicleExportPath))
            // CSVフォルダーを詳細診断から別に選択した場合は、選択済みの
            // フォルダーを読み取り専用で使います。ABACUSコピーのルートへ
            // 置き換えると、CSVが原本の外側にある構成で対応付けできません。
            : vehicleExportPath;
        if (!Directory.Exists(workspaceVehicleExportPath))
        {
            UnifiedImportImageMappingStatusText.Text = "車両一覧CSVフォルダーが見つかりません。詳細診断で選択内容を確認してください。";
            UnifiedImportImageMappingStatusText.Foreground = ToBrush("#A61B1B");
            return false;
        }
        var fp5SourcePath = Directory.EnumerateFiles(
                unifiedImportOutputSession.WorkAbacusCopyPath,
                "abx-cs-sk.ucs",
                SearchOption.TopDirectoryOnly)
            .SingleOrDefault();
        if (fp5SourcePath is null)
        {
            UnifiedImportImageMappingStatusText.Text = "画像データベース abx-cs-sk.ucs が作業用コピーにありません。";
            UnifiedImportImageMappingStatusText.Foreground = ToBrush("#A61B1B");
            return false;
        }

        var outputParent = Path.Combine(unifiedImportOutputSession.WorkIntermediatePath, "fp5-image-restoration");
        Directory.CreateDirectory(outputParent);
        UnifiedImportImageMappingStatusText.Text = "FP5セクターを解析し、車両レコードと画像を対応付けています…";
        UnifiedImportImageMappingStatusText.Foreground = ToBrush("#52647A");
        UnifiedImportImageMappingSummaryText.Text = "";
        try
        {
            fp5VehicleImageMapping = await fp5VehicleImageMapper.MapAsync(
                fp5SourcePath,
                workspaceVehicleExportPath,
                outputParent,
                allowPartialScope: legacyExportSubsetActive);
            UnifiedImportImageMappingSummaryText.Text =
                $"車両レコード: {fp5VehicleImageMapping.InternalVehicleRecordCount:N0}件 / " +
                $"今回の対象: {fp5VehicleImageMapping.InScopeVehicleRecordCount:N0}件 / " +
                $"JPEG: {fp5VehicleImageMapping.JpegImageCount:N0}件 / " +
                $"対応付け: {fp5VehicleImageMapping.MatchedImageCount:N0}件 / " +
                $"画像なし: {fp5VehicleImageMapping.NoImageCount:N0}件 / " +
                $"対象外: {fp5VehicleImageMapping.OutOfScopeRecordCount:N0}件 / " +
                $"要確認: {fp5VehicleImageMapping.ReviewCount + fp5VehicleImageMapping.UnmatchedCount + fp5VehicleImageMapping.MultipleCandidateCount + fp5VehicleImageMapping.UnknownImageReferenceCount:N0}件";
            if (!fp5VehicleImageMapping.IsFullyMatched)
            {
                UnifiedImportImageMappingStatusText.Text =
                    "FP5画像の復元は完了しましたが、車両への対応付けに要確認項目があります。パッケージ作成を停止しました。";
                UnifiedImportImageMappingStatusText.Foreground = ToBrush("#A61B1B");
                return false;
            }

            UnifiedImportImageMappingStatusText.Text = fp5VehicleImageMapping.OutOfScopeRecordCount > 0 || legacyExportSubsetActive
                ? "Gate 14の方式でFP5画像を復元し、選択顧客の車両だけを対象に自動対応付けしました。対象外の車両は今回の検証から除外しています。"
                : "Gate 14の検証済み方式で画像を復元し、車両へ自動対応付けしました。";
            UnifiedImportImageMappingStatusText.Foreground = ToBrush("#17643A");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            fp5VehicleImageMapping = null;
            UnifiedImportImageMappingStatusText.Text = $"FP5画像の復元・対応付けに失敗しました: {exception.Message}";
            UnifiedImportImageMappingStatusText.Foreground = ToBrush("#A61B1B");
            UnifiedImportImageMappingSummaryText.Text = "";
            return false;
        }
    }

    private void UnifiedImportOpenDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectionStatusTab.Visibility = Visibility.Visible;
        AbacusInspectionTab.Visibility = Visibility.Visible;
        DocumentAnalysisTab.Visibility = Visibility.Visible;
        LinkagePreviewTab.Visibility = Visibility.Visible;
        LegacyPreparationExpander.IsExpanded = true;
        LegacyPreparationExpander.BringIntoView();
        UnifiedImportOpenDiagnosticsButton.Content = "詳細調査タブを表示済み";
        UnifiedImportStatusText.Text = "詳細調査タブを表示しました。通常操作はこの完成タブで続けられます。";
        UnifiedImportStatusText.Foreground = ToBrush("#52647A");
    }

    private void SelectBulkImageDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "一括画像パッケージの保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BulkImageDestinationTextBox.Text = dialog.FolderName;
        BulkImageStatusText.Text = "保存先を選択しました。ABACUSフォルダー内の標準画像を一括照合できます。";
        BulkImageStatusText.Foreground = ToBrush("#52647A");
        UpdateBulkImagePreparationButtonState();
    }

    private async void PrepareBulkImagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (bulkImagePreparationBusy)
        {
            return;
        }

        var sourceFolder = BulkImageSourcePathTextBox.Text.Trim();
        var vehicleExportFolder = LegacyExportPathTextBox.Text.Trim();
        var destinationParent = BulkImageDestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            MessageBox.Show(this, "先にABACUSインポート画面で保存用フォルダーを選択してください。", "入力フォルダー未選択");
            return;
        }

        if (string.IsNullOrWhiteSpace(destinationParent) || !Directory.Exists(destinationParent))
        {
            MessageBox.Show(this, "一括パッケージの保存先フォルダーを選択してください。", "保存先未選択");
            return;
        }

        if (string.IsNullOrWhiteSpace(vehicleExportFolder) || !Directory.Exists(vehicleExportFolder))
        {
            vehicleExportFolder = sourceFolder;
        }

        bulkImagePreparationBusy = true;
        UpdateBulkImagePreparationButtonState();
        BulkImageStatusText.Text = "標準PNG/JPEGを検証し、UCS内部JPEG候補は保存せず診断レポートへ記録しています…";
        BulkImageStatusText.Foreground = ToBrush("#52647A");
        BulkImageCandidatesGrid.ItemsSource = null;
        try
        {
            var result = await bulkImagePreparationStore.CreateAsync(
                sourceFolder,
                vehicleExportFolder,
                destinationParent);
            bulkImagePreparationResult = result;
            BulkImageCandidatesGrid.ItemsSource = result.Candidates;
            OpenBulkImagePackageButton.IsEnabled = true;

            var warningText = result.Warnings.Count == 0
                ? string.Empty
                : $"\n注意: {string.Join("\n", result.Warnings)}";
            BulkImageStatusText.Text =
                $"一括画像パッケージを作成しました。標準画像 {result.SourceImageCount:N0}件 / UCS保存 {result.EmbeddedImageCount:N0}件 / " +
                $"一意照合 {result.MatchedCount:N0}件 / 要確認 {result.ReviewCount:N0}件 / " +
                $"未照合 {result.NotFoundCount:N0}件 / 重複除外 {result.DuplicateCount:N0}件 / 抽出不可 {result.RejectedCount:N0}件。\n" +
                $"登録前パッケージ: {result.PackagePath}\n" +
                (result.ImageCount > 0
                    ? "一意照合済み画像をグラフ確定時の画像登録前パッケージへ自動設定しました。"
                    : "一意照合済み画像がないため、画像登録前パッケージはグラフへ設定していません。") +
                warningText;
            BulkImageStatusText.Foreground = ToBrush(result.ImageCount > 0 ? "#17643A" : "#805B10");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or NotSupportedException)
        {
            bulkImagePreparationResult = null;
            OpenBulkImagePackageButton.IsEnabled = false;
            BulkImageStatusText.Text = $"一括画像処理に失敗しました: {exception.Message}";
            BulkImageStatusText.Foreground = ToBrush("#A61B1B");
        }
        finally
        {
            bulkImagePreparationBusy = false;
            UpdateBulkImagePreparationButtonState();
        }
    }

    private void OpenBulkImagePackageButton_Click(object sender, RoutedEventArgs e)
    {
        var packagePath = bulkImagePreparationResult?.PackagePath;
        if (string.IsNullOrWhiteSpace(packagePath) || !Directory.Exists(packagePath))
        {
            BulkImageStatusText.Text = "作成済み一括画像パッケージの保存先が見つかりません。もう一度実行してください。";
            BulkImageStatusText.Foreground = ToBrush("#A61B1B");
            OpenBulkImagePackageButton.IsEnabled = false;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetFullPath(packagePath),
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            BulkImageStatusText.Text = $"一括画像パッケージを開けません: {exception.Message}";
            BulkImageStatusText.Foreground = ToBrush("#A61B1B");
        }
    }

    private void UpdateUnifiedImportEntryState()
    {
        var outputParentPath = UnifiedImportOutputParentPathTextBox.Text.Trim();
        var hasOutputParent = !string.IsNullOrWhiteSpace(outputParentPath) &&
                              string.Equals(
                                  unifiedImportValidatedOutputParentPath,
                                  outputParentPath,
                                  StringComparison.OrdinalIgnoreCase);
        if (legacyExportCandidateGraphResult is not null && fp5VehicleImageMapping?.IsFullyMatched == true)
        {
            UnifiedImportCreateCanvasButton.IsEnabled = true;
            UnifiedImportCreateCanvasButton.Content = "キャンパスを表示";
            UnifiedImportStatusText.Text = "キャンパスを表示できます。";
            UnifiedImportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            UnifiedImportSummaryText.Text = "候補パッケージは読み込み済みです。ボタンを押すとキャンパスへ移動します。";
            return;
        }

        UnifiedImportCreateCanvasButton.Content = "解析を開始";
        UnifiedImportCreateCanvasButton.IsEnabled = CreateLegacyExportPreviewButton.IsEnabled && hasOutputParent;
        UnifiedImportStatusText.Text = CreateLegacyExportPreviewButton.IsEnabled
            ? hasOutputParent
                ? "CSVの固定列診断に合格しました。解析を開始するとFP5画像の復元・対応付けも実行します。"
                : "CSVの固定列診断に合格しました。先に生成物の保存先を選択してください。"
            : "CSVを検出または診断できていません。必要な場合は詳細診断を開いて確認してください。";
        UnifiedImportStatusText.Foreground = ToBrush(CreateLegacyExportPreviewButton.IsEnabled && hasOutputParent ? "#17643A" : "#805B10");
        UnifiedImportSummaryText.Text = string.IsNullOrWhiteSpace(LegacyExportPathTextBox.Text)
            ? "CSVフォルダーを検出できていません。"
            : LegacyExportStatusText.Text;
    }

    private void ShowUnifiedImportGraph()
    {
        LegacyPreparationExpander.IsExpanded = false;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => LegacyGraphSectionTitle.BringIntoView()));
    }

    private void SelectMigrationSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "移行準備情報を作成するABACUSフォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            MigrationSourcePathTextBox.Text = dialog.FolderName;
            MigrationPreviewStatusText.Text = "未作成";
            MigrationPreviewResultText.Text = "";
            Fp5InspectionStatusText.Text = "未診断";
            Fp5InspectionResultText.Text = "";
            Fp5CandidatesGrid.ItemsSource = null;
            fp5Inspection = null;
            ExtractFp5CandidateButton.IsEnabled = false;
            Fp5CandidateExportStatusText.Text = "候補を選択すると、標準JPEG構造を再検証してから1件だけ出力します。内部ブロックと判定した場合は保存しません。";
            ResetImageLinkCapture();
        }
    }

    private async void InspectFp5Button_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MigrationSourcePathTextBox.Text))
        {
            MessageBox.Show(this, "ABACUSフォルダーを指定してください。", "フォルダー未選択");
            return;
        }

        InspectFp5Button.IsEnabled = false;
        Fp5InspectionStatusText.Text = "BackUp-5.fp5を読み取り専用で診断しています…";
        Fp5InspectionStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        Fp5InspectionResultText.Text = "";
        Fp5CandidatesGrid.ItemsSource = null;
        fp5Inspection = null;
        ExtractFp5CandidateButton.IsEnabled = false;
        Fp5CandidateExportStatusText.Text = "候補を選択すると、標準JPEG構造を再検証してから1件だけ出力します。内部ブロックと判定した場合は保存しません。";
        try
        {
            var result = await fp5Inspector.InspectAsync(MigrationSourcePathTextBox.Text.Trim());
            fp5Inspection = result;
            Fp5CandidatesGrid.ItemsSource = result.Candidates;
            ExtractFp5CandidateButton.IsEnabled = result.IsValid && result.Candidates.Count > 0;
            Fp5InspectionStatusText.Text = result.IsValid
                ? "FileMaker Pro 5.0のヘッダーとJPEGマーカー候補を確認しました（画像としては未確定）。ファイルの書き込みは行っていません。"
                : $"fp5診断を完了しました。エラー{result.Errors.Count:N0}件、警告{result.Warnings.Count:N0}件。";
            Fp5InspectionStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(
                result.IsValid ? "#805B10" : result.Errors.Count > 0 ? "#A61B1B" : "#805B10")!;

            var details = new List<string>
            {
                $"形式: {result.FormatLabel}",
                $"ファイルサイズ: {result.FileSize:N0} bytes",
                $"JPEG候補: {result.JpegCandidateCount:N0}件 / 合計{result.JpegCandidateBytes:N0} bytes",
                $"候補一覧表示: {result.Candidates.Count:N0}件（最大100件）",
                "注意: これは開始・終了マーカーの候補一覧です。標準JPEG構造として確認できない候補は保存しません。",
            };
            if (result.OversizeCandidateCount > 0)
            {
                details.Add($"上限超過候補: {result.OversizeCandidateCount:N0}件");
            }

            if (result.Errors.Count > 0)
            {
                details.Add($"エラー: {string.Join(" / ", result.Errors)}");
            }

            if (result.Warnings.Count > 0)
            {
                details.Add($"警告: {string.Join(" / ", result.Warnings)}");
            }

            Fp5InspectionResultText.Text = string.Join("\n", details);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            fp5Inspection = null;
            ExtractFp5CandidateButton.IsEnabled = false;
            Fp5InspectionStatusText.Text = $"fp5診断に失敗しました: {exception.Message}";
            Fp5InspectionStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            InspectFp5Button.IsEnabled = true;
        }
    }

    private void Fp5CandidatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ExtractFp5CandidateButton.IsEnabled = fp5Inspection?.IsValid == true &&
                                              Fp5CandidatesGrid.SelectedItem is AbacusFp5ImageCandidate;
    }

    private async void ExtractFp5CandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (fp5Inspection is not { IsValid: true } ||
            Fp5CandidatesGrid.SelectedItem is not AbacusFp5ImageCandidate candidate)
        {
            MessageBox.Show(this, "先に診断結果からJPEG候補を1件選択してください。", "候補未選択");
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "検証出力するJPEGの保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"候補#{candidate.Index}を1件だけJPEGとして検証出力します。\n\n" +
            "保存用原本・作業用コピーの内部には保存しません。元画像と車両の紐付けは行いません。\n\n" +
            $"保存先: {dialog.FolderName}\n続行しますか？",
            "JPEG候補を1件検証出力",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        ExtractFp5CandidateButton.IsEnabled = false;
        Fp5CandidateExportStatusText.Text = "候補の範囲・JPEGマーカー・画像デコードを再検証しています…";
        Fp5CandidateExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await fp5CandidateExporter.ExportAsync(
                fp5Inspection.FolderPath,
                candidate,
                dialog.FolderName);
            Fp5CandidateExportStatusText.Text =
                $"1件の検証出力に成功しました。\n" +
                $"保存先: {result.FilePath}\n" +
                $"サイズ: {result.FileSize:N0} bytes / 寸法: {result.PixelWidth:N0} × {result.PixelHeight:N0}\n" +
                $"SHA-256: {result.Sha256}\n" +
                "fp5内の元データや車両情報は変更していません。";
            Fp5CandidateExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or InvalidOperationException or
                                           NotSupportedException)
        {
            Fp5CandidateExportStatusText.Text = $"JPEG候補の検証出力に失敗しました: {exception.Message}";
            Fp5CandidateExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            ExtractFp5CandidateButton.IsEnabled = fp5Inspection?.IsValid == true &&
                                                  Fp5CandidatesGrid.SelectedItem is AbacusFp5ImageCandidate;
        }
    }

    private async void CreateMigrationPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MigrationSourcePathTextBox.Text))
        {
            MessageBox.Show(this, "ABACUSフォルダーを指定してください。", "フォルダー未選択");
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "永続保存する移行準備パッケージの親フォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        CreateMigrationPreviewButton.IsEnabled = false;
        MigrationSourcePathTextBox.IsEnabled = false;
        MigrationPreviewStatusText.Text = "ABACUSファイルを再検証し、移行準備情報を作成しています…";
        MigrationPreviewResultText.Text = "";
        try
        {
            var result = await migrationPreviewStore.CreateAsync(
                MigrationSourcePathTextBox.Text.Trim(),
                dialog.FolderName);
            MigrationPreviewStatusText.Text = "作成後の再読込検証に合格しました。顧客・車両・書類の登録やアップロードは行っていません。";
            MigrationPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            MigrationPreviewResultText.Text =
                $"保存先: {result.PackagePath}\n" +
                $"マニフェスト: {result.ManifestPath}\n" +
                $"顧客候補: {result.CustomerCandidates:N0} / 車両候補: {result.VehicleCandidates:N0} / 書類候補: {result.DocumentCandidates:N0}\n" +
                $"顧客名空欄で除外: {result.SkippedBlankCustomerDocuments:N0} / 要確認の競合: {result.ConflictGroups:N0}\n" +
                $"マニフェスト SHA-256: {result.ManifestSha256}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            MigrationPreviewStatusText.Text = $"移行準備情報を作成できません: {exception.Message}";
            MigrationPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            CreateMigrationPreviewButton.IsEnabled = true;
            MigrationSourcePathTextBox.IsEnabled = true;
        }
    }

    private async Task AutoDetectAndInspectLegacyExportsAsync(string abacusFolder)
    {
        CancelLegacyExportFolderDetection();
        var cancellation = new CancellationTokenSource();
        legacyExportDetectionCancellation = cancellation;
        LegacyExportAutoDetectStatusText.Text = "ABACUSフォルダー内の既存CSVを自動検出しています…";
        LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var detection = await legacyExportFolderDetector.DetectAsync(abacusFolder, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            if (detection.HasUniqueCandidate && detection.UniqueFolderPath is { } folderPath)
            {
                LegacyExportPathTextBox.Text = folderPath;
                LegacyExportAutoDetectStatusText.Text = "ABACUSフォルダー内のCSVフォルダーを1件検出しました。固定列を自動診断しています…";
                LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
                await InspectLegacyExportsAsync(folderPath, automatic: true, cancellation.Token);
                return;
            }

            LegacyExportPathTextBox.Clear();
            legacyExportReadResult = null;
            legacyExportSubsetActive = false;
            LegacyExportFilesGrid.ItemsSource = null;
            LegacyExportColumnsGrid.ItemsSource = null;
            LegacyExportCustomerComboBox.ItemsSource = null;
            LegacyExportCustomerComboBox.SelectedItem = null;
            CreateLegacyExportSubsetButton.IsEnabled = false;
            CreateLegacyExportPreviewButton.IsEnabled = false;
            if (detection.ScanLimitReached)
            {
                LegacyExportAutoDetectStatusText.Text = "CSV自動検出の走査上限に達しました。CSVフォルダーを手動で選択してください。";
                LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            }
            else if (detection.CandidateFolders.Count > 1)
            {
                LegacyExportAutoDetectStatusText.Text = $"CSVフォルダーを{detection.CandidateFolders.Count:N0}件検出しました。重複を避けるため自動選択せず、全件を含むフォルダーを手動で選択してください。";
                LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#805B10")!;
            }
            else
            {
                LegacyExportAutoDetectStatusText.Text = "ABACUSフォルダー内に既存の販売・整備・車両一覧CSVを検出できませんでした。CSVフォルダーを手動で選択してください。";
                LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#805B10")!;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            LegacyExportAutoDetectStatusText.Text = $"CSV自動検出に失敗しました: {exception.Message}。CSVフォルダーを手動で選択してください。";
            LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            if (ReferenceEquals(legacyExportDetectionCancellation, cancellation))
            {
                legacyExportDetectionCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelLegacyExportFolderDetection()
    {
        legacyExportDetectionCancellation?.Cancel();
        legacyExportDetectionCancellation?.Dispose();
        legacyExportDetectionCancellation = null;
    }

    private void ResetLegacyExportImportState()
    {
        CancelLegacyExportFolderDetection();
        legacyExportReadResult = null;
        legacyExportSubsetActive = false;
        LegacyExportPathTextBox.Clear();
        LegacyExportAutoDetectStatusText.Text = "ABACUSフォルダーを選択すると、内部のCSVを自動検出します。";
        LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        LegacyExportStatusText.Text = "未診断";
        LegacyExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        LegacyExportFilesGrid.ItemsSource = null;
        LegacyExportColumnsGrid.ItemsSource = null;
        LegacyExportCustomerComboBox.ItemsSource = null;
        LegacyExportCustomerComboBox.SelectedItem = null;
        LegacyExportSubsetStatusText.Text = "固定列診断に合格すると、抽出対象を選択できます。";
        LegacyExportSubsetStatusText.Foreground = ToBrush("#52647A");
        CreateLegacyExportSubsetButton.IsEnabled = false;
        TransformationReadinessText.Text = "";
        CreateLegacyExportPreviewButton.IsEnabled = false;
        LegacyExportPreviewGrid.ItemsSource = null;
        LegacyExportPreviewStatusText.Text = "未作成";
        LegacyExportPreviewResultText.Text = "";
        LegacyExportPackagePathTextBox.Text = "";
        ReadLegacyExportPackageButton.IsEnabled = false;
        LegacyExportPackageStatusText.Text = "未読込";
        LegacyExportPackageResultText.Text = "";
        LegacyExportPackageRowsGrid.ItemsSource = null;
        ResetLegacyCandidateGraph("候補パッケージを再検証すると、ここにグラフを表示します。");
        unifiedImportOutputSession = null;
        fp5VehicleImageMapping = null;
        UnifiedImportCreateCanvasButton.IsEnabled = false;
        UnifiedImportCreateCanvasButton.Content = "解析を開始";
        UnifiedImportStatusText.Text = "ABACUSフォルダーを選択してください。";
        UnifiedImportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        UnifiedImportSummaryText.Text = "";
        UnifiedImportImageMappingStatusText.Text =
            "解析を開始すると、FP5内の車検証画像を自動復元して車両へ対応付けます。";
        UnifiedImportImageMappingStatusText.Foreground = ToBrush("#52647A");
        UnifiedImportImageMappingSummaryText.Text = "";
    }

    private void SelectLegacyExportFolderButton_Click(object sender, RoutedEventArgs e)
    {
        CancelLegacyExportFolderDetection();
        var dialog = new OpenFolderDialog
        {
            Title = "ABACUSの販売・整備・車両一覧CSVがあるフォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            legacyExportReadResult = null;
            legacyExportSubsetActive = false;
            LegacyExportPathTextBox.Text = dialog.FolderName;
            LegacyExportAutoDetectStatusText.Text = "CSVフォルダーを手動で選択しました。固定列を診断してください。";
            LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
            LegacyExportStatusText.Text = "未診断";
            LegacyExportFilesGrid.ItemsSource = null;
            LegacyExportColumnsGrid.ItemsSource = null;
            LegacyExportCustomerComboBox.ItemsSource = null;
            LegacyExportCustomerComboBox.SelectedItem = null;
            LegacyExportSubsetStatusText.Text = "固定列診断に合格すると、抽出対象を選択できます。";
            LegacyExportSubsetStatusText.Foreground = ToBrush("#52647A");
            CreateLegacyExportSubsetButton.IsEnabled = false;
            TransformationReadinessText.Text = "";
            CreateLegacyExportPreviewButton.IsEnabled = false;
            LegacyExportPreviewGrid.ItemsSource = null;
            LegacyExportPreviewStatusText.Text = "未作成";
            LegacyExportPreviewResultText.Text = "";
            LegacyExportPackagePathTextBox.Text = "";
            ReadLegacyExportPackageButton.IsEnabled = false;
            LegacyExportPackageStatusText.Text = "未読込";
            LegacyExportPackageResultText.Text = "";
            LegacyExportPackageRowsGrid.ItemsSource = null;
            ResetLegacyCandidateGraph("候補パッケージを再検証すると、ここにグラフを表示します。");
        }
    }

    private async void InspectLegacyExportsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LegacyExportPathTextBox.Text))
        {
            MessageBox.Show(this, "ABACUSエクスポートフォルダーを指定してください。", "フォルダー未選択");
            return;
        }

        await InspectLegacyExportsAsync(LegacyExportPathTextBox.Text.Trim(), automatic: false, CancellationToken.None);
    }

    private void LegacyExportCustomerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CreateLegacyExportSubsetButton is null || LegacyExportSubsetStatusText is null)
        {
            return;
        }

        if (LegacyExportCustomerComboBox.SelectedItem is AbacusLegacyExportCustomerOption option)
        {
            CreateLegacyExportSubsetButton.IsEnabled = legacyExportReadResult?.IsValid == true && option.HasVehicle;
            LegacyExportSubsetStatusText.Text = option.HasVehicle
                ? $"選択中: {option.DisplayName}（車両 {option.VehicleRows:N0} / 販売 {option.SalesRows:N0} / 整備 {option.MaintenanceRows:N0} 行）"
                : $"{option.DisplayName}は車両一覧に行がないため抽出できません。";
            LegacyExportSubsetStatusText.Foreground = ToBrush(option.HasVehicle ? "#1E40AF" : "#805B10");
        }
        else if (legacyExportReadResult?.IsValid == true)
        {
            CreateLegacyExportSubsetButton.IsEnabled = false;
            LegacyExportSubsetStatusText.Text = "抽出する顧客を選択してください。";
            LegacyExportSubsetStatusText.Foreground = ToBrush("#52647A");
        }
    }

    private static bool IsCustomerSubsetExport(
        string folderPath,
        AbacusLegacyExportReadResult result)
    {
        if (result.Files.Any(file => file.Kind == "車両一覧" &&
                                     !string.Equals(file.FileName, "syaryou.csv", StringComparison.OrdinalIgnoreCase) &&
                                     !string.Equals(file.FileName, "syaryou2.csv", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var manifestPath = Path.Combine(folderPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("kind", out var kind) &&
                   string.Equals(kind.GetString(), "abacus-export-customer-subset", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task InspectLegacyExportsAsync(string folderPath, bool automatic, CancellationToken cancellationToken)
    {
        var previousCustomerKey = (LegacyExportCustomerComboBox.SelectedItem as AbacusLegacyExportCustomerOption)?.Key;
        InspectLegacyExportsButton.IsEnabled = false;
        LegacyExportPathTextBox.IsEnabled = false;
        LegacyExportStatusText.Text = "固定列CSVをShift-JISとして厳格に診断しています…";
        try
        {
            var result = await legacyExportReader.ReadAsync(folderPath, cancellationToken);
            legacyExportReadResult = result;
            if (!legacyExportSubsetActive && IsCustomerSubsetExport(folderPath, result))
            {
                legacyExportSubsetActive = true;
            }
            var fileAnalyses = result.Files
                .Select(file => new AbacusLegacyExportFileAnalysis(
                    file.FileName,
                    file.Kind,
                    file.ExpectedColumns,
                    file.TotalRows,
                    file.ValidRows,
                    file.BlankRequiredRows,
                    file.InvalidDateRows,
                    file.Errors))
                .ToArray();
            LegacyExportFilesGrid.ItemsSource = fileAnalyses;
            LegacyExportColumnsGrid.ItemsSource = result.FirstRowSamples;
            IReadOnlyList<AbacusLegacyExportCustomerOption> customerOptions = result.IsValid
                ? legacyExportSubsetStore.GetCustomerOptions(result)
                : [];
            LegacyExportCustomerComboBox.ItemsSource = customerOptions;
            LegacyExportCustomerComboBox.SelectedItem = customerOptions.FirstOrDefault(option =>
                string.Equals(option.Key, previousCustomerKey, StringComparison.Ordinal));
            CreateLegacyExportSubsetButton.IsEnabled = result.IsValid &&
                                                        customerOptions.Any(option => option.HasVehicle) &&
                                                        LegacyExportCustomerComboBox.SelectedItem is AbacusLegacyExportCustomerOption;
            LegacyExportSubsetStatusText.Text = result.IsValid
                ? customerOptions.Count == 0
                    ? "顧客名が入った抽出対象を見つけられませんでした。"
                    : $"抽出候補: {customerOptions.Count:N0}件。顧客を選んで別フォルダーへ出力できます。"
                : "固定列診断に合格すると、抽出対象を選択できます。";
            LegacyExportSubsetStatusText.Foreground = ToBrush(result.IsValid ? "#1E40AF" : "#52647A");
            var errors = result.Errors.Count + result.Files.Sum(file => file.InvalidDateRows);
            LegacyExportStatusText.Text = result.IsValid
                ? "固定列形式の診断に合格しました。先頭行の列サンプルを表示しています。CSVへの書き込みは行っていません。"
                : $"構造エラーが{errors:N0}件あります。変換には使用できません。";
            LegacyExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(result.IsValid ? "#17643A" : "#A61B1B")!;
            var vehicleDecision = result.VehicleFileCount switch
            {
                0 => "車両一覧CSVがないため、書類のない車両を移行できません。",
                1 => $"車両一覧は{result.VehicleRows:N0}行です。全件出力であることをABACUS側で確認してください。",
                _ => $"車両一覧CSVが{result.VehicleFileCount:N0}ファイルあります（合計{result.VehicleRows:N0}行）。重複の可能性があるため自動結合しません。全件を含む1ファイルを選ぶ仕組みが必要です。",
            };
            TransformationReadinessText.Text =
                $"販売CSV: {result.SalesRows:N0}行 / 整備CSV: {result.MaintenanceRows:N0}行 / 車両CSV: {result.VehicleRows:N0}行\n" +
                $"{vehicleDecision}\n" +
                "下の列サンプルで金額・明細・日付の位置を確認してから、登録用データの候補化へ進みます。車両一覧が1ファイルで診断に合格した場合だけ、下の候補作成ボタンを押せます。必要なら上の抽出機能で1顧客分だけの検証用CSVを作成できます。";
            CreateLegacyExportPreviewButton.IsEnabled = result.IsValid && result.VehicleFileCount == 1;

            if (automatic)
            {
                LegacyExportAutoDetectStatusText.Text = result.IsValid
                    ? "自動検出したCSVの診断に合格しました。列サンプルを確認して候補作成へ進めます。"
                    : "自動検出したCSVの診断に失敗しました。内容を確認し、必要ならCSVフォルダーを手動で選択してください。";
                LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(result.IsValid ? "#17643A" : "#A61B1B")!;
            }
        }
        catch (OperationCanceledException)
        {
            if (automatic)
            {
                LegacyExportAutoDetectStatusText.Text = "CSV自動診断をキャンセルしました。CSVフォルダーを手動で選択できます。";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            LegacyExportStatusText.Text = $"固定列CSVを診断できません: {exception.Message}";
            LegacyExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            LegacyExportFilesGrid.ItemsSource = null;
            LegacyExportColumnsGrid.ItemsSource = null;
            legacyExportReadResult = null;
            LegacyExportCustomerComboBox.ItemsSource = null;
            LegacyExportCustomerComboBox.SelectedItem = null;
            LegacyExportSubsetStatusText.Text = "固定列診断に合格すると、抽出対象を選択できます。";
            LegacyExportSubsetStatusText.Foreground = ToBrush("#52647A");
            CreateLegacyExportSubsetButton.IsEnabled = false;
            TransformationReadinessText.Text = "";
            CreateLegacyExportPreviewButton.IsEnabled = false;
            if (automatic)
            {
                LegacyExportAutoDetectStatusText.Text = $"自動検出したCSVを診断できません: {exception.Message}。CSVフォルダーを手動で選択してください。";
                LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            }
        }
        finally
        {
            InspectLegacyExportsButton.IsEnabled = true;
            LegacyExportPathTextBox.IsEnabled = true;
        }
    }

    private async void CreateLegacyExportPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LegacyExportPathTextBox.Text))
        {
            MessageBox.Show(this, "先にABACUSエクスポートフォルダーを指定してください。", "フォルダー未選択");
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "登録前候補CSVの保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await CreateLegacyExportPreviewAsync(dialog.FolderName);
    }

    private async void CreateLegacyExportSubsetButton_Click(object sender, RoutedEventArgs e)
    {
        if (legacyExportReadResult is null || !legacyExportReadResult.IsValid)
        {
            MessageBox.Show(this, "先に固定列CSVの診断を完了してください。", "CSV未診断");
            return;
        }

        if (LegacyExportCustomerComboBox.SelectedItem is not AbacusLegacyExportCustomerOption option)
        {
            MessageBox.Show(this, "抽出する顧客を選択してください。", "顧客未選択");
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "顧客別検証用CSVの保存先を選択（原本とは別のフォルダー）",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        CreateLegacyExportSubsetButton.IsEnabled = false;
        InspectLegacyExportsButton.IsEnabled = false;
        LegacyExportPathTextBox.IsEnabled = false;
        LegacyExportSubsetStatusText.Text = $"{option.DisplayName}の行を抽出し、作成後の再読込検証を実行しています…";
        LegacyExportSubsetStatusText.Foreground = ToBrush("#52647A");
        try
        {
            var result = await legacyExportSubsetStore.CreateAsync(
                LegacyExportPathTextBox.Text.Trim(),
                dialog.FolderName,
                option.Key);
            legacyExportSubsetActive = true;
            LegacyExportPathTextBox.Text = result.PackagePath;
            LegacyExportAutoDetectStatusText.Text = "顧客別の検証用CSVを選択中です。原本フォルダーは変更していません。";
            LegacyExportAutoDetectStatusText.Foreground = ToBrush("#1E40AF");
            await InspectLegacyExportsAsync(result.PackagePath, automatic: false, CancellationToken.None);
            LegacyExportCustomerComboBox.SelectedItem =
                (LegacyExportCustomerComboBox.ItemsSource as IEnumerable<AbacusLegacyExportCustomerOption>)
                ?.FirstOrDefault(item => string.Equals(item.Key, option.Key, StringComparison.Ordinal));
            LegacyExportSubsetStatusText.Text =
                $"抽出済み: {result.CustomerName}{(string.IsNullOrEmpty(result.CustomerAddress) ? "" : $"（住所: {result.CustomerAddress}）")}\n" +
                $"車両 {result.VehicleRows:N0}行 / 販売 {result.SalesRows:N0}行 / 整備 {result.MaintenanceRows:N0}行\n" +
                $"保存先: {result.PackagePath}\n" +
                "原本は変更していません。このフォルダーを選択した状態で、登録前候補の作成や解析を実行できます。";
            LegacyExportSubsetStatusText.Foreground = ToBrush("#17643A");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            LegacyExportSubsetStatusText.Text = $"顧客別CSVを作成できません: {exception.Message}";
            LegacyExportSubsetStatusText.Foreground = ToBrush("#A61B1B");
        }
        finally
        {
            InspectLegacyExportsButton.IsEnabled = true;
            LegacyExportPathTextBox.IsEnabled = true;
            CreateLegacyExportSubsetButton.IsEnabled = legacyExportReadResult?.IsValid == true &&
                                                        legacyExportSubsetStore.GetCustomerOptions(legacyExportReadResult).Any(option => option.HasVehicle);
            UpdateUnifiedImportEntryState();
        }
    }

    private async Task CreateLegacyExportPreviewAsync(string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(LegacyExportPathTextBox.Text))
        {
            return;
        }

        CreateLegacyExportPreviewButton.IsEnabled = false;
        InspectLegacyExportsButton.IsEnabled = false;
        LegacyExportPathTextBox.IsEnabled = false;
        LegacyExportPreviewGrid.ItemsSource = null;
        LegacyExportPackagePathTextBox.Text = "";
        ReadLegacyExportPackageButton.IsEnabled = false;
        SelectLegacyExportPackageFolderButton.IsEnabled = false;
        LegacyExportPackageStatusText.Text = "未読込";
        LegacyExportPackageResultText.Text = "";
        LegacyExportPackageRowsGrid.ItemsSource = null;
        ResetLegacyCandidateGraph("候補パッケージを再検証すると、ここにグラフを表示します。");
        LegacyExportPreviewStatusText.Text = "固定列CSVを再検証し、登録前候補CSVを作成しています…";
        LegacyExportPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        LegacyExportPreviewResultText.Text = "";
        try
        {
            var result = await legacyExportPreviewStore.CreateAsync(
                LegacyExportPathTextBox.Text.Trim(),
                destinationFolder);
            LegacyExportPreviewGrid.ItemsSource = result.PreviewRows;
            LegacyExportPreviewStatusText.Text = "登録前候補CSVを作成し、作成後の再読込検証に合格しました。登録・API送信・画像アップロードは行っていません。";
            LegacyExportPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            LegacyExportPreviewResultText.Text =
                $"保存先: {result.PackagePath}\n" +
                $"マニフェスト: {result.ManifestPath}\n" +
                $"顧客: {result.CustomerRowCount:N0}行 / 車両: {result.VehicleRowCount:N0}行 / 販売書類: {result.SalesRowCount:N0}行 / 整備書類: {result.MaintenanceRowCount:N0}行\n" +
                $"顧客名空欄（書類除外・車両は未確定保持）: {result.SkippedBlankCustomerRows:N0}行 / 整備書類の車両未確定で除外: {result.SkippedMaintenanceWithoutVehicleRows:N0}行 / 要確認の車両紐付け: {result.AmbiguousVehicleRows:N0}行\n" +
                $"Gate 19明細: 対応付け済み {result.DetailMappedDocumentCount:N0}件 / 要確認 {result.DetailReviewDocumentCount:N0}件 / 未対応 {result.DetailUnsupportedDocumentCount:N0}件 / 除外行 {result.DetailExcludedRowCount:N0}件 / 金額のみ行 {result.AmountOnlyDetailRowCount:N0}件\n" +
                $"マニフェスト SHA-256: {result.ManifestSha256}";
            LegacyExportPackagePathTextBox.Text = result.PackagePath;
            ReadLegacyExportPackageButton.IsEnabled = true;
            LegacyExportPackageStatusText.Text = "候補パッケージを読み込んで再検証できます。";
            LegacyExportPackageStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#1E40AF")!;
            await ReadLegacyExportPackageAsync(result.PackagePath, automatic: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            LegacyExportPreviewStatusText.Text = $"登録前候補CSVを作成できません: {exception.Message}";
            LegacyExportPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            InspectLegacyExportsButton.IsEnabled = true;
            LegacyExportPathTextBox.IsEnabled = true;
            CreateLegacyExportPreviewButton.IsEnabled = false;
            SelectLegacyExportPackageFolderButton.IsEnabled = true;
            UpdateUnifiedImportEntryState();
        }
    }

    private void SelectLegacyExportPackageFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "ABACUS登録前候補パッケージを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LegacyExportPackagePathTextBox.Text = dialog.FolderName;
        ReadLegacyExportPackageButton.IsEnabled = true;
        LegacyExportPackageStatusText.Text = "候補パッケージを選択しました。再検証を実行してください。";
        LegacyExportPackageStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        LegacyExportPackageResultText.Text = "";
        LegacyExportPackageRowsGrid.ItemsSource = null;
        ResetLegacyCandidateGraph("候補パッケージを再検証すると、ここにグラフを表示します。");
    }

    private async void ReadLegacyExportPackageButton_Click(object sender, RoutedEventArgs e)
    {
        var packagePath = LegacyExportPackagePathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            MessageBox.Show(this, "登録前候補パッケージを選択してください。", "候補未選択");
            return;
        }

        await ReadLegacyExportPackageAsync(packagePath, automatic: false);
    }

    private async Task<bool> ReadLegacyExportPackageAsync(
        string packagePath,
        bool automatic,
        LegacyGraphWorkCheckpoint? checkpoint = null)
    {
        var succeeded = false;
        ReadLegacyExportPackageButton.IsEnabled = false;
        SelectLegacyExportPackageFolderButton.IsEnabled = false;
        LegacyExportPackagePathTextBox.IsEnabled = false;
        LegacyExportPackageRowsGrid.ItemsSource = null;
        LegacyExportPackageResultText.Text = "";
        if (checkpoint is null)
        {
            ResetLegacyCandidateGraph("候補パッケージを再検証すると、ここにグラフを表示します。");
        }
        LegacyExportPackageStatusText.Text = "マニフェストと候補CSVを再検証しています…";
        LegacyExportPackageStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await legacyExportPackageReader.ReadAsync(packagePath);
            if (checkpoint is not null &&
                !string.Equals(result.ManifestSha256, checkpoint.CandidateManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "候補パッケージのマニフェストSHA-256が作業チェックポイントと一致しません。"
                    + "候補CSVを変更せず、元の作業フォルダーを選択してください。");
            }
            LegacyExportPackageRowsGrid.ItemsSource = result.Rows;
            var statusSummary = result.Rows
                .GroupBy(row => row.MatchStatus, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}:{group.Count():N0}");
            var warningSummary = result.Warnings.Count == 0
                ? "なし"
                : string.Join(" / ", result.Warnings.Take(3));
            LegacyExportPackageStatusText.Text = automatic
                ? "候補パッケージを自動で再検証し、グラフを表示しています。登録・API送信・画像アップロードは行っていません。"
                : "候補パッケージの再検証に合格しました。登録・API送信・画像アップロードは行っていません。";
            LegacyExportPackageStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            LegacyExportPackageResultText.Text =
                $"顧客: {result.CustomerRowCount:N0}行 / 車両: {result.VehicleRowCount:N0}行 / 販売書類: {result.SalesRowCount:N0}行 / 整備書類: {result.MaintenanceRowCount:N0}行\n" +
                $"顧客名空欄（書類除外・車両は未確定保持）: {result.SkippedBlankCustomerRows:N0}行 / 整備書類の車両未確定で除外: {result.SkippedMaintenanceWithoutVehicleRows:N0}行 / 要確認の車両紐付け: {result.AmbiguousVehicleRows:N0}行\n" +
                $"表示行の判定: {string.Join(" / ", statusSummary)}\n" +
                $"CSV: {string.Join(" / ", result.DataFiles.Select(file => $"{file.FileName} {file.SizeBytes:N0} bytes"))}\n" +
                $"マニフェスト SHA-256: {result.ManifestSha256}\n" +
                $"警告: {warningSummary}";
            try
            {
                var graph = await legacyExportCandidateGraphService.BuildAsync(result);
                // 候補パッケージの再読込とグラフ構築に合格してから、画面上の
                // グラフを置き換えます。再開に失敗した場合は既存のグラフを残します。
                ResetLegacyCandidateGraph("候補パッケージを再検証すると、ここにグラフを表示します。");
                legacyExportCandidateGraphResult = graph;
                RebuildLegacyGraphRecommendationCandidates();
                legacyGraphRecommendationDecisions.Clear();
                legacyGraphManualDocumentLinks.Clear();
                legacyGraphManualVehicleCustomerLinks.Clear();
                legacyGraphManualDocumentCustomerLinks.Clear();
                legacyGraphDocumentLinkMethods.Clear();
                legacyGraphDocumentLinkReasons.Clear();
                legacyGraphUnconnectedDocumentKeys.Clear();
                legacyGraphTrayDocumentKeys.Clear();
                legacyGraphExcludedDocumentKeys.Clear();
                legacyGraphTrayVehicleIds.Clear();
                legacyGraphTrashCustomerIds.Clear();
                legacyGraphTrashVehicleIds.Clear();
                legacyGraphTrashDocumentKeys.Clear();
                legacyGraphImportConfirmed = false;
                legacyGraphCustomerMergeDrafts.Clear();
                legacyGraphAppliedCustomerMergeKeys.Clear();
                legacyGraphVirtualCustomerMergeKeys.Clear();
                legacyGraphCustomerGroupExpanded.Clear();
                legacyGraphLogicalCustomerMergeGroupByCustomerId.Clear();
                legacyGraphMatchingManualCustomerCandidateTargets.Clear();
                legacyGraphCustomerApprovalStates.Clear();
                legacyGraphCustomerReviewStates.Clear();
                InitializeLegacyGraphCustomerMergeGroups();
                legacyGraphSelectedItem = null;
                legacyGraphSelectedWorkGroupKey = null;
                RefreshLegacyGraphCustomerList();
                RefreshLegacyGraphUnresolvedVehicleList();
                RefreshLegacyGraphUnresolvedDocumentLists();
                RefreshLegacyGraphTrashLists();
                if (checkpoint is not null)
                {
                    RestoreLegacyGraphWorkCheckpoint(checkpoint, result, graph);
                }
                LegacyGraphStatusText.Text =
                    $"{(checkpoint is null ? "グラフを作成しました" : "作業チェックポイントを検証し、グラフを再開しました")}。" +
                    $"顧客 {graph.Customers.Count:N0}件 / 車両 {graph.Customers.Sum(customer => customer.Vehicles.Count):N0}台 / 書類 {graph.AllDocuments.Count:N0}件。" +
                    $"未確定車両 {GetLegacyGraphUnresolvedVehicleCount():N0}件 / 未確定トレイ {graph.AllDocuments.Count(IsLegacyGraphDocumentInTray):N0}件。" +
                    $"実線 {graph.SolidLinkCount:N0}件 / 要確認 {graph.ReviewLinkCount:N0}件 / 未確定 {graph.UnmatchedDocumentCount:N0}件。" +
                    $"Gate28おすすめ {legacyGraphRecommendationCandidates.Count(candidate => candidate.IsEligible):N0}件 / 競合候補 {legacyGraphRecommendationCandidates.Count(candidate => !candidate.IsEligible):N0}件。";
                LegacyGraphStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
                LegacyGraphLegendText.Text =
                    "車両右側と書類左側の●が接続ノードです。緑の実線は自動確定、青の点線は仮紐付け、赤の点線は未接続です。顧客・書類カードはドラッグできます。すべての操作が終わったら、下部の「インポート内容を確定」を押してください。";
                UpdateLegacyGraphImportConfirmationButton();
                ScheduleLegacyGraphCheckpointSave();
                succeeded = true;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
            {
                if (checkpoint is null)
                {
                    ResetLegacyCandidateGraph($"グラフの作成に失敗しました: {exception.Message}");
                }
                else
                {
                    LegacyGraphStatusText.Text = $"作業チェックポイントの再開に失敗しました: {exception.Message}";
                }
                LegacyGraphStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            LegacyExportPackageRowsGrid.ItemsSource = null;
            if (checkpoint is null)
            {
                ResetLegacyCandidateGraph("候補パッケージの再検証に失敗したため、グラフをクリアしました。");
            }
            else
            {
                LegacyGraphStatusText.Text = $"作業チェックポイントの再開に失敗しました: {exception.Message}";
                LegacyGraphStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            }
            LegacyExportPackageStatusText.Text = $"候補パッケージの再検証に失敗しました: {exception.Message}";
            LegacyExportPackageStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            LegacyExportPackageResultText.Text = "ハッシュ、見出し、列数、行数のいずれかが一致しない可能性があります。元の候補パッケージを変更せず、再作成してください。";
        }
        finally
        {
            ReadLegacyExportPackageButton.IsEnabled = true;
            SelectLegacyExportPackageFolderButton.IsEnabled = true;
            LegacyExportPackagePathTextBox.IsEnabled = true;
        }

        return succeeded;
    }

    private async void InspectAbacusMenuButton_Click(object sender, RoutedEventArgs e)
    {
        InspectAbacusMenuButton.IsEnabled = false;
        AbacusMenuItemsGrid.ItemsSource = null;
        AbacusMenuStatusText.Text = "コピー側ABACUSの標準Windowsメニューを読み取り診断しています…";
        AbacusMenuStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await session.InspectAbacusMenuAsync();
            abacusMayBeRunning = result.IsRunning;
            AbacusMenuItemsGrid.ItemsSource = result.MenuItems ?? [];
            AbacusMenuStatusText.Text = result.Message;
            AbacusMenuStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(
                result.Status == "standard-menu-ready" ? "#17643A" : "#805B10")!;
        }
        catch (Exception exception)
        {
            AbacusMenuStatusText.Text = $"メニュー診断に失敗しました: {exception.Message}";
            AbacusMenuStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            SetAbacusButtonsBusy(false);
        }
    }

    private void LegacyGraphModeButton_Click(object sender, RoutedEventArgs e) =>
        SetLegacyGraphUiMode("graph");

    private void LegacyMatchingModeButton_Click(object sender, RoutedEventArgs e) =>
        SetLegacyGraphUiMode("matching");

    private void SetLegacyGraphUiMode(string mode, bool scheduleCheckpoint = true)
    {
        var isMatching = string.Equals(mode, "matching", StringComparison.OrdinalIgnoreCase);
        var wasMatching = string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase);
        legacyGraphUiMode = isMatching ? "matching" : "graph";
        if (LegacyGraphWorkspaceGrid is null || LegacyGraphMatchingWorkspace is null)
        {
            return;
        }

        if (isMatching && !wasMatching)
        {
            // 前回のマッチング表示で設定したページ全体の固定高さを、
            // 新しい候補データを測定する前に一度だけ解除します。
            LegacyGraphMatchingPageGrid?.ClearValue(FrameworkElement.HeightProperty);
        }

        // マッチングUIも同じキャンバスを使い、左の統合候補欄と右のおすすめ欄だけを切り替えます。
        LegacyGraphWorkspaceGrid.Visibility = Visibility.Visible;
        LegacyGraphInspectorColumn.Width = new GridLength(
            isMatching
                ? LegacyMatchingInspectorColumnWidth
                : LegacyGraphInspectorColumnWidth);
        LegacyGraphCustomersList.Visibility = isMatching ? Visibility.Collapsed : Visibility.Visible;
        LegacyMatchingCustomerQueueList.Visibility = isMatching ? Visibility.Visible : Visibility.Collapsed;
        LegacyMatchingTopHeader.Visibility = isMatching ? Visibility.Visible : Visibility.Collapsed;
        LegacyGraphMatchingWorkspace.Visibility = Visibility.Collapsed;
        LegacyGraphInspectorLayer.Visibility = Visibility.Visible;
        LegacyGraphInspectorScrollViewer.Visibility = isMatching ? Visibility.Collapsed : Visibility.Visible;
        LegacyMatchingSidePanel.Visibility = isMatching ? Visibility.Visible : Visibility.Collapsed;
        // 未確定の状態は共通ですが、顧客マッチングUIでは右側の未確定タブへ一本化し、
        // キャンバス下の従来トレイとの二重表示を避けます。
        LegacyGraphUnresolvedTraySection.Visibility = isMatching
            ? Visibility.Collapsed
            : Visibility.Visible;
        LegacyGraphModeButton.IsEnabled = true;
        LegacyMatchingModeButton.IsEnabled = true;
        LegacyGraphModeButton.IsChecked = !isMatching;
        LegacyMatchingModeButton.IsChecked = isMatching;
        LegacyGraphModeButton.Content = isMatching ? "グラフUIへ戻る" : "グラフUI（表示中）";
        LegacyMatchingModeButton.Content = isMatching ? "顧客マッチングUI（表示中）" : "顧客マッチングUI";

        if (isMatching)
        {
            LegacyGraphCustomerListTitleText.Text = "統合候補欄";
            LegacyGraphCustomerListDescriptionText.Text =
                "基準顧客・追加統合済み・現在確認中・未処理・保留を分けて表示します。統合済みの構成顧客は通常の巡回対象から除外されます。";
            LegacyGraphLegendText.Text =
                "中央キャンバスはグラフUIと共通です。顧客確認済みは青で表示します。おすすめ以外の車両・書類は、右側の未確定タブから検索してキャンバスへドラッグできます。";
            EnsureLegacyGraphMatchingCustomerSelection(
                GetLegacyGraphCustomerForCurrentSelection(),
                allowBaseCustomerChange: true);
            RefreshLegacyGraphCustomerList();
            SetLegacyMatchingSideTab(recommendationsVisible: true);
            RefreshLegacyMatchingView();
        }
        else
        {
            LegacyGraphCustomerListTitleText.Text = "顧客を選択";
            LegacyGraphCustomerListDescriptionText.Text =
                "顧客単位の確認完了は青、内容変更後の再確認待ちは橙で表示します。顧客カードを別の顧客へドラッグすると、異なる名前でも候補に追加できます。";
            LegacyGraphLegendText.Text =
                "顧客は確認完了を青、未確認を通常色で表示します。車両右側と書類左側の●が接続ノードです。緑の実線は自動確定、青の点線は仮紐付け、赤の点線は未接続です。下部の未確定トレイからもキャンバスへ接続できます。";
            var selectedGraphCustomer = string.IsNullOrWhiteSpace(legacyGraphMatchingCustomerId)
                ? GetLegacyGraphCustomerForCurrentSelection()
                : FindLegacyGraphCustomerById(legacyGraphMatchingCustomerId);
            RefreshLegacyGraphCustomerList(selectedGraphCustomer is null
                ? null
                : GetLegacyGraphCustomerListEntryId(selectedGraphCustomer));
            if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphCustomer selectedGraphItemCustomer)
            {
                var displayCustomer = GetLegacyGraphDisplayCustomer(selectedGraphItemCustomer);
                legacyGraphSelectedItem = displayCustomer;
                UpdateLegacyGraphInspector(displayCustomer);
                RenderLegacyGraphCustomer(displayCustomer);
            }
            else if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphVehicle selectedVehicle)
            {
                UpdateLegacyGraphInspector(selectedVehicle);
            }
            else if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphDocument selectedDocument)
            {
                UpdateLegacyGraphInspector(selectedDocument);
            }
            else if (selectedGraphCustomer is not null)
            {
                var displayCustomer = GetLegacyGraphDisplayCustomer(selectedGraphCustomer);
                legacyGraphSelectedItem = displayCustomer;
                UpdateLegacyGraphInspector(displayCustomer);
                RenderLegacyGraphCustomer(displayCustomer);
            }
        }

        RefreshLegacyMatchingChanges();
        RefreshLegacyMatchingMergeRemovalButton();
        ScheduleLegacyGraphWorkspaceHeightUpdate();

        if (scheduleCheckpoint)
        {
            ScheduleLegacyGraphCheckpointSave();
        }
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> GetLegacyGraphMatchingCustomers()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        var pendingAutomaticRelations = legacyGraphRecommendationCandidates
            .Where(candidate => LegacyMatchingCategoryKinds.GetKind(candidate) == LegacyMatchingCategoryKinds.Customer)
            .Where(candidate => IsLegacyGraphRecommendationActive(candidate))
            .Where(candidate => FindLegacyGraphCustomerById(candidate.SubjectId) is not null &&
                                FindLegacyGraphCustomerById(candidate.TargetId) is not null)
            .Select(candidate => new LegacyMatchingCustomerMergeRelation(
                candidate.SubjectId,
                candidate.TargetId,
                candidate.Origin,
                GetLegacyGraphRecommendationDecision(candidate)))
            .ToArray();
        var pendingAutomaticCustomerIds = LegacyMatchingWorkflow.GetPendingAutomaticCustomerIdsToHide(
            pendingAutomaticRelations);
        var temporaryMergeGroups = legacyGraphCustomerMergeGroups.Values
            .Where(group => group.CustomerIds.Count > 1 &&
                            string.Equals(group.Origin, "manual", StringComparison.Ordinal) &&
                            !IsLegacyGraphLogicalCustomerGroup(group.GroupId) &&
                            !legacyGraphAppliedCustomerMergeKeys.Contains(group.GroupId))
            .ToArray();
        var temporaryMergeCustomerIds = temporaryMergeGroups
            .SelectMany(group => group.CustomerIds)
            .ToHashSet(StringComparer.Ordinal);
        var temporaryMergeRepresentativeIds = temporaryMergeGroups
            .Select(group => group.CustomerIds.FirstOrDefault())
            .Where(customerId => !string.IsNullOrWhiteSpace(customerId))
            .ToHashSet(StringComparer.Ordinal);
        var selectedBaseCustomerId = legacyGraphMatchingCustomerId;

        // 未処理の自動統合候補は、候補元の顧客単位で確認します。
        // 候補先を独立顧客としてもう一度上部の巡回へ出すと、同じ候補が重複して表示されるため、
        // 保留中も含めて候補先だけを巡回対象から除外します。統合後の論理顧客は従来どおり一つにまとめます。
        return legacyExportCandidateGraphResult.Customers
            .Where(customer => !legacyGraphTrashCustomerIds.Contains(customer.CustomerId))
            .Where(customer => !pendingAutomaticCustomerIds.Contains(customer.CustomerId) ||
                               string.Equals(
                                   customer.CustomerId,
                                   selectedBaseCustomerId,
                                   StringComparison.Ordinal))
            .Where(customer => !temporaryMergeCustomerIds.Contains(customer.CustomerId) ||
                               string.Equals(customer.CustomerId, selectedBaseCustomerId, StringComparison.Ordinal) ||
                               string.IsNullOrWhiteSpace(selectedBaseCustomerId) &&
                               temporaryMergeRepresentativeIds.Contains(customer.CustomerId))
            .GroupBy(GetLegacyGraphLogicalCustomerKey, StringComparer.Ordinal)
            .Select(group =>
            {
                // 統合後も、現在の巡回基準顧客が論理グループの先頭とは限りません。
                // 基準顧客が明示的に選択されている場合は、その顧客を表示対象として維持します。
                var selectedBaseCustomer = string.IsNullOrWhiteSpace(selectedBaseCustomerId)
                    ? null
                    : group.FirstOrDefault(customer => string.Equals(
                        customer.CustomerId,
                        selectedBaseCustomerId,
                        StringComparison.Ordinal));
                if (selectedBaseCustomer is not null)
                {
                    return selectedBaseCustomer;
                }

                if (legacyGraphCustomerMergeGroups.TryGetValue(group.Key, out var logicalGroup))
                {
                    var firstId = logicalGroup.CustomerIds.FirstOrDefault();
                    return group.FirstOrDefault(customer => string.Equals(customer.CustomerId, firstId, StringComparison.Ordinal)) ??
                           group.First();
                }

                return group.First();
            })
            .ToArray();
    }

    private AbacusLegacyExportCandidateGraphCustomer? GetLegacyGraphMatchingCustomer()
    {
        var customers = GetLegacyGraphMatchingCustomers();
        if (customers.Count == 0)
        {
            legacyGraphMatchingCustomerIndex = -1;
            legacyGraphMatchingCustomerId = null;
            return null;
        }

        var selectedById = string.IsNullOrWhiteSpace(legacyGraphMatchingCustomerId)
            ? null
            : customers.FirstOrDefault(customer => string.Equals(
                customer.CustomerId,
                legacyGraphMatchingCustomerId,
                StringComparison.Ordinal));
        if (selectedById is not null)
        {
            legacyGraphMatchingCustomerIndex = customers
                .Select((customer, index) => new { customer, index })
                .First(item => ReferenceEquals(item.customer, selectedById))
                .index;
            return selectedById;
        }

        legacyGraphMatchingCustomerIndex = Math.Clamp(legacyGraphMatchingCustomerIndex, 0, customers.Count - 1);
        legacyGraphMatchingCustomerId = customers[legacyGraphMatchingCustomerIndex].CustomerId;
        return customers[legacyGraphMatchingCustomerIndex];
    }

    private void EnsureLegacyGraphMatchingCustomerSelection(
        AbacusLegacyExportCandidateGraphCustomer? preferredCustomer = null,
        bool allowBaseCustomerChange = false)
    {
        var previousCustomerId = legacyGraphMatchingCustomerId;
        if (allowBaseCustomerChange && preferredCustomer is not null)
        {
            legacyGraphMatchingCustomerId = preferredCustomer.CustomerId.StartsWith(
                    "merge-preview:",
                    StringComparison.Ordinal)
                ? GetLegacyGraphSourceCustomer(preferredCustomer).CustomerId
                : preferredCustomer.CustomerId;
        }

        var customers = GetLegacyGraphMatchingCustomers();
        if (customers.Count == 0)
        {
            legacyGraphMatchingCustomerIndex = -1;
            legacyGraphMatchingCustomerId = null;
            legacyGraphMatchingSelectedMergeMemberCustomerId = null;
            return;
        }

        var index = string.IsNullOrWhiteSpace(legacyGraphMatchingCustomerId)
            ? -1
            : customers.Select((customer, customerIndex) => new { customer, customerIndex })
                .FirstOrDefault(item => string.Equals(
                    item.customer.CustomerId,
                    legacyGraphMatchingCustomerId,
                    StringComparison.Ordinal))?.customerIndex ?? -1;
        legacyGraphMatchingCustomerIndex = index >= 0
            ? index
            : Math.Clamp(legacyGraphMatchingCustomerIndex, 0, customers.Count - 1);
        legacyGraphMatchingCustomerId = customers[legacyGraphMatchingCustomerIndex].CustomerId;
        if (!string.Equals(previousCustomerId, legacyGraphMatchingCustomerId, StringComparison.Ordinal))
        {
            legacyGraphMatchingSelectedMergeMemberCustomerId = null;
        }
        legacyGraphSelectedItem = customers[legacyGraphMatchingCustomerIndex];
    }

    private void RefreshLegacyMatchingView()
    {
        // マッチングUIの再描画では、内部でグラフ側の顧客一覧を一度クリアしてから
        // マッチング候補一覧を設定します。グラフUI表示中にこれを呼ぶと、顧客一覧だけが
        // 空になるため、表示モードがマッチングUIのときだけ実行します。
        if (!string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase) ||
            LegacyMatchingRecommendationsList is null)
        {
            return;
        }

        RefreshLegacyMatchingUnresolvedList();

        var customer = GetLegacyGraphMatchingCustomer();
        if (customer is null)
        {
            legacyGraphSelectedRecommendation = null;
            ClearLegacyMatchingRecommendationPreview();
            SetLegacyMatchingHeader(
                "顧客を選択してください",
                "顧客 0 / 0",
                "候補パッケージを読み込むと、顧客単位のマッチング確認を開始できます。",
                hasCustomer: false,
                canGoPrevious: false,
                canGoNext: false);
            LegacyMatchingConfirmedVehiclesItemsControl.ItemsSource = null;
            LegacyMatchingConfirmedDocumentsItemsControl.ItemsSource = null;
            LegacyMatchingRecommendationsList.ItemsSource = null;
            LegacyMatchingRecommendationStatusText.Text = "おすすめ候補はありません。";
            LegacyMatchingConfirmedEmptyText.Visibility = Visibility.Visible;
            LegacyMatchingSideCustomerText.Text = "対象顧客なし";
            LegacyMatchingSideProgressText.Text = "候補 0件";
            LegacyMatchingCurrentCategoryText.Text = "顧客統合 / 0件";
            LegacyMatchingCurrentProgressText.Text = "0 / 0";
            LegacyMatchingCurrentTargetHeadingText.Text = "紐付け先";
            LegacyMatchingCurrentTargetText.Text = "候補パッケージを読み込むと、おすすめ欄を表示できます。";
            LegacyMatchingCurrentSubjectHeadingText.Text = "候補";
            LegacyMatchingCurrentSubjectText.Text = "顧客を選択してください";
            LegacyMatchingCurrentRelationArrowText.Text = "←";
            LegacyMatchingCurrentMatchedText.Text = "一致: なし";
            LegacyMatchingCurrentDifferenceText.Text = "差異: なし";
            LegacyMatchingCurrentMissingText.Text = "不足情報: なし";
            LegacyMatchingCurrentConflictText.Text = "問題: なし";
            LegacyMatchingCurrentReasonText.Text = "";
            LegacyMatchingCurrentDecisionText.Text = "";
            LegacyMatchingCurrentAutoDecisionReasonText.Text = "";
            LegacyMatchingDetailsSubjectSummaryText.Text = "";
            LegacyMatchingDetailsSourceHeadingText.Text = "";
            LegacyMatchingDetailsCandidateHeadingText.Text = "";
            LegacyMatchingDetailsDecisionText.Text = "";
            LegacyMatchingDetailsReasonText.Text = "";
            LegacyMatchingDetailsAutoDecisionText.Text = "";
            LegacyMatchingDetailsSourceItemsControl.ItemsSource = null;
            LegacyMatchingDetailsCandidateItemsControl.ItemsSource = null;
            LegacyMatchingDetailsComparisonItemsControl.ItemsSource = null;
            LegacyMatchingDetailsEvidenceItemsControl.ItemsSource = null;
            LegacyMatchingDetailsDifferenceItemsControl.ItemsSource = null;
            LegacyMatchingDetailsMissingItemsControl.ItemsSource = null;
            LegacyMatchingDetailsConflictItemsControl.ItemsSource = null;
            LegacyMatchingDetailsAlternativesItemsControl.ItemsSource = null;
            LegacyMatchingDetailsInternalItemsControl.ItemsSource = null;
            LegacyMatchingDetailsAlternativesHeadingText.Text = "";
            LegacyMatchingDetailsAlternativesPlaceholderText.Text = "";
            LegacyMatchingDetailsMergePreviewText.Text = "";
            LegacyMatchingDetailsMergePreviewSection.Visibility = Visibility.Collapsed;
            LegacyMatchingCurrentMatchedItemsControl.ItemsSource = null;
            LegacyMatchingCurrentDifferenceItemsControl.ItemsSource = null;
            LegacyMatchingCurrentMissingItemsControl.ItemsSource = null;
            LegacyMatchingCurrentConflictItemsControl.ItemsSource = null;
            LegacyMatchingCurrentDifferenceBorder.Visibility = Visibility.Collapsed;
            LegacyMatchingCurrentMissingBorder.Visibility = Visibility.Collapsed;
            LegacyMatchingCurrentConflictBorder.Visibility = Visibility.Collapsed;
            LegacyMatchingNoRecommendationText.Text = "顧客を選択すると、おすすめ欄を表示できます。";
            LegacyMatchingNoRecommendationText.Visibility = Visibility.Visible;
            LegacyMatchingDetailsButton.IsEnabled = false;
            ApplyLegacyMatchingDecisionBadgeStyle(null);
            SetLegacyMatchingDetailsVisible(false);
            LegacyMatchingSideApproveButton.IsEnabled = false;
            LegacyMatchingSideRejectButton.IsEnabled = false;
            LegacyMatchingSideHoldButton.IsEnabled = false;
            LegacyMatchingSidePreviousButton.IsEnabled = false;
            LegacyMatchingSideNextButton.IsEnabled = false;
            RefreshLegacyMatchingChanges();
            RefreshLegacyMatchingRecommendationActions();
            LegacyMatchingPreviousButton.IsEnabled = false;
            LegacyMatchingNextButton.IsEnabled = false;
            return;
        }

        var customers = GetLegacyGraphMatchingCustomers();
        // マッチングUIの巡回単位は、統合後もユーザーが選択した基準顧客です。
        // 表示情報だけは、同じ論理顧客グループの採用済み表示モデルを参照します。
        var sourceCustomer = customer;
        var displayCustomer = GetLegacyGraphDisplayCustomer(sourceCustomer);
        var confirmedVehicles = GetLegacyGraphVehiclesForDisplay(customer)
            .Select(vehicle => new LegacyMatchingVehicleCard(
                $"{Fallback(vehicle.Maker)} {vehicle.DisplayName}".Trim(),
                string.Join(" / ", new[]
                {
                    string.IsNullOrWhiteSpace(vehicle.Model) ? "" : $"型式 {vehicle.Model}",
                    string.IsNullOrWhiteSpace(vehicle.RegistrationNumber) ? "" : $"登録 {vehicle.RegistrationNumber}",
                    string.IsNullOrWhiteSpace(vehicle.ChassisNumber) ? "" : $"車台 {vehicle.ChassisNumber}",
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
                $"確定書類 {GetDocumentsForVehicle(vehicle).Count(document => IsLegacyGraphConfirmedDocumentForMatching(document)):N0}件 / {vehicle.SourceLocation}"))
            .ToArray();
        var confirmedDocuments = GetLegacyGraphConfirmedDocumentsForMatching(customer)
            .Select(item => new LegacyMatchingDocumentCard(
                $"{item.Document.Kind} {Fallback(item.Document.DocumentNumber)}",
                $"{item.OwnerLabel} / {Fallback(item.Document.DocumentDate)} / {Fallback(item.Document.TotalAmount)}"))
            .ToArray();
        var allRecommendations = GetLegacyGraphMatchingRecommendations(customer);
        var recommendationItems = allRecommendations
            .Select(candidate => CreateLegacyMatchingRecommendationItem(candidate, sourceCustomer))
            .ToArray();

        SetLegacyMatchingHeader(
            GetLegacyGraphCustomerDisplayName(displayCustomer),
            $"顧客 {legacyGraphMatchingCustomerIndex + 1:N0} / {customers.Count:N0}",
            $"ふりがな: {Fallback(displayCustomer.NameKana)} / " +
            $"電話: {Fallback(displayCustomer.PhoneNumber)} / " +
            $"住所: {Fallback(displayCustomer.Address)}",
            hasCustomer: true,
            canGoPrevious: legacyGraphMatchingCustomerIndex > 0,
            canGoNext: legacyGraphMatchingCustomerIndex >= 0 &&
                       legacyGraphMatchingCustomerIndex < customers.Count - 1);
        LegacyMatchingConfirmedVehiclesItemsControl.ItemsSource = confirmedVehicles;
        LegacyMatchingConfirmedDocumentsItemsControl.ItemsSource = confirmedDocuments;
        LegacyMatchingConfirmedEmptyText.Visibility = confirmedVehicles.Length == 0 && confirmedDocuments.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var selectedCandidateId = legacyGraphSelectedRecommendation?.CandidateId;
        LegacyMatchingRecommendationsList.ItemsSource = recommendationItems;
        var selectedRecommendation = recommendationItems.FirstOrDefault(item =>
            string.Equals(item.Candidate.CandidateId, selectedCandidateId, StringComparison.OrdinalIgnoreCase)) ??
            recommendationItems.FirstOrDefault();
        LegacyMatchingRecommendationsList.SelectedItem = selectedRecommendation;
        legacyGraphSelectedRecommendation = selectedRecommendation?.Candidate;
        LegacyMatchingRecommendationStatusText.Text = recommendationItems.Length == 0
            ? "この顧客に表示できるおすすめ候補はありません。"
            : "候補を選択し、一致項目・差異・競合を確認してください。";
        legacyGraphSelectedItem = displayCustomer;
        RenderLegacyGraphCustomer(displayCustomer);
        UpdateLegacyGraphInspector(displayCustomer);
        RefreshLegacyMatchingSidePanel(sourceCustomer, allRecommendations);
        RefreshLegacyGraphCustomerApproval(sourceCustomer);
        RefreshLegacyMatchingRecommendationPreview(legacyGraphSelectedRecommendation);
        RefreshLegacyMatchingChanges();
        RefreshLegacyMatchingRecommendationActions();
        RefreshLegacyMatchingCustomerQueue();
    }

    private void RefreshLegacyMatchingChanges()
    {
        if (LegacyMatchingChangesList is null)
        {
            return;
        }

        var visible = string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase);
        LegacyMatchingChangesPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var currentCustomerId = GetLegacyMatchingChangeCustomerId();
        var visibleChanges = legacyGraphMatchingChanges
            .Where(change => change.CustomerId is null ||
                             currentCustomerId is null ||
                             string.Equals(change.CustomerId, currentCustomerId, StringComparison.Ordinal))
            .ToArray();
        LegacyMatchingChangesList.ItemsSource = visibleChanges;
        LegacyMatchingChangesSummaryText.Text = visibleChanges.Length == 0
            ? "変更はありません"
            : $"{visibleChanges.Length:N0}件 / 元に戻せます";
        ScheduleLegacyGraphWorkspaceHeightUpdate();
    }

    private string? GetLegacyMatchingChangeCustomerId()
    {
        if (!string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            return legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphCustomer selectedCustomer
                ? GetLegacyGraphSourceCustomer(selectedCustomer).CustomerId
                : null;
        }

        var customer = GetLegacyGraphMatchingCustomer();
        return customer is null
            ? legacyGraphMatchingCustomerId
            : customer.CustomerId;
    }

    private bool TryGetLegacyGraphMatchingSelectedMergeMember(
        out AbacusLegacyExportCandidateGraphCustomer member,
        out string groupKey)
    {
        member = null!;
        groupKey = "";
        var focusCustomer = GetLegacyGraphMatchingCustomer();
        var selectedMemberId = legacyGraphMatchingSelectedMergeMemberCustomerId;
        if (focusCustomer is null || string.IsNullOrWhiteSpace(selectedMemberId) ||
            FindLegacyGraphCustomerById(selectedMemberId) is not { } selectedMember)
        {
            return false;
        }

        var focusSource = GetLegacyGraphSourceCustomer(focusCustomer);
        groupKey = GetLegacyCustomerMergeKey(focusSource);
        if (!TryGetLegacyGraphMergeGroup(groupKey, out var group) ||
            group.CustomerIds.Count < 2 ||
            !group.CustomerIds.Contains(focusSource.CustomerId, StringComparer.Ordinal) ||
            !group.CustomerIds.Contains(selectedMember.CustomerId, StringComparer.Ordinal))
        {
            return false;
        }

        member = selectedMember;
        return true;
    }

    private void RefreshLegacyMatchingMergeRemovalButton()
    {
        if (LegacyMatchingRemoveCustomerFromMergeButton is null)
        {
            return;
        }

        var isMatching = string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase);
        if (isMatching)
        {
            var focusCustomer = GetLegacyGraphMatchingCustomer();
            var focusSource = focusCustomer is null
                ? null
                : GetLegacyGraphSourceCustomer(focusCustomer);
            var matchingGroupKey = focusSource is null
                ? null
                : GetLegacyCustomerMergeKey(focusSource);
            LegacyGraphCustomerMergeGroup? matchingGroup = null;
            var hasMatchingGroup = false;
            if (matchingGroupKey is not null &&
                TryGetLegacyGraphMergeGroup(matchingGroupKey, out var resolvedMatchingGroup) &&
                resolvedMatchingGroup.CustomerIds.Count >= 2 &&
                focusSource is not null &&
                resolvedMatchingGroup.CustomerIds.Contains(focusSource.CustomerId, StringComparer.Ordinal))
            {
                matchingGroup = resolvedMatchingGroup;
                hasMatchingGroup = true;
            }
            LegacyMatchingRemoveCustomerFromMergeButton.Visibility = hasMatchingGroup
                ? Visibility.Visible
                : Visibility.Collapsed;
            LegacyMatchingRemoveCustomerFromMergeButton.IsEnabled = hasMatchingGroup &&
                TryGetLegacyGraphMatchingSelectedMergeMember(out var selectedMember, out _) &&
                matchingGroup is not null &&
                LegacyGraphMatchingSelectionState.CanRemoveMergeMember(
                    selectedMember.CustomerId,
                    focusSource!.CustomerId,
                    matchingGroup.CustomerIds,
                    CanMutateLegacyGraph);
            return;
        }

        var selectedCustomer = isMatching
            ? null
            : LegacyGraphCustomersList.SelectedItem is LegacyGraphCustomerListEntry { IsGroupHeader: false } entry
                ? entry.Customer
                : null;
        var sourceCustomer = selectedCustomer is null ? null : GetLegacyGraphSourceCustomer(selectedCustomer);
        var groupKey = sourceCustomer is null ? null : GetLegacyCustomerMergeKey(sourceCustomer);
        var isAcceptedLogicalGroup = groupKey is not null &&
                                     (IsLegacyGraphLogicalCustomerGroup(groupKey) ||
                                      legacyGraphAppliedCustomerMergeKeys.Contains(groupKey));
        var isVisible =
            groupKey is not null &&
            isAcceptedLogicalGroup &&
            TryGetLegacyGraphMergeGroup(groupKey, out var group) && group.CustomerIds.Count >= 2
                ? Visibility.Visible
                : Visibility.Collapsed;
        LegacyMatchingRemoveCustomerFromMergeButton.Visibility = isVisible;
        LegacyMatchingRemoveCustomerFromMergeButton.IsEnabled = isVisible == Visibility.Visible &&
                                                                  CanMutateLegacyGraph;
    }

    private void LegacyMatchingChangeUndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        if ((sender as Button)?.Tag is not LegacyMatchingChangeItem change)
        {
            return;
        }

        if (change.Kind == "recommendation")
        {
            if (!string.IsNullOrWhiteSpace(change.WorkTargetKey) &&
                !string.IsNullOrWhiteSpace(change.ExternalCustomerId))
            {
                var scope = new LegacyCustomerRecommendationScope(
                    change.WorkTargetKey,
                    change.ExternalCustomerId);
                if (ResetLegacyGraphRecommendationScope(scope))
                {
                    InvalidateLegacyGraphImportConfirmation();
                    RefreshLegacyMatchingView();
                    ScheduleLegacyGraphCheckpointSave();
                }

                return;
            }

            var candidate = legacyGraphRecommendationCandidates.FirstOrDefault(item =>
                string.Equals(item.CandidateId, change.SubjectId, StringComparison.OrdinalIgnoreCase));
            if (candidate is not null && SetLegacyGraphRecommendationDecision(
                    candidate,
                    AbacusRecommendationDecisionValues.Pending))
            {
                RefreshLegacyMatchingView();
                ScheduleLegacyGraphCheckpointSave();
            }
        }
        else if (change.Kind == "vehicle" && FindLegacyGraphVehicleById(change.SubjectId) is { } vehicle)
        {
            RestoreLegacyMatchingVehicleFromTray(vehicle, change.UndoState);
        }
        else if (change.Kind == "document" &&
                 legacyExportCandidateGraphResult?.AllDocuments.FirstOrDefault(document =>
                     string.Equals(GetLegacyDocumentKey(document), change.SubjectId, StringComparison.OrdinalIgnoreCase)) is { } document)
        {
            RestoreLegacyMatchingDocumentFromTray(document, change.UndoState);
        }
    }

    private LegacyGraphDetachedDocumentState CaptureLegacyGraphDetachedDocumentState(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        var key = GetLegacyDocumentKey(document);
        return new LegacyGraphDetachedDocumentState(
            key,
            legacyGraphManualDocumentLinks.TryGetValue(key, out var vehicleId) ? vehicleId : null,
            legacyGraphManualDocumentCustomerLinks.TryGetValue(key, out var groupKey) ? groupKey : null,
            legacyGraphDocumentLinkMethods.TryGetValue(key, out var method) ? method : null,
            legacyGraphDocumentLinkReasons.TryGetValue(key, out var reason) ? reason : null,
            legacyGraphUnconnectedDocumentKeys.Contains(key),
            legacyGraphTrayDocumentKeys.Contains(key),
            legacyGraphExcludedDocumentKeys.Contains(key));
    }

    private void RestoreLegacyGraphDetachedDocumentState(
        LegacyGraphDetachedDocumentState state)
    {
        legacyGraphManualDocumentLinks.Remove(state.DocumentKey);
        legacyGraphManualDocumentCustomerLinks.Remove(state.DocumentKey);
        legacyGraphDocumentLinkMethods.Remove(state.DocumentKey);
        legacyGraphDocumentLinkReasons.Remove(state.DocumentKey);
        legacyGraphUnconnectedDocumentKeys.Remove(state.DocumentKey);
        legacyGraphTrayDocumentKeys.Remove(state.DocumentKey);
        legacyGraphExcludedDocumentKeys.Remove(state.DocumentKey);

        if (state.ManualVehicleId is not null)
        {
            legacyGraphManualDocumentLinks[state.DocumentKey] = state.ManualVehicleId;
        }

        if (state.ManualCustomerGroupKey is not null)
        {
            legacyGraphManualDocumentCustomerLinks[state.DocumentKey] = state.ManualCustomerGroupKey;
        }

        if (state.LinkMethod is not null)
        {
            legacyGraphDocumentLinkMethods[state.DocumentKey] = state.LinkMethod;
        }

        if (state.LinkReason is not null)
        {
            legacyGraphDocumentLinkReasons[state.DocumentKey] = state.LinkReason;
        }

        if (state.IsUnconnected)
        {
            legacyGraphUnconnectedDocumentKeys.Add(state.DocumentKey);
        }

        if (state.IsTray)
        {
            legacyGraphTrayDocumentKeys.Add(state.DocumentKey);
        }

        if (state.IsExcluded)
        {
            legacyGraphExcludedDocumentKeys.Add(state.DocumentKey);
        }
    }

    private void RestoreLegacyMatchingVehicleFromTray(
        AbacusLegacyExportCandidateGraphVehicle vehicle,
        LegacyGraphDetachedUndoState? undoState = null)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        if (undoState?.Kind == "vehicle" &&
            string.Equals(undoState.SubjectId, vehicle.VehicleId, StringComparison.Ordinal))
        {
            InvalidateLegacyGraphApprovalForVehicle(vehicle);
            var manualCustomerId = LegacyGraphVehicleUndoState.ResolveManualCustomerId(
                undoState.ManualVehicleCustomerId,
                vehicle.HasCustomer,
                vehicle.CustomerId);
            if (manualCustomerId is null)
            {
                legacyGraphManualVehicleCustomerLinks.Remove(vehicle.VehicleId);
            }
            else
            {
                legacyGraphManualVehicleCustomerLinks[vehicle.VehicleId] = manualCustomerId;
            }

            if (undoState.IsTray)
            {
                legacyGraphTrayVehicleIds.Add(vehicle.VehicleId);
            }
            else
            {
                legacyGraphTrayVehicleIds.Remove(vehicle.VehicleId);
            }

            foreach (var documentState in undoState.Documents)
            {
                RestoreLegacyGraphDetachedDocumentState(documentState);
                if (legacyExportCandidateGraphResult?.AllDocuments.FirstOrDefault(document =>
                        string.Equals(GetLegacyDocumentKey(document), documentState.DocumentKey, StringComparison.OrdinalIgnoreCase)) is { } document)
                {
                    InvalidateLegacyGraphApprovalForDocument(document);
                }
            }

            InvalidateLegacyGraphImportConfirmation();
            RebuildLegacyGraphRecommendationCandidates();
            legacyGraphMatchingChanges.RemoveAll(change =>
                change.Kind == "vehicle" &&
                string.Equals(change.SubjectId, vehicle.VehicleId, StringComparison.Ordinal));
            RefreshLegacyGraphUnresolvedVehicleList();
            RefreshLegacyGraphUnresolvedDocumentLists();
            RefreshLegacyMatchingView();
            ScheduleLegacyGraphCheckpointSave();
            return;
        }

        if (!legacyGraphTrayVehicleIds.Remove(vehicle.VehicleId))
        {
            return;
        }

        foreach (var document in GetLegacyGraphDocumentsAffectedByVehicle(vehicle))
        {
            var key = GetLegacyDocumentKey(document);
            legacyGraphTrayDocumentKeys.Remove(key);
            legacyGraphUnconnectedDocumentKeys.Remove(key);
            legacyGraphExcludedDocumentKeys.Remove(key);
            InvalidateLegacyGraphApprovalForDocument(document);
        }

        if (FindCurrentCustomerForVehicle(vehicle) is { } vehicleCustomer)
        {
            InvalidateLegacyGraphCustomerApproval(vehicleCustomer);
        }

        InvalidateLegacyGraphImportConfirmation();
        legacyGraphMatchingChanges.RemoveAll(change =>
            change.Kind == "vehicle" && string.Equals(change.SubjectId, vehicle.VehicleId, StringComparison.Ordinal));
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphUnresolvedDocumentLists();
        RefreshLegacyMatchingView();
        ScheduleLegacyGraphCheckpointSave();
    }

    private void RestoreLegacyMatchingDocumentFromTray(
        AbacusLegacyExportCandidateGraphDocument document,
        LegacyGraphDetachedUndoState? undoState = null)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var key = GetLegacyDocumentKey(document);
        if (undoState?.Kind == "document" &&
            string.Equals(undoState.SubjectId, key, StringComparison.OrdinalIgnoreCase))
        {
            InvalidateLegacyGraphApprovalForDocument(document);
            var state = undoState.Documents.FirstOrDefault(item =>
                string.Equals(item.DocumentKey, key, StringComparison.OrdinalIgnoreCase));
            if (state is not null)
            {
                RestoreLegacyGraphDetachedDocumentState(state);
            }

            InvalidateLegacyGraphApprovalForDocument(document);
            InvalidateLegacyGraphImportConfirmation();
            RebuildLegacyGraphRecommendationCandidates();
            legacyGraphMatchingChanges.RemoveAll(change =>
                change.Kind == "document" &&
                string.Equals(change.SubjectId, key, StringComparison.OrdinalIgnoreCase));
            RefreshLegacyGraphUnresolvedDocumentLists();
            RefreshLegacyMatchingView();
            ScheduleLegacyGraphCheckpointSave();
            return;
        }

        if (!legacyGraphTrayDocumentKeys.Remove(key))
        {
            return;
        }

        legacyGraphUnconnectedDocumentKeys.Remove(key);
        legacyGraphExcludedDocumentKeys.Remove(key);
        if (!string.IsNullOrWhiteSpace(document.LinkedVehicleId) &&
            FindLegacyGraphVehicleById(document.LinkedVehicleId) is { } vehicle &&
            !legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId))
        {
            legacyGraphManualDocumentLinks.Remove(key);
        }
        else if (FindCurrentCustomerForDocument(document) is { } customer)
        {
            ApplyLegacyGraphManualCustomerLink(document, customer);
        }

        InvalidateLegacyGraphImportConfirmation();
        legacyGraphMatchingChanges.RemoveAll(change =>
            change.Kind == "document" && string.Equals(change.SubjectId, key, StringComparison.OrdinalIgnoreCase));
        RefreshLegacyGraphUnresolvedDocumentLists();
        RefreshLegacyMatchingView();
        ScheduleLegacyGraphCheckpointSave();
    }

    private void RefreshLegacyMatchingSidePanel(
        AbacusLegacyExportCandidateGraphCustomer sourceCustomer,
        IReadOnlyList<AbacusRecommendationCandidate> allRecommendations)
    {
        var actionableRecommendations = GetLegacyMatchingActionableRecommendations(allRecommendations);
        var summaries = LegacyMatchingWorkflow.BuildCategorySummaries(allRecommendations, legacyGraphRecommendationDecisions);
        var categorySummary = summaries.FirstOrDefault(summary => summary.Kind == legacyGraphMatchingCategory)
            ?? summaries.FirstOrDefault(summary => summary.Total > 0)
            ?? summaries[0];
        if (categorySummary.Kind != legacyGraphMatchingCategory)
        {
            legacyGraphMatchingCategory = categorySummary.Kind;
            legacyGraphMatchingRecommendationIndex = -1;
        }

        var categoryCandidates = LegacyMatchingWorkflow.OrderCandidates(
            actionableRecommendations,
            legacyGraphMatchingCategory,
            legacyGraphRecommendationDecisions,
            includeCompleted: true);
        if (categoryCandidates.Count == 0 && actionableRecommendations.Count > 0)
        {
            var fallbackCategory = actionableRecommendations
                .Select(LegacyMatchingCategoryKinds.GetKind)
                .Distinct(StringComparer.Ordinal)
                .FirstOrDefault()
                ?? LegacyMatchingCategoryKinds.Customer;
            legacyGraphMatchingCategory = fallbackCategory;
            categoryCandidates = LegacyMatchingWorkflow.OrderCandidates(
                actionableRecommendations,
                fallbackCategory,
                legacyGraphRecommendationDecisions,
                includeCompleted: true);
        }
        categorySummary = summaries.First(summary => summary.Kind == legacyGraphMatchingCategory);

        legacyGraphMatchingRecommendationIndex = categoryCandidates.Count == 0
            ? -1
            : Math.Clamp(legacyGraphMatchingRecommendationIndex, 0, categoryCandidates.Count - 1);
        var currentCandidate = legacyGraphMatchingRecommendationIndex >= 0
            ? categoryCandidates[legacyGraphMatchingRecommendationIndex]
            : null;
        legacyGraphSelectedRecommendation = currentCandidate;
        RefreshLegacyMatchingCustomerMergePreviewAction(sourceCustomer);

        LegacyMatchingSideCustomerText.Text =
            $"基準顧客: {GetLegacyGraphMatchingDisplayName(sourceCustomer)} / 候補の接続先と承認後の変更を確認";
        LegacyMatchingSideProgressText.Text =
            $"未処理 {categorySummary.Pending:N0} / 保留 {categorySummary.Held:N0}";
        LegacyMatchingCustomerCategoryButton.Content = BuildLegacyMatchingCategoryButtonText(
            summaries, LegacyMatchingCategoryKinds.Customer);
        LegacyMatchingVehicleCategoryButton.Content = BuildLegacyMatchingCategoryButtonText(
            summaries, LegacyMatchingCategoryKinds.Vehicle);
        LegacyMatchingDocumentCategoryButton.Content = BuildLegacyMatchingCategoryButtonText(
            summaries, LegacyMatchingCategoryKinds.Document);
        ApplyLegacyMatchingCategoryButtonStyle(LegacyMatchingCustomerCategoryButton,
            legacyGraphMatchingCategory == LegacyMatchingCategoryKinds.Customer);
        ApplyLegacyMatchingCategoryButtonStyle(LegacyMatchingVehicleCategoryButton,
            legacyGraphMatchingCategory == LegacyMatchingCategoryKinds.Vehicle);
        ApplyLegacyMatchingCategoryButtonStyle(LegacyMatchingDocumentCategoryButton,
            legacyGraphMatchingCategory == LegacyMatchingCategoryKinds.Document);
        var activeRecommendationCount = summaries.Sum(summary => summary.Total);
        LegacyMatchingRecommendationsTabButton.Content = $"おすすめ ({activeRecommendationCount:N0})";
        LegacyMatchingCurrentCategoryText.Text = LegacyMatchingCategoryKinds.GetLabel(legacyGraphMatchingCategory);
        LegacyMatchingSideApproveButton.Content = legacyGraphMatchingCategory == LegacyMatchingCategoryKinds.Customer
            ? "統合する"
            : "承認";
        LegacyMatchingSideRejectButton.Content = legacyGraphMatchingCategory == LegacyMatchingCategoryKinds.Customer
            ? "別人"
            : "却下";
        var queueLabel = summaries.Sum(summary => summary.Pending) > 0
            ? "未処理"
            : "保留再確認";
        LegacyMatchingCurrentProgressText.Text = categoryCandidates.Count == 0
            ? $"{queueLabel} 0 / 0"
            : $"{queueLabel} {legacyGraphMatchingRecommendationIndex + 1:N0} / {categoryCandidates.Count:N0}";

        if (currentCandidate is null)
        {
            LegacyMatchingCurrentTargetHeadingText.Text = "紐付け先";
            LegacyMatchingCurrentTargetText.Text = "未確定タブで検索するか、別のカテゴリを選択してください。";
            LegacyMatchingCurrentSubjectHeadingText.Text = "候補";
            LegacyMatchingCurrentSubjectText.Text = $"このカテゴリに{queueLabel}のおすすめはありません";
            LegacyMatchingCurrentRelationArrowText.Text = "←";
            LegacyMatchingCurrentMatchedText.Text = "一致: なし";
            LegacyMatchingCurrentDifferenceText.Text = "差異: なし";
            LegacyMatchingCurrentMissingText.Text = "不足情報: なし";
            LegacyMatchingCurrentConflictText.Text = "問題: なし";
            LegacyMatchingCurrentReasonText.Text = "";
            LegacyMatchingCurrentDecisionText.Text = "";
            LegacyMatchingCurrentAutoDecisionReasonText.Text = "";
            LegacyMatchingDetailsSubjectSummaryText.Text = "";
            LegacyMatchingDetailsSourceHeadingText.Text = "";
            LegacyMatchingDetailsCandidateHeadingText.Text = "";
            LegacyMatchingDetailsDecisionText.Text = "";
            LegacyMatchingDetailsReasonText.Text = "";
            LegacyMatchingDetailsAutoDecisionText.Text = "";
            LegacyMatchingDetailsSourceItemsControl.ItemsSource = null;
            LegacyMatchingDetailsCandidateItemsControl.ItemsSource = null;
            LegacyMatchingDetailsComparisonItemsControl.ItemsSource = null;
            LegacyMatchingDetailsEvidenceItemsControl.ItemsSource = null;
            LegacyMatchingDetailsDifferenceItemsControl.ItemsSource = null;
            LegacyMatchingDetailsMissingItemsControl.ItemsSource = null;
            LegacyMatchingDetailsConflictItemsControl.ItemsSource = null;
            LegacyMatchingDetailsAlternativesItemsControl.ItemsSource = null;
            LegacyMatchingDetailsInternalItemsControl.ItemsSource = null;
            LegacyMatchingDetailsAlternativesHeadingText.Text = "";
            LegacyMatchingDetailsAlternativesPlaceholderText.Text = "";
            LegacyMatchingDetailsMergePreviewText.Text = "";
            LegacyMatchingDetailsMergePreviewSection.Visibility = Visibility.Collapsed;
            LegacyMatchingCurrentMatchedItemsControl.ItemsSource = null;
            LegacyMatchingCurrentDifferenceItemsControl.ItemsSource = null;
            LegacyMatchingCurrentMissingItemsControl.ItemsSource = null;
            LegacyMatchingCurrentConflictItemsControl.ItemsSource = null;
            LegacyMatchingCurrentDifferenceBorder.Visibility = Visibility.Collapsed;
            LegacyMatchingCurrentMissingBorder.Visibility = Visibility.Collapsed;
            LegacyMatchingCurrentConflictBorder.Visibility = Visibility.Collapsed;
            LegacyMatchingCurrentRecommendationScrollViewer.Visibility = Visibility.Collapsed;
            LegacyMatchingNoRecommendationText.Text = $"このカテゴリに{queueLabel}のおすすめはありません。別のカテゴリまたは未確定タブを確認してください。";
            LegacyMatchingNoRecommendationText.Visibility = Visibility.Visible;
            LegacyMatchingDetailsButton.IsEnabled = false;
            ApplyLegacyMatchingDecisionBadgeStyle(null);
            SetLegacyMatchingDetailsVisible(false);
            LegacyMatchingSideStatusText.Text = "保留中の候補は通常の巡回後にもう一度確認できます。";
            LegacyMatchingSideApproveButton.Content = legacyGraphMatchingCategory == LegacyMatchingCategoryKinds.Customer
                ? "統合する"
                : "承認";
            LegacyMatchingSideApproveButton.IsEnabled = false;
            LegacyMatchingSideRejectButton.IsEnabled = false;
            LegacyMatchingSideHoldButton.IsEnabled = false;
            LegacyMatchingSidePreviousButton.IsEnabled = false;
            LegacyMatchingSideNextButton.IsEnabled = false;
        RefreshLegacyGraphCustomerApproval(null);
        RefreshLegacyMatchingCustomerMergePreviewAction(null);
        return;
    }

        var item = CreateLegacyMatchingRecommendationItem(currentCandidate, sourceCustomer);
        var decision = GetLegacyGraphRecommendationDecision(currentCandidate);
        var reverseComparisonDisplay = !IsLegacyMatchingCustomerMerge(currentCandidate) ||
            ShouldReverseLegacyMatchingCustomerMerge(currentCandidate, sourceCustomer);
        var matchedRows = currentCandidate.MatchedFields
            .Select(field =>
            {
                var values = GetLegacyMatchingDisplayedValues(
                    field.SourceValue,
                    field.CandidateValue,
                    reverse: reverseComparisonDisplay);
                return new LegacyMatchingDetailRow(
                    field.Label,
                    DisplayLegacyMatchingValue(values.SourceValue),
                    DisplayLegacyMatchingValue(values.CandidateValue),
                    GetLegacyMatchingMatchKindText(field.MatchKind),
                    ToBrush("#166534"),
                    field.MatchKind,
                    "");
            })
            .ToArray();
        var differenceRows = currentCandidate.Differences
            .Select(field =>
            {
                var values = GetLegacyMatchingDisplayedValues(
                    field.SourceValue,
                    field.CandidateValue,
                    reverse: reverseComparisonDisplay);
                return new LegacyMatchingDetailRow(
                    field.Label,
                    DisplayLegacyMatchingValue(values.SourceValue),
                    DisplayLegacyMatchingValue(values.CandidateValue),
                    "値が異なります",
                    ToBrush("#805B10"),
                    "difference",
                    "");
            })
            .ToArray();
        var missingRows = currentCandidate.MissingFields
            .Select(field =>
            {
                var missingSide = GetLegacyMatchingDisplayedMissingSide(
                    field.MissingSide,
                    reverse: reverseComparisonDisplay);
                var values = GetLegacyMatchingDisplayedValues(
                    field.SourceValue,
                    field.CandidateValue,
                    reverse: reverseComparisonDisplay);
                var sourceValue = missingSide == "source"
                    ? "情報なし"
                    : DisplayLegacyMatchingValue(values.SourceValue);
                var candidateValue = missingSide == "candidate"
                    ? "情報なし"
                    : DisplayLegacyMatchingValue(values.CandidateValue);
                return new LegacyMatchingDetailRow(
                    field.Label,
                    sourceValue,
                    candidateValue,
                    missingSide == "source" ? "紐付け先側：情報なし" : "候補側：情報なし",
                    ToBrush("#6B7280"),
                    $"missing:{missingSide}",
                    "");
            })
            .ToArray();
        var conflictRows = currentCandidate.Conflicts
            .Select(conflict => new LegacyMatchingDetailRow(
                "確認内容",
                "",
                "",
                "要確認",
                ToBrush("#B91C1C"),
                "conflict",
                conflict))
            .ToArray();
        var comparisonRows = matchedRows
            .Concat(differenceRows)
            .Concat(missingRows)
            .ToArray();
        var displayEndpoints = GetLegacyMatchingDisplayEndpoints(currentCandidate, sourceCustomer);
        var sourceEntitySections = BuildLegacyMatchingEntitySections(
            displayEndpoints.ParentKind,
            displayEndpoints.ParentId);
        var candidateEntitySections = BuildLegacyMatchingEntitySections(
            displayEndpoints.CandidateKind,
            displayEndpoints.CandidateId);
        var evidenceItems = currentCandidate.MatchedFields
            .Select(field =>
            {
                var values = GetLegacyMatchingDisplayedValues(
                    field.SourceValue,
                    field.CandidateValue,
                    reverse: reverseComparisonDisplay);
                return $"{field.Label}: {DisplayLegacyMatchingValue(values.SourceValue)} ← " +
                    $"{DisplayLegacyMatchingValue(values.CandidateValue)}（{GetLegacyMatchingMatchKindText(field.MatchKind)}）";
            })
            .ToArray();
        var differenceItems = currentCandidate.Differences
            .Select(field =>
            {
                var values = GetLegacyMatchingDisplayedValues(
                    field.SourceValue,
                    field.CandidateValue,
                    reverse: reverseComparisonDisplay);
                return $"{field.Label}: {DisplayLegacyMatchingValue(values.SourceValue)} ← " +
                    $"{DisplayLegacyMatchingValue(values.CandidateValue)}（値が異なります）";
            })
            .ToArray();
        var missingItems = currentCandidate.MissingFields
            .Select(field =>
            {
                var missingSide = GetLegacyMatchingDisplayedMissingSide(
                    field.MissingSide,
                    reverse: reverseComparisonDisplay);
                return $"{field.Label}: {(missingSide == "source" ? "紐付け先側" : "候補側")}に情報がありません";
            })
            .ToArray();
        var alternativeItems = BuildLegacyMatchingAlternativeRows(currentCandidate, sourceCustomer, allRecommendations);
        var internalRows = new[]
        {
            new LegacyMatchingInternalInfoRow("判定エンジン", "Gate28", "Gate28"),
            new LegacyMatchingInternalInfoRow(
                "候補ID",
                ShortenLegacyMatchingInternalValue(currentCandidate.CandidateId),
                currentCandidate.CandidateId),
            new LegacyMatchingInternalInfoRow(
                "対象ID",
                ShortenLegacyMatchingInternalValue($"{currentCandidate.SubjectKind}: {currentCandidate.SubjectId}"),
                $"{currentCandidate.SubjectKind}: {currentCandidate.SubjectId}"),
            new LegacyMatchingInternalInfoRow(
                "紐付け先ID",
                ShortenLegacyMatchingInternalValue($"{currentCandidate.TargetKind}: {currentCandidate.TargetId}"),
                $"{currentCandidate.TargetKind}: {currentCandidate.TargetId}"),
            new LegacyMatchingInternalInfoRow(
                "内部判定",
                currentCandidate.MatchedFields.Count == 0
                    ? "なし"
                    : string.Join("、", currentCandidate.MatchedFields.Select(field =>
                        $"{field.Field}:{field.MatchKind}")),
                string.Join("、", currentCandidate.MatchedFields.Select(field =>
                    $"{field.Field}:{field.MatchKind}"))),
        };
        LegacyMatchingNoRecommendationText.Visibility = Visibility.Collapsed;
        LegacyMatchingCurrentRecommendationScrollViewer.Visibility = Visibility.Visible;
        SetLegacyMatchingDetailsVisible(false);
        LegacyMatchingCurrentTargetHeadingText.Text = GetLegacyMatchingTargetHeading(currentCandidate, sourceCustomer);
        LegacyMatchingCurrentTargetText.Text = item.TargetText;
        LegacyMatchingCurrentSubjectHeadingText.Text = GetLegacyMatchingSubjectHeading(currentCandidate);
        LegacyMatchingCurrentSubjectText.Text = item.SubjectText;
        LegacyMatchingCurrentRelationArrowText.Text = "←";
        LegacyMatchingCurrentMatchedText.Text = $"一致 {matchedRows.Length:N0}件";
        LegacyMatchingCurrentDifferenceText.Text = $"差異 {differenceRows.Length:N0}件";
        LegacyMatchingCurrentMissingText.Text = $"情報なし {missingRows.Length:N0}件";
        LegacyMatchingCurrentConflictText.Text = $"要確認 {conflictRows.Length:N0}件";
        LegacyMatchingCurrentMatchedItemsControl.ItemsSource = matchedRows;
        LegacyMatchingCurrentDifferenceItemsControl.ItemsSource = differenceRows;
        LegacyMatchingCurrentMissingItemsControl.ItemsSource = missingRows;
        LegacyMatchingCurrentConflictItemsControl.ItemsSource = conflictRows;
        LegacyMatchingDetailsSubjectSummaryText.Text = $"{item.TargetText} ← {item.SubjectText}";
        LegacyMatchingDetailsSourceHeadingText.Text = GetLegacyMatchingTargetHeading(currentCandidate, sourceCustomer);
        LegacyMatchingDetailsCandidateHeadingText.Text = GetLegacyMatchingSubjectHeading(currentCandidate);
        LegacyMatchingDetailsSourceItemsControl.ItemsSource = sourceEntitySections;
        LegacyMatchingDetailsCandidateItemsControl.ItemsSource = candidateEntitySections;
        LegacyMatchingDetailsComparisonItemsControl.ItemsSource = comparisonRows;
        LegacyMatchingDetailsEvidenceItemsControl.ItemsSource = evidenceItems;
        LegacyMatchingDetailsDifferenceItemsControl.ItemsSource = differenceItems;
        LegacyMatchingDetailsMissingItemsControl.ItemsSource = missingItems;
        LegacyMatchingDetailsConflictItemsControl.ItemsSource = currentCandidate.Conflicts;
        LegacyMatchingDetailsAlternativesItemsControl.ItemsSource = alternativeItems;
        LegacyMatchingDetailsAlternativesHeadingText.Text = $"他の候補 {alternativeItems.Count:N0}件";
        LegacyMatchingDetailsAlternativesPlaceholderText.Text = alternativeItems.Count == 0
            ? "他の候補はありません。"
            : "※ここでは比較のみ";
        LegacyMatchingDetailsMergePreviewText.Text = BuildLegacyMatchingMergePreviewText(currentCandidate);
        LegacyMatchingDetailsMergePreviewSection.Visibility = IsLegacyMatchingCustomerMerge(currentCandidate)
            ? Visibility.Visible
            : Visibility.Collapsed;
        LegacyMatchingDetailsInternalItemsControl.ItemsSource = internalRows;
        LegacyMatchingCurrentDifferenceBorder.Visibility = differenceRows.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        LegacyMatchingCurrentMissingBorder.Visibility = missingRows.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        LegacyMatchingCurrentConflictBorder.Visibility = conflictRows.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        LegacyMatchingCurrentDecisionText.Text = GetLegacyMatchingDecisionText(decision);
        ApplyLegacyMatchingDecisionBadgeStyle(decision);
        LegacyMatchingCurrentReasonText.Text = $"おすすめ理由: {BuildLegacyMatchingShortReason(currentCandidate)}";
        LegacyMatchingCurrentAutoDecisionReasonText.Text = BuildLegacyMatchingShortAutoDecisionReason(currentCandidate);
        LegacyMatchingDetailsDecisionText.Text = $"現在の判定: {GetLegacyMatchingDecisionText(decision)}";
        LegacyMatchingDetailsReasonText.Text = $"おすすめ理由: {BuildLegacyMatchingShortReason(currentCandidate)}";
        LegacyMatchingDetailsAutoDecisionText.Text = BuildLegacyMatchingShortAutoDecisionReason(currentCandidate);
        LegacyMatchingDetailsButton.IsEnabled = true;
        var canApproveCurrentCandidate = CanManuallyApproveLegacyMatchingCandidate(currentCandidate);
        var requiresManualReview = RequiresLegacyMatchingManualReview(currentCandidate);
        LegacyMatchingSideStatusText.Text = canApproveCurrentCandidate
            ? requiresManualReview
                ? "競合があるため自動承認はできません。内容を確認してから手動承認できます。"
                : currentCandidate.IsManual
                    ? "検索から追加した顧客候補です。"
                    : ""
            : "対象データまたは接続先を特定できないため、この候補は承認できません。";
        LegacyMatchingSideApproveButton.Content = legacyGraphMatchingCategory == LegacyMatchingCategoryKinds.Customer
            ? "統合する"
            : "承認";
        LegacyMatchingSideApproveButton.IsEnabled = canApproveCurrentCandidate &&
            decision != AbacusRecommendationDecisionValues.Approved;
        LegacyMatchingSideRejectButton.IsEnabled = decision != AbacusRecommendationDecisionValues.Approved;
        LegacyMatchingSideHoldButton.IsEnabled = decision != AbacusRecommendationDecisionValues.Approved;
        LegacyMatchingSidePreviousButton.IsEnabled = legacyGraphMatchingRecommendationIndex > 0;
        LegacyMatchingSideNextButton.IsEnabled = legacyGraphMatchingRecommendationIndex < categoryCandidates.Count - 1;
    }

    private void RefreshLegacyMatchingCustomerMergePreviewAction(
        AbacusLegacyExportCandidateGraphCustomer? customer)
    {
        if (LegacyMatchingOpenCustomerMergePreviewButton is null ||
            LegacyMatchingCustomerMergePreviewStatusText is null)
        {
            return;
        }

        LegacyMatchingOpenCustomerMergePreviewButton.Visibility = Visibility.Collapsed;
        LegacyMatchingOpenCustomerMergePreviewButton.IsEnabled = false;
        LegacyMatchingCustomerMergePreviewStatusText.Text = "";
        if (customer is null)
        {
            return;
        }

        var sourceCustomer = GetLegacyGraphSourceCustomer(customer);
        var mergeKey = GetLegacyCustomerMergeKey(sourceCustomer);
        var hasAcceptedMerge = TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup) &&
                               mergeGroup.CustomerIds.Count > 1 &&
                               (IsLegacyGraphLogicalCustomerGroup(mergeKey) ||
                                legacyGraphAppliedCustomerMergeKeys.Contains(mergeKey) ||
                                string.Equals(mergeGroup.Origin, "manual", StringComparison.Ordinal));
        if (!hasAcceptedMerge)
        {
            return;
        }

        var hasDraft = HasCompleteLegacyGraphCustomerMergeDraft(
            mergeKey,
            mergeGroup.CustomerIds);
        LegacyMatchingOpenCustomerMergePreviewButton.Visibility = Visibility.Visible;
        LegacyMatchingOpenCustomerMergePreviewButton.IsEnabled = true;
        LegacyMatchingOpenCustomerMergePreviewButton.Content = hasDraft
            ? "採用内容を再確認"
            : "顧客情報を確認・統一";
        LegacyMatchingCustomerMergePreviewStatusText.Text = hasDraft
            ? "顧客情報 ✓ 決定済み"
            : "顧客情報 [未決定]";
    }

    private void LegacyMatchingOpenCustomerMergePreviewButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var matchingCustomer = GetLegacyGraphMatchingCustomer();
        if (matchingCustomer is null)
        {
            return;
        }

        var sourceCustomer = GetLegacyGraphSourceCustomer(matchingCustomer);
        var mergeKey = GetLegacyCustomerMergeKey(sourceCustomer);
        var candidates = GetLegacyGraphCustomerMergeCandidates(sourceCustomer);

        if (candidates.Count < 2 ||
            !OpenLegacyGraphMergeDialog(sourceCustomer, mergeKey, candidates))
        {
            LegacyGraphStatusText.Text = "統合対象の顧客が2件以上ないため、顧客情報プレビューを開けません。";
            LegacyGraphStatusText.Foreground = ToBrush("#805B10");
        }
    }

    private IReadOnlyList<AbacusRecommendationCandidate> GetLegacyMatchingActionableRecommendations(
        IEnumerable<AbacusRecommendationCandidate> recommendations)
    {
        var pending = recommendations
            .Where(candidate => GetLegacyGraphRecommendationDecision(candidate) == AbacusRecommendationDecisionValues.Pending)
            .ToArray();
        if (pending.Length > 0)
        {
            return pending;
        }

        return recommendations
            .Where(candidate => GetLegacyGraphRecommendationDecision(candidate) == AbacusRecommendationDecisionValues.Hold)
            .ToArray();
    }

    private static string BuildLegacyMatchingCategoryButtonText(
        IReadOnlyList<LegacyMatchingCategorySummary> summaries,
        string kind)
    {
        var summary = summaries.FirstOrDefault(item => item.Kind == kind);
        return summary is null
            ? LegacyMatchingCategoryKinds.GetLabel(kind)
            : $"{summary.Label} {summary.Total:N0}";
    }

    private static void ApplyLegacyMatchingCategoryButtonStyle(Button button, bool isSelected)
    {
        button.Background = ToBrush(isSelected ? "#FFF7ED" : "#FFFFFF");
        button.BorderBrush = ToBrush(isSelected ? "#F59E0B" : "#CBD5E1");
        button.Foreground = ToBrush(isSelected ? "#92400E" : "#52647A");
        button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static string DisplayLegacyMatchingValue(string value) =>
        string.IsNullOrWhiteSpace(value) ? "情報なし" : value;

    private static string DisplayLegacyMatchingEntityValue(string value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string BuildLegacyMatchingShortReason(AbacusRecommendationCandidate candidate)
    {
        if (candidate.IsManual)
        {
            return "検索から手動追加した顧客候補です。";
        }

        var labels = candidate.MatchedFields
            .Select(field => field.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var matchedText = labels.Length == 0
            ? "一致する情報がある"
            : string.Join("・", labels);
        var hasFuzzyMatch = candidate.MatchedFields.Any(field => field.MatchKind == "fuzzy");
        var reason = hasFuzzyMatch
            ? $"{matchedText}が近いため"
            : $"{matchedText}が一致したため";
        if (candidate.Conflicts.Count > 0)
        {
            return $"{reason}。競合があるため確認が必要です。";
        }

        if (candidate.Differences.Count > 0 || candidate.MissingFields.Count > 0)
        {
            return $"{reason}。差異・不足情報を確認してください。";
        }

        return $"{reason}。";
    }

    private static string BuildLegacyMatchingShortAutoDecisionReason(AbacusRecommendationCandidate candidate)
    {
        if (candidate.IsManual)
        {
            return "一致判定は行わず、ユーザーが明示的に統合を判断します。";
        }

        if (candidate.Conflicts.Count > 0)
        {
            return "競合があるため自動確定しません。";
        }

        if (candidate.MatchedFields.Any(field => field.MatchKind == "fuzzy") ||
            candidate.Differences.Count > 0 ||
            candidate.MissingFields.Count > 0)
        {
            return "完全一致ではないため、自動確定しません。";
        }

        return "おすすめは自動確定せず、承認操作で反映します。";
    }

    private IReadOnlyList<LegacyMatchingEntitySection> BuildLegacyMatchingEntitySections(
        string kind,
        string id)
    {
        var sections = new List<LegacyMatchingEntitySection>();
        switch (kind)
        {
            case AbacusRecommendationEntityKinds.Customer:
                if (FindLegacyGraphCustomerById(id) is { } customer)
                {
                    sections.Add(new LegacyMatchingEntitySection(
                        "基本情報",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("顧客番号", customer.CustomerNumber),
                            CreateLegacyMatchingEntityRow("顧客名", GetLegacyGraphCustomerDisplayName(customer)),
                            CreateLegacyMatchingEntityRow("ふりがな", customer.NameKana),
                        }));
                    sections.Add(new LegacyMatchingEntitySection(
                        "連絡先",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("電話番号", customer.PhoneNumber),
                            CreateLegacyMatchingEntityRow("メール", customer.EmailAddress),
                            CreateLegacyMatchingEntityRow("郵便番号", customer.PostalCode),
                            CreateLegacyMatchingEntityRow("住所", customer.Address),
                        }));
                    sections.Add(new LegacyMatchingEntitySection(
                        "関連データ",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("車両数", $"{GetLegacyGraphVehiclesForDisplay(customer).Count:N0}台"),
                            CreateLegacyMatchingEntityRow("書類数", $"{GetLegacyGraphDisplayedDocumentCount(customer):N0}件"),
                        }));
                    sections.Add(new LegacyMatchingEntitySection(
                        "その他",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("メモ", customer.Memo),
                            CreateLegacyMatchingEntityRow("出典", "顧客候補パッケージ / customers.csv"),
                        }));
                }

                break;
            case AbacusRecommendationEntityKinds.Vehicle:
                if (FindLegacyGraphVehicleById(id) is { } vehicle)
                {
                    sections.Add(new LegacyMatchingEntitySection(
                        "車両情報",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("車名", vehicle.VehicleName),
                            CreateLegacyMatchingEntityRow("メーカー", vehicle.Maker),
                            CreateLegacyMatchingEntityRow("型式", vehicle.Model),
                            CreateLegacyMatchingEntityRow("所有者", GetLegacyGraphCurrentVehicleOwnerName(vehicle)),
                        }));
                    sections.Add(new LegacyMatchingEntitySection(
                        "識別情報",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("登録番号", vehicle.RegistrationNumber),
                            CreateLegacyMatchingEntityRow("車台番号", vehicle.ChassisNumber),
                        }));
                    sections.Add(new LegacyMatchingEntitySection(
                        "管理情報",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("年式", vehicle.ModelYear),
                            CreateLegacyMatchingEntityRow("車検満了日", vehicle.InspectionDate),
                            CreateLegacyMatchingEntityRow("走行距離", vehicle.Mileage),
                            CreateLegacyMatchingEntityRow("書類数", $"{GetDocumentsForVehicle(vehicle).Count:N0}件"),
                        }));
                    sections.Add(new LegacyMatchingEntitySection(
                        "出典",
                        new[] { CreateLegacyMatchingEntityRow("出典", vehicle.SourceLocation) }));
                }

                break;
            case AbacusRecommendationEntityKinds.Document:
                if (FindLegacyRecommendationDocumentById(id) is { } document)
                {
                    var profile = document.RecommendationProfile;
                    sections.Add(new LegacyMatchingEntitySection(
                        "書類情報",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("書類種別", document.Kind),
                            CreateLegacyMatchingEntityRow("書類番号", document.DocumentNumber),
                        }));
                    sections.Add(new LegacyMatchingEntitySection(
                        "顧客情報",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("顧客名", FirstNonEmpty(document.CustomerName, profile?.CustomerName ?? "")),
                            CreateLegacyMatchingEntityRow("ふりがな", profile?.NameKana ?? ""),
                            CreateLegacyMatchingEntityRow("電話番号", profile?.PhoneNumber ?? ""),
                            CreateLegacyMatchingEntityRow("郵便番号", profile?.PostalCode ?? ""),
                            CreateLegacyMatchingEntityRow("住所", profile?.Address ?? ""),
                        }));
                    sections.Add(new LegacyMatchingEntitySection(
                        "車両情報",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("車名", FirstNonEmpty(document.VehicleName, profile?.VehicleName ?? "")),
                            CreateLegacyMatchingEntityRow("メーカー", profile?.Maker ?? ""),
                            CreateLegacyMatchingEntityRow("型式", profile?.Model ?? ""),
                            CreateLegacyMatchingEntityRow("登録番号", FirstNonEmpty(document.RegistrationNumber, profile?.RegistrationNumber ?? "")),
                            CreateLegacyMatchingEntityRow("車台番号", profile?.ChassisNumber ?? ""),
                        }));
                    sections.Add(new LegacyMatchingEntitySection(
                        "金額・日付",
                        new[]
                        {
                            CreateLegacyMatchingEntityRow("日付", document.DocumentDate),
                            CreateLegacyMatchingEntityRow("合計", document.TotalAmount),
                        },
                        BuildLegacyMatchingDocumentDetail(document)));
                    sections.Add(new LegacyMatchingEntitySection(
                        "出典",
                        new[] { CreateLegacyMatchingEntityRow("出典", document.SourceLocation) }));
                }

                break;
        }

        return sections;
    }

    private static LegacyMatchingEntityRow CreateLegacyMatchingEntityRow(
        string label,
        string value) =>
        new(label, DisplayLegacyMatchingEntityValue(value));

    private static LegacyMatchingExpandableDetail BuildLegacyMatchingDocumentDetail(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        var fullText = BuildLegacyGraphDocumentDetailSummary(document).Trim();
        if (string.IsNullOrWhiteSpace(document.DetailsJson))
        {
            return new LegacyMatchingExpandableDetail(
                "明細",
                "明細情報を取得できません",
                fullText);
        }

        try
        {
            var detail = JsonSerializer.Deserialize<AbacusDetailJsonDocument>(
                document.DetailsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (detail is null)
            {
                return new LegacyMatchingExpandableDetail(
                    "明細",
                    "明細情報を解析できません",
                    fullText);
            }

            var summary = $"{detail.Lines.Count:N0}行 / 明細合計 {detail.DetailAmount:N0}";
            if (detail.AmountOnlyRowCount > 0)
            {
                summary += $" / 金額のみ {detail.AmountOnlyRowCount:N0}行";
            }

            if (detail.ExcludedDetailCount > 0)
            {
                summary += $" / 除外 {detail.ExcludedDetailCount:N0}行";
            }

            return new LegacyMatchingExpandableDetail("明細", summary, fullText);
        }
        catch (JsonException)
        {
            return new LegacyMatchingExpandableDetail(
                "明細",
                "明細情報を解析できません",
                fullText);
        }
    }

    private static string GetLegacyMatchingEntityHeading(
        string subjectKind,
        string targetKind,
        bool isSource) =>
        (subjectKind, targetKind, isSource) switch
        {
            (AbacusRecommendationEntityKinds.Customer, AbacusRecommendationEntityKinds.Customer, true) => "現在の顧客",
            (AbacusRecommendationEntityKinds.Customer, AbacusRecommendationEntityKinds.Customer, false) => "統合候補の顧客",
            (AbacusRecommendationEntityKinds.Document, AbacusRecommendationEntityKinds.Customer, true) => "対象書類",
            (AbacusRecommendationEntityKinds.Document, AbacusRecommendationEntityKinds.Customer, false) => "紐付け先候補の顧客",
            (AbacusRecommendationEntityKinds.Document, AbacusRecommendationEntityKinds.Vehicle, true) => "対象書類",
            (AbacusRecommendationEntityKinds.Document, AbacusRecommendationEntityKinds.Vehicle, false) => "紐付け先候補の車両",
            (AbacusRecommendationEntityKinds.Vehicle, AbacusRecommendationEntityKinds.Customer, true) => "対象車両",
            (AbacusRecommendationEntityKinds.Vehicle, AbacusRecommendationEntityKinds.Customer, false) => "紐付け先候補の顧客",
            _ => isSource ? "対象の情報" : "候補・紐付け先の情報",
        };

    private static string GetLegacyMatchingSubjectHeading(
        AbacusRecommendationCandidate candidate) =>
        (candidate.SubjectKind, candidate.TargetKind) switch
        {
            (AbacusRecommendationEntityKinds.Customer, AbacusRecommendationEntityKinds.Customer) => "統合候補",
            (AbacusRecommendationEntityKinds.Document, _) => "対象書類",
            (AbacusRecommendationEntityKinds.Vehicle, _) => "対象車両",
            _ => "候補",
        };

    private (string ParentKind, string ParentId, string CandidateKind, string CandidateId)
        GetLegacyMatchingDisplayEndpoints(
            AbacusRecommendationCandidate candidate,
            AbacusLegacyExportCandidateGraphCustomer focusCustomer)
    {
        if (IsLegacyMatchingCustomerMerge(candidate))
        {
            var focusSource = focusCustomer;
            var logicalCustomerIds = GetLegacyGraphLogicalCustomerMembers(focusSource)
                .Select(customer => customer.CustomerId)
                .ToHashSet(StringComparer.Ordinal);
            if (logicalCustomerIds.Contains(candidate.SubjectId) &&
                !logicalCustomerIds.Contains(candidate.TargetId))
            {
                return (
                    candidate.SubjectKind,
                    candidate.SubjectId,
                    candidate.TargetKind,
                    candidate.TargetId);
            }

            if (logicalCustomerIds.Contains(candidate.TargetId) &&
                !logicalCustomerIds.Contains(candidate.SubjectId))
            {
                return (
                    candidate.TargetKind,
                    candidate.TargetId,
                    candidate.SubjectKind,
                    candidate.SubjectId);
            }
        }

        return (
            candidate.TargetKind,
            candidate.TargetId,
            candidate.SubjectKind,
            candidate.SubjectId);
    }

    private AbacusLegacyExportCandidateGraphDocument? FindLegacyRecommendationDocumentById(string id) =>
        legacyExportCandidateGraphResult?.AllDocuments.FirstOrDefault(document =>
            string.Equals(GetLegacyDocumentKey(document), id, StringComparison.OrdinalIgnoreCase));

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private IReadOnlyList<LegacyMatchingAlternativeRow> BuildLegacyMatchingAlternativeRows(
        AbacusRecommendationCandidate currentCandidate,
        AbacusLegacyExportCandidateGraphCustomer sourceCustomer,
        IReadOnlyList<AbacusRecommendationCandidate> allRecommendations)
    {
        var sourceCustomerIds = GetLegacyGraphCustomerMergeCandidates(sourceCustomer)
            .Select(customer => customer.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        var alternatives = allRecommendations
            .Where(candidate => !string.Equals(candidate.CandidateId, currentCandidate.CandidateId, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => currentCandidate.SubjectKind == AbacusRecommendationEntityKinds.Customer
                ? candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
                  candidate.TargetKind == AbacusRecommendationEntityKinds.Customer &&
                  (sourceCustomerIds.Contains(candidate.SubjectId) || sourceCustomerIds.Contains(candidate.TargetId))
                : string.Equals(candidate.SubjectKind, currentCandidate.SubjectKind, StringComparison.Ordinal) &&
                  string.Equals(candidate.SubjectId, currentCandidate.SubjectId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.TargetKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetId, StringComparer.Ordinal)
            .Take(12)
            .Select(candidate =>
            {
                var targetText = GetLegacyMatchingTargetText(candidate, sourceCustomerIds);
                var comparisonLines = candidate.MatchedFields
                    .GroupBy(field => field.Label, StringComparer.Ordinal)
                    .Select(group => new LegacyMatchingAlternativeLine(
                        "✓",
                        $"{group.Key}：一致",
                        ToBrush("#166534")))
                    .Concat(candidate.Differences
                        .GroupBy(field => field.Label, StringComparer.Ordinal)
                        .Select(group => new LegacyMatchingAlternativeLine(
                            "▲",
                            $"{group.Key}：差異",
                            ToBrush("#805B10"))))
                    .ToArray();
                if (comparisonLines.Length == 0)
                {
                    comparisonLines =
                    [
                        new LegacyMatchingAlternativeLine(
                            "—",
                            "一致・差異なし",
                            ToBrush("#64748B")),
                    ];
                }

                var conflictText = candidate.Conflicts.Count == 0
                    ? "要確認なし"
                    : $"! 要確認あり（{candidate.Conflicts.Count:N0}件）";
                var conflictBrush = ToBrush(candidate.Conflicts.Count == 0 ? "#166534" : "#B91C1C");
                return new LegacyMatchingAlternativeRow(
                    targetText,
                    GetLegacyMatchingAlternativeIdentifier(candidate, sourceCustomerIds),
                    comparisonLines,
                    conflictText,
                    conflictBrush,
                    $"判定：{GetLegacyMatchingDecisionText(GetLegacyGraphRecommendationDecision(candidate))}",
                    candidate.CandidateId);
            })
            .ToArray();
        return alternatives;
    }

    private string GetLegacyMatchingTargetText(
        AbacusRecommendationCandidate candidate,
        IReadOnlySet<string> sourceCustomerIds)
    {
        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            var otherId = sourceCustomerIds.Contains(candidate.SubjectId)
                ? candidate.TargetId
                : candidate.SubjectId;
            return FindLegacyGraphCustomerById(otherId) is { } customer
                ? GetLegacyGraphCustomerDisplayName(customer)
                : "顧客候補";
        }

        return candidate.TargetKind switch
        {
            AbacusRecommendationEntityKinds.Customer when FindLegacyGraphCustomerById(candidate.TargetId) is { } customer =>
                GetLegacyGraphCustomerDisplayName(customer),
            AbacusRecommendationEntityKinds.Vehicle when FindLegacyGraphVehicleById(candidate.TargetId) is { } vehicle =>
                $"{Fallback(vehicle.Maker)} {vehicle.DisplayName}".Trim(),
            _ => "接続先候補",
        };
    }

    private string GetLegacyMatchingAlternativeIdentifier(
        AbacusRecommendationCandidate candidate,
        IReadOnlySet<string> sourceCustomerIds)
    {
        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            var otherId = sourceCustomerIds.Contains(candidate.SubjectId)
                ? candidate.TargetId
                : candidate.SubjectId;
            return FindLegacyGraphCustomerById(otherId) is { } customer
                ? $"顧客番号: {Fallback(customer.CustomerNumber)}"
                : "顧客番号: —";
        }

        return candidate.TargetKind switch
        {
            AbacusRecommendationEntityKinds.Customer when FindLegacyGraphCustomerById(candidate.TargetId) is { } customer =>
                $"顧客番号: {Fallback(customer.CustomerNumber)}",
            AbacusRecommendationEntityKinds.Vehicle when FindLegacyGraphVehicleById(candidate.TargetId) is { } vehicle =>
                $"登録番号: {Fallback(vehicle.RegistrationNumber)} / 車台番号: {Fallback(vehicle.ChassisNumber)}",
            AbacusRecommendationEntityKinds.Document when FindLegacyRecommendationDocumentById(candidate.TargetId) is { } document =>
                $"書類番号: {Fallback(document.DocumentNumber)}",
            _ => "識別情報: —",
        };
    }

    private string GetLegacyGraphCustomerMergePreviewKey(
        AbacusLegacyExportCandidateGraphCustomer sourceCustomer,
        AbacusLegacyExportCandidateGraphCustomer targetCustomer)
    {
        var sourceKey = GetLegacyCustomerMergeKey(sourceCustomer);
        if (TryGetLegacyGraphMergeGroup(sourceKey, out var sourceGroup) &&
            sourceGroup.CustomerIds.Contains(targetCustomer.CustomerId, StringComparer.Ordinal))
        {
            return sourceKey;
        }

        var targetKey = GetLegacyCustomerMergeKey(targetCustomer);
        if (string.Equals(sourceKey, targetKey, StringComparison.Ordinal))
        {
            return sourceKey;
        }

        return BuildLegacyGraphCustomerMergeCandidateId(
            "preview",
            sourceCustomer.CustomerId,
            targetCustomer.CustomerId);
    }

    private string BuildLegacyMatchingMergePreviewText(AbacusRecommendationCandidate candidate)
    {
        if (!IsLegacyMatchingCustomerMerge(candidate) ||
            FindLegacyGraphCustomerById(candidate.SubjectId) is not { } sourceCustomer ||
            FindLegacyGraphCustomerById(candidate.TargetId) is not { } targetCustomer)
        {
            return "";
        }

        var mergeKey = GetLegacyGraphCustomerMergePreviewKey(sourceCustomer, targetCustomer);
        var logicalMembers = GetLegacyGraphLogicalCustomerMembers(sourceCustomer);
        var logicalIds = logicalMembers
            .Select(customer => customer.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        var customers = logicalIds.Contains(targetCustomer.CustomerId)
            ? logicalMembers
            : TryGetLegacyGraphMergeGroup(GetLegacyCustomerMergeKey(sourceCustomer), out var mergeGroup) &&
              mergeGroup.CustomerIds.Contains(targetCustomer.CustomerId, StringComparer.Ordinal)
                ? GetLegacyGraphCandidateGroupMembers(sourceCustomer)
                : logicalMembers
                    .Append(targetCustomer)
                    .GroupBy(customer => customer.CustomerId, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
        var vehicles = customers
            .SelectMany(GetLegacyGraphVehiclesForDisplay)
            .Where(vehicle => !IsLegacyGraphVehicleInTrash(vehicle))
            .GroupBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var documents = customers
            .SelectMany(GetLegacyMatchingDocumentsForCustomer)
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var fieldLines = GetLegacyGraphCustomerMergeFields()
            .Select(field =>
            {
                legacyGraphCustomerMergeDrafts.TryGetValue(mergeKey, out var draft);
                if (draft?.SelectedValues.TryGetValue(field.Key, out var selectedValue) == true &&
                    !string.IsNullOrWhiteSpace(selectedValue))
                {
                    return $"{field.Label}: {Fallback(selectedValue)}";
                }

                var values = customers.Select(field.ValueSelector)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return values.Length switch
                {
                    0 => $"{field.Label}: 情報なし",
                    1 => $"{field.Label}: {Fallback(values[0])}",
                    _ => $"{field.Label}: 要選択（{string.Join(" / ", values.Select(Fallback))}）",
                };
            });

        return $"統合候補: {string.Join(" / ", customers.Select(GetLegacyGraphCustomerDisplayName))}\n" +
               $"統合後の構成: 車両 {vehicles.Length:N0}台 / 書類 {documents.Length:N0}件\n" +
               "採用値:\n" + string.Join("\n", fieldLines) +
               "\n差異がある項目は、既存の統合プレビューで採用値を選択してから確定します。";
    }

    private static bool IsLegacyMatchingCustomerMerge(AbacusRecommendationCandidate candidate) =>
        candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
        candidate.TargetKind == AbacusRecommendationEntityKinds.Customer;

    private bool ShouldReverseLegacyMatchingCustomerMerge(
        AbacusRecommendationCandidate candidate,
        AbacusLegacyExportCandidateGraphCustomer focusCustomer)
    {
        if (!IsLegacyMatchingCustomerMerge(candidate))
        {
            return false;
        }

        var logicalCustomerIds = GetLegacyGraphLogicalCustomerMembers(focusCustomer)
            .Select(customer => customer.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        // 推薦データは Subject -> Target の順ですが、顧客統合の画面では
        // 現在顧客を左、統合候補を右に固定して「現在顧客 ← 候補」と表示します。
        // 現在顧客が Target 側のときだけ、値の左右を入れ替えます。
        return logicalCustomerIds.Contains(candidate.TargetId) &&
               !logicalCustomerIds.Contains(candidate.SubjectId);
    }

    private static (string SourceValue, string CandidateValue) GetLegacyMatchingDisplayedValues(
        string sourceValue,
        string candidateValue,
        bool reverse)
    {
        return reverse
            ? (candidateValue, sourceValue)
            : (sourceValue, candidateValue);
    }

    private static string GetLegacyMatchingDisplayedMissingSide(
        string missingSide,
        bool reverse)
    {
        if (!reverse)
        {
            return missingSide;
        }

        return missingSide switch
        {
            "source" => "candidate",
            "candidate" => "source",
            _ => missingSide,
        };
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetLegacyMatchingDocumentsForCustomer(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var documents = GetLegacyGraphVehiclesForDisplay(customer)
            .SelectMany(GetDocumentsForVehicle)
            .Concat(GetLegacyGraphCustomerDirectDocuments(customer))
            .Where(document => !IsLegacyGraphDocumentInTrash(document) &&
                               !IsLegacyGraphDocumentInTray(document))
            .ToList();
        return documents
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string ShortenLegacyMatchingInternalValue(string value)
    {
        const int maxLength = 34;
        if (value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..20]}…{value[^12..]}";
    }

    private static string GetLegacyMatchingMatchKindText(string matchKind) => matchKind switch
    {
        "exact" => "完全一致",
        "fuzzy" => "軽微な表記差",
        _ => "判定済み",
    };

    private void ApplyLegacyMatchingDecisionBadgeStyle(string? decision)
    {
        var (background, border, foreground) = decision switch
        {
            AbacusRecommendationDecisionValues.Approved => ("#ECFDF5", "#86EFAC", "#166534"),
            AbacusRecommendationDecisionValues.Rejected => ("#F8FAFC", "#CBD5E1", "#64748B"),
            AbacusRecommendationDecisionValues.Hold => ("#FFFBEB", "#FCD34D", "#92400E"),
            AbacusRecommendationDecisionValues.Pending => ("#FFF7ED", "#FDBA74", "#9A3412"),
            _ => ("#F1F5F9", "#CBD5E1", "#52647A"),
        };
        LegacyMatchingCurrentDecisionBadge.Background = ToBrush(background);
        LegacyMatchingCurrentDecisionBadge.BorderBrush = ToBrush(border);
        LegacyMatchingCurrentDecisionText.Foreground = ToBrush(foreground);
    }

    private void SetLegacyMatchingDetailsVisible(bool visible)
    {
        legacyMatchingDetailsVisible = visible;
        LegacyMatchingDetailsView.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        LegacyMatchingDetailsButton.Visibility = visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        LegacyMatchingBackToRecommendationButton.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (visible)
        {
            LegacyMatchingCurrentRecommendationScrollViewer.Visibility = Visibility.Collapsed;
            LegacyMatchingNoRecommendationText.Visibility = Visibility.Collapsed;
        }
        else if (legacyGraphSelectedRecommendation is not null)
        {
            LegacyMatchingCurrentRecommendationScrollViewer.Visibility = Visibility.Visible;
            LegacyMatchingNoRecommendationText.Visibility = Visibility.Collapsed;
        }
        else
        {
            LegacyMatchingCurrentRecommendationScrollViewer.Visibility = Visibility.Collapsed;
        }
    }

    private void LegacyMatchingDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!legacyMatchingDetailsVisible && legacyGraphSelectedRecommendation is not null)
        {
            SetLegacyMatchingDetailsVisible(true);
        }
    }

    private void LegacyMatchingBackToRecommendationButton_Click(object sender, RoutedEventArgs e)
    {
        if (legacyMatchingDetailsVisible)
        {
            SetLegacyMatchingDetailsVisible(false);
        }
    }

    private void LegacyMatchingInternalValueCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string value || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        Clipboard.SetText(value);
        LegacyGraphStatusText.Text = "内部情報をコピーしました。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private void SetLegacyMatchingHeader(
        string title,
        string progress,
        string summary,
        bool hasCustomer,
        bool canGoPrevious,
        bool canGoNext)
    {
        LegacyMatchingCustomerTitleText.Text = title;
        LegacyMatchingTopCustomerTitleText.Text = title;
        LegacyMatchingCustomerProgressText.Text = progress;
        LegacyMatchingTopCustomerProgressText.Text = progress;
        LegacyMatchingCustomerSummaryText.Text = summary;
        LegacyMatchingTopCustomerSummaryText.Text = summary;
        LegacyMatchingPreviousButton.IsEnabled = canGoPrevious;
        LegacyMatchingTopPreviousButton.IsEnabled = canGoPrevious;
        LegacyMatchingNextButton.IsEnabled = canGoNext;
        LegacyMatchingTopNextButton.IsEnabled = canGoNext;
        LegacyMatchingTopEditCustomerNameButton.IsEnabled = hasCustomer;
    }

    private void LegacyMatchingEditCustomerNameButton_Click(object sender, RoutedEventArgs e)
    {
        var customer = string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase)
            ? GetLegacyGraphMatchingCustomer()
            : legacyGraphSelectedItem as AbacusLegacyExportCandidateGraphCustomer;
        if (customer is not null)
        {
            EditLegacyGraphCustomerName(customer);
        }
    }

    private void EditLegacyGraphCustomerName(
        AbacusLegacyExportCandidateGraphCustomer selectedCustomer)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var sourceCustomer = GetLegacyGraphSourceCustomer(selectedCustomer);
        var mergeKey = GetLegacyCustomerMergeKey(selectedCustomer);
        var isMergedDisplay = selectedCustomer.CustomerId.StartsWith("merge-preview:", StringComparison.Ordinal);
        var overrideKey = isMergedDisplay
            ? $"merge:{mergeKey}"
            : $"customer:{sourceCustomer.CustomerId}";
        var currentName = GetLegacyGraphCustomerDisplayName(selectedCustomer);
        var textBox = new TextBox
        {
            MinHeight = 36,
            Padding = new Thickness(8, 5, 8, 5),
            Text = currentName,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var dialog = new Window
        {
            Owner = this,
            Title = isMergedDisplay ? "統合後の顧客名を変更" : "顧客名を変更",
            Width = 520,
            Height = 190,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var saveButton = new Button
        {
            Content = "保存",
            MinWidth = 110,
            MinHeight = 34,
            IsDefault = true,
        };
        var cancelButton = new Button
        {
            Content = "キャンセル",
            MinWidth = 110,
            MinHeight = 34,
            IsCancel = true,
        };
        var errorText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = ToBrush("#B91C1C"),
            TextWrapping = TextWrapping.Wrap,
        };
        saveButton.Click += (_, _) =>
        {
            var name = textBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                errorText.Text = "顧客名を入力してください。";
                return;
            }

            if (string.Equals(name, currentName, StringComparison.Ordinal))
            {
                dialog.DialogResult = true;
                return;
            }

            legacyGraphCustomerNameOverrides[overrideKey] = name;
            dialog.DialogResult = true;
        };
        var buttons = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0),
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancelButton);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock
        {
            Text = isMergedDisplay
                ? "統合後にキャンバスへ表示する名称を入力してください。統合元の原本名称は保持されます。"
                : "顧客名を入力してください。統合元の顧客情報と原本CSVは変更しません。",
            Foreground = ToBrush("#52647A"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(textBox);
        content.Children.Add(errorText);
        content.Children.Add(buttons);
        dialog.Content = content;
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var updatedName = legacyGraphCustomerNameOverrides.TryGetValue(overrideKey, out var savedName)
            ? savedName
            : currentName;
        if (string.Equals(updatedName, currentName, StringComparison.Ordinal))
        {
            return;
        }

        if (!isMergedDisplay &&
            TryGetLegacyGraphMergeGroup(mergeKey, out var editedGroup) &&
            editedGroup.CustomerIds.Count > 1)
        {
            // 統合元の名称を変えた場合は、保存済みの採用値を再確認させます。
            InvalidateLegacyGraphMergeGroupState(mergeKey);
        }
        InvalidateLegacyGraphCustomerApproval(sourceCustomer);
        InvalidateLegacyGraphImportConfirmation();
        RebuildLegacyGraphRecommendationCandidates();
        legacyGraphSelectedRecommendation = null;
        if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            EnsureLegacyGraphMatchingCustomerSelection();
            RefreshLegacyGraphCustomerList();
            RefreshLegacyMatchingView();
        }
        else
        {
            var displayCustomer = GetLegacyGraphDisplayCustomer(sourceCustomer);
            legacyGraphSelectedItem = displayCustomer;
            RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(sourceCustomer));
            UpdateLegacyGraphInspector(displayCustomer);
            RenderLegacyGraphCustomer(displayCustomer);
        }

        RefreshLegacyGraphSearchResults();
        LegacyGraphStatusText.Text = $"顧客名を「{legacyGraphCustomerNameOverrides[overrideKey]}」へ変更しました。元データは変更していません。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
        ScheduleLegacyGraphCheckpointSave();
    }

    private IReadOnlyList<(AbacusLegacyExportCandidateGraphDocument Document, string OwnerLabel)> GetLegacyGraphConfirmedDocumentsForMatching(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var documents = new List<(AbacusLegacyExportCandidateGraphDocument Document, string OwnerLabel)>();
        foreach (var vehicle in GetLegacyGraphVehiclesForDisplay(customer))
        {
            foreach (var document in GetDocumentsForVehicle(vehicle).Where(IsLegacyGraphConfirmedDocumentForMatching))
            {
                documents.Add((document, $"車両 {Fallback(vehicle.DisplayName)}"));
            }
        }

        foreach (var document in GetLegacyGraphCustomerDirectDocuments(customer)
                     .Where(IsLegacyGraphConfirmedDocumentForMatching))
        {
            documents.Add((document, "顧客へ直接紐付け"));
        }

        return documents
            .GroupBy(item => GetLegacyDocumentKey(item.Document), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Document.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Document.DocumentNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool IsLegacyGraphConfirmedDocumentForMatching(
        AbacusLegacyExportCandidateGraphDocument document) =>
        !IsLegacyGraphDocumentInTrash(document) &&
        !IsLegacyGraphDocumentInTray(document) &&
        !IsLegacyGraphDocumentUnconnected(document);

    private IReadOnlyList<AbacusRecommendationCandidate> GetLegacyGraphMatchingRecommendations(
        AbacusLegacyExportCandidateGraphCustomer customer,
        bool includeCompletedCustomerCandidates = false)
    {
        var includeGraphOriginalCustomerDocuments =
            !string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase);
        return GetLegacyGraphRecommendationsForCustomer(
            customer,
            includeCompletedCustomerCandidates,
            includeGraphOriginalCustomerDocuments);
    }

    private IReadOnlyList<AbacusRecommendationCandidate> GetLegacyGraphCustomerReviewRecommendations(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        // 顧客単位の最終確認は表示モードに依存させず、候補が実際に示す
        // 顧客範囲だけを共通のゲートとして評価します。
        return GetLegacyGraphRecommendationsForCustomer(
            customer,
            includeCompletedCustomerCandidates: false,
            includeGraphOriginalCustomerDocuments: false);
    }

    private IReadOnlyList<AbacusRecommendationCandidate> GetLegacyGraphRecommendationsForCustomer(
        AbacusLegacyExportCandidateGraphCustomer customer,
        bool includeCompletedCustomerCandidates,
        bool includeGraphOriginalCustomerDocuments)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        var sourceCustomerIds = GetLegacyGraphLogicalCustomerMembers(customer)
            .Select(candidate => candidate.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        var filtered = legacyGraphRecommendationCandidates
            .Where(candidate => IsLegacyGraphRecommendationForCustomer(
                candidate,
                sourceCustomerIds,
                includeGraphOriginalCustomerDocuments))
            .Where(IsLegacyGraphRecommendationActive)
            .Where(candidate => includeCompletedCustomerCandidates ||
                                LegacyMatchingCategoryKinds.GetKind(candidate) != LegacyMatchingCategoryKinds.Customer ||
                                GetLegacyGraphRecommendationDecision(candidate) is
                                    AbacusRecommendationDecisionValues.Pending or
                                    AbacusRecommendationDecisionValues.Hold)
            .Where(candidate => candidate.SubjectKind != AbacusRecommendationEntityKinds.Document ||
                                legacyExportCandidateGraphResult.AllDocuments.FirstOrDefault(document =>
                                    string.Equals(GetLegacyDocumentKey(document), candidate.SubjectId, StringComparison.OrdinalIgnoreCase)) is not { } document ||
                                !IsLegacyGraphDocumentInTrash(document))
            .Where(candidate => candidate.SubjectKind != AbacusRecommendationEntityKinds.Vehicle ||
                                FindLegacyGraphVehicleById(candidate.SubjectId) is not { } vehicle ||
                                !IsLegacyGraphVehicleInTrash(vehicle))
            .ToArray();

        return CollapseLegacyGraphRecommendationGroups(filtered);
    }

    private bool IsLegacyGraphRecommendationActive(AbacusRecommendationCandidate candidate) =>
        (!legacyGraphRecommendationStates.TryGetValue(candidate.CandidateId, out var state) ||
         state.Lifecycle == LegacyGraphRecommendationLifecycle.Active) &&
        LegacyGraphRecommendationAvailability.AreEndpointsActive(
            candidate,
            legacyGraphTrashCustomerIds,
            legacyGraphTrashVehicleIds,
            legacyGraphTrashDocumentKeys) &&
        AreLegacyGraphRecommendationEntitiesActive(candidate);

    private bool AreLegacyGraphRecommendationEntitiesActive(
        AbacusRecommendationCandidate candidate)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return false;
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            return FindLegacyGraphCustomerById(candidate.SubjectId) is { } sourceCustomer &&
                   FindLegacyGraphCustomerById(candidate.TargetId) is { } targetCustomer &&
                   !IsLegacyGraphCustomerInTrash(sourceCustomer.CustomerId) &&
                   !IsLegacyGraphCustomerInTrash(targetCustomer.CustomerId);
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Document)
        {
            var document = FindLegacyRecommendationDocumentById(candidate.SubjectId);
            if (document is null || IsLegacyGraphDocumentInTrash(document))
            {
                return false;
            }

            return candidate.TargetKind switch
            {
                AbacusRecommendationEntityKinds.Vehicle =>
                    FindLegacyGraphVehicleById(candidate.TargetId) is { } vehicle &&
                    !IsLegacyGraphVehicleInTrash(vehicle) &&
                    FindCurrentCustomerForVehicle(vehicle) is { } vehicleCustomer &&
                    !IsLegacyGraphCustomerInTrash(vehicleCustomer.CustomerId),
                AbacusRecommendationEntityKinds.Customer =>
                    FindLegacyGraphCustomerById(candidate.TargetId) is { } customer &&
                    !IsLegacyGraphCustomerInTrash(customer.CustomerId),
                _ => false,
            };
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Vehicle &&
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            return FindLegacyGraphVehicleById(candidate.SubjectId) is { } vehicle &&
                   FindLegacyGraphCustomerById(candidate.TargetId) is { } customer &&
                   !IsLegacyGraphVehicleInTrash(vehicle) &&
                   (FindCurrentCustomerForVehicle(vehicle) is not { } currentCustomer ||
                    !IsLegacyGraphCustomerInTrash(currentCustomer.CustomerId)) &&
                   !IsLegacyGraphCustomerInTrash(customer.CustomerId);
        }

        return false;
    }

    private IReadOnlyList<AbacusRecommendationCandidate> CollapseLegacyGraphRecommendationGroups(
        IEnumerable<AbacusRecommendationCandidate> candidates)
    {
        var orderedCandidates = candidates
            .OrderBy(GetLegacyGraphRecommendationDecisionRank)
            .ThenByDescending(candidate => candidate.IsEligible)
            .ThenByDescending(candidate => candidate.HasStrongEvidence)
            .ThenByDescending(candidate => candidate.MatchedFields.Count)
            .ThenBy(candidate => candidate.SubjectKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SubjectId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetId, StringComparer.Ordinal)
            .ToArray();
        return LegacyMatchingRecommendationGrouping
            .Group(orderedCandidates, GetLegacyGraphRecommendationActionKey)
            .Select(group => group.Representative)
            .ToArray();
    }

    private int GetLegacyGraphRecommendationDecisionRank(
        AbacusRecommendationCandidate candidate) =>
        GetLegacyGraphRecommendationDecision(candidate) switch
        {
            AbacusRecommendationDecisionValues.Approved => 0,
            AbacusRecommendationDecisionValues.Pending => 1,
            AbacusRecommendationDecisionValues.Hold => 2,
            AbacusRecommendationDecisionValues.Rejected => 3,
            _ => 4,
        };

    private string GetLegacyGraphRecommendationActionKey(
        AbacusRecommendationCandidate candidate)
    {
        var categoryKind = LegacyMatchingCategoryKinds.GetKind(candidate);
        if (categoryKind == LegacyMatchingCategoryKinds.Customer)
        {
            var left = GetLegacyGraphRecommendationCustomerScopeKey(candidate.SubjectId);
            var right = GetLegacyGraphRecommendationCustomerScopeKey(candidate.TargetId);
            if (string.CompareOrdinal(left, right) > 0)
            {
                (left, right) = (right, left);
            }

            return string.Join("\u001F", "customer-merge", left, right);
        }

        var targetScope = GetLegacyGraphRecommendationTargetScopeKey(candidate);
        return string.Join("\u001F", categoryKind, candidate.SubjectKind, candidate.SubjectId, targetScope);
    }

    private string GetLegacyGraphRecommendationTargetScopeKey(
        AbacusRecommendationCandidate candidate)
    {
        if (candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            return GetLegacyGraphRecommendationCustomerScopeKey(candidate.TargetId);
        }

        if (candidate.TargetKind == AbacusRecommendationEntityKinds.Vehicle &&
            FindLegacyGraphVehicleById(candidate.TargetId) is { } vehicle)
        {
            return FindCurrentCustomerForVehicle(vehicle) is { } currentCustomer
                ? GetLegacyGraphRecommendationCustomerScopeKey(currentCustomer.CustomerId)
                : $"vehicle:{vehicle.VehicleId}";
        }

        return $"{candidate.TargetKind}:{candidate.TargetId}";
    }

    private string GetLegacyGraphRecommendationCustomerScopeKey(string customerId)
    {
        return FindLegacyGraphCustomerById(customerId) is { } customer
            ? GetLegacyGraphLogicalCustomerKey(customer)
            : $"customer:{customerId}";
    }

    private bool TryGetLegacyGraphWorkTargetCustomerIds(
        string workTargetKey,
        out HashSet<string> customerIds)
    {
        customerIds = new HashSet<string>(StringComparer.Ordinal);
        if (legacyGraphCustomerMergeGroups.TryGetValue(workTargetKey, out var group))
        {
            customerIds.UnionWith(group.CustomerIds.Where(customerId =>
                !legacyGraphTrashCustomerIds.Contains(customerId)));
            return customerIds.Count > 0;
        }

        const string customerPrefix = "customer:";
        if (workTargetKey.StartsWith(customerPrefix, StringComparison.Ordinal))
        {
            var customerId = workTargetKey[customerPrefix.Length..];
            if (FindLegacyGraphCustomerById(customerId) is not null &&
                !legacyGraphTrashCustomerIds.Contains(customerId))
            {
                customerIds.Add(customerId);
            }
        }

        return customerIds.Count > 0;
    }

    private AbacusLegacyExportCandidateGraphCustomer? GetLegacyGraphScopeFocusCustomer()
    {
        if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(legacyGraphMatchingCustomerId) &&
            FindLegacyGraphCustomerById(legacyGraphMatchingCustomerId) is { } matchingCustomer)
        {
            return matchingCustomer;
        }

        return legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphCustomer selectedCustomer
            ? GetLegacyGraphSourceCustomer(selectedCustomer)
            : null;
    }

    private string GetLegacyGraphRecommendationWorkTargetKey(
        AbacusLegacyExportCandidateGraphCustomer customer) =>
        string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase)
            ? GetLegacyGraphLogicalCustomerKey(customer)
            : GetLegacyCustomerMergeKey(customer);

    private bool TryGetLegacyGraphCurrentRecommendationScope(
        AbacusRecommendationCandidate candidate,
        out LegacyCustomerRecommendationScope scope)
    {
        scope = null!;
        var focusCustomer = GetLegacyGraphScopeFocusCustomer();
        if (focusCustomer is null)
        {
            return false;
        }

        var workTargetKey = GetLegacyGraphRecommendationWorkTargetKey(focusCustomer);
        return TryGetLegacyGraphWorkTargetCustomerIds(workTargetKey, out var workTargetCustomerIds) &&
               LegacyCustomerRecommendationScope.TryCreate(
                   candidate,
                   workTargetKey,
                   workTargetCustomerIds,
                   out scope);
    }

    private bool TryGetLegacyGraphRecommendationScopeForKey(
        AbacusRecommendationCandidate candidate,
        string workTargetKey,
        out LegacyCustomerRecommendationScope scope)
    {
        scope = null!;
        return TryGetLegacyGraphWorkTargetCustomerIds(workTargetKey, out var workTargetCustomerIds) &&
               LegacyCustomerRecommendationScope.TryCreate(
                   candidate,
                   workTargetKey,
                   workTargetCustomerIds,
                   out scope);
    }

    private bool TryInferLegacyGraphRecommendationScope(
        AbacusRecommendationCandidate candidate,
        IReadOnlySet<string>? preferredWorkTargetKeys,
        out LegacyCustomerRecommendationScope scope)
    {
        scope = null!;
        if (candidate.SubjectKind != AbacusRecommendationEntityKinds.Customer ||
            candidate.TargetKind != AbacusRecommendationEntityKinds.Customer)
        {
            return false;
        }

        var customerIds = new[] { candidate.SubjectId, candidate.TargetId };
        var candidateWorkTargetKeys = customerIds
            .Select(FindLegacyGraphCustomerById)
            .Where(customer => customer is not null)
            .Cast<AbacusLegacyExportCandidateGraphCustomer>()
            .Select(GetLegacyGraphRecommendationWorkTargetKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var preferredKeys = preferredWorkTargetKeys is null
            ? Array.Empty<string>()
            : candidateWorkTargetKeys
                .Where(preferredWorkTargetKeys.Contains)
                .ToArray();
        foreach (var workTargetKey in preferredKeys
                     .Concat(candidateWorkTargetKeys.Except(preferredKeys, StringComparer.Ordinal)))
        {
            if (TryGetLegacyGraphRecommendationScopeForKey(candidate, workTargetKey, out scope))
            {
                return true;
            }
        }

        var fallbackWorkTargetKey = $"customer:{candidate.SubjectId}";
        return TryGetLegacyGraphRecommendationScopeForKey(
            candidate,
            fallbackWorkTargetKey,
            out scope);
    }

    private bool CandidateMatchesLegacyGraphRecommendationScope(
        AbacusRecommendationCandidate candidate,
        LegacyCustomerRecommendationScope scope) =>
        TryGetLegacyGraphRecommendationScopeForKey(candidate, scope.WorkTargetKey, out var candidateScope) &&
        candidateScope.Matches(scope);

    private IEnumerable<KeyValuePair<string, LegacyGraphRecommendationState>>
        GetActiveLegacyGraphRecommendationScopeStates() =>
        legacyGraphRecommendationStates
            .Where(pair => pair.Value.Lifecycle == LegacyGraphRecommendationLifecycle.Active &&
                           !string.IsNullOrWhiteSpace(pair.Value.WorkTargetKey) &&
                           !string.IsNullOrWhiteSpace(pair.Value.ExternalCustomerId));

    private void HydrateLegacyGraphRecommendationScopes(
        IReadOnlySet<string>? preferredWorkTargetKeys = null)
    {
        foreach (var pair in legacyGraphRecommendationStates.ToArray())
        {
            var state = pair.Value;
            if (state.Lifecycle != LegacyGraphRecommendationLifecycle.Active ||
                !string.IsNullOrWhiteSpace(state.WorkTargetKey) ||
                !string.IsNullOrWhiteSpace(state.ExternalCustomerId) ||
                legacyGraphRecommendationCandidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.CandidateId, pair.Key, StringComparison.OrdinalIgnoreCase)) is not { } candidate ||
                !TryInferLegacyGraphRecommendationScope(candidate, preferredWorkTargetKeys, out var scope))
            {
                continue;
            }

            legacyGraphRecommendationStates[pair.Key] = state with
            {
                WorkTargetKey = scope.WorkTargetKey,
                ExternalCustomerId = scope.ExternalCustomerId,
            };
        }

        foreach (var index in Enumerable.Range(0, legacyGraphMatchingChanges.Count).Reverse())
        {
            var change = legacyGraphMatchingChanges[index];
            if (change.Kind != "recommendation" ||
                !string.IsNullOrWhiteSpace(change.WorkTargetKey) ||
                !string.IsNullOrWhiteSpace(change.ExternalCustomerId) ||
                legacyGraphRecommendationCandidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.CandidateId, change.SubjectId, StringComparison.OrdinalIgnoreCase)) is not { } candidate ||
                !TryInferLegacyGraphRecommendationScope(candidate, preferredWorkTargetKeys, out var scope))
            {
                continue;
            }

            legacyGraphMatchingChanges[index] = change with
            {
                WorkTargetKey = scope.WorkTargetKey,
                ExternalCustomerId = scope.ExternalCustomerId,
            };
        }
    }

    private IReadOnlyList<AbacusRecommendationCandidate> GetLegacyGraphRecommendationGroupMembers(
        AbacusRecommendationCandidate candidate) =>
        legacyGraphRecommendationCandidates
            .Where(item => string.Equals(
                GetLegacyGraphRecommendationActionKey(item),
                GetLegacyGraphRecommendationActionKey(candidate),
                StringComparison.OrdinalIgnoreCase))
            .Where(IsLegacyGraphRecommendationActive)
            .OrderBy(GetLegacyGraphRecommendationDecisionRank)
            .ThenByDescending(item => item.IsEligible)
            .ThenByDescending(item => item.HasStrongEvidence)
            .ThenByDescending(item => item.MatchedFields.Count)
            .ThenBy(item => item.CandidateId, StringComparer.Ordinal)
            .ToArray();

    private void RebuildLegacyGraphRecommendationCandidates()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            legacyGraphRecommendationCandidates = [];
            legacyGraphRecommendationDecisions.Clear();
            legacyGraphRecommendationStates.Clear();
            return;
        }

        legacyGraphRecommendationCandidates = AugmentLegacyGraphCustomerMergeCandidates(
            legacyRecommendationEngine.Build(
                legacyExportCandidateGraphResult,
                GetLegacyGraphCustomerNameById));
        var validCandidateIds = legacyGraphRecommendationCandidates
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateId in legacyGraphRecommendationStates.Keys
                     .Where(candidateId => !validCandidateIds.Contains(candidateId))
                     .ToArray())
        {
            var state = legacyGraphRecommendationStates[candidateId];
            if (state.Lifecycle == LegacyGraphRecommendationLifecycle.Active &&
                !string.IsNullOrWhiteSpace(state.WorkTargetKey) &&
                !string.IsNullOrWhiteSpace(state.ExternalCustomerId))
            {
                continue;
            }

            legacyGraphRecommendationStates[candidateId] =
                LegacyGraphRecommendationLifecycleReconciler.MarkObsoleteAfterRebuild(
                    state,
                    "候補の再評価により現在の候補から外れました。",
                    DateTimeOffset.UtcNow);
        }

        HydrateLegacyGraphRecommendationScopes();
        ReconcileLegacyGraphRecommendationLifecycles(legacyGraphRecommendationCandidates);
        legacyGraphRecommendationDecisions.Clear();
        foreach (var candidate in legacyGraphRecommendationCandidates)
        {
            var decision = GetLegacyGraphRecommendationDecision(candidate);
            if (decision != AbacusRecommendationDecisionValues.Pending)
            {
                legacyGraphRecommendationDecisions[candidate.CandidateId] = decision;
            }
        }

    }

    private void RebuildLegacyGraphRecommendationCandidatesForCustomers(
        IReadOnlySet<string> affectedCustomerIds)
    {
        if (legacyExportCandidateGraphResult is null || affectedCustomerIds.Count == 0)
        {
            return;
        }

        var affectedExisting = legacyGraphRecommendationCandidates
            .Where(candidate => IsLegacyGraphRecommendationAffected(candidate, affectedCustomerIds))
            .ToHashSet();
        var recalculated = legacyRecommendationEngine.BuildForCustomers(
            legacyExportCandidateGraphResult,
            affectedCustomerIds,
            GetLegacyGraphCustomerNameById);
        legacyGraphRecommendationCandidates = AugmentLegacyGraphCustomerMergeCandidates(
            legacyGraphRecommendationCandidates
            .Where(candidate => !affectedExisting.Contains(candidate))
            .Concat(recalculated)
            .GroupBy(candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(candidate => candidate.IsEligible)
            .ThenByDescending(candidate => candidate.HasStrongEvidence)
            .ThenByDescending(candidate => candidate.MatchedFields.Count)
            .ThenBy(candidate => candidate.SubjectKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SubjectId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetId, StringComparer.Ordinal)
            .ToArray());

        var validCandidateIds = legacyGraphRecommendationCandidates
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateId in legacyGraphRecommendationStates.Keys
                     .Where(candidateId => affectedExisting.Any(candidate =>
                         string.Equals(candidate.CandidateId, candidateId, StringComparison.OrdinalIgnoreCase)) &&
                         !validCandidateIds.Contains(candidateId))
                     .ToArray())
        {
            var state = legacyGraphRecommendationStates[candidateId];
            if (state.Lifecycle == LegacyGraphRecommendationLifecycle.Active &&
                !string.IsNullOrWhiteSpace(state.WorkTargetKey) &&
                !string.IsNullOrWhiteSpace(state.ExternalCustomerId))
            {
                continue;
            }

            legacyGraphRecommendationStates[candidateId] =
                LegacyGraphRecommendationLifecycleReconciler.MarkObsoleteAfterRebuild(
                    state,
                    "統合状態の変更後に候補が解消されました。",
                    DateTimeOffset.UtcNow);
        }

        HydrateLegacyGraphRecommendationScopes(affectedCustomerIds
            .Select(customerId => FindLegacyGraphCustomerById(customerId))
            .Where(customer => customer is not null)
            .Cast<AbacusLegacyExportCandidateGraphCustomer>()
            .Select(GetLegacyGraphRecommendationWorkTargetKey)
            .ToHashSet(StringComparer.Ordinal));
        ReconcileLegacyGraphRecommendationLifecycles(legacyGraphRecommendationCandidates);
        legacyGraphRecommendationDecisions.Clear();
        foreach (var candidate in legacyGraphRecommendationCandidates)
        {
            var decision = GetLegacyGraphRecommendationDecision(candidate);
            if (decision != AbacusRecommendationDecisionValues.Pending)
            {
                legacyGraphRecommendationDecisions[candidate.CandidateId] = decision;
            }
        }

    }

    private void ReconcileLegacyGraphRecommendationLifecycles(
        IEnumerable<AbacusRecommendationCandidate> currentCandidates)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in currentCandidates)
        {
            if (TryGetLegacyGraphCurrentRecommendationScope(candidate, out var scope))
            {
                foreach (var pair in legacyGraphRecommendationStates.ToArray())
                {
                    if (pair.Value.Lifecycle != LegacyGraphRecommendationLifecycle.Obsolete ||
                        !LegacyGraphRecommendationLifecycleReconciler.IsTemporaryManualLinkObsolete(
                            pair.Value.ResolutionReason) ||
                        !string.Equals(pair.Value.WorkTargetKey, scope.WorkTargetKey, StringComparison.Ordinal) ||
                        !string.Equals(pair.Value.ExternalCustomerId, scope.ExternalCustomerId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    legacyGraphRecommendationStates[pair.Key] =
                        LegacyGraphRecommendationLifecycleReconciler.ReconcileCurrentCandidate(
                            pair.Value,
                            now)!;
                }
            }
            else if (legacyGraphRecommendationStates.TryGetValue(candidate.CandidateId, out var state))
            {
                var reconciled = LegacyGraphRecommendationLifecycleReconciler.ReconcileCurrentCandidate(
                    state,
                    now);
                if (reconciled is not null)
                {
                    legacyGraphRecommendationStates[candidate.CandidateId] = reconciled;
                }
            }
        }
    }

    private IReadOnlyList<AbacusRecommendationCandidate> AugmentLegacyGraphCustomerMergeCandidates(
        IEnumerable<AbacusRecommendationCandidate> candidates)
    {
        var all = candidates
            .Concat(BuildLegacyGraphAutomaticCustomerMergeRecommendations())
            .Concat(BuildLegacyGraphManualCustomerMergeRecommendations())
            .GroupBy(candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(candidate => candidate.IsEligible)
            .ThenByDescending(candidate => candidate.HasStrongEvidence)
            .ThenByDescending(candidate => candidate.MatchedFields.Count)
            .ThenBy(candidate => candidate.SubjectKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.SubjectId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetKind, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.TargetId, StringComparer.Ordinal)
            .ToArray();
        return all;
    }

    private IReadOnlyList<AbacusRecommendationCandidate> BuildLegacyGraphAutomaticCustomerMergeRecommendations()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        return legacyExportCandidateGraphResult.Customers
            .Where(customer => !legacyGraphTrashCustomerIds.Contains(customer.CustomerId))
            .Where(customer => !string.IsNullOrWhiteSpace(
                NormalizeLegacyCustomerMergeText(GetLegacyGraphCustomerName(customer))))
            .GroupBy(customer => NormalizeLegacyCustomerMergeText(GetLegacyGraphCustomerName(customer)), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group =>
            {
                var customers = group.OrderBy(customer => customer.CustomerId, StringComparer.Ordinal).ToArray();
                return customers.SelectMany((source, sourceIndex) => customers
                    .Skip(sourceIndex + 1)
                    .Select(target => CreateLegacyGraphAutomaticCustomerMergeRecommendation(source, target)));
            })
            .ToArray();
    }

    private AbacusRecommendationCandidate CreateLegacyGraphAutomaticCustomerMergeRecommendation(
        AbacusLegacyExportCandidateGraphCustomer source,
        AbacusLegacyExportCandidateGraphCustomer target)
    {
        var sourceName = GetLegacyGraphCustomerName(source);
        var targetName = GetLegacyGraphCustomerName(target);
        var differences = string.Equals(
                NormalizeLegacyCustomerMergeText(source.NameKana),
                NormalizeLegacyCustomerMergeText(target.NameKana),
                StringComparison.Ordinal)
            ? Array.Empty<AbacusRecommendationDifference>()
            : new[]
            {
                new AbacusRecommendationDifference(
                    "nameKana",
                    "ふりがな",
                    source.NameKana,
                    target.NameKana),
            };
        return new AbacusRecommendationCandidate(
            BuildLegacyGraphCustomerMergeCandidateId("automatic", source.CustomerId, target.CustomerId),
            AbacusRecommendationEntityKinds.Customer,
            source.CustomerId,
            AbacusRecommendationEntityKinds.Customer,
            target.CustomerId,
            target.CustomerId,
            new[]
            {
                new AbacusRecommendationEvidence(
                    "customerName",
                    "顧客名",
                    sourceName,
                    targetName,
                    "exact",
                    true),
            },
            differences,
            Array.Empty<AbacusRecommendationMissingField>(),
            Array.Empty<string>(),
            "顧客名が完全一致する自動統合候補です。自動確定はせず、ユーザーの確認を待ちます。",
            "automatic");
    }

    private IReadOnlyList<AbacusRecommendationCandidate> BuildLegacyGraphManualCustomerMergeRecommendations()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        return legacyGraphMatchingManualCustomerCandidateTargets
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair =>
            {
                var source = FindLegacyGraphCustomerById(pair.Key);
                return source is null
                    ? []
                    : pair.Value
                        .OrderBy(customerId => customerId, StringComparer.Ordinal)
                        .Select(targetId => (source, target: FindLegacyGraphCustomerById(targetId)))
                        .Where(item => item.target is not null)
                        .Select(item => CreateLegacyGraphManualCustomerMergeRecommendation(
                            item.source!,
                            item.target!));
            })
            .ToArray();
    }

    private static AbacusRecommendationCandidate CreateLegacyGraphManualCustomerMergeRecommendation(
        AbacusLegacyExportCandidateGraphCustomer source,
        AbacusLegacyExportCandidateGraphCustomer target) =>
        new(
            BuildLegacyGraphCustomerMergeCandidateId("manual", source.CustomerId, target.CustomerId),
            AbacusRecommendationEntityKinds.Customer,
            source.CustomerId,
            AbacusRecommendationEntityKinds.Customer,
            target.CustomerId,
            target.CustomerId,
            Array.Empty<AbacusRecommendationEvidence>(),
            Array.Empty<AbacusRecommendationDifference>(),
            Array.Empty<AbacusRecommendationMissingField>(),
            Array.Empty<string>(),
            "検索から手動追加した顧客候補です。統合するかをユーザーが判断します。",
            "manual");

    private static string BuildLegacyGraphCustomerMergeCandidateId(
        string origin,
        string sourceCustomerId,
        string targetCustomerId)
    {
        var orderedIds = new[] { sourceCustomerId, targetCustomerId }
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return $"customer-merge:{origin}:{orderedIds[0]}:{orderedIds[1]}";
    }

    private bool IsLegacyGraphRecommendationAffected(
        AbacusRecommendationCandidate candidate,
        IReadOnlySet<string> affectedCustomerIds)
    {
        if (candidate.TargetCustomerId is not null && affectedCustomerIds.Contains(candidate.TargetCustomerId) ||
            candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
            affectedCustomerIds.Contains(candidate.SubjectId) ||
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer &&
            affectedCustomerIds.Contains(candidate.TargetId))
        {
            return true;
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Vehicle &&
            FindLegacyGraphVehicleById(candidate.SubjectId) is { } vehicle)
        {
            return FindCurrentCustomerForVehicle(vehicle) is { } currentCustomer &&
                   affectedCustomerIds.Contains(currentCustomer.CustomerId);
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Document &&
            FindLegacyRecommendationDocument(candidate) is { } document)
        {
            return FindCurrentCustomerForDocument(document) is { } customer &&
                   affectedCustomerIds.Contains(customer.CustomerId);
        }

        return false;
    }

    private IReadOnlyList<(AbacusRecommendationCandidate Recommendation,
        AbacusLegacyExportCandidateGraphCustomer Customer)> GetLegacyGraphCustomerIntegrationRecommendations(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        var sourceCustomerIds = GetLegacyGraphLogicalCustomerMembers(customer)
            .Select(candidate => candidate.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        var candidatePoolIds = GetLegacyGraphCandidateGroupMembers(customer)
            .Select(candidate => candidate.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        var results = new List<(AbacusRecommendationCandidate Recommendation,
            AbacusLegacyExportCandidateGraphCustomer Customer)>();
        foreach (var recommendation in legacyGraphRecommendationCandidates.Where(candidate =>
                     candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
                     candidate.TargetKind == AbacusRecommendationEntityKinds.Customer &&
                     IsLegacyGraphRecommendationActive(candidate) &&
                     GetLegacyGraphRecommendationDecision(candidate) is
                         AbacusRecommendationDecisionValues.Pending or
                         AbacusRecommendationDecisionValues.Hold))
        {
            var otherCustomerId = sourceCustomerIds.Contains(recommendation.SubjectId)
                ? recommendation.TargetId
                : sourceCustomerIds.Contains(recommendation.TargetId)
                    ? recommendation.SubjectId
                    : null;
            if (otherCustomerId is null || sourceCustomerIds.Contains(otherCustomerId) ||
                (!candidatePoolIds.Contains(otherCustomerId) &&
                 !string.Equals(recommendation.Origin, "manual", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(recommendation.Origin, "automatic", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var otherCustomer = FindLegacyGraphCustomerById(otherCustomerId);
            if (otherCustomer is not null)
            {
                results.Add((recommendation, otherCustomer));
            }
        }

        return results
            .OrderByDescending(item => item.Recommendation.IsEligible)
            .ThenByDescending(item => item.Recommendation.HasStrongEvidence)
            .ThenBy(item => GetLegacyGraphCustomerDisplayName(item.Customer), StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Customer.CustomerId, StringComparer.Ordinal)
            .ToArray();
    }

    private bool IsLegacyGraphRecommendationForCustomer(
        AbacusRecommendationCandidate candidate,
        IReadOnlySet<string> sourceCustomerIds,
        bool includeGraphOriginalCustomerDocuments)
    {
        if (LegacyMatchingWorkflow.IsRelatedToCustomer(candidate, sourceCustomerIds))
        {
            return true;
        }

        if (!includeGraphOriginalCustomerDocuments)
        {
            return false;
        }

        // グラフUIの表示では、元所属顧客の未確定書類もキャンバス上の候補として
        // 扱います。ただし顧客単位の最終確認ではこのフォールバックを使いません。
        if (candidate.SubjectKind != AbacusRecommendationEntityKinds.Document ||
            legacyExportCandidateGraphResult is null)
        {
            return false;
        }

        var document = legacyExportCandidateGraphResult.AllDocuments.FirstOrDefault(item =>
            string.Equals(GetLegacyDocumentKey(item), candidate.SubjectId, StringComparison.OrdinalIgnoreCase));
        var originalCustomer = document is null ? null : FindOriginalCustomerForDocument(document);
        return originalCustomer is not null && sourceCustomerIds.Contains(originalCustomer.CustomerId);
    }

    private LegacyMatchingRecommendationItem CreateLegacyMatchingRecommendationItem(
        AbacusRecommendationCandidate candidate,
        AbacusLegacyExportCandidateGraphCustomer? focusCustomer = null)
    {
        var subjectText = candidate.SubjectKind switch
        {
            AbacusRecommendationEntityKinds.Document when FindLegacyRecommendationDocument(candidate) is { } document =>
                $"{document.Kind} {Fallback(document.DocumentNumber)}",
            AbacusRecommendationEntityKinds.Vehicle when FindLegacyGraphVehicleById(candidate.SubjectId) is { } vehicle =>
                $"{Fallback(vehicle.Maker)} {vehicle.DisplayName}".Trim(),
            AbacusRecommendationEntityKinds.Customer when FindLegacyGraphCustomerById(candidate.SubjectId) is { } customer =>
                GetLegacyGraphCustomerDisplayName(customer),
            _ => $"{candidate.SubjectKind}: {candidate.SubjectId}",
        };
        var targetText = BuildLegacyMatchingTargetText(candidate, focusCustomer);
        if (focusCustomer is not null &&
            candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            var focusSource = focusCustomer;
            var logicalCustomerIds = GetLegacyGraphLogicalCustomerMembers(focusSource)
                .Select(item => item.CustomerId)
                .ToHashSet(StringComparer.Ordinal);
            var otherCustomerId = logicalCustomerIds.Contains(candidate.SubjectId)
                ? candidate.TargetId
                : logicalCustomerIds.Contains(candidate.TargetId)
                    ? candidate.SubjectId
                    : null;
            if (otherCustomerId is not null && FindLegacyGraphCustomerById(otherCustomerId) is { } otherCustomer)
            {
                subjectText = GetLegacyGraphCustomerDisplayName(otherCustomer);
                targetText = $"{GetLegacyGraphMatchingDisplayName(focusSource)}（現在の顧客）";
            }
        }
        var reverseCustomerMergeDisplay = focusCustomer is not null &&
            ShouldReverseLegacyMatchingCustomerMerge(candidate, focusCustomer);
        var decision = GetLegacyGraphRecommendationDecision(candidate);
        var decisionText = decision switch
        {
            AbacusRecommendationDecisionValues.Approved => "承認済み",
            AbacusRecommendationDecisionValues.Rejected => "却下",
            AbacusRecommendationDecisionValues.Hold => "保留",
            _ => "未処理",
        };
        var (background, border, decisionBrush) = decision switch
        {
            AbacusRecommendationDecisionValues.Approved => ("#F0FDF4", "#86EFAC", "#166534"),
            AbacusRecommendationDecisionValues.Rejected => ("#F8FAFC", "#CBD5E1", "#64748B"),
            AbacusRecommendationDecisionValues.Hold => ("#FFFBEB", "#FCD34D", "#92400E"),
            _ when !candidate.IsEligible => ("#FFF1F2", "#FCA5A5", "#B91C1C"),
            _ => ("#FFF7ED", "#FDBA74", "#9A3412"),
        };
        var matchedText = candidate.IsManual
            ? "手動追加"
            : candidate.MatchedFields.Count == 0
                ? "一致: なし"
            : $"一致: {string.Join("、", candidate.MatchedFields.Select(evidence =>
            {
                var values = GetLegacyMatchingDisplayedValues(
                    evidence.SourceValue,
                    evidence.CandidateValue,
                    reverseCustomerMergeDisplay);
                return $"{evidence.Label} {DisplayLegacyMatchingValue(values.SourceValue)} → " +
                    $"{DisplayLegacyMatchingValue(values.CandidateValue)}（{GetLegacyMatchingMatchKindText(evidence.MatchKind)}）";
            }))}";
        var differenceText = candidate.Differences.Count == 0
            ? "差異: なし"
            : $"差異: {string.Join("、", candidate.Differences.Select(difference =>
            {
                var values = GetLegacyMatchingDisplayedValues(
                    difference.SourceValue,
                    difference.CandidateValue,
                    reverseCustomerMergeDisplay);
                return $"{difference.Label}（{DisplayLegacyMatchingValue(values.SourceValue)} → " +
                    $"{DisplayLegacyMatchingValue(values.CandidateValue)}）";
            }))}";
        var conflictText = candidate.Conflicts.Count == 0
            ? ""
            : $"競合: {string.Join("、", candidate.Conflicts)}";
        return new LegacyMatchingRecommendationItem(
            candidate,
            subjectText,
            targetText,
            decisionText,
            matchedText,
            differenceText,
            conflictText,
            BuildLegacyMatchingShortReason(candidate),
            ToBrush(background),
            ToBrush(border),
            ToBrush(decisionBrush));
    }

    private AbacusLegacyExportCandidateGraphDocument? FindLegacyRecommendationDocument(
        AbacusRecommendationCandidate candidate) =>
        legacyExportCandidateGraphResult?.AllDocuments.FirstOrDefault(document =>
            string.Equals(GetLegacyDocumentKey(document), candidate.SubjectId, StringComparison.OrdinalIgnoreCase));

    private string BuildLegacyMatchingTargetText(
        AbacusRecommendationCandidate candidate,
        AbacusLegacyExportCandidateGraphCustomer? focusCustomer)
    {
        var targetText = candidate.TargetKind switch
        {
            AbacusRecommendationEntityKinds.Vehicle when FindLegacyGraphVehicleById(candidate.TargetId) is { } vehicle =>
                $"{Fallback(vehicle.Maker)} {vehicle.DisplayName}".Trim(),
            AbacusRecommendationEntityKinds.Customer when FindLegacyGraphCustomerById(candidate.TargetId) is { } customer =>
                GetLegacyGraphCustomerDisplayName(customer),
            _ => $"紐付け先: {candidate.TargetKind} {candidate.TargetId}",
        };

        if (focusCustomer is null)
        {
            return targetText;
        }

        // 統合後も、ここで表示する「基準顧客」は巡回開始時の顧客を維持します。
        var focusSource = focusCustomer;
        var focusCustomerIds = GetLegacyGraphLogicalCustomerMembers(focusSource)
            .Select(customer => customer.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        if (candidate.TargetKind == AbacusRecommendationEntityKinds.Customer &&
            FindLegacyGraphCustomerById(candidate.TargetId) is { } targetCustomer)
        {
            if (string.Equals(targetCustomer.CustomerId, focusCustomer.CustomerId, StringComparison.Ordinal))
            {
                targetText = GetLegacyGraphMatchingDisplayName(focusCustomer);
            }

            return focusCustomerIds.Contains(targetCustomer.CustomerId)
                ? $"{targetText}（現在の論理顧客）"
                : $"{targetText}（別顧客候補）";
        }

        if (candidate.TargetKind == AbacusRecommendationEntityKinds.Vehicle &&
            FindLegacyGraphVehicleById(candidate.TargetId) is { } targetVehicle)
        {
            var targetVehicleCustomer = FindCurrentCustomerForVehicle(targetVehicle);
            return targetVehicleCustomer is not null &&
                   focusCustomerIds.Contains(targetVehicleCustomer.CustomerId)
                ? $"{targetText}（現在の顧客の車両）"
                : $"{targetText}（別顧客の車両候補）";
        }

        return targetText;
    }

    private string GetLegacyMatchingTargetHeading(
        AbacusRecommendationCandidate candidate,
        AbacusLegacyExportCandidateGraphCustomer focusCustomer)
    {
        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            return "統合先（現在の顧客）";
        }

        var focusCustomerIds = GetLegacyGraphLogicalCustomerMembers(focusCustomer)
            .Select(customer => customer.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        if (candidate.TargetKind == AbacusRecommendationEntityKinds.Customer &&
            FindLegacyGraphCustomerById(candidate.TargetId) is { } targetCustomer)
        {
            return focusCustomerIds.Contains(targetCustomer.CustomerId)
                ? "紐付け先（現在の顧客）"
                : "候補接続先（別顧客）";
        }

        if (candidate.TargetKind == AbacusRecommendationEntityKinds.Vehicle &&
            FindLegacyGraphVehicleById(candidate.TargetId) is { } targetVehicle)
        {
            var targetVehicleCustomer = FindCurrentCustomerForVehicle(targetVehicle);
            return targetVehicleCustomer is not null &&
                   focusCustomerIds.Contains(targetVehicleCustomer.CustomerId)
                ? "紐付け先車両（現在の顧客）"
                : "候補接続先車両（別顧客）";
        }

        return "紐付け先";
    }

    private void LegacyMatchingRecommendationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        legacyGraphSelectedRecommendation = LegacyMatchingRecommendationsList.SelectedItem is LegacyMatchingRecommendationItem item
            ? item.Candidate
            : null;
        RefreshLegacyMatchingCustomerQueue();
        SetLegacyMatchingDetailsVisible(false);
        RefreshLegacyMatchingRecommendationActions();
        RefreshLegacyMatchingRecommendationPreview(legacyGraphSelectedRecommendation);
    }

    private void RefreshLegacyMatchingRecommendationActions()
    {
        if (LegacyMatchingApproveButton is null)
        {
            return;
        }

        var candidate = legacyGraphSelectedRecommendation;
        if (candidate is null)
        {
            LegacyMatchingApproveButton.Content = "承認";
            LegacyMatchingApproveButton.IsEnabled = false;
            LegacyMatchingRejectButton.IsEnabled = false;
            LegacyMatchingHoldButton.IsEnabled = false;
            LegacyMatchingResetButton.IsEnabled = false;
            LegacyMatchingRecommendationStatusText.Text = "おすすめ候補を選択してください。";
            return;
        }

        var decision = GetLegacyGraphRecommendationDecision(candidate);
        var isApproved = decision == AbacusRecommendationDecisionValues.Approved;
        var canApprove = CanManuallyApproveLegacyMatchingCandidate(candidate);
        var requiresManualReview = RequiresLegacyMatchingManualReview(candidate);
        LegacyMatchingApproveButton.Content = "承認";
        LegacyMatchingApproveButton.IsEnabled = canApprove && !isApproved;
        LegacyMatchingRejectButton.IsEnabled = !isApproved;
        LegacyMatchingHoldButton.IsEnabled = !isApproved;
        LegacyMatchingResetButton.IsEnabled = !isApproved && decision != AbacusRecommendationDecisionValues.Pending;
        LegacyMatchingRecommendationStatusText.Text = canApprove
            ? requiresManualReview
                ? $"現在の判定: {GetLegacyMatchingDecisionText(decision)}。競合がありますが、内容を確認して手動承認できます。"
                : $"現在の判定: {GetLegacyMatchingDecisionText(decision)}。"
            : $"現在の判定: {GetLegacyMatchingDecisionText(decision)}。対象データまたは接続先を特定できないため承認できません。";
    }

    private static string GetLegacyMatchingDecisionText(string decision) => decision switch
    {
        AbacusRecommendationDecisionValues.Approved => "承認済み",
        AbacusRecommendationDecisionValues.Rejected => "却下",
        AbacusRecommendationDecisionValues.Hold => "保留",
        _ => "未処理",
    };

    private void ApplyLegacyMatchingDecision(string decision)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var candidate = legacyGraphSelectedRecommendation;
        if (candidate is null)
        {
            return;
        }

        if (decision == AbacusRecommendationDecisionValues.Approved &&
            !CanManuallyApproveLegacyMatchingCandidate(candidate))
        {
            LegacyMatchingRecommendationStatusText.Text = "対象データまたは接続先を特定できないため、この候補は承認できません。";
            LegacyMatchingRecommendationStatusText.Foreground = ToBrush("#B91C1C");
            return;
        }

        if (GetLegacyGraphRecommendationDecision(candidate) == AbacusRecommendationDecisionValues.Approved &&
            decision != AbacusRecommendationDecisionValues.Approved)
        {
            LegacyMatchingRecommendationStatusText.Text = "承認済み候補の判定は、既存リンクを自動解除しないため変更できません。";
            LegacyMatchingRecommendationStatusText.Foreground = ToBrush("#805B10");
            return;
        }

        if (!SetLegacyGraphRecommendationDecision(candidate, decision))
        {
            LegacyMatchingRecommendationStatusText.Text = "おすすめ判定を更新できませんでした。候補パッケージを再確認してください。";
            LegacyMatchingRecommendationStatusText.Foreground = ToBrush("#B91C1C");
            return;
        }

        LegacyMatchingRecommendationStatusText.Foreground = ToBrush("#52647A");
        LegacyGraphStatusText.Text = decision switch
        {
            AbacusRecommendationDecisionValues.Approved => "おすすめを承認し、既存のグラフ状態へ反映しました。",
            AbacusRecommendationDecisionValues.Rejected => "おすすめを却下しました。リンク状態は変更していません。",
            AbacusRecommendationDecisionValues.Hold => "おすすめを保留しました。リンク状態は変更していません。",
            _ => "おすすめを未処理へ戻しました。",
        };
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphUnresolvedDocumentLists();
        RefreshLegacyGraphSearchResults();
        RefreshLegacyMatchingView();
        ScheduleLegacyGraphCheckpointSave();
    }

    private void LegacyMatchingApproveButton_Click(object sender, RoutedEventArgs e) =>
        ApplyLegacyMatchingDecision(AbacusRecommendationDecisionValues.Approved);

    private void LegacyMatchingRejectButton_Click(object sender, RoutedEventArgs e) =>
        ApplyLegacyMatchingDecision(AbacusRecommendationDecisionValues.Rejected);

    private void LegacyMatchingHoldButton_Click(object sender, RoutedEventArgs e) =>
        ApplyLegacyMatchingDecision(AbacusRecommendationDecisionValues.Hold);

    private void LegacyMatchingResetButton_Click(object sender, RoutedEventArgs e) =>
        ApplyLegacyMatchingDecision(AbacusRecommendationDecisionValues.Pending);

    private void LegacyMatchingSideApproveButton_Click(object sender, RoutedEventArgs e) =>
        ApplyLegacyMatchingDecision(AbacusRecommendationDecisionValues.Approved);

    private void LegacyMatchingSideRejectButton_Click(object sender, RoutedEventArgs e) =>
        ApplyLegacyMatchingDecision(AbacusRecommendationDecisionValues.Rejected);

    private void LegacyMatchingSideHoldButton_Click(object sender, RoutedEventArgs e) =>
        ApplyLegacyMatchingDecision(AbacusRecommendationDecisionValues.Hold);

    private void LegacyMatchingSidePreviousButton_Click(object sender, RoutedEventArgs e) =>
        MoveLegacyMatchingRecommendation(-1);

    private void LegacyMatchingSideNextButton_Click(object sender, RoutedEventArgs e) =>
        MoveLegacyMatchingRecommendation(1);

    private void MoveLegacyMatchingRecommendation(int offset)
    {
        var customer = GetLegacyGraphMatchingCustomer();
        if (customer is null)
        {
            return;
        }

        var candidates = LegacyMatchingWorkflow.OrderCandidates(
            GetLegacyMatchingActionableRecommendations(GetLegacyGraphMatchingRecommendations(customer)),
            legacyGraphMatchingCategory,
            legacyGraphRecommendationDecisions,
            includeCompleted: true);
        if (candidates.Count == 0)
        {
            return;
        }

        legacyGraphMatchingRecommendationIndex = Math.Clamp(
            legacyGraphMatchingRecommendationIndex < 0 ? 0 : legacyGraphMatchingRecommendationIndex + offset,
            0,
            candidates.Count - 1);
        legacyGraphSelectedRecommendation = candidates[legacyGraphMatchingRecommendationIndex];
        RefreshLegacyMatchingView();
    }

    private void LegacyMatchingRecommendationsTabButton_Click(object sender, RoutedEventArgs e) =>
        SetLegacyMatchingSideTab(recommendationsVisible: true);

    private void LegacyMatchingUnresolvedTabButton_Click(object sender, RoutedEventArgs e) =>
        SetLegacyMatchingSideTab(recommendationsVisible: false);

    private void SetLegacyMatchingSideTab(bool recommendationsVisible)
    {
        if (recommendationsVisible)
        {
            SetLegacyMatchingDetailsVisible(false);
        }
        LegacyMatchingRecommendationContent.Visibility = recommendationsVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        LegacyMatchingUnresolvedContent.Visibility = recommendationsVisible
            ? Visibility.Collapsed
            : Visibility.Visible;
        LegacyMatchingRecommendationsTabButton.Background = recommendationsVisible
            ? ToBrush("#2563EB")
            : ToBrush("#FFFFFF");
        LegacyMatchingRecommendationsTabButton.Foreground = recommendationsVisible
            ? ToBrush("#FFFFFF")
            : ToBrush("#1D4ED8");
        LegacyMatchingRecommendationsTabButton.BorderBrush = recommendationsVisible
            ? ToBrush("#1D4ED8")
            : ToBrush("#CBD5E1");
        LegacyMatchingRecommendationsTabButton.FontWeight = recommendationsVisible
            ? FontWeights.SemiBold
            : FontWeights.Normal;
        LegacyMatchingUnresolvedTabButton.Background = recommendationsVisible
            ? ToBrush("#FFFFFF")
            : ToBrush("#2563EB");
        LegacyMatchingUnresolvedTabButton.Foreground = recommendationsVisible
            ? ToBrush("#52647A")
            : ToBrush("#FFFFFF");
        LegacyMatchingUnresolvedTabButton.BorderBrush = recommendationsVisible
            ? ToBrush("#CBD5E1")
            : ToBrush("#1D4ED8");
        LegacyMatchingUnresolvedTabButton.FontWeight = recommendationsVisible
            ? FontWeights.Normal
            : FontWeights.SemiBold;
        if (!recommendationsVisible)
        {
            ClearLegacyMatchingRecommendationPreview();
            RefreshLegacyMatchingUnresolvedList();
        }
        else if (GetLegacyGraphMatchingCustomer() is { } customer)
        {
            RefreshLegacyMatchingRecommendationPreview(legacyGraphSelectedRecommendation);
        }
    }

    private void LegacyMatchingCustomerCategoryButton_Click(object sender, RoutedEventArgs e) =>
        SetLegacyMatchingCategory(LegacyMatchingCategoryKinds.Customer);

    private void LegacyMatchingVehicleCategoryButton_Click(object sender, RoutedEventArgs e) =>
        SetLegacyMatchingCategory(LegacyMatchingCategoryKinds.Vehicle);

    private void LegacyMatchingDocumentCategoryButton_Click(object sender, RoutedEventArgs e) =>
        SetLegacyMatchingCategory(LegacyMatchingCategoryKinds.Document);

    private void SetLegacyMatchingCategory(string category)
    {
        if (category is not (LegacyMatchingCategoryKinds.Customer or
            LegacyMatchingCategoryKinds.Vehicle or LegacyMatchingCategoryKinds.Document))
        {
            return;
        }

        legacyGraphMatchingCategory = category;
        legacyGraphMatchingRecommendationIndex = 0;
        SetLegacyMatchingSideTab(recommendationsVisible: true);
        RefreshLegacyMatchingView();
        ScheduleLegacyGraphCheckpointSave();
    }

    private void LegacyMatchingUnresolvedSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        legacyMatchingUnresolvedPageIndex = 0;
        RefreshLegacyMatchingUnresolvedList();
    }

    private void LegacyMatchingUnresolvedFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        legacyMatchingUnresolvedPageIndex = 0;
        RefreshLegacyMatchingUnresolvedList();
    }

    private void LegacyMatchingUnresolvedPreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (legacyMatchingUnresolvedPageIndex <= 0)
        {
            return;
        }

        legacyMatchingUnresolvedPageIndex--;
        RefreshLegacyMatchingUnresolvedList();
    }

    private void LegacyMatchingUnresolvedNextPageButton_Click(object sender, RoutedEventArgs e)
    {
        legacyMatchingUnresolvedPageIndex++;
        RefreshLegacyMatchingUnresolvedList();
    }

    private void RefreshLegacyMatchingUnresolvedList()
    {
        if (LegacyMatchingUnresolvedList is null || legacyExportCandidateGraphResult is null)
        {
            return;
        }

        var search = LegacyMatchingUnresolvedSearchTextBox.Text.Trim();
        LegacyMatchingUnresolvedSearchPlaceholderText.Visibility = string.IsNullOrWhiteSpace(search)
            ? Visibility.Visible
            : Visibility.Collapsed;
        var kindFilter = GetLegacyGraphSearchFilterValue(LegacyMatchingUnresolvedKindComboBox);
        var sortFilter = GetLegacyGraphSearchFilterValue(LegacyMatchingUnresolvedSortComboBox);
        var items = new List<LegacyMatchingUnresolvedItem>();
        foreach (var vehicle in GetLegacyGraphUnresolvedVehicles())
        {
            items.Add(new LegacyMatchingUnresolvedItem(
                vehicle,
                "vehicle",
                "vehicle",
                $"車両: {Fallback(vehicle.Maker)} {Fallback(vehicle.DisplayName)}",
                $"顧客: {Fallback(vehicle.CustomerName)} / 車名: {Fallback(vehicle.Maker)} {Fallback(vehicle.DisplayName)} / " +
                $"登録番号: {Fallback(vehicle.RegistrationNumber)} / 車台番号: {Fallback(vehicle.ChassisNumber)}",
                string.Join(" ", vehicle.CustomerName, vehicle.Maker, vehicle.DisplayName,
                    vehicle.RegistrationNumber, vehicle.ChassisNumber, vehicle.SourceLocation),
                Fallback(vehicle.SourceLocation),
                ToBrush("#FEF2F2"),
                ToBrush("#FCA5A5")));
        }

        foreach (var document in legacyExportCandidateGraphResult.AllDocuments.Where(IsLegacyGraphDocumentInTray))
        {
            items.Add(new LegacyMatchingUnresolvedItem(
                document,
                document.Kind == "販売書類" ? "sales" : "maintenance",
                "document",
                $"{document.Kind}: {Fallback(document.DocumentNumber)}",
                $"顧客: {Fallback(document.CustomerName)} / 車名: {Fallback(document.VehicleName)} / " +
                $"登録番号: {Fallback(document.RegistrationNumber)} / 日付: {Fallback(document.DocumentDate)} / " +
                $"出典: {Fallback(document.SourceLocation)}",
                string.Join(" ", document.CustomerName, document.VehicleName, document.DocumentNumber,
                    document.RegistrationNumber, document.DocumentDate, document.SourceFileName,
                    document.SourceRowNumber),
                $"{Fallback(document.DocumentDate)} / {Fallback(document.SourceFileName)}",
                ToBrush("#FFF7ED"),
                ToBrush("#FDBA74")));
        }

        var filteredQuery = items
            .Where(item => kindFilter == "all" || item.CategoryCode == kindFilter)
            .Where(item => string.IsNullOrWhiteSpace(search) ||
                           string.Join(" ", item.Title, item.Details, item.SearchText)
                               .Contains(search, StringComparison.OrdinalIgnoreCase));
        var filteredItems = sortFilter switch
        {
            "date" => filteredQuery
                .OrderByDescending(item => item.SortKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            "source" => filteredQuery
                .OrderBy(item => item.SortKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            _ => filteredQuery
                .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
        var pageCount = filteredItems.Length == 0
            ? 0
            : (filteredItems.Length + LegacyMatchingUnresolvedPageSize - 1) / LegacyMatchingUnresolvedPageSize;
        legacyMatchingUnresolvedPageIndex = pageCount == 0
            ? 0
            : Math.Clamp(legacyMatchingUnresolvedPageIndex, 0, pageCount - 1);
        var pageItems = filteredItems
            .Skip(legacyMatchingUnresolvedPageIndex * LegacyMatchingUnresolvedPageSize)
            .Take(LegacyMatchingUnresolvedPageSize)
            .ToArray();
        LegacyMatchingUnresolvedList.ItemsSource = pageItems;
        var first = filteredItems.Length == 0
            ? 0
            : legacyMatchingUnresolvedPageIndex * LegacyMatchingUnresolvedPageSize + 1;
        var last = filteredItems.Length == 0
            ? 0
            : first + pageItems.Length - 1;
        LegacyMatchingUnresolvedPageText.Text = filteredItems.Length == 0
            ? "0件"
            : $"{first:N0}-{last:N0} / {filteredItems.Length:N0}件";
        LegacyMatchingUnresolvedPreviousPageButton.IsEnabled = legacyMatchingUnresolvedPageIndex > 0;
        LegacyMatchingUnresolvedNextPageButton.IsEnabled = pageCount > 0 &&
            legacyMatchingUnresolvedPageIndex < pageCount - 1;
        var filterActive = !string.IsNullOrWhiteSpace(search) || kindFilter != "all";
        LegacyMatchingUnresolvedTabButton.Content = filterActive
            ? $"未確定 {filteredItems.Length:N0}/{items.Count:N0}"
            : $"未確定 {items.Count:N0}";
    }

    private void LegacyMatchingUnresolvedList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list ||
            FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not LegacyMatchingUnresolvedItem item)
        {
            return;
        }

        list.SelectedItem = item;
        legacyMatchingUnresolvedDragItem = item;
        legacyMatchingUnresolvedDragStartPoint = e.GetPosition(this);
        list.CaptureMouse();
    }

    private void LegacyMatchingUnresolvedList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list || legacyMatchingUnresolvedDragItem is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - legacyMatchingUnresolvedDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - legacyMatchingUnresolvedDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = legacyMatchingUnresolvedDragItem;
        legacyMatchingUnresolvedDragItem = null;
        if (list.IsMouseCaptured)
        {
            list.ReleaseMouseCapture();
        }

        var data = new DataObject();
        if (item.Item is AbacusLegacyExportCandidateGraphVehicle vehicle)
        {
            data.SetData(typeof(LegacyGraphUnresolvedVehicleDragPayload),
                new LegacyGraphUnresolvedVehicleDragPayload(vehicle.VehicleId));
        }
        else if (item.Item is AbacusLegacyExportCandidateGraphDocument document)
        {
            data.SetData(typeof(LegacyGraphDocumentDragPayload),
                new LegacyGraphDocumentDragPayload(document, "unresolved-tray"));
        }

        try
        {
            DragDrop.DoDragDrop(list, data, DragDropEffects.Link);
        }
        finally
        {
            if (list.IsMouseCaptured)
            {
                list.ReleaseMouseCapture();
            }
        }

        e.Handled = true;
    }

    private void LegacyMatchingUnresolvedList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && list.IsMouseCaptured)
        {
            list.ReleaseMouseCapture();
        }

        legacyMatchingUnresolvedDragItem = null;
    }

    private void LegacyMatchingPreviousButton_Click(object sender, RoutedEventArgs e) =>
        MoveLegacyGraphMatchingCustomer(-1);

    private void LegacyMatchingNextButton_Click(object sender, RoutedEventArgs e) =>
        MoveLegacyGraphMatchingCustomer(1);

    private void MoveLegacyGraphMatchingCustomer(int offset)
    {
        var customers = GetLegacyGraphMatchingCustomers();
        if (customers.Count == 0)
        {
            return;
        }

        legacyGraphMatchingCustomerIndex = Math.Clamp(
            legacyGraphMatchingCustomerIndex < 0 ? 0 : legacyGraphMatchingCustomerIndex + offset,
            0,
            customers.Count - 1);
        legacyGraphMatchingCustomerId = customers[legacyGraphMatchingCustomerIndex].CustomerId;
        legacyGraphMatchingSelectedMergeMemberCustomerId = null;
        legacyGraphSelectedItem = customers[legacyGraphMatchingCustomerIndex];
        legacyGraphSelectedRecommendation = null;
        legacyGraphMatchingCategory = LegacyMatchingCategoryKinds.Customer;
        legacyGraphMatchingRecommendationIndex = 0;
        RefreshLegacyGraphCustomerList();
        RefreshLegacyMatchingView();
        ScheduleLegacyGraphCheckpointSave();
    }

    private void LegacyGraphCustomersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LegacyGraphCustomersList.SelectedItem is LegacyGraphCustomerListEntry entry)
        {
            legacyGraphSelectedWorkGroupKey = IsLegacyGraphCustomerGroupEntry(entry)
                ? entry.GroupKey
                : null;
            var displayCustomer = entry.IsGroupHeader
                ? GetLegacyGraphDisplayCustomer(entry.Customer)
                : entry.Customer;
            legacyGraphSelectedItem = displayCustomer;
            UpdateLegacyGraphInspector(displayCustomer);
            RenderLegacyGraphCustomer(displayCustomer);
        }
        UpdateLegacyGraphCurrentCustomerSelectionText();
        RefreshLegacyMatchingMergeRemovalButton();
    }

    private void LegacyMatchingCustomerQueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLegacyGraphCurrentCustomerSelectionText();
        if (legacyGraphRefreshingMatchingCustomerQueue)
        {
            return;
        }

        if (LegacyMatchingCustomerQueueList.SelectedItem is not LegacyGraphCustomerListEntry entry)
        {
            return;
        }

        var displayCustomer = entry.IsGroupHeader
            ? GetLegacyGraphDisplayCustomer(entry.Customer)
            : entry.Customer;
        if (entry.IsMatchingFocus)
        {
            legacyGraphMatchingSelectedMergeMemberCustomerId = null;
            EnsureLegacyGraphMatchingCustomerSelection(
                displayCustomer,
                allowBaseCustomerChange: true);
            legacyGraphSelectedRecommendation = null;
            RefreshLegacyMatchingView();
            return;
        }

        if (IsLegacyGraphMatchingMergeMemberEntry(entry))
        {
            // 子顧客の詳細だけを選択し、マッチングの作業対象（基準顧客）は変更しません。
            legacyGraphMatchingSelectedMergeMemberCustomerId = entry.Customer.CustomerId;
            legacyGraphSelectedItem = entry.Customer;
            legacyGraphSelectedRecommendation = null;
            UpdateLegacyGraphInspector(entry.Customer);
            RefreshLegacyMatchingMergeRemovalButton();
            RefreshLegacyMatchingView();
            return;
        }

        if (entry.IsGroupHeader)
        {
            legacyGraphMatchingSelectedMergeMemberCustomerId = null;
        }
        legacyGraphSelectedItem = displayCustomer;
        legacyGraphSelectedRecommendation = entry.MatchingRecommendation;
        var focusCustomer = GetLegacyGraphMatchingCustomer();
        if (focusCustomer is not null)
        {
            if (entry.MatchingRecommendation is { } selectedRecommendation)
            {
                legacyGraphMatchingCategory = LegacyMatchingCategoryKinds.GetKind(selectedRecommendation);
                var categoryCandidates = LegacyMatchingWorkflow.OrderCandidates(
                    GetLegacyMatchingActionableRecommendations(GetLegacyGraphMatchingRecommendations(focusCustomer)),
                    legacyGraphMatchingCategory,
                    legacyGraphRecommendationDecisions,
                    includeCompleted: true);
                var selectedIndex = categoryCandidates
                    .Select((candidate, index) => (candidate, index))
                    .FirstOrDefault(item => string.Equals(
                        item.candidate.CandidateId,
                        selectedRecommendation.CandidateId,
                        StringComparison.OrdinalIgnoreCase))
                    .index;
                legacyGraphMatchingRecommendationIndex = selectedIndex;
            }

            RenderLegacyGraphCustomer(GetLegacyGraphDisplayCustomer(focusCustomer));
        }

        UpdateLegacyGraphInspector(displayCustomer);
        RefreshLegacyMatchingMergeRemovalButton();
        RefreshLegacyMatchingView();
    }

    private void UpdateLegacyGraphCurrentCustomerSelectionText()
    {
        if (LegacyGraphCurrentCustomerSelectionText is null)
        {
            return;
        }

        if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase) &&
            GetLegacyGraphMatchingCustomer() is { } matchingCustomer)
        {
            LegacyGraphCurrentCustomerSelectionText.Text =
                $"確認中の顧客: {GetLegacyGraphMatchingDisplayName(matchingCustomer)}";
            return;
        }

        var selectedItem = string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase)
            ? LegacyMatchingCustomerQueueList.SelectedItem
            : LegacyGraphCustomersList.SelectedItem;
        LegacyGraphCurrentCustomerSelectionText.Text = selectedItem is LegacyGraphCustomerListEntry entry
            ? $"現在の選択: {entry.DisplayName}"
            : "現在の選択: なし";
    }

    private void LegacyGraphReturnToCurrentSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            EnsureLegacyGraphMatchingCustomerSelection();
            legacyGraphSelectedRecommendation = null;
            RefreshLegacyGraphCustomerList();
            RefreshLegacyMatchingView();
            e.Handled = true;
            return;
        }

        var customer = GetLegacyGraphCustomerForCurrentSelection();
        if (customer is null)
        {
            LegacyGraphStatusText.Text = "戻る先の顧客選択を特定できません。顧客または車両・書類を選択してください。";
            LegacyGraphStatusText.Foreground = ToBrush("#805B10");
            return;
        }

        var entryId = GetLegacyGraphCustomerListEntryId(customer);
        RefreshLegacyGraphCustomerList(entryId);
        if (LegacyGraphCustomersList.SelectedItem is LegacyGraphCustomerListEntry entry)
        {
            LegacyGraphCustomersList.ScrollIntoView(entry);
        }

        var displayCustomer = LegacyGraphCustomersList.SelectedItem is LegacyGraphCustomerListEntry selectedEntry
            ? selectedEntry.IsGroupHeader
                ? GetLegacyGraphDisplayCustomer(selectedEntry.Customer)
                : selectedEntry.Customer
            : GetLegacyGraphDisplayCustomer(customer);
        legacyGraphSelectedItem = displayCustomer;
        UpdateLegacyGraphInspector(displayCustomer);
        RenderLegacyGraphCustomer(displayCustomer);
        UpdateLegacyGraphCurrentCustomerSelectionText();
        e.Handled = true;
    }

    private string GetLegacyGraphCustomerListEntryId(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var mergeKey = GetLegacyCustomerMergeKey(customer);
        var hasVisibleGroup = TryGetLegacyGraphMergeGroup(mergeKey, out var group) &&
                              group.CustomerIds.Count(customerId =>
                                  !legacyGraphTrashCustomerIds.Contains(customerId) &&
                                  FindLegacyGraphCustomerById(customerId) is not null) > 1;
        return hasVisibleGroup
            ? $"group:{mergeKey}"
            : $"customer:{GetLegacyGraphSourceCustomer(customer).CustomerId}";
    }

    private AbacusLegacyExportCandidateGraphCustomer? GetLegacyGraphCustomerForCurrentSelection()
    {
        if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphCustomer selectedCustomer)
        {
            return selectedCustomer;
        }

        if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphVehicle vehicle &&
            legacyExportCandidateGraphResult is not null)
        {
            if (legacyGraphManualVehicleCustomerLinks.TryGetValue(vehicle.VehicleId, out var linkedCustomerId))
            {
                return FindLegacyGraphCustomerById(linkedCustomerId);
            }

            return legacyExportCandidateGraphResult.Customers.FirstOrDefault(customer =>
                customer.Vehicles.Any(candidate => string.Equals(candidate.VehicleId, vehicle.VehicleId, StringComparison.Ordinal)));
        }

        if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphDocument document &&
            legacyExportCandidateGraphResult is not null)
        {
            return FindCurrentCustomerForDocument(document);
        }

        return null;
    }

    private void LegacyGraphCustomerGroupToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string groupKey ||
            string.IsNullOrWhiteSpace(groupKey))
        {
            return;
        }

        legacyGraphCustomerGroupExpanded[groupKey] =
            !legacyGraphCustomerGroupExpanded.GetValueOrDefault(groupKey);
        RefreshLegacyGraphCustomerList($"group:{groupKey}");
        ScheduleLegacyGraphCheckpointSave();
        e.Handled = true;
    }

    private void LegacyGraphCustomerList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list ||
            FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not LegacyGraphCustomerListEntry entry)
        {
            return;
        }

        list.SelectedItem = entry;
        // 親グループの展開ボタンはクリックで操作し、顧客カードだけをドラッグ対象にします。
        if (entry.IsGroupHeader)
        {
            return;
        }

        legacyGraphCustomerDragSource = GetLegacyGraphSourceCustomer(entry.Customer);
        legacyGraphCustomerDragStartPoint = e.GetPosition(this);
    }

    private void LegacyGraphCustomerList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list ||
            legacyGraphCustomerDragSource is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - legacyGraphCustomerDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - legacyGraphCustomerDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var sourceCustomer = legacyGraphCustomerDragSource;
        legacyGraphCustomerDragSource = null;
        var data = new DataObject();
        data.SetData(
            typeof(LegacyGraphCustomerDragPayload),
            new LegacyGraphCustomerDragPayload(sourceCustomer.CustomerId));
        data.SetData(
            typeof(LegacyGraphTrashCustomerDragPayload),
            new LegacyGraphTrashCustomerDragPayload(sourceCustomer.CustomerId));
        var sourceGroupKey = GetLegacyCustomerMergeKey(sourceCustomer);
        var showUngroupZone = TryGetLegacyGraphMergeGroup(sourceGroupKey, out var sourceGroup) &&
                              sourceGroup.CustomerIds.Count > 1;
        SetLegacyGraphCustomerUngroupDropZoneVisible(showUngroupZone);
        ShowLegacyGraphTrashOverlay();
        try
        {
            DragDrop.DoDragDrop(list, data, DragDropEffects.Link);
        }
        finally
        {
            SetLegacyGraphCustomerUngroupDropZoneVisible(false);
            ClearLegacyGraphCustomerDropHighlight();
            HideLegacyGraphTrashOverlay();
        }

        e.Handled = true;
    }

    private void LegacyGraphCustomerList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        legacyGraphCustomerDragSource = null;
        SetLegacyGraphCustomerUngroupDropZoneVisible(false);
        ClearLegacyGraphCustomerDropHighlight();
    }

    private void LegacyGraphCustomerList_DragOver(object sender, DragEventArgs e)
    {
        if (GetLegacyGraphUnresolvedVehicleDragPayload(e.Data) is { } vehiclePayload &&
            FindLegacyGraphVehicleById(vehiclePayload.VehicleId) is { } unresolvedVehicle &&
            (!unresolvedVehicle.HasCustomer || legacyGraphTrayVehicleIds.Contains(unresolvedVehicle.VehicleId)))
        {
            var vehicleTargetItem = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (vehicleTargetItem?.DataContext is LegacyGraphCustomerListEntry vehicleTargetEntry)
            {
                SetLegacyGraphCustomerDropHighlight(vehicleTargetItem);
                e.Effects = DragDropEffects.Link;
                e.Handled = true;
                return;
            }
        }

        if (sender is not ListBox list ||
            GetLegacyGraphCustomerDragPayload(e.Data) is not LegacyGraphCustomerDragPayload payload ||
            FindLegacyGraphCustomerById(payload.CustomerId) is not { } sourceCustomer)
        {
            ClearLegacyGraphCustomerDropHighlight();
            e.Effects = DragDropEffects.None;
            return;
        }

        AutoScrollLegacyGraphCustomerList(list, e.GetPosition(list));
        var targetItem = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetItem?.DataContext is not LegacyGraphCustomerListEntry targetEntry)
        {
            ClearLegacyGraphCustomerDropHighlight();
            e.Effects = DragDropEffects.None;
            return;
        }

        var targetCustomer = GetLegacyGraphSourceCustomer(targetEntry.Customer);
        var sourceGroup = GetLegacyCustomerMergeKey(sourceCustomer);
        var targetGroup = GetLegacyCustomerMergeKey(targetCustomer);
        if (string.Equals(sourceCustomer.CustomerId, targetCustomer.CustomerId, StringComparison.Ordinal) ||
            string.Equals(sourceGroup, targetGroup, StringComparison.Ordinal))
        {
            ClearLegacyGraphCustomerDropHighlight();
            e.Effects = DragDropEffects.None;
            return;
        }

        SetLegacyGraphCustomerDropHighlight(targetItem);
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphCustomerList_DragLeave(object sender, DragEventArgs e)
    {
        ClearLegacyGraphCustomerDropHighlight();
        ClearLegacyGraphCustomerUngroupDropHighlight();
    }

    private void LegacyGraphCustomerList_Drop(object sender, DragEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        ClearLegacyGraphCustomerDropHighlight();
        if (GetLegacyGraphUnresolvedVehicleDragPayload(e.Data) is { } vehiclePayload &&
            FindLegacyGraphVehicleById(vehiclePayload.VehicleId) is { } unresolvedVehicle &&
            (!unresolvedVehicle.HasCustomer || legacyGraphTrayVehicleIds.Contains(unresolvedVehicle.VehicleId)))
        {
            var vehicleTargetItem = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (vehicleTargetItem?.DataContext is LegacyGraphCustomerListEntry vehicleTargetEntry)
            {
                ApplyLegacyGraphManualVehicleCustomerLink(
                    unresolvedVehicle,
                    GetLegacyGraphSourceCustomer(vehicleTargetEntry.Customer));
                e.Effects = DragDropEffects.Link;
                e.Handled = true;
            }

            return;
        }

        if (sender is not ListBox ||
            GetLegacyGraphCustomerDragPayload(e.Data) is not LegacyGraphCustomerDragPayload payload ||
            FindLegacyGraphCustomerById(payload.CustomerId) is not { } sourceCustomer)
        {
            return;
        }

        var targetItem = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (targetItem?.DataContext is not LegacyGraphCustomerListEntry targetEntry)
        {
            return;
        }

        var targetCustomer = GetLegacyGraphSourceCustomer(targetEntry.Customer);
        if (string.Equals(sourceCustomer.CustomerId, targetCustomer.CustomerId, StringComparison.Ordinal) ||
            string.Equals(GetLegacyCustomerMergeKey(sourceCustomer), GetLegacyCustomerMergeKey(targetCustomer), StringComparison.Ordinal))
        {
            return;
        }

        var groupKey = AddLegacyGraphCustomerToMergeGroup(sourceCustomer, targetCustomer);
        LegacyGraphStatusText.Text =
            $"顧客 {GetLegacyGraphCustomerDisplayName(sourceCustomer)} を {GetLegacyGraphCustomerDisplayName(targetCustomer)} の統合候補へ追加しました。顧客情報をプレビューすると採用値を選択できます。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
        RefreshLegacyGraphCustomerList($"group:{groupKey}");
        var displayCustomer = GetLegacyGraphDisplayCustomer(targetCustomer);
        legacyGraphSelectedItem = displayCustomer;
        UpdateLegacyGraphInspector(displayCustomer);
        RenderLegacyGraphCustomer(displayCustomer);
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void AutoScrollLegacyGraphCustomerList(ListBox list, Point position)
    {
        var scrollViewer = FindVisualDescendant<ScrollViewer>(list);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        const double edge = 48;
        const double step = 22;
        if (position.Y < edge)
        {
            scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - step));
        }
        else if (position.Y > list.ActualHeight - edge)
        {
            scrollViewer.ScrollToVerticalOffset(Math.Min(
                scrollViewer.ScrollableHeight,
                scrollViewer.VerticalOffset + step));
        }
    }

    private void SetLegacyGraphCustomerDropHighlight(ListBoxItem targetItem)
    {
        if (ReferenceEquals(legacyGraphCustomerDragTargetItem, targetItem))
        {
            return;
        }

        ClearLegacyGraphCustomerDropHighlight();
        legacyGraphCustomerDragTargetItem = targetItem;
        targetItem.Opacity = 0.72;
        targetItem.ToolTip = "ここへドロップして統合候補を作成";
    }

    private void ClearLegacyGraphCustomerDropHighlight()
    {
        if (legacyGraphCustomerDragTargetItem is null)
        {
            return;
        }

        legacyGraphCustomerDragTargetItem.Opacity = 1;
        legacyGraphCustomerDragTargetItem.ToolTip = null;
        legacyGraphCustomerDragTargetItem = null;
    }

    private void LegacyGraphCustomerUngroupDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (GetLegacyGraphCustomerDragPayload(e.Data) is not LegacyGraphCustomerDragPayload payload ||
            FindLegacyGraphCustomerById(payload.CustomerId) is not { } sourceCustomer ||
            !TryGetLegacyGraphMergeGroup(GetLegacyCustomerMergeKey(sourceCustomer), out var group) ||
            group.CustomerIds.Count < 2)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (sender is Border zone)
        {
            if (!ReferenceEquals(legacyGraphCustomerUngroupDropHighlight, zone))
            {
                ClearLegacyGraphCustomerUngroupDropHighlight();
                legacyGraphCustomerUngroupDropHighlight = zone;
                zone.Background = ToBrush("#FDE68A");
                zone.BorderBrush = ToBrush("#B45309");
                zone.Opacity = 1;
            }
        }

        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphCustomerUngroupDropZone_DragLeave(object sender, DragEventArgs e) =>
        ClearLegacyGraphCustomerUngroupDropHighlight();

    private void LegacyGraphCustomerUngroupDropZone_Drop(object sender, DragEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        ClearLegacyGraphCustomerUngroupDropHighlight();
        if (GetLegacyGraphCustomerDragPayload(e.Data) is not LegacyGraphCustomerDragPayload payload ||
            FindLegacyGraphCustomerById(payload.CustomerId) is not { } sourceCustomer)
        {
            return;
        }

        var groupKey = GetLegacyCustomerMergeKey(sourceCustomer);
        if (!TryGetLegacyGraphMergeGroup(groupKey, out var group) || group.CustomerIds.Count < 2)
        {
            return;
        }

        RemoveLegacyGraphCustomerFromMergeGroup(sourceCustomer, groupKey);
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void ClearLegacyGraphCustomerUngroupDropHighlight()
    {
        if (legacyGraphCustomerUngroupDropHighlight is null)
        {
            return;
        }

        legacyGraphCustomerUngroupDropHighlight.Background = ToBrush("#FFF7ED");
        legacyGraphCustomerUngroupDropHighlight.BorderBrush = ToBrush("#D97706");
        legacyGraphCustomerUngroupDropHighlight.Opacity = 0.62;
        legacyGraphCustomerUngroupDropHighlight = null;
    }

    private void SetLegacyGraphCustomerUngroupDropZoneVisible(bool visible)
    {
        LegacyGraphCustomerUngroupDropColumn.Width = visible
            ? new GridLength(126)
            : new GridLength(0);
        LegacyGraphCustomerUngroupDropZone.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!visible)
        {
            ClearLegacyGraphCustomerUngroupDropHighlight();
        }
    }

    private void RemoveLegacyGraphCustomerFromMergeGroup(
        AbacusLegacyExportCandidateGraphCustomer sourceCustomer,
        string groupKey)
    {
        if (!TryGetLegacyGraphMergeGroup(groupKey, out var group) || group.CustomerIds.Count < 2)
        {
            return;
        }

        var isLogicalGroup = IsLegacyGraphLogicalCustomerGroup(groupKey);
        var wasTrackedGroup = isLogicalGroup ||
                              legacyGraphAppliedCustomerMergeKeys.Contains(groupKey);
        var wasApprovedGroup = IsLegacyGraphCustomerReviewApprovedForKey(groupKey);
        group.CustomerIds.Remove(sourceCustomer.CustomerId);
        legacyGraphLogicalCustomerMergeGroupByCustomerId.Remove(sourceCustomer.CustomerId);
        legacyGraphCustomerMergeGroupByCustomerId.Remove(sourceCustomer.CustomerId);
        InvalidateLegacyGraphMergeGroupState(
            groupKey,
            group.CustomerIds.Append(sourceCustomer.CustomerId));
        if (group.CustomerIds.Count < 2)
        {
            RehomeLegacyGraphManualDocumentCustomerLinks(
                groupKey,
                group.CustomerIds.Append(sourceCustomer.CustomerId).ToArray(),
                group.CustomerIds.SingleOrDefault());
            foreach (var customerId in group.CustomerIds)
            {
                legacyGraphLogicalCustomerMergeGroupByCustomerId.Remove(customerId);
                legacyGraphCustomerMergeGroupByCustomerId.Remove(customerId);
            }
            legacyGraphCustomerMergeGroups.Remove(groupKey);
            legacyGraphCustomerGroupExpanded.Remove(groupKey);
            MoveLegacyGraphConfirmedGroupReviewStateToRemainingCustomer(
                groupKey,
                wasTrackedGroup,
                wasApprovedGroup,
                group.CustomerIds);
        }
        else
        {
            foreach (var customerId in group.CustomerIds)
            {
                if (isLogicalGroup)
                {
                    legacyGraphLogicalCustomerMergeGroupByCustomerId[customerId] = groupKey;
                    legacyGraphCustomerMergeGroupByCustomerId.Remove(customerId);
                }
                else
                {
                    legacyGraphCustomerMergeGroupByCustomerId[customerId] = groupKey;
                    legacyGraphLogicalCustomerMergeGroupByCustomerId.Remove(customerId);
                }
            }
        }
        RemoveLegacyGraphCustomerFromOtherMergeGroups(sourceCustomer.CustomerId, groupKey);

        var affectedCustomerIds = new HashSet<string>(StringComparer.Ordinal)
        {
            sourceCustomer.CustomerId,
        };
        foreach (var customerId in group.CustomerIds)
        {
            affectedCustomerIds.Add(customerId);
        }
        ResetLegacyGraphApprovedCustomerMergeRecommendations(affectedCustomerIds);
        RebuildLegacyGraphRecommendationCandidatesForCustomers(affectedCustomerIds);

        var remaining = group.CustomerIds
            .Select(FindLegacyGraphCustomerById)
            .Where(customer => customer is not null)
            .Cast<AbacusLegacyExportCandidateGraphCustomer>()
            .FirstOrDefault();
        if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            // 解除した顧客をすぐに巡回対象へ戻し、操作後に見落とさないようにします。
            EnsureLegacyGraphMatchingCustomerSelection();
            legacyGraphSelectedItem = sourceCustomer;
            RefreshLegacyGraphCustomerList();
            RefreshLegacyMatchingView();
        }
        else
        {
            legacyGraphSelectedWorkGroupKey = group.CustomerIds.Count > 1 ? groupKey : null;
            RefreshLegacyGraphCustomerList(remaining is null
                ? null
                : group.CustomerIds.Count > 1
                    ? $"group:{groupKey}"
                    : $"customer:{remaining.CustomerId}");
            if (remaining is not null)
            {
                var display = GetLegacyGraphDisplayCustomer(remaining);
                legacyGraphSelectedItem = display;
                UpdateLegacyGraphInspector(display);
                RenderLegacyGraphCustomer(display);
            }
        }

        LegacyGraphStatusText.Text =
            $"{GetLegacyGraphCustomerDisplayName(sourceCustomer)} を統合から外しました。独立した顧客としてマッチング順へ戻しました。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private void MoveLegacyGraphConfirmedGroupReviewStateToRemainingCustomer(
        string groupKey,
        bool wasTrackedGroup,
        bool wasApprovedGroup,
        IReadOnlyCollection<string> remainingCustomerIds)
    {
        if (!wasTrackedGroup || remainingCustomerIds.Count != 1)
        {
            return;
        }

        LegacyGraphCustomerReviewStateTransition.MoveGroupToStandaloneCustomer(
            legacyGraphCustomerReviewStates,
            legacyGraphCustomerApprovalStates,
            groupKey,
            remainingCustomerIds.Single(),
            wasApprovedGroup);
    }

    private static LegacyGraphCustomerDragPayload? GetLegacyGraphCustomerDragPayload(IDataObject data) =>
        data.GetData(typeof(LegacyGraphCustomerDragPayload)) as LegacyGraphCustomerDragPayload;

    private static LegacyGraphTrashCustomerDragPayload? GetLegacyGraphTrashCustomerDragPayload(IDataObject data) =>
        data.GetData(typeof(LegacyGraphTrashCustomerDragPayload)) as LegacyGraphTrashCustomerDragPayload;

    private void LegacyGraphUnresolvedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is null)
        {
            return;
        }

        // 未確定トレイは3つのListBoxに分かれているため、別ブロックを選んだときに
        // 前のListBoxの選択を残さず、画面全体で1項目だけを選択中にする。
        foreach (var otherList in new[]
                 {
                     LegacyGraphUnresolvedVehicleList,
                     LegacyGraphUnresolvedSalesList,
                     LegacyGraphUnresolvedMaintenanceList,
                 })
        {
            if (!ReferenceEquals(otherList, list))
            {
                otherList.SelectedItem = null;
            }
        }

        if (list.SelectedItem is AbacusLegacyExportCandidateGraphDocument document)
        {
            legacyGraphSelectedItem = document;
            UpdateLegacyGraphInspector(document);
        }
        else if (list.SelectedItem is AbacusLegacyExportCandidateGraphVehicle vehicle)
        {
            legacyGraphSelectedItem = vehicle;
            UpdateLegacyGraphInspector(vehicle);
        }
    }

    private void LegacyMatchingOpenCustomerSearchButton_Click(object sender, RoutedEventArgs e)
    {
        LegacyGraphSearchExpander.IsExpanded = true;
        LegacyGraphSearchTextBox.Focus();
        LegacyGraphSearchTextBox.SelectAll();
    }

    private void LegacyMatchingRemoveCustomerFromMergeButton_Click(object sender, RoutedEventArgs e)
    {
        legacyGraphSelectedItem = string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase)
            ? legacyGraphSelectedItem as AbacusLegacyExportCandidateGraphCustomer ??
              GetLegacyGraphMatchingCustomer()
            : GetLegacyGraphListSelectedDisplayCustomer();

        LegacyGraphRemoveCustomerFromMergeButton_Click(sender, e);
        RefreshLegacyMatchingMergeRemovalButton();
    }

    private void LegacyGraphUnresolvedList_RequestBringIntoView(
        object sender,
        RequestBringIntoViewEventArgs e)
    {
        // クリックによる選択時だけ、ListBoxItemの自動スクロールを抑止する。
        // キーボード操作や明示的なスクロールは通常どおり動作させる。
        if (e.TargetObject is ListBoxItem && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            e.Handled = true;
        }
    }

    private async void LegacyGraphSaveWorkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        try
        {
            await SaveLegacyGraphCheckpointAsync("manual");
            LegacyGraphWorkStatusText.Text =
                $"作業状態を保存しました: {legacyGraphWorkCheckpointStore.GetCheckpointPath(unifiedImportOutputSession!.WorkCheckpointsPath)}";
            LegacyGraphWorkStatusText.Foreground = ToBrush("#17643A");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            LegacyGraphWorkStatusText.Text = $"作業状態を保存できません: {exception.Message}";
            LegacyGraphWorkStatusText.Foreground = ToBrush("#A61B1B");
        }
        catch (Exception exception)
        {
            LegacyGraphWorkStatusText.Text = $"作業状態の保存中に予期しないエラーが発生しました: {exception.Message}";
            LegacyGraphWorkStatusText.Foreground = ToBrush("#A61B1B");
        }
    }

    private async void LegacyGraphOpenWorkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "保存済みのABACUS作業フォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await ResumeLegacyGraphWorkAsync(dialog.FolderName);
    }

    private async Task ResumeLegacyGraphWorkAsync(string rootPath)
    {
        var previousGraphState = CaptureLegacyGraphRuntimeState();
        var previousSession = unifiedImportOutputSession;
        var previousImageMapping = fp5VehicleImageMapping;
        var previousSourcePath = UnifiedImportFolderPathTextBox.Text;
        var previousOutputParentPath = UnifiedImportOutputParentPathTextBox.Text;
        var previousLegacyExportPath = LegacyExportPathTextBox.Text;
        var previousCandidatePackagePath = LegacyExportPackagePathTextBox.Text;
        var previousLegacyExportSubsetActive = legacyExportSubsetActive;
        var hadPendingCheckpointSave = legacyGraphCheckpointSaveScheduled;
        var resumeFailed = false;
        legacyGraphResumeInProgress = true;
        LegacyGraphOpenWorkButton.IsEnabled = false;
        LegacyGraphSaveWorkButton.IsEnabled = false;
        LegacyGraphWorkStatusText.Text = "作業フォルダー、原本指紋、候補マニフェスト、画像を検証しています…";
        LegacyGraphWorkStatusText.Foreground = ToBrush("#52647A");
        try
        {
            var session = await importOutputPackageStore.OpenAsync(rootPath);
            var checkpoint = await legacyGraphWorkCheckpointStore.ReadAsync(session.WorkCheckpointsPath);
            if (!string.Equals(checkpoint.PackageId, session.PackageId, StringComparison.Ordinal) ||
                !string.Equals(checkpoint.SourceFingerprint, session.SourceFingerprint, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFullPath(checkpoint.SourcePath),
                    Path.GetFullPath(session.SourcePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "作業チェックポイントが選択したABACUS作業フォルダーと一致しません。");
            }

            var candidatePackagePath = ResolveLegacyGraphCheckpointPath(
                session.WorkIntermediatePath,
                checkpoint.CandidatePackagePath,
                "候補パッケージ");
            var candidateManifestPath = Path.Combine(candidatePackagePath, "manifest.json");
            var candidateManifestSha256 = await CalculateLegacyGraphSha256Async(candidateManifestPath);
            if (!string.Equals(
                    candidateManifestSha256,
                    checkpoint.CandidateManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "候補パッケージのマニフェストが作業保存後に変更されています。"
                    + "既存チェックポイントは適用せず、再解析してください。");
            }

            if (checkpoint.ImageMapping is not null)
            {
                await ValidateLegacyGraphCheckpointImageMappingAsync(
                    checkpoint.ImageMapping,
                    session.WorkPath,
                    session.WorkAbacusCopyPath);
            }

            unifiedImportOutputSession = session;
            UnifiedImportFolderPathTextBox.Text = session.SourcePath;
            UnifiedImportOutputParentPathTextBox.Text =
                Path.GetDirectoryName(session.RootPath) ?? "";
            LegacyExportPathTextBox.Text = string.IsNullOrWhiteSpace(checkpoint.VehicleExportPath)
                ? session.SourcePath
                : checkpoint.VehicleExportPath;
            legacyExportSubsetActive = checkpoint.LegacyExportSubsetActive;
            fp5VehicleImageMapping = CreateImageMappingFromCheckpoint(checkpoint.ImageMapping);
            UnifiedImportImageMappingStatusText.Text = fp5VehicleImageMapping?.IsFullyMatched == true
                ? "保存済みのGate 14画像対応付けを検証して再利用しています。"
                : "保存済みチェックポイントにGate 14画像対応付けがありません。";
            UnifiedImportImageMappingStatusText.Foreground =
                ToBrush(fp5VehicleImageMapping?.IsFullyMatched == true ? "#17643A" : "#805B10");
            UnifiedImportImageMappingSummaryText.Text = fp5VehicleImageMapping is null
                ? ""
                : $"保存済み対応付け: 車両レコード {fp5VehicleImageMapping.InternalVehicleRecordCount:N0}件 / " +
                  $"画像 {fp5VehicleImageMapping.MatchedImageCount:N0}件 / " +
                  $"画像なし {fp5VehicleImageMapping.NoImageCount:N0}件";
            LegacyExportPackagePathTextBox.Text = candidatePackagePath;
            var resumed = await ReadLegacyExportPackageAsync(candidatePackagePath, automatic: false, checkpoint);
            if (!resumed)
            {
                var detail = LegacyGraphStatusText.Text.Trim();
                throw new InvalidDataException(
                    string.IsNullOrWhiteSpace(detail)
                        ? "候補パッケージの再検証またはグラフ復元に失敗しました。"
                        : detail);
            }

            if (legacyExportCandidateGraphResult is not null)
            {
                UnifiedImportStatusText.Text = "作業状態を再開しました。前回の紐づけ・トレイ・ごみ箱の状態を確認してください。";
                UnifiedImportStatusText.Foreground = ToBrush("#17643A");
                UnifiedImportSummaryText.Text =
                    $"作業フォルダー: {session.RootPath}\n候補パッケージ: {candidatePackagePath}";
                ShowUnifiedImportGraph();
                LegacyPreparationExpander.IsExpanded =
                    string.Equals(checkpoint.UiMode, "preparation", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            // 読み込みに失敗した場合は、再開途中に候補グラフをリセットしていても、
            // 現在のグラフ・チェックポイントを部分適用しません。
            resumeFailed = true;
            RestoreLegacyGraphRuntimeState(previousGraphState);
            legacyGraphCheckpointSaveScheduled = false;
            legacyGraphCheckpointSaveDeferred = false;
            unifiedImportOutputSession = previousSession;
            fp5VehicleImageMapping = previousImageMapping;
            UnifiedImportFolderPathTextBox.Text = previousSourcePath;
            UnifiedImportOutputParentPathTextBox.Text = previousOutputParentPath;
            LegacyExportPathTextBox.Text = previousLegacyExportPath;
            LegacyExportPackagePathTextBox.Text = previousCandidatePackagePath;
            legacyExportSubsetActive = previousLegacyExportSubsetActive;
            LegacyGraphWorkStatusText.Text = $"作業を再開できません: {exception.Message}";
            LegacyGraphWorkStatusText.Foreground = ToBrush("#A61B1B");
            UnifiedImportStatusText.Text = "作業フォルダーの再開に失敗しました。現在の状態は変更していません。";
            UnifiedImportStatusText.Foreground = ToBrush("#A61B1B");
        }
        catch (Exception exception)
        {
            resumeFailed = true;
            RestoreLegacyGraphRuntimeState(previousGraphState);
            legacyGraphCheckpointSaveScheduled = false;
            legacyGraphCheckpointSaveDeferred = false;
            unifiedImportOutputSession = previousSession;
            fp5VehicleImageMapping = previousImageMapping;
            UnifiedImportFolderPathTextBox.Text = previousSourcePath;
            UnifiedImportOutputParentPathTextBox.Text = previousOutputParentPath;
            LegacyExportPathTextBox.Text = previousLegacyExportPath;
            LegacyExportPackagePathTextBox.Text = previousCandidatePackagePath;
            legacyExportSubsetActive = previousLegacyExportSubsetActive;
            LegacyGraphWorkStatusText.Text = $"作業再開中に予期しないエラーが発生しました: {exception.Message}";
            LegacyGraphWorkStatusText.Foreground = ToBrush("#A61B1B");
            UnifiedImportStatusText.Text = "作業フォルダーの再開に失敗しました。現在の状態は変更していません。";
            UnifiedImportStatusText.Foreground = ToBrush("#A61B1B");
        }
        finally
        {
            legacyGraphResumeInProgress = false;
            UpdateLegacyGraphImportConfirmationButton();
            LegacyGraphOpenWorkButton.IsEnabled = true;
            if (LegacyGraphCheckpointSaveState.ShouldRescheduleAfterResumeFailure(
                    resumeFailed,
                    hadPendingCheckpointSave))
            {
                // 再開失敗時は、失敗した読み込み処理の一時フラグを復元せず、
                // 再開前に実際に予定されていた保存だけを現在状態へ再登録します。
                legacyGraphCheckpointSaveDeferred = true;
                ScheduleDeferredLegacyGraphCheckpointSave();
            }
        }
    }

    private LegacyGraphRuntimeStateSnapshot CaptureLegacyGraphRuntimeState()
    {
        return new LegacyGraphRuntimeStateSnapshot
        {
            CandidateGraph = legacyExportCandidateGraphResult,
            RecommendationCandidates = legacyGraphRecommendationCandidates,
            RecommendationDecisions = new Dictionary<string, string>(
                legacyGraphRecommendationDecisions,
                StringComparer.OrdinalIgnoreCase),
            RecommendationStates = new Dictionary<string, LegacyGraphRecommendationState>(
                legacyGraphRecommendationStates,
                StringComparer.OrdinalIgnoreCase),
            ManualDocumentLinks = new Dictionary<string, string>(
                legacyGraphManualDocumentLinks,
                StringComparer.OrdinalIgnoreCase),
            ManualVehicleCustomerLinks = new Dictionary<string, string>(
                legacyGraphManualVehicleCustomerLinks,
                StringComparer.OrdinalIgnoreCase),
            ManualDocumentCustomerLinks = new Dictionary<string, string>(
                legacyGraphManualDocumentCustomerLinks,
                StringComparer.OrdinalIgnoreCase),
            DocumentLinkMethods = new Dictionary<string, string>(
                legacyGraphDocumentLinkMethods,
                StringComparer.OrdinalIgnoreCase),
            DocumentLinkReasons = new Dictionary<string, string>(
                legacyGraphDocumentLinkReasons,
                StringComparer.OrdinalIgnoreCase),
            UnconnectedDocumentKeys = new HashSet<string>(
                legacyGraphUnconnectedDocumentKeys,
                StringComparer.OrdinalIgnoreCase),
            TrayDocumentKeys = new HashSet<string>(
                legacyGraphTrayDocumentKeys,
                StringComparer.OrdinalIgnoreCase),
            ExcludedDocumentKeys = new HashSet<string>(
                legacyGraphExcludedDocumentKeys,
                StringComparer.OrdinalIgnoreCase),
            TrayVehicleIds = new HashSet<string>(
                legacyGraphTrayVehicleIds,
                StringComparer.OrdinalIgnoreCase),
            TrashCustomerIds = new HashSet<string>(
                legacyGraphTrashCustomerIds,
                StringComparer.OrdinalIgnoreCase),
            TrashVehicleIds = new HashSet<string>(
                legacyGraphTrashVehicleIds,
                StringComparer.OrdinalIgnoreCase),
            TrashDocumentKeys = new HashSet<string>(
                legacyGraphTrashDocumentKeys,
                StringComparer.OrdinalIgnoreCase),
            ImportConfirmed = legacyGraphImportConfirmed,
            CustomerMergeDrafts = new Dictionary<string, LegacyGraphCustomerMergeDraft>(
                legacyGraphCustomerMergeDrafts,
                StringComparer.Ordinal),
            AppliedCustomerMergeKeys = new HashSet<string>(
                legacyGraphAppliedCustomerMergeKeys,
                StringComparer.Ordinal),
            VirtualCustomerMergeKeys = new Dictionary<string, string>(
                legacyGraphVirtualCustomerMergeKeys,
                StringComparer.Ordinal),
            CustomerNameOverrides = new Dictionary<string, string>(
                legacyGraphCustomerNameOverrides,
                StringComparer.Ordinal),
            CustomerGroupExpanded = new Dictionary<string, bool>(
                legacyGraphCustomerGroupExpanded,
                StringComparer.Ordinal),
            CustomerMergeGroups = legacyGraphCustomerMergeGroups.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal),
            CustomerMergeGroupByCustomerId = new Dictionary<string, string>(
                legacyGraphCustomerMergeGroupByCustomerId,
                StringComparer.Ordinal),
            LogicalCustomerMergeGroupByCustomerId = new Dictionary<string, string>(
                legacyGraphLogicalCustomerMergeGroupByCustomerId,
                StringComparer.Ordinal),
            MatchingManualCustomerCandidateTargets = legacyGraphMatchingManualCustomerCandidateTargets.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<string>)new HashSet<string>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            CustomerApprovalStates = new Dictionary<string, bool>(
                legacyGraphCustomerApprovalStates,
                StringComparer.Ordinal),
            CustomerReviewStates = new Dictionary<string, string>(
                legacyGraphCustomerReviewStates,
                StringComparer.Ordinal),
            SelectedItem = legacyGraphSelectedItem,
            SelectedWorkGroupKey = legacyGraphSelectedWorkGroupKey,
            UiMode = legacyGraphUiMode,
            MatchingCustomerId = legacyGraphMatchingCustomerId,
            MatchingCustomerIndex = legacyGraphMatchingCustomerIndex,
            MatchingCategory = legacyGraphMatchingCategory,
            MatchingRecommendationIndex = legacyGraphMatchingRecommendationIndex,
            MatchingUnresolvedPageIndex = legacyMatchingUnresolvedPageIndex,
            MatchingDetailsVisible = legacyMatchingDetailsVisible,
            MatchingChanges = legacyGraphMatchingChanges.ToList(),
            SelectedRecommendation = legacyGraphSelectedRecommendation,
            FinalPackageBusy = legacyGraphFinalPackageBusy,
            FinalPackageHasError = legacyGraphFinalPackageHasError,
            FinalPackagePath = legacyGraphFinalPackagePath,
            PreparationExpanded = LegacyPreparationExpander.IsExpanded,
        };
    }

    private void RestoreLegacyGraphRuntimeState(LegacyGraphRuntimeStateSnapshot snapshot)
    {
        legacyExportCandidateGraphResult = snapshot.CandidateGraph;
        legacyGraphRecommendationCandidates = snapshot.RecommendationCandidates;
        RestoreDictionary(legacyGraphRecommendationDecisions, snapshot.RecommendationDecisions);
        RestoreDictionary(legacyGraphRecommendationStates, snapshot.RecommendationStates);
        RestoreDictionary(legacyGraphManualDocumentLinks, snapshot.ManualDocumentLinks);
        RestoreDictionary(legacyGraphManualVehicleCustomerLinks, snapshot.ManualVehicleCustomerLinks);
        RestoreDictionary(legacyGraphManualDocumentCustomerLinks, snapshot.ManualDocumentCustomerLinks);
        RestoreDictionary(legacyGraphDocumentLinkMethods, snapshot.DocumentLinkMethods);
        RestoreDictionary(legacyGraphDocumentLinkReasons, snapshot.DocumentLinkReasons);
        RestoreSet(legacyGraphUnconnectedDocumentKeys, snapshot.UnconnectedDocumentKeys);
        RestoreSet(legacyGraphTrayDocumentKeys, snapshot.TrayDocumentKeys);
        RestoreSet(legacyGraphExcludedDocumentKeys, snapshot.ExcludedDocumentKeys);
        RestoreSet(legacyGraphTrayVehicleIds, snapshot.TrayVehicleIds);
        RestoreSet(legacyGraphTrashCustomerIds, snapshot.TrashCustomerIds);
        RestoreSet(legacyGraphTrashVehicleIds, snapshot.TrashVehicleIds);
        RestoreSet(legacyGraphTrashDocumentKeys, snapshot.TrashDocumentKeys);
        legacyGraphImportConfirmed = snapshot.ImportConfirmed;
        RestoreDictionary(legacyGraphCustomerMergeDrafts, snapshot.CustomerMergeDrafts);
        RestoreSet(legacyGraphAppliedCustomerMergeKeys, snapshot.AppliedCustomerMergeKeys);
        RestoreDictionary(legacyGraphVirtualCustomerMergeKeys, snapshot.VirtualCustomerMergeKeys);
        RestoreDictionary(legacyGraphCustomerNameOverrides, snapshot.CustomerNameOverrides);
        RestoreDictionary(legacyGraphCustomerGroupExpanded, snapshot.CustomerGroupExpanded);
        legacyGraphCustomerMergeGroups.Clear();
        foreach (var pair in snapshot.CustomerMergeGroups)
        {
            legacyGraphCustomerMergeGroups[pair.Key] = pair.Value.Clone();
        }

        RestoreDictionary(legacyGraphCustomerMergeGroupByCustomerId, snapshot.CustomerMergeGroupByCustomerId);
        RestoreDictionary(
            legacyGraphLogicalCustomerMergeGroupByCustomerId,
            snapshot.LogicalCustomerMergeGroupByCustomerId);
        legacyGraphMatchingManualCustomerCandidateTargets.Clear();
        foreach (var pair in snapshot.MatchingManualCustomerCandidateTargets)
        {
            legacyGraphMatchingManualCustomerCandidateTargets[pair.Key] =
                new HashSet<string>(pair.Value, StringComparer.Ordinal);
        }

        RestoreDictionary(legacyGraphCustomerApprovalStates, snapshot.CustomerApprovalStates);
        RestoreDictionary(legacyGraphCustomerReviewStates, snapshot.CustomerReviewStates);
        legacyGraphSelectedItem = snapshot.SelectedItem;
        legacyGraphSelectedWorkGroupKey = snapshot.SelectedWorkGroupKey;
        legacyGraphUiMode = snapshot.UiMode;
        legacyGraphMatchingCustomerId = snapshot.MatchingCustomerId;
        legacyGraphMatchingCustomerIndex = snapshot.MatchingCustomerIndex;
        legacyGraphMatchingCategory = snapshot.MatchingCategory;
        legacyGraphMatchingRecommendationIndex = snapshot.MatchingRecommendationIndex;
        legacyMatchingUnresolvedPageIndex = snapshot.MatchingUnresolvedPageIndex;
        legacyMatchingDetailsVisible = snapshot.MatchingDetailsVisible;
        legacyGraphMatchingChanges.Clear();
        legacyGraphMatchingChanges.AddRange(snapshot.MatchingChanges);
        legacyGraphSelectedRecommendation = snapshot.SelectedRecommendation;
        legacyGraphFinalPackageBusy = snapshot.FinalPackageBusy;
        legacyGraphFinalPackageHasError = snapshot.FinalPackageHasError;
        legacyGraphFinalPackagePath = snapshot.FinalPackagePath;

        if (legacyExportCandidateGraphResult is null)
        {
            return;
        }

        // 新しいグラフの表示要素を破棄し、復元したドメイン状態からUIを再投影します。
        legacyGraphEdges.Clear();
        LegacyGraphCanvas.Children.Clear();
        LegacyGraphEdgesCanvas.Children.Clear();
        SetLegacyGraphUiMode(snapshot.UiMode, scheduleCheckpoint: false);
        LegacyPreparationExpander.IsExpanded = snapshot.PreparationExpanded;
        RefreshLegacyGraphCustomerList();
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphUnresolvedDocumentLists();
        RefreshLegacyGraphTrashLists();
        UpdateLegacyGraphImportConfirmationButton();
        RefreshLegacyMatchingChanges();
        RefreshLegacyMatchingMergeRemovalButton();
        if (string.Equals(snapshot.UiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            RefreshLegacyMatchingView();
            SetLegacyMatchingDetailsVisible(snapshot.MatchingDetailsVisible);
        }
        else
        {
            switch (legacyGraphSelectedItem)
            {
                case AbacusLegacyExportCandidateGraphCustomer customer:
                    var displayCustomer = GetLegacyGraphDisplayCustomer(customer);
                    legacyGraphSelectedItem = displayCustomer;
                    UpdateLegacyGraphInspector(displayCustomer);
                    RenderLegacyGraphCustomer(displayCustomer);
                    break;
                case AbacusLegacyExportCandidateGraphVehicle vehicle:
                    UpdateLegacyGraphInspector(vehicle);
                    break;
                case AbacusLegacyExportCandidateGraphDocument document:
                    UpdateLegacyGraphInspector(document);
                    break;
            }
        }
    }

    private LegacyGraphDomainStateSnapshot CaptureLegacyGraphDomainState()
    {
        return new LegacyGraphDomainStateSnapshot
        {
            CandidateGraph = legacyExportCandidateGraphResult,
            RecommendationCandidates = legacyGraphRecommendationCandidates,
            RecommendationDecisions = new Dictionary<string, string>(
                legacyGraphRecommendationDecisions,
                StringComparer.OrdinalIgnoreCase),
            RecommendationStates = new Dictionary<string, LegacyGraphRecommendationState>(
                legacyGraphRecommendationStates,
                StringComparer.OrdinalIgnoreCase),
            ManualDocumentLinks = new Dictionary<string, string>(
                legacyGraphManualDocumentLinks,
                StringComparer.OrdinalIgnoreCase),
            ManualVehicleCustomerLinks = new Dictionary<string, string>(
                legacyGraphManualVehicleCustomerLinks,
                StringComparer.OrdinalIgnoreCase),
            ManualDocumentCustomerLinks = new Dictionary<string, string>(
                legacyGraphManualDocumentCustomerLinks,
                StringComparer.OrdinalIgnoreCase),
            DocumentLinkMethods = new Dictionary<string, string>(
                legacyGraphDocumentLinkMethods,
                StringComparer.OrdinalIgnoreCase),
            DocumentLinkReasons = new Dictionary<string, string>(
                legacyGraphDocumentLinkReasons,
                StringComparer.OrdinalIgnoreCase),
            UnconnectedDocumentKeys = new HashSet<string>(
                legacyGraphUnconnectedDocumentKeys,
                StringComparer.OrdinalIgnoreCase),
            TrayDocumentKeys = new HashSet<string>(
                legacyGraphTrayDocumentKeys,
                StringComparer.OrdinalIgnoreCase),
            ExcludedDocumentKeys = new HashSet<string>(
                legacyGraphExcludedDocumentKeys,
                StringComparer.OrdinalIgnoreCase),
            TrayVehicleIds = new HashSet<string>(
                legacyGraphTrayVehicleIds,
                StringComparer.OrdinalIgnoreCase),
            TrashCustomerIds = new HashSet<string>(
                legacyGraphTrashCustomerIds,
                StringComparer.OrdinalIgnoreCase),
            TrashVehicleIds = new HashSet<string>(
                legacyGraphTrashVehicleIds,
                StringComparer.OrdinalIgnoreCase),
            TrashDocumentKeys = new HashSet<string>(
                legacyGraphTrashDocumentKeys,
                StringComparer.OrdinalIgnoreCase),
            ImportConfirmed = legacyGraphImportConfirmed,
            CustomerMergeDrafts = new Dictionary<string, LegacyGraphCustomerMergeDraft>(
                legacyGraphCustomerMergeDrafts,
                StringComparer.Ordinal),
            AppliedCustomerMergeKeys = new HashSet<string>(
                legacyGraphAppliedCustomerMergeKeys,
                StringComparer.Ordinal),
            VirtualCustomerMergeKeys = new Dictionary<string, string>(
                legacyGraphVirtualCustomerMergeKeys,
                StringComparer.Ordinal),
            CustomerNameOverrides = new Dictionary<string, string>(
                legacyGraphCustomerNameOverrides,
                StringComparer.Ordinal),
            CustomerMergeGroups = legacyGraphCustomerMergeGroups.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal),
            CustomerMergeGroupByCustomerId = new Dictionary<string, string>(
                legacyGraphCustomerMergeGroupByCustomerId,
                StringComparer.Ordinal),
            LogicalCustomerMergeGroupByCustomerId = new Dictionary<string, string>(
                legacyGraphLogicalCustomerMergeGroupByCustomerId,
                StringComparer.Ordinal),
            MatchingManualCustomerCandidateTargets = legacyGraphMatchingManualCustomerCandidateTargets.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<string>)new HashSet<string>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            CustomerApprovalStates = new Dictionary<string, bool>(
                legacyGraphCustomerApprovalStates,
                StringComparer.Ordinal),
            CustomerReviewStates = new Dictionary<string, string>(
                legacyGraphCustomerReviewStates,
                StringComparer.Ordinal),
            MatchingChanges = legacyGraphMatchingChanges.ToList(),
        };
    }

    private void RestoreLegacyGraphDomainState(LegacyGraphDomainStateSnapshot snapshot)
    {
        legacyExportCandidateGraphResult = snapshot.CandidateGraph;
        legacyGraphRecommendationCandidates = snapshot.RecommendationCandidates;
        RestoreDictionary(legacyGraphRecommendationDecisions, snapshot.RecommendationDecisions);
        RestoreDictionary(legacyGraphRecommendationStates, snapshot.RecommendationStates);
        RestoreDictionary(legacyGraphManualDocumentLinks, snapshot.ManualDocumentLinks);
        RestoreDictionary(legacyGraphManualVehicleCustomerLinks, snapshot.ManualVehicleCustomerLinks);
        RestoreDictionary(legacyGraphManualDocumentCustomerLinks, snapshot.ManualDocumentCustomerLinks);
        RestoreDictionary(legacyGraphDocumentLinkMethods, snapshot.DocumentLinkMethods);
        RestoreDictionary(legacyGraphDocumentLinkReasons, snapshot.DocumentLinkReasons);
        RestoreSet(legacyGraphUnconnectedDocumentKeys, snapshot.UnconnectedDocumentKeys);
        RestoreSet(legacyGraphTrayDocumentKeys, snapshot.TrayDocumentKeys);
        RestoreSet(legacyGraphExcludedDocumentKeys, snapshot.ExcludedDocumentKeys);
        RestoreSet(legacyGraphTrayVehicleIds, snapshot.TrayVehicleIds);
        RestoreSet(legacyGraphTrashCustomerIds, snapshot.TrashCustomerIds);
        RestoreSet(legacyGraphTrashVehicleIds, snapshot.TrashVehicleIds);
        RestoreSet(legacyGraphTrashDocumentKeys, snapshot.TrashDocumentKeys);
        legacyGraphImportConfirmed = snapshot.ImportConfirmed;
        RestoreDictionary(legacyGraphCustomerMergeDrafts, snapshot.CustomerMergeDrafts);
        RestoreSet(legacyGraphAppliedCustomerMergeKeys, snapshot.AppliedCustomerMergeKeys);
        RestoreDictionary(legacyGraphVirtualCustomerMergeKeys, snapshot.VirtualCustomerMergeKeys);
        RestoreDictionary(legacyGraphCustomerNameOverrides, snapshot.CustomerNameOverrides);
        legacyGraphCustomerMergeGroups.Clear();
        foreach (var pair in snapshot.CustomerMergeGroups)
        {
            legacyGraphCustomerMergeGroups[pair.Key] = pair.Value.Clone();
        }

        RestoreDictionary(legacyGraphCustomerMergeGroupByCustomerId, snapshot.CustomerMergeGroupByCustomerId);
        RestoreDictionary(
            legacyGraphLogicalCustomerMergeGroupByCustomerId,
            snapshot.LogicalCustomerMergeGroupByCustomerId);
        legacyGraphMatchingManualCustomerCandidateTargets.Clear();
        foreach (var pair in snapshot.MatchingManualCustomerCandidateTargets)
        {
            legacyGraphMatchingManualCustomerCandidateTargets[pair.Key] =
                new HashSet<string>(pair.Value, StringComparer.Ordinal);
        }

        RestoreDictionary(legacyGraphCustomerApprovalStates, snapshot.CustomerApprovalStates);
        RestoreDictionary(legacyGraphCustomerReviewStates, snapshot.CustomerReviewStates);
        legacyGraphMatchingChanges.Clear();
        legacyGraphMatchingChanges.AddRange(snapshot.MatchingChanges);
    }

    private static void RestoreDictionary<TValue>(
        IDictionary<string, TValue> destination,
        IReadOnlyDictionary<string, TValue> source)
    {
        destination.Clear();
        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value;
        }
    }

    private async Task SaveLegacyGraphCheckpointAsync(string reason)
    {
        if (!CanStartLegacyGraphCheckpointSave())
        {
            legacyGraphCheckpointSaveDeferred = true;
            return;
        }

        if (legacyExportCandidateGraphResult is null || unifiedImportOutputSession is null)
        {
            throw new InvalidDataException("候補パッケージと作業フォルダーを読み込んでから保存してください。");
        }

        await legacyGraphCheckpointSaveGate.WaitAsync();
        try
        {
            if (!CanStartLegacyGraphCheckpointSave())
            {
                legacyGraphCheckpointSaveDeferred = true;
                return;
            }

            var graph = legacyExportCandidateGraphResult;
            var session = unifiedImportOutputSession;
            var candidatePackagePath = Path.GetFullPath(graph.PackagePath);
            if (!IsSameOrSubPath(candidatePackagePath, session.WorkIntermediatePath))
            {
                throw new InvalidDataException("候補パッケージが作業用中間フォルダーの外側を指しています。");
            }

            var candidateManifestPath = Path.Combine(candidatePackagePath, "manifest.json");
            var candidateManifestSha256 = await CalculateLegacyGraphSha256Async(candidateManifestPath);
            if (!CanStartLegacyGraphCheckpointSave())
            {
                legacyGraphCheckpointSaveDeferred = true;
                return;
            }

            var checkpoint = BuildLegacyGraphWorkCheckpoint(
                graph,
                session,
                Path.GetRelativePath(session.WorkIntermediatePath, candidatePackagePath),
                candidateManifestSha256);
            if (!CanStartLegacyGraphCheckpointSave())
            {
                legacyGraphCheckpointSaveDeferred = true;
                return;
            }

            await legacyGraphWorkCheckpointStore.SaveAsync(
                session.WorkCheckpointsPath,
                checkpoint);
            legacyGraphCheckpointSaveDeferred = false;
        }
        finally
        {
            legacyGraphCheckpointSaveGate.Release();
        }
    }

    private void ScheduleLegacyGraphCheckpointSave()
    {
        if (!CanStartLegacyGraphCheckpointSave() ||
            legacyExportCandidateGraphResult is null ||
            unifiedImportOutputSession is null ||
            legacyGraphCheckpointSaveScheduled)
        {
            if (!CanStartLegacyGraphCheckpointSave() &&
                legacyExportCandidateGraphResult is not null &&
                unifiedImportOutputSession is not null)
            {
                legacyGraphCheckpointSaveDeferred = true;
            }

            return;
        }

        legacyGraphCheckpointSaveDeferred = false;
        legacyGraphCheckpointSaveScheduled = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                legacyGraphCheckpointSaveScheduled = false;
                if (!CanStartLegacyGraphCheckpointSave())
                {
                    legacyGraphCheckpointSaveDeferred = true;
                    return;
                }

                _ = SaveLegacyGraphCheckpointInBackgroundAsync();
            }));
    }

    private bool CanStartLegacyGraphCheckpointSave() =>
        LegacyGraphCheckpointSaveState.CanStart(
            legacyGraphResumeInProgress,
            legacyGraphBulkMergeBusy,
            legacyGraphFinalPackageBusy);

    private void ScheduleDeferredLegacyGraphCheckpointSave()
    {
        if (!legacyGraphCheckpointSaveDeferred)
        {
            return;
        }

        legacyGraphCheckpointSaveDeferred = false;
        ScheduleLegacyGraphCheckpointSave();
    }

    private async Task SaveLegacyGraphCheckpointInBackgroundAsync()
    {
        if (!CanStartLegacyGraphCheckpointSave())
        {
            legacyGraphCheckpointSaveDeferred = true;
            return;
        }

        try
        {
            await SaveLegacyGraphCheckpointAsync("automatic");
            if (!CanStartLegacyGraphCheckpointSave())
            {
                return;
            }

            if (LegacyGraphWorkStatusText is not null)
            {
                LegacyGraphWorkStatusText.Text =
                    $"作業状態を自動保存しました（{DateTime.Now:HH:mm:ss}）。アプリ終了後もこの作業フォルダーから再開できます。";
                LegacyGraphWorkStatusText.Foreground = ToBrush("#52647A");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            if (LegacyGraphWorkStatusText is not null)
            {
                LegacyGraphWorkStatusText.Text = $"作業状態の自動保存に失敗しました: {exception.Message}";
                LegacyGraphWorkStatusText.Foreground = ToBrush("#A61B1B");
            }
        }
        catch (Exception exception)
        {
            if (LegacyGraphWorkStatusText is not null)
            {
                LegacyGraphWorkStatusText.Text = $"作業状態の自動保存中に予期しないエラーが発生しました: {exception.Message}";
                LegacyGraphWorkStatusText.Foreground = ToBrush("#A61B1B");
            }
        }
    }

    private static string ResolveLegacyGraphCheckpointPath(
        string rootPath,
        string relativePath,
        string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"{label}の相対パスが不正です。");
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        if (!IsSameOrSubPath(fullPath, rootPath) || !Directory.Exists(fullPath))
        {
            throw new InvalidDataException($"{label}が作業用中間フォルダーの外側、または存在しません。");
        }

        return fullPath;
    }

    private static async Task<string> CalculateLegacyGraphSha256Async(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"ハッシュ対象ファイルが見つかりません: {path}");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private LegacyGraphWorkCheckpoint BuildLegacyGraphWorkCheckpoint(
        AbacusLegacyExportCandidateGraphResult graph,
        AbacusImportOutputPackageSession session,
        string candidatePackagePath,
        string candidateManifestSha256) =>
        new(
            LegacyGraphWorkCheckpointSchema.Kind,
            LegacyGraphWorkCheckpointSchema.CurrentVersion,
            session.PackageId,
            session.SourcePath,
            session.SourceFingerprint,
            candidatePackagePath,
            candidateManifestSha256,
            LegacyExportPathTextBox.Text.Trim(),
            legacyExportSubsetActive,
            string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase)
                ? "matching"
                : LegacyPreparationExpander.IsExpanded ? "preparation" : "graph",
            GetLegacyGraphSelectedCheckpointItem().Type,
            GetLegacyGraphSelectedCheckpointItem().Id,
            legacyGraphImportConfirmed,
            new Dictionary<string, string>(legacyGraphManualDocumentLinks, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(legacyGraphManualVehicleCustomerLinks, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(legacyGraphManualDocumentCustomerLinks, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(legacyGraphDocumentLinkMethods, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(legacyGraphDocumentLinkReasons, StringComparer.OrdinalIgnoreCase),
            legacyGraphUnconnectedDocumentKeys.ToArray(),
            legacyGraphTrayDocumentKeys.ToArray(),
            legacyGraphExcludedDocumentKeys.ToArray(),
            legacyGraphTrayVehicleIds.ToArray(),
            legacyGraphTrashCustomerIds.ToArray(),
            legacyGraphTrashVehicleIds.ToArray(),
            legacyGraphTrashDocumentKeys.ToArray(),
            legacyGraphCustomerMergeGroups.Values
                .Select(group => new LegacyGraphCheckpointMergeGroup(
                    group.GroupId,
                    group.Origin,
                    group.CustomerIds.ToArray()))
                .ToArray(),
            new Dictionary<string, string>(
                legacyGraphCustomerMergeGroupByCustomerId,
                StringComparer.Ordinal),
            legacyGraphCustomerMergeDrafts.ToDictionary(
                pair => pair.Key,
                pair => new LegacyGraphCheckpointMergeDraft(
                    pair.Value.GroupKey,
                    pair.Value.CandidateCustomerIds.ToArray(),
                    new Dictionary<string, string>(pair.Value.FieldSelections, StringComparer.Ordinal),
                    new Dictionary<string, string>(pair.Value.SelectedValues, StringComparer.Ordinal),
                    pair.Value.SavedAtUtc),
                StringComparer.Ordinal),
            legacyGraphAppliedCustomerMergeKeys.ToArray(),
            new Dictionary<string, string>(legacyGraphVirtualCustomerMergeKeys, StringComparer.Ordinal),
            new Dictionary<string, bool>(legacyGraphCustomerGroupExpanded, StringComparer.Ordinal),
            graph.AllDocuments
                .Select(document => new LegacyGraphCheckpointDetailState(
                    GetLegacyDocumentKey(document),
                    document.DetailsJson,
                    document.DocumentType,
                    document.MaintenanceCategory,
                    document.ClassificationWarning))
                .ToArray(),
             CreateCheckpointImageMapping(fp5VehicleImageMapping),
             DateTimeOffset.UtcNow,
             legacyGraphRecommendationStates
                 .Concat(legacyGraphRecommendationDecisions
                     .Where(pair => !legacyGraphRecommendationStates.ContainsKey(pair.Key))
                     .Select(pair => new KeyValuePair<string, LegacyGraphRecommendationState>(
                         pair.Key,
                         new LegacyGraphRecommendationState(pair.Value))))
                 .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(pair => new LegacyGraphCheckpointRecommendationState(
                     pair.Key,
                     pair.Value.Decision,
                     pair.Value.Lifecycle,
                     pair.Value.ResolutionReason,
                     pair.Value.UpdatedAtUtc,
                     pair.Value.WorkTargetKey,
                     pair.Value.ExternalCustomerId))
                 .ToArray(),
             new Dictionary<string, string>(legacyGraphCustomerNameOverrides, StringComparer.Ordinal),
             legacyGraphMatchingCategory,
             legacyGraphMatchingRecommendationIndex,
             legacyGraphMatchingChanges
                 .Select(change => new LegacyGraphCheckpointMatchingChange(
                     change.ChangeId,
                     change.Kind,
                     change.SubjectId,
                     change.Title,
                     change.Details,
                     change.CreatedAtUtc,
                     change.CustomerId,
                     change.WorkTargetKey,
                     change.ExternalCustomerId,
                     change.UndoState))
                 .ToArray(),
             new Dictionary<string, string>(
                 legacyGraphLogicalCustomerMergeGroupByCustomerId,
                 StringComparer.Ordinal),
             legacyGraphMatchingManualCustomerCandidateTargets.ToDictionary(
                 pair => pair.Key,
                 pair => pair.Value.ToArray(),
                 StringComparer.Ordinal),
             new Dictionary<string, bool>(
                 legacyGraphCustomerApprovalStates,
                 StringComparer.Ordinal),
             new Dictionary<string, string>(
                 legacyGraphCustomerReviewStates,
                 StringComparer.Ordinal),
             MatchingCustomerId: legacyGraphMatchingCustomerId);

    private (string? Type, string? Id) GetLegacyGraphSelectedCheckpointItem()
    {
        switch (legacyGraphSelectedItem)
        {
            case AbacusLegacyExportCandidateGraphCustomer customer:
                return ("customer", GetLegacyGraphSourceCustomer(customer).CustomerId);
            case AbacusLegacyExportCandidateGraphVehicle vehicle:
                return ("vehicle", vehicle.VehicleId);
            case AbacusLegacyExportCandidateGraphDocument document:
                return ("document", GetLegacyDocumentKey(document));
            default:
                return (null, null);
        }
    }

    private void RestoreLegacyGraphWorkCheckpoint(
        LegacyGraphWorkCheckpoint checkpoint,
        AbacusLegacyExportPreviewPackageResult package,
        AbacusLegacyExportCandidateGraphResult graph)
    {
        var recovery = LegacyGraphWorkCheckpointRecovery.NormalizeMergeMembership(checkpoint);
        checkpoint = recovery.Checkpoint;
        if (!string.Equals(
                Path.GetFullPath(package.PackagePath),
                Path.GetFullPath(graph.PackagePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("作業チェックポイントの候補パッケージが再読込結果と一致しません。");
        }

        var customerIds = graph.Customers
            .Select(customer => customer.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        ValidateLegacyGraphCheckpointDetails(checkpoint, graph);
        legacyGraphCustomerNameOverrides.Clear();
        CopyInto(legacyGraphCustomerNameOverrides, checkpoint.CustomerNameOverrides ?? []);
        legacyGraphMatchingManualCustomerCandidateTargets.Clear();
        foreach (var pair in checkpoint.MatchingManualCustomerCandidateTargets ?? [])
        {
            if (!customerIds.Contains(pair.Key) ||
                pair.Value is null ||
                pair.Value.Any(customerId => !customerIds.Contains(customerId)))
            {
                throw new InvalidDataException("チェックポイントの手動顧客候補が候補パッケージに存在しません。");
            }

            legacyGraphMatchingManualCustomerCandidateTargets[pair.Key] =
                pair.Value.ToHashSet(StringComparer.Ordinal);
        }
        RebuildLegacyGraphRecommendationCandidates();
        var vehicles = graph.Customers
            .SelectMany(customer => customer.Vehicles)
            .Concat(graph.UnresolvedVehicleRows)
            .GroupBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var documents = graph.AllDocuments
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var recommendationCandidateIds = legacyGraphRecommendationCandidates
            .Select(candidate => candidate.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recommendationStates = checkpoint.RecommendationStates ?? [];
        if (recommendationStates
            .GroupBy(state => state.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("チェックポイントのおすすめ判定状態が重複しています。");
        }
        ValidateCheckpointKeys(
            recommendationStates
                .Where(state => state.Lifecycle == LegacyGraphRecommendationLifecycle.Active &&
                                string.IsNullOrWhiteSpace(state.WorkTargetKey) &&
                                string.IsNullOrWhiteSpace(state.ExternalCustomerId))
                .Select(state => state.CandidateId),
            recommendationCandidateIds,
            "おすすめ判定状態");
        var validMergeKeys = graph.Customers
            .Select(GetLegacyCustomerMergeKey)
            .ToHashSet(StringComparer.Ordinal);
        var checkpointGroupIds = checkpoint.CustomerMergeGroups
            .Select(group => group.GroupId)
            .ToHashSet(StringComparer.Ordinal);
        if (checkpointGroupIds.Count != checkpoint.CustomerMergeGroups.Length)
        {
            throw new InvalidDataException("チェックポイントの顧客統合グループIDが重複しています。");
        }

        var validGroupKeys = validMergeKeys
            .Concat(checkpointGroupIds)
            .ToHashSet(StringComparer.Ordinal);
        var groupedCustomerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in checkpoint.CustomerMergeGroups)
        {
            foreach (var customerId in group.CustomerIds)
            {
                if (!customerIds.Contains(customerId))
                {
                    throw new InvalidDataException("チェックポイントに存在しない顧客IDが含まれています。");
                }

                if (!groupedCustomerIds.Add(customerId))
                {
                    throw new InvalidDataException("チェックポイントで同じ顧客が複数の統合グループに含まれています。");
                }
            }
        }

        ValidateCheckpointKeys(
            checkpoint.ManualDocumentVehicleLinks.Keys,
            documents.Keys,
            "手動書類・車両紐付け");
        ValidateCheckpointKeys(
            checkpoint.ManualDocumentCustomerGroupLinks.Keys,
            documents.Keys,
            "手動顧客のみ紐付け");
        ValidateCheckpointKeys(
            checkpoint.DocumentLinkMethods.Keys,
            documents.Keys,
            "書類紐付け方法");
        ValidateCheckpointKeys(
            checkpoint.DocumentLinkReasons.Keys,
            documents.Keys,
            "書類紐付け根拠");
        ValidateCheckpointKeys(
            checkpoint.UnconnectedDocumentKeys.Concat(checkpoint.TrayDocumentKeys)
                .Concat(checkpoint.ExcludedDocumentKeys)
                .Concat(checkpoint.TrashDocumentKeys),
            documents.Keys,
            "書類状態");
        ValidateCheckpointKeys(
            checkpoint.ManualVehicleCustomerLinks.Keys
                .Concat(checkpoint.TrayVehicleIds)
                .Concat(checkpoint.TrashVehicleIds),
            vehicles.Keys,
            "車両状態");
        ValidateCheckpointKeys(
            checkpoint.TrashCustomerIds,
            customerIds,
            "顧客状態");
        foreach (var vehicleCustomerId in checkpoint.ManualVehicleCustomerLinks.Values)
        {
            if (!customerIds.Contains(vehicleCustomerId))
            {
                throw new InvalidDataException("チェックポイントの車両紐付け先顧客が存在しません。");
            }
        }

        foreach (var pair in checkpoint.CustomerMergeGroupByCustomerId)
        {
            if (!customerIds.Contains(pair.Key) || !checkpointGroupIds.Contains(pair.Value))
            {
                throw new InvalidDataException("チェックポイントの顧客統合グループが候補パッケージに存在しません。");
            }
        }

        foreach (var pair in checkpoint.LogicalCustomerMergeGroupByCustomerId ?? [])
        {
            if (!customerIds.Contains(pair.Key) || !checkpointGroupIds.Contains(pair.Value))
            {
                throw new InvalidDataException("チェックポイントの論理顧客グループが候補パッケージに存在しません。");
            }
        }

        foreach (var pair in checkpoint.CustomerMergeDrafts)
        {
            if (!string.Equals(pair.Key, pair.Value.GroupKey, StringComparison.Ordinal) ||
                !validGroupKeys.Contains(pair.Value.GroupKey) ||
                pair.Value.CandidateCustomerIds is null ||
                pair.Value.CandidateCustomerIds.Any(customerId => !customerIds.Contains(customerId)))
            {
                throw new InvalidDataException("チェックポイントの顧客統合プレビューが候補パッケージに存在しません。");
            }
        }

        foreach (var groupKey in checkpoint.AppliedCustomerMergeKeys
                     .Concat(checkpoint.CustomerGroupExpanded.Keys)
                     .Concat(checkpoint.CustomerMergeDrafts.Keys)
                     .Concat(checkpoint.VirtualCustomerMergeKeys.Values)
                     .Concat((checkpoint.CustomerApprovalStates ?? []).Keys)
                     .Concat((checkpoint.CustomerReviewStates ?? []).Keys))
        {
            if (!validGroupKeys.Contains(groupKey))
            {
                throw new InvalidDataException("チェックポイントの顧客統合グループが候補パッケージに存在しません。");
            }
        }

        var groups = checkpoint.CustomerMergeGroups.ToDictionary(
            group => group.GroupId,
            group => new LegacyGraphCustomerMergeGroup(
                group.GroupId,
                group.Origin,
                group.CustomerIds.ToList()),
            StringComparer.Ordinal);
        var drafts = checkpoint.CustomerMergeDrafts.ToDictionary(
            pair => pair.Key,
            pair => new LegacyGraphCustomerMergeDraft(
                pair.Value.GroupKey,
                pair.Value.CandidateCustomerIds,
                pair.Value.FieldSelections,
                pair.Value.SelectedValues,
                pair.Value.SavedAtUtc),
            StringComparer.Ordinal);

        legacyGraphManualDocumentLinks.Clear();
        CopyInto(legacyGraphManualDocumentLinks, checkpoint.ManualDocumentVehicleLinks);
        legacyGraphManualVehicleCustomerLinks.Clear();
        CopyInto(legacyGraphManualVehicleCustomerLinks, checkpoint.ManualVehicleCustomerLinks);
        legacyGraphManualDocumentCustomerLinks.Clear();
        CopyInto(legacyGraphManualDocumentCustomerLinks, checkpoint.ManualDocumentCustomerGroupLinks);
        legacyGraphDocumentLinkMethods.Clear();
        CopyInto(legacyGraphDocumentLinkMethods, checkpoint.DocumentLinkMethods);
        legacyGraphDocumentLinkReasons.Clear();
        CopyInto(legacyGraphDocumentLinkReasons, checkpoint.DocumentLinkReasons);
        legacyGraphRecommendationDecisions.Clear();
        legacyGraphRecommendationStates.Clear();
        foreach (var state in recommendationStates)
        {
            if (!AbacusRecommendationDecisionValues.IsSupported(state.Decision))
            {
                throw new InvalidDataException("チェックポイントに対応していないおすすめ判定状態があります。");
            }

            if (!LegacyGraphRecommendationLifecycle.IsSupported(state.Lifecycle))
            {
                throw new InvalidDataException("チェックポイントに対応していないおすすめライフサイクルがあります。");
            }

            legacyGraphRecommendationStates[state.CandidateId] = new LegacyGraphRecommendationState(
                state.Decision,
                state.Lifecycle,
                state.ResolutionReason,
                state.UpdatedAtUtc,
                state.WorkTargetKey,
                state.ExternalCustomerId);
            if (state.Lifecycle == LegacyGraphRecommendationLifecycle.Active)
            {
                legacyGraphRecommendationDecisions[state.CandidateId] = state.Decision;
            }
        }
        legacyGraphMatchingCategory = checkpoint.MatchingCategory is
            LegacyMatchingCategoryKinds.Customer or LegacyMatchingCategoryKinds.Vehicle or LegacyMatchingCategoryKinds.Document
            ? checkpoint.MatchingCategory
            : LegacyMatchingCategoryKinds.Customer;
        legacyGraphMatchingRecommendationIndex = checkpoint.MatchingRecommendationIndex;
        legacyGraphMatchingChanges.Clear();
        foreach (var change in checkpoint.MatchingChanges ?? [])
        {
            legacyGraphMatchingChanges.Add(new LegacyMatchingChangeItem(
                change.ChangeId,
                change.Kind,
                change.SubjectId,
                change.Title,
                change.Details,
                change.CreatedAtUtc,
                change.CustomerId,
                change.WorkTargetKey,
                change.ExternalCustomerId,
                change.UndoState));
        }
        RestoreSet(legacyGraphUnconnectedDocumentKeys, checkpoint.UnconnectedDocumentKeys);
        RestoreSet(legacyGraphTrayDocumentKeys, checkpoint.TrayDocumentKeys);
        RestoreSet(legacyGraphExcludedDocumentKeys, checkpoint.ExcludedDocumentKeys);
        RestoreSet(legacyGraphTrayVehicleIds, checkpoint.TrayVehicleIds);
        RestoreSet(legacyGraphTrashCustomerIds, checkpoint.TrashCustomerIds);
        RestoreSet(legacyGraphTrashVehicleIds, checkpoint.TrashVehicleIds);
        RestoreSet(legacyGraphTrashDocumentKeys, checkpoint.TrashDocumentKeys);
        legacyGraphCustomerMergeGroups.Clear();
        foreach (var pair in groups)
        {
            legacyGraphCustomerMergeGroups[pair.Key] = pair.Value;
        }

        legacyGraphCustomerMergeGroupByCustomerId.Clear();
        CopyInto(legacyGraphCustomerMergeGroupByCustomerId, checkpoint.CustomerMergeGroupByCustomerId);
        legacyGraphLogicalCustomerMergeGroupByCustomerId.Clear();
        CopyInto(
            legacyGraphLogicalCustomerMergeGroupByCustomerId,
            checkpoint.LogicalCustomerMergeGroupByCustomerId ?? []);
        legacyGraphCustomerMergeDrafts.Clear();
        foreach (var pair in drafts)
        {
            legacyGraphCustomerMergeDrafts[pair.Key] = pair.Value;
        }

        RestoreSet(legacyGraphAppliedCustomerMergeKeys, checkpoint.AppliedCustomerMergeKeys);
        legacyGraphVirtualCustomerMergeKeys.Clear();
        CopyInto(legacyGraphVirtualCustomerMergeKeys, checkpoint.VirtualCustomerMergeKeys);
        legacyGraphCustomerGroupExpanded.Clear();
        foreach (var pair in checkpoint.CustomerGroupExpanded)
        {
            legacyGraphCustomerGroupExpanded[pair.Key] = pair.Value;
        }

        // Version 4以前は、キャンバス承認済みグループを論理顧客として別保存していません。
        // 旧形式の承認済みグループだけは、元の構成を壊さないよう論理顧客へ読み替えます。
        foreach (var group in groups.Values.Where(group => group.CustomerIds.Count > 1 &&
                                                           (group.Origin == "logical" ||
                                                            legacyGraphAppliedCustomerMergeKeys.Contains(group.GroupId))))
        {
            foreach (var customerId in group.CustomerIds)
            {
                legacyGraphLogicalCustomerMergeGroupByCustomerId[customerId] = group.GroupId;
            }
        }

        legacyGraphCustomerApprovalStates.Clear();
        foreach (var pair in checkpoint.CustomerApprovalStates ?? [])
        {
            legacyGraphCustomerApprovalStates[pair.Key] = pair.Value;
        }
        legacyGraphCustomerReviewStates.Clear();
        foreach (var pair in checkpoint.CustomerReviewStates ?? [])
        {
            legacyGraphCustomerReviewStates[pair.Key] = pair.Value;
            legacyGraphCustomerApprovalStates[pair.Key] =
                string.Equals(pair.Value, LegacyGraphCustomerReviewStateValues.Approved, StringComparison.Ordinal);
        }
        RebuildLegacyGraphRecommendationCandidates();

        legacyGraphImportConfirmed = checkpoint.ImportConfirmed;
        SetLegacyGraphUiMode(
            string.Equals(checkpoint.UiMode, "matching", StringComparison.OrdinalIgnoreCase)
                ? "matching"
                : "graph",
            scheduleCheckpoint: false);
        LegacyPreparationExpander.IsExpanded =
            string.Equals(checkpoint.UiMode, "preparation", StringComparison.OrdinalIgnoreCase);
        RefreshLegacyGraphCustomerList();
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphUnresolvedDocumentLists();
        RefreshLegacyGraphTrashLists();
        RestoreLegacyGraphSelectedItem(checkpoint.SelectedItemType, checkpoint.SelectedItemId, graph);
        if (string.Equals(checkpoint.UiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            // Checkpointに保存したMatching基準顧客を、汎用SelectedItemより優先します。
            // 旧CheckpointだけはMatchingCustomerIdがないため、従来の選択項目をfallbackに使います。
            legacyGraphMatchingSelectedMergeMemberCustomerId = null;
            legacyGraphMatchingCustomerId = checkpoint.MatchingCustomerId;
            var savedMatchingCustomer = string.IsNullOrWhiteSpace(checkpoint.MatchingCustomerId)
                ? null
                : GetLegacyGraphMatchingCustomers().FirstOrDefault(customer =>
                    string.Equals(
                        customer.CustomerId,
                        checkpoint.MatchingCustomerId,
                        StringComparison.Ordinal));
            if (savedMatchingCustomer is not null)
            {
                EnsureLegacyGraphMatchingCustomerSelection();
            }
            else
            {
                legacyGraphMatchingCustomerId = null;
                EnsureLegacyGraphMatchingCustomerSelection(
                    GetLegacyGraphCustomerForCurrentSelection(),
                    allowBaseCustomerChange: true);
            }
            legacyGraphSelectedRecommendation = null;
            RefreshLegacyGraphCustomerList();
            RefreshLegacyMatchingView();
        }
        UpdateLegacyGraphImportConfirmationButton();
        LegacyGraphWorkStatusText.Text =
            $"作業チェックポイントを復元しました（保存日時: {checkpoint.SavedAtUtc.ToLocalTime():yyyy/MM/dd HH:mm:ss}）。" +
            (recovery.Changed ? "旧形式の顧客統合グループの重複所属を整理しました。" : "");
        LegacyGraphWorkStatusText.Foreground = ToBrush("#17643A");
    }

    private static void ValidateCheckpointKeys(
        IEnumerable<string> actualKeys,
        IEnumerable<string> validKeys,
        string label)
    {
        var valid = validKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (actualKeys.Any(key => !valid.Contains(key)))
        {
            throw new InvalidDataException($"チェックポイントの{label}に候補パッケージにないIDが含まれています。");
        }
    }

    private void ValidateLegacyGraphCheckpointDetails(
        LegacyGraphWorkCheckpoint checkpoint,
        AbacusLegacyExportCandidateGraphResult graph)
    {
        var current = graph.AllDocuments
            .Select(document => new LegacyGraphCheckpointDetailState(
                GetLegacyDocumentKey(document),
                document.DetailsJson,
                document.DocumentType,
                document.MaintenanceCategory,
                document.ClassificationWarning))
            .ToDictionary(detail => detail.DocumentKey, StringComparer.OrdinalIgnoreCase);
        var saved = checkpoint.DetailStates
            .ToDictionary(detail => detail.DocumentKey, StringComparer.OrdinalIgnoreCase);
        if (current.Count != saved.Count ||
            current.Any(pair =>
                !saved.TryGetValue(pair.Key, out var detail) ||
                !string.Equals(pair.Value.DetailsJson, detail.DetailsJson, StringComparison.Ordinal) ||
                !string.Equals(pair.Value.DocumentType, detail.DocumentType, StringComparison.Ordinal) ||
                !string.Equals(pair.Value.MaintenanceCategory, detail.MaintenanceCategory, StringComparison.Ordinal) ||
                !string.Equals(pair.Value.ClassificationWarning, detail.ClassificationWarning, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "候補CSVのGate 19明細または書類分類が作業保存後に変更されています。"
                + "チェックポイントは適用しません。");
        }
    }

    private void RestoreLegacyGraphSelectedItem(
        string? selectedItemType,
        string? selectedItemId,
        AbacusLegacyExportCandidateGraphResult graph)
    {
        legacyGraphSelectedItem = null;
        if (string.IsNullOrWhiteSpace(selectedItemType) || string.IsNullOrWhiteSpace(selectedItemId))
        {
            return;
        }

        object? selected = selectedItemType switch
        {
            "customer" => graph.Customers.FirstOrDefault(customer =>
                string.Equals(customer.CustomerId, selectedItemId, StringComparison.Ordinal)),
            "vehicle" => graph.Customers.SelectMany(customer => customer.Vehicles)
                .Concat(graph.UnresolvedVehicleRows)
                .FirstOrDefault(vehicle =>
                    string.Equals(vehicle.VehicleId, selectedItemId, StringComparison.Ordinal)),
            "document" => graph.AllDocuments.FirstOrDefault(document =>
                string.Equals(GetLegacyDocumentKey(document), selectedItemId, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        if (selected is null)
        {
            return;
        }

        legacyGraphSelectedItem = selected;
        var customer = selected switch
        {
            AbacusLegacyExportCandidateGraphCustomer selectedCustomer => selectedCustomer,
            AbacusLegacyExportCandidateGraphVehicle selectedVehicle =>
                FindLegacyGraphCustomerById(
                    legacyGraphManualVehicleCustomerLinks.TryGetValue(
                        selectedVehicle.VehicleId,
                        out var linkedCustomerId)
                        ? linkedCustomerId
                        : selectedVehicle.CustomerId),
            AbacusLegacyExportCandidateGraphDocument selectedDocument =>
                FindCurrentCustomerForDocument(selectedDocument),
            _ => null,
        };
        if (customer is not null)
        {
            var entryId = GetLegacyGraphCustomerListEntryId(customer);
            RefreshLegacyGraphCustomerList(entryId);
            if (selected is AbacusLegacyExportCandidateGraphCustomer selectedCustomer)
            {
                var display = GetLegacyGraphDisplayCustomer(selectedCustomer);
                legacyGraphSelectedItem = display;
                UpdateLegacyGraphInspector(display);
                RenderLegacyGraphCustomer(display);
            }
            else
            {
                UpdateLegacyGraphInspector(selected);
                RenderLegacyGraphCustomer(GetLegacyGraphDisplayCustomer(customer));
            }
        }
        else
        {
            UpdateLegacyGraphInspector(selected);
        }
    }

    private static void CopyInto(
        IDictionary<string, string> destination,
        IReadOnlyDictionary<string, string> source)
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value;
        }
    }

    private static void RestoreSet(
        ISet<string> destination,
        IEnumerable<string> source)
    {
        destination.Clear();
        foreach (var value in source)
        {
            destination.Add(value);
        }
    }

    private static LegacyGraphCheckpointImageMapping? CreateCheckpointImageMapping(
        AbacusFp5VehicleImageMappingResult? mapping) =>
        mapping is null
            ? null
            : new LegacyGraphCheckpointImageMapping(
                mapping.OutputFolderPath,
                mapping.ReportPath,
                mapping.SourceFilePath,
                mapping.InternalVehicleRecordCount,
                mapping.VehicleCsvRowCount,
                mapping.JpegImageCount,
                mapping.GifPlaceholderCount,
                mapping.MatchedImageCount,
                mapping.NoImageCount,
                mapping.ReviewCount,
                mapping.UnmatchedCount,
                mapping.MultipleCandidateCount,
                mapping.UnknownImageReferenceCount,
                mapping.DuplicateImageReferenceCount,
                mapping.DuplicateImageSha256Count,
                mapping.UnreferencedImageCount,
                mapping.Mappings.Select(item => new LegacyGraphCheckpointImageMappingRow(
                    item.Index,
                    item.RecordIdHex,
                    item.ImageIdHex,
                    item.ImageRelativePath,
                    item.ImageSha256,
                    item.VehicleFileName,
                    item.VehicleRowNumber,
                    item.Status,
                    item.Evidence)).ToArray());

    private static AbacusFp5VehicleImageMappingResult? CreateImageMappingFromCheckpoint(
        LegacyGraphCheckpointImageMapping? mapping) =>
        mapping is null
            ? null
            : new AbacusFp5VehicleImageMappingResult(
                mapping.OutputFolderPath,
                mapping.ReportPath,
                mapping.SourceFilePath,
                mapping.InternalVehicleRecordCount,
                mapping.VehicleCsvRowCount,
                mapping.JpegImageCount,
                mapping.GifPlaceholderCount,
                mapping.MatchedImageCount,
                mapping.NoImageCount,
                mapping.ReviewCount,
                mapping.UnmatchedCount,
                mapping.MultipleCandidateCount,
                mapping.UnknownImageReferenceCount,
                mapping.DuplicateImageReferenceCount,
                mapping.DuplicateImageSha256Count,
                mapping.UnreferencedImageCount,
                mapping.Mappings.Select(item => new AbacusFp5VehicleImageMapping(
                    item.Index,
                    item.RecordIdHex,
                    item.ImageIdHex,
                    item.ImageRelativePath,
                    item.ImageSha256,
                    item.VehicleFileName,
                    item.VehicleRowNumber,
                    item.Status,
                    item.Evidence,
                    null,
                    null,
                    null,
                    null)).ToArray());

    private async Task ValidateLegacyGraphCheckpointImageMappingAsync(
        LegacyGraphCheckpointImageMapping mapping,
        string workPath,
        string workAbacusCopyPath)
    {
        if (!IsSameOrSubPath(mapping.OutputFolderPath, workPath) ||
            !IsSameOrSubPath(mapping.ReportPath, workPath) ||
            !IsSameOrSubPath(mapping.SourceFilePath, workAbacusCopyPath))
        {
            throw new InvalidDataException("保存済み画像対応付けが作業フォルダーの外側を指しています。");
        }

        if (!File.Exists(mapping.ReportPath))
        {
            throw new InvalidDataException("保存済み画像対応付けレポートが見つかりません。");
        }

        foreach (var image in mapping.Mappings.Where(item =>
                     item.Status == "matched" &&
                     !string.IsNullOrWhiteSpace(item.ImageRelativePath)))
        {
            var imagePath = Path.GetFullPath(Path.Combine(mapping.OutputFolderPath, image.ImageRelativePath!));
            if (!IsSameOrSubPath(imagePath, mapping.OutputFolderPath) ||
                !File.Exists(imagePath))
            {
                throw new InvalidDataException("保存済み画像対応付けの画像ファイルが見つかりません。");
            }

            if (!string.IsNullOrWhiteSpace(image.ImageSha256))
            {
                var actual = await CalculateLegacyGraphSha256Async(imagePath);
                if (!string.Equals(actual, image.ImageSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("保存済み画像のSHA-256が一致しません。画像を変更せず再解析してください。");
                }
            }
        }
    }

    private void LegacyGraphTrashSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        legacyGraphSelectedItem = list.SelectedItem;
        UpdateLegacyGraphInspector(list.SelectedItem);
    }

    private int GetLegacyGraphUnresolvedVehicleCount() =>
        GetLegacyGraphUnresolvedVehicles().Count;

    private IReadOnlyList<AbacusLegacyExportCandidateGraphVehicle> GetLegacyGraphAllVehicles()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        return legacyExportCandidateGraphResult.Customers
            .SelectMany(customer => customer.Vehicles)
            .Concat(legacyExportCandidateGraphResult.UnresolvedVehicleRows)
            .GroupBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphVehicle> GetLegacyGraphUnresolvedVehicles()
    {
        return GetLegacyGraphAllVehicles()
            .Where(vehicle => !IsLegacyGraphVehicleInTrash(vehicle) &&
                              (legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId) ||
                              (!vehicle.HasCustomer &&
                               !legacyGraphManualVehicleCustomerLinks.ContainsKey(vehicle.VehicleId))))
            .OrderBy(vehicle => vehicle.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .ToArray();
    }

    private bool IsLegacyGraphCustomerInTrash(string customerId) =>
        legacyGraphTrashCustomerIds.Contains(customerId);

    private bool IsLegacyGraphVehicleInTrash(
        AbacusLegacyExportCandidateGraphVehicle vehicle) =>
        legacyGraphTrashVehicleIds.Contains(vehicle.VehicleId) ||
        (FindCurrentCustomerForVehicle(vehicle) is { } currentCustomer &&
         legacyGraphTrashCustomerIds.Contains(currentCustomer.CustomerId));

    private bool IsLegacyGraphDocumentInTrash(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        var key = GetLegacyDocumentKey(document);
        if (legacyGraphTrashDocumentKeys.Contains(key))
        {
            return true;
        }

        if (FindCurrentVehicleForDocument(document) is { } currentVehicle &&
            IsLegacyGraphVehicleInTrash(currentVehicle))
        {
            return true;
        }

        if (FindCurrentCustomerForDocument(document) is { } currentCustomer &&
            legacyGraphTrashCustomerIds.Contains(currentCustomer.CustomerId))
        {
            return true;
        }

        return legacyGraphManualDocumentCustomerLinks.TryGetValue(key, out var groupKey) &&
               TryGetLegacyGraphMergeGroup(groupKey, out var group) &&
               group.CustomerIds.Any(legacyGraphTrashCustomerIds.Contains);
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> GetLegacyGraphTrashCustomers() =>
        legacyExportCandidateGraphResult?.Customers
            .Where(customer => legacyGraphTrashCustomerIds.Contains(customer.CustomerId))
            .OrderBy(customer => GetLegacyGraphCustomerDisplayName(customer), StringComparer.OrdinalIgnoreCase)
            .ThenBy(customer => customer.CustomerId, StringComparer.Ordinal)
            .ToArray() ?? [];

    private IReadOnlyList<AbacusLegacyExportCandidateGraphVehicle> GetLegacyGraphTrashVehicles() =>
        GetLegacyGraphAllVehicles()
            .Where(vehicle => legacyGraphTrashVehicleIds.Contains(vehicle.VehicleId))
            .OrderBy(vehicle => vehicle.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .ToArray();

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetLegacyGraphTrashDocuments() =>
        legacyExportCandidateGraphResult?.AllDocuments
            .Where(document => legacyGraphTrashDocumentKeys.Contains(GetLegacyDocumentKey(document)))
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(document => document.Kind, StringComparer.Ordinal)
            .ThenBy(document => document.DocumentNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

    private IReadOnlyList<AbacusLegacyExportCandidateGraphVehicle> GetLegacyGraphVehiclesAffectedByCustomer(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var customerId = GetLegacyGraphSourceCustomer(customer).CustomerId;
        return GetLegacyGraphAllVehicles()
            .Where(vehicle => FindCurrentCustomerForVehicle(vehicle) is { } currentCustomer &&
                              string.Equals(currentCustomer.CustomerId, customerId, StringComparison.Ordinal))
            .ToArray();
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetLegacyGraphDocumentsAffectedByVehicle(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        return legacyExportCandidateGraphResult?.AllDocuments
            .Where(document => string.Equals(
                ResolveCurrentDocumentVehicleId(document),
                vehicle.VehicleId,
                StringComparison.Ordinal))
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray() ?? [];
    }

    private LegacyGraphTrashImpact GetLegacyGraphTrashImpact(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var vehicles = GetLegacyGraphVehiclesAffectedByCustomer(customer);
        var vehicleIds = vehicles.Select(vehicle => vehicle.VehicleId).ToHashSet(StringComparer.Ordinal);
        var customerId = GetLegacyGraphSourceCustomer(customer).CustomerId;
        var documents = legacyExportCandidateGraphResult?.AllDocuments
            .Where(document =>
                (ResolveCurrentDocumentVehicleId(document) is { } currentVehicleId &&
                 vehicleIds.Contains(currentVehicleId)) ||
                (FindCurrentCustomerForDocument(document) is { } current &&
                 string.Equals(current.CustomerId, customerId, StringComparison.Ordinal)) ||
                (legacyGraphManualDocumentCustomerLinks.TryGetValue(GetLegacyDocumentKey(document), out var groupKey) &&
                 string.Equals(groupKey, GetLegacyCustomerMergeKey(customer), StringComparison.Ordinal)))
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray() ?? [];
        return new LegacyGraphTrashImpact(
            1,
            vehicles.Count,
            documents.Length,
            GetLegacyGraphImageCountForVehicles(vehicles));
    }

    private LegacyGraphTrashImpact GetLegacyGraphTrashImpact(
        AbacusLegacyExportCandidateGraphVehicle vehicle) =>
        new(
            0,
            1,
            GetLegacyGraphDocumentsAffectedByVehicle(vehicle).Count,
            GetLegacyGraphImageCountForVehicles([vehicle]));

    private static LegacyGraphTrashImpact GetLegacyGraphTrashImpact(
        AbacusLegacyExportCandidateGraphDocument document) =>
        new(0, 0, 1, 0);

    private int GetLegacyGraphImageCountForVehicles(
        IEnumerable<AbacusLegacyExportCandidateGraphVehicle> vehicles)
    {
        if (fp5VehicleImageMapping is null)
        {
            return 0;
        }

        var identifiers = vehicles
            .Select(vehicle => NormalizeLegacyGraphImageIdentifier(vehicle.ChassisNumber) + "|" +
                               NormalizeLegacyGraphImageIdentifier(vehicle.RegistrationNumber))
            .Where(identifier => !identifier.StartsWith("|", StringComparison.Ordinal) &&
                                 !identifier.EndsWith("|", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        return fp5VehicleImageMapping.Mappings.Count(mapping =>
            string.Equals(mapping.Status, "matched", StringComparison.Ordinal) &&
            identifiers.Contains(
                NormalizeLegacyGraphImageIdentifier(mapping.ChassisNumber) + "|" +
                NormalizeLegacyGraphImageIdentifier(mapping.RegistrationNumber)));
    }

    private static string NormalizeLegacyGraphImageIdentifier(string? value) =>
        string.Concat((value ?? "").Normalize(NormalizationForm.FormKC)
            .Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();

    private void RefreshLegacyGraphTrashLists()
    {
        LegacyGraphTrashCustomerList.ItemsSource = GetLegacyGraphTrashCustomers();
        LegacyGraphTrashVehicleList.ItemsSource = GetLegacyGraphTrashVehicles();
        LegacyGraphTrashDocumentList.ItemsSource = GetLegacyGraphTrashDocuments();
        RefreshLegacyGraphSearchResults();
    }

    private void RefreshLegacyGraphUnresolvedVehicleList()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            LegacyGraphUnresolvedVehicleList.ItemsSource = null;
            return;
        }

        LegacyGraphUnresolvedVehicleList.ItemsSource = GetLegacyGraphUnresolvedVehicles();
        RefreshLegacyGraphSearchResults();
    }

    private void LegacyGraphUnresolvedVehicleList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not AbacusLegacyExportCandidateGraphVehicle vehicle)
        {
            return;
        }

        list.SelectedItem = vehicle;
        legacyGraphTrayDragVehicle = vehicle;
        legacyGraphTrayDragVehicleStartPoint = e.GetPosition(this);
        // カードからポインターが出ても移動イベントを受け取り、しきい値到達時に確実にドラッグを開始する。
        list.CaptureMouse();
    }

    private void LegacyGraphUnresolvedVehicleList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list ||
            legacyGraphTrayDragVehicle is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - legacyGraphTrayDragVehicleStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - legacyGraphTrayDragVehicleStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var vehicle = legacyGraphTrayDragVehicle;
        legacyGraphTrayDragVehicle = null;
        var data = new DataObject();
        data.SetData(typeof(LegacyGraphUnresolvedVehicleDragPayload),
            new LegacyGraphUnresolvedVehicleDragPayload(vehicle.VehicleId));
        if (list.IsMouseCaptured)
        {
            list.ReleaseMouseCapture();
        }

        ShowLegacyGraphTrashOverlay();
        try
        {
            DragDrop.DoDragDrop(list, data, DragDropEffects.Link);
        }
        finally
        {
            if (list.IsMouseCaptured)
            {
                list.ReleaseMouseCapture();
            }

            ClearLegacyGraphCustomerDropHighlight();
            legacyGraphNativeDocumentDropTargetValid = false;
            HideLegacyGraphTrashOverlay();
        }

        e.Handled = true;
    }

    private void LegacyGraphUnresolvedVehicleList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && list.IsMouseCaptured)
        {
            list.ReleaseMouseCapture();
        }

        legacyGraphTrayDragVehicle = null;
    }

    private static LegacyGraphUnresolvedVehicleDragPayload? GetLegacyGraphUnresolvedVehicleDragPayload(IDataObject data) =>
        data.GetData(typeof(LegacyGraphUnresolvedVehicleDragPayload)) as LegacyGraphUnresolvedVehicleDragPayload;

    private AbacusLegacyExportCandidateGraphVehicle? FindLegacyGraphVehicleById(string vehicleId)
    {
        return GetLegacyGraphAllVehicles().FirstOrDefault(vehicle =>
            string.Equals(vehicle.VehicleId, vehicleId, StringComparison.Ordinal));
    }

    private void ApplyLegacyGraphManualVehicleCustomerLink(
        AbacusLegacyExportCandidateGraphVehicle vehicle,
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        ClearLegacyGraphRecommendationDecisions(
            AbacusRecommendationEntityKinds.Vehicle,
            vehicle.VehicleId);
        var targetCustomer = GetLegacyGraphSourceCustomer(customer);
        if (vehicle.HasCustomer)
        {
            if (!legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId))
            {
                LegacyGraphStatusText.Text = "顧客へ接続済みの車両は、未確定車両トレイから再割当できません。";
                LegacyGraphStatusText.Foreground = ToBrush("#805B10");
                return;
            }

            var targetCustomerIds = GetLegacyGraphCustomerMergeCandidates(customer)
                .Select(candidate => candidate.CustomerId)
                .ToHashSet(StringComparer.Ordinal);
            if (!targetCustomerIds.Contains(vehicle.CustomerId))
            {
                LegacyGraphStatusText.Text = "この車両は元の顧客カードへ戻してください。別の顧客へ変更する場合は、車両情報を確認してから手動で判断してください。";
                LegacyGraphStatusText.Foreground = ToBrush("#805B10");
                return;
            }

            // 変更前の手動接続先・元CSV接続先・関連書類の所属先を先に再確認待ちへ戻します。
            InvalidateLegacyGraphApprovalForVehicle(vehicle);
            legacyGraphTrayVehicleIds.Remove(vehicle.VehicleId);
            InvalidateLegacyGraphCustomerApproval(targetCustomer);
            InvalidateLegacyGraphImportConfirmation();
            RebuildLegacyGraphRecommendationCandidates();
            RefreshLegacyGraphUnresolvedVehicleList();
            RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(targetCustomer));
            var displayCustomer = GetLegacyGraphDisplayCustomer(targetCustomer);
            legacyGraphSelectedItem = displayCustomer;
            UpdateLegacyGraphInspector(displayCustomer);
            RenderLegacyGraphCustomer(displayCustomer);
            LegacyGraphStatusText.Text =
                $"未確定車両 {vehicle.DisplayName} を元の顧客 {GetLegacyGraphCustomerDisplayName(targetCustomer)} へ戻しました。";
            LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
            return;
        }

        InvalidateLegacyGraphApprovalForVehicle(vehicle);
        legacyGraphTrayVehicleIds.Remove(vehicle.VehicleId);
        legacyGraphManualVehicleCustomerLinks[vehicle.VehicleId] = targetCustomer.CustomerId;
        InvalidateLegacyGraphCustomerApproval(targetCustomer);
        InvalidateLegacyGraphImportConfirmation();
        legacyGraphAppliedCustomerMergeKeys.Remove(GetLegacyCustomerMergeKey(targetCustomer));
        RebuildLegacyGraphRecommendationCandidates();
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(targetCustomer));
        var assignedDisplayCustomer = GetLegacyGraphDisplayCustomer(targetCustomer);
        legacyGraphSelectedItem = assignedDisplayCustomer;
        UpdateLegacyGraphInspector(assignedDisplayCustomer);
        RenderLegacyGraphCustomer(assignedDisplayCustomer);
        LegacyGraphStatusText.Text =
            $"未確定車両 {vehicle.DisplayName} を顧客 {GetLegacyGraphCustomerDisplayName(targetCustomer)} へ手動接続しました。顧客へ接続しない車両は最終パッケージから除外されます。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private void ResetLegacyGraphApprovedCustomerMergeRecommendations(
        IEnumerable<string> customerIds)
    {
        var affectedIds = customerIds.ToHashSet(StringComparer.Ordinal);
        var updatedAtUtc = DateTimeOffset.UtcNow;
        foreach (var candidate in legacyGraphRecommendationCandidates.Where(candidate =>
                     candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
                     candidate.TargetKind == AbacusRecommendationEntityKinds.Customer &&
                     (affectedIds.Contains(candidate.SubjectId) ||
                      affectedIds.Contains(candidate.TargetId))))
        {
            if (legacyGraphRecommendationStates.TryGetValue(candidate.CandidateId, out var state))
            {
                var reconciled = LegacyGraphRecommendationLifecycleReconciler
                    .ReconcileAfterMergeMembershipChange(state, updatedAtUtc);
                if (!ReferenceEquals(reconciled, state) &&
                    !AreLegacyGraphCustomerRecommendationEndpointsInSameMergeGroup(candidate))
                {
                    legacyGraphRecommendationStates[candidate.CandidateId] = reconciled;
                    legacyGraphRecommendationDecisions.Remove(candidate.CandidateId);
                    continue;
                }
            }

            if (GetLegacyGraphRecommendationDecision(candidate) != AbacusRecommendationDecisionValues.Approved)
            {
                continue;
            }

            legacyGraphRecommendationStates.Remove(candidate.CandidateId);
            legacyGraphRecommendationDecisions.Remove(candidate.CandidateId);
        }
    }

    private bool AreLegacyGraphCustomerRecommendationEndpointsInSameMergeGroup(
        AbacusRecommendationCandidate candidate)
    {
        if (candidate.SubjectKind != AbacusRecommendationEntityKinds.Customer ||
            candidate.TargetKind != AbacusRecommendationEntityKinds.Customer ||
            FindLegacyGraphCustomerById(candidate.SubjectId) is not { } subjectCustomer ||
            FindLegacyGraphCustomerById(candidate.TargetId) is not { } targetCustomer)
        {
            return false;
        }

        var subjectGroupKey = GetLegacyCustomerMergeKey(subjectCustomer);
        var targetGroupKey = GetLegacyCustomerMergeKey(targetCustomer);
        return string.Equals(subjectGroupKey, targetGroupKey, StringComparison.Ordinal) &&
               TryGetLegacyGraphMergeGroup(subjectGroupKey, out var group) &&
               group.CustomerIds.Count > 1 &&
               group.CustomerIds.Contains(subjectCustomer.CustomerId, StringComparer.Ordinal) &&
               group.CustomerIds.Contains(targetCustomer.CustomerId, StringComparer.Ordinal);
    }

    private void MoveLegacyGraphVehicleToTray(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var manualCustomerId = legacyGraphManualVehicleCustomerLinks.TryGetValue(
            vehicle.VehicleId,
            out var savedManualCustomerId)
            ? savedManualCustomerId
            : null;
        var targetCustomerId = manualCustomerId ??
            (vehicle.HasCustomer ? vehicle.CustomerId : null);
        var selectedCustomer = GetLegacyGraphListSelectedDisplayCustomer();
        var originalCustomer = targetCustomerId is null
            ? null
            : FindLegacyGraphCustomerById(targetCustomerId);
        var relatedDocumentStates = GetLegacyGraphDocumentsAffectedByVehicle(vehicle)
            .Select(CaptureLegacyGraphDetachedDocumentState)
            .ToArray();
        var undoState = new LegacyGraphDetachedUndoState(
            "vehicle",
            vehicle.VehicleId,
            manualCustomerId,
            legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId),
            relatedDocumentStates);

        InvalidateLegacyGraphApprovalForVehicle(vehicle);
        legacyGraphManualVehicleCustomerLinks.Remove(vehicle.VehicleId);
        legacyGraphTrayVehicleIds.Add(vehicle.VehicleId);

        // 車両を未確定へ戻した場合、その車両に属する書類も同時に未確定へ戻します。
        // 元のCSVやABACUSフォルダーは変更せず、画面上の一時状態だけを更新します。
        var relatedDocumentKeys = relatedDocumentStates
            .Select(state => state.DocumentKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var documentKey in relatedDocumentKeys)
        {
            legacyGraphManualDocumentLinks.Remove(documentKey);
            legacyGraphManualDocumentCustomerLinks.Remove(documentKey);
            legacyGraphDocumentLinkMethods.Remove(documentKey);
            legacyGraphDocumentLinkReasons.Remove(documentKey);
            legacyGraphUnconnectedDocumentKeys.Remove(documentKey);
            legacyGraphTrayDocumentKeys.Add(documentKey);
        }

        RebuildLegacyGraphRecommendationCandidates();

        ClearLegacyGraphBlockVisualDrag();
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphUnresolvedDocumentLists();
        var customer = selectedCustomer ??
            (targetCustomerId is null ? null : FindLegacyGraphCustomerById(targetCustomerId));
        if (customer is not null)
        {
            RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(customer));
            RenderLegacyGraphCustomer(GetLegacyGraphDisplayCustomer(customer));
        }

        legacyGraphSelectedItem = vehicle;
        UpdateLegacyGraphInspector(vehicle);
        LegacyGraphStatusText.Text =
            relatedDocumentKeys.Count > 0
                ? $"車両 {vehicle.DisplayName} と関連書類 {relatedDocumentKeys.Count:N0}件を未確定トレイへ戻しました。元データは変更していません。"
                : $"車両 {vehicle.DisplayName} を未確定車両トレイへ戻しました。元データは変更していません。";
        LegacyGraphStatusText.Foreground = ToBrush("#D97706");
        AddLegacyMatchingDetachedChange(
            $"vehicle:{vehicle.VehicleId}",
            "vehicle",
            vehicle.VehicleId,
            "取り外した項目",
            $"車両: {Fallback(vehicle.DisplayName)}\n" +
            $"↑\n顧客: {(originalCustomer is null ? "未設定" : GetLegacyGraphCustomerDisplayName(originalCustomer))}\n" +
            $"変更後: 未確定" +
            (relatedDocumentKeys.Count > 0
                ? $"（関連書類 {relatedDocumentKeys.Count:N0}件も未確定）"
                : "") +
            " / 元に戻せます。",
            undoState);
        if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            RefreshLegacyGraphCustomerList();
            RefreshLegacyMatchingView();
        }
    }

    private void ClearLegacyGraphManualVehicleCustomerLink(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        if (!legacyGraphManualVehicleCustomerLinks.TryGetValue(vehicle.VehicleId, out var customerId))
        {
            return;
        }

        var relatedDocuments = GetLegacyGraphDocumentsAffectedByVehicle(vehicle);
        InvalidateLegacyGraphApprovalForVehicle(vehicle);
        legacyGraphManualVehicleCustomerLinks.Remove(vehicle.VehicleId);
        legacyGraphTrayVehicleIds.Add(vehicle.VehicleId);
        foreach (var document in relatedDocuments)
        {
            var key = GetLegacyDocumentKey(document);
            if (legacyGraphManualDocumentLinks.TryGetValue(key, out var linkedVehicleId) &&
                LegacyGraphVehicleDetachState.IsManualDocumentLinkedToVehicle(
                    linkedVehicleId,
                    vehicle.VehicleId))
            {
                legacyGraphManualDocumentLinks.Remove(key);
                legacyGraphDocumentLinkMethods.Remove(key);
                legacyGraphDocumentLinkReasons.Remove(key);
            }

            legacyGraphManualDocumentCustomerLinks.Remove(key);
            legacyGraphUnconnectedDocumentKeys.Remove(key);
            legacyGraphTrayDocumentKeys.Add(key);
            legacyGraphExcludedDocumentKeys.Remove(key);
        }

        RebuildLegacyGraphRecommendationCandidates();
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphUnresolvedDocumentLists();
        var customer = FindLegacyGraphCustomerById(customerId);
        if (customer is not null)
        {
            RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(customer));
            RenderLegacyGraphCustomer(GetLegacyGraphDisplayCustomer(customer));
        }

        legacyGraphSelectedItem = vehicle;
        UpdateLegacyGraphInspector(vehicle);
        LegacyGraphStatusText.Text = $"未確定車両 {vehicle.DisplayName} を顧客から外し、未確定車両トレイへ戻しました。";
        LegacyGraphStatusText.Foreground = ToBrush("#D97706");
    }

    private void LegacyGraphUnresolvedDocumentList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        var item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not AbacusLegacyExportCandidateGraphDocument document)
        {
            return;
        }

        list.SelectedItem = document;
        legacyGraphTrayDragDocument = document;
        legacyGraphTrayDragStartPoint = e.GetPosition(this);
        // カードからポインターが出ても移動イベントを受け取り、しきい値到達時に確実にドラッグを開始する。
        list.CaptureMouse();
    }

    private void LegacyGraphUnresolvedDocumentList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list ||
            legacyGraphTrayDragDocument is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - legacyGraphTrayDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - legacyGraphTrayDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var document = legacyGraphTrayDragDocument;
        legacyGraphTrayDragDocument = null;
        legacyGraphNativeDocumentDropTargetValid = false;
        var payload = new LegacyGraphDocumentDragPayload(document, "unresolved-tray");
        var data = new DataObject();
        data.SetData(typeof(LegacyGraphDocumentDragPayload), payload);
        var dragSource = sender as UIElement;
        if (dragSource is not null)
        {
            dragSource.GiveFeedback += LegacyGraphDocumentDrag_GiveFeedback;
        }

        if (list.IsMouseCaptured)
        {
            list.ReleaseMouseCapture();
        }

        ShowLegacyGraphTrashOverlay();
        try
        {
            DragDrop.DoDragDrop(list, data, DragDropEffects.Link);
        }
        finally
        {
            if (list.IsMouseCaptured)
            {
                list.ReleaseMouseCapture();
            }

            if (dragSource is not null)
            {
                dragSource.GiveFeedback -= LegacyGraphDocumentDrag_GiveFeedback;
            }

            Mouse.OverrideCursor = null;
            Cursor = null;
            legacyGraphNativeDocumentDropTargetValid = false;
            HideLegacyGraphTrashOverlay();
        }
        e.Handled = true;
    }

    private void LegacyGraphDocumentDrag_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        var cursor = legacyGraphNativeDocumentDropTargetValid ? Cursors.Hand : Cursors.SizeAll;
        Mouse.OverrideCursor = cursor;
        Cursor = cursor;
        e.UseDefaultCursors = false;
        e.Handled = true;
    }

    private void LegacyGraphUnresolvedDocumentList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && list.IsMouseCaptured)
        {
            list.ReleaseMouseCapture();
        }

        legacyGraphTrayDragDocument = null;
    }

    private void LegacyGraphUnresolvedDocumentList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list ||
            GetLegacyGraphDocumentDragPayload(e.Data) is null)
        {
            legacyGraphNativeDocumentDropTargetValid = false;
            e.Effects = DragDropEffects.None;
            return;
        }

        ClearLegacyGraphTrayDropHighlight();
        legacyGraphTrayDropHighlightList = list;
        list.Opacity = 0.82;
        legacyGraphNativeDocumentDropTargetValid = true;
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphUnresolvedDocumentList_DragLeave(object sender, DragEventArgs e)
    {
        legacyGraphNativeDocumentDropTargetValid = false;
        ClearLegacyGraphTrayDropHighlight();
    }

    private void LegacyGraphUnresolvedDocumentList_Drop(object sender, DragEventArgs e)
    {
        ClearLegacyGraphTrayDropHighlight();
        if (GetLegacyGraphDocumentDragPayload(e.Data) is not { } payload)
        {
            return;
        }

        MoveLegacyGraphDocumentToTray(payload.Document);
        LegacyGraphStatusText.Text =
            $"{payload.Document.Kind} {Fallback(payload.Document.DocumentNumber)} を未確定トレイへ戻しました。元データは変更していません。";
        LegacyGraphStatusText.Foreground = ToBrush("#D97706");
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void ClearLegacyGraphTrayDropHighlight()
    {
        if (legacyGraphTrayDropHighlightList is null)
        {
            return;
        }

        legacyGraphTrayDropHighlightList.Opacity = 1;
        legacyGraphTrayDropHighlightList = null;
    }

    private void ClearLegacyGraphTrashDropHighlight()
    {
        if (legacyGraphTrashDropHighlightList is not null)
        {
            legacyGraphTrashDropHighlightList.Opacity = 1;
            legacyGraphTrashDropHighlightList = null;
        }

        if (LegacyGraphTrashOverlay.Visibility == Visibility.Visible)
        {
            SetLegacyGraphTrashOverlayHighlight(false);
        }
    }

    private void LegacyGraphTrashOverlay_DragOver(object sender, DragEventArgs e)
    {
        var valid = GetLegacyGraphTrashCustomerDragPayload(e.Data) is { } customerPayload &&
                    FindLegacyGraphCustomerById(customerPayload.CustomerId) is not null;
        valid |= GetLegacyGraphUnresolvedVehicleDragPayload(e.Data) is { } vehiclePayload &&
                 FindLegacyGraphVehicleById(vehiclePayload.VehicleId) is not null;
        valid |= GetLegacyGraphDocumentDragPayload(e.Data) is not null;
        if (!valid)
        {
            ClearLegacyGraphTrashDropHighlight();
            e.Effects = DragDropEffects.None;
            return;
        }

        ClearLegacyGraphTrashDropHighlight();
        SetLegacyGraphTrashOverlayHighlight(true);
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphTrashOverlay_DragLeave(object sender, DragEventArgs e)
    {
        ClearLegacyGraphTrashDropHighlight();
    }

    private void LegacyGraphTrashOverlay_Drop(object sender, DragEventArgs e)
    {
        ClearLegacyGraphTrashDropHighlight();
        if (GetLegacyGraphTrashCustomerDragPayload(e.Data) is { } customerPayload &&
            FindLegacyGraphCustomerById(customerPayload.CustomerId) is { } customer)
        {
            MoveLegacyGraphCustomerToTrash(customer);
        }
        else if (GetLegacyGraphUnresolvedVehicleDragPayload(e.Data) is { } vehiclePayload &&
                 FindLegacyGraphVehicleById(vehiclePayload.VehicleId) is { } vehicle)
        {
            MoveLegacyGraphVehicleToTrash(vehicle);
        }
        else if (GetLegacyGraphDocumentDragPayload(e.Data) is { } documentPayload)
        {
            MoveLegacyGraphDocumentToTrash(documentPayload.Document);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphTrashCustomerList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list || GetLegacyGraphTrashCustomerDragPayload(e.Data) is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        ClearLegacyGraphTrashDropHighlight();
        legacyGraphTrashDropHighlightList = list;
        list.Opacity = 0.82;
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphTrashVehicleList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list || GetLegacyGraphUnresolvedVehicleDragPayload(e.Data) is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        ClearLegacyGraphTrashDropHighlight();
        legacyGraphTrashDropHighlightList = list;
        list.Opacity = 0.82;
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphTrashDocumentList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list || GetLegacyGraphDocumentDragPayload(e.Data) is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        ClearLegacyGraphTrashDropHighlight();
        legacyGraphTrashDropHighlightList = list;
        list.Opacity = 0.82;
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphTrashList_DragLeave(object sender, DragEventArgs e)
    {
        ClearLegacyGraphTrashDropHighlight();
    }

    private void LegacyGraphTrashCustomerList_Drop(object sender, DragEventArgs e)
    {
        ClearLegacyGraphTrashDropHighlight();
        if (GetLegacyGraphTrashCustomerDragPayload(e.Data) is not { } payload ||
            FindLegacyGraphCustomerById(payload.CustomerId) is not { } customer)
        {
            return;
        }

        MoveLegacyGraphCustomerToTrash(customer);
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphTrashVehicleList_Drop(object sender, DragEventArgs e)
    {
        ClearLegacyGraphTrashDropHighlight();
        if (GetLegacyGraphUnresolvedVehicleDragPayload(e.Data) is not { } payload ||
            FindLegacyGraphVehicleById(payload.VehicleId) is not { } vehicle)
        {
            return;
        }

        MoveLegacyGraphVehicleToTrash(vehicle);
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphTrashDocumentList_Drop(object sender, DragEventArgs e)
    {
        ClearLegacyGraphTrashDropHighlight();
        if (GetLegacyGraphDocumentDragPayload(e.Data) is not { } payload)
        {
            return;
        }

        MoveLegacyGraphDocumentToTrash(payload.Document);
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphPageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindVisualAncestor<DataGrid>(source) is not null ||
            FindVisualAncestor<ListBox>(source) is not null)
        {
            // 一覧上のホイールはDataGrid/ListBox自身へ渡します。
            return;
        }

        var inspector = FindVisualAncestor<ScrollViewer>(source);
        if (ReferenceEquals(inspector, LegacyGraphInspectorScrollViewer))
        {
            LegacyGraphInspectorScrollViewer.UpdateLayout();
            if (TryScrollVertical(LegacyGraphInspectorScrollViewer, e.Delta))
            {
                e.Handled = true;
                return;
            }
        }

        var matchingPanel = FindVisualAncestor<ScrollViewer>(source);
        if (ReferenceEquals(matchingPanel, LegacyMatchingCurrentRecommendationScrollViewer) ||
            ReferenceEquals(matchingPanel, LegacyMatchingDetailsScrollViewer))
        {
            matchingPanel.UpdateLayout();
            // 内側のスクロールが先頭・末尾に達していても、ページ全体へ
            // ホイール操作を伝播させないようにします。
            TryScrollVertical(matchingPanel, e.Delta);
            e.Handled = true;
            return;
        }

        if (LegacyMatchingSidePanel.Visibility == Visibility.Visible &&
            LegacyMatchingSidePanel.IsMouseOver)
        {
            // 右パネル内のヘッダーやタブ上でも、ページ全体を動かしません。
            e.Handled = true;
            return;
        }

        // グラフ上のホイールは、グラフ内のScrollViewerではなくページを動かします。
        TryScrollLegacyGraphPageByWheel(e.Delta);
        e.Handled = true;
    }

    private void LegacyGraphScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindLegacyGraphBlock(source) is not null ||
            FindVisualAncestor<ScrollBar>(source) is not null)
        {
            return;
        }

        legacyGraphPanning = true;
        legacyGraphPanStartPoint = e.GetPosition(LegacyGraphScrollViewer);
        legacyGraphPanStartHorizontalOffset = LegacyGraphScrollViewer.HorizontalOffset;
        legacyGraphPanStartVerticalOffset = LegacyGraphScrollViewer.VerticalOffset;
        LegacyGraphScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void LegacyGraphScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!legacyGraphPanning || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(LegacyGraphScrollViewer);
        var desiredHorizontalOffset = legacyGraphPanStartHorizontalOffset +
            legacyGraphPanStartPoint.X - currentPoint.X;
        var desiredVerticalOffset = legacyGraphPanStartVerticalOffset +
            legacyGraphPanStartPoint.Y - currentPoint.Y;
        var constrainedOffset = ConstrainLegacyGraphPan(
            desiredHorizontalOffset,
            desiredVerticalOffset);
        LegacyGraphScrollViewer.ScrollToHorizontalOffset(constrainedOffset.X);
        LegacyGraphScrollViewer.ScrollToVerticalOffset(constrainedOffset.Y);
        e.Handled = true;
    }

    private void LegacyGraphScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!legacyGraphPanning)
        {
            return;
        }

        legacyGraphPanning = false;
        LegacyGraphScrollViewer.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void LegacyGraphInspectorScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindVisualAncestor<ScrollBar>(source) is not null ||
            FindVisualAncestor<Button>(source) is not null ||
            FindVisualAncestor<TextBox>(source) is not null ||
            FindVisualAncestor<ComboBox>(source) is not null)
        {
            return;
        }

        LegacyGraphInspectorScrollViewer.UpdateLayout();
        LegacyGraphPageScrollViewer.UpdateLayout();
        legacyGraphInspectorPanning = true;
        legacyGraphInspectorPanningPage = LegacyGraphInspectorScrollViewer.ScrollableHeight <= 0;
        legacyGraphInspectorPanStartPoint = e.GetPosition(LegacyGraphInspectorScrollViewer);
        legacyGraphInspectorPanStartVerticalOffset = LegacyGraphInspectorScrollViewer.VerticalOffset;
        legacyGraphInspectorPanStartPageOffset = LegacyGraphPageScrollViewer.VerticalOffset;
        LegacyGraphInspectorScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void LegacyGraphInspectorScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!legacyGraphInspectorPanning || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(LegacyGraphInspectorScrollViewer);
        var dragDelta = legacyGraphInspectorPanStartPoint.Y - currentPoint.Y;
        if (legacyGraphInspectorPanningPage)
        {
            LegacyGraphPageScrollViewer.ScrollToVerticalOffset(Math.Clamp(
                legacyGraphInspectorPanStartPageOffset + dragDelta,
                0,
                LegacyGraphPageScrollViewer.ScrollableHeight));
        }
        else
        {
            var desiredVerticalOffset = legacyGraphInspectorPanStartVerticalOffset + dragDelta;
            LegacyGraphInspectorScrollViewer.ScrollToVerticalOffset(Math.Clamp(
                desiredVerticalOffset,
                0,
                LegacyGraphInspectorScrollViewer.ScrollableHeight));
        }
        e.Handled = true;
    }

    private void LegacyGraphInspectorScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!legacyGraphInspectorPanning)
        {
            return;
        }

        legacyGraphInspectorPanning = false;
        legacyGraphInspectorPanningPage = false;
        LegacyGraphInspectorScrollViewer.ReleaseMouseCapture();
        e.Handled = true;
    }

    private bool TryScrollLegacyGraphPageByWheel(int delta)
    {
        LegacyGraphPageScrollViewer.UpdateLayout();
        return TryScrollVertical(LegacyGraphPageScrollViewer, delta);
    }

    private static bool TryScrollVertical(ScrollViewer scrollViewer, int delta)
    {
        if (scrollViewer.ScrollableHeight <= 0)
        {
            return false;
        }

        var currentOffset = scrollViewer.VerticalOffset;
        var nextOffset = Math.Clamp(
            currentOffset - delta,
            0,
            scrollViewer.ScrollableHeight);
        if (Math.Abs(nextOffset - currentOffset) <= double.Epsilon)
        {
            return false;
        }

        scrollViewer.ScrollToVerticalOffset(nextOffset);
        return true;
    }

    private void LegacyGraphCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindLegacyGraphBlock(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        legacyGraphPanning = true;
        legacyGraphPanStartPoint = e.GetPosition(LegacyGraphScrollViewer);
        legacyGraphPanStartHorizontalOffset = LegacyGraphScrollViewer.HorizontalOffset;
        legacyGraphPanStartVerticalOffset = LegacyGraphScrollViewer.VerticalOffset;
        LegacyGraphCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void LegacyGraphCanvas_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!legacyGraphPanning || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(LegacyGraphScrollViewer);
        var desiredHorizontalOffset = legacyGraphPanStartHorizontalOffset +
            legacyGraphPanStartPoint.X - currentPoint.X;
        var desiredVerticalOffset = legacyGraphPanStartVerticalOffset +
            legacyGraphPanStartPoint.Y - currentPoint.Y;
        var constrainedOffset = ConstrainLegacyGraphPan(
            desiredHorizontalOffset,
            desiredVerticalOffset);
        LegacyGraphScrollViewer.ScrollToHorizontalOffset(constrainedOffset.X);
        LegacyGraphScrollViewer.ScrollToVerticalOffset(constrainedOffset.Y);
        e.Handled = true;
    }

    private void LegacyGraphCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!legacyGraphPanning)
        {
            return;
        }

        legacyGraphPanning = false;
        LegacyGraphCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private Point ConstrainLegacyGraphPan(double desiredHorizontalOffset, double desiredVerticalOffset)
    {
        var maxHorizontalOffset = Math.Max(0, LegacyGraphScrollViewer.ScrollableWidth);
        var maxVerticalOffset = Math.Max(0, LegacyGraphScrollViewer.ScrollableHeight);
        var normalHorizontalOffset = Math.Clamp(desiredHorizontalOffset, 0, maxHorizontalOffset);
        var normalVerticalOffset = Math.Clamp(desiredVerticalOffset, 0, maxVerticalOffset);
        var viewportWidth = LegacyGraphScrollViewer.ViewportWidth;
        var viewportHeight = LegacyGraphScrollViewer.ViewportHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return new Point(normalHorizontalOffset, normalVerticalOffset);
        }

        const double minimumVisibleBlockPixels = 40;
        var blockRanges = LegacyGraphCanvas.Children
            .OfType<FrameworkElement>()
            .Select(block =>
            {
                var left = Canvas.GetLeft(block);
                var top = Canvas.GetTop(block);
                var width = GetLegacyGraphElementWidth(block);
                var height = GetLegacyGraphElementHeight(block);
                var minimumHorizontal = Math.Max(0, left - viewportWidth + minimumVisibleBlockPixels);
                var maximumHorizontal = Math.Min(maxHorizontalOffset, left + width - minimumVisibleBlockPixels);
                var minimumVertical = Math.Max(0, top - viewportHeight + minimumVisibleBlockPixels);
                var maximumVertical = Math.Min(maxVerticalOffset, top + height - minimumVisibleBlockPixels);
                return (minimumHorizontal, maximumHorizontal, minimumVertical, maximumVertical);
            })
            .Where(range => range.minimumHorizontal <= range.maximumHorizontal &&
                            range.minimumVertical <= range.maximumVertical)
            .ToArray();
        if (blockRanges.Length == 0)
        {
            return new Point(normalHorizontalOffset, normalVerticalOffset);
        }

        var best = blockRanges
            .Select(range =>
            {
                var horizontal = Math.Clamp(
                    normalHorizontalOffset,
                    range.minimumHorizontal,
                    range.maximumHorizontal);
                var vertical = Math.Clamp(
                    normalVerticalOffset,
                    range.minimumVertical,
                    range.maximumVertical);
                var distance = Math.Pow(horizontal - normalHorizontalOffset, 2) +
                    Math.Pow(vertical - normalVerticalOffset, 2);
                return (horizontal, vertical, distance);
            })
            .OrderBy(candidate => candidate.distance)
            .First();
        return new Point(best.horizontal, best.vertical);
    }

    private static DependencyObject? FindLegacyGraphBlock(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element &&
                (element.Tag is AbacusLegacyExportCandidateGraphCustomer ||
                 element.Tag is AbacusLegacyExportCandidateGraphVehicle ||
                 element.Tag is AbacusLegacyExportCandidateGraphDocument))
            {
                return current;
            }

            current = GetLegacyGraphParent(current);
        }

        return null;
    }

    private static DependencyObject? FindLegacyGraphNodeInteraction(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element &&
                (element.Tag is LegacyGraphDocumentNodeMarker or
                 LegacyGraphDocumentDisconnectMarker or
                 LegacyGraphVehicleNodeMarker))
            {
                return current;
            }

            current = GetLegacyGraphParent(current);
        }

        return null;
    }

    private static DependencyObject? GetLegacyGraphParent(DependencyObject source)
    {
        // TextBlock内のRunなどはVisualではなくContentElementです。
        // VisualTreeHelper.GetParentを直接呼ぶとInvalidOperationExceptionになるため、
        // WPFの要素種別に応じて親をたどります。
        if (source is Visual || source is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(source);
        }

        if (source is ContentElement contentElement)
        {
            return ContentOperations.GetParent(contentElement);
        }

        if (source is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent;
        }

        return LogicalTreeHelper.GetParent(source);
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = GetLegacyGraphParent(current);
        }

        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject? source)
        where T : DependencyObject
    {
        if (source is null)
        {
            return null;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(source);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void InitializeLegacyGraphCustomerMergeGroups()
    {
        legacyGraphCustomerMergeGroups.Clear();
        legacyGraphCustomerMergeGroupByCustomerId.Clear();
        if (legacyExportCandidateGraphResult is null)
        {
            return;
        }

        var sameNameGroups = legacyExportCandidateGraphResult.Customers
            .Where(customer => !string.IsNullOrWhiteSpace(NormalizeLegacyCustomerMergeText(GetLegacyGraphCustomerName(customer))))
            .GroupBy(customer => NormalizeLegacyCustomerMergeText(GetLegacyGraphCustomerName(customer)), StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        foreach (var group in sameNameGroups)
        {
            var groupId = $"same-name:{group.Key}";
            var mergeGroup = new LegacyGraphCustomerMergeGroup(
                groupId,
                "same-name",
                group.Select(customer => customer.CustomerId).ToList());
            legacyGraphCustomerMergeGroups[groupId] = mergeGroup;
            foreach (var customerId in mergeGroup.CustomerIds)
            {
                legacyGraphCustomerMergeGroupByCustomerId[customerId] = groupId;
            }
        }
    }

    private bool TryGetLegacyGraphMergeGroup(string groupKey, out LegacyGraphCustomerMergeGroup group) =>
        legacyGraphCustomerMergeGroups.TryGetValue(groupKey, out group!);

    private bool IsLegacyGraphCustomerGroupEntry(LegacyGraphCustomerListEntry entry) =>
        (entry.IsGroupHeader || !entry.IsMatchingEntry) &&
        !string.IsNullOrWhiteSpace(entry.GroupKey) &&
        TryGetLegacyGraphMergeGroup(entry.GroupKey, out var group) &&
        group.CustomerIds.Count > 1;

    private string? GetLegacyGraphSelectedWorkGroupKeyForRefresh()
    {
        if (!string.IsNullOrWhiteSpace(legacyGraphSelectedWorkGroupKey) &&
            TryGetLegacyGraphMergeGroup(legacyGraphSelectedWorkGroupKey, out var selectedGroup) &&
            selectedGroup.CustomerIds.Count > 1)
        {
            return legacyGraphSelectedWorkGroupKey;
        }

        if (LegacyGraphCustomersList.SelectedItem is LegacyGraphCustomerListEntry selectedEntry &&
            IsLegacyGraphCustomerGroupEntry(selectedEntry))
        {
            return selectedEntry.GroupKey;
        }

        if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphCustomer selectedCustomer)
        {
            var sourceCustomer = GetLegacyGraphSourceCustomer(selectedCustomer);
            var mergeKey = GetLegacyCustomerMergeKey(sourceCustomer);
            return TryGetLegacyGraphMergeGroup(mergeKey, out var group) && group.CustomerIds.Count > 1
                ? mergeKey
                : null;
        }

        return null;
    }

    private string? GetLegacyGraphPreferredCustomerListEntryId(
        IReadOnlyList<LegacyGraphCustomerListEntry> entries)
    {
        var currentEntry = LegacyGraphCustomersList.SelectedItem as LegacyGraphCustomerListEntry;
        if (currentEntry is not null &&
            entries.Any(entry => string.Equals(entry.EntryId, currentEntry.EntryId, StringComparison.Ordinal)))
        {
            return currentEntry.EntryId;
        }

        var groupKey = GetLegacyGraphSelectedWorkGroupKeyForRefresh();
        if (groupKey is null)
        {
            if (legacyGraphSelectedItem is not AbacusLegacyExportCandidateGraphCustomer selectedCustomer)
            {
                return null;
            }

            var sourceCustomer = GetLegacyGraphSourceCustomer(selectedCustomer);
            return entries.Any(entry => string.Equals(
                        entry.EntryId,
                        $"customer:{sourceCustomer.CustomerId}",
                        StringComparison.Ordinal))
                ? $"customer:{sourceCustomer.CustomerId}"
                : null;
        }

        if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphCustomer groupSelectedCustomer)
        {
            var sourceCustomer = GetLegacyGraphSourceCustomer(groupSelectedCustomer);
            var childEntry = entries.FirstOrDefault(entry =>
                !entry.IsGroupHeader &&
                string.Equals(entry.GroupKey, groupKey, StringComparison.Ordinal) &&
                string.Equals(entry.Customer.CustomerId, sourceCustomer.CustomerId, StringComparison.Ordinal));
            if (childEntry is not null)
            {
                return childEntry.EntryId;
            }
        }

        return entries.Any(entry => string.Equals(
                    entry.EntryId,
                    $"group:{groupKey}",
                    StringComparison.Ordinal))
            ? $"group:{groupKey}"
            : null;
    }

    private void RefreshLegacyGraphCustomerList(string? selectedEntryId = null)
    {
        if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            RefreshLegacyMatchingCustomerQueue();
            UpdateLegacyGraphCurrentCustomerSelectionText();
            UpdateLegacyGraphImportConfirmationButton();
            RefreshLegacyGraphSearchResults();
            RefreshLegacyMatchingMergeRemovalButton();
            return;
        }

        LegacyMatchingCustomerQueueList.ItemsSource = null;
        var entries = BuildLegacyGraphCustomerListEntries();
        var preferredEntryId = selectedEntryId ?? GetLegacyGraphPreferredCustomerListEntryId(entries);
        LegacyGraphCustomersList.ItemsSource = entries;
        if (entries.Count == 0)
        {
            LegacyGraphCustomersList.SelectedIndex = -1;
            legacyGraphSelectedWorkGroupKey = null;
            legacyGraphEdges.Clear();
            LegacyGraphCanvas.Children.Clear();
            LegacyGraphEdgesCanvas.Children.Clear();
            UpdateLegacyGraphCurrentCustomerSelectionText();
            UpdateLegacyGraphImportConfirmationButton();
            RefreshLegacyGraphSearchResults();
            RefreshLegacyMatchingMergeRemovalButton();
            return;
        }

        var selectedEntry = preferredEntryId is null
                ? entries[0]
                : entries.FirstOrDefault(entry => string.Equals(entry.EntryId, preferredEntryId, StringComparison.Ordinal));
        LegacyGraphCustomersList.SelectedItem = selectedEntry ?? entries[0];
        UpdateLegacyGraphCurrentCustomerSelectionText();
        UpdateLegacyGraphImportConfirmationButton();
        RefreshLegacyGraphSearchResults();
        RefreshLegacyMatchingMergeRemovalButton();
    }

    private void RefreshLegacyMatchingCustomerQueue()
    {
        var entries = BuildLegacyMatchingCustomerListEntries();
        legacyGraphRefreshingMatchingCustomerQueue = true;
        try
        {
            LegacyGraphCustomersList.ItemsSource = null;
            LegacyMatchingCustomerQueueList.ItemsSource = entries;
            if (entries.Count == 0)
            {
                LegacyMatchingCustomerQueueList.SelectedIndex = -1;
                legacyGraphEdges.Clear();
                LegacyGraphCanvas.Children.Clear();
                LegacyGraphEdgesCanvas.Children.Clear();
                return;
            }

            var focusCustomer = GetLegacyGraphMatchingCustomer();
            var focusSource = focusCustomer is null
                ? null
                : GetLegacyGraphSourceCustomer(focusCustomer);
            var focusGroupKey = focusSource is null
                ? null
                : GetLegacyCustomerMergeKey(focusSource);
            IReadOnlyCollection<string> focusGroupCustomerIds = focusGroupKey is not null &&
                                                                 TryGetLegacyGraphMergeGroup(focusGroupKey, out var focusGroup)
                ? focusGroup.CustomerIds
                : Array.Empty<string>();
            if (!LegacyGraphMatchingSelectionState.IsSelectedMemberStillInGroup(
                    legacyGraphMatchingSelectedMergeMemberCustomerId,
                    focusGroupCustomerIds) ||
                string.Equals(
                    legacyGraphMatchingSelectedMergeMemberCustomerId,
                    focusSource?.CustomerId,
                    StringComparison.Ordinal))
            {
                legacyGraphMatchingSelectedMergeMemberCustomerId = null;
            }

            var selectedRecommendationId = legacyGraphSelectedRecommendation?.CandidateId;
            var selectedRecommendationEntry = entries.FirstOrDefault(entry =>
                entry.MatchingRecommendation is not null &&
                string.Equals(
                    entry.MatchingRecommendation.CandidateId,
                    selectedRecommendationId,
                    StringComparison.OrdinalIgnoreCase));
            var selectedMemberEntry = entries.FirstOrDefault(entry =>
                IsLegacyGraphMatchingMergeMemberEntry(entry) &&
                string.Equals(
                    entry.Customer.CustomerId,
                    legacyGraphMatchingSelectedMergeMemberCustomerId,
                    StringComparison.Ordinal));
            LegacyMatchingCustomerQueueList.SelectedItem =
                selectedRecommendationEntry ??
                selectedMemberEntry ??
                entries.FirstOrDefault(entry => entry.IsMatchingFocus) ?? entries[0];
        }
        finally
        {
            legacyGraphRefreshingMatchingCustomerQueue = false;
        }
    }

    private bool IsLegacyGraphMatchingMergeMemberEntry(LegacyGraphCustomerListEntry entry)
    {
        if (entry.IsGroupHeader || !entry.IsMatchingGroupChild || entry.MatchingRecommendation is not null ||
            string.IsNullOrWhiteSpace(entry.GroupKey) ||
            !TryGetLegacyGraphMergeGroup(entry.GroupKey, out var group))
        {
            return false;
        }

        return group.CustomerIds.Count >= 2 &&
               group.CustomerIds.Contains(entry.Customer.CustomerId, StringComparer.Ordinal);
    }

    private IReadOnlyList<LegacyGraphCustomerListEntry> BuildLegacyMatchingCustomerListEntries()
    {
        var focusCustomer = GetLegacyGraphMatchingCustomer();
        if (focusCustomer is null)
        {
            return [];
        }

        // 統合後も、ここで表示する「基準顧客」は巡回開始時の顧客を維持します。
        var focusSource = focusCustomer;
        var focusSnapshot = GetLegacyGraphCustomerReviewSnapshot(focusSource);
        var focusApproved = string.Equals(
            focusSnapshot.Status,
            LegacyGraphCustomerReviewStateValues.Approved,
            StringComparison.Ordinal);
        var focusNeedsReview = string.Equals(
            focusSnapshot.Status,
            LegacyGraphCustomerReviewStateValues.NeedsReview,
            StringComparison.Ordinal);
        var entries = new List<LegacyGraphCustomerListEntry>();
        var logicalMembers = GetLegacyGraphLogicalCustomerMembers(focusSource).ToArray();
        var logicalKey = GetLegacyGraphLogicalCustomerKey(focusSource);
        var candidateGroupKey = GetLegacyCustomerMergeKey(focusSource);
        var candidateMembers = GetLegacyGraphCustomerMergeCandidates(focusSource).ToArray();
        var isLogicalGroup = logicalMembers.Length > 1 &&
                             IsLegacyGraphLogicalCustomerGroup(logicalKey);
        var isTemporaryGroup = !isLogicalGroup &&
                               candidateMembers.Length > 1 &&
                               TryGetLegacyGraphMergeGroup(candidateGroupKey, out var temporaryGroup) &&
                               string.Equals(temporaryGroup.Origin, "manual", StringComparison.Ordinal) &&
                               !legacyGraphAppliedCustomerMergeKeys.Contains(candidateGroupKey);
        if (isLogicalGroup || isTemporaryGroup)
        {
            var logicalSnapshot = GetLegacyGraphCustomerReviewSnapshot(focusSource);
            var logicalApproved = string.Equals(
                logicalSnapshot.Status,
                LegacyGraphCustomerReviewStateValues.Approved,
                StringComparison.Ordinal);
            var logicalNeedsReview = string.Equals(
                logicalSnapshot.Status,
                LegacyGraphCustomerReviewStateValues.NeedsReview,
                StringComparison.Ordinal);
            var logicalState = logicalApproved
                ? "確定済み"
                : logicalNeedsReview
                    ? "再確認待ち"
                    : logicalSnapshot.PendingCount == 0 && logicalSnapshot.HeldCount == 0
                        ? "確認待ち"
                        : "未確認";
            var groupKey = isLogicalGroup ? logicalKey : candidateGroupKey;
            var groupMembers = isLogicalGroup ? logicalMembers : candidateMembers;
            // 仮グループ／論理グループへの遷移で自動展開しません。
            // 明示的に展開した状態だけを保存済み表示状態として引き継ぎます。
            var expanded = legacyGraphCustomerGroupExpanded.GetValueOrDefault(groupKey, false);
            var logicalDisplayCustomer = GetLegacyGraphDisplayCustomer(focusSource);
            entries.Add(CreateLegacyMatchingGroupHeader(
                groupKey,
                focusSource,
                isLogicalGroup ? GetLegacyGraphCustomerDisplayName(logicalDisplayCustomer) : "統合候補",
                isLogicalGroup
                    ? $"{groupMembers.Length:N0}件 / {logicalState}"
                    : $"{groupMembers.Length:N0}件 / 統合候補",
                expanded,
                ToBrush(isLogicalGroup && logicalApproved ? "#EAF2FF" : "#FFF7ED"),
                ToBrush(isLogicalGroup && logicalApproved ? "#2563EB" : "#D97706"),
                ToBrush(isLogicalGroup && logicalApproved ? "#1D4ED8" : "#9A3412")));
            if (expanded)
            {
                foreach (var member in groupMembers)
                {
                    entries.Add(CreateLegacyMatchingCustomerListEntry(
                        member,
                        isLogicalGroup ? "統合元" : "構成顧客",
                        isLogicalGroup ? "統合元" : "統合候補",
                        ToBrush(isLogicalGroup ? "#F4F7FB" : "#FFF7ED"),
                        ToBrush(isLogicalGroup ? "#CBD5E1" : "#FDBA74"),
                        ToBrush(isLogicalGroup ? "#52647A" : "#9A3412"),
                        isFocus: string.Equals(
                            member.CustomerId,
                            focusSource.CustomerId,
                            StringComparison.Ordinal),
                        isGroupChild: true,
                        groupKeyOverride: groupKey,
                        summaryOverride: BuildLegacyMatchingCustomerIdentitySummary(member)));
                }
            }
        }
        else
        {
            entries.Add(CreateLegacyMatchingCustomerListEntry(
                focusCustomer,
                focusApproved ? "承認済み" : focusNeedsReview ? "再確認待ち" : "現在確認中",
                "基準顧客",
                ToBrush(focusApproved ? "#EAF2FF" : focusNeedsReview ? "#FFF7ED" : "#F4F7FB"),
                ToBrush(focusApproved ? "#2563EB" : focusNeedsReview ? "#D97706" : "#718096"),
                ToBrush(focusApproved ? "#1D4ED8" : focusNeedsReview ? "#9A3412" : "#52647A"),
                isFocus: true,
                summaryOverride: $"車両 {GetLegacyGraphVehiclesForDisplay(focusSource).Count:N0}台 / " +
                    $"書類 {GetLegacyGraphDisplayedDocumentCount(focusSource):N0}件"));
        }

        var allRecommendations = GetLegacyGraphMatchingRecommendations(focusSource);
        var customerRecommendations = allRecommendations
            .Where(candidate => LegacyMatchingCategoryKinds.GetKind(candidate) == LegacyMatchingCategoryKinds.Customer)
            .Select(candidate =>
            {
                var otherCustomer = GetLegacyGraphOtherCustomerForMatchingCandidate(candidate, focusSource);
                return (Recommendation: candidate, Customer: otherCustomer);
            })
            .Where(item => item.Customer is not null)
            .Select(item => (item.Recommendation, Customer: item.Customer!))
            .ToArray();
        var pendingRecommendations = customerRecommendations
            .Where(item => GetLegacyGraphRecommendationDecision(item.Recommendation) == AbacusRecommendationDecisionValues.Pending)
            .ToArray();
        var heldRecommendations = customerRecommendations
            .Where(item => GetLegacyGraphRecommendationDecision(item.Recommendation) == AbacusRecommendationDecisionValues.Hold)
            .ToArray();
        bool IsCurrentCandidate(AbacusRecommendationCandidate recommendation) =>
            legacyGraphSelectedRecommendation is not null &&
            string.Equals(
                legacyGraphSelectedRecommendation.CandidateId,
                recommendation.CandidateId,
                StringComparison.OrdinalIgnoreCase);

        void AddCustomerCandidateGroup(
            string groupKey,
            string groupTitle,
            string groupSummary,
            IReadOnlyList<(AbacusRecommendationCandidate Recommendation, AbacusLegacyExportCandidateGraphCustomer Customer)> items,
            Brush background,
            Brush border,
            Brush statusBrush,
            bool defaultExpanded)
        {
            if (items.Count == 0)
            {
                return;
            }

            var expanded = legacyGraphCustomerGroupExpanded.GetValueOrDefault(groupKey, defaultExpanded);
            entries.Add(CreateLegacyMatchingGroupHeader(
                groupKey,
                focusSource,
                groupTitle,
                groupSummary,
                expanded,
                background,
                border,
                statusBrush));
            if (expanded)
            {
                foreach (var item in items)
                {
                    entries.Add(CreateLegacyMatchingCustomerListEntry(
                        item.Customer,
                        IsCurrentCandidate(item.Recommendation) ? "現在確認中" : groupTitle,
                        GetLegacyMatchingCustomerCandidateOriginText(item.Recommendation),
                        background,
                        border,
                        statusBrush,
                        isFocus: false,
                        item.Recommendation,
                        isGroupChild: true,
                        groupKeyOverride: groupKey));
                }
            }
        }

        AddCustomerCandidateGroup(
            $"matching-pending:{focusSource.CustomerId}",
            $"未処理 {pendingRecommendations.Length:N0}件",
            "顧客統合候補",
            pendingRecommendations,
            ToBrush("#FFF7ED"),
            ToBrush("#FDBA74"),
            ToBrush("#9A3412"),
            defaultExpanded: true);
        AddCustomerCandidateGroup(
            $"matching-hold:{focusSource.CustomerId}",
            $"保留 {heldRecommendations.Length:N0}件",
            "顧客統合候補",
            heldRecommendations,
            ToBrush("#FFFBEB"),
            ToBrush("#FCD34D"),
            ToBrush("#92400E"),
            defaultExpanded: false);

        return entries;
    }

    private AbacusLegacyExportCandidateGraphCustomer? GetLegacyGraphOtherCustomerForMatchingCandidate(
        AbacusRecommendationCandidate candidate,
        AbacusLegacyExportCandidateGraphCustomer focusCustomer)
    {
        var focusCustomerIds = GetLegacyGraphLogicalCustomerMembers(focusCustomer)
            .Select(customer => customer.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        if (focusCustomerIds.Contains(candidate.SubjectId))
        {
            return FindLegacyGraphCustomerById(candidate.TargetId);
        }

        if (focusCustomerIds.Contains(candidate.TargetId))
        {
            return FindLegacyGraphCustomerById(candidate.SubjectId);
        }

        return null;
    }

    private static string GetLegacyMatchingCustomerCandidateOriginText(
        AbacusRecommendationCandidate recommendation) =>
        recommendation.IsManual || string.Equals(recommendation.Origin, "manual", StringComparison.OrdinalIgnoreCase)
            ? "手動追加"
            : string.Equals(recommendation.Origin, "automatic", StringComparison.OrdinalIgnoreCase)
                ? "自動統合候補"
                : "おすすめ";

    private LegacyGraphCustomerListEntry CreateLegacyMatchingGroupHeader(
        string groupKey,
        AbacusLegacyExportCandidateGraphCustomer customer,
        string displayName,
        string summary,
        bool expanded,
        Brush background,
        Brush border,
        Brush statusBrush) =>
        new(
            $"matching-group:{groupKey}",
            true,
            customer,
            displayName,
            summary,
            "展開して構成顧客を確認",
            groupKey,
            expanded ? "▼" : "▶",
            background,
            border,
            statusBrush,
            "確認対象を展開");

    private LegacyGraphCustomerListEntry CreateLegacyMatchingCustomerListEntry(
        AbacusLegacyExportCandidateGraphCustomer customer,
        string statusText,
        string addressSummary,
        Brush background,
        Brush border,
        Brush statusBrush,
        bool isFocus,
        AbacusRecommendationCandidate? recommendation = null,
        bool isGroupChild = false,
        string? groupKeyOverride = null,
        string? summaryOverride = null) =>
        new(
            isGroupChild
                ? $"matching-child:{recommendation?.CandidateId ?? customer.CustomerId}"
                : isFocus
                    ? $"matching-focus:{customer.CustomerId}"
                    : $"matching-customer:{customer.CustomerId}",
            false,
            customer,
            GetLegacyGraphCustomerDisplayName(customer),
            summaryOverride ??
                $"車両 {GetLegacyGraphVehiclesForDisplay(customer).Count:N0}台 / 書類 {GetLegacyGraphDisplayedDocumentCount(customer):N0}",
            addressSummary,
            groupKeyOverride ?? GetLegacyCustomerMergeKey(customer),
            "",
            background,
            border,
            statusBrush,
            statusText,
            recommendation,
            isFocus);

    private string BuildLegacyMatchingCustomerIdentitySummary(
        AbacusLegacyExportCandidateGraphCustomer customer) =>
        $"顧客番号: {Fallback(customer.CustomerNumber)} / ふりがな: {Fallback(customer.NameKana)} / " +
        $"住所: {Fallback(customer.Address)}";

    private void LegacyGraphSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RefreshLegacyGraphSearchResults();

    private void LegacyGraphSearchFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshLegacyGraphSearchResults();

    private void LegacyGraphSearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LegacyGraphSearchOpenButton.IsEnabled = LegacyGraphSearchResultsList.SelectedItem is LegacyGraphSearchResult;
        LegacyGraphSearchOpenMatchingButton.IsEnabled =
            LegacyGraphSearchResultsList.SelectedItem is LegacyGraphSearchResult result &&
            GetLegacyGraphMatchingCustomerForSearchItem(result.Item) is not null;
    }

    private void LegacyGraphSearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LegacyGraphSearchResultsList.SelectedItem is LegacyGraphSearchResult)
        {
            ShowLegacyGraphSearchResult();
            e.Handled = true;
        }
    }

    private void LegacyGraphSearchOpenButton_Click(object sender, RoutedEventArgs e) =>
        ShowLegacyGraphSearchResult();

    private void LegacyGraphSearchOpenMatchingButton_Click(object sender, RoutedEventArgs e)
    {
        if (LegacyGraphSearchResultsList.SelectedItem is not LegacyGraphSearchResult result ||
            GetLegacyGraphMatchingCustomerForSearchItem(result.Item) is not { } customer)
        {
            return;
        }

        SetLegacyGraphUiMode("matching");
        EnsureLegacyGraphMatchingCustomerSelection(
            customer,
            allowBaseCustomerChange: true);
        legacyGraphSelectedRecommendation = null;
        RefreshLegacyGraphCustomerList();
        RefreshLegacyMatchingView();
        LegacyGraphStatusText.Text = $"検索結果から顧客 {GetLegacyGraphCustomerDisplayName(customer)} をマッチングUIで表示しました。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private AbacusLegacyExportCandidateGraphCustomer? GetLegacyGraphMatchingCustomerForSearchItem(object item) =>
        item switch
        {
            AbacusLegacyExportCandidateGraphCustomer customer when !legacyGraphTrashCustomerIds.Contains(customer.CustomerId) =>
                customer,
            AbacusLegacyExportCandidateGraphVehicle vehicle when !IsLegacyGraphVehicleInTrash(vehicle) =>
                FindCurrentCustomerForVehicle(vehicle),
            AbacusLegacyExportCandidateGraphDocument document when !IsLegacyGraphDocumentInTrash(document) =>
                FindCurrentCustomerForDocument(document),
            _ => null,
        };

    private void RefreshLegacyGraphSearchResults()
    {
        if (LegacyGraphSearchResultsList is null || LegacyGraphSearchTextBox is null)
        {
            return;
        }

        if (legacyExportCandidateGraphResult is null)
        {
            LegacyGraphSearchResultsList.ItemsSource = null;
            LegacyGraphSearchOpenButton.IsEnabled = false;
            LegacyGraphSearchOpenMatchingButton.IsEnabled = false;
            LegacyGraphSearchStatusText.Text = "候補パッケージを読み込むと検索できます。";
            return;
        }

        var query = NormalizeLegacyGraphSearchText(LegacyGraphSearchTextBox.Text);
        var kindFilter = GetLegacyGraphSearchFilterValue(LegacyGraphSearchKindComboBox);
        var stateFilter = GetLegacyGraphSearchFilterValue(LegacyGraphSearchStateComboBox);
        var methodFilter = GetLegacyGraphSearchFilterValue(LegacyGraphSearchMethodComboBox);
        var results = BuildLegacyGraphSearchResults()
            .Where(result => kindFilter == "all" || result.TypeCode == kindFilter)
            .Where(result => stateFilter == "all" || result.StateCode == stateFilter)
            .Where(result => methodFilter == "all" || result.MethodCode == methodFilter)
            .Where(result => string.IsNullOrWhiteSpace(query) || result.SearchText.Contains(query, StringComparison.Ordinal))
            .OrderBy(result => result.TypeText, StringComparer.Ordinal)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToArray();

        LegacyGraphSearchResultsList.ItemsSource = results;
        LegacyGraphSearchResultsList.SelectedIndex = -1;
        LegacyGraphSearchOpenButton.IsEnabled = false;
        LegacyGraphSearchOpenMatchingButton.IsEnabled = false;
        LegacyGraphSearchStatusText.Text = results.Length == 500
            ? "検索結果が500件を超えたため、先頭500件だけ表示しています。検索条件を追加してください。"
            : $"検索結果: {results.Length:N0}件。結果を選択して表示できます。";
    }

    private IReadOnlyList<LegacyGraphSearchResult> BuildLegacyGraphSearchResults()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        var results = new List<LegacyGraphSearchResult>();
        foreach (var customer in legacyExportCandidateGraphResult.Customers)
        {
            var state = GetLegacyGraphSearchCustomerState(customer);
            var method = GetLegacyGraphSearchCustomerMethod(customer);
            results.Add(CreateLegacyGraphSearchResult(
                customer,
                "customer",
                "顧客",
                GetLegacyGraphCustomerDisplayName(customer),
                $"{Fallback(customer.NameKana)} / {Fallback(customer.PhoneNumber)} / 車両 {customer.Vehicles.Count:N0}台",
                state,
                method,
                new[]
                {
                    customer.CustomerId,
                    customer.CustomerNumber,
                    GetLegacyGraphCustomerName(customer),
                    customer.NameKana,
                    customer.PhoneNumber,
                    customer.EmailAddress,
                    customer.PostalCode,
                    customer.Address,
                    customer.Memo,
                }));
        }

        foreach (var vehicle in GetLegacyGraphAllVehicles())
        {
            var state = GetLegacyGraphSearchVehicleState(vehicle);
            var method = GetLegacyGraphSearchVehicleMethod(vehicle);
            results.Add(CreateLegacyGraphSearchResult(
                vehicle,
                "vehicle",
                "車両",
                $"{Fallback(vehicle.Maker)} {vehicle.DisplayName}",
                $"{Fallback(GetLegacyGraphCurrentVehicleOwnerName(vehicle))} / {Fallback(vehicle.IdentifierSummary)} / {vehicle.SourceLocation}",
                state,
                method,
                new[]
                {
                    vehicle.VehicleId,
                    GetLegacyGraphCurrentVehicleOwnerName(vehicle),
                    vehicle.Maker,
                    vehicle.VehicleName,
                    vehicle.ModelYear,
                    vehicle.InspectionDate,
                    vehicle.Mileage,
                    vehicle.RegistrationNumber,
                    vehicle.ChassisNumber,
                    vehicle.SourceFileName,
                    vehicle.SourceRowNumber.ToString(),
                }));
        }

        foreach (var document in legacyExportCandidateGraphResult.AllDocuments
                     .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            var state = GetLegacyGraphSearchDocumentState(document);
            var method = GetLegacyGraphSearchDocumentMethod(document);
            results.Add(CreateLegacyGraphSearchResult(
                document,
                "document",
                "書類",
                $"{document.Kind} {Fallback(document.DocumentNumber)}",
                $"{Fallback(document.CustomerName)} / {Fallback(document.VehicleName)} / {Fallback(document.DocumentDate)} / {Fallback(document.TotalAmount)}",
                state,
                method,
                new[]
                {
                    document.Kind,
                    document.SourceFileName,
                    document.SourceRowNumber.ToString(),
                    document.DocumentNumber,
                    document.CustomerName,
                    document.VehicleName,
                    document.RegistrationNumber,
                    document.DocumentDate,
                    document.TotalAmount,
                    document.DocumentType,
                    document.MaintenanceCategory,
                    document.ClassificationWarning,
                }));
        }

        return results;
    }

    private LegacyGraphSearchResult CreateLegacyGraphSearchResult(
        object item,
        string typeCode,
        string typeText,
        string title,
        string subtitle,
        (string Code, string Text) state,
        (string Code, string Text) method,
        IEnumerable<string> searchValues)
    {
        var stateBrush = state.Code switch
        {
            "trash" => ToBrush("#B91C1C"),
            "unresolved" => ToBrush("#C2410C"),
            _ => ToBrush("#17643A"),
        };
        var backgroundBrush = state.Code switch
        {
            "trash" => ToBrush("#FFF1F2"),
            "unresolved" => ToBrush("#FFF7ED"),
            _ => ToBrush("#F0FDF4"),
        };
        var borderBrush = state.Code switch
        {
            "trash" => ToBrush("#FCA5A5"),
            "unresolved" => ToBrush("#FDBA74"),
            _ => ToBrush("#86EFAC"),
        };
        return new LegacyGraphSearchResult(
            item,
            typeCode,
            typeText,
            title,
            subtitle,
            state.Code,
            state.Text,
            method.Code,
            method.Text,
            NormalizeLegacyGraphSearchText(string.Join("\n", searchValues)),
            stateBrush,
            backgroundBrush,
            borderBrush);
    }

    private void ShowLegacyGraphSearchResult()
    {
        if (LegacyGraphSearchResultsList.SelectedItem is not LegacyGraphSearchResult result)
        {
            return;
        }

        switch (result.Item)
        {
            case AbacusLegacyExportCandidateGraphCustomer customer:
                if (legacyGraphTrashCustomerIds.Contains(customer.CustomerId))
                {
                    LegacyGraphTrashCustomerList.SelectedItem = customer;
                    LegacyGraphTrashCustomerList.ScrollIntoView(customer);
                    UpdateLegacyGraphInspector(customer);
                    LegacyGraphStatusText.Text = $"検索結果からごみ箱の顧客 {GetLegacyGraphCustomerDisplayName(customer)} を表示しました。";
                }
                else
                {
                    if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
                    {
                        if (AddLegacyMatchingManualCustomerCandidate(customer))
                        {
                            LegacyGraphStatusText.Text =
                                $"顧客 {GetLegacyGraphCustomerDisplayName(customer)} を現在の顧客の未処理候補へ追加しました。";
                        }
                    }
                    else
                    {
                        SelectLegacyGraphCustomerInList(customer);
                        LegacyGraphStatusText.Text = $"検索結果から顧客 {GetLegacyGraphCustomerDisplayName(customer)} を表示しました。";
                    }
                }
                LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
                break;
            case AbacusLegacyExportCandidateGraphVehicle vehicle:
                ShowLegacyGraphSearchVehicle(vehicle);
                break;
            case AbacusLegacyExportCandidateGraphDocument document:
                ShowLegacyGraphSearchDocument(document);
                break;
        }
    }

    private bool AddLegacyMatchingManualCustomerCandidate(
        AbacusLegacyExportCandidateGraphCustomer targetCustomer)
    {
        if (!CanMutateLegacyGraph)
        {
            return false;
        }

        var focusCustomer = GetLegacyGraphMatchingCustomer();
        if (focusCustomer is null)
        {
            return false;
        }

        var sourceCustomer = GetLegacyGraphSourceCustomer(focusCustomer);
        var logicalMembers = GetLegacyGraphLogicalCustomerMembers(sourceCustomer)
            .Select(customer => customer.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        if (logicalMembers.Contains(targetCustomer.CustomerId) ||
            string.Equals(sourceCustomer.CustomerId, targetCustomer.CustomerId, StringComparison.Ordinal))
        {
            LegacyGraphStatusText.Text = "現在の論理顧客に含まれている顧客は、候補へ重複追加できません。";
            LegacyGraphStatusText.Foreground = ToBrush("#805B10");
            return false;
        }

        if (!LegacyGraphMutationState.TryAddManualCustomerCandidate(
                legacyGraphMatchingManualCustomerCandidateTargets,
                sourceCustomer.CustomerId,
                targetCustomer.CustomerId,
                legacyGraphBulkMergeBusy,
                legacyGraphFinalPackageBusy,
                legacyGraphResumeInProgress))
        {
            LegacyGraphStatusText.Text = "この顧客はすでに現在の候補一覧へ追加されています。";
            LegacyGraphStatusText.Foreground = ToBrush("#805B10");
            return false;
        }

        InvalidateLegacyGraphImportConfirmation();
        RebuildLegacyGraphRecommendationCandidates();
        RefreshLegacyGraphCustomerList();
        RefreshLegacyMatchingView();
        ScheduleLegacyGraphCheckpointSave();
        return true;
    }

    private void ShowLegacyGraphSearchVehicle(AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        if (IsLegacyGraphVehicleInTrash(vehicle))
        {
            LegacyGraphTrashVehicleList.SelectedItem = vehicle;
            LegacyGraphTrashVehicleList.ScrollIntoView(vehicle);
            UpdateLegacyGraphInspector(vehicle);
            LegacyGraphStatusText.Text = $"検索結果からごみ箱の車両 {vehicle.DisplayName} を表示しました。";
            LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
            return;
        }

        if (GetLegacyGraphUnresolvedVehicles().Any(candidate =>
                string.Equals(candidate.VehicleId, vehicle.VehicleId, StringComparison.Ordinal)))
        {
            LegacyGraphUnresolvedVehicleList.SelectedItem = vehicle;
            LegacyGraphUnresolvedVehicleList.ScrollIntoView(vehicle);
            UpdateLegacyGraphInspector(vehicle);
            LegacyGraphStatusText.Text = $"検索結果から未確定車両 {vehicle.DisplayName} を表示しました。";
            LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
            return;
        }

        var customer = FindCurrentCustomerForVehicle(vehicle);
        if (customer is not null)
        {
            SelectLegacyGraphCustomerInList(customer);
            var displayCustomer = GetLegacyGraphDisplayCustomer(customer);
            RenderLegacyGraphCustomer(displayCustomer);
        }

        UpdateLegacyGraphInspector(vehicle);
        LegacyGraphStatusText.Text = $"検索結果から車両 {vehicle.DisplayName} をキャンバスで表示しました。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private void ShowLegacyGraphSearchDocument(AbacusLegacyExportCandidateGraphDocument document)
    {
        if (IsLegacyGraphDocumentInTrash(document))
        {
            LegacyGraphTrashDocumentList.SelectedItem = document;
            LegacyGraphTrashDocumentList.ScrollIntoView(document);
            UpdateLegacyGraphInspector(document);
            LegacyGraphStatusText.Text = $"検索結果からごみ箱の書類 {document.DocumentNumber} を表示しました。";
            LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
            return;
        }

        if (IsLegacyGraphDocumentInTray(document))
        {
            var trayList = document.Kind == "販売書類"
                ? LegacyGraphUnresolvedSalesList
                : LegacyGraphUnresolvedMaintenanceList;
            trayList.SelectedItem = document;
            trayList.ScrollIntoView(document);
            UpdateLegacyGraphInspector(document);
            LegacyGraphStatusText.Text = $"検索結果から未確定トレイの書類 {document.DocumentNumber} を表示しました。";
            LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
            return;
        }

        var customer = FindCurrentCustomerForDocument(document);

        if (customer is not null)
        {
            SelectLegacyGraphCustomerInList(customer);
            RenderLegacyGraphCustomer(GetLegacyGraphDisplayCustomer(customer));
        }

        UpdateLegacyGraphInspector(document);
        LegacyGraphStatusText.Text = $"検索結果から書類 {document.DocumentNumber} をキャンバスで表示しました。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private static string GetLegacyGraphSearchFilterValue(ComboBox comboBox) =>
        comboBox.SelectedItem is ComboBoxItem item && item.Tag is string value
            ? value
            : "all";

    private static string NormalizeLegacyGraphSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        return string.Concat(normalized.Where(character =>
            !char.IsWhiteSpace(character) && !IsLegacyGraphSearchHyphen(character)));
    }

    private static bool IsLegacyGraphSearchHyphen(char character) =>
        character is '-' or '－' or '‐' or '‑' or '‒' or '–' or '—' or '−';

    private (string Code, string Text) GetLegacyGraphSearchCustomerState(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (legacyGraphTrashCustomerIds.Contains(customer.CustomerId))
        {
            return ("trash", "ごみ箱");
        }

        var key = GetLegacyCustomerMergeKey(customer);
        return TryGetLegacyGraphMergeGroup(key, out var group) &&
               group.CustomerIds.Count > 1 &&
               !legacyGraphAppliedCustomerMergeKeys.Contains(key)
            ? ("unresolved", "未確定")
            : ("confirmed", "確定");
    }

    private (string Code, string Text) GetLegacyGraphSearchCustomerMethod(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var key = GetLegacyCustomerMergeKey(customer);
        if (TryGetLegacyGraphMergeGroup(key, out var group) && group.Origin == "manual")
        {
            return ("manual", "手動紐づけ");
        }

        return TryGetLegacyGraphMergeGroup(key, out var candidateGroup) &&
               candidateGroup.CustomerIds.Count > 1 &&
               !legacyGraphAppliedCustomerMergeKeys.Contains(key)
            ? ("recommended", "おすすめ")
            : ("automatic", "自動紐づけ");
    }

    private (string Code, string Text) GetLegacyGraphSearchVehicleState(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        if (IsLegacyGraphVehicleInTrash(vehicle))
        {
            return ("trash", "ごみ箱");
        }

        var isUnresolved = legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId) ||
                           FindCurrentCustomerForVehicle(vehicle) is null;
        return isUnresolved ? ("unresolved", "未確定") : ("confirmed", "確定");
    }

    private (string Code, string Text) GetLegacyGraphSearchVehicleMethod(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        if (legacyGraphManualVehicleCustomerLinks.ContainsKey(vehicle.VehicleId))
        {
            return ("manual", "手動紐づけ");
        }

        return FindCurrentCustomerForVehicle(vehicle) is not null &&
               !legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId)
            ? ("automatic", "自動紐づけ")
            : ("recommended", "おすすめ");
    }

    private (string Code, string Text) GetLegacyGraphSearchDocumentState(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        if (IsLegacyGraphDocumentInTrash(document))
        {
            return ("trash", "ごみ箱");
        }

        return IsLegacyGraphDocumentInTray(document) || IsLegacyGraphDocumentUnconnected(document)
            ? ("unresolved", "未確定")
            : ("confirmed", "確定");
    }

    private (string Code, string Text) GetLegacyGraphSearchDocumentMethod(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        var method = GetLegacyGraphDocumentLinkMethod(document);
        if (method is "manual-vehicle" or "manual-customer-only")
        {
            return ("manual", "手動紐づけ");
        }

        return method == AbacusLinkMethods.Recommended || document.MatchStatus is "要確認" or "候補"
            ? ("recommended", "おすすめ")
            : ("automatic", "自動紐づけ");
    }

    private IReadOnlyList<LegacyGraphCustomerListEntry> BuildLegacyGraphCustomerListEntries()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        var entries = new List<LegacyGraphCustomerListEntry>();
        var visibleCustomers = legacyExportCandidateGraphResult.Customers
            .Where(customer => !legacyGraphTrashCustomerIds.Contains(customer.CustomerId))
            .ToArray();
        var visibleCustomerIds = visibleCustomers
            .Select(customer => customer.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        var groups = visibleCustomers
            .OrderBy(customer => GetLegacyGraphCustomerDisplayName(customer), StringComparer.OrdinalIgnoreCase)
            .GroupBy(GetLegacyCustomerMergeKey, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var groupKey = group.Key;
            var customers = group.ToArray();
            if (!TryGetLegacyGraphMergeGroup(groupKey, out var mergeGroup))
            {
                foreach (var customer in customers)
                {
                    entries.Add(CreateLegacyGraphCustomerListEntry(customer));
                }
                continue;
            }

            // 一覧は現在の所属グループを表示する。マップとグループの集合が
            // 一致しない状態はドラッグ処理の不整合なので、表示側で別グループへ
            // 寄せず、個別カードへ安全に戻す。
            var groupCustomerIds = mergeGroup.CustomerIds
                .Where(visibleCustomerIds.Contains)
                .ToHashSet(StringComparer.Ordinal);
            var mappedCustomerIds = customers
                .Select(customer => customer.CustomerId)
                .ToHashSet(StringComparer.Ordinal);
            if (groupCustomerIds.Count < 2 || !groupCustomerIds.SetEquals(mappedCustomerIds))
            {
                foreach (var customer in customers)
                {
                    entries.Add(CreateLegacyGraphCustomerListEntry(customer));
                }
                continue;
            }

            customers = groupCustomerIds
                .Select(FindLegacyGraphCustomerById)
                .Where(customer => customer is not null)
                .Cast<AbacusLegacyExportCandidateGraphCustomer>()
                .OrderBy(customer => GetLegacyGraphCustomerDisplayName(customer), StringComparer.OrdinalIgnoreCase)
                .ThenBy(customer => customer.CustomerId, StringComparer.Ordinal)
                .ToArray();
            if (customers.Length < 2)
            {
                foreach (var customer in group)
                {
                    entries.Add(CreateLegacyGraphCustomerListEntry(customer));
                }
                continue;
            }

            var hasDraft = HasCompleteLegacyGraphCustomerMergeDraft(
                groupKey,
                customers.Select(customer => customer.CustomerId).ToArray());
            var isApplied = IsLegacyGraphLogicalCustomerGroup(groupKey) ||
                            legacyGraphAppliedCustomerMergeKeys.Contains(groupKey);
            var representative = GetLegacyGraphDisplayCustomer(customers[0]);
            var customerReviewSnapshot = GetLegacyGraphCustomerReviewSnapshot(
                customers[0],
                useGraphStructuralMergeIntent: true);
            var isCustomerApproved = string.Equals(
                customerReviewSnapshot.Status,
                LegacyGraphCustomerReviewStateValues.Approved,
                StringComparison.Ordinal);
            var customerNeedsReview = string.Equals(
                customerReviewSnapshot.Status,
                LegacyGraphCustomerReviewStateValues.NeedsReview,
                StringComparison.Ordinal);
            var isFormalGroup = isApplied || IsLegacyGraphLogicalCustomerGroup(groupKey);
            var expanded = legacyGraphCustomerGroupExpanded.GetValueOrDefault(groupKey);
            var vehicleCount = GetLegacyGraphVehiclesForDisplay(representative).Count;
            var documentCount = GetLegacyGraphDisplayedDocumentCount(representative);
            var (background, border, statusBrush, statusText) = isCustomerApproved
                ? ("#EAF2FF", "#2563EB", "#1D4ED8", "顧客確認済み")
                : customerNeedsReview
                    ? ("#FFF7ED", "#D97706", "#9A3412", "再確認待ち")
                : isFormalGroup
                    ? ("#FFF7ED", "#D97706", "#9A3412", "顧客確認待ち")
                : hasDraft
                    ? ("#FFF7ED", "#D97706", "#9A3412", "顧客情報選択済み")
                    : ("#FFF7ED", "#D97706", "#9A3412", "統合候補");
            var groupDisplayName = isFormalGroup
                ? GetLegacyGraphCustomerDisplayName(representative)
                : "統合候補";
            entries.Add(new LegacyGraphCustomerListEntry(
                $"group:{groupKey}",
                true,
                representative,
                groupDisplayName,
                $"{(isFormalGroup ? "構成顧客" : "構成候補")} {customers.Length:N0}件 / 車両 {vehicleCount:N0}台 / 書類 {documentCount:N0}件",
                isCustomerApproved
                    ? "顧客情報・構成を確認済み"
                    : customerNeedsReview
                        ? "構成または採用値が変更されたため再確認が必要です"
                    : isFormalGroup
                        ? "論理顧客の構成を確認して『この顧客を確定』してください"
                    : hasDraft
                        ? "統合後の顧客情報を選択済み。『この顧客を確定』で完了"
                        : "統合候補を展開して顧客情報をプレビュー",
                groupKey,
                expanded ? "▼" : "▶",
                ToBrush(background),
                ToBrush(border),
                ToBrush(statusBrush),
                statusText));

            if (!expanded)
            {
                continue;
            }

            foreach (var customer in customers)
            {
                entries.Add(new LegacyGraphCustomerListEntry(
                    $"customer:{customer.CustomerId}",
                    false,
                    customer,
                    GetLegacyGraphCustomerDisplayName(customer),
                    $"車両 {GetLegacyGraphVehiclesForDisplay(customer).Count:N0}台 / 書類 {GetLegacyGraphDisplayedDocumentCount(customer):N0}件",
                    string.IsNullOrWhiteSpace(customer.Address) ? "住所未設定" : customer.Address,
                    groupKey,
                    "",
                    ToBrush("#FFFFFF"),
                    ToBrush("#D8E1EC"),
                    ToBrush("#718096"),
                    isFormalGroup
                        ? "構成顧客"
                        : mergeGroup.Origin == "same-name" ? "統合候補" : "手動追加候補"));
            }
        }

        return entries;
    }

    private LegacyGraphCustomerListEntry CreateLegacyGraphCustomerListEntry(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var customerReviewSnapshot = GetLegacyGraphCustomerReviewSnapshot(
            customer,
            useGraphStructuralMergeIntent: true);
        var isCustomerApproved = string.Equals(
            customerReviewSnapshot.Status,
            LegacyGraphCustomerReviewStateValues.Approved,
            StringComparison.Ordinal);
        var customerNeedsReview = string.Equals(
            customerReviewSnapshot.Status,
            LegacyGraphCustomerReviewStateValues.NeedsReview,
            StringComparison.Ordinal);
        return new(
            $"customer:{customer.CustomerId}",
            false,
            customer,
            GetLegacyGraphCustomerDisplayName(customer),
            $"車両 {GetLegacyGraphVehiclesForDisplay(customer).Count:N0}台 / 書類 {GetLegacyGraphDisplayedDocumentCount(customer):N0}件",
            string.IsNullOrWhiteSpace(customer.Address) ? "住所未設定" : customer.Address,
            GetLegacyCustomerMergeKey(customer),
            "",
            ToBrush(isCustomerApproved ? "#EAF2FF" : customerNeedsReview ? "#FFF7ED" : "#FFFFFF"),
            ToBrush(isCustomerApproved ? "#2563EB" : customerNeedsReview ? "#D97706" : "#D8E1EC"),
            ToBrush(isCustomerApproved ? "#1D4ED8" : customerNeedsReview ? "#9A3412" : "#718096"),
            isCustomerApproved ? "顧客確認済み" : customerNeedsReview ? "再確認待ち" : "通常・確認待ち");
    }

    private void SelectLegacyGraphCustomerInList(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var groupKey = GetLegacyCustomerMergeKey(customer);
        var hasGroup = TryGetLegacyGraphMergeGroup(groupKey, out var group) &&
                       group.CustomerIds.Count > 1;
        if (hasGroup)
        {
            legacyGraphCustomerGroupExpanded[groupKey] = true;
            legacyGraphSelectedWorkGroupKey = groupKey;
        }
        else
        {
            legacyGraphSelectedWorkGroupKey = null;
        }

        RefreshLegacyGraphCustomerList(hasGroup
            ? $"group:{groupKey}"
            : $"customer:{GetLegacyGraphSourceCustomer(customer).CustomerId}");
        ScheduleLegacyGraphCheckpointSave();
    }

    private void UpdateLegacyGraphInspector(object? selected)
    {
        legacyGraphSelectedItem = selected;
        LegacyGraphInspectorTabs.SelectedIndex = 0;
        LegacyGraphInspectorSourcesItemsControl.ItemsSource = null;
        LegacyGraphInspectorDifferencesItemsControl.ItemsSource = null;
        LegacyGraphInspectorSourcesPlaceholderText.Text = "顧客を選択すると、統合元候補を表示します。";
        LegacyGraphInspectorDifferencesPlaceholderText.Text = "顧客を選択すると、項目ごとの差分と採用根拠を表示します。";
        LegacyGraphInspectorStateText.Text = "";
        LegacyGraphInspectorStateText.Foreground = ToBrush("#52647A");
        LegacyGraphInspectorStateBorder.Background = ToBrush("#F4F7FB");
        LegacyGraphMergeButton.IsEnabled = false;
        LegacyGraphMergeButton.Visibility = Visibility.Collapsed;
        LegacyGraphMergeButton.Content = "顧客情報をプレビュー";
        LegacyGraphCustomerApprovalReasonText.Text = "";
        LegacyGraphCustomerApprovalReasonText.Visibility = Visibility.Collapsed;
        LegacyGraphCustomerApproveButton.IsEnabled = false;
        LegacyGraphCustomerApproveButton.Visibility = Visibility.Collapsed;
        LegacyGraphCustomerApproveButton.Content = "この顧客を確定";
        LegacyGraphEditCustomerNameButton.IsEnabled = false;
        LegacyGraphEditCustomerNameButton.Visibility = Visibility.Collapsed;
        LegacyGraphCustomerMergeStatusText.Text = "";
        LegacyGraphReassignButton.Visibility = Visibility.Collapsed;
        LegacyGraphReassignButton.IsEnabled = selected is AbacusLegacyExportCandidateGraphDocument documentForButton &&
            FindManualLinkedVehicle(documentForButton) is not null;
        LegacyGraphReassignButton.Visibility = selected is AbacusLegacyExportCandidateGraphVehicle vehicleForButton &&
            legacyGraphManualVehicleCustomerLinks.ContainsKey(vehicleForButton.VehicleId)
                ? Visibility.Visible
                : selected is AbacusLegacyExportCandidateGraphDocument documentForVisibility &&
                  FindManualLinkedVehicle(documentForVisibility) is not null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        LegacyGraphReassignButton.Content = selected is AbacusLegacyExportCandidateGraphVehicle
            ? "顧客への接続を解除"
            : "仮紐付けを解除";
        switch (selected)
        {
            case AbacusLegacyExportCandidateGraphCustomer customer:
                var customerSource = GetLegacyGraphSourceCustomer(customer);
                LegacyGraphEditCustomerNameButton.IsEnabled = true;
                LegacyGraphEditCustomerNameButton.Visibility = Visibility.Visible;
                var mergeCandidates = GetLegacyGraphCustomerMergeCandidates(customer);
                var mergeKey = GetLegacyCustomerMergeKey(customer);
                var hasMergeIntent = HasLegacyGraphStructuralMergeIntent(customerSource, mergeCandidates);
                var hasMergeDraft = hasMergeIntent &&
                                    HasCompleteLegacyGraphCustomerMergeDraft(
                                        mergeKey,
                                        mergeCandidates.Select(candidate => candidate.CustomerId).ToArray());
                var isAppliedMerge = IsLegacyGraphLogicalCustomerGroup(mergeKey) ||
                                     legacyGraphAppliedCustomerMergeKeys.Contains(mergeKey);
                var customerReviewSnapshot = GetLegacyGraphCustomerReviewSnapshot(
                    customerSource,
                    useGraphStructuralMergeIntent: true);
                var isCustomerApproved = string.Equals(
                    customerReviewSnapshot.Status,
                    LegacyGraphCustomerReviewStateValues.Approved,
                    StringComparison.Ordinal);
                var customerNeedsReview = string.Equals(
                    customerReviewSnapshot.Status,
                    LegacyGraphCustomerReviewStateValues.NeedsReview,
                    StringComparison.Ordinal);
                var hasMergeGroup = TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup) &&
                                    mergeGroup.CustomerIds.Count > 1 &&
                                    hasMergeIntent;
                legacyGraphCustomerMergeDrafts.TryGetValue(mergeKey, out var mergeDraft);
                var trayDocuments = GetLegacyGraphTrayDocuments(customer);
                var excludedTrayDocumentCount = trayDocuments.Count(document =>
                    legacyGraphExcludedDocumentKeys.Contains(GetLegacyDocumentKey(document)));
                RefreshLegacyGraphCustomerApproval(customerSource);
                RefreshLegacyMatchingMergeRemovalButton();
                var isManualMerge = mergeGroup?.Origin == "manual";
                LegacyGraphInspectorStateText.Text = isCustomerApproved
                    ? "● 顧客確認済み"
                    : customerNeedsReview
                        ? "● 顧客再確認待ち"
                    : isAppliedMerge
                        ? "● 統合済み・顧客確認待ち"
                    : hasMergeDraft
                        ? "● 顧客情報選択済み・確定時に統合"
                        : hasMergeGroup
                            ? "● 統合候補（顧客情報をプレビューしてください）"
                            : "● 顧客確認待ち";
                LegacyGraphInspectorStateText.Foreground = ToBrush(isCustomerApproved
                    ? "#1D4ED8"
                    : customerNeedsReview || isAppliedMerge || hasMergeDraft || hasMergeGroup
                        ? "#9A3412"
                        : "#52647A");
                LegacyGraphInspectorStateBorder.Background = ToBrush(isCustomerApproved
                    ? "#EAF2FF"
                    : customerNeedsReview || isAppliedMerge || hasMergeDraft || hasMergeGroup
                        ? "#FFF7ED"
                        : "#F4F7FB");
                LegacyGraphInspectorTitleText.Text = $"顧客: {GetLegacyGraphCustomerDisplayName(customer)}";
                LegacyGraphInspectorStatusText.Text =
                    (isCustomerApproved ? "顧客確認済み\n" : customerNeedsReview ? "顧客再確認待ち\n" :
                        isAppliedMerge ? "統合済み・顧客確認待ち\n" :
                        hasMergeGroup ? "統合候補・顧客情報確認待ち\n" : "顧客確認待ち\n") +
                    $"顧客ID: {customer.CustomerId}\n車両 {GetLegacyGraphVehiclesForDisplay(customer).Count:N0}台 / 書類 {GetLegacyGraphDisplayedDocumentCount(customer):N0}件" +
                    (trayDocuments.Count > 0
                        ? $"\n未確定トレイ: {trayDocuments.Count:N0}件（{excludedTrayDocumentCount:N0}件は除外確定 / 残りはインポート確定時に除外）"
                        : "");
                LegacyGraphInspectorDetailsText.Text =
                    $"顧客番号: {Fallback(customer.CustomerNumber)}\n" +
                    $"ふりがな: {Fallback(customer.NameKana)}\n" +
                    $"電話番号: {Fallback(customer.PhoneNumber)}\n" +
                    $"メール: {Fallback(customer.EmailAddress)}\n" +
                    $"郵便番号: {Fallback(customer.PostalCode)}\n" +
                    $"住所: {Fallback(customer.Address)}\n" +
                    $"メモ: {Fallback(customer.Memo)}";
                LegacyGraphInspectorEvidenceText.Text = isAppliedMerge
                    ? "顧客CSVの複数候補を、保存済みの統合プレビューに従って表示上の論理顧客へまとめています。元の顧客ID・CSV・ABACUSフォルダーは変更していません。"
                    : isManualMerge
                        ? "顧客カードのドラッグで追加された候補です。比較画面で項目ごとに採用値を選択して保存します。顧客の統合・確定は『この顧客を確定』で行います。"
                        : "顧客CSVの1行を表示しています。顧客名だけが一致する候補は自動統合せず、比較画面で項目ごとに採用値を選択して保存します。顧客の統合・確定は『この顧客を確定』で行います。";
                LegacyGraphCustomerMergeStatusText.Text = hasMergeGroup
                    ? hasMergeDraft
                        ? isCustomerApproved
                            ? $"統合候補 {mergeCandidates.Count}件\n顧客情報：設定済み\n顧客確認済みです。"
                            : isAppliedMerge
                                ? $"統合候補 {mergeCandidates.Count}件\n顧客情報：設定済み\n統合済みです。顧客単位で最終確認してください。"
                                : $"統合候補 {mergeCandidates.Count}件\n顧客情報：設定済み\n採用内容を再確認できます。統合反映は『この顧客を確定』で行います。"
                        : $"統合候補 {mergeCandidates.Count}件\n顧客情報：未設定\n統合後に使用する顧客情報を決定してください。"
                    : "統合候補はありません。\n現在の内容に問題がなければ、この顧客を確定してください。";
                UpdateLegacyGraphCustomerInspectorTabs(
                    customer,
                    mergeCandidates,
                    hasMergeDraft ? mergeDraft : null,
                    isCustomerApproved);
                break;
            case AbacusLegacyExportCandidateGraphVehicle vehicle:
                var manualVehicleCustomerId = legacyGraphManualVehicleCustomerLinks.TryGetValue(vehicle.VehicleId, out var linkedCustomerId)
                    ? linkedCustomerId
                    : null;
                var linkedCustomer = manualVehicleCustomerId is null ? null : FindLegacyGraphCustomerById(manualVehicleCustomerId);
                var currentVehicleCustomer = FindCurrentCustomerForVehicle(vehicle);
                var isVehicleInTray = legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId);
                LegacyGraphReassignButton.IsEnabled = linkedCustomer is not null;
                LegacyGraphReassignButton.Visibility = linkedCustomer is null ? Visibility.Collapsed : Visibility.Visible;
                LegacyGraphReassignButton.Content = "顧客への接続を解除";
                LegacyGraphInspectorStateText.Text = isVehicleInTray
                    ? "● 未確定トレイの車両を選択中"
                    : currentVehicleCustomer is null
                        ? "● 未確定車両を選択中"
                        : linkedCustomer is not null
                            ? "● 未確定車両を顧客へ手動接続中"
                            : "● 顧客へ接続済みの車両を選択中";
                LegacyGraphCustomerMergeStatusText.Text = "顧客ブロックを選択すると、同名顧客の比較候補を表示します。";
                LegacyGraphInspectorTitleText.Text = $"車両: {Fallback(vehicle.Maker)} {vehicle.DisplayName}";
                LegacyGraphInspectorStatusText.Text =
                    $"車両ID: {vehicle.VehicleId}\n書類 {GetDocumentsForVehicle(vehicle).Count:N0}件\n" +
                    (isVehicleInTray
                        ? "最終パッケージ: 未確定トレイのため除外"
                        : currentVehicleCustomer is null
                        ? "最終パッケージ: 未接続のため除外"
                        : $"接続先顧客: {GetLegacyGraphCustomerDisplayName(currentVehicleCustomer)}");
                LegacyGraphInspectorDetailsText.Text =
                    $"顧客: {Fallback(GetLegacyGraphCurrentVehicleOwnerName(vehicle))}\n" +
                    $"登録番号: {Fallback(vehicle.RegistrationNumber)}\n" +
                    $"車台番号: {Fallback(vehicle.ChassisNumber)}\n" +
                    $"年式: {Fallback(vehicle.ModelYear)}\n" +
                    $"車検満了日: {Fallback(vehicle.InspectionDate)}\n" +
                    $"走行距離: {Fallback(vehicle.Mileage)}\n" +
                    $"出典: {vehicle.SourceLocation}\n" +
                    "画像: 次段階で候補画像を表示";
                LegacyGraphInspectorEvidenceText.Text =
                    (isVehicleInTray
                        ? "ユーザーが車両カードを未確定車両トレイへ移動しました。車両と関連書類は最終パッケージから除外されます。"
                        : linkedCustomer is null
                        ? vehicle.Warning
                        : $"ユーザーが顧客 {linkedCustomer.DisplayName} へ手動接続しました。") +
                    "\n登録番号・車台番号は書類と画像の候補判定にも使用します。";
                break;
            case AbacusLegacyExportCandidateGraphDocument document:
                var isUnconnectedDocument = IsLegacyGraphDocumentUnconnected(document);
                var isTrayDocument = IsLegacyGraphDocumentInTray(document);
                var isCustomerDirectDocument = IsLegacyGraphCustomerDirectDocument(document);
                var isManualCustomerDocument = legacyGraphManualDocumentCustomerLinks.ContainsKey(GetLegacyDocumentKey(document));
                var linkMethod = GetLegacyGraphDocumentLinkMethod(document);
                var linkReason = GetLegacyGraphDocumentLinkReason(document);
                LegacyGraphInspectorStateText.Text = isUnconnectedDocument
                    ? "● 書類を選択中（未接続）"
                    : isTrayDocument
                        ? "● 未確定トレイの書類を選択中"
                        : isManualCustomerDocument
                            ? "● 書類を選択中（顧客へ仮紐付け）"
                        : isCustomerDirectDocument
                            ? "● 書類を選択中（車両未確定）"
                        : "● 書類を選択中";
                LegacyGraphCustomerMergeStatusText.Text = "顧客ブロックを選択すると、同名顧客の比較候補を表示します。";
                LegacyGraphInspectorTitleText.Text = $"{document.Kind}: {Fallback(document.DocumentNumber)}";
                var manualVehicle = FindManualLinkedVehicle(document);
                LegacyGraphReassignButton.IsEnabled = manualVehicle is not null;
                LegacyGraphInspectorStatusText.Text = isUnconnectedDocument
                    ? "判定: 未接続（画面上の一時状態）\n車両ノードへ接続するか、書類カードを未確定トレイへ移動してください。"
                    : isTrayDocument
                        ? $"判定: 未確定トレイ\nインポート: {(legacyGraphExcludedDocumentKeys.Contains(GetLegacyDocumentKey(document)) ? "除外確定" : "インポート確定時に除外")}\n候補車両: {document.CandidateSummary}"
                    : isManualCustomerDocument
                            ? $"判定: 顧客へ手動仮紐付け（車両なし）\n紐づけ方法: {linkMethod}\nキャンバス承認前の画面上の一時状態です。"
                        : isCustomerDirectDocument
                            ? $"判定: 顧客に一意紐付け（車両情報なしの特例）\n紐づけ方法: {linkMethod}\n候補車両: {document.CandidateSummary}\nこのまま確定すると顧客だけへ保存します。必要なら車両ノードへ接続できます。"
                        : manualVehicle is null
                            ? $"判定: {document.MatchStatus}\n紐づけ方法: {linkMethod}\n候補車両: {document.CandidateSummary}"
                    : $"判定: 手動仮紐付け（未登録）\n紐づけ方法: {linkMethod}\n紐付け先: {manualVehicle.DisplayName}";
                LegacyGraphInspectorDetailsText.Text =
                    $"顧客: {Fallback(document.CustomerName)}\n" +
                    $"車名: {Fallback(document.VehicleName)}\n" +
                    $"登録番号: {Fallback(document.RegistrationNumber)}\n" +
                    $"日付: {Fallback(document.DocumentDate)}\n" +
                    $"合計: {Fallback(document.TotalAmount)}\n" +
                    BuildLegacyGraphDocumentDetailSummary(document) +
                    $"出典: {document.SourceLocation}\n" +
                    $"元候補ID: {GetLegacyDocumentKey(document)}\n" +
                    $"判断根拠: {linkReason}";
                var manualEvidence = isUnconnectedDocument
                    ? "\nこの書類はノード接続がありません。キャンバス承認前に車両へ接続するか、未確定トレイへ移動してください。"
                    : isManualCustomerDocument
                        ? "\n車両情報がない書類を顧客へ直接仮紐付けしています。必要なら車両ノードへ接続するか、未確定トレイへ戻してください。"
                    : isCustomerDirectDocument
                        ? "\n顧客には一意に紐付いています。車両ノードへ接続しない場合は、車両情報なしの互換特例として顧客だけへ保存します。"
                    : manualVehicle is null
                        ? ""
                    : $"\n手動で選択した車両ID: {manualVehicle.VehicleId}\nこの変更は画面上の仮紐付けです。元CSV・ABACUSフォルダーは変更していません。";
                LegacyGraphInspectorEvidenceText.Text =
                    $"{Fallback(document.Warning)}\n候補車両ID: {(document.CandidateVehicleIds.Count == 0 ? "なし" : string.Join(", ", document.CandidateVehicleIds))}" +
                    manualEvidence;
                break;
            default:
                LegacyGraphInspectorStateText.Text = "顧客・車両・書類を選択してください。";
                LegacyGraphCustomerMergeStatusText.Text = "";
                LegacyGraphInspectorTitleText.Text = "顧客を選択してください";
                LegacyGraphInspectorStatusText.Text = "";
                LegacyGraphInspectorDetailsText.Text = "";
                LegacyGraphInspectorEvidenceText.Text = "";
                break;
        }
    }

    private static string BuildLegacyGraphDocumentDetailSummary(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        var documentType = AbacusDocumentClassification.NormalizeDocumentType(
            document.DocumentType,
            "請求書");
        var text =
            $"ABACUS書類種別（原文）: {Fallback(documentType.RawValue)}\n" +
            $"ABACUS書類種別（正規化）: {documentType.Value}\n";
        if (document.Kind == "整備書類")
        {
            var maintenanceCategory = AbacusDocumentClassification.NormalizeMaintenanceCategory(
                document.MaintenanceCategory);
            text +=
                $"ABACUS入庫区分（原文）: {Fallback(maintenanceCategory.RawValue)}\n" +
                $"ABACUS入庫区分（正規化）: {maintenanceCategory.Value}\n";
        }

        if (string.IsNullOrWhiteSpace(document.DetailsJson))
        {
            return text + "Gate19明細: 取得できない（DetailsJsonなし）\n";
        }

        try
        {
            var detail = JsonSerializer.Deserialize<AbacusDetailJsonDocument>(
                document.DetailsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (detail is null)
            {
                return text + "Gate19明細: 解析できない形式です\n";
            }

            return text +
                   $"Gate19明細: {detail.Lines.Count:N0}行 / 金額のみ {detail.AmountOnlyRowCount:N0}行 / 除外 {detail.ExcludedDetailCount:N0}行\n" +
                   $"明細合計: {detail.DetailAmount:N0} / ABACUS小計: {detail.AbacusSubtotal?.ToString("N0") ?? "未設定"} / ABACUS合計: {detail.AbacusTotal?.ToString("N0") ?? "未設定"}\n";
        }
        catch (JsonException)
        {
            return text + "Gate19明細: JSONを解析できません\n";
        }
    }

    private void UpdateLegacyGraphCustomerInspectorTabs(
        AbacusLegacyExportCandidateGraphCustomer selectedCustomer,
        IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> candidates,
        LegacyGraphCustomerMergeDraft? draft,
        bool isCustomerApproved)
    {
        var mergeKey = GetLegacyCustomerMergeKey(selectedCustomer);
        var originLabel = TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup) &&
                          mergeGroup.Origin == "manual"
            ? "手動追加候補"
            : "同名候補";
        LegacyGraphInspectorSourcesPlaceholderText.Text = candidates.Count > 1
            ? $"{originLabel} {candidates.Count:N0}件。各候補のID・住所・車両台数を確認できます。"
            : "この顧客に統合元候補はありません。";
        LegacyGraphInspectorSourcesItemsControl.ItemsSource = candidates
            .Select((candidate, index) => new LegacyGraphCustomerSourceSummary(
                $"候補 {index + 1}: {candidate.DisplayName}",
                $"顧客ID: {candidate.CustomerId}\n" +
                $"車両 {candidate.Vehicles.Count:N0}台 / 書類 {candidate.Documents:N0}件\n" +
                $"住所: {Fallback(candidate.Address)}\n" +
                $"電話: {Fallback(candidate.PhoneNumber)}",
                $"顧客番号: {Fallback(candidate.CustomerNumber)} / ふりがな: {Fallback(candidate.NameKana)}",
                ToBrush(index == 0 && isCustomerApproved ? "#EAF2FF" : "#FFFFFF"),
                ToBrush(index == 0 && isCustomerApproved ? "#2563EB" : "#D8E1EC")))
            .ToArray();

        var fields = GetLegacyGraphCustomerMergeFields();
        var differenceRows = fields
            .Select(field =>
            {
                var values = candidates
                    .Select(candidate => field.ValueSelector(candidate))
                    .Select(Fallback)
                    .ToArray();
                var distinctValues = values
                    .Where(value => !string.Equals(value, "未設定", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var hasDifference = distinctValues.Length > 1;
                var selectedValue = draft?.SelectedValues.TryGetValue(field.Key, out var savedValue) == true
                    ? Fallback(savedValue)
                    : hasDifference
                        ? "未選択"
                        : values.FirstOrDefault() ?? "未設定";
                var selectedCandidateName = draft?.FieldSelections.TryGetValue(field.Key, out var selectedCandidateId) == true
                    ? candidates.FirstOrDefault(candidate =>
                        string.Equals(candidate.CustomerId, selectedCandidateId, StringComparison.Ordinal))?.DisplayName
                    : null;
                var statusText = draft is not null
                    ? $"採用元: {Fallback(selectedCandidateName ?? "候補未選択")}" +
                      (isCustomerApproved ? " / 顧客確認済み" : " / 保存済み")
                    : hasDifference
                        ? "候補間に差異があります。比較画面で採用候補を選択してください。"
                        : "候補間で一致しています。";
                var background = draft is not null
                    ? isCustomerApproved ? "#EAF2FF" : "#FFF7ED"
                    : hasDifference ? "#FFF7ED" : "#EEF7F1";
                var border = draft is not null
                    ? isCustomerApproved ? "#2563EB" : "#D97706"
                    : hasDifference ? "#F59E0B" : "#B8DFC5";
                var statusBrush = draft is not null
                    ? isCustomerApproved ? "#1D4ED8" : "#9A3412"
                    : hasDifference ? "#9A3412" : "#17643A";
                return new LegacyGraphCustomerDifferenceSummary(
                    field.Label,
                    string.Join(" / ", values.Select((value, index) => $"候補{index + 1}: {value}")),
                    $"採用値: {selectedValue}",
                    statusText,
                    ToBrush(background),
                    ToBrush(border),
                    ToBrush(statusBrush),
                    hasDifference);
            })
            .OrderByDescending(row => row.HasDifference)
            .ToArray();
        LegacyGraphInspectorDifferencesPlaceholderText.Text = differenceRows.Any(row => row.HasDifference)
            ? "差異のある項目を上から表示しています。採用元と採用値は統合プレビューに記録されます。"
            : $"{originLabel}の顧客情報に大きな差異はありません。判定根拠は下部に表示します。";
        LegacyGraphInspectorDifferencesItemsControl.ItemsSource = differenceRows;
    }

    private AbacusLegacyExportCandidateGraphVehicle? FindManualLinkedVehicle(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        if (legacyExportCandidateGraphResult is null ||
            !legacyGraphManualDocumentLinks.TryGetValue(GetLegacyDocumentKey(document), out var vehicleId))
        {
            return null;
        }

        return FindLegacyGraphVehicleById(vehicleId);
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> GetLegacyGraphCustomerMergeCandidates(
        AbacusLegacyExportCandidateGraphCustomer selectedCustomer)
    {
        var mergeKey = GetLegacyCustomerMergeKey(selectedCustomer);
        if (legacyExportCandidateGraphResult is null ||
            !TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup))
        {
            return [selectedCustomer];
        }

        return legacyExportCandidateGraphResult.Customers
            .Where(customer => !legacyGraphTrashCustomerIds.Contains(customer.CustomerId))
            .Where(customer => mergeGroup.CustomerIds.Contains(customer.CustomerId, StringComparer.Ordinal))
            .OrderBy(customer => string.Equals(customer.CustomerId, selectedCustomer.CustomerId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(customer => customer.CustomerId, StringComparer.Ordinal)
            .ToArray();
    }

    private bool HasLegacyMatchingCustomerMergeIntent(
        AbacusLegacyExportCandidateGraphCustomer customer,
        IReadOnlyCollection<AbacusLegacyExportCandidateGraphCustomer> mergeCandidates)
    {
        if (mergeCandidates.Count < 2)
        {
            return false;
        }

        var mergeKey = GetLegacyCustomerMergeKey(customer);
        if (IsLegacyGraphLogicalCustomerGroup(mergeKey) ||
            legacyGraphAppliedCustomerMergeKeys.Contains(mergeKey))
        {
            return true;
        }

        if (TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup) &&
            string.Equals(mergeGroup.Origin, "manual", StringComparison.Ordinal))
        {
            return true;
        }

        // 同名候補は初期表示時点で構造上のグループを持ちますが、
        // マッチングUIで当該顧客に関係する推薦をすべて拒否した場合は、
        // 顧客情報の統合意思が残っていないため、普通の顧客として扱います。
        var customerMergeRecommendations = GetLegacyGraphMatchingRecommendations(
                customer,
                includeCompletedCustomerCandidates: true)
            .Where(candidate => LegacyMatchingCategoryKinds.GetKind(candidate) ==
                                LegacyMatchingCategoryKinds.Customer)
            .ToArray();
        return !LegacyMatchingWorkflow.AreAllCandidatesRejected(
            customerMergeRecommendations,
            legacyGraphRecommendationDecisions);
    }

    private bool HasLegacyGraphStructuralMergeIntent(
        AbacusLegacyExportCandidateGraphCustomer customer,
        IReadOnlyCollection<AbacusLegacyExportCandidateGraphCustomer> mergeCandidates)
    {
        if (mergeCandidates.Count < 2)
        {
            return false;
        }

        var mergeKey = GetLegacyCustomerMergeKey(customer);
        if (!TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup))
        {
            return false;
        }

        var activeMemberCount = mergeGroup.CustomerIds.Count(customerId =>
            !legacyGraphTrashCustomerIds.Contains(customerId) &&
            FindLegacyGraphCustomerById(customerId) is not null);
        return LegacyGraphTemporaryMergeGroupState.HasActiveMembership(activeMemberCount);
    }

    private string GetLegacyGraphLogicalCustomerKey(
        AbacusLegacyExportCandidateGraphCustomer customer) =>
        legacyGraphLogicalCustomerMergeGroupByCustomerId.TryGetValue(customer.CustomerId, out var groupKey)
            ? groupKey
            : $"customer:{customer.CustomerId}";

    private bool IsLegacyGraphLogicalCustomerGroup(string groupKey) =>
        groupKey.StartsWith("logical:", StringComparison.Ordinal) ||
        legacyGraphLogicalCustomerMergeGroupByCustomerId.Values.Contains(groupKey, StringComparer.Ordinal);

    private IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> GetLegacyGraphLogicalCustomerMembers(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var logicalKey = GetLegacyGraphLogicalCustomerKey(customer);
        if (!legacyGraphCustomerMergeGroups.TryGetValue(logicalKey, out var group))
        {
            return [customer];
        }

        return group.CustomerIds
            .Where(customerId => !legacyGraphTrashCustomerIds.Contains(customerId))
            .Select(FindLegacyGraphCustomerById)
            .Where(item => item is not null)
            .Cast<AbacusLegacyExportCandidateGraphCustomer>()
            .OrderBy(item => string.Equals(item.CustomerId, customer.CustomerId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(item => item.CustomerId, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> GetLegacyGraphCandidateGroupMembers(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var candidateKey = legacyGraphCustomerMergeGroupByCustomerId.TryGetValue(
            customer.CustomerId,
            out var mappedKey)
            ? mappedKey
            : $"customer:{customer.CustomerId}";
        if (!legacyGraphCustomerMergeGroups.TryGetValue(candidateKey, out var group))
        {
            return [customer];
        }

        return group.CustomerIds
            .Where(customerId => !legacyGraphTrashCustomerIds.Contains(customerId))
            .Select(FindLegacyGraphCustomerById)
            .Where(item => item is not null)
            .Cast<AbacusLegacyExportCandidateGraphCustomer>()
            .OrderBy(item => string.Equals(item.CustomerId, customer.CustomerId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(item => item.CustomerId, StringComparer.Ordinal)
            .ToArray();
    }

    private void MigrateLegacyGraphRecommendationScopes(
        IReadOnlySet<string> previousWorkTargetKeys,
        string newWorkTargetKey,
        IReadOnlySet<string> newWorkTargetCustomerIds)
    {
        if (previousWorkTargetKeys.Count == 0)
        {
            return;
        }

        foreach (var pair in legacyGraphRecommendationStates.ToArray())
        {
            var state = pair.Value;
            if (state.Lifecycle != LegacyGraphRecommendationLifecycle.Active ||
                string.IsNullOrWhiteSpace(state.WorkTargetKey) ||
                string.IsNullOrWhiteSpace(state.ExternalCustomerId) ||
                !previousWorkTargetKeys.Contains(state.WorkTargetKey))
            {
                continue;
            }

            if (newWorkTargetCustomerIds.Contains(state.ExternalCustomerId))
            {
                legacyGraphRecommendationStates[pair.Key] = state with
                {
                    Lifecycle = LegacyGraphRecommendationLifecycle.Obsolete,
                    ResolutionReason = "候補顧客を同じ論理顧客グループへ統合したため、別人判定を解消しました。",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                legacyGraphRecommendationDecisions.Remove(pair.Key);
                continue;
            }

            legacyGraphRecommendationStates[pair.Key] = state with
            {
                WorkTargetKey = newWorkTargetKey,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        foreach (var index in Enumerable.Range(0, legacyGraphMatchingChanges.Count).Reverse())
        {
            var change = legacyGraphMatchingChanges[index];
            if (change.Kind != "recommendation" ||
                string.IsNullOrWhiteSpace(change.WorkTargetKey) ||
                string.IsNullOrWhiteSpace(change.ExternalCustomerId) ||
                !previousWorkTargetKeys.Contains(change.WorkTargetKey))
            {
                continue;
            }

            if (newWorkTargetCustomerIds.Contains(change.ExternalCustomerId))
            {
                legacyGraphMatchingChanges.RemoveAt(index);
                continue;
            }

            var migratedScope = new LegacyCustomerRecommendationScope(
                newWorkTargetKey,
                change.ExternalCustomerId);
            legacyGraphMatchingChanges[index] = change with
            {
                ChangeId = BuildLegacyGraphRecommendationScopeChangeId(migratedScope),
                WorkTargetKey = migratedScope.WorkTargetKey,
            };
        }
    }

    private string AcceptLegacyGraphCustomerMerge(
        AbacusLegacyExportCandidateGraphCustomer sourceCustomer,
        AbacusLegacyExportCandidateGraphCustomer targetCustomer)
    {
        // 統合確定はデータ状態の変更であり、アコーディオンの開閉状態は変更しません。
        // 仮グループから論理顧客グループへIDが変わる場合だけ、直前の開閉状態を引き継ぎます。
        var sourceMergeKey = GetLegacyCustomerMergeKey(sourceCustomer);
        var targetMergeKey = GetLegacyCustomerMergeKey(targetCustomer);
        var wasExpanded = legacyGraphCustomerGroupExpanded.GetValueOrDefault(sourceMergeKey) ||
                          legacyGraphCustomerGroupExpanded.GetValueOrDefault(targetMergeKey);
        var sourceKey = GetLegacyGraphLogicalCustomerKey(sourceCustomer);
        var targetKey = GetLegacyGraphLogicalCustomerKey(targetCustomer);
        var previousRecommendationScopeKeys = new[]
                {
                    sourceMergeKey,
                    targetMergeKey,
                    sourceKey,
                    targetKey,
                }
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);
        HydrateLegacyGraphRecommendationScopes(previousRecommendationScopeKeys);
        var sourceGroup = legacyGraphCustomerMergeGroups.TryGetValue(sourceKey, out var existingSourceGroup) &&
                          IsLegacyGraphLogicalCustomerGroup(sourceKey)
            ? existingSourceGroup
            : null;
        var targetGroup = legacyGraphCustomerMergeGroups.TryGetValue(targetKey, out var existingTargetGroup) &&
                          IsLegacyGraphLogicalCustomerGroup(targetKey)
            ? existingTargetGroup
            : null;
        var affectedCustomerIds = new HashSet<string>(StringComparer.Ordinal)
        {
            sourceCustomer.CustomerId,
            targetCustomer.CustomerId,
        };
        if (sourceGroup is not null)
        {
            affectedCustomerIds.UnionWith(sourceGroup.CustomerIds);
        }

        if (targetGroup is not null)
        {
            affectedCustomerIds.UnionWith(targetGroup.CustomerIds);
        }

        var wasApproved = IsLegacyGraphCustomerReviewApprovedForKey(sourceMergeKey) ||
                          IsLegacyGraphCustomerReviewApprovedForKey(targetMergeKey) ||
                          IsLegacyGraphCustomerReviewApprovedForKey(sourceKey) ||
                          IsLegacyGraphCustomerReviewApprovedForKey(targetKey) ||
                          sourceGroup is not null &&
                          IsLegacyGraphCustomerReviewApprovedForKey(sourceGroup.GroupId) ||
                          targetGroup is not null &&
                          IsLegacyGraphCustomerReviewApprovedForKey(targetGroup.GroupId);
        var previousReviewKeys = new[]
                {
                    sourceKey,
                    targetKey,
                    sourceMergeKey,
                    targetMergeKey,
                    sourceGroup?.GroupId,
                    targetGroup?.GroupId,
                }
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);
        var wasNeedsReview = previousReviewKeys.Any(key =>
            string.Equals(
                legacyGraphCustomerReviewStates.GetValueOrDefault(key),
                LegacyGraphCustomerReviewStateValues.NeedsReview,
                StringComparison.Ordinal));
        var mergeRecommendations = legacyGraphRecommendationCandidates
            .Where(candidate =>
                candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
                candidate.TargetKind == AbacusRecommendationEntityKinds.Customer &&
                affectedCustomerIds.Contains(candidate.SubjectId) &&
                affectedCustomerIds.Contains(candidate.TargetId) &&
                IsLegacyGraphRecommendationActive(candidate) &&
                GetLegacyGraphRecommendationDecision(candidate) is
                    AbacusRecommendationDecisionValues.Pending or
                    AbacusRecommendationDecisionValues.Hold)
            .ToArray();
        var mergeGroup = sourceGroup ?? targetGroup;
        if (mergeGroup is null)
        {
            mergeGroup = new LegacyGraphCustomerMergeGroup(
                $"logical:{Guid.NewGuid():N}",
                "logical",
                []);
            legacyGraphCustomerMergeGroups[mergeGroup.GroupId] = mergeGroup;
        }

        if (sourceGroup is not null && targetGroup is not null &&
            !string.Equals(sourceGroup.GroupId, targetGroup.GroupId, StringComparison.Ordinal))
        {
            foreach (var customerId in targetGroup.CustomerIds)
            {
                if (!mergeGroup.CustomerIds.Contains(customerId, StringComparer.Ordinal))
                {
                    mergeGroup.CustomerIds.Add(customerId);
                }

                legacyGraphLogicalCustomerMergeGroupByCustomerId[customerId] = mergeGroup.GroupId;
            }

            legacyGraphCustomerMergeGroups.Remove(targetGroup.GroupId);
            legacyGraphCustomerGroupExpanded.Remove(targetGroup.GroupId);
        }

        foreach (var customerId in new[] { sourceCustomer.CustomerId, targetCustomer.CustomerId })
        {
            if (!mergeGroup.CustomerIds.Contains(customerId, StringComparer.Ordinal))
            {
                mergeGroup.CustomerIds.Add(customerId);
            }

            legacyGraphLogicalCustomerMergeGroupByCustomerId[customerId] = mergeGroup.GroupId;
        }

        if (wasExpanded)
        {
            legacyGraphCustomerGroupExpanded[mergeGroup.GroupId] = true;
        }
        else
        {
            legacyGraphCustomerGroupExpanded.Remove(mergeGroup.GroupId);
        }
        var previousGroupKeys = previousRecommendationScopeKeys
            .Concat(sourceGroup is null ? [] : [sourceGroup.GroupId])
            .Concat(targetGroup is null ? [] : [targetGroup.GroupId])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        MigrateLegacyGraphGroupReferences(previousGroupKeys, mergeGroup.GroupId);
        legacyGraphCustomerApprovalStates.Remove(sourceKey);
        legacyGraphCustomerApprovalStates.Remove(targetKey);
        legacyGraphCustomerApprovalStates.Remove(mergeGroup.GroupId);
        legacyGraphCustomerReviewStates.Remove(sourceKey);
        legacyGraphCustomerReviewStates.Remove(targetKey);
        legacyGraphCustomerReviewStates.Remove(mergeGroup.GroupId);
        foreach (var recommendation in mergeRecommendations)
        {
            SetLegacyGraphRecommendationState(
                recommendation,
                AbacusRecommendationDecisionValues.Approved);
        }

        if ((wasApproved || wasNeedsReview) &&
            FindLegacyGraphCustomerById(sourceCustomer.CustomerId) is { } affectedCustomer)
        {
            SetLegacyGraphCustomerReviewState(
                affectedCustomer,
                LegacyGraphCustomerReviewStateValues.NeedsReview);
        }

        // 統合確定後は、採用した論理顧客グループだけを所属先として残します。
        // 同姓同名などの仮候補グループに同じ顧客が残ると、作業保存後の
        // 再開時に二重所属として検証に失敗し、左一覧の構成も不定になります。
        foreach (var customerId in mergeGroup.CustomerIds.ToArray())
        {
            RemoveLegacyGraphCustomerFromOtherMergeGroups(customerId, mergeGroup.GroupId);
        }

        MigrateLegacyGraphRecommendationScopes(
            previousRecommendationScopeKeys,
            mergeGroup.GroupId,
            mergeGroup.CustomerIds.ToHashSet(StringComparer.Ordinal));

        InvalidateLegacyGraphImportConfirmation();
        RebuildLegacyGraphRecommendationCandidatesForCustomers(
            mergeGroup.CustomerIds.ToHashSet(StringComparer.Ordinal));
        return mergeGroup.GroupId;
    }

    private void MigrateLegacyGraphGroupReferences(
        IReadOnlySet<string> oldGroupKeys,
        string newGroupKey)
    {
        foreach (var documentKey in legacyGraphManualDocumentCustomerLinks.Keys.ToArray())
        {
            if (oldGroupKeys.Contains(legacyGraphManualDocumentCustomerLinks[documentKey]))
            {
                legacyGraphManualDocumentCustomerLinks[documentKey] = newGroupKey;
            }
        }

        // 「今回の変更」に保存したUndoStateも、通常の顧客直結リンクと同じ
        // 現在の論理顧客グループを参照し続ける必要があります。
        for (var index = 0; index < legacyGraphMatchingChanges.Count; index++)
        {
            var change = legacyGraphMatchingChanges[index];
            if (change.UndoState is not { } undoState)
            {
                continue;
            }

            var migratedUndoState = LegacyGraphDetachedUndoStateMigration.MigrateCustomerGroupReferences(
                undoState,
                oldGroupKeys,
                newGroupKey);
            if (!ReferenceEquals(migratedUndoState, undoState))
            {
                legacyGraphMatchingChanges[index] = change with { UndoState = migratedUndoState };
            }
        }

        MigrateLegacyGraphApprovalStateKeys(oldGroupKeys, newGroupKey);
        MigrateLegacyGraphReviewStateKeys(oldGroupKeys, newGroupKey);
        MigrateLegacyGraphStringKeySet(oldGroupKeys, newGroupKey, legacyGraphAppliedCustomerMergeKeys);
        var wasExpanded = oldGroupKeys.Any(key =>
            legacyGraphCustomerGroupExpanded.GetValueOrDefault(key));
        foreach (var oldKey in oldGroupKeys.Where(key => !string.Equals(key, newGroupKey, StringComparison.Ordinal)))
        {
            legacyGraphCustomerGroupExpanded.Remove(oldKey);
        }

        if (wasExpanded)
        {
            legacyGraphCustomerGroupExpanded[newGroupKey] = true;
        }

        foreach (var virtualCustomerId in legacyGraphVirtualCustomerMergeKeys.Keys.ToArray())
        {
            if (oldGroupKeys.Contains(legacyGraphVirtualCustomerMergeKeys[virtualCustomerId]))
            {
                legacyGraphVirtualCustomerMergeKeys[virtualCustomerId] = newGroupKey;
            }
        }

        foreach (var overrideKey in oldGroupKeys.Select(key => $"merge:{key}"))
        {
            if (legacyGraphCustomerNameOverrides.TryGetValue(overrideKey, out var name))
            {
                legacyGraphCustomerNameOverrides.TryAdd($"merge:{newGroupKey}", name);
                legacyGraphCustomerNameOverrides.Remove(overrideKey);
            }
        }

        if (oldGroupKeys.Contains(legacyGraphSelectedWorkGroupKey ?? ""))
        {
            legacyGraphSelectedWorkGroupKey = newGroupKey;
        }

        foreach (var oldKey in oldGroupKeys.Where(key => !string.Equals(key, newGroupKey, StringComparison.Ordinal)))
        {
            if (!legacyGraphCustomerMergeDrafts.TryGetValue(oldKey, out var draft))
            {
                continue;
            }

            legacyGraphCustomerMergeDrafts.TryAdd(
                newGroupKey,
                draft with { GroupKey = newGroupKey });
            legacyGraphCustomerMergeDrafts.Remove(oldKey);
        }
    }

    private void MigrateLegacyGraphApprovalStateKeys(
        IReadOnlySet<string> oldGroupKeys,
        string newGroupKey)
    {
        var approved = legacyGraphCustomerApprovalStates.GetValueOrDefault(newGroupKey);
        foreach (var oldKey in oldGroupKeys)
        {
            if (!legacyGraphCustomerApprovalStates.TryGetValue(oldKey, out var oldApproved))
            {
                continue;
            }

            approved |= oldApproved;
            if (!string.Equals(oldKey, newGroupKey, StringComparison.Ordinal))
            {
                legacyGraphCustomerApprovalStates.Remove(oldKey);
            }
        }

        if (oldGroupKeys.Any(legacyGraphCustomerApprovalStates.ContainsKey) || approved)
        {
            legacyGraphCustomerApprovalStates[newGroupKey] = approved;
        }
    }

    private void MigrateLegacyGraphReviewStateKeys(
        IReadOnlySet<string> oldGroupKeys,
        string newGroupKey)
    {
        var states = oldGroupKeys
            .Select(key => legacyGraphCustomerReviewStates.TryGetValue(key, out var state)
                ? state
                : null)
            .Where(state => state is not null)
            .Cast<string>()
            .ToArray();
        foreach (var oldKey in oldGroupKeys.Where(key => !string.Equals(key, newGroupKey, StringComparison.Ordinal)))
        {
            legacyGraphCustomerReviewStates.Remove(oldKey);
        }

        if (states.Any(state => state == LegacyGraphCustomerReviewStateValues.NeedsReview))
        {
            legacyGraphCustomerReviewStates[newGroupKey] = LegacyGraphCustomerReviewStateValues.NeedsReview;
        }
        else if (states.Any(state => state == LegacyGraphCustomerReviewStateValues.Approved))
        {
            legacyGraphCustomerReviewStates[newGroupKey] = LegacyGraphCustomerReviewStateValues.Approved;
        }
        else if (states.Length > 0)
        {
            legacyGraphCustomerReviewStates[newGroupKey] = states[0];
        }
    }

    private static void MigrateLegacyGraphStringKeySet(
        IReadOnlySet<string> oldGroupKeys,
        string newGroupKey,
        ISet<string> values)
    {
        if (oldGroupKeys.Any(values.Contains))
        {
            values.Add(newGroupKey);
        }

        foreach (var oldKey in oldGroupKeys.Where(key => !string.Equals(key, newGroupKey, StringComparison.Ordinal)))
        {
            values.Remove(oldKey);
        }
    }


    private string GetLegacyCustomerMergeKey(AbacusLegacyExportCandidateGraphCustomer customer)
    {
        return legacyGraphLogicalCustomerMergeGroupByCustomerId.TryGetValue(customer.CustomerId, out var logicalKey)
            ? logicalKey
            : legacyGraphVirtualCustomerMergeKeys.TryGetValue(customer.CustomerId, out var virtualKey)
            ? virtualKey
            : legacyGraphCustomerMergeGroupByCustomerId.TryGetValue(customer.CustomerId, out var groupKey)
                ? groupKey
                : $"customer:{customer.CustomerId}";
    }

    private int GetLegacyGraphDisplayedDocumentCount(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var logicalMembers = legacyExportCandidateGraphResult is null
            ? new[] { customer }
            : GetLegacyGraphLogicalCustomerMembers(customer);
        var logicalMemberIds = logicalMembers
            .Select(member => member.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        var sourceDocuments = logicalMembers
            .SelectMany(source => source.Vehicles.SelectMany(GetDocumentsForVehicle)
                .Concat(source.UnresolvedDocuments
                    .Where(document => !IsLegacyGraphCustomerDirectDocument(document))
                    .Where(document => legacyExportCandidateGraphResult is null ||
                                       !IsLegacyGraphDocumentInTray(document) &&
                                       !IsLegacyGraphDocumentUnconnected(document) &&
                                       FindCurrentCustomerForDocument(document) is { } currentCustomer &&
                                       logicalMemberIds.Contains(currentCustomer.CustomerId))))
            .Concat(GetLegacyGraphCustomerDirectDocuments(customer))
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var count = sourceDocuments
            .Count(document => !IsLegacyGraphDocumentInTrash(document));
        return count;
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphVehicle> GetLegacyGraphVehiclesForDisplay(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return customer.Vehicles;
        }

        var sourceCustomerIds = GetLegacyGraphLogicalCustomerMembers(customer)
            .Select(candidate => candidate.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        var assignedVehicles = legacyExportCandidateGraphResult.UnresolvedVehicleRows
            .Where(vehicle => legacyGraphManualVehicleCustomerLinks.TryGetValue(vehicle.VehicleId, out var targetCustomerId) &&
                              sourceCustomerIds.Contains(targetCustomerId) &&
                              !legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId) &&
                              !IsLegacyGraphVehicleInTrash(vehicle))
            .ToArray();
        var logicalMembers = GetLegacyGraphLogicalCustomerMembers(customer);
        return logicalMembers
            .SelectMany(source => source.Vehicles)
            .Where(vehicle => !legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId) &&
                              !IsLegacyGraphVehicleInTrash(vehicle) &&
                              FindCurrentCustomerForVehicle(vehicle) is { } currentCustomer &&
                              sourceCustomerIds.Contains(currentCustomer.CustomerId))
            .Concat(assignedVehicles)
            .GroupBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(vehicle => vehicle.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .ToArray();
    }

    private AbacusLegacyExportCandidateGraphCustomer GetLegacyGraphDisplayCustomer(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var mergeKey = GetLegacyCustomerMergeKey(customer);
        if (!TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup) ||
            mergeGroup.CustomerIds.Count < 2 ||
            !IsLegacyGraphLogicalCustomerGroup(mergeKey) &&
            !legacyGraphCustomerMergeDrafts.ContainsKey(mergeKey) &&
            !legacyGraphAppliedCustomerMergeKeys.Contains(mergeKey))
        {
            return customer;
        }

        var candidates = GetLegacyGraphCustomerMergeCandidates(customer);
        if (candidates.Count < 2)
        {
            return customer;
        }

        legacyGraphCustomerMergeDrafts.TryGetValue(mergeKey, out var draft);

        string SelectedValue(string key, Func<AbacusLegacyExportCandidateGraphCustomer, string> selector)
        {
            if (draft?.SelectedValues.TryGetValue(key, out var selectedValue) == true &&
                !string.IsNullOrWhiteSpace(selectedValue))
            {
                return selectedValue;
            }

            return candidates
                       .Select(selector)
                       .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
                   "";
        }

        var virtualCustomerId = $"merge-preview:{mergeKey}";
        legacyGraphVirtualCustomerMergeKeys[virtualCustomerId] = mergeKey;
        var vehicles = candidates
            .SelectMany(candidate => candidate.Vehicles)
            .Where(vehicle => !legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId) &&
                              !IsLegacyGraphVehicleInTrash(vehicle))
            .Concat(legacyExportCandidateGraphResult?.UnresolvedVehicleRows.Where(vehicle =>
                legacyGraphManualVehicleCustomerLinks.TryGetValue(vehicle.VehicleId, out var targetCustomerId) &&
                candidates.Any(candidate => string.Equals(candidate.CustomerId, targetCustomerId, StringComparison.Ordinal)) &&
                !legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId) &&
                !IsLegacyGraphVehicleInTrash(vehicle)) ?? [])
            .GroupBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(vehicle => vehicle.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unresolvedDocuments = candidates
            .SelectMany(candidate => candidate.UnresolvedDocuments)
            .Where(document => !IsLegacyGraphDocumentInTrash(document))
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var mergedDisplayName = legacyGraphCustomerNameOverrides.TryGetValue($"merge:{mergeKey}", out var overrideName)
            ? overrideName
            : GetLegacyGraphCustomerDisplayName(candidates[0]);
        return new AbacusLegacyExportCandidateGraphCustomer(
            virtualCustomerId,
            SelectedValue("customer-number", candidate => candidate.CustomerNumber),
            draft?.SelectedValues.TryGetValue("customer-name", out var selectedName) == true &&
            !string.IsNullOrWhiteSpace(selectedName)
                ? selectedName
                : mergedDisplayName,
            SelectedValue("name-kana", candidate => candidate.NameKana),
            SelectedValue("phone", candidate => candidate.PhoneNumber),
            SelectedValue("email", candidate => candidate.EmailAddress),
            SelectedValue("postal-code", candidate => candidate.PostalCode),
            SelectedValue("address", candidate => candidate.Address),
            SelectedValue("memo", candidate => candidate.Memo),
            vehicles,
            unresolvedDocuments);
    }

    private static string NormalizeLegacyCustomerMergeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = value.Normalize(NormalizationForm.FormKC);
        return string.Concat(normalized.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
    }

    private IReadOnlyList<LegacyGraphCustomerMergeField> GetLegacyGraphCustomerMergeFields() =>
    [
        new("customer-number", "顧客番号", customer => customer.CustomerNumber),
        new("customer-name", "顧客名", GetLegacyGraphCustomerName),
        new("name-kana", "ふりがな", customer => customer.NameKana),
        new("phone", "電話番号", customer => customer.PhoneNumber),
        new("email", "メールアドレス", customer => customer.EmailAddress),
        new("postal-code", "郵便番号", customer => customer.PostalCode),
        new("address", "住所", customer => customer.Address),
        new("memo", "メモ", customer => customer.Memo),
    ];

    private void LegacyGraphMergeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        if (legacyGraphSelectedItem is not AbacusLegacyExportCandidateGraphCustomer selectedCustomer)
        {
            return;
        }

        OpenLegacyGraphMergeDialog(selectedCustomer, GetLegacyCustomerMergeKey(selectedCustomer));
    }

    private bool OpenLegacyGraphMergeDialog(
        AbacusLegacyExportCandidateGraphCustomer selectedCustomer,
        string mergeKey)
    {
        return OpenLegacyGraphMergeDialog(
            selectedCustomer,
            mergeKey,
            GetLegacyGraphCustomerMergeCandidates(selectedCustomer));
    }

    private bool OpenLegacyGraphMergeDialog(
        AbacusLegacyExportCandidateGraphCustomer selectedCustomer,
        string mergeKey,
        IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> candidates)
    {
        if (candidates.Count < 2)
        {
            return false;
        }

        legacyGraphCustomerMergeDrafts.TryGetValue(mergeKey, out var existingDraft);
        var isManualMerge = TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup) &&
                            mergeGroup.Origin == "manual";
        var fields = GetLegacyGraphCustomerMergeFields();
        var comboBoxes = new Dictionary<string, ComboBox>(StringComparer.Ordinal);

        var dialog = new Window
        {
            Owner = this,
            Title = $"顧客の比較・統合プレビュー（{candidates.Count}件）",
            Width = 1080,
            Height = 760,
            MinWidth = 820,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ToBrush("#F4F7FB"),
        };
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = isManualMerge
                ? "ドラッグで追加した顧客候補を比較しています。自動統合はせず、項目ごとに採用する候補を選択して保存してください。顧客の統合・最終確認は『この顧客を確定』で行います。"
                : "同じ顧客名の候補を比較しています。自動統合はせず、項目ごとに採用する候補を選択して保存してください。顧客の統合・最終確認は『この顧客を確定』で行います。",
            Foreground = ToBrush("#805B10"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        var comparisonScroll = new ScrollViewer
        {
            Margin = new Thickness(0, 16, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
        };
        var comparisonGrid = new Grid { MinWidth = 760 };
        comparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        foreach (var _ in candidates)
        {
            comparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        comparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(265) });
        comparisonGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddComparisonElement(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            comparisonGrid.Children.Add(element);
        }

        AddComparisonElement(new TextBlock
        {
            Text = "項目",
            FontWeight = FontWeights.SemiBold,
            Foreground = ToBrush("#10233F"),
            Margin = new Thickness(8),
        }, 0, 0);
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var candidate = candidates[candidateIndex];
            AddComparisonElement(new Border
            {
                Margin = new Thickness(3),
                Padding = new Thickness(8),
                Background = ToBrush("#EAF2FF"),
                BorderBrush = ToBrush("#B9D0F5"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock
                {
                    Text = $"{candidate.DisplayName}\nID: {candidate.CustomerId}\n車両 {candidate.Vehicles.Count:N0}台 / 書類 {candidate.Documents:N0}件",
                    Foreground = ToBrush("#10233F"),
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
            }, 0, candidateIndex + 1);
        }
        AddComparisonElement(new TextBlock
        {
            Text = "採用する候補",
            FontWeight = FontWeights.SemiBold,
            Foreground = ToBrush("#10233F"),
            Margin = new Thickness(8),
        }, 0, candidates.Count + 1);

        var rowIndex = 1;
        foreach (var field in fields)
        {
            comparisonGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddComparisonElement(new TextBlock
            {
                Text = field.Label,
                FontWeight = FontWeights.SemiBold,
                Foreground = ToBrush("#36465A"),
                Margin = new Thickness(8, 9, 8, 9),
                VerticalAlignment = VerticalAlignment.Top,
            }, rowIndex, 0);

            var options = candidates
                .Select(candidate => new LegacyGraphMergeFieldOption(
                    candidate.CustomerId,
                    candidate.DisplayName,
                    field.ValueSelector(candidate)))
                .ToArray();
            for (var candidateIndex = 0; candidateIndex < options.Length; candidateIndex++)
            {
                var option = options[candidateIndex];
                AddComparisonElement(new Border
                {
                    Margin = new Thickness(3),
                    Padding = new Thickness(7),
                    Background = Brushes.White,
                    BorderBrush = ToBrush("#D8E1EC"),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = Fallback(option.Value),
                        Foreground = ToBrush("#52647A"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                }, rowIndex, candidateIndex + 1);
            }

            var comboBox = new ComboBox
            {
                Margin = new Thickness(3),
                Padding = new Thickness(5, 3, 5, 3),
                ItemsSource = options,
                DisplayMemberPath = nameof(LegacyGraphMergeFieldOption.DisplayText),
                SelectedValuePath = nameof(LegacyGraphMergeFieldOption.CustomerId),
                MinHeight = 34,
            };
            var selectedCandidateId = existingDraft?.FieldSelections.TryGetValue(field.Key, out var savedCandidateId) == true
                ? savedCandidateId
                : options.FirstOrDefault(option => !string.IsNullOrWhiteSpace(option.Value))?.CustomerId ?? options[0].CustomerId;
            comboBox.SelectedValue = selectedCandidateId;
            if (comboBox.SelectedItem is null)
            {
                comboBox.SelectedIndex = 0;
            }
            comboBoxes[field.Key] = comboBox;
            AddComparisonElement(comboBox, rowIndex, candidates.Count + 1);
            rowIndex++;
        }
        comparisonScroll.Content = comparisonGrid;
        Grid.SetRow(comparisonScroll, 1);
        root.Children.Add(comparisonScroll);

        var previewBorder = new Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(10),
            Background = ToBrush("#EEF7F1"),
            BorderBrush = ToBrush("#B8DFC5"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
        };
        var previewText = new TextBlock
        {
            Foreground = ToBrush("#17643A"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        };
        previewBorder.Child = previewText;
        Grid.SetRow(previewBorder, 2);
        root.Children.Add(previewBorder);

        void RefreshMergePreview()
        {
            var previewLines = fields.Select(field =>
            {
                var option = comboBoxes[field.Key].SelectedItem as LegacyGraphMergeFieldOption;
                return $"{field.Label}: {Fallback(option?.Value ?? "未設定")}（{Fallback(option?.CustomerName ?? "候補未選択")}）";
            });
            previewText.Text = "統合プレビュー（確定前の採用内容）\n" + string.Join("\n", previewLines);
        }
        foreach (var comboBox in comboBoxes.Values)
        {
            comboBox.SelectionChanged += (_, _) => RefreshMergePreview();
        }
        RefreshMergePreview();

        var buttons = new StackPanel
        {
            Margin = new Thickness(0, 14, 0, 0),
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var saveButton = new Button { Content = "統合プレビューを保存", MinWidth = 180, MinHeight = 36 };
        var cancelButton = new Button { Content = "キャンセル", MinWidth = 110, MinHeight = 36 };
        saveButton.Click += (_, _) =>
        {
            var fieldSelections = fields.ToDictionary(
                field => field.Key,
                field => (comboBoxes[field.Key].SelectedItem as LegacyGraphMergeFieldOption)?.CustomerId ?? candidates[0].CustomerId,
                StringComparer.Ordinal);
            var selectedValues = fields.ToDictionary(
                field => field.Key,
                field => field.ValueSelector(candidates.First(candidate =>
                    string.Equals(candidate.CustomerId, fieldSelections[field.Key], StringComparison.Ordinal))),
                StringComparer.Ordinal);
            var nextDraft = new LegacyGraphCustomerMergeDraft(
                mergeKey,
                candidates.Select(candidate => candidate.CustomerId).ToArray(),
                fieldSelections,
                selectedValues,
                DateTimeOffset.UtcNow);
            var draftChanged = !AreLegacyGraphCustomerMergeDraftsEquivalent(existingDraft, nextDraft);
            legacyGraphCustomerMergeDrafts[mergeKey] = nextDraft;
            if (draftChanged)
            {
                InvalidateLegacyGraphCustomerApprovalForMergeKey(mergeKey);
                InvalidateLegacyGraphImportConfirmation();
            }
            dialog.DialogResult = true;
        };
        cancelButton.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(saveButton);
        buttons.Children.Add(cancelButton);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        dialog.Content = root;
        if (dialog.ShowDialog() == true)
        {
            var sourceCustomer = GetLegacyGraphSourceCustomer(selectedCustomer);
            if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
            {
                EnsureLegacyGraphMatchingCustomerSelection();
                RefreshLegacyGraphCustomerList();
                RefreshLegacyMatchingView();
            }
            else
            {
                RefreshLegacyGraphCustomerList($"group:{mergeKey}");
                var displayCustomer = GetLegacyGraphDisplayCustomer(sourceCustomer);
                UpdateLegacyGraphInspector(displayCustomer);
                RenderLegacyGraphCustomer(displayCustomer);
            }

            return true;
        }

        return false;
    }

    private bool TryApplyLegacyGraphCustomerMerge(
        AbacusLegacyExportCandidateGraphCustomer customer,
        out AbacusLegacyExportCandidateGraphCustomer displayCustomer)
    {
        var sourceCustomer = GetLegacyGraphSourceCustomer(customer);
        var mergeKey = GetLegacyCustomerMergeKey(sourceCustomer);
        var mergeCandidates = GetLegacyGraphCustomerMergeCandidates(sourceCustomer);
        displayCustomer = GetLegacyGraphDisplayCustomer(sourceCustomer);
        var hasMergeIntent = string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase)
            ? HasLegacyMatchingCustomerMergeIntent(sourceCustomer, mergeCandidates)
            : HasLegacyGraphStructuralMergeIntent(sourceCustomer, mergeCandidates);
        if (!hasMergeIntent ||
            IsLegacyGraphLogicalCustomerGroup(mergeKey) ||
            legacyGraphAppliedCustomerMergeKeys.Contains(mergeKey))
        {
            return true;
        }

        var candidateCustomerIds = mergeCandidates
            .Select(candidate => candidate.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        if (!HasCompleteLegacyGraphCustomerMergeDraft(mergeKey, candidateCustomerIds))
        {
            LegacyGraphStatusText.Text =
                "顧客情報の採用内容を保存してから、この顧客を確定してください。";
            LegacyGraphStatusText.Foreground = ToBrush("#805B10");
            return false;
        }

        var unconnectedDocuments = GetLegacyGraphUnconnectedDocuments(displayCustomer).ToArray();
        if (unconnectedDocuments.Length > 0)
        {
            LegacyGraphStatusText.Text =
                $"ノード未接続の書類が{unconnectedDocuments.Length:N0}件あります。書類ノードを車両へ接続するか、未確定トレイへ移動してから確定してください。";
            LegacyGraphStatusText.Foreground = ToBrush("#A61B1B");
            MessageBox.Show(
                this,
                $"この統合候補にはノード未接続の書類が{unconnectedDocuments.Length:N0}件あります。\n" +
                "\n書類ブロックを車両へドラッグして接続するか、書類ブロックを未確定トレイへドラッグしてから、もう一度確定してください。",
                "車両未接続の書類があります",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var appliedMergeKey = mergeKey;
        var candidateDraftBeforeApply = legacyGraphCustomerMergeDrafts.TryGetValue(
            mergeKey,
            out var savedCandidateDraft)
            ? savedCandidateDraft
            : null;
        string? candidateGroupKeyToCleanup = null;
        if (TryGetLegacyGraphMergeGroup(mergeKey, out var candidateGroup) &&
            candidateGroup.CustomerIds.Count > 1)
        {
            candidateGroupKeyToCleanup = candidateGroup.GroupId;
            var groupCustomers = candidateGroup.CustomerIds
                .Select(FindLegacyGraphCustomerById)
                .Where(item => item is not null)
                .Cast<AbacusLegacyExportCandidateGraphCustomer>()
                .ToArray();
            if (groupCustomers.Length > 1)
            {
                var logicalCustomer = groupCustomers[0];
                foreach (var targetCustomer in groupCustomers.Skip(1))
                {
                    appliedMergeKey = AcceptLegacyGraphCustomerMerge(logicalCustomer, targetCustomer);
                }

                if (candidateDraftBeforeApply is not null)
                {
                    legacyGraphCustomerMergeDrafts[appliedMergeKey] = candidateDraftBeforeApply with
                    {
                        GroupKey = appliedMergeKey,
                    };
                }

                legacyGraphAppliedCustomerMergeKeys.Remove(mergeKey);
            }
        }

        // 顧客確定後は、候補用マップと論理顧客用マップを重ねて保持しない。
        // 論理顧客側へ移した構成顧客が、後のドラッグで候補グループへも
        // 所属しているように解釈される二重所属を防ぎます。
        if (candidateGroupKeyToCleanup is not null &&
            !string.Equals(candidateGroupKeyToCleanup, appliedMergeKey, StringComparison.Ordinal) &&
            legacyGraphCustomerMergeGroups.Remove(candidateGroupKeyToCleanup, out var staleCandidateGroup))
        {
            RehomeLegacyGraphManualDocumentCustomerLinks(
                candidateGroupKeyToCleanup,
                staleCandidateGroup.CustomerIds,
                null);
            foreach (var customerId in staleCandidateGroup.CustomerIds)
            {
                if (legacyGraphCustomerMergeGroupByCustomerId.TryGetValue(customerId, out var mappedGroupKey) &&
                    string.Equals(mappedGroupKey, candidateGroupKeyToCleanup, StringComparison.Ordinal))
                {
                    legacyGraphCustomerMergeGroupByCustomerId.Remove(customerId);
                }
            }

            legacyGraphCustomerGroupExpanded.Remove(candidateGroupKeyToCleanup);
            foreach (var virtualCustomerId in legacyGraphVirtualCustomerMergeKeys
                         .Where(pair => string.Equals(pair.Value, candidateGroupKeyToCleanup, StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                legacyGraphVirtualCustomerMergeKeys.Remove(virtualCustomerId);
            }
        }

        legacyGraphAppliedCustomerMergeKeys.Add(appliedMergeKey);
        displayCustomer = GetLegacyGraphDisplayCustomer(sourceCustomer);
        legacyGraphSelectedItem = displayCustomer;
        return true;
    }

    private async void LegacyGraphApproveAllMergeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph || legacyGraphBulkMergeBusy)
        {
            return;
        }

        var domainSnapshot = CaptureLegacyGraphDomainState();
        var expandedGroupsBeforeBulk = new Dictionary<string, bool>(
            legacyGraphCustomerGroupExpanded,
            StringComparer.Ordinal);
        legacyGraphBulkMergeBusy = true;
        LegacyGraphApproveAllMergeButton.IsEnabled = false;
        try
        {
            await LegacyGraphApproveAllMergeButton_ClickCore(sender, e);
        }
        catch (Exception exception)
        {
            // async void のイベントハンドラーから例外が外へ出ると、WPF全体が終了します。
            // 一括処理は対象件数が多いため、想定外のデータでも画面へ戻して再試行できるようにします。
            RestoreLegacyGraphDomainState(domainSnapshot);
            RestoreDictionary(legacyGraphCustomerGroupExpanded, expandedGroupsBeforeBulk);
            RefreshLegacyGraphCustomerList(legacyGraphSelectedWorkGroupKey);
            RefreshLegacyGraphUnresolvedVehicleList();
            RefreshLegacyGraphUnresolvedDocumentLists();
            RefreshLegacyGraphTrashLists();
            RefreshLegacyMatchingView();
            UpdateLegacyGraphImportConfirmationButton();
            LegacyGraphStatusText.Text =
                $"顧客の一括確定に失敗しました: {exception.Message}（{exception.GetType().Name}）";
            LegacyGraphStatusText.Foreground = ToBrush("#A61B1B");
        }
        finally
        {
            legacyGraphBulkMergeBusy = false;
            UpdateLegacyGraphImportConfirmationButton();
            ScheduleDeferredLegacyGraphCheckpointSave();
            ScheduleLegacyGraphCheckpointSave();
        }
    }

    private async Task LegacyGraphApproveAllMergeButton_ClickCore(object sender, RoutedEventArgs e)
    {
        if (legacyExportCandidateGraphResult is null || legacyGraphImportConfirmed)
        {
            return;
        }

        var pendingGroups = GetLegacyGraphPendingMergeGroups();
        var unapprovedCustomerCount = GetLegacyGraphUnapprovedCustomerCount();
        if (pendingGroups.Length == 0 && unapprovedCustomerCount == 0)
        {
            UpdateLegacyGraphImportConfirmationButton();
            return;
        }

        var groupEntries = new List<LegacyGraphMergeApprovalEntry>(pendingGroups.Length);
        for (var index = 0; index < pendingGroups.Length; index++)
        {
            var group = pendingGroups[index];
            var sourceCustomer = legacyExportCandidateGraphResult.Customers
                .FirstOrDefault(customer =>
                    group.CustomerIds.Contains(customer.CustomerId, StringComparer.Ordinal) &&
                    !legacyGraphTrashCustomerIds.Contains(customer.CustomerId));
            if (sourceCustomer is null)
            {
                break;
            }

            var candidates = GetLegacyGraphCustomerMergeCandidates(sourceCustomer);
            if (candidates.Count < 2)
            {
                break;
            }

            var hasCurrentDraft = legacyGraphCustomerMergeDrafts.TryGetValue(group.GroupId, out var draft) &&
                                   draft.CandidateCustomerIds.Count == candidates.Count &&
                                   draft.CandidateCustomerIds.All(candidateId => candidates.Any(candidate =>
                                       string.Equals(candidate.CustomerId, candidateId, StringComparison.Ordinal)));
            groupEntries.Add(new LegacyGraphMergeApprovalEntry(group, sourceCustomer, candidates, hasCurrentDraft));

            if ((index + 1) % 8 == 0)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        if (groupEntries.Count != pendingGroups.Length)
        {
            MessageBox.Show(
                this,
                "一括確定対象の顧客候補を再構成できません。候補パッケージを再読込してから、もう一度お試しください。",
                "一括確定できません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var needsDefaultPreview = groupEntries.Count(entry => !entry.HasCurrentDraft);
        var confirmationParts = new List<string>();
        if (pendingGroups.Length > 0)
        {
            confirmationParts.Add(needsDefaultPreview > 0
                ? $"統合候補 {pendingGroups.Length:N0}件を論理顧客として確定します。\n\n" +
                  $"このうち{needsDefaultPreview:N0}件は顧客情報プレビューが未保存です。未保存の候補は、各項目で最初に値が入っている候補を採用し、画面上の統合プレビューを自動作成します。"
                : $"顧客情報プレビュー保存済みの統合候補 {pendingGroups.Length:N0}件を論理顧客として確定します。");
        }

        if (unapprovedCustomerCount > 0)
        {
            confirmationParts.Add(
                $"通常顧客を含む顧客確認未完了 {unapprovedCustomerCount:N0}件を、現在の構成のまま顧客確認済みにします。");
        }

        var confirmationText = string.Join(
            "\n\n",
            confirmationParts.Append("元CSV・ABACUSフォルダーは変更されません。実行しますか？"));
        if (MessageBox.Show(
                this,
                confirmationText,
                "顧客を一括確定",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        // 承認前の未接続チェックでは、各グループの表示用顧客カードを作りません。
        // 192件規模では仮想顧客カードとキャンバスを全件生成すると、WPFのレイアウト負荷が
        // 急増して画面が応答しなくなるため、元顧客IDだけで対象書類を判定します。
        LegacyGraphStatusText.Text = pendingGroups.Length > 0
            ? $"統合候補 {pendingGroups.Length:N0}件の未接続書類を確認しています…"
            : "顧客の一括確定を準備しています…";
        LegacyGraphStatusText.Foreground = ToBrush("#52647A");
        await Dispatcher.Yield(DispatcherPriority.Background);
        var originalCustomerIdsByDocumentKey = BuildLegacyGraphOriginalCustomerIdMap();
        var blockedEntries = new List<(LegacyGraphMergeApprovalEntry Entry,
            IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> Documents)>();
        for (var index = 0; index < groupEntries.Count; index++)
        {
            var entry = groupEntries[index];
            var documents = GetLegacyGraphUnconnectedDocuments(
                entry.Candidates.Select(candidate => candidate.CustomerId)
                    .ToHashSet(StringComparer.Ordinal),
                originalCustomerIdsByDocumentKey);
            if (documents.Count > 0)
            {
                blockedEntries.Add((entry, documents));
            }

            if ((index + 1) % 8 == 0)
            {
                LegacyGraphStatusText.Text =
                    $"統合候補を確認しています… {index + 1:N0}/{groupEntries.Count:N0}件";
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        if (blockedEntries.Count > 0)
        {
            var blockedDocumentCount = blockedEntries.Sum(item => item.Documents.Count);
            LegacyGraphStatusText.Text =
                $"一括確定を中止しました。ノード未接続の書類が{blockedDocumentCount:N0}件あります。";
            LegacyGraphStatusText.Foreground = ToBrush("#A61B1B");
            MessageBox.Show(
                this,
                $"{blockedEntries.Count:N0}件の統合候補に、合計{blockedDocumentCount:N0}件のノード未接続書類があります。\n\n" +
                "書類を車両へ接続するか、未確定トレイへ移動してから、もう一度一括確定してください。\n" +
                "顧客だけが一意に判定できる車両情報なし書類は、互換特例として一括確定できます。",
                "未接続書類があります",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        LegacyGraphStatusText.Text = pendingGroups.Length > 0
            ? "統合候補の顧客プレビューを準備しています…"
            : "顧客の一括確定を実行しています…";
        await Dispatcher.Yield(DispatcherPriority.Background);
        var defaultDrafts = new Dictionary<string, LegacyGraphCustomerMergeDraft>(StringComparer.Ordinal);
        for (var index = 0; index < groupEntries.Count; index++)
        {
            var entry = groupEntries[index];
            if (!entry.HasCurrentDraft)
            {
                defaultDrafts[entry.Group.GroupId] =
                    BuildLegacyGraphDefaultMergeDraft(entry.Group.GroupId, entry.Candidates);
            }

            if ((index + 1) % 8 == 0)
            {
                LegacyGraphStatusText.Text =
                    $"統合候補の顧客プレビューを準備しています… {index + 1:N0}/{groupEntries.Count:N0}件";
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        InvalidateLegacyGraphImportConfirmation();
        for (var index = 0; index < groupEntries.Count; index++)
        {
            var entry = groupEntries[index];
            var appliedMergeKey = entry.Group.GroupId;
            if (defaultDrafts.TryGetValue(entry.Group.GroupId, out var defaultDraft))
            {
                legacyGraphCustomerMergeDrafts[entry.Group.GroupId] = defaultDraft;
            }
            var candidateDraftBeforeApply = legacyGraphCustomerMergeDrafts.TryGetValue(
                entry.Group.GroupId,
                out var savedCandidateDraft)
                ? savedCandidateDraft
                : null;

            var groupCustomers = entry.Candidates.ToArray();
            if (!IsLegacyGraphLogicalCustomerGroup(entry.Group.GroupId) && groupCustomers.Length > 1)
            {
                var logicalCustomer = groupCustomers[0];
                foreach (var targetCustomer in groupCustomers.Skip(1))
                {
                    appliedMergeKey = AcceptLegacyGraphCustomerMerge(logicalCustomer, targetCustomer);
                }

                if (candidateDraftBeforeApply is not null)
                {
                    legacyGraphCustomerMergeDrafts[appliedMergeKey] = candidateDraftBeforeApply with
                    {
                        GroupKey = appliedMergeKey,
                    };
                }

                legacyGraphAppliedCustomerMergeKeys.Remove(entry.Group.GroupId);
            }

            legacyGraphAppliedCustomerMergeKeys.Add(appliedMergeKey);
            legacyGraphCustomerApprovalStates.Remove(appliedMergeKey);
            legacyGraphCustomerReviewStates.Remove(appliedMergeKey);

            if ((index + 1) % 16 == 0)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        if (!TryApproveAllLegacyGraphCustomers(
                out var approvedCustomerGroupCount,
                out var approvalFailureReason))
        {
            LegacyGraphStatusText.Text = approvalFailureReason;
            LegacyGraphStatusText.Foreground = ToBrush("#A61B1B");
            MessageBox.Show(
                this,
                approvalFailureReason,
                "顧客を一括確定できません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            throw new InvalidOperationException(approvalFailureReason);
        }

        var selectedCustomer = GetLegacyGraphListSelectedDisplayCustomer() ??
                               legacyExportCandidateGraphResult.Customers.FirstOrDefault(customer =>
                                   !legacyGraphTrashCustomerIds.Contains(customer.CustomerId));
        if (selectedCustomer is not null)
        {
            var selectedEntryId = GetLegacyGraphCustomerListEntryId(selectedCustomer);
            RefreshLegacyGraphCustomerList(selectedEntryId);
            var displayCustomer = GetLegacyGraphDisplayCustomer(selectedCustomer);
            legacyGraphSelectedItem = displayCustomer;
            UpdateLegacyGraphInspector(displayCustomer);
            RenderLegacyGraphCustomer(displayCustomer);
        }

        RefreshLegacyMatchingView();
        LegacyGraphStatusText.Text =
            $"顧客確認を一括完了しました。統合候補 {pendingGroups.Length:N0}件を論理顧客として確定し、{approvedCustomerGroupCount:N0}論理顧客を顧客確認済みにしました。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
        UpdateLegacyGraphImportConfirmationButton();
    }

    private LegacyGraphCustomerMergeDraft BuildLegacyGraphDefaultMergeDraft(
        string mergeKey,
        IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> candidates)
    {
        var fields = GetLegacyGraphCustomerMergeFields();
        var fieldSelections = fields.ToDictionary(
            field => field.Key,
            field => candidates
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(field.ValueSelector(candidate)))?.CustomerId ??
                     candidates[0].CustomerId,
            StringComparer.Ordinal);
        var selectedValues = fields.ToDictionary(
            field => field.Key,
            field => field.ValueSelector(candidates.First(candidate =>
                string.Equals(candidate.CustomerId, fieldSelections[field.Key], StringComparison.Ordinal))),
            StringComparer.Ordinal);
        return new LegacyGraphCustomerMergeDraft(
            mergeKey,
            candidates.Select(candidate => candidate.CustomerId).ToArray(),
            fieldSelections,
            selectedValues,
            DateTimeOffset.UtcNow);
    }

    private static bool AreLegacyGraphCustomerMergeDraftsEquivalent(
        LegacyGraphCustomerMergeDraft? current,
        LegacyGraphCustomerMergeDraft next)
    {
        if (current is null ||
            !current.CandidateCustomerIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(next.CandidateCustomerIds))
        {
            return false;
        }

        static bool AreValuesEqual(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right) =>
            left.Count == right.Count &&
            left.All(pair => right.TryGetValue(pair.Key, out var value) &&
                             string.Equals(pair.Value, value, StringComparison.Ordinal));

        return AreValuesEqual(current.FieldSelections, next.FieldSelections) &&
               AreValuesEqual(current.SelectedValues, next.SelectedValues);
    }

    private void LegacyGraphFinalizeImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph || legacyExportCandidateGraphResult is null || legacyGraphImportConfirmed)
        {
            return;
        }

        var pendingMergeGroupCount = GetLegacyGraphPendingMergeGroupCount();
        var pendingDocumentCount = GetLegacyGraphPendingDocumentCount();
        var unapprovedCustomerCount = GetLegacyGraphUnapprovedCustomerCount();
        if (!LegacyMatchingWorkflow.CanFinalizeImport(
                pendingMergeGroupCount,
                pendingDocumentCount,
                unapprovedCustomerCount))
        {
            var pendingDetails = new List<string>();
            if (pendingMergeGroupCount > 0)
            {
                pendingDetails.Add($"未承認の統合候補 {pendingMergeGroupCount:N0}件");
            }

            if (pendingDocumentCount > 0)
            {
                pendingDetails.Add($"ノード未接続の書類 {pendingDocumentCount:N0}件");
            }

            if (unapprovedCustomerCount > 0)
            {
                pendingDetails.Add($"顧客確認未完了 {unapprovedCustomerCount:N0}件");
            }

            MessageBox.Show(
                this,
                "次の操作が残っているため、インポート内容を確定できません。\n\n" +
                string.Join("\n", pendingDetails) +
                "\n\n統合候補の承認、顧客単位の確定、書類ノードの車両接続、または未確定トレイへの移動を完了してください。",
                "操作が残っています",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            UpdateLegacyGraphImportConfirmationButton();
            return;
        }

        var trayDocuments = GetLegacyGraphTrayDocuments();
        var unresolvedVehicleCount = GetLegacyGraphUnresolvedVehicleCount();
        var trashCustomerCount = legacyGraphTrashCustomerIds.Count;
        var trashVehicleCount = legacyGraphTrashVehicleIds.Count;
        var trashDocumentCount = legacyGraphTrashDocumentKeys.Count;
        var excludedSummary = trayDocuments.Count == 0
            ? "未確定トレイに残っている書類はありません。"
            : "未確定トレイに残っている次の書類は、今回のインポートから除外されます。\n" +
              string.Join(
                  "、",
                  trayDocuments
                  .GroupBy(document => document.Kind, StringComparer.Ordinal)
                  .OrderBy(group => group.Key, StringComparer.Ordinal)
                  .Select(group => $"{group.Key}{group.Count():N0}件"));
        if (unresolvedVehicleCount > 0)
        {
            excludedSummary += $"\n\n未確定車両 {unresolvedVehicleCount:N0}件は顧客へ接続されていないため、登録前パッケージから除外されます。対応画像も同じ扱いです。";
        }
        if (trashCustomerCount > 0 || trashVehicleCount > 0 || trashDocumentCount > 0)
        {
            excludedSummary += $"\n\nごみ箱（明示除外）: 顧客 {trashCustomerCount:N0}件 / 車両 {trashVehicleCount:N0}件 / 書類 {trashDocumentCount:N0}件。関連する車両・書類・画像も除外されます。";
        }
        var confirmation = MessageBox.Show(
            this,
            "顧客統合、書類紐付け、ノード操作を完了した状態としてインポート内容を確定します。\n\n" +
            excludedSummary +
            "\n\n確定後に操作を変更した場合は、もう一度この確認が必要です。\n元のCSV・ABACUSフォルダーは変更されません。\n\nインポート内容を確定しますか？",
            "インポート内容を確定",
            MessageBoxButton.YesNo,
            trayDocuments.Count == 0 ? MessageBoxImage.Question : MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        legacyGraphExcludedDocumentKeys.Clear();
        foreach (var document in trayDocuments)
        {
            legacyGraphExcludedDocumentKeys.Add(GetLegacyDocumentKey(document));
        }

        legacyGraphImportConfirmed = true;
        UpdateLegacyGraphImportConfirmationButton();
        LegacyGraphStatusText.Text = trayDocuments.Count == 0 && unresolvedVehicleCount == 0
            ? "インポート内容を確定しました。未確定トレイの書類はありません。元CSV・ABACUSフォルダーは変更していません。"
            : $"インポート内容を確定しました。書類{trayDocuments.Count:N0}件、未接続車両{unresolvedVehicleCount:N0}件は今回のインポートから除外します。元CSV・ABACUSフォルダーは変更していません。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
        ScheduleLegacyGraphCheckpointSave();
    }

    private async void LegacyGraphCreateFinalPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph ||
            legacyExportCandidateGraphResult is null ||
            !legacyGraphImportConfirmed ||
            legacyGraphFinalPackageBusy)
        {
            return;
        }

        if (unifiedImportOutputSession is null || fp5VehicleImageMapping is null || !fp5VehicleImageMapping.IsFullyMatched)
        {
            legacyGraphFinalPackageHasError = true;
            LegacyGraphFinalPackageStatusText.Text =
                "Gate 14画像対応付けが完了していないため、登録前パッケージを作成できません。先に解析を完了してください。";
            LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#A61B1B");
            LegacyGraphFinalPackageNextStepText.Text =
                "次の操作: ABACUSフォルダーを選び直し、解析を再実行してください。";
            LegacyGraphFinalPackageNextStepText.Foreground = ToBrush("#A61B1B");
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "確定した顧客統合・書類紐付けを登録前パッケージへ出力しますか？\n\n" +
            "顧客CSV・車両CSV・販売CSV・整備CSV・書類リンク・除外一覧を保存し、Gate 14で対応付けた画像も自動で同梱します。\n" +
            $"画像: {fp5VehicleImageMapping.MatchedImageCount:N0}件 / 画像なし車両: {fp5VehicleImageMapping.NoImageCount:N0}件\n" +
            "Web API、D1、画像アップロード、元CSV・ABACUSフォルダーの変更は行いません。",
            "登録前パッケージの作成",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        legacyGraphFinalPackageBusy = true;
        legacyGraphFinalPackageHasError = false;
        legacyGraphFinalPackagePath = null;
        BeginOperation("登録前パッケージを作成しています…");
        OperationProgressBar.IsIndeterminate = true;
        LegacyGraphOpenFinalPackageButton.IsEnabled = false;
        LegacyGraphCreateFinalPackageButton.IsEnabled = false;
        LegacyGraphFinalPackageResultText.Text = "";
        LegacyGraphFinalPackageStatusText.Text = "確定状態・書類リンク・除外一覧を再検証してパッケージを作成しています…";
        LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#52647A");
        LegacyGraphFinalPackageNextStepText.Text = "処理中: ファイルの再検証とパッケージ作成が終わるまでお待ちください。";
        LegacyGraphFinalPackageNextStepText.Foreground = ToBrush("#52647A");
        try
        {
            var cancellationToken = operationCancellation?.Token ?? CancellationToken.None;
            var snapshot = BuildLegacyGraphFinalizationSnapshot();
            var result = await legacyGraphFinalPackageStore.CreateAsync(
                legacyExportCandidateGraphResult,
                snapshot,
                unifiedImportOutputSession.WorkIntermediatePath,
                fp5VehicleImageMapping,
                cancellationToken);
            if (!LegacyGraphFinalPackageState.CanComplete(
                    snapshot.ImportConfirmed,
                    legacyGraphImportConfirmed))
            {
                throw new InvalidOperationException(
                    "パッケージ作成中にグラフの確定状態が変更されたため、登録前パッケージを完成できません。状態を確認して再実行してください。");
            }

            LegacyGraphFinalPackageStatusText.Text =
                "グラフ確定パッケージを検証しました。readyフォルダーへ完成品を移しています…";
            var readyResult = await importOutputPackageStore.CompleteAsync(
                unifiedImportOutputSession,
                result,
                fp5VehicleImageMapping.ReportPath,
                cancellationToken);
            LegacyGraphFinalPackageStatusText.Text =
                result.ImageCount > 0
                    ? "グラフ確定後の登録前パッケージを作成しました。画像も再検証して同梱済みです。Web API・DB・画像アップロードはまだ行っていません。"
                    : "グラフ確定後の登録前パッケージを作成しました。Web API・DB・画像アップロードはまだ行っていません。";
            LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#17643A");
            legacyGraphFinalPackagePath = readyResult?.ReadyPath ?? result.PackagePath;
            LegacyGraphOpenFinalPackageButton.IsEnabled = true;
            LegacyGraphFinalPackageNextStepText.Text =
                "次の操作: Webアプリの「ABACUS登録前パッケージをプレビュー」で、このフォルダーを選択してください。確認文字列を入力するまでは登録されません。";
            LegacyGraphFinalPackageNextStepText.Foreground = ToBrush("#1E40AF");
            LegacyGraphFinalPackageResultText.Text =
                $"保存先: {readyResult?.ReadyPath ?? result.PackagePath}\n" +
                $"マニフェスト: {readyResult?.ReadyManifestPath ?? result.ManifestPath}\n" +
                $"顧客CSV: {(readyResult is null ? result.CustomersCsvPath : Path.Combine(readyResult.ReadyPath, "data", "customers.csv"))}\n" +
                $"車両CSV: {(readyResult is null ? result.VehiclesCsvPath : Path.Combine(readyResult.ReadyPath, "data", "vehicles.csv"))}\n" +
                $"販売CSV: {(readyResult is null ? result.SalesCsvPath : Path.Combine(readyResult.ReadyPath, "data", "sales-documents.csv"))}\n" +
                $"整備CSV: {(readyResult is null ? result.MaintenanceCsvPath : Path.Combine(readyResult.ReadyPath, "data", "maintenance-documents.csv"))}\n" +
                $"書類リンク: {(readyResult is null ? result.DocumentLinksPath : Path.Combine(readyResult.ReadyPath, "mappings", "document-links.json"))}\n" +
                (result.ImageAttachmentsPath is null ? "" : $"画像対応表: {(readyResult is null ? result.ImageAttachmentsPath : Path.Combine(readyResult.ReadyPath, "mappings", "image-attachments.json"))}\n") +
                $"顧客: {result.CustomerRowCount:N0}行 / 車両: {result.VehicleRowCount:N0}行 / " +
                $"販売書類: {result.SalesRowCount:N0}行 / 整備書類: {result.MaintenanceRowCount:N0}行\n" +
                 $"車両情報なし: {result.VehiclelessDocumentCount:N0}件 / 除外書類: {result.ExcludedDocumentCount:N0}件 / " +
                 $"未確定由来の除外書類: {result.UnresolvedDocumentCount:N0}件 / 明示除外由来: {result.ExplicitExcludedDocumentCount:N0}件 / " +
                 $"除外顧客: {result.ExcludedCustomerCount:N0}件 / 除外車両: {result.ExcludedVehicleCount:N0}件（画像 {result.ExcludedVehicleImageCount:N0}件） / 画像: {result.ImageCount:N0}件\n" +
                $"マニフェスト SHA-256: {result.ManifestSha256}";
        }
        catch (OperationCanceledException)
        {
            legacyGraphFinalPackageHasError = true;
            LegacyGraphFinalPackageResultText.Text = "";
            LegacyGraphFinalPackageStatusText.Text =
                "登録前パッケージの作成をキャンセルしました。保存用ABACUS原本は変更していません。";
            LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#A61B1B");
            LegacyGraphFinalPackageNextStepText.Text =
                "次の操作: 確定内容を確認してから、もう一度「確定内容から登録前パッケージを作成」を実行してください。";
            LegacyGraphFinalPackageNextStepText.Foreground = ToBrush("#A61B1B");
        }
        catch (Exception exception)
        {
            // async void のイベントハンドラーから例外を外へ出すと、画面には
            // 「何も起きなかった」ように見え、ready配下には作成開始時の
            // 空フォルダーだけが残ります。ここでは想定外の例外も含めて
            // 必ず画面へ表示し、再試行可能な状態へ戻します。
            legacyGraphFinalPackageHasError = true;
            LegacyGraphFinalPackageResultText.Text = "";
            LegacyGraphFinalPackageStatusText.Text =
                $"グラフ確定パッケージを作成できません: {exception.Message}" +
                $"（{exception.GetType().Name}）";
            LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#A61B1B");
            LegacyGraphFinalPackageNextStepText.Text =
                "次の操作: エラー内容を確認し、グラフを再検証してからもう一度パッケージを作成してください。";
            LegacyGraphFinalPackageNextStepText.Foreground = ToBrush("#A61B1B");
        }
        finally
        {
            legacyGraphFinalPackageBusy = false;
            EndOperation();
            UpdateLegacyGraphImportConfirmationButton();
            ScheduleDeferredLegacyGraphCheckpointSave();
            ScheduleLegacyGraphCheckpointSave();
        }
    }

    private void LegacyGraphOpenFinalPackageButton_Click(object sender, RoutedEventArgs e)
    {
        var packagePath = legacyGraphFinalPackagePath;
        if (string.IsNullOrWhiteSpace(packagePath) || !Directory.Exists(packagePath))
        {
            LegacyGraphFinalPackageStatusText.Text =
                "作成済みパッケージの保存先が見つかりません。もう一度パッケージを作成してください。";
            LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#A61B1B");
            LegacyGraphOpenFinalPackageButton.IsEnabled = false;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetFullPath(packagePath),
                UseShellExecute = true,
            });
        }
        catch (InvalidOperationException exception)
        {
            LegacyGraphFinalPackageStatusText.Text = $"保存先フォルダーを開けません: {exception.Message}";
            LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#A61B1B");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            LegacyGraphFinalPackageStatusText.Text = $"保存先フォルダーを開けません: {exception.Message}";
            LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#A61B1B");
        }
    }

    private string GetLegacyGraphCustomerApprovalKey(
        AbacusLegacyExportCandidateGraphCustomer customer) =>
        GetLegacyGraphLogicalCustomerKey(customer);

    private string GetLegacyGraphStoredCustomerReviewState(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var key = GetLegacyGraphCustomerApprovalKey(customer);
        if (legacyGraphCustomerReviewStates.TryGetValue(key, out var state) &&
            LegacyGraphCustomerReviewStateValues.IsSupported(state))
        {
            return state;
        }

        return legacyGraphCustomerApprovalStates.TryGetValue(key, out var approved) && approved
            ? LegacyGraphCustomerReviewStateValues.Approved
            : LegacyGraphCustomerReviewStateValues.Unreviewed;
    }

    private bool IsLegacyGraphCustomerReviewApprovedForKey(string key) =>
        legacyGraphCustomerReviewStates.TryGetValue(key, out var state) &&
        string.Equals(state, LegacyGraphCustomerReviewStateValues.Approved, StringComparison.Ordinal) ||
        legacyGraphCustomerApprovalStates.TryGetValue(key, out var approved) && approved;

    private void SetLegacyGraphCustomerReviewState(
        AbacusLegacyExportCandidateGraphCustomer customer,
        string state)
    {
        if (!LegacyGraphCustomerReviewStateValues.IsSupported(state))
        {
            throw new ArgumentException("顧客確認状態が不正です。", nameof(state));
        }

        var key = GetLegacyGraphCustomerApprovalKey(customer);
        legacyGraphCustomerReviewStates[key] = state;
        // 旧チェックポイント・最終化処理との互換性のため、承認済みだけをboolにも投影します。
        legacyGraphCustomerApprovalStates[key] =
            string.Equals(state, LegacyGraphCustomerReviewStateValues.Approved, StringComparison.Ordinal);
    }

    private LegacyGraphCustomerReviewSnapshot GetLegacyGraphCustomerReviewSnapshot(
        AbacusLegacyExportCandidateGraphCustomer customer,
        bool useGraphStructuralMergeIntent = false)
    {
        customer = GetLegacyGraphSourceCustomer(customer);
        var mergeKey = GetLegacyCustomerMergeKey(customer);
        var mergeCandidates = GetLegacyGraphCustomerMergeCandidates(customer);
        var hasMergeIntent = useGraphStructuralMergeIntent
            ? HasLegacyGraphStructuralMergeIntent(customer, mergeCandidates)
            : HasLegacyMatchingCustomerMergeIntent(customer, mergeCandidates);
        var candidateCustomerIds = mergeCandidates
            .Select(candidate => candidate.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        var hasCompleteMergeDraft = hasMergeIntent &&
                                     HasCompleteLegacyGraphCustomerMergeDraft(
                                         mergeKey,
                                         candidateCustomerIds);
        var requiresCustomerPreview = LegacyMatchingWorkflow.RequiresCustomerPreview(
            hasMergeIntent,
            hasCompleteMergeDraft);
        var recommendationGate = GetLegacyGraphCustomerRecommendationGate(
            customer,
            candidateCustomerIds,
            hasCompleteMergeDraft);
        var customerGate = LegacyMatchingWorkflow.EvaluateCustomerApprovalGate(
            requiresCustomerPreview);
        var storedState = GetLegacyGraphStoredCustomerReviewState(customer);
        var status = string.Equals(storedState, LegacyGraphCustomerReviewStateValues.Approved, StringComparison.Ordinal) &&
                     !customerGate.CanApprove
            ? LegacyGraphCustomerReviewStateValues.NeedsReview
            : storedState;

        var reason = customerGate.RequiresCustomerPreview
            ? "統合後に使用する顧客情報を決定してください。"
            : string.Equals(status, LegacyGraphCustomerReviewStateValues.NeedsReview, StringComparison.Ordinal)
            ? "承認後に内容が変更されたため、再確認が必要です。"
            : "まだ顧客単位の確認を完了していません。";

        return new LegacyGraphCustomerReviewSnapshot(
            GetLegacyGraphCustomerApprovalKey(customer),
            recommendationGate.PendingCount,
            recommendationGate.HeldCount,
            customerGate.RequiresCustomerPreview,
            customerGate.CanApprove,
            status,
            reason);
    }

    private LegacyCustomerReviewGate GetLegacyGraphCustomerRecommendationGate(
        AbacusLegacyExportCandidateGraphCustomer customer,
        IReadOnlySet<string> candidateCustomerIds,
        bool hasCompleteMergeDraft)
    {
        var reviewRecommendations = GetLegacyGraphCustomerReviewRecommendations(customer)
            .Where(candidate => !hasCompleteMergeDraft ||
                                LegacyMatchingCategoryKinds.GetKind(candidate) != LegacyMatchingCategoryKinds.Customer ||
                                !candidateCustomerIds.Contains(candidate.SubjectId) ||
                                !candidateCustomerIds.Contains(candidate.TargetId))
            .ToArray();
        var reviewDecisions = reviewRecommendations.ToDictionary(
            candidate => candidate.CandidateId,
            GetLegacyGraphRecommendationDecision,
            StringComparer.OrdinalIgnoreCase);
        return LegacyMatchingWorkflow.EvaluateCustomerReviewGate(
            reviewRecommendations,
            reviewDecisions,
            requiresCustomerPreview: false);
    }

    private IReadOnlyList<LegacyMatchingCustomerCategoryReviewSummary>
        GetLegacyGraphMatchingCustomerCategoryReviewSummaries(
            AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var candidates = GetLegacyGraphMatchingRecommendations(
            customer,
            includeCompletedCustomerCandidates: true);
        return LegacyMatchingCategoryKinds.All
            .Select(kind =>
            {
                var categoryCandidates = candidates
                    .Where(candidate => LegacyMatchingCategoryKinds.GetKind(candidate) == kind)
                    .ToArray();
                var pending = categoryCandidates.Count(candidate =>
                    GetLegacyGraphRecommendationDecision(candidate) == AbacusRecommendationDecisionValues.Pending);
                var held = categoryCandidates.Count(candidate =>
                    GetLegacyGraphRecommendationDecision(candidate) == AbacusRecommendationDecisionValues.Hold);
                var completed = categoryCandidates.Count(candidate =>
                    GetLegacyGraphRecommendationDecision(candidate) is
                        AbacusRecommendationDecisionValues.Approved or
                        AbacusRecommendationDecisionValues.Rejected);
                return new LegacyMatchingCustomerCategoryReviewSummary(
                    kind,
                    LegacyMatchingCategoryKinds.GetLabel(kind),
                    categoryCandidates.Length,
                    pending,
                    held,
                    completed);
            })
            .ToArray();
    }

    private bool HasCompleteLegacyGraphCustomerMergeDraft(
        string mergeKey,
        IReadOnlyCollection<string> customerIds)
    {
        if (!legacyGraphCustomerMergeDrafts.TryGetValue(mergeKey, out var draft))
        {
            return false;
        }

        if (!string.Equals(draft.GroupKey, mergeKey, StringComparison.Ordinal))
        {
            return false;
        }

        var candidateIds = customerIds.ToHashSet(StringComparer.Ordinal);
        var draftCustomerIds = draft.CandidateCustomerIds.ToHashSet(StringComparer.Ordinal);
        if (!candidateIds.SetEquals(draftCustomerIds))
        {
            return false;
        }

        return GetLegacyGraphCustomerMergeFields()
            .All(field => draft.FieldSelections.ContainsKey(field.Key) &&
                          draft.SelectedValues.ContainsKey(field.Key));
    }

    private bool IsLegacyGraphCustomerApproved(
        AbacusLegacyExportCandidateGraphCustomer customer) =>
        string.Equals(
            GetLegacyGraphCustomerReviewSnapshot(
                customer,
                useGraphStructuralMergeIntent: true).Status,
            LegacyGraphCustomerReviewStateValues.Approved,
            StringComparison.Ordinal);

    private bool CanApproveLegacyGraphCustomer(
        AbacusLegacyExportCandidateGraphCustomer customer) =>
        GetLegacyGraphCustomerReviewSnapshot(
            customer,
            useGraphStructuralMergeIntent: true).CanApprove;

    private void RefreshLegacyGraphCustomerApproval(
        AbacusLegacyExportCandidateGraphCustomer? customer)
    {
        RefreshLegacyGraphCustomerApprovalPanel(customer);
        RefreshLegacyMatchingCustomerApprovalPanel(customer);
    }

    private void RefreshLegacyGraphCustomerApprovalPanel(
        AbacusLegacyExportCandidateGraphCustomer? customer)
    {
        if (LegacyGraphCustomerApproveButton is null ||
            LegacyGraphMergeButton is null ||
            LegacyGraphCustomerApprovalReasonText is null)
        {
            return;
        }

        var hasGraphCustomer = customer is not null &&
                               !string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase);
        LegacyGraphMergeButton.Visibility = Visibility.Collapsed;
        LegacyGraphMergeButton.IsEnabled = false;
        LegacyGraphMergeButton.Content = "顧客情報をプレビュー";
        LegacyGraphCustomerApprovalReasonText.Text = "";
        LegacyGraphCustomerApprovalReasonText.Visibility = Visibility.Collapsed;
        LegacyGraphCustomerApproveButton.Visibility = hasGraphCustomer
            ? Visibility.Visible
            : Visibility.Collapsed;
        LegacyGraphCustomerApproveButton.IsEnabled = false;
        LegacyGraphCustomerApproveButton.Content = "この顧客を確定";
        if (!hasGraphCustomer)
        {
            return;
        }

        var sourceCustomer = GetLegacyGraphSourceCustomer(customer!);
        var mergeKey = GetLegacyCustomerMergeKey(sourceCustomer);
        var mergeCandidates = GetLegacyGraphCustomerMergeCandidates(sourceCustomer);
        var hasMergeIntent = HasLegacyGraphStructuralMergeIntent(sourceCustomer, mergeCandidates);
        var hasMergeGroup = hasMergeIntent;
        var hasMergeDraft = hasMergeIntent &&
                            HasCompleteLegacyGraphCustomerMergeDraft(
                                mergeKey,
                                mergeCandidates.Select(candidate => candidate.CustomerId).ToHashSet(StringComparer.Ordinal));
        var snapshot = GetLegacyGraphCustomerReviewSnapshot(
            sourceCustomer,
            useGraphStructuralMergeIntent: true);
        var approved = string.Equals(
            snapshot.Status,
            LegacyGraphCustomerReviewStateValues.Approved,
            StringComparison.Ordinal);
        LegacyGraphMergeButton.Visibility = hasMergeGroup
            ? Visibility.Visible
            : Visibility.Collapsed;
        LegacyGraphMergeButton.IsEnabled = hasMergeGroup;
        LegacyGraphMergeButton.Content = hasMergeDraft
            ? "採用内容を再確認"
            : "顧客情報をプレビュー";
        LegacyGraphCustomerApproveButton.Content = approved ? "顧客確認済み" : "この顧客を確定";
        LegacyGraphCustomerApproveButton.IsEnabled = !approved && snapshot.CanApprove;
        LegacyGraphCustomerApprovalReasonText.Visibility = Visibility.Visible;
        var recommendationQueueInfo = BuildLegacyMatchingRecommendationQueueInfo(
            snapshot.PendingCount,
            snapshot.HeldCount);
        if (approved)
        {
            LegacyGraphCustomerApprovalReasonText.Text =
                AppendLegacyMatchingRecommendationQueueInfo(
                    "✓ 顧客情報の確認と顧客単位の確定が完了しています。",
                    recommendationQueueInfo);
            LegacyGraphCustomerApprovalReasonText.Foreground = ToBrush("#1D4ED8");
        }
        else if (snapshot.CanApprove)
        {
            LegacyGraphCustomerApprovalReasonText.Text =
                AppendLegacyMatchingRecommendationQueueInfo(
                    "✓ 顧客の確定条件を満たしています。",
                    recommendationQueueInfo);
            LegacyGraphCustomerApprovalReasonText.Foreground = ToBrush("#17643A");
        }
        else
        {
            LegacyGraphCustomerApprovalReasonText.Text =
                AppendLegacyMatchingRecommendationQueueInfo(
                    $"確定できません: {snapshot.Reason}",
                    recommendationQueueInfo);
            LegacyGraphCustomerApprovalReasonText.Foreground = ToBrush("#805B10");
        }
    }

    private void RefreshLegacyMatchingCustomerApprovalPanel(
        AbacusLegacyExportCandidateGraphCustomer? customer)
    {
        if (LegacyMatchingCustomerApprovalBorder is null ||
            LegacyMatchingCustomerReviewSummaryText is null ||
            LegacyMatchingCustomerApprovalStatusText is null ||
            LegacyMatchingCustomerApprovalStateBorder is null ||
            LegacyMatchingCustomerApprovalStateText is null ||
            LegacyMatchingCustomerApproveButton is null)
        {
            return;
        }

        var hasMatchingCustomer = customer is not null &&
                                  string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase);
        LegacyMatchingCustomerApprovalBorder.Visibility = hasMatchingCustomer
            ? Visibility.Visible
            : Visibility.Collapsed;
        LegacyMatchingCustomerApproveButton.IsEnabled = false;
        LegacyMatchingCustomerApprovalStateText.Text = "";
        if (customer is null)
        {
            LegacyMatchingCustomerReviewSummaryText.Text = "顧客単位の確認対象はありません。";
            LegacyMatchingCustomerApprovalStatusText.Text = "顧客を選択すると、顧客単位の確定状態を表示します。";
            LegacyMatchingCustomerApprovalBorder.Background = ToBrush("#F4F7FB");
            LegacyMatchingCustomerApprovalBorder.BorderBrush = ToBrush("#CBD5E1");
            LegacyMatchingCustomerApprovalStatusText.Foreground = ToBrush("#52647A");
            RefreshLegacyMatchingCustomerMergePreviewAction(null);
            return;
        }

        var snapshot = GetLegacyGraphCustomerReviewSnapshot(customer);
        var categorySummaries = GetLegacyGraphMatchingCustomerCategoryReviewSummaries(customer);
        LegacyMatchingCustomerReviewSummaryText.Text = BuildLegacyMatchingCustomerReviewSummaryText(categorySummaries);
        RefreshLegacyMatchingCustomerMergePreviewAction(customer);
        var approved = string.Equals(
            snapshot.Status,
            LegacyGraphCustomerReviewStateValues.Approved,
            StringComparison.Ordinal);
        if (approved)
        {
            var recommendationQueueInfo = BuildLegacyMatchingRecommendationQueueInfo(
                categorySummaries.Sum(summary => summary.Pending),
                categorySummaries.Sum(summary => summary.Held));
            LegacyMatchingCustomerReviewSummaryText.Text = recommendationQueueInfo.Length == 0
                ? ""
                : BuildLegacyMatchingCustomerReviewSummaryText(categorySummaries);
            LegacyMatchingCustomerApprovalStatusText.Text = AppendLegacyMatchingRecommendationQueueInfo(
                "✓ この顧客の確認は完了しています。",
                recommendationQueueInfo);
            LegacyMatchingCustomerApprovalStateText.Text = "承認済み";
            LegacyMatchingCustomerApprovalStateBorder.Background = ToBrush("#EAF2FF");
            LegacyMatchingCustomerApprovalStateBorder.BorderBrush = ToBrush("#93C5FD");
            LegacyMatchingCustomerApprovalStateText.Foreground = ToBrush("#1D4ED8");
            LegacyMatchingCustomerApproveButton.Content = "顧客確認済み";
            LegacyMatchingCustomerApprovalBorder.Background = ToBrush("#EAF2FF");
            LegacyMatchingCustomerApprovalBorder.BorderBrush = ToBrush("#93C5FD");
            LegacyMatchingCustomerApprovalStatusText.Foreground = ToBrush("#1D4ED8");
            return;
        }

        LegacyMatchingCustomerApproveButton.Content = "この顧客を確定";
        LegacyMatchingCustomerApprovalStatusText.Text =
            BuildLegacyMatchingCustomerApprovalStatusText(categorySummaries, snapshot);
        LegacyMatchingCustomerApprovalStateText.Text = snapshot.Status ==
                LegacyGraphCustomerReviewStateValues.NeedsReview
            ? "再確認待ち"
            : snapshot.CanApprove
            ? "確認待ち"
            : "未確認";
        LegacyMatchingCustomerApprovalStateBorder.Background = ToBrush("#FFF7ED");
        LegacyMatchingCustomerApprovalStateBorder.BorderBrush = ToBrush("#FCD34D");
        LegacyMatchingCustomerApprovalStateText.Foreground = ToBrush("#92400E");
        LegacyMatchingCustomerApprovalBorder.Background = ToBrush("#FFF7ED");
        LegacyMatchingCustomerApprovalBorder.BorderBrush = ToBrush("#FCD34D");
        LegacyMatchingCustomerApprovalStatusText.Foreground = ToBrush("#92400E");
        LegacyMatchingCustomerApproveButton.IsEnabled = snapshot.CanApprove;
    }

    private static string BuildLegacyMatchingCustomerReviewSummaryText(
        IReadOnlyList<LegacyMatchingCustomerCategoryReviewSummary> summaries)
    {
        var held = summaries.Sum(summary => summary.Held);
        var categoryParts = summaries.Select(summary =>
        {
            var isComplete = summary.Total == 0 || summary.Completed >= summary.Total;
            return $"{summary.Label} {(isComplete ? "✓ " : "")}{summary.Completed:N0}/{summary.Total:N0}";
        });
        return string.Join("   ", categoryParts.Append($"保留 {held:N0}"));
    }

    private void ResolveLegacyGraphCustomerRecommendationsAtApproval(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        // 顧客を確定した時点の構成を完成形として採用するため、
        // この論理顧客に関係する未処理・保留候補だけを既存の却下状態へ移します。
        // 候補元の顧客・車両・書類や、未確定トレイのデータは変更しません。
        var unresolvedRecommendations = GetLegacyGraphCustomerReviewRecommendations(customer)
            .Where(candidate => GetLegacyGraphRecommendationDecision(candidate) is
                AbacusRecommendationDecisionValues.Pending or
                AbacusRecommendationDecisionValues.Hold)
            .ToArray();
        foreach (var candidate in unresolvedRecommendations)
        {
            var groupCandidates = GetLegacyGraphRecommendationGroupMembers(candidate);
            if (groupCandidates.Count == 0)
            {
                groupCandidates = [candidate];
            }

            // 顧客確定に伴う一括解決は、通常の「別人」「却下」操作ではありません。
            // Decision は既存のRejectedを再利用しますが、個別判断の履歴は追加しません。
            foreach (var groupCandidate in groupCandidates)
            {
                SetLegacyGraphRecommendationState(
                    groupCandidate,
                    AbacusRecommendationDecisionValues.Rejected,
                    resolutionReason: LegacyGraphRecommendationLifecycleReconciler.CustomerApprovalResolutionReason);
            }
        }
    }

    private static string BuildLegacyMatchingRecommendationQueueInfo(
        int pending,
        int held)
    {
        var parts = new List<string>();
        if (pending > 0)
        {
            parts.Add($"未処理 {pending:N0}件");
        }

        if (held > 0)
        {
            parts.Add($"保留 {held:N0}件");
        }

        return parts.Count == 0
            ? ""
            : $"マッチング候補: {string.Join("・", parts)}（顧客確定とは別に処理できます）";
    }

    private static string AppendLegacyMatchingRecommendationQueueInfo(
        string message,
        string recommendationQueueInfo) =>
        string.IsNullOrWhiteSpace(recommendationQueueInfo)
            ? message
            : $"{message}\n{recommendationQueueInfo}";

    private static string BuildLegacyMatchingCustomerApprovalStatusText(
        IReadOnlyList<LegacyMatchingCustomerCategoryReviewSummary> summaries,
        LegacyGraphCustomerReviewSnapshot snapshot)
    {
        if (string.Equals(
                snapshot.Status,
                LegacyGraphCustomerReviewStateValues.NeedsReview,
                StringComparison.Ordinal))
        {
            return $"⚠ 再確認待ち: {snapshot.Reason}";
        }

        if (snapshot.RequiresCustomerPreview)
        {
            return $"⚠ {snapshot.Reason}";
        }

        var pendingCount = summaries.Sum(summary => summary.Pending);
        var heldCount = summaries.Sum(summary => summary.Held);
        var recommendationQueueInfo = BuildLegacyMatchingRecommendationQueueInfo(
            pendingCount,
            heldCount);
        if (snapshot.CanApprove)
        {
            return AppendLegacyMatchingRecommendationQueueInfo(
                "✓ 顧客の確定条件を満たしています。",
                recommendationQueueInfo);
        }

        var pendingSummaries = summaries
            .Where(summary => summary.Pending > 0)
            .ToArray();
        if (pendingSummaries.Length == 1 && heldCount == 0)
        {
            var pending = pendingSummaries[0];
            return $"⚠ {pending.Label}の未処理が{pending.Pending:N0}件あります。";
        }

        var remainingParts = pendingSummaries
            .Select(summary => $"{summary.Label}{summary.Pending:N0}件")
            .ToList();
        if (heldCount > 0)
        {
            remainingParts.Add($"保留{heldCount:N0}件");
        }

        return remainingParts.Count > 0
            ? $"⚠ {string.Join("・", remainingParts)}が残っています。"
            : snapshot.Reason;
    }

    private bool ApproveLegacyGraphCustomer(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (!CanMutateLegacyGraph)
        {
            return false;
        }

        var sourceCustomer = GetLegacyGraphSourceCustomer(customer);
        var useGraphStructuralMergeIntent =
            !string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase);
        var snapshot = GetLegacyGraphCustomerReviewSnapshot(
            sourceCustomer,
            useGraphStructuralMergeIntent);
        if (string.Equals(snapshot.Status, LegacyGraphCustomerReviewStateValues.Approved, StringComparison.Ordinal) &&
            snapshot.CanApprove)
        {
            return true;
        }

        if (!snapshot.CanApprove)
        {
            LegacyGraphStatusText.Text =
                snapshot.Reason;
            LegacyGraphStatusText.Foreground = ToBrush("#805B10");
            return false;
        }

        if (!TryApplyLegacyGraphCustomerMerge(sourceCustomer, out _))
        {
            return false;
        }

        var finalSnapshot = GetLegacyGraphCustomerReviewSnapshot(
            sourceCustomer,
            useGraphStructuralMergeIntent);
        if (!finalSnapshot.CanApprove)
        {
            LegacyGraphStatusText.Text = finalSnapshot.Reason;
            LegacyGraphStatusText.Foreground = ToBrush("#805B10");
            return false;
        }

        SetLegacyGraphCustomerReviewState(
            sourceCustomer,
            LegacyGraphCustomerReviewStateTransition.MarkApproved());
        ResolveLegacyGraphCustomerRecommendationsAtApproval(sourceCustomer);
        InvalidateLegacyGraphImportConfirmation();
        LegacyGraphStatusText.Text =
            $"顧客 {GetLegacyGraphCustomerDisplayName(sourceCustomer)} の確認を完了しました。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
        RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(sourceCustomer));
        if (!string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            var displayCustomer = GetLegacyGraphDisplayCustomer(sourceCustomer);
            legacyGraphSelectedItem = displayCustomer;
            UpdateLegacyGraphInspector(displayCustomer);
            RenderLegacyGraphCustomer(displayCustomer);
        }
        RefreshLegacyMatchingView();
        ScheduleLegacyGraphCheckpointSave();
        return true;
    }

    private bool TryApproveAllLegacyGraphCustomers(
        out int approvedCustomerGroupCount,
        out string approvalFailureReason)
    {
        approvedCustomerGroupCount = 0;
        approvalFailureReason = "";
        if (legacyExportCandidateGraphResult is null)
        {
            approvalFailureReason = "候補パッケージが読み込まれていないため、顧客を一括確定できません。";
            return false;
        }

        var approvalTargets = legacyExportCandidateGraphResult.Customers
            .Where(customer => !legacyGraphTrashCustomerIds.Contains(customer.CustomerId))
            .GroupBy(GetLegacyGraphLogicalCustomerKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var sourceCustomer = group.First();
                return (
                    Customer: sourceCustomer,
                    Snapshot: GetLegacyGraphCustomerReviewSnapshot(
                        sourceCustomer,
                        useGraphStructuralMergeIntent: true));
            })
            .ToArray();
        var blockedTargets = approvalTargets
            .Where(target => !target.Snapshot.CanApprove)
            .ToArray();
        if (blockedTargets.Length > 0)
        {
            var blockedNames = string.Join(
                "、",
                blockedTargets
                    .Take(5)
                    .Select(target => GetLegacyGraphCustomerDisplayName(target.Customer)));
            var suffix = blockedTargets.Length > 5 ? "ほか" : "";
            approvalFailureReason =
                $"顧客確認未完了のため一括確定できません。対象: {blockedNames}{suffix}。" +
                $"理由: {blockedTargets[0].Snapshot.Reason}";
            return false;
        }

        foreach (var target in approvalTargets)
        {
            SetLegacyGraphCustomerReviewState(
                target.Customer,
                LegacyGraphCustomerReviewStateTransition.MarkApproved());
            // 一括確定はグラフの「この顧客を確定」と同じ扱いにし、
            // 現在の構成に関係する未処理・保留候補も既存の却下状態へ移します。
            ResolveLegacyGraphCustomerRecommendationsAtApproval(target.Customer);
        }

        approvedCustomerGroupCount = approvalTargets.Length;
        InvalidateLegacyGraphImportConfirmation();
        return true;
    }

    private void LegacyMatchingCustomerApproveButton_Click(object sender, RoutedEventArgs e)
    {
        var customer = GetLegacyGraphMatchingCustomer();
        if (customer is not null)
        {
            ApproveLegacyGraphCustomer(GetLegacyGraphSourceCustomer(customer));
        }
    }

    private void LegacyGraphCustomerApproveButton_Click(object sender, RoutedEventArgs e)
    {
        var customer = legacyGraphSelectedItem as AbacusLegacyExportCandidateGraphCustomer;
        if (customer is not null)
        {
            ApproveLegacyGraphCustomer(GetLegacyGraphSourceCustomer(customer));
        }
    }

    private void InvalidateLegacyGraphCustomerApproval(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var key = GetLegacyGraphCustomerApprovalKey(customer);
        var hadReviewState = legacyGraphCustomerReviewStates.ContainsKey(key) ||
                             legacyGraphCustomerApprovalStates.TryGetValue(key, out var approved) && approved;
        if (hadReviewState)
        {
            SetLegacyGraphCustomerReviewState(
                customer,
                LegacyGraphCustomerReviewStateTransition.MarkNeedsReview(
                    GetLegacyGraphStoredCustomerReviewState(customer)));
        }
    }

    private void InvalidateLegacyGraphCustomerApprovalForMergeKey(string mergeKey)
    {
        if (TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup))
        {
            foreach (var customerId in mergeGroup.CustomerIds)
            {
                if (FindLegacyGraphCustomerById(customerId) is { } customer)
                {
                    InvalidateLegacyGraphCustomerApproval(customer);
                }
            }

            return;
        }

        const string customerPrefix = "customer:";
        if (mergeKey.StartsWith(customerPrefix, StringComparison.Ordinal) &&
            FindLegacyGraphCustomerById(mergeKey[customerPrefix.Length..]) is { } standaloneCustomer)
        {
            InvalidateLegacyGraphCustomerApproval(standaloneCustomer);
        }
    }

    private int GetLegacyGraphUnapprovedCustomerCount()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return 0;
        }

        return legacyExportCandidateGraphResult.Customers
            .Where(customer => !legacyGraphTrashCustomerIds.Contains(customer.CustomerId))
            .GroupBy(GetLegacyGraphLogicalCustomerKey, StringComparer.Ordinal)
            .Count(group => !IsLegacyGraphCustomerApproved(group.First()));
    }

    private AbacusLegacyGraphFinalizationSnapshot BuildLegacyGraphFinalizationSnapshot()
    {
        if (legacyExportCandidateGraphResult is null || !legacyGraphImportConfirmed)
        {
            throw new InvalidDataException("グラフのインポート内容が最終確定されていません。");
        }

        var groups = legacyExportCandidateGraphResult.Customers
            .OrderBy(customer => customer.CustomerId, StringComparer.Ordinal)
            .GroupBy(GetLegacyGraphLogicalCustomerKey, StringComparer.Ordinal)
            .Select(grouping =>
            {
                var sourceCustomers = grouping.ToArray();
                LegacyGraphCustomerMergeGroup? mergeGroup = null;
                var hasMergeGroup = IsLegacyGraphLogicalCustomerGroup(grouping.Key) &&
                                    TryGetLegacyGraphMergeGroup(grouping.Key, out mergeGroup) &&
                                    mergeGroup.CustomerIds.Count > 1;
                var displayCustomer = hasMergeGroup
                    ? GetLegacyGraphDisplayCustomer(sourceCustomers[0])
                    : sourceCustomers[0];
                return new AbacusLegacyGraphFinalCustomerGroup(
                    grouping.Key,
                    hasMergeGroup ? mergeGroup!.Origin : "single",
                    IsLegacyGraphCustomerApproved(sourceCustomers[0]),
                    sourceCustomers.Select(customer => customer.CustomerId).ToArray(),
                    displayCustomer.CustomerId,
                    displayCustomer.CustomerNumber,
                    GetLegacyGraphCustomerDisplayName(displayCustomer),
                    displayCustomer.NameKana,
                    displayCustomer.PhoneNumber,
                    displayCustomer.EmailAddress,
                    displayCustomer.PostalCode,
                    displayCustomer.Address,
                    displayCustomer.Memo);
            })
            .ToArray();

        return new AbacusLegacyGraphFinalizationSnapshot(
            groups,
            new Dictionary<string, string>(legacyGraphManualDocumentLinks, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(legacyGraphManualDocumentCustomerLinks, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(legacyGraphExcludedDocumentKeys, StringComparer.OrdinalIgnoreCase),
            legacyGraphImportConfirmed,
            new Dictionary<string, string>(legacyGraphDocumentLinkMethods, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(legacyGraphDocumentLinkReasons, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(legacyGraphManualVehicleCustomerLinks, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(legacyGraphTrayVehicleIds, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(legacyGraphTrashDocumentKeys, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(legacyGraphTrashVehicleIds, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(legacyGraphTrashCustomerIds, StringComparer.OrdinalIgnoreCase));
    }

    private LegacyGraphCustomerMergeGroup[] GetLegacyGraphPendingMergeGroups() =>
        legacyGraphCustomerMergeGroups.Values
            .Where(IsLegacyGraphPendingMergeGroup)
            .OrderBy(group => group.GroupId, StringComparer.Ordinal)
            .ToArray();

    private bool IsLegacyGraphPendingMergeGroup(LegacyGraphCustomerMergeGroup group)
    {
        var activeCustomerIds = group.CustomerIds
            .Where(customerId => !legacyGraphTrashCustomerIds.Contains(customerId))
            .ToArray();
        return LegacyGraphTemporaryMergeGroupState.IsPending(
            activeCustomerIds.Length,
            IsLegacyGraphLogicalCustomerGroup(group.GroupId),
            legacyGraphAppliedCustomerMergeKeys.Contains(group.GroupId));
    }

    private int GetLegacyGraphPendingMergeGroupCount() =>
        GetLegacyGraphPendingMergeGroups().Length;

    private int GetLegacyGraphPendingDocumentCount()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return 0;
        }

        return legacyExportCandidateGraphResult.AllDocuments.Count(document =>
            !IsLegacyGraphDocumentInTray(document) &&
            IsLegacyGraphDocumentUnconnected(document));
    }

    private void UpdateLegacyGraphImportConfirmationButton()
    {
        if (LegacyGraphFinalizeImportButton is null || LegacyGraphFinalizeImportStatusText is null ||
            LegacyGraphApproveAllMergeButton is null || LegacyGraphOpenFinalPackageButton is null ||
            LegacyGraphFinalPackageNextStepText is null || LegacyGraphWorkStatusText is null ||
            LegacyGraphSaveWorkButton is null || LegacyGraphOpenWorkButton is null)
        {
            return;
        }

        if (legacyExportCandidateGraphResult is null)
        {
            LegacyGraphFinalizeImportButton.IsEnabled = false;
            LegacyGraphFinalizeImportButton.Content = "インポート内容を確定";
            LegacyGraphFinalizeImportStatusText.Text =
                "候補パッケージを読み込むと、顧客統合と書類・ノード操作の完了後に確定できます。";
            LegacyGraphCreateFinalPackageButton.IsEnabled = false;
            legacyGraphFinalPackageHasError = false;
            LegacyGraphFinalPackageStatusText.Text =
                "①インポート内容を確定し、②確定内容から登録前パッケージを作成します。作成後にWeb側でフォルダーを選択します。";
            LegacyGraphFinalPackageNextStepText.Text =
                "次の操作: 候補パッケージを読み込み、顧客統合・書類・ノードの操作を完了してください。";
            LegacyGraphFinalPackageNextStepText.Foreground = ToBrush("#1E40AF");
            LegacyGraphOpenFinalPackageButton.IsEnabled = false;
            LegacyGraphApproveAllMergeButton.IsEnabled = false;
            LegacyGraphApproveAllMergeButton.Content = "統合候補・顧客を一括確定";
            LegacyGraphSaveWorkButton.IsEnabled = false;
            LegacyGraphOpenWorkButton.IsEnabled = !legacyGraphFinalPackageBusy && !legacyGraphBulkMergeBusy;
            return;
        }

        LegacyGraphSaveWorkButton.IsEnabled = unifiedImportOutputSession is not null &&
                                               !legacyGraphFinalPackageBusy &&
                                               !legacyGraphBulkMergeBusy;
        LegacyGraphOpenWorkButton.IsEnabled = !legacyGraphFinalPackageBusy && !legacyGraphBulkMergeBusy;

        if (legacyGraphImportConfirmed)
        {
            LegacyGraphFinalizeImportButton.IsEnabled = false;
            LegacyGraphFinalizeImportButton.Content = "インポート内容を確定済み";
            LegacyGraphFinalizeImportStatusText.Text =
                $"インポート内容を確定済みです。未確定トレイ除外: {legacyGraphExcludedDocumentKeys.Count:N0}件 / 明示除外: 顧客 {legacyGraphTrashCustomerIds.Count:N0}件・車両 {legacyGraphTrashVehicleIds.Count:N0}件・書類 {legacyGraphTrashDocumentKeys.Count:N0}件。操作を変更すると確定が解除されます。";
            LegacyGraphApproveAllMergeButton.IsEnabled = false;
            LegacyGraphApproveAllMergeButton.Content = "統合候補・顧客を一括確定済み";
            var gate14Ready = unifiedImportOutputSession is not null &&
                              fp5VehicleImageMapping?.IsFullyMatched == true;
            LegacyGraphCreateFinalPackageButton.IsEnabled = gate14Ready && CanMutateLegacyGraph;
            LegacyGraphOpenFinalPackageButton.IsEnabled = !string.IsNullOrWhiteSpace(legacyGraphFinalPackagePath) &&
                                                           Directory.Exists(legacyGraphFinalPackagePath);
            if (!legacyGraphFinalPackageBusy && string.IsNullOrWhiteSpace(LegacyGraphFinalPackageResultText.Text) &&
                !legacyGraphFinalPackageHasError)
            {
                LegacyGraphFinalPackageStatusText.Text = gate14Ready
                    ? "確定内容を登録前パッケージへ保存できます。保存後もWeb API・DB・画像アップロードは行いません。"
                    : "Gate 14画像対応付けが未完了です。上部の「解析を開始」を実行してから、確定内容をパッケージ化してください。";
                LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#52647A");
                LegacyGraphFinalPackageNextStepText.Text =
                    gate14Ready
                        ? "次の操作: 「確定内容から登録前パッケージを作成」を押してください。"
                        : "次の操作: ABACUSフォルダーを選び直し、Gate 14解析を完了してください。";
                LegacyGraphFinalPackageNextStepText.Foreground = ToBrush(gate14Ready ? "#1E40AF" : "#A61B1B");
            }
            return;
        }

        var pendingMergeGroupCount = GetLegacyGraphPendingMergeGroupCount();
        var pendingDocumentCount = GetLegacyGraphPendingDocumentCount();
        var unapprovedCustomerCount = GetLegacyGraphUnapprovedCustomerCount();
        var trayCount = GetLegacyGraphTrayDocuments().Count;
        var unresolvedVehicleCount = GetLegacyGraphUnresolvedVehicleCount();
        var hasBulkCustomerApprovalTarget = pendingMergeGroupCount > 0 || unapprovedCustomerCount > 0;
        LegacyGraphApproveAllMergeButton.IsEnabled = CanMutateLegacyGraph &&
                                                     hasBulkCustomerApprovalTarget;
        LegacyGraphApproveAllMergeButton.Content = legacyGraphBulkMergeBusy
            ? "統合候補・顧客を一括確定中…"
            : pendingMergeGroupCount > 0 && unapprovedCustomerCount > 0
                ? $"統合候補・顧客を一括確定（候補 {pendingMergeGroupCount:N0}件 / 未確認 {unapprovedCustomerCount:N0}件）"
                : pendingMergeGroupCount > 0
                    ? $"統合候補・顧客を一括確定（候補 {pendingMergeGroupCount:N0}件）"
                    : unapprovedCustomerCount > 0
                        ? $"顧客を一括確定（未確認 {unapprovedCustomerCount:N0}件）"
                        : "統合候補・顧客を一括確定";
        LegacyGraphFinalizeImportButton.IsEnabled = CanMutateLegacyGraph &&
            LegacyMatchingWorkflow.CanFinalizeImport(
                pendingMergeGroupCount,
                pendingDocumentCount,
                unapprovedCustomerCount);
        LegacyGraphFinalizeImportButton.Content = "インポート内容を確定";
        LegacyGraphCreateFinalPackageButton.IsEnabled = false;
        LegacyGraphOpenFinalPackageButton.IsEnabled = false;
        if (!LegacyMatchingWorkflow.CanFinalizeImport(
                pendingMergeGroupCount,
                pendingDocumentCount,
                unapprovedCustomerCount))
        {
            var pendingDetails = new List<string>();
            if (pendingMergeGroupCount > 0)
            {
                pendingDetails.Add($"統合候補 {pendingMergeGroupCount:N0}件");
            }

            if (pendingDocumentCount > 0)
            {
                pendingDetails.Add($"ノード未接続書類 {pendingDocumentCount:N0}件");
            }

            if (unapprovedCustomerCount > 0)
            {
                pendingDetails.Add($"顧客確認未完了 {unapprovedCustomerCount:N0}件");
            }

            LegacyGraphFinalizeImportStatusText.Text =
                $"確定前に完了してください: {string.Join(" / ", pendingDetails)}。" +
                (trayCount > 0 ? $" 未確定トレイ {trayCount:N0}件は確定時に除外できます。" : "") +
                (unresolvedVehicleCount > 0 ? $" 未接続車両 {unresolvedVehicleCount:N0}件はパッケージ作成時に除外されます。" : "") +
                ((legacyGraphTrashCustomerIds.Count + legacyGraphTrashVehicleIds.Count + legacyGraphTrashDocumentKeys.Count) > 0
                    ? $" ごみ箱（明示除外）: 顧客 {legacyGraphTrashCustomerIds.Count:N0}件 / 車両 {legacyGraphTrashVehicleIds.Count:N0}件 / 書類 {legacyGraphTrashDocumentKeys.Count:N0}件。"
                    : "");
            LegacyGraphFinalPackageNextStepText.Text =
                "次の操作: 表示された未完了項目を処理してから、「インポート内容を確定」を押してください。";
            LegacyGraphFinalPackageNextStepText.Foreground = ToBrush("#1E40AF");
        }
        else
        {
            LegacyGraphFinalizeImportStatusText.Text = trayCount > 0 || unresolvedVehicleCount > 0 ||
                                                       legacyGraphTrashCustomerIds.Count > 0 ||
                                                       legacyGraphTrashVehicleIds.Count > 0 ||
                                                       legacyGraphTrashDocumentKeys.Count > 0
                ? $"確定できます。未確定トレイ {trayCount:N0}件、未接続車両 {unresolvedVehicleCount:N0}件、明示除外（顧客 {legacyGraphTrashCustomerIds.Count:N0}件 / 車両 {legacyGraphTrashVehicleIds.Count:N0}件 / 書類 {legacyGraphTrashDocumentKeys.Count:N0}件）は確認後に今回のインポートから除外します。"
                : "確定できます。未確定トレイの書類・未接続車両・明示除外はありません。";
            LegacyGraphFinalPackageNextStepText.Text =
                "次の操作: 「インポート内容を確定」を押してください。確認後に登録前パッケージ作成ボタンが有効になります。";
            LegacyGraphFinalPackageNextStepText.Foreground = ToBrush("#1E40AF");
        }
    }

    private AbacusLegacyExportCandidateGraphCustomer GetLegacyGraphSourceCustomer(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (legacyExportCandidateGraphResult is null ||
            !legacyGraphVirtualCustomerMergeKeys.ContainsKey(customer.CustomerId))
        {
            return customer;
        }

        return GetLegacyGraphCustomerMergeCandidates(customer).FirstOrDefault() ??
               customer;
    }

    private string GetLegacyGraphCustomerName(
        AbacusLegacyExportCandidateGraphCustomer customer) =>
        legacyGraphCustomerNameOverrides.TryGetValue($"customer:{customer.CustomerId}", out var overrideName)
            ? overrideName
            : customer.CustomerName;

    private string GetLegacyGraphCustomerNameById(string customerId) =>
        FindLegacyGraphCustomerById(customerId) is { } customer
            ? GetLegacyGraphCustomerName(customer)
            : "";

    private string GetLegacyGraphCustomerDisplayName(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (legacyGraphVirtualCustomerMergeKeys.TryGetValue(customer.CustomerId, out var mergeKey) &&
            legacyGraphCustomerNameOverrides.TryGetValue($"merge:{mergeKey}", out var mergedName))
        {
            return mergedName;
        }

        return legacyGraphCustomerNameOverrides.TryGetValue($"customer:{customer.CustomerId}", out var customerName)
            ? customerName
            : customer.DisplayName;
    }

    private string GetLegacyGraphMatchingDisplayName(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var sourceCustomer = GetLegacyGraphSourceCustomer(customer);
        var displayCustomer = GetLegacyGraphDisplayCustomer(sourceCustomer);
        return GetLegacyGraphCustomerDisplayName(displayCustomer);
    }

    private AbacusLegacyExportCandidateGraphCustomer? FindLegacyGraphCustomerById(string customerId) =>
        legacyExportCandidateGraphResult?.Customers.FirstOrDefault(customer =>
            string.Equals(customer.CustomerId, customerId, StringComparison.Ordinal));

    private string AddLegacyGraphCustomerToMergeGroup(
        AbacusLegacyExportCandidateGraphCustomer sourceCustomer,
        AbacusLegacyExportCandidateGraphCustomer targetCustomer)
    {
        var sourceGroupKey = GetLegacyCustomerMergeKey(sourceCustomer);
        var targetGroupKey = GetLegacyCustomerMergeKey(targetCustomer);
        var hasSourceGroup = TryGetLegacyGraphMergeGroup(sourceGroupKey, out var sourceGroup);
        var hasTargetGroup = TryGetLegacyGraphMergeGroup(targetGroupKey, out var targetGroup);
        LegacyGraphCustomerMergeGroup mergeGroup;
        var sourceGroupIsLogical = hasSourceGroup && IsLegacyGraphLogicalCustomerGroup(sourceGroupKey);
        var sourceGroupWasTracked = hasSourceGroup &&
                                    (sourceGroupIsLogical ||
                                     legacyGraphAppliedCustomerMergeKeys.Contains(sourceGroupKey));
        var sourceGroupWasApproved = hasSourceGroup &&
                                     IsLegacyGraphCustomerReviewApprovedForKey(sourceGroupKey);
        var affectedCustomerIds = new HashSet<string>(StringComparer.Ordinal)
        {
            sourceCustomer.CustomerId,
            targetCustomer.CustomerId,
        };
        if (hasSourceGroup)
        {
            affectedCustomerIds.UnionWith(sourceGroup.CustomerIds);
        }

        if (hasTargetGroup)
        {
            affectedCustomerIds.UnionWith(targetGroup.CustomerIds);
        }

        // ドラッグ元が既存グループの子であっても、移動するのは選択した1顧客だけです。
        // グループ全体を暗黙に移動させないことで、同姓同名候補から1件だけ外せます。
        if (hasSourceGroup && !string.Equals(sourceGroupKey, targetGroupKey, StringComparison.Ordinal))
        {
            sourceGroup.CustomerIds.Remove(sourceCustomer.CustomerId);
            legacyGraphCustomerMergeGroupByCustomerId.Remove(sourceCustomer.CustomerId);
            legacyGraphLogicalCustomerMergeGroupByCustomerId.Remove(sourceCustomer.CustomerId);
            InvalidateLegacyGraphMergeGroupState(
                sourceGroup.GroupId,
                sourceGroup.CustomerIds.Append(sourceCustomer.CustomerId));
            if (sourceGroup.CustomerIds.Count < 2)
            {
                RehomeLegacyGraphManualDocumentCustomerLinks(
                    sourceGroup.GroupId,
                    sourceGroup.CustomerIds.Append(sourceCustomer.CustomerId).ToArray(),
                    sourceGroup.CustomerIds.SingleOrDefault());
                foreach (var customerId in sourceGroup.CustomerIds)
                {
                    legacyGraphCustomerMergeGroupByCustomerId.Remove(customerId);
                    legacyGraphLogicalCustomerMergeGroupByCustomerId.Remove(customerId);
                }
                legacyGraphCustomerMergeGroups.Remove(sourceGroup.GroupId);
                legacyGraphCustomerGroupExpanded.Remove(sourceGroup.GroupId);
                MoveLegacyGraphConfirmedGroupReviewStateToRemainingCustomer(
                    sourceGroup.GroupId,
                    sourceGroupWasTracked,
                    sourceGroupWasApproved,
                    sourceGroup.CustomerIds);
            }
            else
            {
                foreach (var customerId in sourceGroup.CustomerIds)
                {
                    if (sourceGroupIsLogical)
                    {
                        legacyGraphLogicalCustomerMergeGroupByCustomerId[customerId] = sourceGroup.GroupId;
                        legacyGraphCustomerMergeGroupByCustomerId.Remove(customerId);
                    }
                    else
                    {
                        legacyGraphCustomerMergeGroupByCustomerId[customerId] = sourceGroup.GroupId;
                        legacyGraphLogicalCustomerMergeGroupByCustomerId.Remove(customerId);
                    }
                }
            }
        }
        RemoveLegacyGraphCustomerFromOtherMergeGroups(sourceCustomer.CustomerId, sourceGroupKey);

        if (hasTargetGroup && !string.Equals(sourceGroupKey, targetGroupKey, StringComparison.Ordinal))
        {
            mergeGroup = targetGroup;
        }
        else
        {
            var groupId = $"manual:{Guid.NewGuid():N}";
            mergeGroup = new LegacyGraphCustomerMergeGroup(groupId, "manual", []);
            legacyGraphCustomerMergeGroups[groupId] = mergeGroup;
        }

        var mergeGroupIsLogical =
            (hasTargetGroup && IsLegacyGraphLogicalCustomerGroup(targetGroupKey)) ||
            IsLegacyGraphLogicalCustomerGroup(mergeGroup.GroupId);
        foreach (var customerId in new[] { sourceCustomer.CustomerId, targetCustomer.CustomerId })
        {
            if (!mergeGroup.CustomerIds.Contains(customerId, StringComparer.Ordinal))
            {
                mergeGroup.CustomerIds.Add(customerId);
            }

            if (mergeGroupIsLogical)
            {
                legacyGraphLogicalCustomerMergeGroupByCustomerId[customerId] = mergeGroup.GroupId;
                legacyGraphCustomerMergeGroupByCustomerId.Remove(customerId);
            }
            else
            {
                legacyGraphCustomerMergeGroupByCustomerId[customerId] = mergeGroup.GroupId;
                legacyGraphLogicalCustomerMergeGroupByCustomerId.Remove(customerId);
            }
        }
        RemoveLegacyGraphCustomerFromOtherMergeGroups(
            targetCustomer.CustomerId,
            mergeGroup.GroupId);

        mergeGroup.Origin = mergeGroupIsLogical ? "logical" : "manual";
        legacyGraphCustomerGroupExpanded[mergeGroup.GroupId] = true;
        InvalidateLegacyGraphMergeGroupState(mergeGroup.GroupId);
        affectedCustomerIds.UnionWith(mergeGroup.CustomerIds);
        ResetLegacyGraphApprovedCustomerMergeRecommendations(affectedCustomerIds);
        RebuildLegacyGraphRecommendationCandidatesForCustomers(
            affectedCustomerIds);
        return mergeGroup.GroupId;
    }

    private void InvalidateLegacyGraphMergeGroupState(
        string groupKey,
        IEnumerable<string>? affectedCustomerIds = null)
    {
        var affectedIds = (affectedCustomerIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        if (legacyGraphCustomerMergeGroups.TryGetValue(groupKey, out var mergeGroup))
        {
            affectedIds.UnionWith(mergeGroup.CustomerIds);
        }

        var wasApproved = IsLegacyGraphCustomerReviewApprovedForKey(groupKey);
        var wasNeedsReview = string.Equals(
            legacyGraphCustomerReviewStates.GetValueOrDefault(groupKey),
            LegacyGraphCustomerReviewStateValues.NeedsReview,
            StringComparison.Ordinal);
        foreach (var customerId in affectedIds)
        {
            if (FindLegacyGraphCustomerById(customerId) is { } customer)
            {
                var customerReviewKey = GetLegacyGraphCustomerApprovalKey(customer);
                wasApproved |= IsLegacyGraphCustomerReviewApprovedForKey(customerReviewKey);
                wasNeedsReview |= string.Equals(
                    legacyGraphCustomerReviewStates.GetValueOrDefault(customerReviewKey),
                    LegacyGraphCustomerReviewStateValues.NeedsReview,
                    StringComparison.Ordinal);
            }
        }

        InvalidateLegacyGraphImportConfirmation();
        legacyGraphCustomerMergeDrafts.Remove(groupKey);
        legacyGraphAppliedCustomerMergeKeys.Remove(groupKey);
        legacyGraphCustomerApprovalStates.Remove(groupKey);
        legacyGraphCustomerReviewStates.Remove(groupKey);
        foreach (var virtualCustomerId in legacyGraphVirtualCustomerMergeKeys
                     .Where(pair => string.Equals(pair.Value, groupKey, StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            legacyGraphVirtualCustomerMergeKeys.Remove(virtualCustomerId);
        }

        if (wasApproved || wasNeedsReview)
        {
            foreach (var customerId in affectedIds)
            {
                if (FindLegacyGraphCustomerById(customerId) is { } customer)
                {
                    SetLegacyGraphCustomerReviewState(
                        customer,
                        LegacyGraphCustomerReviewStateValues.NeedsReview);
                }
            }
        }
    }

    private void RemoveLegacyGraphCustomerFromOtherMergeGroups(
        string customerId,
        string? keepGroupKey)
    {
        foreach (var otherGroup in legacyGraphCustomerMergeGroups.Values
                     .Where(group => !string.Equals(group.GroupId, keepGroupKey, StringComparison.Ordinal) &&
                                    group.CustomerIds.Contains(customerId, StringComparer.Ordinal))
                     .ToArray())
        {
            var otherGroupWasTracked = IsLegacyGraphLogicalCustomerGroup(otherGroup.GroupId) ||
                                       legacyGraphAppliedCustomerMergeKeys.Contains(otherGroup.GroupId);
            var otherGroupWasApproved = IsLegacyGraphCustomerReviewApprovedForKey(otherGroup.GroupId);
            var affectedCustomerIds = otherGroup.CustomerIds
                .Append(customerId)
                .ToArray();
            otherGroup.CustomerIds.Remove(customerId);
            if (legacyGraphCustomerMergeGroupByCustomerId.TryGetValue(customerId, out var candidateGroupKey) &&
                string.Equals(candidateGroupKey, otherGroup.GroupId, StringComparison.Ordinal))
            {
                legacyGraphCustomerMergeGroupByCustomerId.Remove(customerId);
            }

            if (legacyGraphLogicalCustomerMergeGroupByCustomerId.TryGetValue(customerId, out var logicalGroupKey) &&
                string.Equals(logicalGroupKey, otherGroup.GroupId, StringComparison.Ordinal))
            {
                legacyGraphLogicalCustomerMergeGroupByCustomerId.Remove(customerId);
            }

            InvalidateLegacyGraphMergeGroupState(otherGroup.GroupId, affectedCustomerIds);
            if (otherGroup.CustomerIds.Count < 2)
            {
                RehomeLegacyGraphManualDocumentCustomerLinks(
                    otherGroup.GroupId,
                    affectedCustomerIds,
                    otherGroup.CustomerIds.SingleOrDefault());
                foreach (var remainingCustomerId in otherGroup.CustomerIds)
                {
                    legacyGraphCustomerMergeGroupByCustomerId.Remove(remainingCustomerId);
                    legacyGraphLogicalCustomerMergeGroupByCustomerId.Remove(remainingCustomerId);
                }

                legacyGraphCustomerMergeGroups.Remove(otherGroup.GroupId);
                legacyGraphCustomerGroupExpanded.Remove(otherGroup.GroupId);
                MoveLegacyGraphConfirmedGroupReviewStateToRemainingCustomer(
                    otherGroup.GroupId,
                    otherGroupWasTracked,
                    otherGroupWasApproved,
                    otherGroup.CustomerIds);
            }
        }
    }

    private void LegacyGraphRemoveCustomerFromMergeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        AbacusLegacyExportCandidateGraphCustomer? sourceCustomer;
        string groupKey;
        if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryGetLegacyGraphMatchingSelectedMergeMember(out sourceCustomer!, out groupKey) ||
                GetLegacyGraphMatchingCustomer() is not { } focusCustomer ||
                !TryGetLegacyGraphMergeGroup(groupKey, out var matchingGroup) ||
                !LegacyGraphMatchingSelectionState.CanRemoveMergeMember(
                    sourceCustomer.CustomerId,
                    focusCustomer.CustomerId,
                    matchingGroup.CustomerIds,
                    CanMutateLegacyGraph))
            {
                return;
            }

            legacyGraphMatchingSelectedMergeMemberCustomerId = null;
        }
        else if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphCustomer selectedCustomer)
        {
            sourceCustomer = GetLegacyGraphSourceCustomer(selectedCustomer);
            groupKey = GetLegacyCustomerMergeKey(sourceCustomer);
        }
        else
        {
            return;
        }

        if (!TryGetLegacyGraphMergeGroup(groupKey, out var mergeGroup) || mergeGroup.CustomerIds.Count < 2)
        {
            return;
        }

        if ((IsLegacyGraphLogicalCustomerGroup(groupKey) ||
             legacyGraphAppliedCustomerMergeKeys.Contains(groupKey)) &&
            MessageBox.Show(
                this,
                $"{GetLegacyGraphCustomerDisplayName(sourceCustomer)}を統合から外します。\n" +
                "この顧客の車両・書類は元の顧客へ戻り、統合状態は未確定へ戻ります。続行しますか？",
                "統合から外す",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        RemoveLegacyGraphCustomerFromMergeGroup(sourceCustomer, groupKey);
    }

    private void LegacyGraphDissolveCustomerMergeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        if (legacyGraphSelectedItem is not AbacusLegacyExportCandidateGraphCustomer selectedCustomer)
        {
            return;
        }

        var sourceCustomer = GetLegacyGraphSourceCustomer(selectedCustomer);
        var groupKey = GetLegacyCustomerMergeKey(sourceCustomer);
        if (!TryGetLegacyGraphMergeGroup(groupKey, out var mergeGroup) || mergeGroup.CustomerIds.Count < 2)
        {
            return;
        }

        if ((IsLegacyGraphLogicalCustomerGroup(groupKey) ||
             legacyGraphAppliedCustomerMergeKeys.Contains(groupKey)) &&
            MessageBox.Show(
                this,
                "この統合グループを解除し、構成顧客をそれぞれ独立した顧客へ戻します。\n" +
                "車両・書類の元の所有関係は保持されます。続行しますか？",
                "統合を解除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var customerIds = mergeGroup.CustomerIds.ToArray();
        var isLogicalGroup = IsLegacyGraphLogicalCustomerGroup(groupKey);
        ResetLegacyGraphApprovedCustomerMergeRecommendations(customerIds);
        InvalidateLegacyGraphMergeGroupState(groupKey, customerIds);
        RehomeLegacyGraphManualDocumentCustomerLinks(groupKey, customerIds, null);
        legacyGraphCustomerMergeGroups.Remove(groupKey);
        legacyGraphCustomerGroupExpanded.Remove(groupKey);
        foreach (var customerId in customerIds)
        {
            if (isLogicalGroup)
            {
                legacyGraphLogicalCustomerMergeGroupByCustomerId.Remove(customerId);
            }
            else
            {
                legacyGraphCustomerMergeGroupByCustomerId.Remove(customerId);
            }
        }

        RebuildLegacyGraphRecommendationCandidatesForCustomers(
            customerIds.ToHashSet(StringComparer.Ordinal));
        var firstCustomer = FindLegacyGraphCustomerById(customerIds[0]);
        if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase) &&
            firstCustomer is not null)
        {
            EnsureLegacyGraphMatchingCustomerSelection();
            legacyGraphSelectedItem = firstCustomer;
            RefreshLegacyGraphCustomerList();
            RefreshLegacyMatchingView();
        }
        else
        {
            RefreshLegacyGraphCustomerList(firstCustomer is null ? null : $"customer:{firstCustomer.CustomerId}");
            if (firstCustomer is not null)
            {
                legacyGraphSelectedItem = firstCustomer;
                UpdateLegacyGraphInspector(firstCustomer);
                RenderLegacyGraphCustomer(firstCustomer);
            }
        }

        LegacyGraphStatusText.Text = "統合を解除し、構成顧客をマッチング順へ戻しました。元データは変更していません。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private LegacyGraphMergeStateSnapshot CaptureLegacyGraphMergeState() =>
        new(
            legacyGraphCustomerMergeGroups.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal),
            new Dictionary<string, string>(legacyGraphCustomerMergeGroupByCustomerId, StringComparer.Ordinal),
            new Dictionary<string, LegacyGraphCustomerMergeDraft>(legacyGraphCustomerMergeDrafts, StringComparer.Ordinal),
            new HashSet<string>(legacyGraphAppliedCustomerMergeKeys, StringComparer.Ordinal),
            new Dictionary<string, string>(legacyGraphVirtualCustomerMergeKeys, StringComparer.Ordinal),
            new Dictionary<string, bool>(legacyGraphCustomerGroupExpanded, StringComparer.Ordinal));

    private void RestoreLegacyGraphMergeState(LegacyGraphMergeStateSnapshot snapshot)
    {
        legacyGraphCustomerMergeGroups.Clear();
        foreach (var pair in snapshot.Groups)
        {
            legacyGraphCustomerMergeGroups[pair.Key] = pair.Value.Clone();
        }

        legacyGraphCustomerMergeGroupByCustomerId.Clear();
        foreach (var pair in snapshot.GroupByCustomerId)
        {
            legacyGraphCustomerMergeGroupByCustomerId[pair.Key] = pair.Value;
        }

        legacyGraphCustomerMergeDrafts.Clear();
        foreach (var pair in snapshot.Drafts)
        {
            legacyGraphCustomerMergeDrafts[pair.Key] = pair.Value;
        }

        legacyGraphAppliedCustomerMergeKeys.Clear();
        foreach (var groupKey in snapshot.AppliedGroupKeys)
        {
            legacyGraphAppliedCustomerMergeKeys.Add(groupKey);
        }

        legacyGraphVirtualCustomerMergeKeys.Clear();
        foreach (var pair in snapshot.VirtualCustomerMergeKeys)
        {
            legacyGraphVirtualCustomerMergeKeys[pair.Key] = pair.Value;
        }

        legacyGraphCustomerGroupExpanded.Clear();
        foreach (var pair in snapshot.ExpandedGroups)
        {
            legacyGraphCustomerGroupExpanded[pair.Key] = pair.Value;
        }
    }

    private void LegacyGraphClearManualLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphVehicle vehicle)
        {
            ClearLegacyGraphManualVehicleCustomerLink(vehicle);
            return;
        }

        if (legacyGraphSelectedItem is not AbacusLegacyExportCandidateGraphDocument document)
        {
            return;
        }

        if (legacyGraphManualDocumentLinks.Remove(GetLegacyDocumentKey(document)))
        {
            legacyGraphDocumentLinkMethods.Remove(GetLegacyDocumentKey(document));
            legacyGraphDocumentLinkReasons.Remove(GetLegacyDocumentKey(document));
            RefreshLegacyGraphAfterManualLink(document, null);
        }
    }

    private void LegacyGraphReassignButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphVehicle vehicle)
        {
            ClearLegacyGraphManualVehicleCustomerLink(vehicle);
            e.Handled = true;
            return;
        }

        if (legacyGraphSelectedItem is not AbacusLegacyExportCandidateGraphDocument document ||
            legacyExportCandidateGraphResult is null)
        {
            return;
        }

        var candidateVehicleIds = document.CandidateVehicleIds.ToHashSet(StringComparer.Ordinal);
        var choices = legacyExportCandidateGraphResult.Customers
            .SelectMany(customer => customer.Vehicles.Select(vehicle => new LegacyGraphVehicleChoice(
                vehicle.VehicleId,
                $"{GetLegacyGraphCustomerDisplayName(customer)} / {vehicle.Maker} {vehicle.DisplayName}",
                vehicle.IdentifierSummary,
                GetLegacyGraphCustomerDisplayName(customer))))
            .OrderByDescending(choice => candidateVehicleIds.Contains(choice.VehicleId))
            .ThenBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (choices.Length == 0)
        {
            MessageBox.Show(this, "選択できる車両がありません。", "紐付け先なし", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Owner = this,
            Title = $"{document.Kind}の紐付け先を変更",
            Width = 720,
            Height = 430,
            MinWidth = 560,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ToBrush("#F4F7FB"),
        };
        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = $"{document.Kind}: {Fallback(document.DocumentNumber)}",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = ToBrush("#10233F"),
        });
        var evidence = new TextBlock
        {
            Margin = new Thickness(0, 9, 0, 0),
            Text = $"顧客名: {Fallback(document.CustomerName)} / 車名: {Fallback(document.VehicleName)} / 登録番号: {Fallback(document.RegistrationNumber)}\n" +
                   $"元の判定: {document.MatchStatus} / {Fallback(document.Warning)}",
            Foreground = ToBrush("#52647A"),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(evidence, 1);
        root.Children.Add(evidence);

        var choicePanel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        choicePanel.Children.Add(new TextBlock
        {
            Text = "紐付け先の車両",
            FontWeight = FontWeights.SemiBold,
            Foreground = ToBrush("#10233F"),
        });
        choicePanel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 8),
            Text = "候補車両を先頭に表示しています。ここでの変更は画面上の仮紐付けです。",
            Foreground = ToBrush("#52647A"),
            TextWrapping = TextWrapping.Wrap,
        });
        var vehicleCombo = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(LegacyGraphVehicleChoice.DisplayName),
            MinHeight = 38,
            Padding = new Thickness(8, 5, 8, 5),
        };
        var currentVehicle = FindManualLinkedVehicle(document);
        vehicleCombo.SelectedItem = choices.FirstOrDefault(choice =>
            string.Equals(choice.VehicleId, currentVehicle?.VehicleId, StringComparison.Ordinal)) ?? choices[0];
        choicePanel.Children.Add(vehicleCombo);
        var choiceDetails = new TextBlock
        {
            Margin = new Thickness(0, 9, 0, 0),
            Foreground = ToBrush("#718096"),
            TextWrapping = TextWrapping.Wrap,
        };
        void RefreshChoiceDetails()
        {
            choiceDetails.Text = vehicleCombo.SelectedItem is LegacyGraphVehicleChoice choice
                ? $"対象顧客: {choice.CustomerName}\n識別情報: {Fallback(choice.IdentifierSummary)}\n車両ID: {choice.VehicleId}"
                : "車両を選択してください。";
        }
        vehicleCombo.SelectionChanged += (_, _) => RefreshChoiceDetails();
        RefreshChoiceDetails();
        choicePanel.Children.Add(choiceDetails);
        Grid.SetRow(choicePanel, 2);
        root.Children.Add(choicePanel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var clearRequested = false;
        var assignButton = new Button { Content = "この車両へ仮紐付け", MinWidth = 150 };
        var clearButton = new Button { Content = "未紐付けに戻す", MinWidth = 125, IsEnabled = currentVehicle is not null };
        var cancelButton = new Button { Content = "キャンセル", MinWidth = 100 };
        assignButton.Click += (_, _) =>
        {
            if (vehicleCombo.SelectedItem is not LegacyGraphVehicleChoice)
            {
                MessageBox.Show(dialog, "紐付け先の車両を選択してください。", "車両未選択", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            dialog.DialogResult = true;
        };
        clearButton.Click += (_, _) =>
        {
            clearRequested = true;
            InvalidateLegacyGraphApprovalForDocument(document);
            legacyGraphManualDocumentLinks.Remove(GetLegacyDocumentKey(document));
            legacyGraphDocumentLinkMethods.Remove(GetLegacyDocumentKey(document));
            legacyGraphDocumentLinkReasons.Remove(GetLegacyDocumentKey(document));
            dialog.DialogResult = true;
            RefreshLegacyGraphAfterManualLink(document, null);
        };
        cancelButton.Click += (_, _) => dialog.DialogResult = false;
        buttons.Children.Add(assignButton);
        buttons.Children.Add(clearButton);
        buttons.Children.Add(cancelButton);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        dialog.Content = root;
        if (dialog.ShowDialog() == true && !clearRequested && vehicleCombo.SelectedItem is LegacyGraphVehicleChoice selectedChoice)
        {
            var key = GetLegacyDocumentKey(document);
            InvalidateLegacyGraphApprovalForDocument(document);
            legacyGraphManualDocumentLinks[key] = selectedChoice.VehicleId;
            legacyGraphDocumentLinkMethods[key] = "manual-vehicle";
            legacyGraphDocumentLinkReasons[key] = "ユーザーが紐づけ先車両を選択";
            RefreshLegacyGraphAfterManualLink(document, selectedChoice.VehicleId);
        }
    }

    private void RefreshLegacyGraphAfterManualLink(
        AbacusLegacyExportCandidateGraphDocument document,
        string? vehicleId)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return;
        }

        var documentKey = GetLegacyDocumentKey(document);
        var selectedCustomerBeforeChange = GetLegacyGraphListSelectedDisplayCustomer();
        InvalidateLegacyGraphApprovalForDocument(document);
        legacyGraphManualDocumentCustomerLinks.Remove(documentKey);
        if (vehicleId is null)
        {
            legacyGraphManualDocumentLinks.Remove(documentKey);
            legacyGraphDocumentLinkMethods.Remove(documentKey);
            legacyGraphDocumentLinkReasons.Remove(documentKey);
            if (!legacyGraphTrayDocumentKeys.Contains(documentKey))
            {
                legacyGraphExcludedDocumentKeys.Remove(documentKey);
            }
        }
        else
        {
            legacyGraphUnconnectedDocumentKeys.Remove(documentKey);
            legacyGraphTrayDocumentKeys.Remove(documentKey);
            legacyGraphExcludedDocumentKeys.Remove(documentKey);
        }

        RebuildLegacyGraphRecommendationCandidates();

        var targetCustomer = selectedCustomerBeforeChange ?? (vehicleId is null
            ? FindCurrentCustomerForDocument(document)
            : legacyExportCandidateGraphResult.Customers.FirstOrDefault(customer =>
                customer.Vehicles.Any(vehicle => string.Equals(vehicle.VehicleId, vehicleId, StringComparison.Ordinal))));
        if (targetCustomer is not null)
        {
            RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(targetCustomer));
            RenderLegacyGraphCustomer(targetCustomer);
        }

        RefreshLegacyGraphUnresolvedDocumentLists();
        legacyGraphSelectedItem = document;
        UpdateLegacyGraphInspector(document);
        var manualCount = legacyGraphManualDocumentLinks.Count;
        LegacyGraphStatusText.Text = vehicleId is null &&
                                    legacyGraphUnconnectedDocumentKeys.Contains(documentKey)
            ? "書類の接続を切り、キャンバス上の未接続状態にしました。車両へ接続するか、未確定トレイへ移動してください。"
            : manualCount == 0
                ? "手動仮紐付けを解除しました。"
                : $"手動仮紐付けを{manualCount:N0}件表示中です。元CSV・ABACUSフォルダーは変更していません。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private AbacusLegacyExportCandidateGraphCustomer? GetLegacyGraphListSelectedDisplayCustomer()
    {
        var selectedItem = string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase)
            ? LegacyMatchingCustomerQueueList.SelectedItem
            : LegacyGraphCustomersList.SelectedItem;
        if (selectedItem is not LegacyGraphCustomerListEntry entry)
        {
            return null;
        }

        return entry.IsGroupHeader
            ? GetLegacyGraphDisplayCustomer(entry.Customer)
            : entry.Customer;
    }

    private void ApplyLegacyGraphManualCustomerLink(
        AbacusLegacyExportCandidateGraphDocument document,
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var key = GetLegacyDocumentKey(document);
        ClearLegacyGraphRecommendationDecisions(
            AbacusRecommendationEntityKinds.Document,
            key);
        var targetGroupKey = GetLegacyCustomerMergeKey(customer);
        InvalidateLegacyGraphApprovalForDocument(document);
        legacyGraphManualDocumentLinks.Remove(key);
        legacyGraphUnconnectedDocumentKeys.Remove(key);
        legacyGraphTrayDocumentKeys.Remove(key);
        legacyGraphExcludedDocumentKeys.Remove(key);
        legacyGraphManualDocumentCustomerLinks[key] = targetGroupKey;
        legacyGraphDocumentLinkMethods[key] = "manual-customer-only";
        legacyGraphDocumentLinkReasons[key] = "ユーザーが顧客だけへ手動紐づけ（車両なし）";
        legacyGraphAppliedCustomerMergeKeys.Remove(targetGroupKey);
        InvalidateLegacyGraphCustomerApproval(customer);
        RebuildLegacyGraphRecommendationCandidates();
        RefreshLegacyGraphAfterManualCustomerLink(document, customer);
    }

    private void RefreshLegacyGraphAfterManualCustomerLink(
        AbacusLegacyExportCandidateGraphDocument document,
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return;
        }

        var selectedCustomer = GetLegacyGraphListSelectedDisplayCustomer() ?? customer;
        RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(selectedCustomer));
        RenderLegacyGraphCustomer(selectedCustomer);
        RefreshLegacyGraphUnresolvedDocumentLists();
        legacyGraphSelectedItem = document;
        UpdateLegacyGraphInspector(document);
        LegacyGraphStatusText.Text =
            $"{document.Kind} {Fallback(document.DocumentNumber)} を顧客へ仮紐付けしました。車両情報は未設定のままです。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private void MoveLegacyGraphDocumentToTray(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var key = GetLegacyDocumentKey(document);
        var originalCustomer = FindCurrentCustomerForDocument(document);
        var originalVehicle = FindCurrentVehicleForDocument(document);
        var undoState = new LegacyGraphDetachedUndoState(
            "document",
            key,
            null,
            legacyGraphTrayDocumentKeys.Contains(key),
            [CaptureLegacyGraphDetachedDocumentState(document)]);
        InvalidateLegacyGraphApprovalForDocument(document);
        legacyGraphManualDocumentLinks.Remove(key);
        legacyGraphManualDocumentCustomerLinks.Remove(key);
        legacyGraphDocumentLinkMethods.Remove(key);
        legacyGraphDocumentLinkReasons.Remove(key);
        legacyGraphUnconnectedDocumentKeys.Remove(key);
        legacyGraphTrayDocumentKeys.Add(key);
        ClearLegacyGraphBlockVisualDrag();
        RefreshLegacyGraphAfterManualLink(document, null);
        RefreshLegacyGraphUnresolvedDocumentLists();
        AddLegacyMatchingDetachedChange(
            $"document:{key}",
            "document",
            key,
            "取り外した項目",
            $"{document.Kind} {Fallback(document.DocumentNumber)}\n" +
            $"↑\n車両: {(originalVehicle is null ? "未設定" : $"{Fallback(originalVehicle.Maker)} {originalVehicle.DisplayName}".Trim())}\n" +
            $"顧客: {(originalCustomer is null ? "未設定" : GetLegacyGraphCustomerDisplayName(originalCustomer))}\n" +
            "変更後: 未確定 / 元に戻せます。",
            undoState);
        if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
        {
            RefreshLegacyGraphCustomerList();
            RefreshLegacyMatchingView();
        }
    }

    private bool ConfirmLegacyGraphTrashMove(
        string title,
        string description,
        LegacyGraphTrashImpact impact)
    {
        var message = $"{description}\n\n" +
                      $"影響範囲: 顧客 {impact.CustomerCount:N0}件 / 車両 {impact.VehicleCount:N0}件 / 書類 {impact.DocumentCount:N0}件 / 対応画像 {impact.ImageCount:N0}件\n\n" +
                      "この項目を今回のインポートから除外します。後でごみ箱から復元できます。実行しますか？";
        return MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void MoveLegacyGraphDocumentToTrash(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var key = GetLegacyDocumentKey(document);
        if (legacyGraphTrashDocumentKeys.Contains(key))
        {
            return;
        }

        var impact = GetLegacyGraphTrashImpact(document);
        if (!ConfirmLegacyGraphTrashMove(
                "書類をごみ箱へ移動",
                $"{document.Kind} {Fallback(document.DocumentNumber)} をごみ箱へ移動します。",
                impact))
        {
            return;
        }

        InvalidateLegacyGraphApprovalForDocument(document);
        legacyGraphTrashDocumentKeys.Add(key);
        ClearLegacyGraphBlockVisualDrag();
        RefreshLegacyGraphTrashLists();
        RefreshLegacyGraphUnresolvedDocumentLists();
        var customer = FindCurrentCustomerForDocument(document);
        if (customer is not null && !IsLegacyGraphCustomerInTrash(customer.CustomerId))
        {
            RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(customer));
            RenderLegacyGraphCustomer(GetLegacyGraphDisplayCustomer(customer));
        }

        legacyGraphSelectedItem = document;
        UpdateLegacyGraphInspector(document);
        LegacyGraphStatusText.Text = $"{document.Kind} {Fallback(document.DocumentNumber)} をごみ箱へ移動しました。";
        LegacyGraphStatusText.Foreground = ToBrush("#B91C1C");
    }

    private void RestoreLegacyGraphDocumentFromTrash(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        if (!legacyGraphTrashDocumentKeys.Remove(GetLegacyDocumentKey(document)))
        {
            return;
        }

        InvalidateLegacyGraphApprovalForDocument(document);
        RebuildLegacyGraphRecommendationCandidates();
        RefreshLegacyGraphTrashLists();
        RefreshLegacyGraphUnresolvedDocumentLists();
        var customer = FindCurrentCustomerForDocument(document);
        if (customer is not null && !IsLegacyGraphCustomerInTrash(customer.CustomerId))
        {
            RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(customer));
            RenderLegacyGraphCustomer(GetLegacyGraphDisplayCustomer(customer));
        }

        legacyGraphSelectedItem = document;
        UpdateLegacyGraphInspector(document);
        LegacyGraphStatusText.Text = $"{document.Kind} {Fallback(document.DocumentNumber)} をごみ箱から復元しました。移動前の紐付け状態を維持しています。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private void MoveLegacyGraphVehicleToTrash(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        if (legacyGraphTrashVehicleIds.Contains(vehicle.VehicleId))
        {
            return;
        }

        var impact = GetLegacyGraphTrashImpact(vehicle);
        if (!ConfirmLegacyGraphTrashMove(
                "車両をごみ箱へ移動",
                $"車両 {Fallback(vehicle.DisplayName)} をごみ箱へ移動します。関連書類と対応画像も除外対象になります。",
                impact))
        {
            return;
        }

        InvalidateLegacyGraphApprovalForVehicle(vehicle);
        legacyGraphTrashVehicleIds.Add(vehicle.VehicleId);
        ClearLegacyGraphBlockVisualDrag();
        RefreshLegacyGraphTrashLists();
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphUnresolvedDocumentLists();
        var customer = FindCurrentCustomerForVehicle(vehicle);
        if (customer is not null && !IsLegacyGraphCustomerInTrash(customer.CustomerId))
        {
            RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(customer));
            RenderLegacyGraphCustomer(GetLegacyGraphDisplayCustomer(customer));
        }

        legacyGraphSelectedItem = vehicle;
        UpdateLegacyGraphInspector(vehicle);
        LegacyGraphStatusText.Text = $"車両 {Fallback(vehicle.DisplayName)} をごみ箱へ移動しました。関連書類は今回のインポートから除外されます。";
        LegacyGraphStatusText.Foreground = ToBrush("#B91C1C");
    }

    private void RestoreLegacyGraphVehicleFromTrash(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        if (!legacyGraphTrashVehicleIds.Remove(vehicle.VehicleId))
        {
            return;
        }

        InvalidateLegacyGraphApprovalForVehicle(vehicle);
        RebuildLegacyGraphRecommendationCandidates();
        RefreshLegacyGraphTrashLists();
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphUnresolvedDocumentLists();
        var customer = FindCurrentCustomerForVehicle(vehicle);
        if (customer is not null && !IsLegacyGraphCustomerInTrash(customer.CustomerId))
        {
            RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(customer));
            RenderLegacyGraphCustomer(GetLegacyGraphDisplayCustomer(customer));
        }

        legacyGraphSelectedItem = vehicle;
        UpdateLegacyGraphInspector(vehicle);
        LegacyGraphStatusText.Text = $"車両 {Fallback(vehicle.DisplayName)} をごみ箱から復元しました。移動前の紐付け状態を維持しています。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private void MoveLegacyGraphCustomerToTrash(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var sourceCustomer = GetLegacyGraphSourceCustomer(customer);
        if (sourceCustomer.CustomerId.StartsWith("merge-preview:", StringComparison.Ordinal))
        {
            MessageBox.Show(this, "統合プレビューそのものはごみ箱へ移動できません。統合元の顧客を選択してください。", "顧客を除外できません", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (legacyGraphTrashCustomerIds.Contains(sourceCustomer.CustomerId))
        {
            return;
        }

        var impact = GetLegacyGraphTrashImpact(sourceCustomer);
        if (!ConfirmLegacyGraphTrashMove(
                "顧客をごみ箱へ移動",
                $"顧客 {Fallback(GetLegacyGraphCustomerDisplayName(sourceCustomer))} をごみ箱へ移動します。顧客に属する車両・書類・対応画像も除外対象になります。",
                impact))
        {
            return;
        }

        var mergeKeyBeforeTrash = GetLegacyCustomerMergeKey(sourceCustomer);
        if (TryGetLegacyGraphMergeGroup(mergeKeyBeforeTrash, out var mergeGroupBeforeTrash) &&
            mergeGroupBeforeTrash.CustomerIds.Count > 1)
        {
            // ごみ箱へ移した顧客を論理顧客グループに残すと、出力時に
            // 「有効顧客と除外顧客が同じグループ」という矛盾になります。
            // 既存の統合解除処理で所属マップ・下書き・リンク先を同時に整理します。
            RemoveLegacyGraphCustomerFromMergeGroup(sourceCustomer, mergeKeyBeforeTrash);
        }

        InvalidateLegacyGraphCustomerApproval(sourceCustomer);
        InvalidateLegacyGraphImportConfirmation();
        legacyGraphTrashCustomerIds.Add(sourceCustomer.CustomerId);
        legacyGraphAppliedCustomerMergeKeys.Remove(mergeKeyBeforeTrash);
        RebuildLegacyGraphRecommendationCandidates();
        RefreshLegacyGraphTrashLists();
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphUnresolvedDocumentLists();
        RefreshLegacyGraphCustomerList();
        legacyGraphSelectedItem = sourceCustomer;
        UpdateLegacyGraphInspector(sourceCustomer);
        LegacyGraphStatusText.Text = $"顧客 {Fallback(GetLegacyGraphCustomerDisplayName(sourceCustomer))} をごみ箱へ移動しました。関連する車両・書類・画像は今回のインポートから除外されます。";
        LegacyGraphStatusText.Foreground = ToBrush("#B91C1C");
    }

    private void RestoreLegacyGraphCustomerFromTrash(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var sourceCustomer = GetLegacyGraphSourceCustomer(customer);
        if (!legacyGraphTrashCustomerIds.Remove(sourceCustomer.CustomerId))
        {
            return;
        }

        InvalidateLegacyGraphCustomerApproval(sourceCustomer);
        InvalidateLegacyGraphImportConfirmation();
        RebuildLegacyGraphRecommendationCandidates();
        RefreshLegacyGraphTrashLists();
        RefreshLegacyGraphUnresolvedVehicleList();
        RefreshLegacyGraphUnresolvedDocumentLists();
        RefreshLegacyGraphCustomerList(GetLegacyGraphCustomerListEntryId(sourceCustomer));
        var displayCustomer = GetLegacyGraphDisplayCustomer(sourceCustomer);
        legacyGraphSelectedItem = displayCustomer;
        UpdateLegacyGraphInspector(displayCustomer);
        RenderLegacyGraphCustomer(displayCustomer);
        LegacyGraphStatusText.Text = $"顧客 {Fallback(GetLegacyGraphCustomerDisplayName(sourceCustomer))} をごみ箱から復元しました。以前の統合グループには戻さず、通常の顧客として確認待ちに戻しました。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private void ApplyLegacyGraphManualLink(
        AbacusLegacyExportCandidateGraphDocument document,
        string vehicleId)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        var key = GetLegacyDocumentKey(document);
        ClearLegacyGraphRecommendationDecisions(
            AbacusRecommendationEntityKinds.Document,
            key);
        InvalidateLegacyGraphApprovalForDocument(document);
        legacyGraphUnconnectedDocumentKeys.Remove(key);
        legacyGraphTrayDocumentKeys.Remove(key);
        legacyGraphManualDocumentCustomerLinks.Remove(key);
        var originalVehicle = FindOriginalVehicleForDocument(document);
        if (originalVehicle is not null &&
            string.Equals(originalVehicle.VehicleId, vehicleId, StringComparison.Ordinal))
        {
            legacyGraphManualDocumentLinks.Remove(key);
            legacyGraphDocumentLinkMethods.Remove(key);
            legacyGraphDocumentLinkReasons.Remove(key);
        }
        else
        {
            legacyGraphManualDocumentLinks[key] = vehicleId;
            legacyGraphDocumentLinkMethods[key] = "manual-vehicle";
            legacyGraphDocumentLinkReasons[key] = "ユーザーが紐づけ先車両を選択";
        }
        RefreshLegacyGraphAfterManualLink(document, vehicleId);
    }

    private void InvalidateLegacyGraphApprovalForDocument(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        InvalidateLegacyGraphImportConfirmation();
        var affectedCustomers = new Dictionary<string, AbacusLegacyExportCandidateGraphCustomer>(
            StringComparer.Ordinal);
        var key = GetLegacyDocumentKey(document);
        if (legacyGraphManualDocumentCustomerLinks.TryGetValue(key, out var manualCustomerGroupKey))
        {
            legacyGraphAppliedCustomerMergeKeys.Remove(manualCustomerGroupKey);
            if (FindLegacyGraphCustomerForGroupKey(manualCustomerGroupKey) is { } directCustomer)
            {
                affectedCustomers[directCustomer.CustomerId] = directCustomer;
            }
        }

        if (legacyGraphManualDocumentLinks.TryGetValue(key, out var manualVehicleId) &&
            FindLegacyGraphVehicleById(manualVehicleId) is { } manualVehicle)
        {
            if (FindCurrentCustomerForVehicle(manualVehicle) is { } manualVehicleCustomer)
            {
                affectedCustomers[manualVehicleCustomer.CustomerId] = manualVehicleCustomer;
            }
        }

        if (!string.IsNullOrWhiteSpace(document.LinkedVehicleId) &&
            FindLegacyGraphVehicleById(document.LinkedVehicleId) is { } linkedVehicle &&
            FindCurrentCustomerForVehicle(linkedVehicle) is { } linkedCustomer)
        {
            affectedCustomers[linkedCustomer.CustomerId] = linkedCustomer;
        }

        if (FindOriginalCustomerForDocument(document) is { } originalCustomer)
        {
            affectedCustomers[originalCustomer.CustomerId] = originalCustomer;
        }

        foreach (var customer in affectedCustomers.Values)
        {
            legacyGraphAppliedCustomerMergeKeys.Remove(GetLegacyCustomerMergeKey(customer));
            InvalidateLegacyGraphCustomerApproval(customer);
        }
    }

    private void InvalidateLegacyGraphApprovalForVehicle(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        var affectedCustomerIds = new HashSet<string>(StringComparer.Ordinal);
        if (FindCurrentCustomerForVehicle(vehicle) is { } currentCustomer)
        {
            affectedCustomerIds.Add(currentCustomer.CustomerId);
        }

        var relatedDocuments = GetLegacyGraphDocumentsAffectedByVehicle(vehicle);

        foreach (var document in relatedDocuments
                     .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            InvalidateLegacyGraphApprovalForDocument(document);
        }

        foreach (var customerId in affectedCustomerIds)
        {
            if (FindLegacyGraphCustomerById(customerId) is { } customer)
            {
                InvalidateLegacyGraphCustomerApproval(customer);
            }
        }

        InvalidateLegacyGraphImportConfirmation();
    }

    private void InvalidateLegacyGraphImportConfirmation()
    {
        if (!legacyGraphImportConfirmed && legacyGraphExcludedDocumentKeys.Count == 0)
        {
            UpdateLegacyGraphImportConfirmationButton();
            ScheduleLegacyGraphCheckpointSave();
            return;
        }

        legacyGraphImportConfirmed = false;
        legacyGraphExcludedDocumentKeys.Clear();
        UpdateLegacyGraphImportConfirmationButton();
        ScheduleLegacyGraphCheckpointSave();
    }

    private void RefreshLegacyGraphUnresolvedDocumentLists()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            LegacyGraphUnresolvedSalesList.ItemsSource = null;
            LegacyGraphUnresolvedMaintenanceList.ItemsSource = null;
            return;
        }

        var unresolved = legacyExportCandidateGraphResult.AllDocuments
            .Where(IsLegacyGraphDocumentInTray)
            .ToArray();
        LegacyGraphUnresolvedSalesList.ItemsSource = unresolved
            .Where(document => document.Kind == "販売書類")
            .ToArray();
        LegacyGraphUnresolvedMaintenanceList.ItemsSource = unresolved
            .Where(document => document.Kind == "整備書類")
            .ToArray();
        RefreshLegacyGraphSearchResults();
    }

    private bool IsLegacyGraphDocumentInTray(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        if (IsLegacyGraphDocumentInTrash(document))
        {
            return false;
        }

        var key = GetLegacyDocumentKey(document);
        if (legacyGraphTrayDocumentKeys.Contains(key))
        {
            return true;
        }

        if (legacyGraphManualDocumentLinks.ContainsKey(key) ||
            legacyGraphManualDocumentCustomerLinks.ContainsKey(key) ||
            legacyGraphUnconnectedDocumentKeys.Contains(key))
        {
            return false;
        }

        // 顧客候補が一意で車両だけ未確定の書類は、顧客ブロックから
        // 書類ブロックを確認できるようにします。候補顧客も不明な書類だけを
        // 初期状態の未確定トレイへ送ります。
        return !document.IsLinked && !IsLegacyGraphCustomerDirectDocument(document);
    }

    private bool IsLegacyGraphCustomerDirectDocument(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        if (IsLegacyGraphDocumentInTrash(document))
        {
            return false;
        }

        if (document.IsLinked || document.CandidateCustomerIds.Count != 1)
        {
            return false;
        }

        if (legacyGraphManualDocumentLinks.ContainsKey(GetLegacyDocumentKey(document)))
        {
            return false;
        }

        if (legacyGraphManualDocumentCustomerLinks.ContainsKey(GetLegacyDocumentKey(document)))
        {
            return false;
        }

        return FindOriginalVehicleForDocument(document) is null;
    }

    private bool IsLegacyGraphDocumentUnconnected(
        AbacusLegacyExportCandidateGraphDocument document) =>
        !IsLegacyGraphDocumentInTrash(document) &&
        legacyGraphUnconnectedDocumentKeys.Contains(GetLegacyDocumentKey(document));

    private bool IsManualCustomerLinkForCustomer(
        AbacusLegacyExportCandidateGraphDocument document,
        AbacusLegacyExportCandidateGraphCustomer customer) =>
        legacyGraphManualDocumentCustomerLinks.TryGetValue(
            GetLegacyDocumentKey(document),
            out var targetGroupKey) &&
        string.Equals(targetGroupKey, GetLegacyCustomerMergeKey(customer), StringComparison.Ordinal);

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetLegacyGraphUnconnectedDocuments(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var sourceCustomerIds = GetLegacyGraphLogicalCustomerMembers(customer)
            .Select(candidate => candidate.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        return GetLegacyGraphUnconnectedDocuments(sourceCustomerIds);
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetLegacyGraphUnconnectedDocuments(
        IReadOnlySet<string> sourceCustomerIds)
    {
        if (legacyExportCandidateGraphResult is null || sourceCustomerIds.Count == 0)
        {
            return [];
        }

        return legacyExportCandidateGraphResult.AllDocuments
            .Where(document => !IsLegacyGraphDocumentInTrash(document))
            .Where(IsLegacyGraphDocumentUnconnected)
            .Where(document => FindOriginalCustomerForDocument(document) is { } original &&
                               sourceCustomerIds.Contains(original.CustomerId))
            .ToArray();
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetLegacyGraphUnconnectedDocuments(
        IReadOnlySet<string> sourceCustomerIds,
        IReadOnlyDictionary<string, string> originalCustomerIdsByDocumentKey)
    {
        if (legacyExportCandidateGraphResult is null || sourceCustomerIds.Count == 0)
        {
            return [];
        }

        return legacyExportCandidateGraphResult.AllDocuments
            .Where(document => !IsLegacyGraphDocumentInTrash(document))
            .Where(IsLegacyGraphDocumentUnconnected)
            .Where(document => originalCustomerIdsByDocumentKey.TryGetValue(
                                   GetLegacyDocumentKey(document),
                                   out var originalCustomerId) &&
                               sourceCustomerIds.Contains(originalCustomerId))
            .ToArray();
    }

    private IReadOnlyDictionary<string, string> BuildLegacyGraphOriginalCustomerIdMap()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (legacyExportCandidateGraphResult is null)
        {
            return result;
        }

        foreach (var customer in legacyExportCandidateGraphResult.Customers)
        {
            foreach (var document in customer.UnresolvedDocuments)
            {
                result.TryAdd(GetLegacyDocumentKey(document), customer.CustomerId);
            }

            foreach (var vehicle in customer.Vehicles)
            {
                foreach (var document in vehicle.Documents)
                {
                    result.TryAdd(GetLegacyDocumentKey(document), customer.CustomerId);
                }
            }
        }

        return result;
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetLegacyGraphTrayDocuments(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        var sourceCustomerIds = GetLegacyGraphLogicalCustomerMembers(customer)
            .Select(candidate => candidate.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        return legacyExportCandidateGraphResult.AllDocuments
            .Where(IsLegacyGraphDocumentInTray)
            .Where(document => FindOriginalCustomerForDocument(document) is { } original &&
                               sourceCustomerIds.Contains(original.CustomerId))
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetLegacyGraphTrayDocuments()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        return legacyExportCandidateGraphResult.AllDocuments
            .Where(IsLegacyGraphDocumentInTray)
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetLegacyGraphCustomerDirectDocuments(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var customerGroupKey = GetLegacyCustomerMergeKey(customer);
        var documents = GetLegacyGraphLogicalCustomerMembers(customer)
            .SelectMany(source => source.UnresolvedDocuments)
            .Where(document => !IsLegacyGraphDocumentInTrash(document))
            .Where(IsLegacyGraphCustomerDirectDocument)
            .Where(document => !IsLegacyGraphDocumentInTray(document))
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (legacyExportCandidateGraphResult is null)
        {
            return documents;
        }

        foreach (var document in legacyExportCandidateGraphResult.AllDocuments.Where(candidate =>
                     !IsLegacyGraphDocumentInTrash(candidate) &&
                     !IsLegacyGraphDocumentInTray(candidate) &&
                     !IsLegacyGraphDocumentUnconnected(candidate) &&
                     legacyGraphManualDocumentCustomerLinks.TryGetValue(
                         GetLegacyDocumentKey(candidate), out var targetGroupKey) &&
                     string.Equals(targetGroupKey, customerGroupKey, StringComparison.Ordinal) &&
                     !legacyGraphManualDocumentLinks.ContainsKey(GetLegacyDocumentKey(candidate)) &&
                     documents.All(existing => !string.Equals(
                         GetLegacyDocumentKey(existing),
                         GetLegacyDocumentKey(candidate),
                         StringComparison.OrdinalIgnoreCase))))
        {
            documents.Add(document);
        }

        return documents;
    }

    private AbacusLegacyExportCandidateGraphCustomer? FindOriginalCustomerForDocument(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return null;
        }

        var key = GetLegacyDocumentKey(document);
        return legacyExportCandidateGraphResult.Customers.FirstOrDefault(customer =>
            customer.Vehicles.Any(vehicle => vehicle.Documents.Any(item =>
                string.Equals(GetLegacyDocumentKey(item), key, StringComparison.OrdinalIgnoreCase))) ||
            customer.UnresolvedDocuments.Any(item =>
                string.Equals(GetLegacyDocumentKey(item), key, StringComparison.OrdinalIgnoreCase)));
    }

    private AbacusLegacyExportCandidateGraphVehicle? FindOriginalVehicleForDocument(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return null;
        }

        var key = GetLegacyDocumentKey(document);
        return legacyExportCandidateGraphResult.Customers
            .SelectMany(customer => customer.Vehicles)
            .FirstOrDefault(vehicle => vehicle.Documents.Any(item =>
                string.Equals(GetLegacyDocumentKey(item), key, StringComparison.OrdinalIgnoreCase)));
    }

    private AbacusLegacyExportCandidateGraphCustomer? FindLegacyGraphCustomerForGroupKey(
        string groupKey)
    {
        if (TryGetLegacyGraphMergeGroup(groupKey, out var mergeGroup))
        {
            return mergeGroup.CustomerIds
                .Select(FindLegacyGraphCustomerById)
                .FirstOrDefault(customer => customer is not null);
        }

        const string customerPrefix = "customer:";
        if (groupKey.StartsWith(customerPrefix, StringComparison.Ordinal) &&
            FindLegacyGraphCustomerById(groupKey[customerPrefix.Length..]) is { } standaloneCustomer)
        {
            return standaloneCustomer;
        }

        return legacyExportCandidateGraphResult?.Customers.FirstOrDefault(customer =>
            string.Equals(GetLegacyCustomerMergeKey(customer), groupKey, StringComparison.Ordinal));
    }

    private void RehomeLegacyGraphManualDocumentCustomerLinks(
        string oldGroupKey,
        IReadOnlyCollection<string> memberCustomerIds,
        string? remainingCustomerId)
    {
        var memberIds = memberCustomerIds.ToHashSet(StringComparer.Ordinal);
        var affectedDocumentKeys = legacyGraphManualDocumentCustomerLinks
            .Where(pair => string.Equals(pair.Value, oldGroupKey, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var documentKey in affectedDocumentKeys)
        {
            if (remainingCustomerId is not null)
            {
                var remainingCustomer = FindLegacyGraphCustomerById(remainingCustomerId);
                var replacementGroupKey = remainingCustomer is null
                    ? $"customer:{remainingCustomerId}"
                    : GetLegacyCustomerMergeKey(remainingCustomer);
                if (string.Equals(replacementGroupKey, oldGroupKey, StringComparison.Ordinal))
                {
                    replacementGroupKey = $"customer:{remainingCustomerId}";
                }

                legacyGraphManualDocumentCustomerLinks[documentKey] =
                    replacementGroupKey;
                continue;
            }

            var document = legacyExportCandidateGraphResult?.AllDocuments.FirstOrDefault(item =>
                string.Equals(GetLegacyDocumentKey(item), documentKey, StringComparison.OrdinalIgnoreCase));
            var originalCustomer = document is null
                ? null
                : FindOriginalCustomerForDocument(document);
            if (originalCustomer is not null && memberIds.Contains(originalCustomer.CustomerId))
            {
                legacyGraphManualDocumentCustomerLinks[documentKey] =
                    $"customer:{originalCustomer.CustomerId}";
                continue;
            }

            legacyGraphManualDocumentCustomerLinks.Remove(documentKey);
            legacyGraphDocumentLinkMethods.Remove(documentKey);
            legacyGraphDocumentLinkReasons.Remove(documentKey);
            if (!legacyGraphTrayDocumentKeys.Contains(documentKey) &&
                !legacyGraphTrashDocumentKeys.Contains(documentKey))
            {
                legacyGraphUnconnectedDocumentKeys.Add(documentKey);
            }
        }
    }

    /// <summary>
    /// 書類の現在の所属先を、手動状態を優先して解決します。
    /// 元CSVの所有者は、手動車両リンク・顧客直結リンクがない場合だけ利用します。
    /// </summary>
    private AbacusLegacyExportCandidateGraphCustomer? FindCurrentCustomerForDocument(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        var key = GetLegacyDocumentKey(document);
        string? manualVehicleCustomerId = null;
        if (legacyGraphManualDocumentLinks.TryGetValue(key, out var manualVehicleId) &&
            FindLegacyGraphVehicleById(manualVehicleId) is { } manualVehicle)
        {
            manualVehicleCustomerId = FindCurrentCustomerForVehicle(manualVehicle)?.CustomerId;
        }

        string? manualCustomerGroupCustomerId = null;
        if (legacyGraphManualDocumentCustomerLinks.TryGetValue(key, out var manualGroupKey) &&
            FindLegacyGraphCustomerForGroupKey(manualGroupKey) is { } directCustomer)
        {
            manualCustomerGroupCustomerId = directCustomer.CustomerId;
        }

        string? linkedVehicleCustomerId = null;
        if (!string.IsNullOrWhiteSpace(document.LinkedVehicleId) &&
            FindLegacyGraphVehicleById(document.LinkedVehicleId) is { } linkedVehicle &&
            FindCurrentCustomerForVehicle(linkedVehicle) is { } linkedCustomer)
        {
            linkedVehicleCustomerId = linkedCustomer.CustomerId;
        }

        var currentCustomerId = LegacyGraphDocumentOwnership.ResolveCurrentCustomerId(
            manualVehicleCustomerId,
            manualCustomerGroupCustomerId,
            linkedVehicleCustomerId,
            FindOriginalCustomerForDocument(document)?.CustomerId);
        return currentCustomerId is null
            ? null
            : FindLegacyGraphCustomerById(currentCustomerId);
    }

    private AbacusLegacyExportCandidateGraphVehicle? FindCurrentVehicleForDocument(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        var vehicleId = ResolveCurrentDocumentVehicleId(document);
        return vehicleId is null ? null : FindLegacyGraphVehicleById(vehicleId);
    }

    private string? ResolveCurrentDocumentVehicleId(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        var key = GetLegacyDocumentKey(document);
        var manualVehicleId = legacyGraphManualDocumentLinks.TryGetValue(
            key,
            out var manualVehicleIdCandidate) &&
            FindLegacyGraphVehicleById(manualVehicleIdCandidate) is not null
            ? manualVehicleIdCandidate
            : null;
        var linkedVehicleId = !string.IsNullOrWhiteSpace(document.LinkedVehicleId) &&
                              FindLegacyGraphVehicleById(document.LinkedVehicleId) is not null
            ? document.LinkedVehicleId
            : null;
        var originalVehicleId = FindOriginalVehicleForDocument(document)?.VehicleId;
        return LegacyGraphDocumentOwnership.ResolveCurrentVehicleId(
            manualVehicleId,
            linkedVehicleId,
            originalVehicleId,
            legacyGraphManualDocumentCustomerLinks.ContainsKey(key));
    }

    private AbacusLegacyExportCandidateGraphCustomer? FindCurrentCustomerForVehicle(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        if (legacyGraphManualVehicleCustomerLinks.TryGetValue(
                vehicle.VehicleId,
                out var manualCustomerId) &&
            FindLegacyGraphCustomerById(manualCustomerId) is { } manualCustomer)
        {
            return manualCustomer;
        }

        return vehicle.HasCustomer
            ? FindLegacyGraphCustomerById(vehicle.CustomerId)
            : null;
    }

    private void RenderLegacyGraphCustomer(AbacusLegacyExportCandidateGraphCustomer customer)
    {
        ClearLegacyGraphBlockVisualDrag();
        EndLegacyGraphConnectionPreview();
        LegacyGraphCanvas.Children.Clear();
        LegacyGraphEdgesCanvas.Children.Clear();
        ClearLegacyMatchingRecommendationPreview();
        LegacyGraphPageDragPreviewCanvas.Children.Clear();
        LegacyGraphBoardGrid.Width = 1120;
        LegacyGraphBoardGrid.Height = LegacyGraphMinimumCanvasHeight;
        LegacyGraphCanvas.Width = 1120;
        LegacyGraphCanvas.Height = LegacyGraphMinimumCanvasHeight;
        LegacyGraphEdgesCanvas.Width = 1120;
        LegacyGraphEdgesCanvas.Height = LegacyGraphMinimumCanvasHeight;
        LegacyMatchingRecommendationPreviewEdgesCanvas.Width = 1120;
        LegacyMatchingRecommendationPreviewEdgesCanvas.Height = LegacyGraphMinimumCanvasHeight;
        LegacyMatchingRecommendationPreviewCanvas.Width = 1120;
        LegacyMatchingRecommendationPreviewCanvas.Height = LegacyGraphMinimumCanvasHeight;
        legacyGraphEdges.Clear();
        const double customerX = 30;
        const double customerY = 32;
        const double customerWidth = 225;
        const double customerHeight = 112;
        const double vehicleX = 320;
        const double vehicleWidth = 290;
        const double vehicleHeight = 112;
        const double documentX = 690;
        const double documentWidth = 390;
        const double documentHeight = 118;
        const double documentSpacing = 24;

        var customerMergeKey = GetLegacyCustomerMergeKey(customer);
        var isCustomerMergeGroup = TryGetLegacyGraphMergeGroup(customerMergeKey, out var customerMergeGroup) &&
                                    customerMergeGroup.CustomerIds.Count > 1;
        var customerSource = GetLegacyGraphSourceCustomer(customer);
        var customerReviewSnapshot = GetLegacyGraphCustomerReviewSnapshot(
            customerSource,
            useGraphStructuralMergeIntent: true);
        var isCustomerApproved = string.Equals(
            customerReviewSnapshot.Status,
            LegacyGraphCustomerReviewStateValues.Approved,
            StringComparison.Ordinal);
        var customerNeedsReview = string.Equals(
            customerReviewSnapshot.Status,
            LegacyGraphCustomerReviewStateValues.NeedsReview,
            StringComparison.Ordinal);
        var isCustomerMergeApplied = IsLegacyGraphLogicalCustomerGroup(customerMergeKey) ||
                                      legacyGraphAppliedCustomerMergeKeys.Contains(customerMergeKey);
        var customerStroke = isCustomerApproved
            ? "#2563EB"
            : customerNeedsReview || isCustomerMergeGroup || isCustomerMergeApplied
                ? "#D97706"
                : "#718096";
        var customerFill = isCustomerApproved
            ? "#EAF2FF"
            : customerNeedsReview || isCustomerMergeGroup || isCustomerMergeApplied
                ? "#FFF7ED"
                : "#F4F7FB";
        var customerBadge = isCustomerApproved
            ? "顧客確認済み"
            : customerNeedsReview
                ? "再確認待ち"
            : isCustomerMergeApplied
                ? "統合済み・確認待ち"
                : isCustomerMergeGroup
                    ? legacyGraphCustomerMergeDrafts.ContainsKey(customerMergeKey)
                        ? "仮反映中"
                        : "統合候補"
                    : "顧客確認待ち";

        var customerBlock = CreateLegacyGraphBlock(
            $"顧客: {GetLegacyGraphCustomerDisplayName(customer)}",
            $"ID: {customer.CustomerId}\n車両 {GetLegacyGraphVehiclesForDisplay(customer).Count:N0}台 / 書類 {GetLegacyGraphDisplayedDocumentCount(customer):N0}件",
            customerStroke,
            customerFill,
            dashed: false,
            customerWidth,
            customerHeight,
            customerBadge);
        customerBlock.Tag = customer;
        AttachLegacyGraphCustomerDropTarget(customerBlock, customer);
        AddGraphElement(customerBlock, customerX, customerY);

        // 書類は同じ列に縦積みします。固定間隔では長いカードが重なるため、
        // 実際にレイアウトされたカードの高さを使って次の位置を決めます。
        var nextVehicleY = customerY;
        var displayVehicles = GetLegacyGraphVehiclesForDisplay(customer);
        foreach (var vehicle in displayVehicles)
        {
            // 車両と、その車両に属する最初の書類を同じ高さに揃えます。
            var vehicleY = nextVehicleY;
            var vehicleTitle = string.IsNullOrWhiteSpace(vehicle.Maker)
                ? vehicle.DisplayName
                : $"{vehicle.Maker} {vehicle.DisplayName}";
            var vehicleBlock = CreateLegacyGraphBlock(
                $"車両: {vehicleTitle}",
                $"登録: {Fallback(vehicle.RegistrationNumber)}\n車台: {Fallback(vehicle.ChassisNumber)}",
                "#059669",
                "#ECFDF5",
                dashed: false,
                vehicleWidth,
                vehicleHeight);
            vehicleBlock.Tag = vehicle;
            AttachLegacyGraphVehicleDropTarget(vehicleBlock, vehicle);
            AttachLegacyGraphVehicleNode(vehicleBlock, vehicle);
            AddGraphElement(vehicleBlock, vehicleX, vehicleY);
            AddGraphEdge(customerBlock, vehicleBlock, "#2563EB", dashed: false);

            var vehicleDocuments = GetDocumentsForVehicle(vehicle);
            var documentY = vehicleY;
            foreach (var document in vehicleDocuments)
            {
                var documentBlock = CreateLegacyDocumentBlock(
                    document,
                    documentWidth,
                    documentHeight,
                    IsManualLinkForVehicle(document, vehicle.VehicleId),
                    unconnected: false);
                documentBlock.Tag = document;
                AddGraphElement(documentBlock, documentX, documentY);
                var isManualLink = IsManualLinkForVehicle(document, vehicle.VehicleId);
                AddGraphEdge(vehicleBlock, documentBlock, isManualLink ? "#2563EB" : "#166534", dashed: isManualLink);
                LegacyGraphCanvas.UpdateLayout();
                documentY += GetLegacyGraphElementHeight(documentBlock) + documentSpacing;
            }

            LegacyGraphCanvas.UpdateLayout();
            var vehicleBottom = vehicleY + GetLegacyGraphElementHeight(vehicleBlock);
            var documentBottom = vehicleDocuments.Count == 0
                ? vehicleY
                : documentY - documentSpacing;
            nextVehicleY = Math.Max(vehicleBottom, documentBottom) + documentSpacing;
        }

        var nextDocumentY = nextVehicleY;

        var unresolvedDocuments = customer.UnresolvedDocuments
            .Where(document => !IsLegacyGraphDocumentInTrash(document))
            .Where(document => !IsLegacyGraphDocumentInTray(document))
            .Where(document => !IsLegacyGraphDocumentUnconnected(document))
            .Where(document => !legacyGraphManualDocumentLinks.ContainsKey(GetLegacyDocumentKey(document)))
            .Where(document => !legacyGraphManualDocumentCustomerLinks.ContainsKey(GetLegacyDocumentKey(document)))
            .Where(document => !IsLegacyGraphCustomerDirectDocument(document))
            .ToArray();
        if (unresolvedDocuments.Length > 0)
        {
            LegacyGraphCanvas.UpdateLayout();
            var assignedContentBottom = LegacyGraphCanvas.Children
                .OfType<FrameworkElement>()
                .Select(element => Canvas.GetTop(element) + GetLegacyGraphElementHeight(element))
                .DefaultIfEmpty(nextDocumentY)
                .Max();
            nextDocumentY = Math.Max(nextDocumentY, assignedContentBottom + 20);
        }
        var unresolvedDocumentY = nextDocumentY;
        foreach (var document in unresolvedDocuments)
        {
            var documentBlock = CreateLegacyDocumentBlock(document, documentWidth, documentHeight, manual: false, unconnected: false);
            documentBlock.Tag = document;
            AddGraphElement(documentBlock, documentX, unresolvedDocumentY);
            AddGraphEdge(customerBlock, documentBlock, "#D97706", dashed: true);
            LegacyGraphCanvas.UpdateLayout();
            unresolvedDocumentY += GetLegacyGraphElementHeight(documentBlock) + documentSpacing;
        }

        // 車両情報がない書類は、顧客ブロックから直接つながる特例として表示します。
        // トレイから顧客へ手動で戻した書類も同じ列に表示し、車両未確定と区別します。
        var customerDirectDocuments = GetLegacyGraphCustomerDirectDocuments(customer);
        if (customerDirectDocuments.Count > 0)
        {
            LegacyGraphCanvas.UpdateLayout();
            var assignedContentBottom = LegacyGraphCanvas.Children
                .OfType<FrameworkElement>()
                .Select(element => Canvas.GetTop(element) + GetLegacyGraphElementHeight(element))
                .DefaultIfEmpty(nextDocumentY)
                .Max();
            unresolvedDocumentY = Math.Max(unresolvedDocumentY, assignedContentBottom + 20);
        }

        foreach (var document in customerDirectDocuments)
        {
            var isManualCustomerLink = IsManualCustomerLinkForCustomer(document, customer);
            var documentBlock = CreateLegacyDocumentBlock(
                document,
                documentWidth,
                documentHeight,
                manual: false,
                unconnected: false,
                customerManual: isManualCustomerLink);
            documentBlock.Tag = document;
            AddGraphElement(documentBlock, documentX, unresolvedDocumentY);
            AddGraphEdge(
                customerBlock,
                documentBlock,
                isManualCustomerLink ? "#2563EB" : "#D97706",
                dashed: true);
            LegacyGraphCanvas.UpdateLayout();
            unresolvedDocumentY += GetLegacyGraphElementHeight(documentBlock) + documentSpacing;
        }

        var unconnectedDocuments = GetLegacyGraphUnconnectedDocuments(customer);
        if (unconnectedDocuments.Count > 0)
        {
            LegacyGraphCanvas.UpdateLayout();
            var assignedContentBottom = LegacyGraphCanvas.Children
                .OfType<FrameworkElement>()
                .Select(element => Canvas.GetTop(element) + GetLegacyGraphElementHeight(element))
                .DefaultIfEmpty(nextDocumentY)
                .Max();
            unresolvedDocumentY = Math.Max(unresolvedDocumentY, assignedContentBottom + 20);
        }
        foreach (var document in unconnectedDocuments)
        {
            var documentBlock = CreateLegacyDocumentBlock(document, documentWidth, documentHeight, manual: false, unconnected: true);
            documentBlock.Tag = document;
            AddGraphElement(documentBlock, documentX, unresolvedDocumentY);
            LegacyGraphCanvas.UpdateLayout();
            unresolvedDocumentY += GetLegacyGraphElementHeight(documentBlock) + documentSpacing;
        }

        var hasPendingVehicleOrDocumentRecommendations =
            GetLegacyGraphMatchingRecommendations(customer).Any(candidate =>
            {
                var categoryKind = LegacyMatchingCategoryKinds.GetKind(candidate);
                return (categoryKind == LegacyMatchingCategoryKinds.Vehicle ||
                        categoryKind == LegacyMatchingCategoryKinds.Document) &&
                       GetLegacyGraphRecommendationDecision(candidate) is
                           AbacusRecommendationDecisionValues.Pending or AbacusRecommendationDecisionValues.Hold;
            });
        if (displayVehicles.Count == 0 &&
            customer.UnresolvedDocuments.Any(document => !IsLegacyGraphDocumentInTrash(document)) == false &&
            customerDirectDocuments.Count == 0 &&
            unconnectedDocuments.Count == 0 &&
            !hasPendingVehicleOrDocumentRecommendations)
        {
            var emptyBlock = CreateLegacyGraphBlock(
                "表示できる車両・書類はありません",
                "顧客CSVには存在しますが、候補CSVに車両・書類がありません。",
                "#718096",
                "#F4F7FB",
                dashed: true,
                vehicleWidth,
                vehicleHeight);
            emptyBlock.Tag = customer;
            AddGraphElement(emptyBlock, vehicleX, vehicleY: 140);
        }

        LegacyGraphCanvas.Width = 1120;
        LegacyGraphCanvas.UpdateLayout();
        var contentBottom = LegacyGraphCanvas.Children
            .OfType<FrameworkElement>()
            .Select(element => Canvas.GetTop(element) + GetLegacyGraphElementHeight(element))
            .DefaultIfEmpty(LegacyGraphMinimumCanvasHeight)
            .Max();
        LegacyGraphCanvas.Height = Math.Max(LegacyGraphMinimumCanvasHeight, contentBottom + 70);
        LegacyGraphEdgesCanvas.Width = LegacyGraphCanvas.Width;
        LegacyGraphEdgesCanvas.Height = LegacyGraphCanvas.Height;
        LegacyMatchingRecommendationPreviewEdgesCanvas.Width = LegacyGraphCanvas.Width;
        LegacyMatchingRecommendationPreviewEdgesCanvas.Height = LegacyGraphCanvas.Height;
        LegacyMatchingRecommendationPreviewCanvas.Width = LegacyGraphCanvas.Width;
        LegacyMatchingRecommendationPreviewCanvas.Height = LegacyGraphCanvas.Height;
        LegacyGraphBoardGrid.Width = LegacyGraphCanvas.Width;
        LegacyGraphBoardGrid.Height = LegacyGraphCanvas.Height;
        LegacyGraphCanvas.UpdateLayout();
        UpdateLegacyGraphEdges();
    }

    private void ClearLegacyMatchingRecommendationPreview()
    {
        if (LegacyMatchingRecommendationPreviewEdgesCanvas is null ||
            LegacyMatchingRecommendationPreviewCanvas is null)
        {
            return;
        }

        LegacyMatchingRecommendationPreviewEdgesCanvas.Children.Clear();
        LegacyMatchingRecommendationPreviewCanvas.Children.Clear();
        LegacyMatchingPreviewLegend.Visibility = Visibility.Collapsed;
    }

    private void RefreshLegacyMatchingRecommendationPreview(
        AbacusRecommendationCandidate? candidate)
    {
        ClearLegacyMatchingRecommendationPreview();
        if (!string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase) ||
            LegacyMatchingRecommendationContent.Visibility != Visibility.Visible ||
            candidate is null ||
            GetLegacyGraphRecommendationDecision(candidate) is not (AbacusRecommendationDecisionValues.Pending or
                AbacusRecommendationDecisionValues.Hold))
        {
            return;
        }

        LegacyMatchingPreviewLegend.Visibility = Visibility.Visible;

        const double customerX = 30;
        const double vehicleX = 320;
        const double documentX = 690;
        const double customerWidth = 225;
        const double vehicleWidth = 290;
        const double documentWidth = 390;
        const double blockHeight = 112;
        const double documentHeight = 118;
        var previewY = GetLegacyMatchingRecommendationPreviewBottom() + 24;
        var focusCustomer = GetLegacyGraphMatchingCustomer();
        var targetCustomer = ResolveLegacyMatchingRecommendationTargetCustomer(candidate);
        var customerBlock = FindLegacyMatchingCanvasCustomer(targetCustomer);

        switch (candidate.SubjectKind, candidate.TargetKind)
        {
            case (AbacusRecommendationEntityKinds.Vehicle, AbacusRecommendationEntityKinds.Customer):
                if (FindLegacyGraphVehicleById(candidate.SubjectId) is not { } vehicle || targetCustomer is null)
                {
                    return;
                }

                var vehicleCustomerRole = GetLegacyMatchingPreviewCustomerRole(targetCustomer, focusCustomer);
                customerBlock ??= CreateLegacyMatchingPreviewCustomerBlock(
                    targetCustomer,
                    customerX,
                    previewY,
                    customerWidth,
                    blockHeight,
                    vehicleCustomerRole);
                var vehicleBlock = FindLegacyMatchingCanvasVehicle(vehicle.VehicleId) ??
                    CreateLegacyMatchingPreviewVehicleBlock(
                        vehicle,
                        previewY,
                        vehicleX,
                        vehicleWidth,
                        blockHeight,
                        GetLegacyMatchingCurrentLinkSummary(vehicle));
                AddLegacyMatchingPreviewEdge(
                    customerBlock,
                    vehicleBlock,
                    GetLegacyMatchingPreviewEdgeLabel(vehicleCustomerRole, "承認後"));
                break;

            case (AbacusRecommendationEntityKinds.Document, AbacusRecommendationEntityKinds.Vehicle):
                if (FindLegacyRecommendationDocumentById(candidate.SubjectId) is not { } document ||
                    FindLegacyGraphVehicleById(candidate.TargetId) is not { } targetVehicle ||
                    (targetCustomer = FindCurrentCustomerForVehicle(targetVehicle)) is null)
                {
                    return;
                }

                customerBlock = FindLegacyMatchingCanvasCustomer(targetCustomer);
                var targetVehicleBlock = FindLegacyMatchingCanvasVehicle(targetVehicle.VehicleId);
                if (targetVehicleBlock is null)
                {
                    var documentVehicleCustomerRole = GetLegacyMatchingPreviewCustomerRole(targetCustomer, focusCustomer);
                    customerBlock ??= CreateLegacyMatchingPreviewCustomerBlock(
                        targetCustomer,
                        customerX,
                        previewY,
                        customerWidth,
                        blockHeight,
                        documentVehicleCustomerRole);
                    targetVehicleBlock = CreateLegacyMatchingPreviewVehicleBlock(
                        targetVehicle,
                        previewY,
                        vehicleX,
                        vehicleWidth,
                        blockHeight,
                        "候補接続先");
                    AddLegacyMatchingPreviewEdge(
                        customerBlock,
                        targetVehicleBlock,
                        GetLegacyMatchingPreviewEdgeLabel(documentVehicleCustomerRole, "接続先"));
                }

                var documentBlock = FindLegacyMatchingCanvasDocument(document) ??
                    CreateLegacyMatchingPreviewDocumentBlock(
                        document,
                        previewY,
                        documentX,
                        documentWidth,
                        documentHeight,
                        GetLegacyMatchingCurrentLinkSummary(document));
                AddLegacyMatchingPreviewEdge(targetVehicleBlock, documentBlock, "承認後");
                break;

            case (AbacusRecommendationEntityKinds.Document, AbacusRecommendationEntityKinds.Customer):
                if (FindLegacyRecommendationDocumentById(candidate.SubjectId) is not { } directDocument ||
                    (targetCustomer = FindLegacyGraphCustomerById(candidate.TargetId)) is null)
                {
                    return;
                }

                var directCustomerRole = GetLegacyMatchingPreviewCustomerRole(targetCustomer, focusCustomer);
                customerBlock = FindLegacyMatchingCanvasCustomer(targetCustomer) ??
                    CreateLegacyMatchingPreviewCustomerBlock(
                        targetCustomer,
                        customerX,
                        previewY,
                        customerWidth,
                        blockHeight,
                        directCustomerRole);
                var directDocumentBlock = FindLegacyMatchingCanvasDocument(directDocument) ??
                    CreateLegacyMatchingPreviewDocumentBlock(
                        directDocument,
                        previewY,
                        documentX,
                        documentWidth,
                        documentHeight,
                        GetLegacyMatchingCurrentLinkSummary(directDocument));
                AddLegacyMatchingPreviewEdge(
                    customerBlock,
                    directDocumentBlock,
                    GetLegacyMatchingPreviewEdgeLabel(directCustomerRole, "顧客へ直接・承認後"));
                break;
        }

        LegacyMatchingRecommendationPreviewEdgesCanvas.UpdateLayout();
        LegacyMatchingRecommendationPreviewCanvas.UpdateLayout();
        var previewBottom = LegacyMatchingRecommendationPreviewCanvas.Children
            .OfType<FrameworkElement>()
            .Select(element => Canvas.GetTop(element) + GetLegacyGraphElementHeight(element))
            .DefaultIfEmpty(LegacyGraphMinimumCanvasHeight)
            .Max();
        var canvasHeight = Math.Max(
            LegacyGraphMinimumCanvasHeight,
            Math.Max(LegacyGraphCanvas.Height, previewBottom + 50));
        LegacyGraphCanvas.Height = canvasHeight;
        LegacyGraphEdgesCanvas.Height = canvasHeight;
        LegacyMatchingRecommendationPreviewEdgesCanvas.Height = canvasHeight;
        LegacyMatchingRecommendationPreviewCanvas.Height = canvasHeight;
        LegacyGraphBoardGrid.Height = canvasHeight;
    }

    private double GetLegacyMatchingRecommendationPreviewBottom()
    {
        var graphBottom = LegacyGraphCanvas.Children
            .OfType<FrameworkElement>()
            .Select(element => Canvas.GetTop(element) + GetLegacyGraphElementHeight(element))
            .DefaultIfEmpty(32)
            .Max();
        var previewBottom = LegacyMatchingRecommendationPreviewCanvas.Children
            .OfType<FrameworkElement>()
            .Select(element => Canvas.GetTop(element) + GetLegacyGraphElementHeight(element))
            .DefaultIfEmpty(32)
            .Max();
        return Math.Max(graphBottom, previewBottom);
    }

    private AbacusLegacyExportCandidateGraphCustomer? ResolveLegacyMatchingRecommendationTargetCustomer(
        AbacusRecommendationCandidate candidate)
    {
        if (candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            return FindLegacyGraphCustomerById(candidate.TargetId);
        }

        return candidate.TargetKind == AbacusRecommendationEntityKinds.Vehicle
            ? FindLegacyGraphVehicleById(candidate.TargetId) is { } vehicle
                ? FindCurrentCustomerForVehicle(vehicle)
                : null
            : null;
    }

    private string GetLegacyMatchingPreviewCustomerRole(
        AbacusLegacyExportCandidateGraphCustomer? targetCustomer,
        AbacusLegacyExportCandidateGraphCustomer? focusCustomer)
    {
        if (targetCustomer is null || focusCustomer is null)
        {
            return "候補顧客 / 承認後";
        }

        var focusSource = GetLegacyGraphSourceCustomer(focusCustomer);
        var focusCustomerIds = GetLegacyGraphLogicalCustomerMembers(focusSource)
            .Select(customer => customer.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        return focusCustomerIds.Contains(targetCustomer.CustomerId)
            ? "現在の顧客 / 承認後"
            : "候補顧客 / 承認後";
    }

    private static string GetLegacyMatchingPreviewEdgeLabel(
        string customerRole,
        string currentLabel) =>
        customerRole == "現在の顧客 / 承認後"
            ? currentLabel
            : "別顧客候補・承認後";

    private FrameworkElement? FindLegacyMatchingCanvasCustomer(
        AbacusLegacyExportCandidateGraphCustomer? targetCustomer)
    {
        if (targetCustomer is null)
        {
            return null;
        }

        var targetMergeKey = GetLegacyCustomerMergeKey(targetCustomer);
        return LegacyGraphCanvas.Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(element => element.Tag is AbacusLegacyExportCandidateGraphCustomer customer &&
                (string.Equals(customer.CustomerId, targetCustomer.CustomerId, StringComparison.Ordinal) ||
                 string.Equals(GetLegacyCustomerMergeKey(customer), targetMergeKey, StringComparison.Ordinal)));
    }

    private FrameworkElement? FindLegacyMatchingCanvasVehicle(string vehicleId) =>
        LegacyGraphCanvas.Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(element => element.Tag is AbacusLegacyExportCandidateGraphVehicle vehicle &&
                                       string.Equals(vehicle.VehicleId, vehicleId, StringComparison.Ordinal));

    private FrameworkElement? FindLegacyMatchingCanvasDocument(
        AbacusLegacyExportCandidateGraphDocument document) =>
        LegacyGraphCanvas.Children
            .OfType<FrameworkElement>()
            .FirstOrDefault(element => element.Tag is AbacusLegacyExportCandidateGraphDocument graphDocument &&
                                       string.Equals(
                                           GetLegacyDocumentKey(graphDocument),
                                           GetLegacyDocumentKey(document),
                                           StringComparison.OrdinalIgnoreCase));

    private Grid CreateLegacyMatchingPreviewCustomerBlock(
        AbacusLegacyExportCandidateGraphCustomer customer,
        double left,
        double top,
        double width,
        double height,
        string roleText = "候補顧客 / 承認後") =>
        AddLegacyMatchingPreviewBlock(
            $"{(roleText.StartsWith("現在の", StringComparison.Ordinal) ? "基準顧客" : "候補顧客")}: {GetLegacyGraphCustomerDisplayName(customer)}",
            $"顧客番号: {Fallback(customer.CustomerNumber)}\n{roleText}",
            left,
            top,
            width,
            height);

    private Grid CreateLegacyMatchingPreviewVehicleBlock(
        AbacusLegacyExportCandidateGraphVehicle vehicle,
        double top,
        double left,
        double width,
        double height,
        string relationText) =>
        AddLegacyMatchingPreviewBlock(
            $"車両: {Fallback(vehicle.Maker)} {vehicle.DisplayName}".Trim(),
            $"登録: {Fallback(vehicle.RegistrationNumber)}\n車台: {Fallback(vehicle.ChassisNumber)}\n{relationText}",
            left,
            top,
            width,
            height);

    private Grid CreateLegacyMatchingPreviewDocumentBlock(
        AbacusLegacyExportCandidateGraphDocument document,
        double top,
        double left,
        double width,
        double height,
        string currentLinkText) =>
        AddLegacyMatchingPreviewBlock(
            $"{document.Kind}: {Fallback(document.DocumentNumber)}",
            $"{Fallback(document.CustomerName)} / {Fallback(document.VehicleName)}\n" +
            $"日付: {Fallback(document.DocumentDate)} / 合計: {Fallback(document.TotalAmount)}\n" +
            $"{currentLinkText}",
            left,
            top,
            width,
            height);

    private Grid AddLegacyMatchingPreviewBlock(
        string title,
        string subtitle,
        double left,
        double top,
        double width,
        double height)
    {
        var block = new Grid
        {
            Width = width,
            MinHeight = height,
            Height = double.NaN,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.9,
            IsHitTestVisible = false,
        };
        var outline = new Rectangle
        {
            Stroke = ToBrush("#7C3AED"),
            Fill = ToBrush("#F5F3FF"),
            StrokeThickness = 3,
            RadiusX = 7,
            RadiusY = 7,
            StrokeDashArray = new DoubleCollection { 6, 4 },
        };
        var content = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        content.Children.Add(new Border
        {
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ToBrush("#7C3AED"),
            CornerRadius = new CornerRadius(3),
            Child = new TextBlock
            {
                Text = "おすすめ・承認後プレビュー",
                FontSize = 10,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
            },
        });
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = ToBrush("#10233F"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            Text = subtitle,
            FontSize = 12,
            Foreground = ToBrush("#5B21B6"),
            TextWrapping = TextWrapping.Wrap,
        });
        block.Children.Add(outline);
        block.Children.Add(content);
        LegacyMatchingRecommendationPreviewCanvas.Children.Add(block);
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        return block;
    }

    private void AddLegacyMatchingPreviewEdge(
        FrameworkElement source,
        FrameworkElement target,
        string label)
    {
        LegacyGraphCanvas.UpdateLayout();
        LegacyMatchingRecommendationPreviewCanvas.UpdateLayout();
        var sourceLeft = Canvas.GetLeft(source);
        var sourceTop = Canvas.GetTop(source);
        var targetLeft = Canvas.GetLeft(target);
        var targetTop = Canvas.GetTop(target);
        var sourceWidth = GetLegacyGraphElementWidth(source);
        var sourceHeight = GetLegacyGraphElementHeight(source);
        var targetWidth = GetLegacyGraphElementWidth(target);
        var targetHeight = GetLegacyGraphElementHeight(target);
        var sourceRight = sourceLeft + sourceWidth;
        var targetRight = targetLeft + targetWidth;
        var line = new Line
        {
            Stroke = ToBrush("#7C3AED"),
            StrokeThickness = 3,
            Opacity = 0.98,
            StrokeDashArray = new DoubleCollection { 6, 4 },
            IsHitTestVisible = false,
        };
        var parallelOffset = (LegacyMatchingRecommendationPreviewEdgesCanvas.Children.Count % 3 - 1) * 8;
        if (sourceLeft <= targetLeft)
        {
            line.X1 = sourceRight;
            line.Y1 = sourceTop + sourceHeight / 2 + parallelOffset;
            line.X2 = targetLeft;
            line.Y2 = targetTop + targetHeight / 2 + parallelOffset;
        }
        else
        {
            line.X1 = sourceLeft;
            line.Y1 = sourceTop + sourceHeight / 2 + parallelOffset;
            line.X2 = targetRight;
            line.Y2 = targetTop + targetHeight / 2 + parallelOffset;
        }

        LegacyMatchingRecommendationPreviewEdgesCanvas.Children.Add(line);
        var edgeLabel = new Border
        {
            Padding = new Thickness(4, 1, 4, 1),
            Background = ToBrush("#F5F3FF"),
            BorderBrush = ToBrush("#A78BFA"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = ToBrush("#5B21B6"),
                FontWeight = FontWeights.SemiBold,
            },
        };
        LegacyMatchingRecommendationPreviewCanvas.Children.Add(edgeLabel);
        Canvas.SetLeft(edgeLabel, (line.X1 + line.X2) / 2 - 28);
        Canvas.SetTop(edgeLabel, (line.Y1 + line.Y2) / 2 - 12);
    }

    private string GetLegacyMatchingCurrentLinkSummary(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        var customer = FindCurrentCustomerForVehicle(vehicle);
        return customer is null
            ? "現在: 未接続"
            : $"現在: {GetLegacyGraphCustomerDisplayName(customer)}";
    }

    private string GetLegacyGraphCurrentVehicleOwnerName(
        AbacusLegacyExportCandidateGraphVehicle vehicle) =>
        FindCurrentCustomerForVehicle(vehicle) is { } customer
            ? GetLegacyGraphCustomerDisplayName(customer)
            : "未接続";

    private string GetLegacyMatchingCurrentLinkSummary(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        var key = GetLegacyDocumentKey(document);
        if (legacyGraphManualDocumentLinks.TryGetValue(key, out var vehicleId) &&
            FindLegacyGraphVehicleById(vehicleId) is { } manualVehicle)
        {
            return $"現在: 車両 {manualVehicle.DisplayName}";
        }

        if (legacyGraphManualDocumentCustomerLinks.TryGetValue(key, out var targetGroupKey) &&
            FindLegacyGraphCustomerForGroupKey(targetGroupKey) is { } manualCustomer)
        {
            return $"現在: {GetLegacyGraphCustomerDisplayName(manualCustomer)}へ直接接続";
        }

        if (!string.IsNullOrWhiteSpace(document.LinkedVehicleId) &&
            FindLegacyGraphVehicleById(document.LinkedVehicleId) is { } linkedVehicle)
        {
            return $"現在: 車両 {linkedVehicle.DisplayName}";
        }

        return FindCurrentCustomerForDocument(document) is { } customer
            ? $"現在: {GetLegacyGraphCustomerDisplayName(customer)}"
            : "現在: 未接続";
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetDocumentsForVehicle(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        var documents = vehicle.Documents
            .Where(document => !IsLegacyGraphDocumentInTrash(document))
            .Where(document => !IsLegacyGraphDocumentInTray(document))
            .Where(document => !IsLegacyGraphDocumentUnconnected(document))
            .Where(document => !legacyGraphManualDocumentCustomerLinks.ContainsKey(GetLegacyDocumentKey(document)))
            .Where(document => !legacyGraphManualDocumentLinks.TryGetValue(
                GetLegacyDocumentKey(document),
                out var targetVehicleId) ||
                string.Equals(targetVehicleId, vehicle.VehicleId, StringComparison.Ordinal))
            .ToList();

        if (legacyExportCandidateGraphResult is null)
        {
            return documents;
        }

        foreach (var document in legacyExportCandidateGraphResult.AllDocuments.Where(document =>
                     !IsLegacyGraphDocumentInTrash(document) &&
                     !IsLegacyGraphDocumentInTray(document) &&
                     !IsLegacyGraphDocumentUnconnected(document) &&
                     !legacyGraphManualDocumentCustomerLinks.ContainsKey(GetLegacyDocumentKey(document)) &&
                     IsManualLinkForVehicle(document, vehicle.VehicleId) &&
                     documents.All(existing => !string.Equals(
                         GetLegacyDocumentKey(existing),
                         GetLegacyDocumentKey(document),
                         StringComparison.OrdinalIgnoreCase))))
        {
            documents.Add(document);
        }

        return documents;
    }

    private Grid CreateLegacyDocumentBlock(
        AbacusLegacyExportCandidateGraphDocument document,
        double width,
        double height,
        bool manual,
        bool unconnected,
        bool customerManual = false)
    {
        var visual = GetDocumentVisual(document.MatchStatus);
        var customerDirect = !unconnected && !manual && !customerManual && IsLegacyGraphCustomerDirectDocument(document);
        var stroke = unconnected ? "#DC2626" : manual || customerManual ? "#2563EB" : customerDirect ? "#D97706" : visual.Stroke;
        var fill = unconnected ? "#FEF2F2" : manual || customerManual ? "#EFF6FF" : customerDirect ? "#FFF7ED" : visual.Fill;
        var statusLabel = unconnected
            ? "未接続"
            : manual
                ? "仮紐付け済み"
                : customerManual
                    ? "顧客へ仮紐付け"
                : customerDirect
                    ? "車両未確定"
                    : visual.Dashed ? "要確認・未確定" : "自動確定";
        var subtitle =
            $"判定: {(unconnected ? "未接続" : manual ? "手動仮紐付け" : customerManual ? "顧客へ手動仮紐付け" : customerDirect ? "車両未確定" : document.MatchStatus)}\n" +
            $"{Fallback(document.CustomerName)} / {Fallback(document.VehicleName)}\n" +
            $"日付: {Fallback(document.DocumentDate)} / 合計: {Fallback(document.TotalAmount)}\n" +
            $"出典: {document.SourceLocation}";
        var block = CreateLegacyGraphBlock(
            $"{document.Kind}: {Fallback(document.DocumentNumber)}",
            subtitle,
            stroke,
            fill,
            unconnected || manual || customerDirect || visual.Dashed,
            width,
            height,
            statusLabel);
        if (block.Children.OfType<StackPanel>().FirstOrDefault() is { } content)
        {
            // 左右のノード・切断ボタンと本文が重ならないように余白を確保します。
            content.Margin = new Thickness(34, 8, 12, 8);
        }
        var documentNode = new Border
        {
            Width = 19,
            Height = 19,
            Margin = new Thickness(-9, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ToBrush(fill),
            BorderBrush = ToBrush(stroke),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand,
            ToolTip = "このノードを車両ノードへドラッグして紐付け",
            Tag = new LegacyGraphDocumentNodeMarker(),
            DataContext = document,
            AllowDrop = true,
        };
        documentNode.Child = new TextBlock
        {
            Text = "●",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = ToBrush(stroke),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        documentNode.PreviewMouseLeftButtonDown += LegacyGraphDocumentHandle_MouseLeftButtonDown;
        documentNode.PreviewMouseMove += LegacyGraphDocumentHandle_MouseMove;
        documentNode.PreviewMouseLeftButtonUp += LegacyGraphDocumentHandle_MouseLeftButtonUp;
        documentNode.DragEnter += LegacyGraphDocumentNode_DragEnter;
        documentNode.DragOver += LegacyGraphDocumentNode_DragOver;
        documentNode.DragLeave += LegacyGraphDocumentNode_DragLeave;
        documentNode.Drop += LegacyGraphDocumentNode_Drop;
        block.Children.Add(documentNode);

        var disconnectButton = new Button
        {
            Width = 18,
            Height = 18,
            MinWidth = 0,
            MinHeight = 0,
            Margin = new Thickness(13, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            Content = "×",
            FontSize = 12,
            Foreground = ToBrush(stroke),
            Background = Brushes.White,
            BorderBrush = ToBrush(stroke),
            BorderThickness = new Thickness(1),
            ToolTip = "書類の接続を切り、未接続状態にする",
            Tag = new LegacyGraphDocumentDisconnectMarker(),
            DataContext = document,
        };
        disconnectButton.Click += LegacyGraphDocumentDisconnectButton_Click;
        block.Children.Add(disconnectButton);
        if (!string.IsNullOrWhiteSpace(document.Warning))
        {
            block.ToolTip = document.Warning;
        }

        return block;
    }

    private Grid CreateLegacyGraphBlock(
        string title,
        string subtitle,
        string stroke,
        string fill,
        bool dashed,
        double width,
        double height,
        string? badgeText = null)
    {
        var block = new Grid
        {
            Width = width,
            MinHeight = height,
            Height = double.NaN,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.SizeAll,
            ClipToBounds = false,
        };
        var outline = new Rectangle
        {
            Stroke = ToBrush(stroke),
            Fill = ToBrush(fill),
            StrokeThickness = 2,
            RadiusX = 7,
            RadiusY = 7,
        };
        if (dashed)
        {
            outline.StrokeDashArray = new DoubleCollection { 5, 3 };
        }

        var content = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        if (!string.IsNullOrWhiteSpace(badgeText))
        {
            content.Children.Add(new Border
            {
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = ToBrush(stroke),
                CornerRadius = new CornerRadius(3),
                Child = new TextBlock
                {
                    Text = badgeText,
                    FontSize = 10,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                },
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = ToBrush("#10233F"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            Text = subtitle,
            FontSize = 12,
            Foreground = ToBrush("#52647A"),
            TextWrapping = TextWrapping.Wrap,
        });
        block.Children.Add(outline);
        block.Children.Add(content);
        block.PreviewMouseLeftButtonDown += LegacyGraphBlock_MouseLeftButtonDown;
        block.PreviewMouseMove += LegacyGraphBlock_MouseMove;
        block.PreviewMouseLeftButtonUp += LegacyGraphBlock_MouseLeftButtonUp;
        return block;
    }

    private void AttachLegacyGraphVehicleDropTarget(
        Grid vehicleBlock,
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        vehicleBlock.AllowDrop = true;
        vehicleBlock.DragEnter += (_, e) => HandleLegacyGraphVehicleDragOver(vehicleBlock, vehicle, e);
        vehicleBlock.DragOver += (_, e) => HandleLegacyGraphVehicleDragOver(vehicleBlock, vehicle, e);
        vehicleBlock.DragLeave += (_, _) =>
        {
            if (ReferenceEquals(legacyGraphDropHighlightTarget, vehicleBlock))
            {
                ClearLegacyGraphDropHighlight();
            }

            legacyGraphNativeDocumentDropTargetValid = false;
        };
        vehicleBlock.Drop += (_, e) =>
        {
            var payload = GetLegacyGraphDocumentDragPayload(e.Data);
            if (payload is null)
            {
                return;
            }

            ApplyLegacyGraphManualLink(payload.Document, vehicle.VehicleId);
            e.Effects = DragDropEffects.Link;
            ClearLegacyGraphDropHighlight();
            e.Handled = true;
        };
    }

    private void AttachLegacyGraphCustomerDropTarget(
        Grid customerBlock,
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        customerBlock.AllowDrop = true;
        customerBlock.DragEnter += (_, e) => HandleLegacyGraphCustomerDragOver(customerBlock, e);
        customerBlock.DragOver += (_, e) => HandleLegacyGraphCustomerDragOver(customerBlock, e);
        customerBlock.DragLeave += (_, _) =>
        {
            if (ReferenceEquals(legacyGraphDropHighlightTarget, customerBlock))
            {
                ClearLegacyGraphDropHighlight();
            }

            legacyGraphNativeDocumentDropTargetValid = false;
        };
        customerBlock.Drop += (_, e) =>
        {
            if (GetLegacyGraphUnresolvedVehicleDragPayload(e.Data) is { } vehiclePayload &&
                FindLegacyGraphVehicleById(vehiclePayload.VehicleId) is { } unresolvedVehicle)
            {
                ApplyLegacyGraphManualVehicleCustomerLink(unresolvedVehicle, customer);
                e.Effects = DragDropEffects.Link;
                ClearLegacyGraphDropHighlight();
                e.Handled = true;
                return;
            }

            if (GetLegacyGraphDocumentDragPayload(e.Data) is not { } payload)
            {
                return;
            }

            ApplyLegacyGraphManualCustomerLink(payload.Document, customer);
            e.Effects = DragDropEffects.Link;
            ClearLegacyGraphDropHighlight();
            e.Handled = true;
        };
    }

    private void HandleLegacyGraphCustomerDragOver(Grid customerBlock, DragEventArgs e)
    {
        if (GetLegacyGraphUnresolvedVehicleDragPayload(e.Data) is { } vehiclePayload &&
            FindLegacyGraphVehicleById(vehiclePayload.VehicleId) is { } vehicle &&
            (!vehicle.HasCustomer || legacyGraphTrayVehicleIds.Contains(vehicle.VehicleId)))
        {
            SetLegacyGraphDropHighlight(customerBlock);
            legacyGraphNativeDocumentDropTargetValid = true;
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
            return;
        }

        if (GetLegacyGraphDocumentDragPayload(e.Data) is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        SetLegacyGraphDropHighlight(customerBlock);
        legacyGraphNativeDocumentDropTargetValid = true;
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void AttachLegacyGraphVehicleNode(
        Grid vehicleBlock,
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        var node = new Border
        {
            Width = 19,
            Height = 19,
            Margin = new Thickness(0, 0, -9, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = ToBrush("#ECFDF5"),
            BorderBrush = ToBrush("#059669"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand,
            ToolTip = "書類ノードをここへドロップして車両へ紐付け",
            Tag = new LegacyGraphVehicleNodeMarker(),
            DataContext = vehicle,
            AllowDrop = true,
        };
        node.Child = new TextBlock
        {
            Text = "●",
            FontSize = 10,
            Foreground = ToBrush("#059669"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        node.PreviewMouseLeftButtonDown += LegacyGraphVehicleNode_MouseLeftButtonDown;
        node.PreviewMouseMove += LegacyGraphVehicleNode_MouseMove;
        node.PreviewMouseLeftButtonUp += LegacyGraphVehicleNode_MouseLeftButtonUp;
        node.DragEnter += (_, e) => HandleLegacyGraphVehicleDragOver(vehicleBlock, vehicle, e);
        node.DragOver += (_, e) => HandleLegacyGraphVehicleDragOver(vehicleBlock, vehicle, e);
        node.DragLeave += (_, _) =>
        {
            if (ReferenceEquals(legacyGraphDropHighlightTarget, vehicleBlock))
            {
                ClearLegacyGraphDropHighlight();
            }

            legacyGraphNativeDocumentDropTargetValid = false;
        };
        node.Drop += (_, e) =>
        {
            var payload = GetLegacyGraphDocumentDragPayload(e.Data);
            if (payload is null)
            {
                return;
            }

            ApplyLegacyGraphManualLink(payload.Document, vehicle.VehicleId);
            e.Effects = DragDropEffects.Link;
            ClearLegacyGraphDropHighlight();
            e.Handled = true;
        };
        vehicleBlock.Children.Add(node);
    }

    private void LegacyGraphDocumentDisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        if (sender is not FrameworkElement element ||
            element.DataContext is not AbacusLegacyExportCandidateGraphDocument document)
        {
            return;
        }

        var key = GetLegacyDocumentKey(document);
        InvalidateLegacyGraphApprovalForDocument(document);
        legacyGraphManualDocumentLinks.Remove(key);
        legacyGraphManualDocumentCustomerLinks.Remove(key);
        legacyGraphDocumentLinkMethods.Remove(key);
        legacyGraphDocumentLinkReasons.Remove(key);
        legacyGraphTrayDocumentKeys.Remove(key);
        legacyGraphUnconnectedDocumentKeys.Add(key);
        RefreshLegacyGraphAfterManualLink(document, null);
        e.Handled = true;
    }

    private void HandleLegacyGraphVehicleDragOver(
        Grid vehicleBlock,
        AbacusLegacyExportCandidateGraphVehicle vehicle,
        DragEventArgs e)
    {
        if (GetLegacyGraphDocumentDragPayload(e.Data) is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        SetLegacyGraphDropHighlight(vehicleBlock);
        legacyGraphNativeDocumentDropTargetValid = true;
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphDocumentNode_DragEnter(object sender, DragEventArgs e) =>
        LegacyGraphDocumentNode_DragOver(sender, e);

    private void LegacyGraphDocumentNode_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement node ||
            node.DataContext is not AbacusLegacyExportCandidateGraphDocument document)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (GetLegacyGraphVehicleNodeDragPayload(e.Data) is { } vehiclePayload)
        {
            if (FindVisualAncestor<Grid>(node) is { } documentBlock)
            {
                SetLegacyGraphDropHighlight(documentBlock);
            }
            UpdateLegacyGraphActiveConnectionLine(e.GetPosition(LegacyGraphCanvas));
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
    }

    private void LegacyGraphDocumentNode_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement node &&
            FindVisualAncestor<Grid>(node) is { } documentBlock &&
            legacyGraphDropHighlightTarget == documentBlock)
        {
            ClearLegacyGraphDropHighlight();
        }
    }

    private void LegacyGraphDocumentNode_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement node ||
            node.DataContext is not AbacusLegacyExportCandidateGraphDocument document ||
            GetLegacyGraphVehicleNodeDragPayload(e.Data) is not { } vehiclePayload)
        {
            return;
        }

        ApplyLegacyGraphManualLink(document, vehiclePayload.VehicleId);
        ClearLegacyGraphDropHighlight();
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
    }

    private void LegacyGraphVehicleNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement node ||
            node.DataContext is not AbacusLegacyExportCandidateGraphVehicle vehicle)
        {
            return;
        }

        legacyGraphNodeDragSource?.ReleaseMouseCapture();
        StopLegacyGraphNodeAutoScroll();
        EndLegacyGraphConnectionPreview();
        ClearLegacyGraphDropHighlight();
        legacyGraphHandleDragDocument = null;
        legacyGraphNodeDragVehicle = vehicle;
        legacyGraphNodeDragSource = node;
        legacyGraphNodeDragStarted = false;
        legacyGraphNodeDragStartPoint = e.GetPosition(LegacyGraphCanvas);
        node.CaptureMouse();
        e.Handled = true;
    }

    private void LegacyGraphVehicleNode_MouseMove(object sender, MouseEventArgs e)
    {
        if (legacyGraphNodeDragVehicle is null ||
            legacyGraphNodeDragSource is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(LegacyGraphCanvas);
        if (!legacyGraphNodeDragStarted &&
            Math.Abs(currentPoint.X - legacyGraphNodeDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - legacyGraphNodeDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!legacyGraphNodeDragStarted)
        {
            legacyGraphNodeDragStarted = true;
            BeginLegacyGraphConnectionPreview(legacyGraphNodeDragSource);
            StartLegacyGraphNodeAutoScroll();
        }

        UpdateLegacyGraphNodeDragFrame();

        e.Handled = true;
    }

    private void LegacyGraphVehicleNode_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CompleteLegacyGraphNodeDrag(e.GetPosition(LegacyGraphCanvas));
        e.Handled = true;
    }

    private static LegacyGraphDocumentDragPayload? GetLegacyGraphDocumentDragPayload(IDataObject data) =>
        data.GetData(typeof(LegacyGraphDocumentDragPayload)) as LegacyGraphDocumentDragPayload;

    private static LegacyGraphVehicleNodeDragPayload? GetLegacyGraphVehicleNodeDragPayload(IDataObject data) =>
        data.GetData(typeof(LegacyGraphVehicleNodeDragPayload)) as LegacyGraphVehicleNodeDragPayload;

    private void SetLegacyGraphDropHighlight(Grid target)
    {
        if (ReferenceEquals(legacyGraphDropHighlightTarget, target))
        {
            return;
        }

        ClearLegacyGraphDropHighlight();
        legacyGraphDropHighlightTarget = target;
        target.Opacity = 0.82;
        target.RenderTransformOrigin = new Point(0.5, 0.5);
        target.RenderTransform = new ScaleTransform(1.02, 1.02);
    }

    private void ClearLegacyGraphDropHighlight()
    {
        if (legacyGraphDropHighlightTarget is null)
        {
            return;
        }

        legacyGraphDropHighlightTarget.Opacity = 1;
        legacyGraphDropHighlightTarget.RenderTransform = null;
        legacyGraphDropHighlightTarget = null;
    }

    private void LegacyGraphDocumentHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement handle || handle.DataContext is not AbacusLegacyExportCandidateGraphDocument document)
        {
            return;
        }

        legacyGraphNodeDragSource?.ReleaseMouseCapture();
        StopLegacyGraphNodeAutoScroll();
        EndLegacyGraphConnectionPreview();
        ClearLegacyGraphDropHighlight();
        legacyGraphNodeDragVehicle = null;
        legacyGraphHandleDragDocument = document;
        legacyGraphNodeDragSource = handle;
        legacyGraphNodeDragStarted = false;
        legacyGraphHandleDragStartPoint = e.GetPosition(LegacyGraphCanvas);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void LegacyGraphDocumentHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (legacyGraphHandleDragDocument is null ||
            legacyGraphNodeDragSource is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(LegacyGraphCanvas);
        if (!legacyGraphNodeDragStarted &&
            Math.Abs(currentPoint.X - legacyGraphHandleDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - legacyGraphHandleDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (!legacyGraphNodeDragStarted)
        {
            legacyGraphNodeDragStarted = true;
            BeginLegacyGraphConnectionPreview(legacyGraphNodeDragSource);
            StartLegacyGraphNodeAutoScroll();
        }

        UpdateLegacyGraphNodeDragFrame();

        e.Handled = true;
    }

    private void LegacyGraphDocumentHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CompleteLegacyGraphNodeDrag(e.GetPosition(LegacyGraphCanvas));
        e.Handled = true;
    }

    private void UpdateLegacyGraphNodeDragTarget(Point point)
    {
        ClearLegacyGraphDropHighlight();

        if (legacyGraphHandleDragDocument is not null)
        {
            if (FindLegacyGraphVehicleNodeAt(point) is { } vehicleNode &&
                FindVisualAncestor<Grid>(vehicleNode) is { } vehicleBlock)
            {
                SetLegacyGraphDropHighlight(vehicleBlock);
            }
        }
        else if (legacyGraphNodeDragVehicle is not null)
        {
            if (FindLegacyGraphDocumentNodeAt(point) is { } documentNode &&
                FindVisualAncestor<Grid>(documentNode) is { } documentBlock)
            {
                SetLegacyGraphDropHighlight(documentBlock);
            }
        }
    }

    private void StartLegacyGraphNodeAutoScroll()
    {
        legacyGraphNodeAutoScrollTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        legacyGraphNodeAutoScrollTimer.Tick -= LegacyGraphNodeAutoScrollTimer_Tick;
        legacyGraphNodeAutoScrollTimer.Tick += LegacyGraphNodeAutoScrollTimer_Tick;
        legacyGraphNodeAutoScrollTimer.Start();
    }

    private void StopLegacyGraphNodeAutoScroll()
    {
        legacyGraphNodeAutoScrollTimer?.Stop();
    }

    private void LegacyGraphNodeAutoScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!legacyGraphNodeDragStarted || legacyGraphNodeDragSource is null)
        {
            StopLegacyGraphNodeAutoScroll();
            return;
        }

        UpdateLegacyGraphNodeDragFrame();
    }

    private void UpdateLegacyGraphNodeDragFrame()
    {
        if (!legacyGraphNodeDragStarted)
        {
            return;
        }

        var pointer = Mouse.GetPosition(LegacyGraphScrollViewer);
        var viewportHeight = LegacyGraphScrollViewer.ViewportHeight > 0
            ? LegacyGraphScrollViewer.ViewportHeight
            : LegacyGraphScrollViewer.ActualHeight;
        var viewportWidth = LegacyGraphScrollViewer.ViewportWidth > 0
            ? LegacyGraphScrollViewer.ViewportWidth
            : LegacyGraphScrollViewer.ActualWidth;
        var nearCanvas = pointer.X >= 0 && pointer.X <= viewportWidth;
        var edge = 64d;
        // 40msごとの移動量を小さくし、端に置いたままでもゆっくり追従させます。
        var step = 0.1d;
        var direction = !nearCanvas
            ? 0
            : pointer.Y < edge
                ? -1
                : pointer.Y > viewportHeight - edge
                    ? 1
                    : 0;
        var delta = direction * step;
        if (delta != 0)
        {
            LegacyGraphScrollViewer.UpdateLayout();
            var effectiveScrollableHeight = Math.Max(
                LegacyGraphScrollViewer.ScrollableHeight,
                Math.Max(0, LegacyGraphBoardGrid.ActualHeight - viewportHeight));
            var nextOffset = Math.Clamp(
                LegacyGraphScrollViewer.VerticalOffset + delta,
                0,
                effectiveScrollableHeight);
            if (Math.Abs(nextOffset - LegacyGraphScrollViewer.VerticalOffset) > double.Epsilon)
            {
                LegacyGraphScrollViewer.ScrollToVerticalOffset(nextOffset);
                LegacyGraphScrollViewer.UpdateLayout();
                LegacyGraphCanvas.UpdateLayout();
            }
        }

        var currentPoint = Mouse.GetPosition(LegacyGraphCanvas);
        UpdateLegacyGraphActiveConnectionLine(currentPoint);
        UpdateLegacyGraphNodeDragTarget(currentPoint);
    }

    private FrameworkElement? FindLegacyGraphVehicleNodeAt(Point point) =>
        FindLegacyGraphNodeAt(point, typeof(LegacyGraphVehicleNodeMarker));

    private FrameworkElement? FindLegacyGraphDocumentNodeAt(Point point) =>
        FindLegacyGraphNodeAt(point, typeof(LegacyGraphDocumentNodeMarker));

    private FrameworkElement? FindLegacyGraphNodeAt(Point point, Type markerType)
    {
        var hit = VisualTreeHelper.HitTest(LegacyGraphCanvas, point)?.VisualHit;
        while (hit is not null)
        {
            if (hit is FrameworkElement element &&
                element.Tag?.GetType() == markerType)
            {
                return element;
            }

            hit = GetLegacyGraphParent(hit);
        }

        return null;
    }

    private void CompleteLegacyGraphNodeDrag(Point point)
    {
        var document = legacyGraphHandleDragDocument;
        var vehicle = legacyGraphNodeDragVehicle;
        var wasDragging = legacyGraphNodeDragStarted;

        StopLegacyGraphNodeAutoScroll();
        legacyGraphNodeDragSource?.ReleaseMouseCapture();
        legacyGraphNodeDragSource = null;
        legacyGraphHandleDragDocument = null;
        legacyGraphNodeDragVehicle = null;
        legacyGraphNodeDragStarted = false;
        EndLegacyGraphConnectionPreview();
        ClearLegacyGraphDropHighlight();

        if (!wasDragging)
        {
            return;
        }

        if (document is not null &&
            FindLegacyGraphVehicleNodeAt(point)?.DataContext is AbacusLegacyExportCandidateGraphVehicle targetVehicle)
        {
            ApplyLegacyGraphManualLink(document, targetVehicle.VehicleId);
            return;
        }

        if (vehicle is not null &&
            FindLegacyGraphDocumentNodeAt(point)?.DataContext is AbacusLegacyExportCandidateGraphDocument targetDocument)
        {
            ApplyLegacyGraphManualLink(targetDocument, vehicle.VehicleId);
        }
    }

    private void BeginLegacyGraphConnectionPreview(FrameworkElement? source)
    {
        EndLegacyGraphConnectionPreview();
        if (source is null)
        {
            return;
        }

        LegacyGraphCanvas.UpdateLayout();
        var sourcePoint = GetLegacyGraphNodeCanvasPoint(source);
        legacyGraphActiveConnectionSource = source;
        source.GiveFeedback += LegacyGraphActiveConnection_GiveFeedback;
        legacyGraphActiveConnectionLine = new Line
        {
            Stroke = ToBrush("#2563EB"),
            StrokeThickness = 3,
            StrokeDashArray = new DoubleCollection { 6, 4 },
            Opacity = 0.9,
            IsHitTestVisible = false,
        };
        legacyGraphActiveConnectionLine.X1 = sourcePoint.X;
        legacyGraphActiveConnectionLine.Y1 = sourcePoint.Y;
        legacyGraphActiveConnectionLine.X2 = sourcePoint.X;
        legacyGraphActiveConnectionLine.Y2 = sourcePoint.Y;
        LegacyGraphEdgesCanvas.Children.Add(legacyGraphActiveConnectionLine);
        Panel.SetZIndex(legacyGraphActiveConnectionLine, 50);
    }

    private void UpdateLegacyGraphActiveConnectionLine(Point targetPoint)
    {
        if (legacyGraphActiveConnectionLine is null)
        {
            return;
        }

        legacyGraphActiveConnectionLine.X2 = targetPoint.X;
        legacyGraphActiveConnectionLine.Y2 = targetPoint.Y;
    }

    private Point GetLegacyGraphNodeCanvasPoint(FrameworkElement node)
    {
        if (FindVisualAncestor<Grid>(node) is not { } block)
        {
            return node.TranslatePoint(
                new Point(node.ActualWidth / 2, node.ActualHeight / 2),
                LegacyGraphCanvas);
        }

        var left = Canvas.GetLeft(block);
        var top = Canvas.GetTop(block);
        if (double.IsNaN(left))
        {
            left = 0;
        }

        if (double.IsNaN(top))
        {
            top = 0;
        }

        var width = GetLegacyGraphElementWidth(block);
        var height = GetLegacyGraphElementHeight(block);
        return node.Tag is LegacyGraphVehicleNodeMarker
            ? new Point(left + width, top + height / 2)
            : new Point(left, top + height / 2);
    }

    private void EndLegacyGraphConnectionPreview()
    {
        if (legacyGraphActiveConnectionLine is not null)
        {
            LegacyGraphEdgesCanvas.Children.Remove(legacyGraphActiveConnectionLine);
        }

        if (legacyGraphActiveConnectionSource is not null)
        {
            legacyGraphActiveConnectionSource.GiveFeedback -= LegacyGraphActiveConnection_GiveFeedback;
        }

        legacyGraphActiveConnectionLine = null;
        legacyGraphActiveConnectionSource = null;
    }

    private void LegacyGraphActiveConnection_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (legacyGraphActiveConnectionLine is not null)
        {
            UpdateLegacyGraphActiveConnectionLine(Mouse.GetPosition(LegacyGraphCanvas));
        }

        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void LegacyGraphCanvas_DragOver(object sender, DragEventArgs e)
    {
        if (legacyGraphActiveConnectionLine is not null)
        {
            UpdateLegacyGraphActiveConnectionLine(e.GetPosition(LegacyGraphCanvas));
        }

        if (GetLegacyGraphUnresolvedVehicleDragPayload(e.Data) is { } unresolvedVehiclePayload &&
            FindLegacyGraphVehicleById(unresolvedVehiclePayload.VehicleId) is { HasCustomer: false })
        {
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
            return;
        }

        if (GetLegacyGraphVehicleNodeDragPayload(e.Data) is not null ||
            GetLegacyGraphDocumentDragPayload(e.Data) is not null)
        {
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
        }
    }

    private void LegacyGraphCanvas_DragLeave(object sender, DragEventArgs e)
    {
        // 子要素への移動時に仮接続線を消さないため、ここでは状態を保持します。
    }

    private void LegacyGraphCanvas_Drop(object sender, DragEventArgs e)
    {
        ClearLegacyGraphDropHighlight();
        if (GetLegacyGraphUnresolvedVehicleDragPayload(e.Data) is { } unresolvedVehiclePayload &&
            FindLegacyGraphVehicleById(unresolvedVehiclePayload.VehicleId) is { } unresolvedVehicle &&
            !unresolvedVehicle.HasCustomer)
        {
            var customer = GetLegacyGraphListSelectedDisplayCustomer();
            if (customer is not null)
            {
                ApplyLegacyGraphManualVehicleCustomerLink(unresolvedVehicle, customer);
                e.Effects = DragDropEffects.Link;
                e.Handled = true;
                return;
            }

            LegacyGraphStatusText.Text = "未確定車両の接続先顧客を先に選択してください。";
            LegacyGraphStatusText.Foreground = ToBrush("#805B10");
            e.Handled = true;
            return;
        }

        if (GetLegacyGraphDocumentDragPayload(e.Data) is { } documentPayload)
        {
            var customer = GetLegacyGraphListSelectedDisplayCustomer();
            if (customer is not null)
            {
                ApplyLegacyGraphManualCustomerLink(documentPayload.Document, customer);
                e.Effects = DragDropEffects.Link;
                e.Handled = true;
                return;
            }
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void AddGraphElement(UIElement element, double left, double vehicleY)
    {
        LegacyGraphCanvas.Children.Add(element);
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, vehicleY);
    }

    private void AddGraphEdge(UIElement source, UIElement target, string stroke, bool dashed)
    {
        var line = new Line
        {
            Stroke = ToBrush(stroke),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        if (dashed)
        {
            line.StrokeDashArray = new DoubleCollection { 5, 3 };
        }

        legacyGraphEdges.Add(new LegacyGraphEdge(source, target, line));
        LegacyGraphEdgesCanvas.Children.Add(line);
    }

    private void UpdateLegacyGraphEdges()
    {
        foreach (var edge in legacyGraphEdges)
        {
            var sourceLeft = Canvas.GetLeft(edge.Source);
            var sourceTop = Canvas.GetTop(edge.Source);
            var targetLeft = Canvas.GetLeft(edge.Target);
            var targetTop = Canvas.GetTop(edge.Target);
            var sourceWidth = edge.Source is FrameworkElement sourceElement
                ? GetLegacyGraphElementWidth(sourceElement)
                : 0;
            var sourceHeight = edge.Source is FrameworkElement sourceHeightElement
                ? GetLegacyGraphElementHeight(sourceHeightElement)
                : 0;
            var targetHeight = edge.Target is FrameworkElement targetElement
                ? GetLegacyGraphElementHeight(targetElement)
                : 0;
            edge.Line.X1 = sourceLeft + sourceWidth;
            edge.Line.Y1 = sourceTop + sourceHeight / 2;
            edge.Line.X2 = targetLeft;
            edge.Line.Y2 = targetTop + targetHeight / 2;
        }
    }

    private static double GetLegacyGraphElementWidth(FrameworkElement element) =>
        element.ActualWidth > 0 && !double.IsNaN(element.ActualWidth)
            ? element.ActualWidth
            : double.IsNaN(element.Width) ? 0 : element.Width;

    private static double GetLegacyGraphElementHeight(FrameworkElement element) =>
        element.ActualHeight > 0 && !double.IsNaN(element.ActualHeight)
            ? element.ActualHeight
            : double.IsNaN(element.Height) ? element.MinHeight : element.Height;

    private void LegacyGraphBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        if (FindLegacyGraphNodeInteraction(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        legacyGraphDraggingElement = element;
        legacyGraphBlockDragStarted = false;
        legacyGraphDocumentCardDragDocument = element is FrameworkElement frameworkElement &&
                                               frameworkElement.Tag is AbacusLegacyExportCandidateGraphDocument document
            ? document
            : null;
        legacyGraphVehicleCardDragVehicle = element is FrameworkElement vehicleElement &&
                                            vehicleElement.Tag is AbacusLegacyExportCandidateGraphVehicle vehicle
            ? vehicle
            : null;
        legacyGraphBlockDragOriginalCursor = element is FrameworkElement cursorElement
            ? cursorElement.Cursor
            : null;
        Mouse.OverrideCursor = null;
        UpdateLegacyGraphInspector(element is FrameworkElement selectedElement ? selectedElement.Tag : null);
        var position = e.GetPosition(LegacyGraphCanvas);
        legacyGraphBlockDragStartPoint = position;
        var elementLeft = Canvas.GetLeft(element);
        var elementTop = Canvas.GetTop(element);
        if (double.IsNaN(elementLeft))
        {
            elementLeft = 0;
        }

        if (double.IsNaN(elementTop))
        {
            elementTop = 0;
        }

        var pageMousePoint = GetLegacyGraphPagePointFromGraph(position);
        var pageElementPoint = GetLegacyGraphPagePointFromGraph(new Point(elementLeft, elementTop));
        legacyGraphDragOffset = new Point(
            pageMousePoint.X - pageElementPoint.X,
            pageMousePoint.Y - pageElementPoint.Y);
        element.CaptureMouse();
        e.Handled = true;
    }

    private void LegacyGraphBlock_MouseMove(object sender, MouseEventArgs e)
    {
        if (legacyGraphDraggingElement is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(LegacyGraphCanvas);
        if (!legacyGraphBlockDragStarted &&
            (Math.Abs(position.X - legacyGraphBlockDragStartPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(position.Y - legacyGraphBlockDragStartPoint.Y) >= SystemParameters.MinimumVerticalDragDistance))
        {
            legacyGraphBlockDragStarted = true;
            BeginLegacyGraphBlockVisualDrag(legacyGraphDraggingElement as FrameworkElement);
        }

        if (legacyGraphBlockDragStarted)
        {
            if (legacyGraphDocumentCardDragDocument is not null || legacyGraphVehicleCardDragVehicle is not null)
            {
                // 書類ブロックを掴んだ直後は通常の移動カーソルを維持し、
                // 有効なドロップ先に入った時だけ手カーソルへ切り替えます。
                SetLegacyGraphDocumentDragCursor(Cursors.SizeAll);
            }
            UpdateLegacyGraphDragPreviewPosition(e.GetPosition(this));
            UpdateLegacyGraphBlockDropTarget(e.GetPosition(this));
            e.Handled = true;
        }
    }

    private void LegacyGraphBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (legacyGraphDraggingElement is null)
        {
            return;
        }

        var document = legacyGraphDocumentCardDragDocument;
        var vehicle = legacyGraphVehicleCardDragVehicle;
        var wasDragging = legacyGraphBlockDragStarted;
        var windowPoint = e.GetPosition(this);
        var droppedOnTrashOverlay = wasDragging && IsLegacyGraphTrashOverlayAt(windowPoint);
        RestoreLegacyGraphBlockDragCursor();
        legacyGraphDraggingElement.ReleaseMouseCapture();
        legacyGraphDraggingElement = null;
        legacyGraphDocumentCardDragDocument = null;
        legacyGraphVehicleCardDragVehicle = null;
        legacyGraphBlockDragStarted = false;
        ClearLegacyGraphBlockVisualDrag(hideTrashOverlay: false);
        ClearLegacyGraphDropHighlight();
        ClearLegacyGraphTrayDropHighlight();
        ClearLegacyGraphTrashDropHighlight();

        if (wasDragging && document is not null)
        {
            HandleLegacyGraphDocumentBlockDrop(document, windowPoint, droppedOnTrashOverlay);
        }
        else if (wasDragging && vehicle is not null)
        {
            HandleLegacyGraphVehicleBlockDrop(vehicle, windowPoint, droppedOnTrashOverlay);
        }

        HideLegacyGraphTrashOverlay();

        e.Handled = true;
    }

    private void BeginLegacyGraphBlockVisualDrag(FrameworkElement? source)
    {
        if (source is null || legacyGraphBlockDragPreview is not null)
        {
            return;
        }

        var width = Math.Max(120, GetLegacyGraphElementWidth(source));
        var height = Math.Max(58, GetLegacyGraphElementHeight(source));
        legacyGraphBlockDragPreviewSource = source;
        source.Opacity = 0.38;
        if (source.Tag is AbacusLegacyExportCandidateGraphDocument or
            AbacusLegacyExportCandidateGraphVehicle)
        {
            ShowLegacyGraphTrashOverlay();
        }
        legacyGraphBlockDragPreview = new Border
        {
            Width = width,
            Height = height,
            Background = new VisualBrush(source) { Opacity = 0.9 },
            BorderBrush = ToBrush("#2563EB"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(7),
            Opacity = 0.9,
            IsHitTestVisible = false,
        };
        LegacyGraphPageDragPreviewCanvas.UpdateLayout();
        LegacyGraphPageDragPreviewCanvas.Children.Add(legacyGraphBlockDragPreview);
        Panel.SetZIndex(legacyGraphBlockDragPreview, 100);
    }

    private Point GetLegacyGraphPagePointFromGraph(Point graphPoint)
    {
        var screenPoint = LegacyGraphCanvas.PointToScreen(graphPoint);
        return LegacyGraphPageDragPreviewCanvas.PointFromScreen(screenPoint);
    }

    private Point GetLegacyGraphPagePointFromWindow(Point windowPoint)
    {
        var screenPoint = PointToScreen(windowPoint);
        return LegacyGraphPageDragPreviewCanvas.PointFromScreen(screenPoint);
    }

    private void UpdateLegacyGraphDragPreviewPosition(Point windowPoint)
    {
        if (legacyGraphBlockDragPreview is null)
        {
            return;
        }

        var pagePoint = GetLegacyGraphPagePointFromWindow(windowPoint);
        Canvas.SetLeft(legacyGraphBlockDragPreview, pagePoint.X - legacyGraphDragOffset.X);
        Canvas.SetTop(legacyGraphBlockDragPreview, pagePoint.Y - legacyGraphDragOffset.Y);
    }

    private void ClearLegacyGraphBlockVisualDrag(bool hideTrashOverlay = true)
    {
        RestoreLegacyGraphBlockDragCursor();
        if (legacyGraphBlockDragPreviewSource is not null)
        {
            legacyGraphBlockDragPreviewSource.Opacity = 1;
        }

        if (legacyGraphBlockDragPreview is not null)
        {
            LegacyGraphPageDragPreviewCanvas.Children.Remove(legacyGraphBlockDragPreview);
        }

        legacyGraphBlockDragPreview = null;
        legacyGraphBlockDragPreviewSource = null;
        if (hideTrashOverlay)
        {
            HideLegacyGraphTrashOverlay();
        }
    }

    private void SetLegacyGraphDocumentDragCursor(Cursor cursor)
    {
        Mouse.OverrideCursor = cursor;
        Cursor = cursor;
        if (legacyGraphDraggingElement is FrameworkElement element)
        {
            element.Cursor = cursor;
        }
    }

    private void RestoreLegacyGraphBlockDragCursor()
    {
        Mouse.OverrideCursor = null;
        Cursor = null;
        if (legacyGraphDraggingElement is FrameworkElement element)
        {
            element.Cursor = legacyGraphBlockDragOriginalCursor;
        }

        legacyGraphBlockDragOriginalCursor = null;
    }

    private void UpdateLegacyGraphBlockDropTarget(Point windowPoint)
    {
        ClearLegacyGraphDropHighlight();
        ClearLegacyGraphTrayDropHighlight();
        ClearLegacyGraphTrashDropHighlight();
        if (legacyGraphDocumentCardDragDocument is null && legacyGraphVehicleCardDragVehicle is null)
        {
            RestoreLegacyGraphBlockDragCursor();
            return;
        }

        if (IsLegacyGraphTrashOverlayAt(windowPoint))
        {
            SetLegacyGraphTrashOverlayHighlight(true);
            SetLegacyGraphDocumentDragCursor(Cursors.Hand);
            return;
        }

        if (legacyGraphVehicleCardDragVehicle is not null)
        {
            var trashVehicleList = FindLegacyGraphTrashVehicleListAt(windowPoint);
            if (trashVehicleList is not null)
            {
                legacyGraphTrashDropHighlightList = trashVehicleList;
                trashVehicleList.Opacity = 0.82;
                SetLegacyGraphDocumentDragCursor(Cursors.Hand);
                return;
            }

            var vehicleList = FindLegacyGraphUnresolvedVehicleListAt(windowPoint);
            if (vehicleList is not null)
            {
                legacyGraphTrayDropHighlightList = vehicleList;
                vehicleList.Opacity = 0.82;
                SetLegacyGraphDocumentDragCursor(Cursors.Hand);
                return;
            }

            SetLegacyGraphDocumentDragCursor(Cursors.SizeAll);
            return;
        }

        var trashDocumentList = FindLegacyGraphTrashDocumentListAt(windowPoint);
        if (trashDocumentList is not null)
        {
            legacyGraphTrashDropHighlightList = trashDocumentList;
            trashDocumentList.Opacity = 0.82;
            SetLegacyGraphDocumentDragCursor(Cursors.Hand);
            return;
        }

        var list = FindLegacyGraphUnresolvedDocumentListAt(windowPoint);
        if (list is not null)
        {
            legacyGraphTrayDropHighlightList = list;
            list.Opacity = 0.82;
            SetLegacyGraphDocumentDragCursor(Cursors.Hand);
            return;
        }

        var vehicleBlock = FindLegacyGraphVehicleBlockAt(windowPoint);
        if (vehicleBlock is not null)
        {
            SetLegacyGraphDropHighlight(vehicleBlock);
            SetLegacyGraphDocumentDragCursor(Cursors.Hand);
            return;
        }

        var customerBlock = FindLegacyGraphCustomerBlockAt(windowPoint);
        if (customerBlock is not null)
        {
            SetLegacyGraphDropHighlight(customerBlock);
            SetLegacyGraphDocumentDragCursor(Cursors.Hand);
            return;
        }

        SetLegacyGraphDocumentDragCursor(Cursors.SizeAll);
    }

    private ListBox? FindLegacyGraphUnresolvedDocumentListAt(Point windowPoint)
    {
        foreach (var list in new[] { LegacyGraphUnresolvedSalesList, LegacyGraphUnresolvedMaintenanceList })
        {
            if (IsLegacyGraphElementAtWindowPoint(list, windowPoint))
            {
                return list;
            }
        }

        return null;
    }

    private ListBox? FindLegacyGraphUnresolvedVehicleListAt(Point windowPoint) =>
        IsLegacyGraphElementAtWindowPoint(LegacyGraphUnresolvedVehicleList, windowPoint)
            ? LegacyGraphUnresolvedVehicleList
            : null;

    private ListBox? FindLegacyGraphTrashDocumentListAt(Point windowPoint) =>
        IsLegacyGraphElementAtWindowPoint(LegacyGraphTrashDocumentList, windowPoint)
            ? LegacyGraphTrashDocumentList
            : null;

    private ListBox? FindLegacyGraphTrashVehicleListAt(Point windowPoint) =>
        IsLegacyGraphElementAtWindowPoint(LegacyGraphTrashVehicleList, windowPoint)
            ? LegacyGraphTrashVehicleList
            : null;

    private Grid? FindLegacyGraphVehicleBlockAt(Point windowPoint)
    {
        foreach (var block in LegacyGraphCanvas.Children.OfType<Grid>())
        {
            if (block.Tag is AbacusLegacyExportCandidateGraphVehicle &&
                IsLegacyGraphElementAtWindowPoint(block, windowPoint))
            {
                return block;
            }
        }

        return null;
    }

    private Grid? FindLegacyGraphCustomerBlockAt(Point windowPoint)
    {
        foreach (var block in LegacyGraphCanvas.Children.OfType<Grid>())
        {
            if (block.Tag is AbacusLegacyExportCandidateGraphCustomer &&
                IsLegacyGraphElementAtWindowPoint(block, windowPoint))
            {
                return block;
            }
        }

        return null;
    }

    private bool IsLegacyGraphElementAtWindowPoint(FrameworkElement element, Point windowPoint)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var bounds = element.TransformToAncestor(this).TransformBounds(
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return bounds.Contains(windowPoint);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool IsLegacyGraphTrashOverlayAt(Point windowPoint) =>
        LegacyGraphTrashOverlay.Visibility == Visibility.Visible &&
        IsLegacyGraphElementAtWindowPoint(LegacyGraphTrashOverlay, windowPoint);

    private void HandleLegacyGraphDocumentBlockDrop(
        AbacusLegacyExportCandidateGraphDocument document,
        Point windowPoint,
        bool droppedOnTrashOverlay = false)
    {
        if (droppedOnTrashOverlay || IsLegacyGraphTrashOverlayAt(windowPoint) ||
            FindLegacyGraphTrashDocumentListAt(windowPoint) is not null)
        {
            MoveLegacyGraphDocumentToTrash(document);
            return;
        }

        var list = FindLegacyGraphUnresolvedDocumentListAt(windowPoint);
        if (list is not null)
        {
            MoveLegacyGraphDocumentToTray(document);
            LegacyGraphStatusText.Text =
                $"{document.Kind} {Fallback(document.DocumentNumber)} を未確定トレイへ戻しました。元データは変更していません。";
            LegacyGraphStatusText.Foreground = ToBrush("#D97706");
            return;
        }

        if (FindLegacyGraphVehicleBlockAt(windowPoint)?.Tag is AbacusLegacyExportCandidateGraphVehicle vehicle)
        {
            ApplyLegacyGraphManualLink(document, vehicle.VehicleId);
            return;
        }

        if (FindLegacyGraphCustomerBlockAt(windowPoint)?.Tag is AbacusLegacyExportCandidateGraphCustomer customer)
        {
            ApplyLegacyGraphManualCustomerLink(document, customer);
        }
    }

    private void LegacyGraphTrashButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMutateLegacyGraph)
        {
            return;
        }

        switch (legacyGraphSelectedItem)
        {
            case AbacusLegacyExportCandidateGraphCustomer customer:
                if (legacyGraphTrashCustomerIds.Contains(GetLegacyGraphSourceCustomer(customer).CustomerId))
                {
                    RestoreLegacyGraphCustomerFromTrash(customer);
                }
                else
                {
                    MoveLegacyGraphCustomerToTrash(customer);
                }
                break;
            case AbacusLegacyExportCandidateGraphVehicle vehicle:
                if (legacyGraphTrashVehicleIds.Contains(vehicle.VehicleId))
                {
                    RestoreLegacyGraphVehicleFromTrash(vehicle);
                }
                else
                {
                    MoveLegacyGraphVehicleToTrash(vehicle);
                }
                break;
            case AbacusLegacyExportCandidateGraphDocument document:
                if (legacyGraphTrashDocumentKeys.Contains(GetLegacyDocumentKey(document)))
                {
                    RestoreLegacyGraphDocumentFromTrash(document);
                }
                else
                {
                    MoveLegacyGraphDocumentToTrash(document);
                }
                break;
        }

        e.Handled = true;
    }

    private void HandleLegacyGraphVehicleBlockDrop(
        AbacusLegacyExportCandidateGraphVehicle vehicle,
        Point windowPoint,
        bool droppedOnTrashOverlay = false)
    {
        if (droppedOnTrashOverlay || IsLegacyGraphTrashOverlayAt(windowPoint) ||
            FindLegacyGraphTrashVehicleListAt(windowPoint) is not null)
        {
            MoveLegacyGraphVehicleToTrash(vehicle);
        }
        else if (FindLegacyGraphUnresolvedVehicleListAt(windowPoint) is not null)
        {
            MoveLegacyGraphVehicleToTray(vehicle);
        }
    }

    private bool IsLegacyGraphUnresolvedDocumentList(ListBox list) =>
        ReferenceEquals(list, LegacyGraphUnresolvedSalesList) ||
        ReferenceEquals(list, LegacyGraphUnresolvedMaintenanceList);

    private void ResetLegacyCandidateGraph(string status)
    {
        legacyExportCandidateGraphResult = null;
        legacyGraphRecommendationCandidates = [];
        legacyGraphRecommendationDecisions.Clear();
        legacyGraphRecommendationStates.Clear();
        legacyGraphMatchingCategory = LegacyMatchingCategoryKinds.Customer;
        legacyGraphMatchingRecommendationIndex = -1;
        legacyMatchingUnresolvedPageIndex = 0;
        legacyGraphMatchingChanges.Clear();
        legacyGraphCheckpointSaveScheduled = false;
        legacyGraphCheckpointSaveDeferred = false;
        legacyGraphManualDocumentLinks.Clear();
        legacyGraphManualVehicleCustomerLinks.Clear();
        legacyGraphManualDocumentCustomerLinks.Clear();
        legacyGraphDocumentLinkMethods.Clear();
        legacyGraphDocumentLinkReasons.Clear();
        legacyGraphUnconnectedDocumentKeys.Clear();
        legacyGraphTrayDocumentKeys.Clear();
        legacyGraphExcludedDocumentKeys.Clear();
        legacyGraphTrayVehicleIds.Clear();
        legacyGraphTrashCustomerIds.Clear();
        legacyGraphTrashVehicleIds.Clear();
        legacyGraphTrashDocumentKeys.Clear();
        legacyGraphImportConfirmed = false;
        legacyGraphFinalPackageBusy = false;
        legacyGraphFinalPackageHasError = false;
        legacyGraphFinalPackagePath = null;
        legacyGraphCustomerMergeDrafts.Clear();
        legacyGraphAppliedCustomerMergeKeys.Clear();
        legacyGraphVirtualCustomerMergeKeys.Clear();
        legacyGraphCustomerGroupExpanded.Clear();
        legacyGraphLogicalCustomerMergeGroupByCustomerId.Clear();
        legacyGraphMatchingManualCustomerCandidateTargets.Clear();
        legacyGraphCustomerApprovalStates.Clear();
        legacyGraphCustomerReviewStates.Clear();
        legacyGraphCustomerMergeGroups.Clear();
         legacyGraphCustomerMergeGroupByCustomerId.Clear();
         legacyGraphCustomerNameOverrides.Clear();
         legacyGraphSelectedItem = null;
         legacyGraphSelectedWorkGroupKey = null;
         legacyGraphUiMode = "graph";
        legacyGraphMatchingCustomerId = null;
        legacyGraphMatchingCustomerIndex = -1;
        legacyGraphSelectedRecommendation = null;
        legacyGraphPanning = false;
        legacyGraphInspectorPanning = false;
        legacyGraphInspectorPanningPage = false;
        legacyGraphTrayDragDocument = null;
        legacyGraphTrayDragVehicle = null;
        legacyGraphNativeDocumentDropTargetValid = false;
        legacyGraphHandleDragDocument = null;
        legacyGraphDocumentCardDragDocument = null;
        legacyGraphVehicleCardDragVehicle = null;
        legacyGraphNodeDragSource?.ReleaseMouseCapture();
        legacyGraphNodeDragSource = null;
        StopLegacyGraphNodeAutoScroll();
        legacyGraphNodeDragStarted = false;
        legacyGraphNodeDragVehicle = null;
        legacyGraphCustomerDragSource = null;
        ClearLegacyGraphCustomerDropHighlight();
        ClearLegacyGraphCustomerUngroupDropHighlight();
        ClearLegacyGraphDropHighlight();
        ClearLegacyGraphTrayDropHighlight();
        ClearLegacyGraphTrashDropHighlight();
        LegacyGraphCanvas.ReleaseMouseCapture();
        LegacyGraphScrollViewer.ReleaseMouseCapture();
        LegacyGraphInspectorScrollViewer.ReleaseMouseCapture();
        legacyGraphDraggingElement = null;
        ClearLegacyGraphBlockVisualDrag();
        EndLegacyGraphConnectionPreview();
        legacyGraphEdges.Clear();
        LegacyGraphCustomersList.ItemsSource = null;
        LegacyMatchingCustomerQueueList.ItemsSource = null;
        LegacyGraphUnresolvedVehicleList.ItemsSource = null;
        LegacyGraphUnresolvedSalesList.ItemsSource = null;
        LegacyGraphUnresolvedMaintenanceList.ItemsSource = null;
        LegacyGraphTrashCustomerList.ItemsSource = null;
        LegacyGraphTrashVehicleList.ItemsSource = null;
        LegacyGraphTrashDocumentList.ItemsSource = null;
        LegacyGraphSearchResultsList.ItemsSource = null;
        LegacyGraphSearchOpenButton.IsEnabled = false;
        LegacyGraphSearchOpenMatchingButton.IsEnabled = false;
        LegacyGraphSearchStatusText.Text = "候補パッケージを読み込むと検索できます。";
        LegacyGraphSearchTextBox.Clear();
        LegacyGraphCanvas.Children.Clear();
        LegacyGraphEdgesCanvas.Children.Clear();
        LegacyGraphPageDragPreviewCanvas.Children.Clear();
        LegacyGraphInspectorTitleText.Text = "顧客を選択してください";
        LegacyGraphInspectorStateText.Text = "";
        LegacyGraphInspectorStateBorder.Background = ToBrush("#F4F7FB");
        LegacyGraphInspectorStatusText.Text = "";
        LegacyGraphInspectorDetailsText.Text = "";
        LegacyGraphInspectorEvidenceText.Text = "";
        LegacyGraphInspectorSourcesPlaceholderText.Text = "顧客を選択すると、統合元候補を表示します。";
        LegacyGraphInspectorSourcesItemsControl.ItemsSource = null;
        LegacyGraphInspectorDifferencesPlaceholderText.Text = "顧客を選択すると、項目ごとの差分と採用根拠を表示します。";
        LegacyGraphInspectorDifferencesItemsControl.ItemsSource = null;
        LegacyGraphCustomerMergeStatusText.Text = "";
        LegacyGraphFinalizeImportButton.IsEnabled = false;
        LegacyGraphFinalizeImportButton.Content = "インポート内容を確定";
        LegacyGraphFinalizeImportStatusText.Text =
            "顧客統合と書類・ノード操作を完了すると、インポート内容を確定できます。";
        LegacyGraphApproveAllMergeButton.IsEnabled = false;
        LegacyGraphApproveAllMergeButton.Content = "統合候補・顧客を一括確定";
        LegacyGraphCreateFinalPackageButton.IsEnabled = false;
        LegacyGraphFinalPackageStatusText.Text =
            "①インポート内容を確定し、②確定内容から登録前パッケージを作成します。作成後にWeb側でフォルダーを選択します。";
        LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#52647A");
        LegacyGraphOpenFinalPackageButton.IsEnabled = false;
        LegacyGraphWorkStatusText.Text = "候補パッケージを読み込むと、作業状態を保存できます。";
        LegacyGraphWorkStatusText.Foreground = ToBrush("#52647A");
        LegacyGraphSaveWorkButton.IsEnabled = false;
        LegacyGraphOpenWorkButton.IsEnabled = !legacyGraphFinalPackageBusy && !legacyGraphBulkMergeBusy;
        LegacyGraphFinalPackageNextStepText.Text =
            "次の操作: グラフの操作を終えたら「インポート内容を確定」を押してください。";
        LegacyGraphFinalPackageNextStepText.Foreground = ToBrush("#1E40AF");
        LegacyGraphFinalPackageResultText.Text = "";
        LegacyGraphStatusText.Text = status;
        LegacyGraphStatusText.Foreground = ToBrush("#52647A");
        LegacyGraphLegendText.Text = "青い顧客ブロックから車両、書類へ読み進めます。候補パッケージを再検証すると表示できます。";
        SetLegacyGraphUiMode("graph", scheduleCheckpoint: false);
        LegacyMatchingCustomerTitleText.Text = "顧客を選択してください";
        LegacyMatchingCustomerProgressText.Text = "顧客 0 / 0";
        LegacyMatchingCustomerSummaryText.Text = "候補パッケージを読み込むと、顧客単位のマッチング確認を開始できます。";
        LegacyMatchingConfirmedVehiclesItemsControl.ItemsSource = null;
        LegacyMatchingConfirmedDocumentsItemsControl.ItemsSource = null;
        LegacyMatchingRecommendationsList.ItemsSource = null;
        LegacyMatchingRecommendationStatusText.Text = "おすすめ候補はありません。";
        LegacyMatchingConfirmedEmptyText.Visibility = Visibility.Visible;
        RefreshLegacyMatchingRecommendationActions();
    }

    private bool IsManualLinkForVehicle(
        AbacusLegacyExportCandidateGraphDocument document,
        string vehicleId) =>
        legacyGraphManualDocumentLinks.TryGetValue(GetLegacyDocumentKey(document), out var linkedVehicleId) &&
        string.Equals(linkedVehicleId, vehicleId, StringComparison.Ordinal);

    private static string GetLegacyDocumentKey(AbacusLegacyExportCandidateGraphDocument document) =>
        string.Join("|", document.Kind, document.SourceFileName, document.SourceRowNumber, document.DocumentNumber);

    private string GetLegacyGraphDocumentLinkMethod(AbacusLegacyExportCandidateGraphDocument document)
    {
        var key = GetLegacyDocumentKey(document);
        if (legacyGraphDocumentLinkMethods.TryGetValue(key, out var method))
        {
            return method;
        }

        if (legacyGraphManualDocumentLinks.ContainsKey(key)) return "manual-vehicle";
        if (legacyGraphManualDocumentCustomerLinks.ContainsKey(key)) return "manual-customer-only";
        return "automatic";
    }

    private string GetLegacyGraphRecommendationDecision(
        AbacusRecommendationCandidate candidate)
    {
        var scopedStates = GetActiveLegacyGraphRecommendationScopeStates();
        if (TryGetLegacyGraphCurrentRecommendationScope(candidate, out var currentScope))
        {
            var currentScopedState = scopedStates
                .Where(pair =>
                    string.Equals(pair.Value.WorkTargetKey, currentScope.WorkTargetKey, StringComparison.Ordinal) &&
                    string.Equals(pair.Value.ExternalCustomerId, currentScope.ExternalCustomerId, StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Value.UpdatedAtUtc)
                .Select(pair => pair.Value)
                .FirstOrDefault();
            if (currentScopedState is not null &&
                AbacusRecommendationDecisionValues.IsSupported(currentScopedState.Decision))
            {
                return currentScopedState.Decision;
            }
        }

        var matchingScopedState = scopedStates
            .Where(pair => CandidateMatchesLegacyGraphRecommendationScope(candidate, new LegacyCustomerRecommendationScope(
                pair.Value.WorkTargetKey!,
                pair.Value.ExternalCustomerId!)))
            .OrderByDescending(pair => pair.Value.UpdatedAtUtc)
            .Select(pair => pair.Value)
            .FirstOrDefault();
        if (matchingScopedState is not null &&
            AbacusRecommendationDecisionValues.IsSupported(matchingScopedState.Decision))
        {
            return matchingScopedState.Decision;
        }

        if (legacyGraphRecommendationStates.TryGetValue(candidate.CandidateId, out var state) &&
            state.Lifecycle == LegacyGraphRecommendationLifecycle.Active &&
            AbacusRecommendationDecisionValues.IsSupported(state.Decision))
        {
            return state.Decision;
        }

        return legacyGraphRecommendationDecisions.TryGetValue(candidate.CandidateId, out var decision) &&
               AbacusRecommendationDecisionValues.IsSupported(decision)
            ? decision
            : AbacusRecommendationDecisionValues.Pending;
    }

    private static string BuildLegacyGraphRecommendationScopeChangeId(
        LegacyCustomerRecommendationScope scope) =>
        string.Join("\u001F", "recommendation-scope", scope.WorkTargetKey, scope.ExternalCustomerId);

    private void RemoveLegacyGraphRecommendationChange(
        LegacyCustomerRecommendationScope? scope,
        string? candidateId = null)
    {
        legacyGraphMatchingChanges.RemoveAll(change =>
            change.Kind == "recommendation" &&
            ((scope is not null &&
              string.Equals(change.WorkTargetKey, scope.WorkTargetKey, StringComparison.Ordinal) &&
              string.Equals(change.ExternalCustomerId, scope.ExternalCustomerId, StringComparison.Ordinal)) ||
             candidateId is not null &&
             (string.Equals(change.SubjectId, candidateId, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(change.ChangeId, $"recommendation:{candidateId}", StringComparison.OrdinalIgnoreCase))));
    }

    private bool ResetLegacyGraphRecommendationScope(
        LegacyCustomerRecommendationScope scope)
    {
        var currentCandidates = legacyGraphRecommendationCandidates
            .Where(candidate =>
                LegacyMatchingCategoryKinds.GetKind(candidate) == LegacyMatchingCategoryKinds.Customer &&
                IsLegacyGraphRecommendationActive(candidate) &&
                CandidateMatchesLegacyGraphRecommendationScope(candidate, scope))
            .ToArray();

        foreach (var pair in legacyGraphRecommendationStates.ToArray())
        {
            var state = pair.Value;
            if (state.Lifecycle != LegacyGraphRecommendationLifecycle.Active ||
                !string.Equals(state.WorkTargetKey, scope.WorkTargetKey, StringComparison.Ordinal) ||
                !string.Equals(state.ExternalCustomerId, scope.ExternalCustomerId, StringComparison.Ordinal))
            {
                continue;
            }

            legacyGraphRecommendationStates[pair.Key] = state with
            {
                Lifecycle = LegacyGraphRecommendationLifecycle.Obsolete,
                ResolutionReason = "ユーザーが別人判定を元に戻しました。",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            legacyGraphRecommendationDecisions.Remove(pair.Key);
        }

        foreach (var candidate in currentCandidates)
        {
            SetLegacyGraphRecommendationState(
                candidate,
                AbacusRecommendationDecisionValues.Pending,
                scope);
        }

        RemoveLegacyGraphRecommendationChange(scope);
        return currentCandidates.Length > 0 ||
               legacyGraphRecommendationStates.Values.Any(state =>
                   state.Lifecycle == LegacyGraphRecommendationLifecycle.Obsolete &&
                   string.Equals(state.WorkTargetKey, scope.WorkTargetKey, StringComparison.Ordinal) &&
                   string.Equals(state.ExternalCustomerId, scope.ExternalCustomerId, StringComparison.Ordinal));
    }

    private void AddLegacyMatchingChange(
        AbacusRecommendationCandidate candidate,
        string decision,
        string previousDecision)
    {
        var hasScope = TryGetLegacyGraphCurrentRecommendationScope(candidate, out var scope);
        var changeId = hasScope
            ? BuildLegacyGraphRecommendationScopeChangeId(scope)
            : $"recommendation:{candidate.CandidateId}";
        RemoveLegacyGraphRecommendationChange(
            hasScope ? scope : null,
            hasScope ? null : candidate.CandidateId);

        if (decision == AbacusRecommendationDecisionValues.Rejected)
        {
            var subject = CreateLegacyMatchingRecommendationItem(candidate);
            var actionText = candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer
                ? "別人と判断"
                : "却下した提案";
            var focusCustomer = candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
                                candidate.TargetKind == AbacusRecommendationEntityKinds.Customer
                ? GetLegacyGraphScopeFocusCustomer()
                : null;
            var otherCustomer = focusCustomer is null
                ? null
                : GetLegacyGraphOtherCustomerForMatchingCandidate(candidate, focusCustomer);
            var details = focusCustomer is not null && otherCustomer is not null
                ? LegacyMatchingWorkflow.BuildCustomerRejectedChangeDetails(
                    GetLegacyGraphMatchingDisplayName(focusCustomer),
                    GetLegacyGraphCustomerDisplayName(otherCustomer),
                    GetLegacyMatchingDecisionText(previousDecision))
                : $"対象: {subject.SubjectText}\n" +
                  $"紐付け先: {subject.TargetText}\n" +
                  $"元の判定: {GetLegacyMatchingDecisionText(previousDecision)} / 元に戻せます。";
            legacyGraphMatchingChanges.Insert(0, new LegacyMatchingChangeItem(
                changeId,
                "recommendation",
                candidate.CandidateId,
                actionText,
                details,
                DateTimeOffset.UtcNow,
                GetLegacyMatchingChangeCustomerId(),
                hasScope ? scope.WorkTargetKey : null,
                hasScope ? scope.ExternalCustomerId : null));
        }
    }

    private void AddLegacyMatchingDetachedChange(
        string changeId,
        string kind,
        string subjectId,
        string title,
        string details,
        LegacyGraphDetachedUndoState? undoState = null)
    {
        legacyGraphMatchingChanges.RemoveAll(change =>
            string.Equals(change.ChangeId, $"detached:{changeId}", StringComparison.OrdinalIgnoreCase));
        legacyGraphMatchingChanges.Insert(0, new LegacyMatchingChangeItem(
            $"detached:{changeId}",
            kind,
            subjectId,
            title,
            details,
            DateTimeOffset.UtcNow,
            GetLegacyMatchingChangeCustomerId(),
            UndoState: undoState));
        RefreshLegacyMatchingChanges();
    }

    /// <summary>
    /// Gate29から利用するGate28の状態遷移です。
    /// 自動承認の適格性と、ユーザーが確認したうえで手動承認できるかを分離し、
    /// 承認時だけ既存リンク状態へ反映します。
    /// </summary>
    private bool CanManuallyApproveLegacyMatchingCandidate(
        AbacusRecommendationCandidate candidate) =>
        IsLegacyGraphRecommendationActive(candidate) &&
        (candidate.IsManual || candidate.MatchedFields.Count > 0) &&
        CanApplyLegacyGraphRecommendation(candidate);

    private static bool RequiresLegacyMatchingManualReview(
        AbacusRecommendationCandidate candidate) =>
        !candidate.IsManual && candidate.Conflicts.Count > 0;

    private bool CanApplyLegacyGraphRecommendation(
        AbacusRecommendationCandidate candidate)
    {
        if (legacyExportCandidateGraphResult is null ||
            !AreLegacyGraphRecommendationEntitiesActive(candidate))
        {
            return false;
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            var sourceCustomer = FindLegacyGraphCustomerById(candidate.SubjectId);
            var targetCustomer = FindLegacyGraphCustomerById(candidate.TargetId);
            return sourceCustomer is not null &&
                   targetCustomer is not null &&
                   !string.Equals(sourceCustomer.CustomerId, targetCustomer.CustomerId, StringComparison.Ordinal);
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Document)
        {
            var document = FindLegacyRecommendationDocumentById(candidate.SubjectId);
            if (document is null)
            {
                return false;
            }

            return candidate.TargetKind switch
            {
                AbacusRecommendationEntityKinds.Vehicle =>
                    FindLegacyGraphVehicleById(candidate.TargetId) is { } targetVehicle &&
                    FindCurrentCustomerForVehicle(targetVehicle) is not null,
                AbacusRecommendationEntityKinds.Customer =>
                    FindLegacyGraphCustomerById(candidate.TargetId) is not null,
                _ => false,
            };
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Vehicle &&
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            var vehicle = FindLegacyGraphVehicleById(candidate.SubjectId);
            var customer = FindLegacyGraphCustomerById(candidate.TargetId);
            return vehicle is not null && customer is not null && !vehicle.HasCustomer;
        }

        return false;
    }

    private bool SetLegacyGraphRecommendationDecision(
        AbacusRecommendationCandidate candidate,
        string decision)
    {
        if (!legacyGraphRecommendationCandidates.Any(item =>
                string.Equals(item.CandidateId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase)) ||
            !AbacusRecommendationDecisionValues.IsSupported(decision))
        {
            return false;
        }

        var previousDecision = GetLegacyGraphRecommendationDecision(candidate);
        var groupCandidates = GetLegacyGraphRecommendationGroupMembers(candidate);
        if (groupCandidates.Count == 0)
        {
            groupCandidates = [candidate];
        }

        if (decision == AbacusRecommendationDecisionValues.Approved)
        {
            if (!CanManuallyApproveLegacyMatchingCandidate(candidate) ||
                !ApplyLegacyGraphRecommendation(candidate))
            {
                return false;
            }

            SetLegacyGraphRecommendationState(
                candidate,
                AbacusRecommendationDecisionValues.Approved);
            foreach (var duplicate in groupCandidates.Where(item =>
                         !string.Equals(item.CandidateId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase)))
            {
                legacyGraphRecommendationStates[duplicate.CandidateId] = new LegacyGraphRecommendationState(
                    AbacusRecommendationDecisionValues.Rejected,
                    LegacyGraphRecommendationLifecycle.Obsolete,
                    LegacyGraphRecommendationLifecycleReconciler.DuplicateCustomerRecommendationObsoleteReason,
                    DateTimeOffset.UtcNow);
                legacyGraphRecommendationDecisions.Remove(duplicate.CandidateId);
            }

            InvalidateLegacyGraphImportConfirmation();
            return true;
        }

        foreach (var groupCandidate in groupCandidates)
        {
            SetLegacyGraphRecommendationState(groupCandidate, decision);
        }

        if (decision != previousDecision)
        {
            AddLegacyMatchingChange(
                candidate,
                decision,
                previousDecision);
        }

        InvalidateLegacyGraphImportConfirmation();
        return true;
    }

    private void SetLegacyGraphRecommendationState(
        AbacusRecommendationCandidate candidate,
        string decision,
        LegacyCustomerRecommendationScope? scope = null,
        string? resolutionReason = null)
    {
        if (scope is null &&
            LegacyMatchingCategoryKinds.GetKind(candidate) == LegacyMatchingCategoryKinds.Customer)
        {
            TryGetLegacyGraphCurrentRecommendationScope(candidate, out scope);
        }

        if (scope is not null)
        {
            foreach (var pair in legacyGraphRecommendationStates.ToArray())
            {
                if (string.Equals(pair.Key, candidate.CandidateId, StringComparison.OrdinalIgnoreCase) ||
                    pair.Value.Lifecycle != LegacyGraphRecommendationLifecycle.Active ||
                    !string.Equals(pair.Value.WorkTargetKey, scope.WorkTargetKey, StringComparison.Ordinal) ||
                    !string.Equals(pair.Value.ExternalCustomerId, scope.ExternalCustomerId, StringComparison.Ordinal))
                {
                    continue;
                }

                legacyGraphRecommendationStates[pair.Key] = pair.Value with
                {
                    Lifecycle = LegacyGraphRecommendationLifecycle.Obsolete,
                    ResolutionReason = "同じ作業対象と候補顧客の最新判定へ統合しました。",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                legacyGraphRecommendationDecisions.Remove(pair.Key);
            }
        }

        legacyGraphRecommendationStates[candidate.CandidateId] = new LegacyGraphRecommendationState(
            decision,
            LegacyGraphRecommendationLifecycle.Active,
            resolutionReason,
            DateTimeOffset.UtcNow,
            scope?.WorkTargetKey,
            scope?.ExternalCustomerId);
        if (decision == AbacusRecommendationDecisionValues.Pending)
        {
            legacyGraphRecommendationDecisions.Remove(candidate.CandidateId);
        }
        else
        {
            legacyGraphRecommendationDecisions[candidate.CandidateId] = decision;
        }
    }

    private bool ApplyLegacyGraphRecommendation(AbacusRecommendationCandidate candidate)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return false;
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Customer &&
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            var sourceCustomer = FindLegacyGraphCustomerById(candidate.SubjectId);
            var targetCustomer = FindLegacyGraphCustomerById(candidate.TargetId);
            if (sourceCustomer is null || targetCustomer is null ||
                string.Equals(sourceCustomer.CustomerId, targetCustomer.CustomerId, StringComparison.Ordinal))
            {
                return false;
            }

            var sourceGroupKey = GetLegacyCustomerMergeKey(sourceCustomer);
            var targetGroupKey = GetLegacyCustomerMergeKey(targetCustomer);
            var previewKey = GetLegacyGraphCustomerMergePreviewKey(sourceCustomer, targetCustomer);
            var draftBeforeApply = new[] { previewKey, sourceGroupKey, targetGroupKey }
                .Distinct(StringComparer.Ordinal)
                .Select(key => legacyGraphCustomerMergeDrafts.TryGetValue(key, out var draft)
                    ? draft
                    : null)
                .FirstOrDefault(draft => draft is not null);
            var wasExpanded = legacyGraphCustomerGroupExpanded.GetValueOrDefault(sourceGroupKey) ||
                              legacyGraphCustomerGroupExpanded.GetValueOrDefault(targetGroupKey);
            // Recommendationの承認は、候補を仮グループへ追加するだけです。
            // 正式な論理顧客への昇格は「この顧客を確定」で行います。
            var groupKey = AddLegacyGraphCustomerToMergeGroup(sourceCustomer, targetCustomer);
            if (draftBeforeApply is not null)
            {
                foreach (var draftKey in new[] { previewKey, sourceGroupKey, targetGroupKey }
                             .Distinct(StringComparer.Ordinal))
                {
                    if (!string.Equals(draftKey, groupKey, StringComparison.Ordinal))
                    {
                        legacyGraphCustomerMergeDrafts.Remove(draftKey);
                    }
                }

                legacyGraphCustomerMergeDrafts[groupKey] = draftBeforeApply with
                {
                    GroupKey = groupKey,
                };
            }
            if (wasExpanded)
            {
                legacyGraphCustomerGroupExpanded[groupKey] = true;
            }
            else
            {
                legacyGraphCustomerGroupExpanded.Remove(groupKey);
            }
            if (string.Equals(legacyGraphUiMode, "matching", StringComparison.OrdinalIgnoreCase))
            {
                // マッチングUIでは、候補の向きにかかわらず確認中の顧客を固定します。
                EnsureLegacyGraphMatchingCustomerSelection();
            }
            RefreshLegacyGraphCustomerList();
            return true;
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Document)
        {
            var document = legacyExportCandidateGraphResult.AllDocuments.FirstOrDefault(item =>
                string.Equals(GetLegacyDocumentKey(item), candidate.SubjectId, StringComparison.OrdinalIgnoreCase));
            if (document is null)
            {
                return false;
            }

            if (candidate.TargetKind == AbacusRecommendationEntityKinds.Vehicle)
            {
                var vehicle = GetLegacyGraphAllVehicles().FirstOrDefault(item =>
                    string.Equals(item.VehicleId, candidate.TargetId, StringComparison.Ordinal));
                if (vehicle is null || FindCurrentCustomerForVehicle(vehicle) is null)
                {
                    return false;
                }

                var key = GetLegacyDocumentKey(document);
                InvalidateLegacyGraphApprovalForDocument(document);
                legacyGraphManualDocumentCustomerLinks.Remove(key);
                legacyGraphManualDocumentLinks[key] = vehicle.VehicleId;
                legacyGraphUnconnectedDocumentKeys.Remove(key);
                legacyGraphTrayDocumentKeys.Remove(key);
                legacyGraphExcludedDocumentKeys.Remove(key);
                legacyGraphDocumentLinkMethods[key] = AbacusLinkMethods.Recommended;
                legacyGraphDocumentLinkReasons[key] = candidate.Reason;
                RefreshLegacyGraphAfterManualLink(document, vehicle.VehicleId);
                legacyGraphDocumentLinkMethods[key] = AbacusLinkMethods.Recommended;
                legacyGraphDocumentLinkReasons[key] = candidate.Reason;
                return true;
            }

            if (candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
            {
                var customer = FindLegacyGraphCustomerById(candidate.TargetId);
                if (customer is null)
                {
                    return false;
                }

                ApplyLegacyGraphManualCustomerLink(document, customer);
                var key = GetLegacyDocumentKey(document);
                legacyGraphDocumentLinkMethods[key] = AbacusLinkMethods.Recommended;
                legacyGraphDocumentLinkReasons[key] = candidate.Reason;
                return true;
            }

            return false;
        }

        if (candidate.SubjectKind == AbacusRecommendationEntityKinds.Vehicle &&
            candidate.TargetKind == AbacusRecommendationEntityKinds.Customer)
        {
            var vehicle = FindLegacyGraphVehicleById(candidate.SubjectId);
            var customer = FindLegacyGraphCustomerById(candidate.TargetId);
            if (vehicle is null || customer is null || vehicle.HasCustomer)
            {
                return false;
            }

            ApplyLegacyGraphManualVehicleCustomerLink(vehicle, customer);
            return true;
        }

        return false;
    }

    private void ClearLegacyGraphRecommendationDecisions(
        string subjectKind,
        string subjectId)
    {
        foreach (var candidateId in legacyGraphRecommendationCandidates
                     .Where(candidate =>
                         string.Equals(candidate.SubjectKind, subjectKind, StringComparison.Ordinal) &&
                         string.Equals(candidate.SubjectId, subjectId, StringComparison.OrdinalIgnoreCase))
                     .Select(candidate => candidate.CandidateId)
                     .ToArray())
        {
            legacyGraphRecommendationDecisions.Remove(candidateId);
            if (legacyGraphRecommendationStates.TryGetValue(candidateId, out var state))
            {
                var resolutionReason = !string.IsNullOrWhiteSpace(state.ResolutionReason)
                    ? state.ResolutionReason
                    : state.Decision == AbacusRecommendationDecisionValues.Rejected
                        ? LegacyGraphRecommendationLifecycleReconciler.ExplicitRejectedObsoleteReason
                        : LegacyGraphRecommendationLifecycleReconciler.TemporaryManualLinkObsoleteReason;
                legacyGraphRecommendationStates[candidateId] = state with
                {
                    Lifecycle = LegacyGraphRecommendationLifecycle.Obsolete,
                    ResolutionReason = resolutionReason,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
            }
        }
    }

    private string GetLegacyGraphDocumentLinkReason(AbacusLegacyExportCandidateGraphDocument document)
    {
        var key = GetLegacyDocumentKey(document);
        if (legacyGraphDocumentLinkReasons.TryGetValue(key, out var reason) && !string.IsNullOrWhiteSpace(reason))
        {
            return reason;
        }

        return GetLegacyGraphDocumentLinkMethod(document) switch
        {
            "manual-vehicle" => "ユーザーが紐づけ先車両を選択",
            "manual-customer-only" => "ユーザーが顧客だけへ手動紐づけ（車両なし）",
            _ when !string.IsNullOrWhiteSpace(document.LinkedVehicleId) => "候補マニフェストの一意一致",
            _ when IsLegacyGraphCustomerDirectDocument(document) => "顧客候補が一意で、車両情報がないため顧客へ自動紐づけ",
            _ => "ABACUS候補グラフの自動判定",
        };
    }

    private static (string Stroke, string Fill, bool Dashed) GetDocumentVisual(string status) => status switch
    {
        "一意一致" => ("#166534", "#ECFDF5", false),
        "要確認" or "候補" => ("#D97706", "#FFF7ED", true),
        _ => ("#B91C1C", "#FEF2F2", true),
    };

    private static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "未設定" : value;

    private static Brush ToBrush(string color) =>
        (Brush)new BrushConverter().ConvertFromString(color)!;

    private async void InspectAbacusNativeWindowsButton_Click(object sender, RoutedEventArgs e)
    {
        InspectAbacusNativeWindowsButton.IsEnabled = false;
        AbacusNativeWindowsGrid.ItemsSource = null;
        AbacusNativeWindowsStatusText.Text = "ABACUSのネイティブ子ウィンドウを読み取り診断しています…";
        AbacusNativeWindowsStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await session.InspectAbacusNativeWindowsAsync();
            abacusMayBeRunning = result.IsRunning;
            AbacusNativeWindowsGrid.ItemsSource = result.NativeWindows ?? [];
            RenderAbacusNavigation(CreateAbacusNavigationSnapshot(result));
            AbacusNativeWindowsStatusText.Text = result.Message;
            AbacusNativeWindowsStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(
                result.Status == "native-windows-ready" ? "#17643A" : "#805B10")!;
        }
        catch (Exception exception)
        {
            AbacusNativeWindowsStatusText.Text = $"内部ウィンドウ診断に失敗しました: {exception.Message}";
            AbacusNativeWindowsStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            SetAbacusButtonsBusy(false);
        }
    }

    private async void InspectAbacusNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        InspectAbacusNavigationButton.IsEnabled = false;
        AbacusNavigationStateText.Text = "判定中…";
        AbacusNavigationDetailText.Text = "ABACUSの表示状態を読み取っています。操作・編集・保存は行いません。";
        AbacusNavigationObservedText.Text = "確認中の子ウィンドウ: -";
        try
        {
            var result = await session.InspectAbacusNativeWindowsAsync();
            abacusMayBeRunning = result.IsRunning;
            RenderAbacusNavigation(CreateAbacusNavigationSnapshot(result));
        }
        catch (Exception exception)
        {
            AbacusNavigationStateText.Text = "判定に失敗しました";
            AbacusNavigationStateText.Foreground = ToBrush("#A61B1B");
            AbacusNavigationDetailText.Text = exception.Message;
            AbacusNavigationObservedText.Text = "確認中の子ウィンドウ: -";
        }
        finally
        {
            SetAbacusButtonsBusy(false);
        }
    }

    private void RenderAbacusNavigation(AbacusNavigationSnapshot snapshot)
    {
        lastAbacusNavigationSnapshot = snapshot;
        var (foreground, background) = snapshot.State switch
        {
            AbacusNavigationState.MainMenu => ("#1E40AF", "#EAF2FF"),
            AbacusNavigationState.VehicleList or AbacusNavigationState.VehicleDetail or
                AbacusNavigationState.VehicleManagement or AbacusNavigationState.ExpandedImage
                => ("#17643A", "#EAF7EF"),
            AbacusNavigationState.NotRunning => ("#52647A", "#E9EEF5"),
            _ => ("#805B10", "#FFF4D6"),
        };

        AbacusNavigationStateText.Text = snapshot.Label;
        AbacusNavigationStateText.Foreground = ToBrush(foreground);
        AbacusNavigationStatePanel.Background = ToBrush(background);
        AbacusNavigationDetailText.Text = snapshot.Detail;
        var observed = snapshot.VisibleChildWindows.Count == 0
            ? "なし（メニュー画面または子ウィンドウ未検出）"
            : string.Join(
                "、",
                snapshot.VisibleChildWindows.Select(window =>
                    string.IsNullOrWhiteSpace(window.Title) ? window.ClassName : window.Title));
        AbacusNavigationObservedText.Text = $"確認中の子ウィンドウ: {observed}";
    }

    private AbacusNavigationSnapshot CreateAbacusNavigationSnapshot(AbacusRuntimeSnapshot result)
    {
        AbacusScreenVisualResult? visual = null;
        if (result.ProcessId.HasValue && result.WindowHandle.HasValue)
        {
            try
            {
                var capture = windowCaptureService.Capture(result.WindowHandle.Value, result.ProcessId.Value);
                visual = abacusScreenStateClassifier.Classify(capture);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // キャプチャ不能時も、従来の読み取り専用子ウィンドウ診断へ安全にフォールバックします。
            }
        }

        return abacusNavigationStateDetector.Detect(result, visual);
    }

    private async void InspectAbacusAutomationButton_Click(object sender, RoutedEventArgs e)
    {
        InspectAbacusAutomationButton.IsEnabled = false;
        AbacusAutomationStatusText.Text = "FileMaker ActiveXの登録と実行中インスタンスを読み取り診断しています…";
        AbacusAutomationStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await session.InspectAbacusAutomationAsync();
            abacusMayBeRunning = result.IsRunning;
            AbacusAutomationStatusText.Text = result.Message;
            AbacusAutomationStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(
                result.Status == "automation-active" ? "#17643A" : "#805B10")!;
        }
        catch (Exception exception)
        {
            AbacusAutomationStatusText.Text = $"ActiveX診断に失敗しました: {exception.Message}";
            AbacusAutomationStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            SetAbacusButtonsBusy(false);
        }
    }

    private void SelectAnalysisFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "分析するABACUSフォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            AnalysisPathTextBox.Text = dialog.FolderName;
            ResetDataAnalysis();
        }
    }

    private void AnalysisPathTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ResetDataAnalysis();

    private async void AnalyzeDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AnalysisPathTextBox.Text))
        {
            MessageBox.Show(this, "分析するABACUSフォルダーを指定してください。", "フォルダー未選択");
            return;
        }

        AnalyzeDataButton.IsEnabled = false;
        AnalysisPathTextBox.IsEnabled = false;
        AnalysisStatusText.Text = "販売・整備バックアップを読み取り専用で分析しています…";
        try
        {
            var analysis = await dataAnalyzer.AnalyzeAsync(AnalysisPathTextBox.Text.Trim());
            RenderDataAnalysis(analysis);
        }
        catch (Exception exception)
        {
            AnalysisStatusText.Text = $"分析に失敗しました: {exception.Message}";
        }
        finally
        {
            AnalyzeDataButton.IsEnabled = true;
            AnalysisPathTextBox.IsEnabled = true;
        }
    }

    private void SelectLinkageFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "紐付け候補を作成するABACUSフォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            LinkagePathTextBox.Text = dialog.FolderName;
        }
    }

    private void LinkagePathTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ResetLinkagePreview();

    private async void BuildLinkageButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LinkagePathTextBox.Text))
        {
            MessageBox.Show(this, "ABACUSフォルダーを指定してください。", "フォルダー未選択");
            return;
        }

        BuildLinkageButton.IsEnabled = false;
        LinkagePathTextBox.IsEnabled = false;
        LinkageStatusText.Text = "顧客・車両・書類の候補と競合を分析しています…";
        try
        {
            linkagePlan = await linkagePlanner.PlanAsync(LinkagePathTextBox.Text.Trim());
            RenderLinkagePlan(linkagePlan);
        }
        catch (Exception exception)
        {
            LinkageStatusText.Text = $"候補作成に失敗しました: {exception.Message}";
        }
        finally
        {
            BuildLinkageButton.IsEnabled = true;
            LinkagePathTextBox.IsEnabled = true;
        }
    }

    private void LinkageFilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyLinkageFilter();

    private void SourcePathTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ResetInspection(clearPath: false);
        OperationStatusText.Text = string.IsNullOrWhiteSpace(SourcePathTextBox.Text)
            ? "未検査"
            : "パスが指定されました。「読取検査」を押してください。";
    }

    private async void InspectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourcePathTextBox.Text))
        {
            MessageBox.Show(this, "保存用ABACUSフォルダーを選択してください。", "フォルダー未選択");
            return;
        }

        BeginOperation("保存用原本を検査しています…");
        ResetInspection(clearPath: false);
        try
        {
            var progress = new Progress<AbacusInspectionProgress>(item =>
            {
                SetProgress(item.CompletedFiles, item.TotalFiles);
                OperationStatusText.Text = $"検査中: {item.CurrentFile}";
            });
            sourceReport = await folderInspector.InspectAsync(
                SourcePathTextBox.Text.Trim(),
                progress,
                operationCancellation!.Token);
            RenderInspection(sourceReport);
        }
        catch (OperationCanceledException)
        {
            OperationStatusText.Text = "検査をキャンセルしました。原本は変更されていません。";
        }
        catch (Exception exception)
        {
            OperationStatusText.Text = $"検査に失敗しました: {exception.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async void CreateWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sourceReport is not { IsValid: true })
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "作業用コピーを保存する親フォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"約{FormatFileSize(sourceReport.TotalBytes)}の作業用コピーを新規作成します。\n\nコピー後に原本とコピーを再検証するため、完了まで時間がかかります。続行しますか？",
            "作業用コピーを作成",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        BeginOperation("作業用コピーを作成しています…");
        workspaceResult = null;
        ResetImageLinkCapture();
        LaunchAbacusButton.IsEnabled = false;
        try
        {
            var progress = new Progress<AbacusWorkspaceProgress>(item =>
            {
                SetProgress(item.CompletedFiles, item.TotalFiles);
                OperationStatusText.Text = $"{item.Phase}: {item.CurrentFile}";
            });
            workspaceResult = await workspaceService.CreateAsync(
                sourceReport,
                dialog.FolderName,
                progress,
                operationCancellation!.Token);
            verifiedWorkspacePath = workspaceResult.WorkspacePath;
            verifiedSourcePath = sourceReport.SourcePath;
            verifiedOriginalFingerprint = sourceReport.FolderFingerprint;
            verifiedWorkspaceFingerprint = workspaceResult.WorkspaceReport.FolderFingerprint;
            WorkspacePathText.Text = $"検証済み作業用コピー:\n{workspaceResult.WorkspacePath}\n原本の再検証: 一致";
            OperationStatusText.Text = "作業用コピーと保存用原本のハッシュが一致しました。";
            LaunchAbacusButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            WorkspacePathText.Text = "コピーをキャンセルしました。途中まで作成されたフォルダーは起動に使用しないでください。";
            OperationStatusText.Text = "コピーをキャンセルしました。保存用原本は変更していません。";
        }
        catch (Exception exception)
        {
            WorkspacePathText.Text = $"作業用コピーを使用できません: {exception.Message}";
            OperationStatusText.Text = "コピーまたは検証に失敗しました。";
        }
        finally
        {
            EndOperation();
        }
    }

    private async void LaunchAbacusButton_Click(object sender, RoutedEventArgs e)
    {
        await LaunchVerifiedAbacusAsync();
    }

    private async Task LaunchVerifiedAbacusAsync()
    {
        var activeWorkspacePath = GetActiveWorkspacePath();
        if (activeWorkspacePath is null)
        {
            return;
        }

        SetAbacusButtonsBusy(true);
        ClearDiagnosedImageWindow();
        AutomationResultText.Text = "コピー側ABACUSを起動し、ウィンドウを認識しています…";
        try
        {
            if (session.Snapshot.State != LegacyHostState.Connected)
            {
                await session.StartAsync();
            }

            var executablePath = Path.Combine(activeWorkspacePath, AbacusConstants.ExecutableFileName);
            var result = await session.LaunchAndInspectAbacusAsync(executablePath);
            RenderAbacusResult(result);
            ImageDiagnosticStatusText.Text = result.Message;
        }
        catch (Exception exception)
        {
            AutomationResultText.Text = $"起動または認識に失敗しました: {exception.Message}";
            ImageDiagnosticStatusText.Text = $"起動または認識に失敗しました: {exception.Message}";
        }
        finally
        {
            SetAbacusButtonsBusy(false);
        }
    }

    private void SelectExistingWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "検証済みABACUS作業用コピーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            ExistingWorkspacePathTextBox.Text = dialog.FolderName;
            workspaceResult = null;
            verifiedWorkspacePath = null;
            verifiedSourcePath = null;
            verifiedOriginalFingerprint = null;
            verifiedWorkspaceFingerprint = null;
            ResetImageLinkCapture();
            VerifyExistingWorkspaceButton.IsEnabled = true;
            ImageLaunchAbacusButton.IsEnabled = false;
            ImageDiagnosticStatusText.Text = "未検証です。「コピーを再検証」を押してください。";
            ImageUiElementsGrid.ItemsSource = null;
        }
    }

    private async void VerifyExistingWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ExistingWorkspacePathTextBox.Text))
        {
            return;
        }

        BeginOperation("検証済み作業用コピーを再検証しています…");
        VerifyExistingWorkspaceButton.IsEnabled = false;
        ImageCancelButton.IsEnabled = true;
        verifiedWorkspacePath = null;
        ResetImageLinkCapture();
        ImageUiElementsGrid.ItemsSource = null;
        try
        {
            var progress = new Progress<AbacusWorkspaceProgress>(item =>
            {
                SetProgress(item.CompletedFiles, item.TotalFiles);
                ImageVerificationProgressBar.Maximum = Math.Max(item.TotalFiles, 1);
                ImageVerificationProgressBar.Value = Math.Min(item.CompletedFiles, Math.Max(item.TotalFiles, 1));
                ImageDiagnosticStatusText.Text = $"{item.Phase}: {item.CurrentFile}";
            });
            var result = await workspaceService.VerifyExistingAsync(
                ExistingWorkspacePathTextBox.Text.Trim(),
                progress,
                operationCancellation!.Token);
            verifiedWorkspacePath = result.WorkspacePath;
            verifiedSourcePath = result.SourcePath;
            verifiedOriginalFingerprint = result.OriginalFingerprint;
            verifiedWorkspaceFingerprint = result.WorkspaceReport.FolderFingerprint;
            workspaceResult = null;
            var runtimeChangeDetail = result.AllowedRuntimeChanges.Count == 0
                ? "作成時から変更なし"
                : $"FileMaker起動時の限定的な変更を許容: {string.Join("、", result.AllowedRuntimeChanges)}";
            ImageDiagnosticStatusText.Text =
                $"再検証に合格しました（{result.WorkspaceReport.FileCount:N0}ファイル、{runtimeChangeDetail}）。コピー側ABACUSを起動できます。";
            ImageLaunchAbacusButton.IsEnabled = !abacusMayBeRunning;
            LaunchAbacusButton.IsEnabled = !abacusMayBeRunning;
        }
        catch (OperationCanceledException)
        {
            ImageDiagnosticStatusText.Text = "再検証をキャンセルしました。作業用コピーは起動対象にしていません。";
        }
        catch (Exception exception)
        {
            ImageDiagnosticStatusText.Text = $"再検証に失敗しました: {exception.Message}";
        }
        finally
        {
            ImageCancelButton.IsEnabled = false;
            EndOperation();
            VerifyExistingWorkspaceButton.IsEnabled = true;
        }
    }

    private void ImageCancelButton_Click(object sender, RoutedEventArgs e) => operationCancellation?.Cancel();

    private async void ImageLaunchAbacusButton_Click(object sender, RoutedEventArgs e)
    {
        await LaunchVerifiedAbacusAsync();
    }

    private async void InspectImageUiButton_Click(object sender, RoutedEventArgs e)
    {
        InspectImageUiButton.IsEnabled = false;
        ClearDiagnosedImageWindow();
        ImageDiagnosticStatusText.Text = "表示中のABACUS画面を読み取り診断しています…";
        try
        {
            var result = await session.InspectAbacusUiAsync();
            abacusMayBeRunning = result.IsRunning;
            ImageDiagnosticStatusText.Text = result.Message;
            ImageUiElementsGrid.ItemsSource = result.AutomationElements ?? [];
            var imageWindow = result.AutomationElements?.FirstOrDefault(element =>
                element.NativeWindowHandle != 0 &&
                string.Equals(element.ControlType, "Window", StringComparison.Ordinal) &&
                element.Name.StartsWith("abx-cs-sk.", StringComparison.OrdinalIgnoreCase));
            if (imageWindow is not null && result.ProcessId.HasValue)
            {
                diagnosedImageWindowHandle = imageWindow.NativeWindowHandle;
                diagnosedAbacusProcessId = result.ProcessId;
                CaptureImageWindowButton.IsEnabled = true;
                ImageWindowCaptureStatusText.Text =
                    $"画像表示ウィンドウを特定しました（{imageWindow.Width:N0} × {imageWindow.Height:N0}px）。拡大表示後にキャプチャできます。";
                ImageWindowCaptureStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            }
            else
            {
                ImageWindowCaptureStatusText.Text = "画像表示ウィンドウを特定できませんでした。画像画面を表示して再診断してください。";
                ImageWindowCaptureStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            }
        }
        catch (Exception exception)
        {
            ImageDiagnosticStatusText.Text = $"画面診断に失敗しました: {exception.Message}";
            ImageUiElementsGrid.ItemsSource = null;
        }
        finally
        {
            SetAbacusButtonsBusy(false);
        }
    }

    private async void CaptureImageWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!diagnosedImageWindowHandle.HasValue || !diagnosedAbacusProcessId.HasValue)
        {
            return;
        }

        AbacusCaptureCropResult cropResult;
        try
        {
            var rawCapture = windowCaptureService.Capture(
                diagnosedImageWindowHandle.Value,
                diagnosedAbacusProcessId.Value);
            cropResult = captureCropper.Crop(rawCapture);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            ImageWindowCaptureStatusText.Text = $"画像ウィンドウを取得できません: {exception.Message}";
            ImageWindowCaptureStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            ClearDiagnosedImageWindow();
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "ABACUS画像ウィンドウ1件の保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.FolderName));
        if (IsSameOrSubPath(destination, verifiedSourcePath) ||
            IsSameOrSubPath(destination, GetActiveWorkspacePath()))
        {
            MessageBox.Show(
                this,
                "保存用原本または作業用コピーの内部には画像を保存できません。別のフォルダーを選択してください。",
                "保存先を変更してください",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"ABACUSの画像表示ウィンドウだけをPNGとして新規保存します。\n\n" +
            $"元のキャプチャ: {cropResult.OriginalWidth:N0} × {cropResult.OriginalHeight:N0}px\n" +
            $"保存する領域: {cropResult.Image.PixelWidth:N0} × {cropResult.Image.PixelHeight:N0}px\n" +
            $"余白の自動除去: {(cropResult.WasCropped ? "適用" : "未適用")}\n" +
            $"保存先: {destination}\n\n画面表示のキャプチャであり、元画像ファイルそのものではありません。続行しますか？",
            "画像表示ウィンドウを保存",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        CaptureImageWindowButton.IsEnabled = false;
        ImageWindowCaptureStatusText.Text = "画像表示ウィンドウをPNGへ変換し、保存前検証を行っています…";
        try
        {
            var result = await clipboardImageExporter.ExportAsync(cropResult.Image, destination);
            lastImageCaptureResult = result;
            lastImageCaptureSourceFolder = verifiedSourcePath;
            lastImageCaptureWorkspaceFolder = GetActiveWorkspacePath();
            lastImageCaptureSourceFingerprint = verifiedOriginalFingerprint;
            lastImageCaptureWorkspaceFingerprint = verifiedWorkspaceFingerprint;
            CreateImageLinkManifestButton.IsEnabled = true;
            ImageLinkManifestStatusText.Text = "画像を保存しました。車両画面で確認した識別子を入力して、紐付け準備を作成してください。";
            ImageLinkManifestStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            ImageWindowCaptureStatusText.Text =
                $"画像表示ウィンドウを保存しました。\n{result.FilePath}\n" +
                $"{result.PixelWidth:N0} × {result.PixelHeight:N0}px / {FormatFileSize(result.FileSize)} / SHA-256: {result.Sha256}";
            ImageWindowCaptureStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or NotSupportedException or ArgumentException)
        {
            ImageWindowCaptureStatusText.Text = $"画像ウィンドウの保存に失敗しました: {exception.Message}";
            ImageWindowCaptureStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            CaptureImageWindowButton.IsEnabled = true;
        }
    }

    private async void CreateImageLinkManifestButton_Click(object sender, RoutedEventArgs e)
    {
        if (lastImageCaptureResult is null ||
            string.IsNullOrWhiteSpace(lastImageCaptureSourceFolder) ||
            string.IsNullOrWhiteSpace(lastImageCaptureWorkspaceFolder))
        {
            MessageBox.Show(
                this,
                "先に検証済み作業用コピーから画像を1件保存してください。",
                "保存画像がありません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var chassisNumber = ImageLinkChassisNumberTextBox.Text.Trim();
        var registrationNumber = ImageLinkRegistrationNumberTextBox.Text.Trim();
        var customerName = ImageLinkCustomerNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(chassisNumber) && string.IsNullOrWhiteSpace(registrationNumber))
        {
            MessageBox.Show(
                this,
                "車台番号または登録番号を少なくとも1つ入力してください。車台番号を優先してください。",
                "車両識別子がありません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var identifierKind = string.IsNullOrWhiteSpace(chassisNumber) ? "登録番号だけ" : "車台番号を優先";
        var confirmation = MessageBox.Show(
            this,
            $"保存済み画像を、次の識別情報で紐付け準備します。\n\n" +
            $"車台番号: {(string.IsNullOrWhiteSpace(chassisNumber) ? "（未入力）" : chassisNumber)}\n" +
            $"登録番号: {(string.IsNullOrWhiteSpace(registrationNumber) ? "（未入力）" : registrationNumber)}\n" +
            $"顧客名: {(string.IsNullOrWhiteSpace(customerName) ? "（未入力）" : customerName)}\n" +
            $"照合方式: {identifierKind}\n\n" +
            "これは確認専用のマニフェストを画像と同じフォルダーへ作成するだけです。データベース登録やアップロードは行いません。ABACUS画面の表示と一致することを確認してから続行してください。",
            "画像の紐付け準備",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        CreateImageLinkManifestButton.IsEnabled = false;
        ImageLinkManifestStatusText.Text = "画像のSHA-256と作業用コピーの検証情報を確認し、マニフェストを作成しています…";
        ImageLinkManifestStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await imageLinkManifestStore.CreateAsync(
                lastImageCaptureResult,
                lastImageCaptureSourceFolder,
                lastImageCaptureWorkspaceFolder,
                lastImageCaptureSourceFingerprint,
                lastImageCaptureWorkspaceFingerprint,
                chassisNumber,
                registrationNumber,
                customerName);
            ImageLinkManifestStatusText.Text =
                $"画像紐付けマニフェストを作成しました。\n" +
                $"保存先: {result.FilePath}\n" +
                $"照合方式: {(result.MatchStrategy == "chassis" ? "車台番号" : "登録番号（要確認）")}\n" +
                $"マニフェスト SHA-256: {result.Sha256}\n" +
                "この段階ではデータベース登録・アップロードを行っていません。";
            ImageLinkManifestStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or InvalidOperationException or ArgumentException)
        {
            ImageLinkManifestStatusText.Text = $"画像紐付けマニフェストの作成に失敗しました: {exception.Message}";
            ImageLinkManifestStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            CreateImageLinkManifestButton.IsEnabled = true;
        }
    }

    private void SelectImageLinkManifestFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "画像マニフェスト保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImageLinkManifestFolderTextBox.Text = dialog.FolderName;
        ResetImageLinkMatch(clearPaths: false);
        ImageLinkMatchStatusText.Text = "画像マニフェスト保存先を選択しました。車両一覧CSVフォルダーも選択してください。";
        UpdateImageLinkMatchButtonState();
    }

    private void SelectImageLinkVehicleExportFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "ABACUS車両一覧CSVフォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImageLinkVehicleExportFolderTextBox.Text = dialog.FolderName;
        ResetImageLinkMatch(clearPaths: false);
        ImageLinkMatchStatusText.Text = "車両一覧CSVフォルダーを選択しました。照合を実行できます。";
        UpdateImageLinkMatchButtonState();
    }

    private async void MatchImageLinksButton_Click(object sender, RoutedEventArgs e)
    {
        var imageFolder = ImageLinkManifestFolderTextBox.Text.Trim();
        var vehicleFolder = ImageLinkVehicleExportFolderTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(imageFolder) || string.IsNullOrWhiteSpace(vehicleFolder))
        {
            MessageBox.Show(
                this,
                "画像マニフェスト保存先と車両一覧CSVフォルダーを選択してください。",
                "照合先がありません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        imageLinkMatchBusy = true;
        SetImageLinkMatchControlsBusy(true);
        ImageLinkMatchesGrid.ItemsSource = null;
        ResetImageRegistrationPreview(clearDestination: false);
        ResetWebImportPreview(clearPaths: true);
        ResetWebImportMapping(clearPaths: true);
        ImageLinkMatchSummaryText.Text = "";
        ImageLinkMatchStatusText.Text = "マニフェスト・画像・車両一覧CSVを読み取り、SHA-256と識別子を照合しています…";
        ImageLinkMatchStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var report = await imageLinkMatcher.MatchAsync(imageFolder, vehicleFolder);
            ImageLinkMatchesGrid.ItemsSource = report.Rows;
            ImageLinkMatchesGrid.SelectedIndex = -1;
            ImageLinkMatchSummaryText.Text =
                $"マニフェスト: {report.ManifestCount:N0}件 / " +
                $"一致: {report.MatchedCount:N0}件 / " +
                $"要確認: {report.ReviewCount:N0}件 / " +
                $"競合: {report.ConflictCount:N0}件 / " +
                $"未一致: {report.NotFoundCount:N0}件 / " +
                $"不正: {report.InvalidCount:N0}件";
            if (report.Errors.Count > 0)
            {
                ImageLinkMatchSummaryText.Text +=
                    $"\nエラー詳細:\n{string.Join("\n", report.Errors.Take(10))}";
            }

            var hasConcerns = report.ReviewCount > 0 ||
                              report.ConflictCount > 0 ||
                              report.NotFoundCount > 0 ||
                              report.InvalidCount > 0;
            ImageLinkMatchStatusText.Text = report.IsValid && !hasConcerns
                ? "照合が完了しました。『一致』だけが自動登録候補です。"
                : "照合が完了しました。要確認・競合・未一致・不正は自動登録候補として扱いません。";
            ImageLinkMatchStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(
                report.ConflictCount > 0 || report.InvalidCount > 0 ? "#A61B1B" : hasConcerns ? "#805B10" : "#17643A")!;
            ImageLinkApprovalStatusText.Text =
                "照合結果から一意の候補を選択すると、目視確認済みとして記録できます。";
            ImageLinkApprovalStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#36465A")!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException or
                                           NotSupportedException)
        {
            ImageLinkMatchesGrid.ItemsSource = null;
            ImageLinkMatchesGrid.SelectedIndex = -1;
            ImageLinkMatchSummaryText.Text = "";
            ImageLinkMatchStatusText.Text = $"画像マニフェストと車両一覧CSVの照合に失敗しました: {exception.Message}";
            ImageLinkMatchStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            ImageLinkApprovalStatusText.Text = "照合に失敗したため、確認済み証跡は作成できません。";
            ImageLinkApprovalStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            imageLinkMatchBusy = false;
            SetImageLinkMatchControlsBusy(false);
        }
    }

    private void SetImageLinkMatchControlsBusy(bool busy)
    {
        SelectImageLinkManifestFolderButton.IsEnabled = !busy;
        SelectImageLinkVehicleExportFolderButton.IsEnabled = !busy;
        MatchImageLinksButton.IsEnabled = !busy &&
            !string.IsNullOrWhiteSpace(ImageLinkManifestFolderTextBox.Text) &&
            !string.IsNullOrWhiteSpace(ImageLinkVehicleExportFolderTextBox.Text);
        if (busy)
        {
            ApproveImageLinkButton.IsEnabled = false;
        }
        else
        {
            UpdateImageLinkApprovalButtonState();
        }
        SelectImageRegistrationDestinationButton.IsEnabled = !busy && !imageRegistrationPreviewBusy && !webImportMappingBusy && !webImportRegistrationBusy;
        UpdateImageRegistrationPreviewButtonState();
        SelectWebImportSourcePackageButton.IsEnabled = !busy && !webImportPreviewBusy && !webImportMappingBusy && !webImportRegistrationBusy;
        SelectWebImportDestinationButton.IsEnabled = !busy && !webImportPreviewBusy && !webImportMappingBusy && !webImportRegistrationBusy;
        UpdateWebImportPreviewButtonState();
        SelectWebImportMappingSourceButton.IsEnabled = !busy && !webImportMappingBusy && !webImportRegistrationBusy;
        SelectWebImportMappingDestinationButton.IsEnabled = !busy && !webImportMappingBusy && !webImportRegistrationBusy;
        UpdateWebImportMappingButtonState();
        SelectWebImportRegistrationSourceButton.IsEnabled = !busy && !webImportMappingBusy && !webImportRegistrationBusy;
        SelectWebImportRegistrationDestinationButton.IsEnabled = !busy && !webImportMappingBusy && !webImportRegistrationBusy;
        UpdateWebImportRegistrationButtonState();
    }

    private void UpdateImageLinkMatchButtonState()
    {
        if (!imageLinkMatchBusy)
        {
            SetImageLinkMatchControlsBusy(false);
        }
    }

    private void ImageLinkMatchesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateImageLinkApprovalButtonState();
    }

    private void UpdateImageLinkApprovalButtonState()
    {
        if (imageLinkMatchBusy || imageLinkApprovalBusy)
        {
            ApproveImageLinkButton.IsEnabled = false;
            return;
        }

        var row = ImageLinkMatchesGrid.SelectedItem as AbacusImageLinkMatchRow;
        ApproveImageLinkButton.IsEnabled =
            row is not null &&
            row.Status is "matched" or "review" &&
            row.CandidateCount == 1 &&
            row.Candidates.Count == 1 &&
            !string.Equals(row.ApprovalStatus, "確認済み", StringComparison.Ordinal);
    }

    private async void ApproveImageLinkButton_Click(object sender, RoutedEventArgs e)
    {
        var row = ImageLinkMatchesGrid.SelectedItem as AbacusImageLinkMatchRow;
        var imageFolder = ImageLinkManifestFolderTextBox.Text.Trim();
        var vehicleFolder = ImageLinkVehicleExportFolderTextBox.Text.Trim();
        if (row is null || row.CandidateCount != 1 || row.Candidates.Count != 1 ||
            row.Status is not ("matched" or "review") ||
            string.IsNullOrWhiteSpace(imageFolder) || string.IsNullOrWhiteSpace(vehicleFolder))
        {
            MessageBox.Show(
                this,
                "確認済みにできる一意の候補を選択してください。",
                "候補を選択してください",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var candidate = row.Candidates[0];
        var confirmation = MessageBox.Show(
            this,
            $"次の画像と車両を同一車両として確認済みにしますか？\n\n" +
            $"画像: {row.ImageFileName}\n" +
            $"判定: {row.StatusLabel}\n" +
            $"識別子: {row.Identifier}\n" +
            $"顧客: {candidate.CustomerName}\n" +
            $"車両: {candidate.Maker} {candidate.VehicleName} {candidate.Model}\n" +
            $"CSV: {candidate.FileName} {candidate.RowNumber}行\n\n" +
            "この操作は確認済み証跡JSONを作成するだけで、顧客・車両の登録やアップロードは行いません。",
            "画像と車両の目視確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        imageLinkApprovalBusy = true;
        UpdateImageLinkApprovalButtonState();
        ImageLinkApprovalStatusText.Text = "承認直前の再照合とSHA-256検証を行い、確認済み証跡を作成しています…";
        ImageLinkApprovalStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await imageLinkApprovalStore.CreateAsync(
                imageFolder,
                vehicleFolder,
                row.ManifestFileName);
            var updatedRows = ImageLinkMatchesGrid.Items
                .OfType<AbacusImageLinkMatchRow>()
                .Select(item => string.Equals(item.ManifestFileName, row.ManifestFileName, StringComparison.Ordinal)
                    ? item with { ApprovalStatus = "確認済み" }
                    : item)
                .ToList();
            ImageLinkMatchesGrid.ItemsSource = updatedRows;
            ImageLinkApprovalStatusText.Text =
                $"確認済み証跡を作成しました: {result.FilePath}\nSHA-256: {result.Sha256}";
            ImageLinkApprovalStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException or
                                           NotSupportedException)
        {
            ImageLinkApprovalStatusText.Text = $"確認済み証跡の作成に失敗しました: {exception.Message}";
            ImageLinkApprovalStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            imageLinkApprovalBusy = false;
            UpdateImageLinkApprovalButtonState();
        }
    }

    private async void InspectClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        InspectClipboardButton.IsEnabled = false;
        SaveClipboardImageButton.IsEnabled = false;
        inspectedClipboardSequenceNumber = null;
        ImageExportStatusText.Text = "クリップボード診断で標準画像を確認すると保存できます。";
        ImageExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#36465A")!;
        ClipboardInspectionStatusText.Text = "クリップボードの形式と画像寸法を読み取り診断しています…";
        try
        {
            var result = await clipboardInspector.InspectAsync();
            ClipboardFormatsText.Text = result.Formats.Count == 0
                ? "形式: なし"
                : $"形式: {string.Join(" / ", result.Formats)}";
            ClipboardFileDropText.Text = result.FileCount == 0
                ? "ファイル参照: なし"
                : $"ファイル参照: {result.FileCount:N0}件（拡張子: {string.Join("、", result.FileExtensions)}）";

            if (result.HasBitmapImage)
            {
                inspectedClipboardSequenceNumber = result.SequenceNumber;
                SaveClipboardImageButton.IsEnabled = true;
                ClipboardInspectionStatusText.Text =
                    $"標準画像として取得できます（{result.PixelWidth:N0} × {result.PixelHeight:N0}px）。次段階で1件だけ保存する検証へ進めます。";
                ClipboardInspectionStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            }
            else if (result.HasPotentialImageData)
            {
                ClipboardInspectionStatusText.Text =
                    "画像候補の形式はありますが、標準画像には変換できませんでした。表示された形式を基に専用読取方式を検討します。";
                ClipboardInspectionStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#805B10")!;
            }
            else
            {
                ClipboardInspectionStatusText.Text =
                    "画像データを確認できませんでした。ABACUSで画像部分をクリックして選択し、Ctrl+Cの直後にもう一度診断してください。";
                ClipboardInspectionStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            ClipboardInspectionStatusText.Text = $"クリップボード診断に失敗しました: {exception.Message}";
            ClipboardInspectionStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            InspectClipboardButton.IsEnabled = true;
        }
    }

    private async void SaveClipboardImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!inspectedClipboardSequenceNumber.HasValue)
        {
            return;
        }

        AbacusClipboardImage clipboardImage;
        try
        {
            clipboardImage = await clipboardInspector.ReadImageAsync(inspectedClipboardSequenceNumber.Value);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            ImageExportStatusText.Text = $"画像を保存できません: {exception.Message}";
            ImageExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            SaveClipboardImageButton.IsEnabled = false;
            inspectedClipboardSequenceNumber = null;
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "ABACUS画像1件の保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dialog.FolderName));
        if (IsSameOrSubPath(destination, verifiedSourcePath) ||
            IsSameOrSubPath(destination, GetActiveWorkspacePath()))
        {
            MessageBox.Show(
                this,
                "保存用原本または作業用コピーの内部には画像を保存できません。別のフォルダーを選択してください。",
                "保存先を変更してください",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"クリップボードで診断した画像1件をPNGとして新規保存します。\n\n" +
            $"画像寸法: {clipboardImage.Image.PixelWidth:N0} × {clipboardImage.Image.PixelHeight:N0}px\n" +
            $"保存先: {destination}\n\n既存ファイルは上書きしません。続行しますか？",
            "ABACUS画像を1件保存",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        SaveClipboardImageButton.IsEnabled = false;
        ImageExportStatusText.Text = "画像をPNGへ変換し、保存前検証を行っています…";
        try
        {
            var result = await clipboardImageExporter.ExportAsync(clipboardImage.Image, destination);
            lastImageCaptureResult = result;
            lastImageCaptureSourceFolder = verifiedSourcePath;
            lastImageCaptureWorkspaceFolder = GetActiveWorkspacePath();
            lastImageCaptureSourceFingerprint = verifiedOriginalFingerprint;
            lastImageCaptureWorkspaceFingerprint = verifiedWorkspaceFingerprint;
            CreateImageLinkManifestButton.IsEnabled = true;
            ImageLinkManifestStatusText.Text = "画像を保存しました。車両画面で確認した識別子を入力して、紐付け準備を作成してください。";
            ImageLinkManifestStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            ImageExportStatusText.Text =
                $"画像1件を保存しました。\n{result.FilePath}\n" +
                $"{result.PixelWidth:N0} × {result.PixelHeight:N0}px / {FormatFileSize(result.FileSize)} / SHA-256: {result.Sha256}";
            ImageExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or NotSupportedException or ArgumentException)
        {
            ImageExportStatusText.Text = $"画像の保存に失敗しました: {exception.Message}";
            ImageExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            SaveClipboardImageButton.IsEnabled = true;
        }
    }

    private async void InspectAbacusButton_Click(object sender, RoutedEventArgs e)
    {
        SetAbacusButtonsBusy(true);
        try
        {
            RenderAbacusResult(await session.InspectAbacusAsync());
        }
        catch (Exception exception)
        {
            AutomationResultText.Text = $"再認識に失敗しました: {exception.Message}";
        }
        finally
        {
            SetAbacusButtonsBusy(false);
        }
    }

    private async void CloseAbacusButton_Click(object sender, RoutedEventArgs e)
    {
        SetAbacusButtonsBusy(true);
        try
        {
            var result = await session.CloseAbacusAsync();
            AutomationResultText.Text = result.Message;
            abacusMayBeRunning = result.IsRunning;
        }
        catch (Exception exception)
        {
            AutomationResultText.Text = $"終了確認に失敗しました: {exception.Message}";
        }
        finally
        {
            SetAbacusButtonsBusy(false);
        }
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e) => operationCancellation?.Cancel();

    private void Session_StateChanged(object? sender, LegacyHostSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Render(snapshot));
            return;
        }

        Render(snapshot);
    }

    private void Render(LegacyHostSnapshot snapshot)
    {
        var (label, background, foreground) = snapshot.State switch
        {
            LegacyHostState.Starting => ("起動中", "#FFF4D6", "#805B10"),
            LegacyHostState.Connected => ("接続済み", "#DDF7E8", "#17643A"),
            LegacyHostState.Stopping => ("停止中…", "#FFF4D6", "#805B10"),
            LegacyHostState.Failed => ("エラー", "#FDE8E8", "#A61B1B"),
            _ => ("停止中", "#E9EEF5", "#52647A"),
        };

        StatusText.Text = label;
        StatusBadge.Background = (Brush)new BrushConverter().ConvertFromString(background)!;
        StatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!;
        ProcessIdText.Text = snapshot.ProcessId?.ToString() ?? "-";
        ArchitectureText.Text = snapshot.Architecture ?? "未判定";
        DetailText.Text = snapshot.Detail ?? "";

        var busy = snapshot.State is LegacyHostState.Starting or LegacyHostState.Stopping;
        StartButton.IsEnabled = !busy && snapshot.State != LegacyHostState.Connected;
        PingButton.IsEnabled = !busy && snapshot.State == LegacyHostState.Connected;
        StopButton.IsEnabled = !busy && snapshot.State == LegacyHostState.Connected;
    }

    private void RenderInspection(AbacusFolderReport report)
    {
        ValidationResultText.Text = report.IsValid ? "合格（作業用コピーを作成できます）" : "不合格";
        ValidationResultText.Foreground = (Brush)new BrushConverter().ConvertFromString(report.IsValid ? "#17643A" : "#A61B1B")!;
        FileCountText.Text = $"{report.FileCount:N0}ファイル";
        TotalSizeText.Text = $"{FormatFileSize(report.TotalBytes)}（{report.TotalBytes:N0} bytes）";
        SourceArchitectureText.Text = $"FileMaker Runtime / {report.ExecutableArchitecture}";
        FingerprintText.Text = report.FolderFingerprint;
        ValidationMessagesText.Text = string.Join("\n", report.Errors.Concat(report.Warnings));
        CreateWorkspaceButton.IsEnabled = report.IsValid;
        OperationStatusText.Text = report.IsValid
            ? "保存用原本の読取検査が完了しました。ファイルへの書き込みは行っていません。"
            : "検査に合格しませんでした。表示された内容を確認してください。";
        if (string.IsNullOrWhiteSpace(AnalysisPathTextBox.Text))
        {
            AnalysisPathTextBox.Text = report.SourcePath;
        }
        if (string.IsNullOrWhiteSpace(LinkagePathTextBox.Text))
        {
            LinkagePathTextBox.Text = report.SourcePath;
        }
    }

    private void RenderDataAnalysis(AbacusDataAnalysis analysis)
    {
        OverallAnalysisResultText.Text = analysis.IsStructurallyValid
            ? "構造検査に合格しました"
            : "構造エラーがあります";
        OverallAnalysisResultText.Foreground = (Brush)new BrushConverter().ConvertFromString(
            analysis.IsStructurallyValid ? "#17643A" : "#A61B1B")!;
        OverallAnalysisDetailText.Text =
            $"取込候補 {analysis.TotalImportCandidateRows:N0}行 / 顧客名空欄で除外 {analysis.TotalSkippedBlankCustomerRows:N0}行 / 保守的な車両候補 {analysis.ConservativeVehicleCandidates:N0}台";
        SalesAnalysisText.Text = FormatDocumentAnalysis(analysis.Sales);
        MaintenanceAnalysisText.Text = FormatDocumentAnalysis(analysis.Maintenance);
        AnalysisErrorsText.Text = string.Join("\n", analysis.Sales.Errors.Concat(analysis.Maintenance.Errors)
            .Take(20)
            .Select(error => $"{error.RowNumber?.ToString() ?? "ファイル"}: {error.Message}"));
        AnalysisStatusText.Text = analysis.IsStructurallyValid
            ? "分析が完了しました。ファイルへの書き込みは行っていません。"
            : "分析が完了しました。構造エラーのため、このファイルは取込対象にできません。";
    }

    private static string FormatDocumentAnalysis(AbacusDocumentAnalysis analysis) =>
        $"全行: {analysis.TotalRows:N0}\n" +
        $"列形式: {analysis.ExpectedColumns}列\n" +
        $"構造正常: {analysis.StructurallyValidRows:N0}\n" +
        $"取込候補: {analysis.ImportCandidateRows:N0}\n" +
        $"顧客名空欄で除外: {analysis.SkippedBlankCustomerRows:N0}\n" +
        $"車両情報なし: {analysis.CandidateRowsWithoutVehicle:N0}\n" +
        $"書類番号重複: {analysis.DuplicateDocumentNumberRows:N0}\n" +
        $"車両候補: {analysis.ConservativeVehicleCandidates:N0}\n" +
        $"既知の内部区切り: {analysis.LegacyControlCharacterCount:N0}";

    private void ResetDataAnalysis()
    {
        AnalysisStatusText.Text = "未分析";
        OverallAnalysisResultText.Text = "分析結果はまだありません。";
        OverallAnalysisDetailText.Text = "";
        SalesAnalysisText.Text = "未分析";
        MaintenanceAnalysisText.Text = "未分析";
        AnalysisErrorsText.Text = "";
    }

    private void RenderLinkagePlan(AbacusLinkagePlan plan)
    {
        LinkageResultText.Text = plan.IsValid ? "紐付け候補を安全側で作成しました" : "構造エラーがあります";
        LinkageResultText.Foreground = (Brush)new BrushConverter().ConvertFromString(plan.IsValid ? "#17643A" : "#A61B1B")!;
        LinkageSummaryText.Text =
            $"取込候補書類: {plan.ImportCandidateDocuments:N0} / 顧客名空欄で除外: {plan.SkippedBlankCustomerDocuments:N0}\n" +
            $"顧客候補: {plan.CustomerCandidates:N0} / 車両候補: {plan.VehicleCandidates:N0} / 複数車両の顧客候補: {plan.CustomersWithMultipleVehicles:N0}\n" +
            $"同姓同名・情報不一致: {plan.SameNameConflictGroups:N0}グループ（{plan.SameNameConflictDocuments:N0}書類）\n" +
            $"登録番号と車台番号の競合: {plan.VehicleIdentifierConflictGroups:N0} / 複数顧客に現れる車両: {plan.VehiclesLinkedToMultipleCustomers:N0}\n" +
            $"車両情報なし: {plan.DocumentsWithoutVehicleInformation:N0} / 強い車両識別子なし: {plan.DocumentsWithVehicleButWithoutStrongIdentifier:N0}";
        SameNameConflictsGrid.ItemsSource = plan.SameNameConflicts;
        MultipleVehicleCustomersGrid.ItemsSource = plan.MultipleVehicleCustomers;
        VehicleConflictsGrid.ItemsSource = plan.VehicleConflicts;
        LinkageErrorsText.Text = string.Join("\n", plan.Errors.Take(20)
            .Select(error => $"{error.RowNumber?.ToString() ?? "ファイル"}: {error.Message}"));
        LinkageStatusText.Text = plan.IsValid
            ? "プレビューを作成しました。データの登録・更新は行っていません。"
            : "構造エラーのため紐付け候補を使用できません。";
        ApplyLinkageFilter();
    }

    private void ApplyLinkageFilter()
    {
        var filter = LinkageFilterTextBox.Text.Trim();
        ApplyFilter(SameNameConflictsGrid.ItemsSource, item => item is AbacusSameNameConflictPreview preview &&
            (preview.CustomerName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
             preview.EvidenceSummary.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
             preview.Reason.Contains(filter, StringComparison.CurrentCultureIgnoreCase)));
        ApplyFilter(MultipleVehicleCustomersGrid.ItemsSource, item => item is AbacusMultipleVehiclePreview preview &&
            (preview.CustomerName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
             preview.VehicleSummary.Contains(filter, StringComparison.CurrentCultureIgnoreCase)));
        ApplyFilter(VehicleConflictsGrid.ItemsSource, item => item is AbacusVehicleConflictPreview preview &&
            (preview.Identifier.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
             preview.CustomerNames.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
             preview.Reason.Contains(filter, StringComparison.CurrentCultureIgnoreCase)));

        void ApplyFilter(System.Collections.IEnumerable? source, Predicate<object> predicate)
        {
            if (source is null)
            {
                return;
            }

            var view = CollectionViewSource.GetDefaultView(source);
            view.Filter = string.IsNullOrEmpty(filter) ? null : predicate;
            view.Refresh();
        }
    }

    private void ResetLinkagePreview()
    {
        linkagePlan = null;
        LinkageStatusText.Text = "未作成";
        LinkageResultText.Text = "プレビューはまだありません。";
        LinkageSummaryText.Text = "";
        SameNameConflictsGrid.ItemsSource = null;
        MultipleVehicleCustomersGrid.ItemsSource = null;
        VehicleConflictsGrid.ItemsSource = null;
        LinkageErrorsText.Text = "";
    }

    private void RenderAbacusResult(AbacusRuntimeSnapshot result)
    {
        AutomationResultText.Text = result.IsRecognized ? $"成功: {result.Message}" : $"未確認: {result.Message}";
        AutomationResultText.Foreground = (Brush)new BrushConverter().ConvertFromString(result.IsRecognized ? "#17643A" : "#A61B1B")!;
        ProcessArchitectureText.Text = $"LegacyHost {result.HostArchitecture ?? "不明"} → ABACUS {result.TargetArchitecture ?? "不明"}";
        AbacusWindowTitleText.Text = string.IsNullOrWhiteSpace(result.WindowTitle) ? "（タイトルなし）" : result.WindowTitle;
        AutomationElementCountText.Text = result.AutomationElementCount?.ToString("N0") ?? "-";
        abacusMayBeRunning = result.IsRunning;
        if (!abacusMayBeRunning)
        {
            ClearDiagnosedImageWindow();
        }
    }

    private void BeginOperation(string status)
    {
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        OperationStatusText.Text = status;
        OperationProgressBar.IsIndeterminate = false;
        OperationProgressBar.Value = 0;
        CancelOperationButton.IsEnabled = true;
        SelectSourceButton.IsEnabled = false;
        SourcePathTextBox.IsEnabled = false;
        InspectFolderButton.IsEnabled = false;
        CreateWorkspaceButton.IsEnabled = false;
    }

    private void EndOperation()
    {
        CancelOperationButton.IsEnabled = false;
        OperationProgressBar.IsIndeterminate = false;
        SelectSourceButton.IsEnabled = true;
        SourcePathTextBox.IsEnabled = true;
        InspectFolderButton.IsEnabled = true;
        CreateWorkspaceButton.IsEnabled = sourceReport?.IsValid == true && workspaceResult is null;
        operationCancellation?.Dispose();
        operationCancellation = null;
    }

    private void SetProgress(int completed, int total)
    {
        OperationProgressBar.Maximum = Math.Max(total, 1);
        OperationProgressBar.Value = Math.Min(completed, Math.Max(total, 1));
    }

    private void SetAbacusButtonsBusy(bool busy)
    {
        var canLaunch = GetActiveWorkspacePath() is not null && !abacusMayBeRunning;
        LaunchAbacusButton.IsEnabled = !busy && canLaunch;
        InspectAbacusButton.IsEnabled = !busy && abacusMayBeRunning;
        CloseAbacusButton.IsEnabled = !busy && abacusMayBeRunning;
        ImageLaunchAbacusButton.IsEnabled = !busy && canLaunch;
        InspectImageUiButton.IsEnabled = !busy && abacusMayBeRunning;
        InspectAbacusMenuButton.IsEnabled = !busy && abacusMayBeRunning;
        InspectAbacusNativeWindowsButton.IsEnabled = !busy && abacusMayBeRunning;
        InspectAbacusNavigationButton.IsEnabled = !busy && abacusMayBeRunning;
        InspectAbacusAutomationButton.IsEnabled = !busy && abacusMayBeRunning;
    }

    private void ResetInspection(bool clearPath = true)
    {
        if (clearPath)
        {
            SourcePathTextBox.Clear();
        }

        sourceReport = null;
        workspaceResult = null;
        verifiedWorkspacePath = null;
        verifiedSourcePath = null;
        verifiedOriginalFingerprint = null;
        verifiedWorkspaceFingerprint = null;
        ValidationResultText.Text = "未検査";
        FileCountText.Text = "-";
        TotalSizeText.Text = "-";
        SourceArchitectureText.Text = "-";
        FingerprintText.Text = "-";
        ValidationMessagesText.Text = "";
        WorkspacePathText.Text = "作業用コピーはまだありません。";
        CreateWorkspaceButton.IsEnabled = false;
        LaunchAbacusButton.IsEnabled = false;
        ImageLaunchAbacusButton.IsEnabled = false;
        ClearDiagnosedImageWindow();
    }

    private void ClearDiagnosedImageWindow()
    {
        diagnosedImageWindowHandle = null;
        diagnosedAbacusProcessId = null;
        CaptureImageWindowButton.IsEnabled = false;
    }

    private void ResetImageLinkCapture()
    {
        lastImageCaptureResult = null;
        lastImageCaptureSourceFolder = null;
        lastImageCaptureWorkspaceFolder = null;
        lastImageCaptureSourceFingerprint = null;
        lastImageCaptureWorkspaceFingerprint = null;
        CreateImageLinkManifestButton.IsEnabled = false;
        ImageLinkChassisNumberTextBox.Clear();
        ImageLinkRegistrationNumberTextBox.Clear();
        ImageLinkCustomerNameTextBox.Clear();
        ImageLinkManifestStatusText.Text = "画像を1件保存すると、車両識別子を入力してマニフェストを作成できます。";
        ImageLinkManifestStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#36465A")!;
        ResetImageLinkMatch();
    }

    private void ResetBulkImagePreparationState()
    {
        bulkImagePreparationResult = null;
        BulkImageCandidatesGrid.ItemsSource = null;
        OpenBulkImagePackageButton.IsEnabled = false;
        BulkImageStatusText.Text = "先にABACUSインポート画面で保存用フォルダーを選択してください。";
        BulkImageStatusText.Foreground = ToBrush("#52647A");
    }

    private void UpdateBulkImagePreparationButtonState()
    {
        PrepareBulkImagesButton.IsEnabled =
            !bulkImagePreparationBusy &&
            !string.IsNullOrWhiteSpace(BulkImageSourcePathTextBox.Text) &&
            Directory.Exists(BulkImageSourcePathTextBox.Text.Trim()) &&
            !string.IsNullOrWhiteSpace(BulkImageDestinationTextBox.Text) &&
            Directory.Exists(BulkImageDestinationTextBox.Text.Trim());
        SelectBulkImageDestinationButton.IsEnabled = !bulkImagePreparationBusy;
    }

    private void ResetImageLinkMatch(bool clearPaths = true)
    {
        if (clearPaths)
        {
            ImageLinkManifestFolderTextBox.Clear();
            ImageLinkVehicleExportFolderTextBox.Clear();
        }

        ImageLinkMatchesGrid.ItemsSource = null;
        ImageLinkMatchesGrid.SelectedIndex = -1;
        ImageLinkMatchSummaryText.Text = "";
        ImageLinkMatchStatusText.Text = "画像保存先と車両一覧CSVフォルダーを選択してください。";
        ImageLinkMatchStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#36465A")!;
        ImageLinkApprovalStatusText.Text = "照合結果から一意の候補を選択すると、目視確認済みとして記録できます。";
        ImageLinkApprovalStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#36465A")!;
        ResetImageRegistrationPreview(clearDestination: clearPaths);
        ResetWebImportPreview(clearPaths: true);
        ResetWebImportMapping(clearPaths: true);
        UpdateImageLinkMatchButtonState();
    }

    private void SelectImageRegistrationDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "登録前候補パッケージの保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImageRegistrationDestinationTextBox.Text = dialog.FolderName;
        ResetImageRegistrationPreview(clearDestination: false);
        ImageRegistrationPreviewStatusText.Text =
            "登録前パッケージ保存先を選択しました。確認済み証跡と車両一覧CSVを確認してください。";
        UpdateImageRegistrationPreviewButtonState();
    }

    private async void CreateImageRegistrationPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        var approvalFolder = ImageLinkManifestFolderTextBox.Text.Trim();
        var vehicleFolder = ImageLinkVehicleExportFolderTextBox.Text.Trim();
        var destinationFolder = ImageRegistrationDestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(approvalFolder) ||
            string.IsNullOrWhiteSpace(vehicleFolder) ||
            string.IsNullOrWhiteSpace(destinationFolder))
        {
            MessageBox.Show(
                this,
                "確認済み証跡保存先、車両一覧CSVフォルダー、登録前パッケージ保存先を選択してください。",
                "保存先がありません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        imageRegistrationPreviewBusy = true;
        UpdateImageRegistrationPreviewButtonState();
        SelectImageRegistrationDestinationButton.IsEnabled = false;
        ImageRegistrationPreviewGrid.ItemsSource = null;
        ImageRegistrationPreviewResultText.Text = "";
        ImageRegistrationPreviewStatusText.Text =
            "確認済み証跡・画像・車両一覧CSVを再検証し、登録前候補パッケージを作成しています…";
        ImageRegistrationPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await imageRegistrationPreviewStore.CreateAsync(
                approvalFolder,
                vehicleFolder,
                destinationFolder);
            ImageRegistrationPreviewGrid.ItemsSource = result.Candidates;
            ImageRegistrationPreviewStatusText.Text =
                "登録前候補パッケージを作成しました。顧客・車両・画像の登録はまだ行っていません。";
            ImageRegistrationPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            ImageRegistrationPreviewResultText.Text =
                $"保存先: {result.PackagePath}\n" +
                $"マニフェスト: {result.ManifestPath}\n" +
                $"候補: {result.CandidateCount:N0}件 / 画像: {result.ImageCount:N0}件\n" +
                $"マニフェスト SHA-256: {result.ManifestSha256}";
            WebImportSourcePackageTextBox.Text = result.PackagePath;
            ResetWebImportPreview(clearPaths: false);
            WebImportPreviewStatusText.Text =
                "Gate5Jの候補パッケージを入力に設定しました。候補保存先を選択するとWeb CSV候補を作成できます。";
            UpdateWebImportPreviewButtonState();
            WebImportMappingSourceTextBox.Text = result.PackagePath;
            ResetWebImportMapping(clearPaths: false);
            WebImportMappingStatusText.Text =
                "Gate5Jの候補パッケージを対応付け確認の入力に設定しました。候補を読み込んでください。";
            UpdateWebImportMappingButtonState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException or
                                           NotSupportedException)
        {
            ImageRegistrationPreviewGrid.ItemsSource = null;
            ImageRegistrationPreviewResultText.Text = "";
            ImageRegistrationPreviewStatusText.Text =
                $"登録前候補パッケージを作成できません: {exception.Message}";
            ImageRegistrationPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            imageRegistrationPreviewBusy = false;
            SelectImageRegistrationDestinationButton.IsEnabled = true;
            UpdateImageRegistrationPreviewButtonState();
        }
    }

    private void SelectWebImportSourcePackageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Webインポート候補の入力パッケージを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        WebImportSourcePackageTextBox.Text = dialog.FolderName;
        ResetWebImportPreview(clearPaths: false);
        ResetWebImportMapping(clearPaths: true);
        WebImportPreviewStatusText.Text =
            "入力候補パッケージを選択しました。候補保存先を選択してください。";
        UpdateWebImportPreviewButtonState();
    }

    private void SelectWebImportDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Webインポート候補の保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        WebImportDestinationTextBox.Text = dialog.FolderName;
        ResetWebImportPreview(clearPaths: false);
        ResetWebImportMapping(clearPaths: true);
        WebImportPreviewStatusText.Text =
            "候補保存先を選択しました。入力パッケージを確認して作成できます。";
        UpdateWebImportPreviewButtonState();
    }

    private async void CreateWebImportPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        var sourcePackage = WebImportSourcePackageTextBox.Text.Trim();
        var destinationFolder = WebImportDestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sourcePackage) || string.IsNullOrWhiteSpace(destinationFolder))
        {
            MessageBox.Show(
                this,
                "入力候補パッケージと候補保存先を選択してください。",
                "保存先がありません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        webImportPreviewBusy = true;
        UpdateWebImportPreviewButtonState();
        SelectWebImportSourcePackageButton.IsEnabled = false;
        SelectWebImportDestinationButton.IsEnabled = false;
        WebImportPreviewGrid.ItemsSource = null;
        WebImportPreviewResultText.Text = "";
        WebImportPreviewStatusText.Text =
            "登録前候補パッケージを再検証し、Webインポート用CSV候補を作成しています…";
        WebImportPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await webImportPreviewStore.CreateAsync(sourcePackage, destinationFolder);
            WebImportPreviewGrid.ItemsSource = result.Candidates;
            WebImportPreviewStatusText.Text =
                "Webインポート用CSV候補を作成しました。登録・API送信・画像アップロードはまだ行っていません。";
            WebImportPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            WebImportPreviewResultText.Text =
                $"保存先: {result.PackagePath}\n" +
                $"顧客CSV: {result.CustomersCsvPath}\n" +
                $"車両CSV: {result.VehiclesCsvPath}\n" +
                $"画像対応表: {result.ImageAttachmentsPath}\n" +
                $"候補: {result.CandidateCount:N0}件 / 顧客行: {result.CustomerRowCount:N0}行 / " +
                $"車両行: {result.VehicleRowCount:N0}行 / 画像: {result.ImageCount:N0}件\n" +
                $"同じ顧客名の手動確認グループ: {result.SameNameGroupCount:N0}件\n" +
                $"マニフェスト SHA-256: {result.ManifestSha256}";
            WebImportMappingSourceTextBox.Text = result.PackagePath;
            ResetWebImportMapping(clearPaths: false);
            WebImportMappingStatusText.Text =
                "Gate5KのWebインポート候補を対応付け確認の入力に設定しました。候補を読み込んでください。";
            UpdateWebImportMappingButtonState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException or
                                           NotSupportedException)
        {
            WebImportPreviewGrid.ItemsSource = null;
            WebImportPreviewResultText.Text = "";
            WebImportPreviewStatusText.Text =
                $"Webインポート用CSV候補を作成できません: {exception.Message}";
            WebImportPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            webImportPreviewBusy = false;
            SelectWebImportSourcePackageButton.IsEnabled = true;
            SelectWebImportDestinationButton.IsEnabled = true;
            UpdateWebImportPreviewButtonState();
        }
    }

    private void SelectWebImportMappingSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "顧客・車両対応付けの入力パッケージを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        WebImportMappingSourceTextBox.Text = dialog.FolderName;
        ResetWebImportMapping(clearPaths: false);
        WebImportMappingStatusText.Text =
            "入力候補パッケージを選択しました。「候補を読み込む」を押してください。";
        UpdateWebImportMappingButtonState();
    }

    private void SelectWebImportMappingDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "顧客・車両対応付け証跡の保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        WebImportMappingDestinationTextBox.Text = dialog.FolderName;
        ResetWebImportMapping(clearPaths: false);
        WebImportMappingStatusText.Text =
            "対応付け証跡の保存先を選択しました。候補を読み込んでください。";
        UpdateWebImportMappingButtonState();
    }

    private async void ReadWebImportMappingCandidatesButton_Click(object sender, RoutedEventArgs e)
    {
        var sourcePackage = WebImportMappingSourceTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sourcePackage))
        {
            MessageBox.Show(
                this,
                "入力候補パッケージを選択してください。",
                "入力先がありません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        webImportMappingBusy = true;
        SetImageLinkMatchControlsBusy(imageLinkMatchBusy);
        UpdateWebImportMappingButtonState();
        SelectWebImportMappingSourceButton.IsEnabled = false;
        SelectWebImportMappingDestinationButton.IsEnabled = false;
        WebImportMappingGrid.ItemsSource = null;
        WebImportMappingResultText.Text = "";
        WebImportMappingStatusText.Text =
            "候補CSV・画像・マニフェストのSHA-256を再検証しています…";
        WebImportMappingStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            loadedWebImportMappingPackage = await webImportMappingStore.ReadPackageAsync(sourcePackage);
            webImportMappingRows = loadedWebImportMappingPackage.Candidates
                .Select(candidate => new WebImportMappingRow(candidate))
                .ToList();
            WebImportMappingGrid.ItemsSource = webImportMappingRows;
            WebImportMappingStatusText.Text =
                $"候補を{loadedWebImportMappingPackage.CandidateCount:N0}件読み込みました。顧客グループキーを確認・編集してください。";
            WebImportMappingStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException or
                                           NotSupportedException)
        {
            loadedWebImportMappingPackage = null;
            webImportMappingRows = [];
            WebImportMappingGrid.ItemsSource = null;
            WebImportMappingStatusText.Text =
                $"候補の読み込みに失敗しました: {exception.Message}";
            WebImportMappingStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            webImportMappingBusy = false;
            SetImageLinkMatchControlsBusy(imageLinkMatchBusy);
            UpdateWebImportMappingButtonState();
        }
    }

    private async void CreateWebImportMappingApprovalButton_Click(object sender, RoutedEventArgs e)
    {
        var sourcePackage = WebImportMappingSourceTextBox.Text.Trim();
        var destinationFolder = WebImportMappingDestinationTextBox.Text.Trim();
        if (loadedWebImportMappingPackage is null ||
            string.IsNullOrWhiteSpace(sourcePackage) ||
            string.IsNullOrWhiteSpace(destinationFolder) ||
            webImportMappingRows.Count == 0)
        {
            MessageBox.Show(
                this,
                "先に入力候補を読み込み、対応付け証跡の保存先を選択してください。",
                "対応付け候補がありません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "表示中の顧客グループキーで対応付けを確定しますか？\n\n" +
            "同じキーの行は同一顧客の複数車両として扱う予定になります。\n" +
            "この操作は確認済み証跡JSONを作成するだけで、顧客・車両・画像の登録やAPI送信は行いません。",
            "対応付けの確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        webImportMappingBusy = true;
        SetImageLinkMatchControlsBusy(imageLinkMatchBusy);
        UpdateWebImportMappingButtonState();
        SelectWebImportMappingSourceButton.IsEnabled = false;
        SelectWebImportMappingDestinationButton.IsEnabled = false;
        CreateWebImportMappingApprovalButton.IsEnabled = false;
        WebImportMappingResultText.Text = "";
        WebImportMappingStatusText.Text =
            "対応付け直前の再検証と顧客名の整合性確認を行っています…";
        WebImportMappingStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var selections = webImportMappingRows
                .Select(row => new AbacusWebImportMappingSelection(row.CandidateId, row.CustomerGroupKey))
                .ToArray();
            var result = await webImportMappingStore.CreateApprovalAsync(
                sourcePackage,
                destinationFolder,
                selections);
            WebImportMappingStatusText.Text =
                "顧客・車両対応付けの確認済み証跡を作成しました。登録・API送信・画像アップロードはまだ行っていません。";
            WebImportMappingStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            WebImportMappingResultText.Text =
                $"保存先: {result.MappingPackagePath}\n" +
                $"マニフェスト: {result.MappingManifestPath}\n" +
                $"候補: {result.CandidateCount:N0}件 / 顧客グループ: {result.CustomerGroupCount:N0}件\n" +
                $"複数車両としてまとめた車両: {result.GroupedVehicleCount:N0}件\n" +
                $"同じ顧客名の確認グループ: {result.SameNameGroupCount:N0}件\n" +
                (result.Warnings.Count == 0 ? "警告: なし" : $"警告:\n{string.Join("\n", result.Warnings)}") +
                $"\nマニフェスト SHA-256: {result.MappingManifestSha256}";
            WebImportRegistrationSourceTextBox.Text = result.MappingPackagePath;
            ResetWebImportRegistration(clearPaths: false);
            WebImportRegistrationStatusText.Text =
                "Gate5Lの対応付け証跡を入力に設定しました。保存先を選択して登録前パッケージを作成してください。";
            UpdateWebImportRegistrationButtonState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException or
                                           NotSupportedException)
        {
            WebImportMappingResultText.Text = "";
            WebImportMappingStatusText.Text =
                $"対応付け証跡を作成できません: {exception.Message}";
            WebImportMappingStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            webImportMappingBusy = false;
            SetImageLinkMatchControlsBusy(imageLinkMatchBusy);
            UpdateWebImportMappingButtonState();
        }
    }

    private void SelectWebImportRegistrationSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Web登録前パッケージの対応付け証跡を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        WebImportRegistrationSourceTextBox.Text = dialog.FolderName;
        ResetWebImportRegistration(clearPaths: false);
        WebImportRegistrationStatusText.Text =
            "対応付け証跡を選択しました。保存先を選択して登録前パッケージを作成してください。";
        UpdateWebImportRegistrationButtonState();
    }

    private void SelectWebImportRegistrationDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Web登録前パッケージの保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        WebImportRegistrationDestinationTextBox.Text = dialog.FolderName;
        ResetWebImportRegistration(clearPaths: false);
        WebImportRegistrationStatusText.Text =
            "登録前パッケージ保存先を選択しました。対応付け証跡を確認して作成できます。";
        UpdateWebImportRegistrationButtonState();
    }

    private async void CreateWebImportRegistrationPackageButton_Click(object sender, RoutedEventArgs e)
    {
        var mappingPackage = WebImportRegistrationSourceTextBox.Text.Trim();
        var destinationFolder = WebImportRegistrationDestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(mappingPackage) || string.IsNullOrWhiteSpace(destinationFolder))
        {
            MessageBox.Show(
                this,
                "対応付け証跡と登録前パッケージ保存先を選択してください。",
                "保存先がありません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "Gate5Lで確認済みの顧客グループから、登録前パッケージを作成しますか？\n\n" +
            "同じ顧客グループの複数車両は、顧客CSVを1行・車両CSVを複数行で出力します。\n" +
            "この操作ではWeb API、DB、画像アップロードは行いません。",
            "登録前パッケージの作成",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        webImportRegistrationBusy = true;
        SetImageLinkMatchControlsBusy(imageLinkMatchBusy);
        UpdateWebImportRegistrationButtonState();
        SelectWebImportRegistrationSourceButton.IsEnabled = false;
        SelectWebImportRegistrationDestinationButton.IsEnabled = false;
        WebImportRegistrationGrid.ItemsSource = null;
        WebImportRegistrationResultText.Text = "";
        WebImportRegistrationStatusText.Text =
            "対応付け証跡・入力候補・画像のSHA-256を再検証し、登録前パッケージを作成しています…";
        WebImportRegistrationStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await webImportRegistrationPackageStore.CreateAsync(
                mappingPackage,
                destinationFolder);
            WebImportRegistrationGrid.ItemsSource = result.Groups;
            WebImportRegistrationStatusText.Text =
                "Web登録前パッケージを作成しました。登録・API送信・画像アップロードはまだ行っていません。";
            WebImportRegistrationStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            WebImportRegistrationResultText.Text =
                $"保存先: {result.PackagePath}\n" +
                $"マニフェスト: {result.ManifestPath}\n" +
                $"顧客CSV: {result.CustomersCsvPath}\n" +
                $"車両CSV: {result.VehiclesCsvPath}\n" +
                $"画像対応表: {result.ImageAttachmentsPath}\n" +
                $"候補: {result.CandidateCount:N0}件 / 顧客行: {result.CustomerRowCount:N0}行 / " +
                $"車両行: {result.VehicleRowCount:N0}行 / 画像: {result.ImageCount:N0}件\n" +
                $"複数車両としてまとめた車両: {result.MergedVehicleCount:N0}件\n" +
                $"マニフェスト SHA-256: {result.ManifestSha256}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException or
                                           NotSupportedException)
        {
            WebImportRegistrationGrid.ItemsSource = null;
            WebImportRegistrationResultText.Text = "";
            WebImportRegistrationStatusText.Text =
                $"Web登録前パッケージを作成できません: {exception.Message}";
            WebImportRegistrationStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
        }
        finally
        {
            webImportRegistrationBusy = false;
            SetImageLinkMatchControlsBusy(imageLinkMatchBusy);
            UpdateWebImportRegistrationButtonState();
        }
    }

    private void ResetImageRegistrationPreview(bool clearDestination)
    {
        if (clearDestination)
        {
            ImageRegistrationDestinationTextBox.Clear();
        }

        ImageRegistrationPreviewGrid.ItemsSource = null;
        ImageRegistrationPreviewResultText.Text = "";
        ImageRegistrationPreviewStatusText.Text =
            "画像保存先・車両一覧CSVフォルダー・パッケージ保存先を選択してください。";
        ImageRegistrationPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#36465A")!;
        UpdateImageRegistrationPreviewButtonState();
    }

    private void ResetWebImportPreview(bool clearPaths)
    {
        if (clearPaths)
        {
            WebImportSourcePackageTextBox.Clear();
            WebImportDestinationTextBox.Clear();
        }

        WebImportPreviewGrid.ItemsSource = null;
        WebImportPreviewResultText.Text = "";
        WebImportPreviewStatusText.Text =
            "入力候補パッケージと候補保存先を選択してください。";
        WebImportPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#36465A")!;
        UpdateWebImportPreviewButtonState();
    }

    private void ResetWebImportMapping(bool clearPaths)
    {
        if (clearPaths)
        {
            WebImportMappingSourceTextBox.Clear();
            WebImportMappingDestinationTextBox.Clear();
        }

        loadedWebImportMappingPackage = null;
        webImportMappingRows = [];
        WebImportMappingGrid.ItemsSource = null;
        WebImportMappingResultText.Text = "";
        WebImportMappingStatusText.Text =
            "入力候補パッケージを選択して候補を読み込んでください。";
        WebImportMappingStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#36465A")!;
        UpdateWebImportMappingButtonState();
        ResetWebImportRegistration(clearPaths: true);
    }

    private void UpdateWebImportMappingButtonState()
    {
        ReadWebImportMappingCandidatesButton.IsEnabled =
            !webImportMappingBusy &&
            !imageLinkMatchBusy &&
            !webImportRegistrationBusy &&
            !string.IsNullOrWhiteSpace(WebImportMappingSourceTextBox.Text);
        CreateWebImportMappingApprovalButton.IsEnabled =
            !webImportMappingBusy &&
            !imageLinkMatchBusy &&
            !webImportRegistrationBusy &&
            loadedWebImportMappingPackage is not null &&
            webImportMappingRows.Count > 0 &&
            !string.IsNullOrWhiteSpace(WebImportMappingDestinationTextBox.Text);
    }

    private void ResetWebImportRegistration(bool clearPaths)
    {
        if (clearPaths)
        {
            WebImportRegistrationSourceTextBox.Clear();
            WebImportRegistrationDestinationTextBox.Clear();
        }

        WebImportRegistrationGrid.ItemsSource = null;
        WebImportRegistrationResultText.Text = "";
        WebImportRegistrationStatusText.Text =
            "対応付け証跡と保存先を選択してください。";
        WebImportRegistrationStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#36465A")!;
        UpdateWebImportRegistrationButtonState();
    }

    private void UpdateWebImportRegistrationButtonState()
    {
        CreateWebImportRegistrationPackageButton.IsEnabled =
            !webImportRegistrationBusy &&
            !imageLinkMatchBusy &&
            !webImportMappingBusy &&
            !string.IsNullOrWhiteSpace(WebImportRegistrationSourceTextBox.Text) &&
            !string.IsNullOrWhiteSpace(WebImportRegistrationDestinationTextBox.Text);
    }

    private void UpdateWebImportPreviewButtonState()
    {
        CreateWebImportPreviewButton.IsEnabled =
            !webImportPreviewBusy &&
            !imageLinkMatchBusy &&
            !imageRegistrationPreviewBusy &&
            !webImportMappingBusy &&
            !webImportRegistrationBusy &&
            !string.IsNullOrWhiteSpace(WebImportSourcePackageTextBox.Text) &&
            !string.IsNullOrWhiteSpace(WebImportDestinationTextBox.Text);
    }

    private void UpdateImageRegistrationPreviewButtonState()
    {
        CreateImageRegistrationPreviewButton.IsEnabled =
            !imageRegistrationPreviewBusy &&
            !imageLinkMatchBusy &&
            !webImportPreviewBusy &&
            !webImportMappingBusy &&
            !webImportRegistrationBusy &&
            !string.IsNullOrWhiteSpace(ImageLinkManifestFolderTextBox.Text) &&
            !string.IsNullOrWhiteSpace(ImageLinkVehicleExportFolderTextBox.Text) &&
            !string.IsNullOrWhiteSpace(ImageRegistrationDestinationTextBox.Text);
    }

    private string? GetActiveWorkspacePath() =>
        workspaceResult?.WorkspacePath ?? verifiedWorkspacePath;

    private static bool IsSameOrSubPath(string candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:N2} {units[unit]}";
    }

    private sealed record LegacyGraphDocumentDragPayload(
        AbacusLegacyExportCandidateGraphDocument Document,
        string SourceKind);

    private sealed record LegacyGraphVehicleNodeDragPayload(string VehicleId);

    private sealed record LegacyGraphUnresolvedVehicleDragPayload(string VehicleId);

    private sealed record LegacyGraphCustomerDragPayload(string CustomerId);

    private sealed record LegacyGraphTrashCustomerDragPayload(string CustomerId);

    private sealed record LegacyGraphTrashImpact(
        int CustomerCount,
        int VehicleCount,
        int DocumentCount,
        int ImageCount);

    private sealed record LegacyGraphDocumentNodeMarker;

    private sealed record LegacyGraphDocumentDisconnectMarker;

    private sealed record LegacyGraphVehicleNodeMarker;

    private sealed record LegacyGraphCustomerMergeField(
        string Key,
        string Label,
        Func<AbacusLegacyExportCandidateGraphCustomer, string> ValueSelector);

    private sealed record LegacyGraphMergeFieldOption(
        string CustomerId,
        string CustomerName,
        string Value)
    {
        public string DisplayText => $"{CustomerName}: {Fallback(Value)}";
    }

    private sealed record LegacyGraphCustomerMergeDraft(
        string GroupKey,
        IReadOnlyList<string> CandidateCustomerIds,
        IReadOnlyDictionary<string, string> FieldSelections,
        IReadOnlyDictionary<string, string> SelectedValues,
        DateTimeOffset SavedAtUtc);

    private sealed record LegacyGraphMergeApprovalEntry(
        LegacyGraphCustomerMergeGroup Group,
        AbacusLegacyExportCandidateGraphCustomer SourceCustomer,
        IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> Candidates,
        bool HasCurrentDraft);

    private sealed class LegacyGraphCustomerMergeGroup(
        string groupId,
        string origin,
        List<string> customerIds)
    {
        public string GroupId { get; } = groupId;

        public string Origin { get; set; } = origin;

        public List<string> CustomerIds { get; } = customerIds;

        public LegacyGraphCustomerMergeGroup Clone() =>
            new(GroupId, Origin, CustomerIds.ToList());
    }

    private sealed record LegacyGraphMergeStateSnapshot(
        IReadOnlyDictionary<string, LegacyGraphCustomerMergeGroup> Groups,
        IReadOnlyDictionary<string, string> GroupByCustomerId,
        IReadOnlyDictionary<string, LegacyGraphCustomerMergeDraft> Drafts,
        IReadOnlySet<string> AppliedGroupKeys,
        IReadOnlyDictionary<string, string> VirtualCustomerMergeKeys,
        IReadOnlyDictionary<string, bool> ExpandedGroups);

    // 一括確定のロールバック対象は、UI選択・展開状態・処理中フラグを含めず、
    // 共通ドメイン状態だけに限定します。処理途中の画面状態を復元して
    // 一括処理の部分適用を隠すことがないよう、ドメインの変更だけを原子的に戻します。
    private sealed class LegacyGraphDomainStateSnapshot
    {
        public AbacusLegacyExportCandidateGraphResult? CandidateGraph { get; init; }

        public IReadOnlyList<AbacusRecommendationCandidate> RecommendationCandidates { get; init; } = [];

        public IReadOnlyDictionary<string, string> RecommendationDecisions { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, LegacyGraphRecommendationState> RecommendationStates { get; init; } =
            new Dictionary<string, LegacyGraphRecommendationState>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> ManualDocumentLinks { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> ManualVehicleCustomerLinks { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> ManualDocumentCustomerLinks { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> DocumentLinkMethods { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> DocumentLinkReasons { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> UnconnectedDocumentKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> TrayDocumentKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> ExcludedDocumentKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> TrayVehicleIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> TrashCustomerIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> TrashVehicleIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> TrashDocumentKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool ImportConfirmed { get; init; }

        public IReadOnlyDictionary<string, LegacyGraphCustomerMergeDraft> CustomerMergeDrafts { get; init; } =
            new Dictionary<string, LegacyGraphCustomerMergeDraft>(StringComparer.Ordinal);

        public IReadOnlySet<string> AppliedCustomerMergeKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> VirtualCustomerMergeKeys { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> CustomerNameOverrides { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, LegacyGraphCustomerMergeGroup> CustomerMergeGroups { get; init; } =
            new Dictionary<string, LegacyGraphCustomerMergeGroup>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> CustomerMergeGroupByCustomerId { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> LogicalCustomerMergeGroupByCustomerId { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, IReadOnlySet<string>> MatchingManualCustomerCandidateTargets { get; init; } =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, bool> CustomerApprovalStates { get; init; } =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> CustomerReviewStates { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyList<LegacyMatchingChangeItem> MatchingChanges { get; init; } = [];
    }

    private sealed class LegacyGraphRuntimeStateSnapshot
    {
        public AbacusLegacyExportCandidateGraphResult? CandidateGraph { get; init; }

        public IReadOnlyList<AbacusRecommendationCandidate> RecommendationCandidates { get; init; } = [];

        public IReadOnlyDictionary<string, string> RecommendationDecisions { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, LegacyGraphRecommendationState> RecommendationStates { get; init; } =
            new Dictionary<string, LegacyGraphRecommendationState>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> ManualDocumentLinks { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> ManualVehicleCustomerLinks { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> ManualDocumentCustomerLinks { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> DocumentLinkMethods { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> DocumentLinkReasons { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> UnconnectedDocumentKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> TrayDocumentKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> ExcludedDocumentKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> TrayVehicleIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> TrashCustomerIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> TrashVehicleIds { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> TrashDocumentKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool ImportConfirmed { get; init; }

        public IReadOnlyDictionary<string, LegacyGraphCustomerMergeDraft> CustomerMergeDrafts { get; init; } =
            new Dictionary<string, LegacyGraphCustomerMergeDraft>(StringComparer.Ordinal);

        public IReadOnlySet<string> AppliedCustomerMergeKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> VirtualCustomerMergeKeys { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> CustomerNameOverrides { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, bool> CustomerGroupExpanded { get; init; } =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, LegacyGraphCustomerMergeGroup> CustomerMergeGroups { get; init; } =
            new Dictionary<string, LegacyGraphCustomerMergeGroup>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> CustomerMergeGroupByCustomerId { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> LogicalCustomerMergeGroupByCustomerId { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, IReadOnlySet<string>> MatchingManualCustomerCandidateTargets { get; init; } =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, bool> CustomerApprovalStates { get; init; } =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> CustomerReviewStates { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public object? SelectedItem { get; init; }

        public string? SelectedWorkGroupKey { get; init; }

        public string UiMode { get; init; } = "graph";

        public string? MatchingCustomerId { get; init; }

        public int MatchingCustomerIndex { get; init; }

        public string MatchingCategory { get; init; } = LegacyMatchingCategoryKinds.Customer;

        public int MatchingRecommendationIndex { get; init; }

        public int MatchingUnresolvedPageIndex { get; init; }

        public bool MatchingDetailsVisible { get; init; }

        public IReadOnlyList<LegacyMatchingChangeItem> MatchingChanges { get; init; } = [];

        public AbacusRecommendationCandidate? SelectedRecommendation { get; init; }

        public bool FinalPackageBusy { get; init; }

        public bool FinalPackageHasError { get; init; }

        public string? FinalPackagePath { get; init; }

        public bool PreparationExpanded { get; init; }
    }

    private sealed record LegacyGraphCustomerListEntry(
        string EntryId,
        bool IsGroupHeader,
        AbacusLegacyExportCandidateGraphCustomer Customer,
        string DisplayName,
        string Summary,
        string AddressSummary,
        string GroupKey,
        string ExpandGlyph,
        Brush BackgroundBrush,
        Brush BorderBrush,
        Brush StatusBrush,
        string StatusText,
        AbacusRecommendationCandidate? MatchingRecommendation = null,
        bool IsMatchingFocus = false)
    {
        public Visibility ExpandButtonVisibility => IsGroupHeader
            ? Visibility.Visible
            : Visibility.Collapsed;

        public GridLength ExpandColumnWidth => IsGroupHeader
            ? new GridLength(28)
            : new GridLength(0);

        public bool IsMatchingEntry => EntryId.StartsWith("matching-", StringComparison.Ordinal);

        public bool IsMatchingGroupChild => EntryId.StartsWith("matching-child:", StringComparison.Ordinal);

        public Visibility MatchingGroupHeaderVisibility => IsGroupHeader
            ? Visibility.Visible
            : Visibility.Collapsed;

        public Visibility MatchingCustomerContentVisibility => IsGroupHeader
            ? Visibility.Collapsed
            : Visibility.Visible;

        public Thickness MatchingContentPadding => IsGroupHeader
            ? new Thickness(6, 3, 6, 3)
            : new Thickness(6);

        public Thickness BlockMargin => IsMatchingGroupChild
            ? new Thickness(24, 3, 1, 3)
            : IsMatchingEntry ||
                                          IsGroupHeader ||
                                          !GroupKey.StartsWith("same-name:", StringComparison.Ordinal) &&
                                          !GroupKey.StartsWith("manual:", StringComparison.Ordinal) &&
                                          !GroupKey.StartsWith("logical:", StringComparison.Ordinal)
            ? new Thickness(1, 3, 1, 3)
            : new Thickness(24, 3, 1, 3);

        public Thickness ContentMargin => IsGroupHeader ||
                                          GroupKey.StartsWith("same-name:", StringComparison.Ordinal) ||
                                          GroupKey.StartsWith("manual:", StringComparison.Ordinal) ||
                                          GroupKey.StartsWith("logical:", StringComparison.Ordinal)
            ? IsGroupHeader ? new Thickness(0) : new Thickness(22, 0, 0, 0)
            : new Thickness(0);
    }

    private sealed record LegacyGraphCustomerReviewSnapshot(
        string CustomerKey,
        int PendingCount,
        int HeldCount,
        bool RequiresCustomerPreview,
        bool CanApprove,
        string Status,
        string Reason);

    private sealed record LegacyMatchingCustomerCategoryReviewSummary(
        string Kind,
        string Label,
        int Total,
        int Pending,
        int Held,
        int Completed);

    private sealed record LegacyGraphCustomerSourceSummary(
        string Title,
        string Details,
        string SourceLabel,
        Brush BackgroundBrush,
        Brush BorderBrush);

    private sealed record LegacyGraphCustomerDifferenceSummary(
        string FieldLabel,
        string CandidateValues,
        string ResultText,
        string StatusText,
        Brush BackgroundBrush,
        Brush BorderBrush,
        Brush StatusBrush,
        bool HasDifference);

    private sealed record LegacyGraphSearchResult(
        object Item,
        string TypeCode,
        string TypeText,
        string Title,
        string Subtitle,
        string StateCode,
        string StateText,
        string MethodCode,
        string MethodText,
        string SearchText,
        Brush StateBrush,
        Brush BackgroundBrush,
        Brush BorderBrush);

    private sealed record LegacyMatchingVehicleCard(
        string Title,
        string Details,
        string DocumentsSummary);

    private sealed record LegacyMatchingDocumentCard(
        string Title,
        string Details);

    private sealed record LegacyMatchingRecommendationItem(
        AbacusRecommendationCandidate Candidate,
        string SubjectText,
        string TargetText,
        string DecisionText,
        string MatchedText,
        string DifferenceText,
        string ConflictText,
        string Reason,
        Brush BackgroundBrush,
        Brush BorderBrush,
        Brush DecisionBrush);

    private sealed record LegacyMatchingDetailRow(
        string FieldLabel,
        string SourceValue,
        string CandidateValue,
        string StatusText,
        Brush StatusBrush,
        string InternalStatusText,
        string ValuesText);

    private sealed record LegacyMatchingAlternativeLine(
        string Symbol,
        string Text,
        Brush Foreground);

    private sealed record LegacyMatchingAlternativeRow(
        string Title,
        string Identifier,
        IReadOnlyList<LegacyMatchingAlternativeLine> ComparisonLines,
        string ConflictText,
        Brush ConflictBrush,
        string Decision,
        string InternalId);

    private sealed record LegacyMatchingEntitySection(
        string Title,
        IReadOnlyList<LegacyMatchingEntityRow> Rows,
        LegacyMatchingExpandableDetail? ExpandableDetail = null)
    {
        public Visibility ExpandableDetailVisibility => ExpandableDetail is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private sealed record LegacyMatchingExpandableDetail(
        string Title,
        string Summary,
        string FullText);

    private sealed record LegacyMatchingEntityRow(
        string Label,
        string Value);

    private sealed record LegacyMatchingInternalInfoRow(
        string Label,
        string DisplayValue,
        string CopyValue);

    private sealed record LegacyMatchingChangeItem(
        string ChangeId,
        string Kind,
        string SubjectId,
        string Title,
        string Details,
        DateTimeOffset CreatedAtUtc,
        string? CustomerId = null,
        string? WorkTargetKey = null,
        string? ExternalCustomerId = null,
        LegacyGraphDetachedUndoState? UndoState = null);

    private sealed record LegacyMatchingUnresolvedItem(
        object Item,
        string CategoryCode,
        string Kind,
        string Title,
        string Details,
        string SearchText,
        string SortKey,
        Brush BackgroundBrush,
        Brush BorderBrush);

    private sealed record LegacyGraphEdge(UIElement Source, UIElement Target, Line Line);

    private sealed record LegacyGraphVehicleChoice(
        string VehicleId,
        string DisplayName,
        string IdentifierSummary,
        string CustomerName);

    private sealed class WebImportMappingRow(AbacusWebImportMappingCandidate candidate)
    {
        public string CandidateId { get; } = candidate.CandidateId;
        public string CustomerName { get; } = candidate.CustomerName;
        public string VehicleName { get; } = candidate.VehicleName;
        public string ChassisNumber { get; } = candidate.ChassisNumber;
        public string RegistrationNumber { get; } = candidate.RegistrationNumber;
        public string CustomerGroupKey { get; set; } = candidate.DefaultCustomerGroupKey;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
        {
            return;
        }

        if (operationCancellation is not null)
        {
            e.Cancel = true;
            operationCancellation.Cancel();
            OperationStatusText.Text = "処理をキャンセルしています。完了表示の後にもう一度画面を閉じてください。";
            return;
        }

        if (closeVerificationInProgress)
        {
            e.Cancel = true;
            return;
        }

        if (abacusMayBeRunning)
        {
            e.Cancel = true;
            closeVerificationInProgress = true;
            IsEnabled = false;
            try
            {
                var result = await session.InspectAbacusAsync();
                RenderAbacusResult(result);
                if (result.IsRunning)
                {
                    MessageBox.Show(
                        this,
                        "コピー側ABACUSが起動中です。先に「ABACUSを閉じる」で終了してから補助ソフトを閉じてください。",
                        "ABACUSが起動中です",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    await FinishCloseAsync();
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    $"ABACUSの終了状態を確認できませんでした。LegacyHostの接続状態を確認して再試行してください。\n\n{exception.Message}",
                    "終了状態を確認できません",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                if (!allowClose)
                {
                    IsEnabled = true;
                    closeVerificationInProgress = false;
                    SetAbacusButtonsBusy(false);
                }
            }
            return;
        }

        e.Cancel = true;
        closeVerificationInProgress = true;
        await FinishCloseAsync();
    }

    private async Task FinishCloseAsync()
    {
        if (!legacyGraphBulkMergeBusy &&
            !legacyGraphFinalPackageBusy &&
            legacyExportCandidateGraphResult is not null &&
            unifiedImportOutputSession is not null)
        {
            try
            {
                // Dispatcherに積まれた自動保存より先に終了しても、最後の画面状態を
                // チェックポイントへ確実に反映してからウィンドウを閉じます。
                await SaveLegacyGraphCheckpointAsync("close");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or ArgumentException or NotSupportedException)
            {
                LegacyGraphWorkStatusText.Text = $"終了前の作業保存に失敗しました: {exception.Message}";
                LegacyGraphWorkStatusText.Foreground = ToBrush("#A61B1B");
            }
        }

        operationCancellation?.Cancel();
        CancelLegacyExportFolderDetection();
        IsEnabled = false;
        await session.DisposeAsync();
        allowClose = true;
        Close();
    }
}
