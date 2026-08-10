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
- 検証マニフェストを使った既存作業用コピーの再検証
- コピー側ABACUSの画像画面に対するUI Automation要素診断
- ABACUSを画面側で終了した場合の終了状態再確認

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

## 顧客・車両の紐付けプレビュー

顧客名だけでは自動統合しません。同じ正規化名の書類について、次のいずれかが一致する場合だけ同じ顧客候補にまとめます。

- 電話番号
- 住所
- 車台番号
- 車台番号との競合がない登録番号

同姓同名でも上記の根拠が一致しなければ別候補にし、競合一覧へ表示します。

車両は車台番号を最優先し、次に登録番号を使用します。同じ登録番号に異なる車台番号がある場合は、登録番号の再利用や入力差異の可能性があるため自動統合しません。同じ車両識別子が複数顧客に現れる場合も、名義変更・過去所有の可能性を含む競合として表示します。

このプレビューは候補と競合の確認専用であり、顧客・車両・書類をデータベースへ登録しません。

## 車両画像の抽出準備

「画像抽出準備」タブでは、作成済みの`ABACUS-Work-*`フォルダーを隣接する検証マニフェストおよび保存用原本と照合し、許可条件を満たした場合だけ起動対象にします。

FileMaker Runtimeは、コピー側ABACUSを起動・操作しただけでも使用した`abx-cs-??.ucs`の管理情報を書き換えます。再利用時は保存用原本が作成時のハッシュを維持していること、ファイル構成・サイズが同一であること、他の全ファイルが原本と一致することを検証します。原本直下の`abx-cs-英数字2文字.ucs`だけは、サイズ不変、1ファイルあたり差分4,096バイト以内、全対象の合計差分16,384バイト以内に限定して許容します。`sbx-*`、実行ファイル、DLL、TABなどの変更は拒否します。

Gate 5Aでは、コピー側ABACUSで車両画像を手動表示した後、LegacyHostが標準UI Automationで認識できる要素を最大30件まで読み取り診断します。画像出力、キー入力、クリック、ABACUSレコードの変更は行いません。診断結果を確認してから、次のGateで対象要素に限定した1件抽出方式を決定します。

実機診断ではFileMakerのMDI子ウィンドウまでしかUI Automationに公開されず、画像フィールドを個別認識できませんでした。Gate 5Bでは利用者がABACUS上で車両画像を手動選択して`Ctrl+C`を行い、補助ソフトはクリップボードの形式名、標準画像として取得可能か、画像寸法、ファイル参照の件数と拡張子だけを診断します。画像本体の保存やプレビューはまだ行いません。

Gate 5Cでは、Gate 5Bで標準画像として診断した同一クリップボード内容を1件だけPNGへ変換します。最大5,000万画素・256MBに制限し、PNGとして再読込できることと寸法一致を保存前に検証します。保存用原本・作業用コピーの内部は保存先にできず、毎回新しいファイル名を作成して既存ファイルを上書きしません。

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
