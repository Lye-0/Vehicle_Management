# ABACUSローカル補助ソフト

ABACUSからのデータ移行など、Windows上でのみ実行できる処理をWebアプリから分離して担当する.NET 10 WPFアプリです。

## 実装済みの範囲

- WPF補助ソフトの起動
- LegacyHostの別プロセス起動・停止
- ユーザー限定Named Pipeによる疎通確認
- WPF終了時のLegacyHost終了
- 保存用ABACUSフォルダーの読取検査
- SHA-256検証付き作業用コピーの作成
- コピー後の保存用原本の再検証
- コピー側ABACUSの起動
- x64 LegacyHostからのUI Automation認識確認
- 販売・整備TABファイルの構造分析

保存用原本は読み取り専用で扱い、ABACUSを起動するのは検証済みの作業用コピーだけです。

## 構成

```text
apps/companion/
├── VehicleManagement.Companion.slnx
└── src/
    ├── VehicleManagement.Companion/   WPF本体・プロセス管理
    ├── VehicleManagement.LegacyHost/  ABACUS処理を隔離するプロセス
    └── VehicleManagement.LocalProtocol/ Named Pipe通信契約
```

LegacyHostのビット数はまだ固定していません。Gate 2でx64 LegacyHostから32bit ABACUSをUI Automationできることを目視確認した後に決定します。

## 作業用コピーの安全策

- 必須ファイルの存在、ファイル数、合計サイズ、実行ファイル形式を検査します。
- リンクや再解析ポイントを拒否し、フォルダー外のファイルを読み込みません。
- 全ファイルのSHA-256を計算します。
- コピー先には毎回新しい`ABACUS-Work-yyyyMMdd-HHmmss`フォルダーを作り、既存ファイルを上書きしません。
- コピーした各ファイルを検証し、最後にコピー全体と保存用原本を再検査します。
- コピー先の隣に検証用の`.manifest.json`を保存します。
- キャンセルまたは失敗したコピーは起動対象にしません。

1GBを超えるファイルを複数回読み取るため、検査とコピーには時間がかかります。処理中もキャンセルできます。

## 書類データ分析

| ファイル | 内容 | 列数 | 主要な識別列（0始まり） |
| --- | --- | ---: | --- |
| `BackUp-2.tab` | 販売書類 | 102 | 顧客名0、登録番号13、車台番号22、書類番号51 |
| `BackUp-3.tab` | 整備書類 | 158 | 顧客名0、登録番号11、車台番号14、書類番号77 |

パーサーはShift-JISの不正バイト、列数違反、上限を超えるファイル・行・フィールド、未許可の制御文字、リンクを拒否します。ABACUS内で確認されたU+0004、U+000B、U+001Dだけは既知の内部区切りとして受け付け、後続変換で空白へ正規化します。

顧客名が空欄の行は構造エラーにはせず、取込候補から除外して件数を集計します。車両候補件数は車台番号、登録番号、顧客＋車両情報の順で保守的に算出しますが、この段階では顧客や車両を統合しません。

GUIを使わず、個人情報を出力せずに集計だけを確認する場合は次を使用します。

```powershell
dotnet run --project apps/companion/src/VehicleManagement.AbacusImport.Cli/VehicleManagement.AbacusImport.Cli.csproj -- "<ABACUSフォルダー>"
```

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
