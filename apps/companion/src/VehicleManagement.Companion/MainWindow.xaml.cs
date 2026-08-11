using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly AbacusLegacyExportInspector legacyExportInspector = new();
    private readonly AbacusFp5Inspector fp5Inspector = new();
    private readonly AbacusFp5CandidateExporter fp5CandidateExporter = new();
    private readonly AbacusImageLinkManifestStore imageLinkManifestStore = new();
    private readonly AbacusImageLinkMatcher imageLinkMatcher = new();
    private readonly AbacusImageLinkApprovalStore imageLinkApprovalStore = new();
    private readonly AbacusImageRegistrationPreviewStore imageRegistrationPreviewStore = new();
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

    public MainWindow()
    {
        InitializeComponent();
        workspaceService = new AbacusWorkspaceService(folderInspector);
        migrationPreviewStore = new AbacusMigrationPreviewStore(dataAnalyzer, linkagePlanner);
        session.StateChanged += Session_StateChanged;
        Closing += MainWindow_Closing;
        Render(session.Snapshot);
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

    private void SelectSourceButton_Click(object sender, RoutedEventArgs e)
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
            Fp5InspectionStatusText.Text = "未診断";
            Fp5InspectionResultText.Text = "";
            Fp5CandidatesGrid.ItemsSource = null;
            fp5Inspection = null;
            ExtractFp5CandidateButton.IsEnabled = false;
            Fp5CandidateExportStatusText.Text = "候補を選択すると、標準JPEG構造を再検証してから1件だけ出力します。内部ブロックと判定した場合は保存しません。";
            ResetImageLinkCapture();
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

    private void SelectLegacyExportFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "ABACUSの販売・整備・車両一覧CSVがあるフォルダーを選択",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            LegacyExportPathTextBox.Text = dialog.FolderName;
            LegacyExportStatusText.Text = "未診断";
            LegacyExportFilesGrid.ItemsSource = null;
            TransformationReadinessText.Text = "";
        }
    }

    private async void InspectLegacyExportsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LegacyExportPathTextBox.Text))
        {
            MessageBox.Show(this, "ABACUSエクスポートフォルダーを指定してください。", "フォルダー未選択");
            return;
        }

        InspectLegacyExportsButton.IsEnabled = false;
        LegacyExportPathTextBox.IsEnabled = false;
        LegacyExportStatusText.Text = "固定列CSVをShift-JISとして厳格に診断しています…";
        try
        {
            var result = await legacyExportInspector.AnalyzeAsync(LegacyExportPathTextBox.Text.Trim());
            LegacyExportFilesGrid.ItemsSource = result.Files;
            var errors = result.Files.Sum(file => file.Errors.Count);
            LegacyExportStatusText.Text = result.IsValid
                ? "固定列形式の診断に合格しました。CSVへの書き込みは行っていません。"
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
                "顧客・車両の基本列位置は確定しました。書類の金額・明細と、全車両一覧の取得方法を確定するまで登録用データは出力しません。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or NotSupportedException)
        {
            LegacyExportStatusText.Text = $"固定列CSVを診断できません: {exception.Message}";
            LegacyExportStatusText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A61B1B")!;
            LegacyExportFilesGrid.ItemsSource = null;
            TransformationReadinessText.Text = "";
        }
        finally
        {
            InspectLegacyExportsButton.IsEnabled = true;
            LegacyExportPathTextBox.IsEnabled = true;
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
        SelectImageRegistrationDestinationButton.IsEnabled = !busy && !imageRegistrationPreviewBusy;
        UpdateImageRegistrationPreviewButtonState();
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

    private void UpdateImageRegistrationPreviewButtonState()
    {
        CreateImageRegistrationPreviewButton.IsEnabled =
            !imageRegistrationPreviewBusy &&
            !imageLinkMatchBusy &&
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
        IsEnabled = false;
        await session.DisposeAsync();
        allowClose = true;
        Close();
    }
}
