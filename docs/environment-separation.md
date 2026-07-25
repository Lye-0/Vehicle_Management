# 開発・本番環境の分離

このプロジェクトでは、開発環境と本番環境で認証、データベース、添付ファイル保存先を分けます。

| 対象 | 開発 | 本番 |
| --- | --- | --- |
| Web認証 | Firebase Auth Emulator | 本番Firebase Authentication |
| Firebaseプロジェクト | `vehicle-management-64` | 開発用とは別のプロジェクトID |
| D1 | WranglerのローカルD1 | `vehicle-management-db-production` |
| B2 | `vehicle-management-64-dev` | 開発用とは別の本番バケット |
| API環境 | `APP_ENV=development` / `DATA_ENV=development` | `APP_ENV=production` / `DATA_ENV=production` |
| 開発用匿名ログイン | 表示する | 表示しない |
| 開発用シード | 実行可能 | 実行しない |

## 仮データの扱い

顧客、車両、販売書類、整備書類、入金のサンプルデータは `apps/api/seed/dev.sql` にだけ置きます。画面側にはサンプル一覧を固定で持たせず、APIの結果が空の場合は空状態を表示します。

開発シードを適用するコマンドは次の一つです。

```powershell
pnpm --filter api db:seed:development
```

このコマンドは次を満たさない場合に停止します。

- `apps/api/.dev.vars` が存在する
- `APP_ENV=development`
- `FIREBASE_AUTH_EMULATOR=true`
- `DATA_ENV=production` ではない
- D1実行が必ず `--local` である

本番マイグレーションは、初回セットアップに必要な空の組織行などのスキーマ準備だけを行います。顧客・車両・書類の開発用サンプルは含めません。

## 本番環境を作成する手順

本番のFirebaseプロジェクトID、D1のdatabase ID、B2バケット名、公開Web/APIドメインは、実際の運用環境に合わせて設定してください。これらは未提示のため、リポジトリ内にはプレースホルダーを置いてあります。

### 1. Firebase

`.firebaserc.example` の `production` を本番FirebaseプロジェクトIDへ置き換え、必要なら次のコマンドで alias を登録します。

```powershell
npx -y firebase-tools@latest use --add
```

本番Firebaseでは Email/Password と Google の認証プロバイダを有効にし、本番Webドメインを承認済みドメインへ追加します。開発プロジェクトの認証ユーザーやAuth Emulatorのデータを本番へ移行しないでください。

### 2. D1

本番用D1を別名で作成します。既存の開発D1のIDを流用しません。

```powershell
cd apps/api
pnpm exec wrangler d1 create vehicle-management-db-production
```

出力されたdatabase IDを `apps/api/wrangler.jsonc` の `env.production.d1_databases[0].database_id` に設定します。現在の `00000000-0000-0000-0000-000000000000` は未設定を示すプレースホルダーです。

### 3. B2

開発用とは別のB2バケットを作成し、本番Workerの `production` 環境へ値を登録します。Application KeyはソースコードやGitへ保存しません。

```powershell
cd apps/api
pnpm exec wrangler secret put B2_ENDPOINT --env production
pnpm exec wrangler secret put B2_REGION --env production
pnpm exec wrangler secret put B2_BUCKET --env production
pnpm exec wrangler secret put B2_KEY_ID --env production
pnpm exec wrangler secret put B2_APPLICATION_KEY --env production
pnpm exec wrangler secret put FIREBASE_WEB_API_KEY --env production
pnpm exec wrangler secret put INITIAL_SETUP_KEY --env production
```

### 4. APIの本番設定

`apps/api/wrangler.jsonc` の次の値を実値へ置き換えます。

- `FIREBASE_PROJECT_ID`
- `CORS_ORIGIN`
- `env.production.d1_databases[0].database_id`
- D1名、Worker名（必要に応じて）

本番環境では `FIREBASE_AUTH_EMULATOR=false`、`DATA_ENV=production` が必要です。開発Firebaseプロジェクト、localhost、開発B2バケットを指定した設定はAPI起動時に拒否されます。

### 5. Webの本番設定

本番Webの環境変数は `apps/web/.env.production.example` を基準に、VercelのProduction Environmentへ登録します。`VITE_FIREBASE_AUTH_EMULATOR_URL` は本番設定に追加しません。

- `VITE_APP_ENV=production`
- 本番Firebase Webアプリの設定値
- 本番APIのURLを `VITE_API_BASE_URL` に設定

開発用の `.env.local` を本番デプロイの設定として使用しないでください。

### 6. マイグレーション、デプロイ、初回セットアップ

```powershell
cd apps/api
pnpm run db:migrate:production
pnpm run deploy:production
```

デプロイ後に `/health` を確認し、`database`、`firebaseAuth`、`objectStorage` が `configured` になっていることを確認します。本番では開発シードを実行せず、ログイン画面から最初の管理者が本番の初回セットアップを行います。

## 環境分離のチェック項目

- 本番FirebaseプロジェクトIDが `vehicle-management-64` と異なる
- 本番D1のdatabase IDが開発D1と異なる
- 本番B2バケットが `vehicle-management-64-dev` と異なる
- 本番Workerに `FIREBASE_AUTH_EMULATOR=true` がない
- 本番WebにAuth Emulator URLがない
- 本番に開発用シードデータが存在しない
- 開発用匿名ログインが本番画面に表示されない
