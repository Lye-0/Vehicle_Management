# ABACUSローカル補助ソフト

ABACUSからのデータ移行など、Windows上でのみ実行できる処理をWebアプリから分離して担当する.NET 10 WPFアプリです。

## Gate 1の範囲

- WPF補助ソフトの起動
- LegacyHostの別プロセス起動・停止
- ユーザー限定Named Pipeによる疎通確認
- WPF終了時のLegacyHost終了

この段階ではABACUS本体や保存用ABACUSフォルダーを読み書きしません。

## 構成

```text
apps/companion/
├── VehicleManagement.Companion.slnx
└── src/
    ├── VehicleManagement.Companion/   WPF本体・プロセス管理
    ├── VehicleManagement.LegacyHost/  ABACUS処理を隔離するプロセス
    └── VehicleManagement.LocalProtocol/ Named Pipe通信契約
```

LegacyHostのビット数はGate 2の検証前には固定しません。現在はSDK既定のアーキテクチャで動作します。

## ビルドと起動

リポジトリルートから実行します。

```powershell
dotnet build apps/companion/VehicleManagement.Companion.slnx
dotnet run --project apps/companion/src/VehicleManagement.Companion/VehicleManagement.Companion.csproj
```

GUIを表示せず通信・終了処理を検証する場合は、ビルド後の実行ファイルに`--self-test`を指定します。

```powershell
apps/companion/src/VehicleManagement.Companion/bin/Debug/net10.0-windows/VehicleManagement.Companion.exe --self-test
```

終了コード`0`なら、LegacyHostの起動、ハンドシェイク、ping、正常終了に成功しています。
