using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
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
    private readonly LegacyHostSession session = new();
    private readonly AbacusFolderInspector folderInspector = new();
    private readonly AbacusWorkspaceService workspaceService;
    private readonly AbacusClipboardInspector clipboardInspector = new();
    private readonly AbacusClipboardImageExporter clipboardImageExporter = new();
    private readonly AbacusWindowCaptureService windowCaptureService = new();
    private readonly AbacusCaptureCropper captureCropper = new();
    private readonly AbacusDataAnalyzer dataAnalyzer = new(new AbacusTabParser());
    private readonly AbacusLinkagePlanner linkagePlanner = new(new AbacusTabParser());
    private readonly AbacusLegacyExportReader legacyExportReader = new();
    private readonly AbacusLegacyExportFolderDetector legacyExportFolderDetector = new();
    private readonly AbacusLegacyExportPreviewStore legacyExportPreviewStore = new();
    private readonly AbacusLegacyExportPreviewPackageReader legacyExportPackageReader = new();
    private readonly AbacusLegacyExportCandidateGraphService legacyExportCandidateGraphService = new();
    private readonly AbacusFp5Inspector fp5Inspector = new();
    private readonly AbacusFp5CandidateExporter fp5CandidateExporter = new();
    private readonly AbacusImageLinkManifestStore imageLinkManifestStore = new();
    private readonly AbacusImageLinkMatcher imageLinkMatcher = new();
    private readonly AbacusImageLinkApprovalStore imageLinkApprovalStore = new();
    private readonly AbacusImageRegistrationPreviewStore imageRegistrationPreviewStore = new();
    private readonly AbacusWebImportPreviewStore webImportPreviewStore = new();
    private readonly AbacusWebImportMappingStore webImportMappingStore = new();
    private readonly AbacusWebImportRegistrationPackageStore webImportRegistrationPackageStore = new();
    private readonly AbacusLegacyGraphFinalPackageStore legacyGraphFinalPackageStore = new();
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
    private bool allowClose;
    private bool closeVerificationInProgress;
    private bool abacusMayBeRunning;
    private bool imageLinkMatchBusy;
    private bool imageLinkApprovalBusy;
    private bool imageRegistrationPreviewBusy;
    private bool webImportPreviewBusy;
    private bool webImportMappingBusy;
    private bool webImportRegistrationBusy;
    private bool legacyGraphFinalPackageBusy;
    private CancellationTokenSource? legacyExportDetectionCancellation;
    private AbacusWebImportMappingPackage? loadedWebImportMappingPackage;
    private List<WebImportMappingRow> webImportMappingRows = [];
    private AbacusLegacyExportCandidateGraphResult? legacyExportCandidateGraphResult;
    private readonly Dictionary<string, string> legacyGraphManualDocumentLinks = new(StringComparer.OrdinalIgnoreCase);
    // 車両情報を持たない書類を、顧客だけへ一時的に紐付ける状態です。
    // 値は顧客IDではなく統合グループキーを保持し、統合前後でも同じ顧客グループを指します。
    private readonly Dictionary<string, string> legacyGraphManualDocumentCustomerLinks = new(StringComparer.OrdinalIgnoreCase);
    // 書類の配置変更は画面上の一時状態としてのみ保持します。
    // 未接続はキャンバス上で確認中、トレイ移動済みはキャンバスから隠す状態です。
    private readonly HashSet<string> legacyGraphUnconnectedDocumentKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> legacyGraphTrayDocumentKeys = new(StringComparer.OrdinalIgnoreCase);
    // インポート全体の最終確定で除外確定した未確定トレイ書類です。
    // トレイからキャンバスへ戻した場合は最終確定状態とともに無効化します。
    private readonly HashSet<string> legacyGraphExcludedDocumentKeys = new(StringComparer.OrdinalIgnoreCase);
    // 顧客統合・書類紐付けを含む、グラフ全体の最終確定状態です。
    private bool legacyGraphImportConfirmed;
    private readonly Dictionary<string, LegacyGraphCustomerMergeDraft> legacyGraphCustomerMergeDrafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> legacyGraphAppliedCustomerMergeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> legacyGraphVirtualCustomerMergeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> legacyGraphCustomerGroupExpanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LegacyGraphCustomerMergeGroup> legacyGraphCustomerMergeGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> legacyGraphCustomerMergeGroupByCustomerId = new(StringComparer.Ordinal);
    private object? legacyGraphSelectedItem;
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
    private AbacusLegacyExportCandidateGraphDocument? legacyGraphHandleDragDocument;
    private Point legacyGraphHandleDragStartPoint;
    private AbacusLegacyExportCandidateGraphDocument? legacyGraphDocumentCardDragDocument;
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
    private bool legacyGraphNativeDocumentDropTargetValid;
    private readonly List<LegacyGraphEdge> legacyGraphEdges = [];

    public MainWindow()
    {
        InitializeComponent();
        workspaceService = new AbacusWorkspaceService(folderInspector);
        migrationPreviewStore = new AbacusMigrationPreviewStore(dataAnalyzer, linkagePlanner);
        session.StateChanged += Session_StateChanged;
        Closing += MainWindow_Closing;
        Render(session.Snapshot);
    }

    private void MainWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // マウスキャプチャ中は元の要素以外の領域でもWindowまでイベントが届くため、
        // ここでドロップ先判定とカーソルを最後に再適用します。
        if (legacyGraphBlockDragStarted && legacyGraphDocumentCardDragDocument is not null)
        {
            UpdateLegacyGraphBlockDropTarget(e.GetPosition(this));
        }

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
            SourcePathTextBox.Text = dialog.FolderName;
            AnalysisPathTextBox.Text = dialog.FolderName;
            LinkagePathTextBox.Text = dialog.FolderName;
            MigrationSourcePathTextBox.Text = dialog.FolderName;
            ResetLegacyExportImportState();
            Fp5InspectionStatusText.Text = "未診断";
            Fp5InspectionResultText.Text = "";
            Fp5CandidatesGrid.ItemsSource = null;
            fp5Inspection = null;
            ExtractFp5CandidateButton.IsEnabled = false;
            Fp5CandidateExportStatusText.Text = "候補を選択すると、標準JPEG構造を再検証してから1件だけ出力します。内部ブロックと判定した場合は保存しません。";
            ResetImageLinkCapture();
            await AutoDetectAndInspectLegacyExportsAsync(dialog.FolderName);
        }
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
            LegacyExportFilesGrid.ItemsSource = null;
            LegacyExportColumnsGrid.ItemsSource = null;
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
        LegacyExportPathTextBox.Clear();
        LegacyExportAutoDetectStatusText.Text = "ABACUSフォルダーを選択すると、内部のCSVを自動検出します。";
        LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        LegacyExportStatusText.Text = "未診断";
        LegacyExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        LegacyExportFilesGrid.ItemsSource = null;
        LegacyExportColumnsGrid.ItemsSource = null;
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
            LegacyExportPathTextBox.Text = dialog.FolderName;
            LegacyExportAutoDetectStatusText.Text = "CSVフォルダーを手動で選択しました。固定列を診断してください。";
            LegacyExportAutoDetectStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
            LegacyExportStatusText.Text = "未診断";
            LegacyExportFilesGrid.ItemsSource = null;
            LegacyExportColumnsGrid.ItemsSource = null;
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

    private async Task InspectLegacyExportsAsync(string folderPath, bool automatic, CancellationToken cancellationToken)
    {
        InspectLegacyExportsButton.IsEnabled = false;
        LegacyExportPathTextBox.IsEnabled = false;
        LegacyExportStatusText.Text = "固定列CSVをShift-JISとして厳格に診断しています…";
        try
        {
            var result = await legacyExportReader.ReadAsync(folderPath, cancellationToken);
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
                "下の列サンプルで金額・明細・日付の位置を確認してから、登録用データの候補化へ進みます。車両一覧が1ファイルで診断に合格した場合だけ、下の候補作成ボタンを押せます。現段階では登録用データを出力しません。";
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
                dialog.FolderName);
            LegacyExportPreviewGrid.ItemsSource = result.PreviewRows;
            LegacyExportPreviewStatusText.Text = "登録前候補CSVを作成し、作成後の再読込検証に合格しました。登録・API送信・画像アップロードは行っていません。";
            LegacyExportPreviewStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            LegacyExportPreviewResultText.Text =
                $"保存先: {result.PackagePath}\n" +
                $"マニフェスト: {result.ManifestPath}\n" +
                $"顧客: {result.CustomerRowCount:N0}行 / 車両: {result.VehicleRowCount:N0}行 / 販売書類: {result.SalesRowCount:N0}行 / 整備書類: {result.MaintenanceRowCount:N0}行\n" +
                $"顧客名空欄で無視: {result.SkippedBlankCustomerRows:N0}行 / 整備書類の車両未確定で除外: {result.SkippedMaintenanceWithoutVehicleRows:N0}行 / 要確認の車両紐付け: {result.AmbiguousVehicleRows:N0}行\n" +
                $"マニフェスト SHA-256: {result.ManifestSha256}";
            LegacyExportPackagePathTextBox.Text = result.PackagePath;
            ReadLegacyExportPackageButton.IsEnabled = true;
            LegacyExportPackageStatusText.Text = "候補パッケージを読み込んで再検証できます。";
            LegacyExportPackageStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#1E40AF")!;
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

        ReadLegacyExportPackageButton.IsEnabled = false;
        SelectLegacyExportPackageFolderButton.IsEnabled = false;
        LegacyExportPackagePathTextBox.IsEnabled = false;
        LegacyExportPackageRowsGrid.ItemsSource = null;
        LegacyExportPackageResultText.Text = "";
        ResetLegacyCandidateGraph("候補パッケージを再検証すると、ここにグラフを表示します。");
        LegacyExportPackageStatusText.Text = "マニフェストと候補CSVを再検証しています…";
        LegacyExportPackageStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#52647A")!;
        try
        {
            var result = await legacyExportPackageReader.ReadAsync(packagePath);
            LegacyExportPackageRowsGrid.ItemsSource = result.Rows;
            var statusSummary = result.Rows
                .GroupBy(row => row.MatchStatus, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}:{group.Count():N0}");
            var warningSummary = result.Warnings.Count == 0
                ? "なし"
                : string.Join(" / ", result.Warnings.Take(3));
            LegacyExportPackageStatusText.Text =
                "候補パッケージの再検証に合格しました。登録・API送信・画像アップロードは行っていません。";
            LegacyExportPackageStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
            LegacyExportPackageResultText.Text =
                $"顧客: {result.CustomerRowCount:N0}行 / 車両: {result.VehicleRowCount:N0}行 / 販売書類: {result.SalesRowCount:N0}行 / 整備書類: {result.MaintenanceRowCount:N0}行\n" +
                $"顧客名空欄で無視: {result.SkippedBlankCustomerRows:N0}行 / 整備書類の車両未確定で除外: {result.SkippedMaintenanceWithoutVehicleRows:N0}行 / 要確認の車両紐付け: {result.AmbiguousVehicleRows:N0}行\n" +
                $"表示行の判定: {string.Join(" / ", statusSummary)}\n" +
                $"CSV: {string.Join(" / ", result.DataFiles.Select(file => $"{file.FileName} {file.SizeBytes:N0} bytes"))}\n" +
                $"マニフェスト SHA-256: {result.ManifestSha256}\n" +
                $"警告: {warningSummary}";
            try
            {
                var graph = await legacyExportCandidateGraphService.BuildAsync(result);
                legacyExportCandidateGraphResult = graph;
                legacyGraphManualDocumentLinks.Clear();
                legacyGraphManualDocumentCustomerLinks.Clear();
                legacyGraphUnconnectedDocumentKeys.Clear();
                legacyGraphTrayDocumentKeys.Clear();
                legacyGraphExcludedDocumentKeys.Clear();
                legacyGraphImportConfirmed = false;
                legacyGraphCustomerMergeDrafts.Clear();
                legacyGraphAppliedCustomerMergeKeys.Clear();
                legacyGraphVirtualCustomerMergeKeys.Clear();
                legacyGraphCustomerGroupExpanded.Clear();
                InitializeLegacyGraphCustomerMergeGroups();
                legacyGraphSelectedItem = null;
                RefreshLegacyGraphCustomerList();
                LegacyGraphUnresolvedVehicleList.ItemsSource = graph.UnresolvedVehicleRows;
                RefreshLegacyGraphUnresolvedDocumentLists();
                LegacyGraphStatusText.Text =
                    $"グラフを作成しました。顧客 {graph.Customers.Count:N0}件 / 車両 {graph.Customers.Sum(customer => customer.Vehicles.Count):N0}台 / 書類 {graph.AllDocuments.Count:N0}件。" +
                    $"未確定車両 {graph.UnresolvedVehicleRows.Count:N0}件 / 未確定トレイ {graph.AllDocuments.Count(IsLegacyGraphDocumentInTray):N0}件。" +
                    $"実線 {graph.SolidLinkCount:N0}件 / 要確認 {graph.ReviewLinkCount:N0}件 / 未確定 {graph.UnmatchedDocumentCount:N0}件。";
                LegacyGraphStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#17643A")!;
                LegacyGraphLegendText.Text =
                    "車両右側と書類左側の●が接続ノードです。緑の実線は自動確定、青の点線は仮紐付け、赤の点線は未接続です。顧客・書類カードはドラッグできます。すべての操作が終わったら、下部の「インポート内容を確定」を押してください。";
                UpdateLegacyGraphImportConfirmationButton();
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
            {
                ResetLegacyCandidateGraph($"グラフの作成に失敗しました: {exception.Message}");
                LegacyGraphStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            LegacyExportPackageRowsGrid.ItemsSource = null;
            ResetLegacyCandidateGraph("候補パッケージの再検証に失敗したため、グラフをクリアしました。");
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

    private void LegacyGraphCustomersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLegacyGraphCurrentCustomerSelectionText();
        if (LegacyGraphCustomersList.SelectedItem is LegacyGraphCustomerListEntry entry)
        {
            var displayCustomer = entry.IsGroupHeader
                ? GetLegacyGraphDisplayCustomer(entry.Customer)
                : entry.Customer;
            legacyGraphSelectedItem = displayCustomer;
            UpdateLegacyGraphInspector(displayCustomer);
            RenderLegacyGraphCustomer(displayCustomer);
        }
    }

    private void UpdateLegacyGraphCurrentCustomerSelectionText()
    {
        if (LegacyGraphCurrentCustomerSelectionText is null)
        {
            return;
        }

        LegacyGraphCurrentCustomerSelectionText.Text = LegacyGraphCustomersList.SelectedItem is LegacyGraphCustomerListEntry entry
            ? $"現在の選択: {entry.DisplayName}"
            : "現在の選択: なし";
    }

    private void LegacyGraphReturnToCurrentSelectionButton_Click(object sender, RoutedEventArgs e)
    {
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
        return TryGetLegacyGraphMergeGroup(mergeKey, out var group) && group.CustomerIds.Count > 1
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
            return legacyExportCandidateGraphResult.Customers.FirstOrDefault(customer =>
                customer.Vehicles.Any(candidate => string.Equals(candidate.VehicleId, vehicle.VehicleId, StringComparison.Ordinal)));
        }

        if (legacyGraphSelectedItem is AbacusLegacyExportCandidateGraphDocument document &&
            legacyExportCandidateGraphResult is not null)
        {
            var key = GetLegacyDocumentKey(document);
            if (legacyGraphManualDocumentCustomerLinks.TryGetValue(key, out var customerGroupKey))
            {
                return legacyExportCandidateGraphResult.Customers.FirstOrDefault(customer =>
                    string.Equals(GetLegacyCustomerMergeKey(customer), customerGroupKey, StringComparison.Ordinal));
            }

            return FindOriginalCustomerForDocument(document);
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
        var sourceGroupKey = GetLegacyCustomerMergeKey(sourceCustomer);
        var showUngroupZone = TryGetLegacyGraphMergeGroup(sourceGroupKey, out var sourceGroup) &&
                              sourceGroup.CustomerIds.Count > 1;
        SetLegacyGraphCustomerUngroupDropZoneVisible(showUngroupZone);
        try
        {
            DragDrop.DoDragDrop(list, data, DragDropEffects.Link);
        }
        finally
        {
            SetLegacyGraphCustomerUngroupDropZoneVisible(false);
            ClearLegacyGraphCustomerDropHighlight();
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
        ClearLegacyGraphCustomerDropHighlight();
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
            $"顧客 {sourceCustomer.DisplayName} を {targetCustomer.DisplayName} の統合候補へ追加しました。顧客情報をプレビューすると採用値を選択できます。";
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

        group.CustomerIds.Remove(sourceCustomer.CustomerId);
        legacyGraphCustomerMergeGroupByCustomerId.Remove(sourceCustomer.CustomerId);
        InvalidateLegacyGraphMergeGroupState(groupKey);
        if (group.CustomerIds.Count < 2)
        {
            legacyGraphCustomerMergeGroups.Remove(groupKey);
            legacyGraphCustomerGroupExpanded.Remove(groupKey);
        }

        var remaining = group.CustomerIds
            .Select(FindLegacyGraphCustomerById)
            .Where(customer => customer is not null)
            .Cast<AbacusLegacyExportCandidateGraphCustomer>()
            .FirstOrDefault();
        RefreshLegacyGraphCustomerList(remaining is null
            ? null
            : $"customer:{remaining.CustomerId}");
        if (remaining is not null)
        {
            var display = GetLegacyGraphDisplayCustomer(remaining);
            legacyGraphSelectedItem = display;
            UpdateLegacyGraphInspector(display);
            RenderLegacyGraphCustomer(display);
        }

        LegacyGraphStatusText.Text =
            $"{sourceCustomer.DisplayName} を統合候補から外しました。1件だけになった場合は通常の顧客へ戻ります。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private static LegacyGraphCustomerDragPayload? GetLegacyGraphCustomerDragPayload(IDataObject data) =>
        data.GetData(typeof(LegacyGraphCustomerDragPayload)) as LegacyGraphCustomerDragPayload;

    private void LegacyGraphUnresolvedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is AbacusLegacyExportCandidateGraphDocument document)
        {
            legacyGraphSelectedItem = document;
            UpdateLegacyGraphInspector(document);
        }
        else if (sender is ListBox vehicleList && vehicleList.SelectedItem is AbacusLegacyExportPreviewRow vehicleRow)
        {
            legacyGraphSelectedItem = vehicleRow;
            UpdateLegacyGraphInspector(vehicleRow);
        }
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
    }

    private void LegacyGraphUnresolvedDocumentList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (legacyGraphTrayDragDocument is null || e.LeftButton != MouseButtonState.Pressed)
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

        try
        {
            DragDrop.DoDragDrop(dragSource ?? this, data, DragDropEffects.Link);
        }
        finally
        {
            if (dragSource is not null)
            {
                dragSource.GiveFeedback -= LegacyGraphDocumentDrag_GiveFeedback;
            }

            Mouse.OverrideCursor = null;
            Cursor = null;
            legacyGraphNativeDocumentDropTargetValid = false;
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
            .Where(customer => !string.IsNullOrWhiteSpace(NormalizeLegacyCustomerMergeText(customer.CustomerName)))
            .GroupBy(customer => NormalizeLegacyCustomerMergeText(customer.CustomerName), StringComparer.Ordinal)
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

    private void RefreshLegacyGraphCustomerList(string? selectedEntryId = null)
    {
        var entries = BuildLegacyGraphCustomerListEntries();
        LegacyGraphCustomersList.ItemsSource = entries;
        if (entries.Count == 0)
        {
            LegacyGraphCustomersList.SelectedIndex = -1;
            UpdateLegacyGraphCurrentCustomerSelectionText();
            UpdateLegacyGraphImportConfirmationButton();
            return;
        }

        var selectedEntry = selectedEntryId is null
            ? entries[0]
            : entries.FirstOrDefault(entry => string.Equals(entry.EntryId, selectedEntryId, StringComparison.Ordinal));
        LegacyGraphCustomersList.SelectedItem = selectedEntry ?? entries[0];
        UpdateLegacyGraphCurrentCustomerSelectionText();
        UpdateLegacyGraphImportConfirmationButton();
    }

    private IReadOnlyList<LegacyGraphCustomerListEntry> BuildLegacyGraphCustomerListEntries()
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        var entries = new List<LegacyGraphCustomerListEntry>();
        var groups = legacyExportCandidateGraphResult.Customers
            .OrderBy(customer => customer.DisplayName, StringComparer.OrdinalIgnoreCase)
            .GroupBy(GetLegacyCustomerMergeKey, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var customers = group.ToArray();
            var groupKey = group.Key;
            if (customers.Length < 2 || !TryGetLegacyGraphMergeGroup(groupKey, out var mergeGroup))
            {
                entries.Add(CreateLegacyGraphCustomerListEntry(customers[0]));
                continue;
            }

            var hasDraft = legacyGraphCustomerMergeDrafts.ContainsKey(groupKey);
            var isApplied = legacyGraphAppliedCustomerMergeKeys.Contains(groupKey);
            var representative = GetLegacyGraphDisplayCustomer(customers[0]);
            var expanded = legacyGraphCustomerGroupExpanded.GetValueOrDefault(groupKey);
            var vehicleCount = representative.Vehicles.Count;
            var documentCount = GetLegacyGraphDisplayedDocumentCount(representative);
            var (background, border, statusBrush, statusText) = isApplied
                ? ("#EAF2FF", "#2563EB", "#1D4ED8", "承認済み")
                : hasDraft
                    ? ("#FFF7ED", "#D97706", "#9A3412", "顧客情報選択済み")
                    : ("#FFF7ED", "#D97706", "#9A3412", $"統合候補 {customers.Length}件");
            var groupDisplayName = hasDraft
                ? representative.DisplayName
                : mergeGroup.Origin == "same-name"
                    ? representative.DisplayName
                    : $"統合候補（{customers.Length}件）";
            entries.Add(new LegacyGraphCustomerListEntry(
                $"group:{groupKey}",
                true,
                representative,
                groupDisplayName,
                $"{customers.Length}件の候補 / 車両 {vehicleCount:N0}台 / 書類 {documentCount:N0}件",
                isApplied
                    ? "統合後の顧客名・情報で承認済み"
                    : hasDraft
                        ? "統合後の顧客情報を選択済み。キャンバスを確認して承認"
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
                    customer.DisplayName,
                    $"車両 {customer.Vehicles.Count:N0}台 / 書類 {GetLegacyGraphDisplayedDocumentCount(customer):N0}件",
                    string.IsNullOrWhiteSpace(customer.Address) ? "住所未設定" : customer.Address,
                    groupKey,
                    "",
                    ToBrush("#FFFFFF"),
                    ToBrush("#D8E1EC"),
                    ToBrush("#718096"),
                    mergeGroup.Origin == "same-name" ? "統合元候補" : "手動追加候補"));
            }
        }

        return entries;
    }

    private LegacyGraphCustomerListEntry CreateLegacyGraphCustomerListEntry(
        AbacusLegacyExportCandidateGraphCustomer customer) =>
        new(
            $"customer:{customer.CustomerId}",
            false,
            customer,
            customer.DisplayName,
            $"車両 {customer.Vehicles.Count:N0}台 / 書類 {GetLegacyGraphDisplayedDocumentCount(customer):N0}件",
            string.IsNullOrWhiteSpace(customer.Address) ? "住所未設定" : customer.Address,
            GetLegacyCustomerMergeKey(customer),
            "",
            ToBrush("#FFFFFF"),
            ToBrush("#D8E1EC"),
            ToBrush("#718096"),
            "");

    private void SelectLegacyGraphCustomerInList(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var groupKey = GetLegacyCustomerMergeKey(customer);
        if (TryGetLegacyGraphMergeGroup(groupKey, out _))
        {
            legacyGraphCustomerGroupExpanded[groupKey] = true;
        }

        RefreshLegacyGraphCustomerList($"customer:{customer.CustomerId}");
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
        LegacyGraphMergeButton.Content = "顧客情報をプレビュー";
        LegacyGraphApplyMergePreviewButton.IsEnabled = false;
        LegacyGraphApplyMergePreviewButton.Content = "キャンバスを承認";
        LegacyGraphRemoveCustomerFromMergeButton.IsEnabled = false;
        LegacyGraphDissolveCustomerMergeButton.IsEnabled = false;
        LegacyGraphCustomerMergeStatusText.Text = "";
        LegacyGraphReassignButton.IsEnabled = selected is AbacusLegacyExportCandidateGraphDocument documentForButton &&
            FindManualLinkedVehicle(documentForButton) is not null;
        switch (selected)
        {
            case AbacusLegacyExportCandidateGraphCustomer customer:
                var mergeCandidates = GetLegacyGraphCustomerMergeCandidates(customer);
                var mergeKey = GetLegacyCustomerMergeKey(customer);
                var hasMergeDraft = mergeCandidates.Count > 1 &&
                                    legacyGraphCustomerMergeDrafts.ContainsKey(mergeKey);
                var isAppliedMerge = legacyGraphAppliedCustomerMergeKeys.Contains(mergeKey);
                var hasMergeGroup = TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup) &&
                                    mergeGroup.CustomerIds.Count > 1;
                legacyGraphCustomerMergeDrafts.TryGetValue(mergeKey, out var mergeDraft);
                var trayDocuments = GetLegacyGraphTrayDocuments(customer);
                var excludedTrayDocumentCount = trayDocuments.Count(document =>
                    legacyGraphExcludedDocumentKeys.Contains(GetLegacyDocumentKey(document)));
                LegacyGraphMergeButton.IsEnabled = hasMergeGroup && !isAppliedMerge;
                LegacyGraphMergeButton.Content = "顧客情報をプレビュー";
                LegacyGraphApplyMergePreviewButton.IsEnabled = hasMergeDraft && !isAppliedMerge;
                LegacyGraphApplyMergePreviewButton.Content = isAppliedMerge
                    ? "キャンバス承認済み"
                    : "キャンバスを承認";
                LegacyGraphRemoveCustomerFromMergeButton.IsEnabled = hasMergeGroup;
                LegacyGraphDissolveCustomerMergeButton.IsEnabled = hasMergeGroup;
                var isManualMerge = mergeGroup?.Origin == "manual";
                LegacyGraphInspectorStateText.Text = isAppliedMerge
                    ? "● キャンバス承認済み（画面上のみ）"
                    : hasMergeDraft
                        ? "● 統合プレビュー保存済み（未反映）"
                        : hasMergeGroup
                            ? "● 統合候補（顧客情報をプレビューしてください）"
                            : "● 通常の顧客";
                LegacyGraphInspectorStateText.Foreground = ToBrush(isAppliedMerge
                    ? "#1D4ED8"
                    : hasMergeDraft
                        ? "#9A3412"
                        : hasMergeGroup
                            ? "#9A3412"
                            : "#52647A");
                LegacyGraphInspectorStateBorder.Background = ToBrush(isAppliedMerge
                    ? "#EAF2FF"
                    : hasMergeDraft
                        ? "#FFF7ED"
                        : hasMergeGroup
                            ? "#FFF7ED"
                            : "#F4F7FB");
                LegacyGraphInspectorTitleText.Text = $"顧客: {customer.DisplayName}";
                LegacyGraphInspectorStatusText.Text =
                    (isAppliedMerge ? "統合済みキャンバス（画面上のみ）\n" :
                        hasMergeGroup ? "統合候補キャンバス（画面上のみ）\n" : "") +
                    $"顧客ID: {customer.CustomerId}\n車両 {customer.Vehicles.Count:N0}台 / 書類 {GetLegacyGraphDisplayedDocumentCount(customer):N0}件" +
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
                    ? "顧客CSVの複数候補を、保存済みの統合プレビューに従ってキャンバス上だけでまとめています。元の顧客ID・CSV・ABACUSフォルダーは変更していません。"
                    : isManualMerge
                        ? "顧客カードのドラッグで追加された候補です。比較画面で項目ごとに採用値を選択します。統合プレビューは画面上だけに保存されます。"
                        : "顧客CSVの1行を表示しています。顧客名だけが一致する候補は自動統合せず、比較画面で項目ごとに採用値を選択します。統合プレビューは画面上だけに保存されます。";
                LegacyGraphCustomerMergeStatusText.Text = hasMergeGroup
                    ? legacyGraphCustomerMergeDrafts.ContainsKey(mergeKey)
                        ? isAppliedMerge
                            ? $"統合候補 {mergeCandidates.Count}件。キャンバス承認済みです。"
                            : $"統合候補 {mergeCandidates.Count}件。顧客情報を選択済みです。キャンバスを確認して承認してください。"
                        : $"統合候補 {mergeCandidates.Count}件。顧客情報をプレビューして採用値を決定してください。"
                    : "統合候補はありません。顧客カードを別の顧客カードへドラッグすると、任意の顧客を比較候補に追加できます。";
                UpdateLegacyGraphCustomerInspectorTabs(customer, mergeCandidates, mergeDraft, isAppliedMerge);
                break;
            case AbacusLegacyExportCandidateGraphVehicle vehicle:
                LegacyGraphInspectorStateText.Text = "● 車両を選択中";
                LegacyGraphCustomerMergeStatusText.Text = "顧客ブロックを選択すると、同名顧客の比較候補を表示します。";
                LegacyGraphInspectorTitleText.Text = $"車両: {Fallback(vehicle.Maker)} {vehicle.DisplayName}";
                LegacyGraphInspectorStatusText.Text =
                    $"車両ID: {vehicle.VehicleId}\n書類 {vehicle.Documents.Count:N0}件";
                LegacyGraphInspectorDetailsText.Text =
                    $"顧客: {Fallback(vehicle.CustomerName)}\n" +
                    $"登録番号: {Fallback(vehicle.RegistrationNumber)}\n" +
                    $"車台番号: {Fallback(vehicle.ChassisNumber)}\n" +
                    $"年式: {Fallback(vehicle.ModelYear)}\n" +
                    $"車検満了日: {Fallback(vehicle.InspectionDate)}\n" +
                    $"走行距離: {Fallback(vehicle.Mileage)}\n" +
                    "画像: 次段階で候補画像を表示";
                LegacyGraphInspectorEvidenceText.Text =
                    "車両CSVの顧客IDで顧客へ結び付けています。登録番号・車台番号は書類の候補判定にも使用します。";
                break;
            case AbacusLegacyExportCandidateGraphDocument document:
                var isUnconnectedDocument = IsLegacyGraphDocumentUnconnected(document);
                var isTrayDocument = IsLegacyGraphDocumentInTray(document);
                var isCustomerDirectDocument = IsLegacyGraphCustomerDirectDocument(document);
                var isManualCustomerDocument = legacyGraphManualDocumentCustomerLinks.ContainsKey(GetLegacyDocumentKey(document));
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
                            ? "判定: 顧客へ手動仮紐付け（車両なし）\nキャンバス承認前の画面上の一時状態です。"
                        : isCustomerDirectDocument
                            ? $"判定: 顧客に一意紐付け（車両情報なしの特例）\n候補車両: {document.CandidateSummary}\nこのまま確定すると顧客だけへ保存します。必要なら車両ノードへ接続できます。"
                        : manualVehicle is null
                            ? $"判定: {document.MatchStatus}\n候補車両: {document.CandidateSummary}"
                    : $"判定: 手動仮紐付け（未登録）\n紐付け先: {manualVehicle.DisplayName}";
                LegacyGraphInspectorDetailsText.Text =
                    $"顧客: {Fallback(document.CustomerName)}\n" +
                    $"車名: {Fallback(document.VehicleName)}\n" +
                    $"登録番号: {Fallback(document.RegistrationNumber)}\n" +
                    $"日付: {Fallback(document.DocumentDate)}\n" +
                    $"合計: {Fallback(document.TotalAmount)}\n" +
                    $"出典: {document.SourceLocation}";
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
            case AbacusLegacyExportPreviewRow vehicleRow:
                LegacyGraphInspectorStateText.Text = "● 未確定車両を選択中";
                LegacyGraphCustomerMergeStatusText.Text = "顧客ブロックを選択すると、同名顧客の比較候補を表示します。";
                LegacyGraphInspectorTitleText.Text = $"未確定車両: {Fallback(vehicleRow.VehicleName)}";
                LegacyGraphInspectorStatusText.Text = $"判定: {vehicleRow.MatchStatus}";
                LegacyGraphInspectorDetailsText.Text =
                    $"顧客: {Fallback(vehicleRow.CustomerName)}\n" +
                    $"出典: {vehicleRow.SourceFileName} #{vehicleRow.SourceRowNumber}";
                LegacyGraphInspectorEvidenceText.Text = vehicleRow.Warning;
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

    private void UpdateLegacyGraphCustomerInspectorTabs(
        AbacusLegacyExportCandidateGraphCustomer selectedCustomer,
        IReadOnlyList<AbacusLegacyExportCandidateGraphCustomer> candidates,
        LegacyGraphCustomerMergeDraft? draft,
        bool isAppliedMerge)
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
                ToBrush(index == 0 && isAppliedMerge ? "#EAF2FF" : "#FFFFFF"),
                ToBrush(index == 0 && isAppliedMerge ? "#2563EB" : "#D8E1EC")))
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
                      (isAppliedMerge ? " / キャンバス承認済み" : " / 保存済み")
                    : hasDifference
                        ? "候補間に差異があります。比較画面で採用候補を選択してください。"
                        : "候補間で一致しています。";
                var background = draft is not null
                    ? isAppliedMerge ? "#EAF2FF" : "#FFF7ED"
                    : hasDifference ? "#FFF7ED" : "#EEF7F1";
                var border = draft is not null
                    ? isAppliedMerge ? "#2563EB" : "#D97706"
                    : hasDifference ? "#F59E0B" : "#B8DFC5";
                var statusBrush = draft is not null
                    ? isAppliedMerge ? "#1D4ED8" : "#9A3412"
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

        return legacyExportCandidateGraphResult.Customers
            .SelectMany(customer => customer.Vehicles)
            .FirstOrDefault(vehicle => string.Equals(vehicle.VehicleId, vehicleId, StringComparison.Ordinal));
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
            .Where(customer => mergeGroup.CustomerIds.Contains(customer.CustomerId, StringComparer.Ordinal))
            .OrderBy(customer => string.Equals(customer.CustomerId, selectedCustomer.CustomerId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(customer => customer.CustomerId, StringComparer.Ordinal)
            .ToArray();
    }

    private string GetLegacyCustomerMergeKey(AbacusLegacyExportCandidateGraphCustomer customer)
    {
        return legacyGraphVirtualCustomerMergeKeys.TryGetValue(customer.CustomerId, out var virtualKey)
            ? virtualKey
            : legacyGraphCustomerMergeGroupByCustomerId.TryGetValue(customer.CustomerId, out var groupKey)
                ? groupKey
                : $"customer:{customer.CustomerId}";
    }

    private int GetLegacyGraphDisplayedDocumentCount(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var count = customer.Documents;
        if (legacyExportCandidateGraphResult is null)
        {
            return count;
        }

        var existingKeys = customer.Vehicles
            .SelectMany(vehicle => vehicle.Documents)
            .Concat(customer.UnresolvedDocuments)
            .Select(GetLegacyDocumentKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groupKey = GetLegacyCustomerMergeKey(customer);
        count += legacyExportCandidateGraphResult.AllDocuments.Count(document =>
            legacyGraphManualDocumentCustomerLinks.TryGetValue(
                GetLegacyDocumentKey(document), out var targetGroupKey) &&
            string.Equals(targetGroupKey, groupKey, StringComparison.Ordinal) &&
            !IsLegacyGraphDocumentInTray(document) &&
            !existingKeys.Contains(GetLegacyDocumentKey(document)));
        return count;
    }

    private AbacusLegacyExportCandidateGraphCustomer GetLegacyGraphDisplayCustomer(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        var mergeKey = GetLegacyCustomerMergeKey(customer);
        if (!TryGetLegacyGraphMergeGroup(mergeKey, out var mergeGroup) ||
            mergeGroup.CustomerIds.Count < 2)
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
            .GroupBy(vehicle => vehicle.VehicleId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(vehicle => vehicle.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unresolvedDocuments = candidates
            .SelectMany(candidate => candidate.UnresolvedDocuments)
            .GroupBy(GetLegacyDocumentKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        return new AbacusLegacyExportCandidateGraphCustomer(
            virtualCustomerId,
            SelectedValue("customer-number", candidate => candidate.CustomerNumber),
            draft?.SelectedValues.TryGetValue("customer-name", out var selectedName) == true &&
            !string.IsNullOrWhiteSpace(selectedName)
                ? selectedName
                : "統合候補",
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

    private static IReadOnlyList<LegacyGraphCustomerMergeField> GetLegacyGraphCustomerMergeFields() =>
    [
        new("customer-number", "顧客番号", customer => customer.CustomerNumber),
        new("customer-name", "顧客名", customer => customer.CustomerName),
        new("name-kana", "ふりがな", customer => customer.NameKana),
        new("phone", "電話番号", customer => customer.PhoneNumber),
        new("email", "メールアドレス", customer => customer.EmailAddress),
        new("postal-code", "郵便番号", customer => customer.PostalCode),
        new("address", "住所", customer => customer.Address),
        new("memo", "メモ", customer => customer.Memo),
    ];

    private void LegacyGraphMergeButton_Click(object sender, RoutedEventArgs e)
    {
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
        var candidates = GetLegacyGraphCustomerMergeCandidates(selectedCustomer);
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
                ? "ドラッグで追加した顧客候補を比較しています。自動統合はせず、項目ごとに採用する候補を選択してください。ここで保存するのは画面上の統合プレビューだけです。"
                : "同じ顧客名の候補を比較しています。自動統合はせず、項目ごとに採用する候補を選択してください。ここで保存するのは画面上の統合プレビューだけです。",
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
            previewText.Text = "統合プレビュー（画面上のみ）\n" + string.Join("\n", previewLines);
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
            legacyGraphCustomerMergeDrafts[mergeKey] = new LegacyGraphCustomerMergeDraft(
                mergeKey,
                candidates.Select(candidate => candidate.CustomerId).ToArray(),
                fieldSelections,
                selectedValues,
                DateTimeOffset.UtcNow);
            InvalidateLegacyGraphImportConfirmation();
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
            RefreshLegacyGraphCustomerList($"group:{mergeKey}");
            var displayCustomer = GetLegacyGraphDisplayCustomer(sourceCustomer);
            UpdateLegacyGraphInspector(displayCustomer);
            RenderLegacyGraphCustomer(displayCustomer);

            return true;
        }

        return false;
    }

    private void LegacyGraphApplyMergePreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (legacyGraphSelectedItem is not AbacusLegacyExportCandidateGraphCustomer customer)
        {
            return;
        }

        var mergeKey = GetLegacyCustomerMergeKey(customer);
        if (!legacyGraphCustomerMergeDrafts.ContainsKey(mergeKey) ||
            legacyGraphAppliedCustomerMergeKeys.Contains(mergeKey))
        {
            return;
        }

        var displayCustomer = GetLegacyGraphDisplayCustomer(customer);
        var unconnectedDocuments = GetLegacyGraphUnconnectedDocuments(displayCustomer).ToArray();
        if (unconnectedDocuments.Length > 0)
        {
            LegacyGraphStatusText.Text =
                $"ノード未接続の書類が{unconnectedDocuments.Length:N0}件あります。書類ノードを車両へ接続するか、未確定トレイへ移動してから承認してください。";
            LegacyGraphStatusText.Foreground = ToBrush("#A61B1B");
            MessageBox.Show(
                this,
                $"この統合候補にはノード未接続の書類が{unconnectedDocuments.Length:N0}件あります。\n" +
                "\n書類ブロックを車両へドラッグして接続するか、書類ブロックを未確定トレイへドラッグしてから、もう一度承認してください。",
                "車両未接続の書類があります",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        legacyGraphAppliedCustomerMergeKeys.Add(mergeKey);
        var sourceCustomer = GetLegacyGraphSourceCustomer(customer);
        RefreshLegacyGraphCustomerList($"group:{mergeKey}");
        displayCustomer = GetLegacyGraphDisplayCustomer(sourceCustomer);
        legacyGraphSelectedItem = displayCustomer;
        UpdateLegacyGraphInspector(displayCustomer);
        RenderLegacyGraphCustomer(displayCustomer);
        LegacyGraphStatusText.Text =
            "統合候補を承認しました。顧客一覧を青色で表示しています。インポート全体の確定はまだ行っていません。元CSV・ABACUSフォルダーは変更していません。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
        UpdateLegacyGraphImportConfirmationButton();
    }

    private void LegacyGraphApproveAllMergeButton_Click(object sender, RoutedEventArgs e)
    {
        if (legacyExportCandidateGraphResult is null || legacyGraphImportConfirmed)
        {
            return;
        }

        var pendingGroups = legacyGraphCustomerMergeGroups.Values
            .Where(group => group.CustomerIds.Count > 1 &&
                            !legacyGraphAppliedCustomerMergeKeys.Contains(group.GroupId))
            .OrderBy(group => group.GroupId, StringComparer.Ordinal)
            .ToArray();
        if (pendingGroups.Length == 0)
        {
            UpdateLegacyGraphImportConfirmationButton();
            return;
        }

        var groupEntries = pendingGroups
            .Select(group =>
            {
                var sourceCustomer = legacyExportCandidateGraphResult.Customers
                    .FirstOrDefault(customer => group.CustomerIds.Contains(customer.CustomerId, StringComparer.Ordinal));
                if (sourceCustomer is null)
                {
                    return null;
                }

                var candidates = GetLegacyGraphCustomerMergeCandidates(sourceCustomer);
                var hasCurrentDraft = legacyGraphCustomerMergeDrafts.TryGetValue(group.GroupId, out var draft) &&
                                       draft.CandidateCustomerIds.Count == candidates.Count &&
                                       draft.CandidateCustomerIds.All(candidateId => candidates.Any(candidate =>
                                           string.Equals(candidate.CustomerId, candidateId, StringComparison.Ordinal)));
                return new LegacyGraphMergeApprovalEntry(group, sourceCustomer, candidates, hasCurrentDraft);
            })
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToArray();

        if (groupEntries.Length != pendingGroups.Length)
        {
            MessageBox.Show(
                this,
                "一括承認対象の顧客候補を再構成できません。候補パッケージを再読込してから、もう一度お試しください。",
                "一括承認できません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var needsDefaultPreview = groupEntries.Count(entry => !entry.HasCurrentDraft);
        var confirmationText = needsDefaultPreview > 0
            ? $"統合候補 {pendingGroups.Length:N0}件を一括承認します。\n\n" +
              $"このうち{needsDefaultPreview:N0}件は顧客情報プレビューが未保存です。未保存の候補は、各項目で最初に値が入っている候補を採用し、画面上の統合プレビューを自動作成します。\n\n" +
              "一括承認後も元CSV・ABACUSフォルダーは変更されません。実行しますか？"
            : $"顧客情報プレビュー保存済みの統合候補 {pendingGroups.Length:N0}件を一括承認します。\n\n" +
              "一括承認後も元CSV・ABACUSフォルダーは変更されません。実行しますか？";
        if (MessageBox.Show(
                this,
                confirmationText,
                "統合候補を一括承認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var blockedEntries = groupEntries
            .Select(entry => (Entry: entry, Documents: GetLegacyGraphUnconnectedDocuments(GetLegacyGraphDisplayCustomer(entry.SourceCustomer)).ToArray()))
            .Where(item => item.Documents.Length > 0)
            .ToArray();
        if (blockedEntries.Length > 0)
        {
            var blockedDocumentCount = blockedEntries.Sum(item => item.Documents.Length);
            LegacyGraphStatusText.Text =
                $"一括承認を中止しました。ノード未接続の書類が{blockedDocumentCount:N0}件あります。";
            LegacyGraphStatusText.Foreground = ToBrush("#A61B1B");
            MessageBox.Show(
                this,
                $"{blockedEntries.Length:N0}件の統合候補に、合計{blockedDocumentCount:N0}件のノード未接続書類があります。\n\n" +
                "書類を車両へ接続するか、未確定トレイへ移動してから、もう一度一括承認してください。\n" +
                "顧客だけが一意に判定できる車両情報なし書類は、互換特例として一括承認できます。",
                "未接続書類があります",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        InvalidateLegacyGraphImportConfirmation();
        foreach (var entry in groupEntries)
        {
            if (!entry.HasCurrentDraft)
            {
                legacyGraphCustomerMergeDrafts[entry.Group.GroupId] =
                    BuildLegacyGraphDefaultMergeDraft(entry.Group.GroupId, entry.Candidates);
            }

            legacyGraphAppliedCustomerMergeKeys.Add(entry.Group.GroupId);
        }

        var selectedCustomer = GetLegacyGraphListSelectedDisplayCustomer() ?? groupEntries[0].SourceCustomer;
        var selectedEntryId = GetLegacyGraphCustomerListEntryId(selectedCustomer);
        RefreshLegacyGraphCustomerList(selectedEntryId);
        var displayCustomer = GetLegacyGraphDisplayCustomer(selectedCustomer);
        legacyGraphSelectedItem = displayCustomer;
        UpdateLegacyGraphInspector(displayCustomer);
        RenderLegacyGraphCustomer(displayCustomer);
        LegacyGraphStatusText.Text =
            $"統合候補{pendingGroups.Length:N0}件を一括承認しました。顧客一覧を青色で表示しています。インポート全体の確定はまだ行っていません。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
        UpdateLegacyGraphImportConfirmationButton();
    }

    private static LegacyGraphCustomerMergeDraft BuildLegacyGraphDefaultMergeDraft(
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

    private void LegacyGraphFinalizeImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (legacyExportCandidateGraphResult is null || legacyGraphImportConfirmed)
        {
            return;
        }

        var pendingMergeGroupCount = GetLegacyGraphPendingMergeGroupCount();
        var pendingDocumentCount = GetLegacyGraphPendingDocumentCount();
        if (pendingMergeGroupCount > 0 || pendingDocumentCount > 0)
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

            MessageBox.Show(
                this,
                "次の操作が残っているため、インポート内容を確定できません。\n\n" +
                string.Join("\n", pendingDetails) +
                "\n\n統合候補の承認、書類ノードの車両接続、または未確定トレイへの移動を完了してください。",
                "操作が残っています",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            UpdateLegacyGraphImportConfirmationButton();
            return;
        }

        var trayDocuments = GetLegacyGraphTrayDocuments();
        var excludedSummary = trayDocuments.Count == 0
            ? "未確定トレイに残っている書類はありません。"
            : "未確定トレイに残っている次の書類は、今回のインポートから除外されます。\n" +
              string.Join(
                  "、",
                  trayDocuments
                      .GroupBy(document => document.Kind, StringComparer.Ordinal)
                      .OrderBy(group => group.Key, StringComparer.Ordinal)
                      .Select(group => $"{group.Key}{group.Count():N0}件"));
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
        LegacyGraphStatusText.Text = trayDocuments.Count == 0
            ? "インポート内容を確定しました。未確定トレイの書類はありません。元CSV・ABACUSフォルダーは変更していません。"
            : $"インポート内容を確定しました。未確定トレイの書類{trayDocuments.Count:N0}件は今回のインポートから除外します。元CSV・ABACUSフォルダーは変更していません。";
        LegacyGraphStatusText.Foreground = ToBrush("#2563EB");
    }

    private async void LegacyGraphCreateFinalPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (legacyExportCandidateGraphResult is null || !legacyGraphImportConfirmed || legacyGraphFinalPackageBusy)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "グラフ確定パッケージの保存先を選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "確定した顧客統合・書類紐付けを登録前パッケージへ出力しますか？\n\n" +
            "顧客CSV・車両CSV・販売CSV・整備CSV・書類リンク・除外一覧を新規フォルダーへ保存します。\n" +
            "Web API、D1、画像アップロード、元CSV・ABACUSフォルダーの変更は行いません。",
            "登録前パッケージの作成",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        legacyGraphFinalPackageBusy = true;
        LegacyGraphCreateFinalPackageButton.IsEnabled = false;
        LegacyGraphFinalPackageResultText.Text = "";
        LegacyGraphFinalPackageStatusText.Text = "確定状態・書類リンク・除外一覧を再検証してパッケージを作成しています…";
        LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#52647A");
        try
        {
            var snapshot = BuildLegacyGraphFinalizationSnapshot();
            var result = await legacyGraphFinalPackageStore.CreateAsync(
                legacyExportCandidateGraphResult,
                snapshot,
                dialog.FolderName);
            LegacyGraphFinalPackageStatusText.Text =
                "グラフ確定後の登録前パッケージを作成しました。Web API・DB・画像アップロードはまだ行っていません。";
            LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#17643A");
            LegacyGraphFinalPackageResultText.Text =
                $"保存先: {result.PackagePath}\n" +
                $"マニフェスト: {result.ManifestPath}\n" +
                $"顧客CSV: {result.CustomersCsvPath}\n" +
                $"車両CSV: {result.VehiclesCsvPath}\n" +
                $"販売CSV: {result.SalesCsvPath}\n" +
                $"整備CSV: {result.MaintenanceCsvPath}\n" +
                $"書類リンク: {result.DocumentLinksPath}\n" +
                $"顧客: {result.CustomerRowCount:N0}行 / 車両: {result.VehicleRowCount:N0}行 / " +
                $"販売書類: {result.SalesRowCount:N0}行 / 整備書類: {result.MaintenanceRowCount:N0}行\n" +
                $"車両情報なし: {result.VehiclelessDocumentCount:N0}件 / 除外: {result.ExcludedDocumentCount:N0}件\n" +
                $"マニフェスト SHA-256: {result.ManifestSha256}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException or
                                           NotSupportedException)
        {
            LegacyGraphFinalPackageResultText.Text = "";
            LegacyGraphFinalPackageStatusText.Text =
                $"グラフ確定パッケージを作成できません: {exception.Message}";
            LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#A61B1B");
        }
        finally
        {
            legacyGraphFinalPackageBusy = false;
            UpdateLegacyGraphImportConfirmationButton();
        }
    }

    private AbacusLegacyGraphFinalizationSnapshot BuildLegacyGraphFinalizationSnapshot()
    {
        if (legacyExportCandidateGraphResult is null || !legacyGraphImportConfirmed)
        {
            throw new InvalidDataException("グラフのインポート内容が最終確定されていません。");
        }

        var groups = legacyExportCandidateGraphResult.Customers
            .OrderBy(customer => customer.CustomerId, StringComparer.Ordinal)
            .GroupBy(GetLegacyCustomerMergeKey, StringComparer.Ordinal)
            .Select(grouping =>
            {
                var sourceCustomers = grouping.ToArray();
                var hasMergeGroup = TryGetLegacyGraphMergeGroup(grouping.Key, out var mergeGroup) &&
                                    mergeGroup.CustomerIds.Count > 1;
                var displayCustomer = hasMergeGroup
                    ? GetLegacyGraphDisplayCustomer(sourceCustomers[0])
                    : sourceCustomers[0];
                return new AbacusLegacyGraphFinalCustomerGroup(
                    grouping.Key,
                    hasMergeGroup ? mergeGroup!.Origin : "single",
                    !hasMergeGroup || legacyGraphAppliedCustomerMergeKeys.Contains(grouping.Key),
                    sourceCustomers.Select(customer => customer.CustomerId).ToArray(),
                    displayCustomer.CustomerId,
                    displayCustomer.CustomerNumber,
                    displayCustomer.CustomerName,
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
            legacyGraphImportConfirmed);
    }

    private int GetLegacyGraphPendingMergeGroupCount() =>
        legacyGraphCustomerMergeGroups.Values.Count(group =>
            group.CustomerIds.Count > 1 &&
            !legacyGraphAppliedCustomerMergeKeys.Contains(group.GroupId));

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
            LegacyGraphApproveAllMergeButton is null)
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
            LegacyGraphFinalPackageStatusText.Text =
                "インポート内容を確定すると、確定済みのCSV・書類リンク・除外一覧をパッケージ化できます。";
            LegacyGraphApproveAllMergeButton.IsEnabled = false;
            LegacyGraphApproveAllMergeButton.Content = "統合候補を一括承認";
            return;
        }

        if (legacyGraphImportConfirmed)
        {
            LegacyGraphFinalizeImportButton.IsEnabled = false;
            LegacyGraphFinalizeImportButton.Content = "インポート内容を確定済み";
            LegacyGraphFinalizeImportStatusText.Text =
                $"インポート内容を確定済みです。除外確定: {legacyGraphExcludedDocumentKeys.Count:N0}件。操作を変更すると確定が解除されます。";
            LegacyGraphApproveAllMergeButton.IsEnabled = false;
            LegacyGraphApproveAllMergeButton.Content = "統合候補を一括承認済み";
            LegacyGraphCreateFinalPackageButton.IsEnabled = !legacyGraphFinalPackageBusy;
            if (!legacyGraphFinalPackageBusy && string.IsNullOrWhiteSpace(LegacyGraphFinalPackageResultText.Text))
            {
                LegacyGraphFinalPackageStatusText.Text =
                    "確定内容を登録前パッケージへ保存できます。保存後もWeb API・DB・画像アップロードは行いません。";
                LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#52647A");
            }
            return;
        }

        var pendingMergeGroupCount = GetLegacyGraphPendingMergeGroupCount();
        var pendingDocumentCount = GetLegacyGraphPendingDocumentCount();
        var trayCount = GetLegacyGraphTrayDocuments().Count;
        LegacyGraphApproveAllMergeButton.IsEnabled = pendingMergeGroupCount > 0;
        LegacyGraphApproveAllMergeButton.Content = pendingMergeGroupCount > 0
            ? $"統合候補を一括承認（{pendingMergeGroupCount:N0}件）"
            : "統合候補を一括承認";
        LegacyGraphFinalizeImportButton.IsEnabled = pendingMergeGroupCount == 0 && pendingDocumentCount == 0;
        LegacyGraphFinalizeImportButton.Content = "インポート内容を確定";
        LegacyGraphCreateFinalPackageButton.IsEnabled = false;
        if (pendingMergeGroupCount > 0 || pendingDocumentCount > 0)
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

            LegacyGraphFinalizeImportStatusText.Text =
                $"確定前に完了してください: {string.Join(" / ", pendingDetails)}。" +
                (trayCount > 0 ? $" 未確定トレイ {trayCount:N0}件は確定時に除外できます。" : "");
        }
        else
        {
            LegacyGraphFinalizeImportStatusText.Text = trayCount > 0
                ? $"確定できます。未確定トレイ {trayCount:N0}件は確認後に今回のインポートから除外します。"
                : "確定できます。未確定トレイの書類はありません。";
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

        // ドラッグ元が既存グループの子であっても、移動するのは選択した1顧客だけです。
        // グループ全体を暗黙に移動させないことで、同姓同名候補から1件だけ外せます。
        if (hasSourceGroup && !string.Equals(sourceGroupKey, targetGroupKey, StringComparison.Ordinal))
        {
            sourceGroup.CustomerIds.Remove(sourceCustomer.CustomerId);
            legacyGraphCustomerMergeGroupByCustomerId.Remove(sourceCustomer.CustomerId);
            InvalidateLegacyGraphMergeGroupState(sourceGroup.GroupId);
            if (sourceGroup.CustomerIds.Count < 2)
            {
                legacyGraphCustomerMergeGroups.Remove(sourceGroup.GroupId);
                legacyGraphCustomerGroupExpanded.Remove(sourceGroup.GroupId);
            }
        }

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

        foreach (var customerId in new[] { sourceCustomer.CustomerId, targetCustomer.CustomerId })
        {
            if (!mergeGroup.CustomerIds.Contains(customerId, StringComparer.Ordinal))
            {
                mergeGroup.CustomerIds.Add(customerId);
            }

            legacyGraphCustomerMergeGroupByCustomerId[customerId] = mergeGroup.GroupId;
        }

        mergeGroup.Origin = "manual";
        legacyGraphCustomerGroupExpanded[mergeGroup.GroupId] = true;
        InvalidateLegacyGraphMergeGroupState(mergeGroup.GroupId);
        return mergeGroup.GroupId;
    }

    private void InvalidateLegacyGraphMergeGroupState(string groupKey)
    {
        InvalidateLegacyGraphImportConfirmation();
        legacyGraphCustomerMergeDrafts.Remove(groupKey);
        legacyGraphAppliedCustomerMergeKeys.Remove(groupKey);
        foreach (var virtualCustomerId in legacyGraphVirtualCustomerMergeKeys
                     .Where(pair => string.Equals(pair.Value, groupKey, StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            legacyGraphVirtualCustomerMergeKeys.Remove(virtualCustomerId);
        }
    }

    private void LegacyGraphRemoveCustomerFromMergeButton_Click(object sender, RoutedEventArgs e)
    {
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

        RemoveLegacyGraphCustomerFromMergeGroup(sourceCustomer, groupKey);
    }

    private void LegacyGraphDissolveCustomerMergeButton_Click(object sender, RoutedEventArgs e)
    {
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

        var customerIds = mergeGroup.CustomerIds.ToArray();
        legacyGraphCustomerMergeGroups.Remove(groupKey);
        legacyGraphCustomerGroupExpanded.Remove(groupKey);
        foreach (var customerId in customerIds)
        {
            legacyGraphCustomerMergeGroupByCustomerId.Remove(customerId);
        }

        InvalidateLegacyGraphMergeGroupState(groupKey);
        var firstCustomer = FindLegacyGraphCustomerById(customerIds[0]);
        RefreshLegacyGraphCustomerList(firstCustomer is null ? null : $"customer:{firstCustomer.CustomerId}");
        if (firstCustomer is not null)
        {
            legacyGraphSelectedItem = firstCustomer;
            UpdateLegacyGraphInspector(firstCustomer);
            RenderLegacyGraphCustomer(firstCustomer);
        }

        LegacyGraphStatusText.Text = "統合候補を解消し、元の顧客一覧へ戻しました。元データは変更していません。";
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
        if (legacyGraphSelectedItem is not AbacusLegacyExportCandidateGraphDocument document)
        {
            return;
        }

        if (legacyGraphManualDocumentLinks.Remove(GetLegacyDocumentKey(document)))
        {
            RefreshLegacyGraphAfterManualLink(document, null);
        }
    }

    private void LegacyGraphReassignButton_Click(object sender, RoutedEventArgs e)
    {
        if (legacyGraphSelectedItem is not AbacusLegacyExportCandidateGraphDocument document ||
            legacyExportCandidateGraphResult is null)
        {
            return;
        }

        var candidateVehicleIds = document.CandidateVehicleIds.ToHashSet(StringComparer.Ordinal);
        var choices = legacyExportCandidateGraphResult.Customers
            .SelectMany(customer => customer.Vehicles.Select(vehicle => new LegacyGraphVehicleChoice(
                vehicle.VehicleId,
                $"{customer.DisplayName} / {vehicle.Maker} {vehicle.DisplayName}",
                vehicle.IdentifierSummary,
                customer.DisplayName)))
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
            legacyGraphManualDocumentLinks.Remove(GetLegacyDocumentKey(document));
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
            legacyGraphManualDocumentLinks[GetLegacyDocumentKey(document)] = selectedChoice.VehicleId;
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

        var targetCustomer = selectedCustomerBeforeChange ?? (vehicleId is null
            ? FindOriginalCustomerForDocument(document)
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
        if (LegacyGraphCustomersList.SelectedItem is not LegacyGraphCustomerListEntry entry)
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
        var key = GetLegacyDocumentKey(document);
        var targetGroupKey = GetLegacyCustomerMergeKey(customer);
        InvalidateLegacyGraphApprovalForDocument(document);
        legacyGraphManualDocumentLinks.Remove(key);
        legacyGraphUnconnectedDocumentKeys.Remove(key);
        legacyGraphTrayDocumentKeys.Remove(key);
        legacyGraphExcludedDocumentKeys.Remove(key);
        legacyGraphManualDocumentCustomerLinks[key] = targetGroupKey;
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
        var key = GetLegacyDocumentKey(document);
        InvalidateLegacyGraphApprovalForDocument(document);
        legacyGraphManualDocumentLinks.Remove(key);
        legacyGraphManualDocumentCustomerLinks.Remove(key);
        legacyGraphUnconnectedDocumentKeys.Remove(key);
        legacyGraphTrayDocumentKeys.Add(key);
        ClearLegacyGraphBlockVisualDrag();
        RefreshLegacyGraphAfterManualLink(document, null);
        RefreshLegacyGraphUnresolvedDocumentLists();
    }

    private void ApplyLegacyGraphManualLink(
        AbacusLegacyExportCandidateGraphDocument document,
        string vehicleId)
    {
        var key = GetLegacyDocumentKey(document);
        InvalidateLegacyGraphApprovalForDocument(document);
        legacyGraphUnconnectedDocumentKeys.Remove(key);
        legacyGraphTrayDocumentKeys.Remove(key);
        legacyGraphManualDocumentCustomerLinks.Remove(key);
        var originalVehicle = FindOriginalVehicleForDocument(document);
        if (originalVehicle is not null &&
            string.Equals(originalVehicle.VehicleId, vehicleId, StringComparison.Ordinal))
        {
            legacyGraphManualDocumentLinks.Remove(key);
        }
        else
        {
            legacyGraphManualDocumentLinks[key] = vehicleId;
        }
        RefreshLegacyGraphAfterManualLink(document, vehicleId);
    }

    private void InvalidateLegacyGraphApprovalForDocument(
        AbacusLegacyExportCandidateGraphDocument document)
    {
        InvalidateLegacyGraphImportConfirmation();
        if (legacyGraphManualDocumentCustomerLinks.TryGetValue(
                GetLegacyDocumentKey(document), out var manualCustomerGroupKey))
        {
            legacyGraphAppliedCustomerMergeKeys.Remove(manualCustomerGroupKey);
        }

        var originalCustomer = FindOriginalCustomerForDocument(document);
        if (originalCustomer is null)
        {
            return;
        }

        legacyGraphAppliedCustomerMergeKeys.Remove(GetLegacyCustomerMergeKey(originalCustomer));
    }

    private void InvalidateLegacyGraphImportConfirmation()
    {
        if (!legacyGraphImportConfirmed && legacyGraphExcludedDocumentKeys.Count == 0)
        {
            UpdateLegacyGraphImportConfirmationButton();
            return;
        }

        legacyGraphImportConfirmed = false;
        legacyGraphExcludedDocumentKeys.Clear();
        UpdateLegacyGraphImportConfirmationButton();
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
    }

    private bool IsLegacyGraphDocumentInTray(
        AbacusLegacyExportCandidateGraphDocument document)
    {
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
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        var sourceCustomerIds = GetLegacyGraphCustomerMergeCandidates(customer)
            .Select(candidate => candidate.CustomerId)
            .ToHashSet(StringComparer.Ordinal);
        return legacyExportCandidateGraphResult.AllDocuments
            .Where(IsLegacyGraphDocumentUnconnected)
            .Where(document => FindOriginalCustomerForDocument(document) is { } original &&
                               sourceCustomerIds.Contains(original.CustomerId))
            .ToArray();
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetLegacyGraphTrayDocuments(
        AbacusLegacyExportCandidateGraphCustomer customer)
    {
        if (legacyExportCandidateGraphResult is null)
        {
            return [];
        }

        var sourceCustomerIds = GetLegacyGraphCustomerMergeCandidates(customer)
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
        var documents = customer.UnresolvedDocuments
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

    private void RenderLegacyGraphCustomer(AbacusLegacyExportCandidateGraphCustomer customer)
    {
        ClearLegacyGraphBlockVisualDrag();
        EndLegacyGraphConnectionPreview();
        LegacyGraphCanvas.Children.Clear();
        LegacyGraphEdgesCanvas.Children.Clear();
        LegacyGraphPageDragPreviewCanvas.Children.Clear();
        LegacyGraphBoardGrid.Width = 1120;
        LegacyGraphBoardGrid.Height = 720;
        LegacyGraphCanvas.Width = 1120;
        LegacyGraphCanvas.Height = 720;
        LegacyGraphEdgesCanvas.Width = 1120;
        LegacyGraphEdgesCanvas.Height = 720;
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
        var isCustomerMergeApproved = legacyGraphAppliedCustomerMergeKeys.Contains(customerMergeKey);
        var customerStroke = isCustomerMergeGroup && !isCustomerMergeApproved ? "#D97706" : "#2563EB";
        var customerFill = isCustomerMergeGroup && !isCustomerMergeApproved ? "#FFF7ED" : "#EAF2FF";
        var customerBadge = isCustomerMergeGroup
            ? isCustomerMergeApproved
                ? "承認済み"
                : legacyGraphCustomerMergeDrafts.ContainsKey(customerMergeKey)
                    ? "仮反映中"
                    : "統合候補"
            : null;

        var customerBlock = CreateLegacyGraphBlock(
            $"顧客: {customer.DisplayName}",
            $"ID: {customer.CustomerId}\n車両 {customer.Vehicles.Count:N0}台 / 書類 {GetLegacyGraphDisplayedDocumentCount(customer):N0}件",
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
        foreach (var vehicle in customer.Vehicles)
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
            .Where(document => !IsLegacyGraphDocumentInTray(document))
            .Where(document => !IsLegacyGraphDocumentUnconnected(document))
            .Where(document => !legacyGraphManualDocumentLinks.ContainsKey(GetLegacyDocumentKey(document)))
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

        if (customer.Vehicles.Count == 0 &&
            customer.UnresolvedDocuments.Count == 0 &&
            customerDirectDocuments.Count == 0 &&
            unconnectedDocuments.Count == 0)
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
            .DefaultIfEmpty(720)
            .Max();
        LegacyGraphCanvas.Height = Math.Max(720, contentBottom + 70);
        LegacyGraphEdgesCanvas.Width = LegacyGraphCanvas.Width;
        LegacyGraphEdgesCanvas.Height = LegacyGraphCanvas.Height;
        LegacyGraphBoardGrid.Width = LegacyGraphCanvas.Width;
        LegacyGraphBoardGrid.Height = LegacyGraphCanvas.Height;
        LegacyGraphCanvas.UpdateLayout();
        UpdateLegacyGraphEdges();
    }

    private IReadOnlyList<AbacusLegacyExportCandidateGraphDocument> GetDocumentsForVehicle(
        AbacusLegacyExportCandidateGraphVehicle vehicle)
    {
        var documents = vehicle.Documents
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
        if (sender is not FrameworkElement element ||
            element.DataContext is not AbacusLegacyExportCandidateGraphDocument document)
        {
            return;
        }

        var key = GetLegacyDocumentKey(document);
        legacyGraphManualDocumentLinks.Remove(key);
        legacyGraphManualDocumentCustomerLinks.Remove(key);
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
            if (legacyGraphDocumentCardDragDocument is not null)
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
        var wasDragging = legacyGraphBlockDragStarted;
        var windowPoint = e.GetPosition(this);
        RestoreLegacyGraphBlockDragCursor();
        legacyGraphDraggingElement.ReleaseMouseCapture();
        legacyGraphDraggingElement = null;
        legacyGraphDocumentCardDragDocument = null;
        legacyGraphBlockDragStarted = false;
        ClearLegacyGraphBlockVisualDrag();
        ClearLegacyGraphDropHighlight();
        ClearLegacyGraphTrayDropHighlight();

        if (wasDragging && document is not null)
        {
            HandleLegacyGraphDocumentBlockDrop(document, windowPoint);
        }

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

    private void ClearLegacyGraphBlockVisualDrag()
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
        if (legacyGraphDocumentCardDragDocument is null)
        {
            RestoreLegacyGraphBlockDragCursor();
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

    private void HandleLegacyGraphDocumentBlockDrop(
        AbacusLegacyExportCandidateGraphDocument document,
        Point windowPoint)
    {
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

    private bool IsLegacyGraphUnresolvedDocumentList(ListBox list) =>
        ReferenceEquals(list, LegacyGraphUnresolvedSalesList) ||
        ReferenceEquals(list, LegacyGraphUnresolvedMaintenanceList);

    private void ResetLegacyCandidateGraph(string status)
    {
        legacyExportCandidateGraphResult = null;
        legacyGraphManualDocumentLinks.Clear();
        legacyGraphManualDocumentCustomerLinks.Clear();
        legacyGraphUnconnectedDocumentKeys.Clear();
        legacyGraphTrayDocumentKeys.Clear();
        legacyGraphExcludedDocumentKeys.Clear();
        legacyGraphImportConfirmed = false;
        legacyGraphFinalPackageBusy = false;
        legacyGraphCustomerMergeDrafts.Clear();
        legacyGraphAppliedCustomerMergeKeys.Clear();
        legacyGraphVirtualCustomerMergeKeys.Clear();
        legacyGraphCustomerGroupExpanded.Clear();
        legacyGraphCustomerMergeGroups.Clear();
        legacyGraphCustomerMergeGroupByCustomerId.Clear();
        legacyGraphSelectedItem = null;
        legacyGraphPanning = false;
        legacyGraphInspectorPanning = false;
        legacyGraphInspectorPanningPage = false;
        legacyGraphTrayDragDocument = null;
        legacyGraphNativeDocumentDropTargetValid = false;
        legacyGraphHandleDragDocument = null;
        legacyGraphDocumentCardDragDocument = null;
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
        LegacyGraphCanvas.ReleaseMouseCapture();
        LegacyGraphScrollViewer.ReleaseMouseCapture();
        LegacyGraphInspectorScrollViewer.ReleaseMouseCapture();
        legacyGraphDraggingElement = null;
        ClearLegacyGraphBlockVisualDrag();
        EndLegacyGraphConnectionPreview();
        legacyGraphEdges.Clear();
        LegacyGraphCustomersList.ItemsSource = null;
        LegacyGraphUnresolvedVehicleList.ItemsSource = null;
        LegacyGraphUnresolvedSalesList.ItemsSource = null;
        LegacyGraphUnresolvedMaintenanceList.ItemsSource = null;
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
        LegacyGraphRemoveCustomerFromMergeButton.IsEnabled = false;
        LegacyGraphDissolveCustomerMergeButton.IsEnabled = false;
        LegacyGraphFinalizeImportButton.IsEnabled = false;
        LegacyGraphFinalizeImportButton.Content = "インポート内容を確定";
        LegacyGraphFinalizeImportStatusText.Text =
            "顧客統合と書類・ノード操作を完了すると、インポート内容を確定できます。";
        LegacyGraphApproveAllMergeButton.IsEnabled = false;
        LegacyGraphApproveAllMergeButton.Content = "統合候補を一括承認";
        LegacyGraphCreateFinalPackageButton.IsEnabled = false;
        LegacyGraphFinalPackageStatusText.Text =
            "インポート内容を確定すると、確定済みのCSV・書類リンク・除外一覧をパッケージ化できます。";
        LegacyGraphFinalPackageStatusText.Foreground = ToBrush("#52647A");
        LegacyGraphFinalPackageResultText.Text = "";
        LegacyGraphStatusText.Text = status;
        LegacyGraphStatusText.Foreground = ToBrush("#52647A");
        LegacyGraphLegendText.Text = "青い顧客ブロックから車両、書類へ読み進めます。候補パッケージを再検証すると表示できます。";
    }

    private bool IsManualLinkForVehicle(
        AbacusLegacyExportCandidateGraphDocument document,
        string vehicleId) =>
        legacyGraphManualDocumentLinks.TryGetValue(GetLegacyDocumentKey(document), out var linkedVehicleId) &&
        string.Equals(linkedVehicleId, vehicleId, StringComparison.Ordinal);

    private static string GetLegacyDocumentKey(AbacusLegacyExportCandidateGraphDocument document) =>
        string.Join("|", document.Kind, document.SourceFileName, document.SourceRowNumber, document.DocumentNumber);

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

    private sealed record LegacyGraphCustomerDragPayload(string CustomerId);

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
        string StatusText)
    {
        public Visibility ExpandButtonVisibility => IsGroupHeader
            ? Visibility.Visible
            : Visibility.Collapsed;

        public GridLength ExpandColumnWidth => IsGroupHeader
            ? new GridLength(28)
            : new GridLength(0);

        public Thickness BlockMargin => IsGroupHeader ||
                                         !GroupKey.StartsWith("same-name:", StringComparison.Ordinal) &&
                                         !GroupKey.StartsWith("manual:", StringComparison.Ordinal)
            ? new Thickness(1, 3, 1, 3)
            : new Thickness(24, 3, 1, 3);

        public Thickness ContentMargin => IsGroupHeader ||
                                          GroupKey.StartsWith("same-name:", StringComparison.Ordinal) ||
                                          GroupKey.StartsWith("manual:", StringComparison.Ordinal)
            ? IsGroupHeader ? new Thickness(0) : new Thickness(22, 0, 0, 0)
            : new Thickness(0);
    }

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
        operationCancellation?.Cancel();
        CancelLegacyExportFolderDetection();
        IsEnabled = false;
        await session.DisposeAsync();
        allowClose = true;
        Close();
    }
}
