using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using VehicleManagement.Companion.Services;

namespace VehicleManagement.Companion;

public partial class MainWindow : Window
{
    private readonly LegacyHostSession session = new();
    private readonly AbacusFolderInspector folderInspector = new();
    private readonly AbacusWorkspaceService workspaceService;
    private CancellationTokenSource? operationCancellation;
    private AbacusFolderReport? sourceReport;
    private AbacusWorkspaceResult? workspaceResult;
    private bool allowClose;
    private bool abacusMayBeRunning;

    public MainWindow()
    {
        InitializeComponent();
        workspaceService = new AbacusWorkspaceService(folderInspector);
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
        }
    }

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
        if (workspaceResult is null)
        {
            return;
        }

        SetAbacusButtonsBusy(true);
        AutomationResultText.Text = "コピー側ABACUSを起動し、ウィンドウを認識しています…";
        try
        {
            if (session.Snapshot.State != LegacyHostState.Connected)
            {
                await session.StartAsync();
            }

            var executablePath = Path.Combine(workspaceResult.WorkspacePath, AbacusConstants.ExecutableFileName);
            var result = await session.LaunchAndInspectAbacusAsync(executablePath);
            abacusMayBeRunning = result.ProcessId.HasValue;
            RenderAbacusResult(result);
        }
        catch (Exception exception)
        {
            AutomationResultText.Text = $"起動または認識に失敗しました: {exception.Message}";
        }
        finally
        {
            SetAbacusButtonsBusy(false);
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
            abacusMayBeRunning = result.Status == "manual-close-required";
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
    }

    private void RenderAbacusResult(AbacusRuntimeSnapshot result)
    {
        AutomationResultText.Text = result.IsRecognized ? $"成功: {result.Message}" : $"未確認: {result.Message}";
        AutomationResultText.Foreground = (Brush)new BrushConverter().ConvertFromString(result.IsRecognized ? "#17643A" : "#A61B1B")!;
        ProcessArchitectureText.Text = $"LegacyHost {result.HostArchitecture ?? "不明"} → ABACUS {result.TargetArchitecture ?? "不明"}";
        AbacusWindowTitleText.Text = string.IsNullOrWhiteSpace(result.WindowTitle) ? "（タイトルなし）" : result.WindowTitle;
        AutomationElementCountText.Text = result.AutomationElementCount?.ToString("N0") ?? "-";
        abacusMayBeRunning = result.ProcessId.HasValue && result.Status != "not-running" && result.Status != "closed";
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
        LaunchAbacusButton.IsEnabled = !busy && workspaceResult is not null && !abacusMayBeRunning;
        InspectAbacusButton.IsEnabled = !busy && abacusMayBeRunning;
        CloseAbacusButton.IsEnabled = !busy && abacusMayBeRunning;
    }

    private void ResetInspection(bool clearPath = true)
    {
        if (clearPath)
        {
            SourcePathTextBox.Clear();
        }

        sourceReport = null;
        workspaceResult = null;
        ValidationResultText.Text = "未検査";
        FileCountText.Text = "-";
        TotalSizeText.Text = "-";
        SourceArchitectureText.Text = "-";
        FingerprintText.Text = "-";
        ValidationMessagesText.Text = "";
        WorkspacePathText.Text = "作業用コピーはまだありません。";
        CreateWorkspaceButton.IsEnabled = false;
        LaunchAbacusButton.IsEnabled = false;
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

        if (abacusMayBeRunning)
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                "コピー側ABACUSが起動中です。先に「ABACUSを閉じる」で終了してから補助ソフトを閉じてください。",
                "ABACUSが起動中です",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        e.Cancel = true;
        operationCancellation?.Cancel();
        IsEnabled = false;
        await session.DisposeAsync();
        allowClose = true;
        Close();
    }
}
