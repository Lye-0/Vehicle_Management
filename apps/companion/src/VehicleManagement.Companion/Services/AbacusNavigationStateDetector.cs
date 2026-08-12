using VehicleManagement.LocalProtocol;

namespace VehicleManagement.Companion.Services;

/// <summary>
/// ABACUSの画面遷移を、LegacyHostから取得した読み取り専用の子ウィンドウ情報だけで分類します。
/// この分類は操作を実行せず、画像やCSVの内容も読み取りません。
/// </summary>
public enum AbacusNavigationState
{
    NotRunning,
    MainMenu,
    VehicleList,
    VehicleManagement,
    ExpandedImage,
    Unknown,
}

public sealed record AbacusNavigationSnapshot(
    AbacusNavigationState State,
    string Label,
    string Detail,
    int? ProcessId,
    long? MainWindowHandle,
    string? MainWindowTitle,
    IReadOnlyList<LegacyNativeWindowInfo> VisibleChildWindows)
{
    public bool IsKnown => State is not AbacusNavigationState.Unknown;
}

public sealed class AbacusNavigationStateDetector
{
    public AbacusNavigationSnapshot Detect(AbacusRuntimeSnapshot runtime)
    {
        if (!runtime.ProcessId.HasValue ||
            runtime.Status is "abacus-not-running" or "abacus-exited")
        {
            return Create(
                AbacusNavigationState.NotRunning,
                "ABACUS未起動",
                "ABACUSプロセスを確認できません。",
                runtime,
                []);
        }

        var visibleWindows = (runtime.NativeWindows ?? [])
            .Where(window => window.IsVisible && window.NativeWindowHandle != 0)
            .ToArray();

        if (visibleWindows.Length == 0)
        {
            return Create(
                AbacusNavigationState.MainMenu,
                "ABACUSメニュー",
                "ABACUSのメインメニューを表示している状態と判定しました。",
                runtime,
                visibleWindows);
        }

        var imageWindow = visibleWindows.FirstOrDefault(window => IsWindow(window, "abx-cs-sk.ucs"));
        if (imageWindow is not null)
        {
            return Create(
                AbacusNavigationState.ExpandedImage,
                "拡大画像",
                $"画像表示ウィンドウ {imageWindow.Title} を確認しました。「拡大表示」後の状態です。",
                runtime,
                visibleWindows);
        }

        var vehicleListWindow = visibleWindows.FirstOrDefault(window => IsWindow(window, "abx-cs-li.ucs"));
        if (vehicleListWindow is not null)
        {
            return Create(
                AbacusNavigationState.VehicleList,
                "車両一覧",
                $"車両一覧ウィンドウ {vehicleListWindow.Title} を確認しました。対象行を1回クリックして移動できます。",
                runtime,
                visibleWindows);
        }

        var vehicleManagementWindow = visibleWindows.FirstOrDefault(window => IsWindow(window, "abx-cs-mn.ucs"));
        if (vehicleManagementWindow is not null)
        {
            return Create(
                AbacusNavigationState.VehicleManagement,
                "車両管理画面（一覧・詳細）",
                $"車両管理ウィンドウ {vehicleManagementWindow.Title} を確認しました。一覧と詳細は画面内容の確認が必要です。",
                runtime,
                visibleWindows);
        }

        var observedNames = string.Join(
            "、",
            visibleWindows
                .Select(window => string.IsNullOrWhiteSpace(window.Title) ? window.ClassName : window.Title)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8));
        return Create(
            AbacusNavigationState.Unknown,
            "画面を判定できません",
            string.IsNullOrWhiteSpace(observedNames)
                ? "既知のABACUS画面識別子が見つかりませんでした。"
                : $"既知の画面識別子が見つかりませんでした（確認できた子ウィンドウ: {observedNames}）。",
            runtime,
            visibleWindows);
    }

    private static bool IsWindow(LegacyNativeWindowInfo window, string title) =>
        string.Equals(window.Title.Trim(), title, StringComparison.OrdinalIgnoreCase);

    private static AbacusNavigationSnapshot Create(
        AbacusNavigationState state,
        string label,
        string detail,
        AbacusRuntimeSnapshot runtime,
        IReadOnlyList<LegacyNativeWindowInfo> visibleWindows) =>
        new(
            state,
            label,
            detail,
            runtime.ProcessId,
            runtime.WindowHandle,
            runtime.WindowTitle,
            visibleWindows);
}
