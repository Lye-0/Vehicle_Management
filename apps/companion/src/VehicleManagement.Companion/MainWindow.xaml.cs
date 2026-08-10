using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
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
    private readonly IAbacusMigrationPreviewStore migrationPreviewStore;
    private CancellationTokenSource? operationCancellation;
    private AbacusFolderReport? sourceReport;
    private AbacusWorkspaceResult? workspaceResult;
    private string? verifiedWorkspacePath;
    private string? verifiedSourcePath;
    private uint? inspectedClipboardSequenceNumber;
    private long? diagnosedImageWindowHandle;
    private int? diagnosedAbacusProcessId;
    private AbacusLinkagePlan? linkagePlan;
    private bool allowClose;
    private bool closeVerificationInProgress;
    private bool abacusMayBeRunning;

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
