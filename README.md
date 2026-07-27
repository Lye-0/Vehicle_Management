# Vehicle_Management
打倒ABACUSのための車両顧客管理ソフト

## 開発環境

開発環境では、ルートディレクトリで次のコマンドを実行します。

```powershell
pnpm dev
```

次の3つが同時に起動します。

- Firebase Auth Emulator（`http://127.0.0.1:9099`）
- Cloudflare Workers API（`http://127.0.0.1:8787`）
- Vite Web（`http://localhost:5173`）

API起動時に開発用ローカルD1の未適用マイグレーションを自動適用します。手動で適用する場合は次を実行してください。

```powershell
pnpm migrate:local
```

個別に起動する場合は、次を使用します。

```powershell
pnpm dev:auth
pnpm dev:api
pnpm dev:web
```

## 環境変数

環境変数の実ファイルはGitへコミットしません。次のファイルへ、退避している値を設定してください。

### Web

開発用は`apps/web/.env.development`、本番用は`apps/web/.env.production`です。

```text
VITE_APP_ENV
VITE_FIREBASE_API_KEY
VITE_FIREBASE_AUTH_DOMAIN
VITE_FIREBASE_PROJECT_ID
VITE_FIREBASE_STORAGE_BUCKET
VITE_FIREBASE_MESSAGING_SENDER_ID
VITE_FIREBASE_APP_ID
VITE_FIREBASE_AUTH_EMULATOR_URL（開発用のみ）
VITE_API_BASE_URL
```

### API

開発用は`apps/api/.dev.vars.development`、本番構成のローカル確認用は`apps/api/.dev.vars.production`です。

```text
APP_ENV
DATA_ENV
FIREBASE_PROJECT_ID
FIREBASE_AUTH_EMULATOR
FIREBASE_WEB_API_KEY
FIREBASE_ADMIN_SERVICE_ACCOUNT_JSON（本番の従業員再追加時のパスワード更新用Secret）
INITIAL_SETUP_KEY
CORS_ORIGIN
B2_ENDPOINT
B2_REGION
B2_BUCKET
B2_KEY_ID
B2_APPLICATION_KEY
```

`.dev.vars.production`はローカル確認用です。実際に本番Workerへ設定するSecretはCloudflare/Wrangler側で管理します。

## 本番用コマンド

本番用Webをビルドする場合：

```powershell
pnpm build:production
```

本番D1へマイグレーションを適用する場合：

```powershell
pnpm migrate:production
```

本番Workerへデプロイする場合：

```powershell
pnpm deploy:production
```

本番操作は、開発用の`pnpm dev`とは別の明示的なコマンドです。

