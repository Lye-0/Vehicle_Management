# ローカル開発環境

## 初回設定

1. `apps/web/.env.example` を `apps/web/.env.local` にコピーし、Firebase Webアプリの値を設定する。
2. `apps/api/.dev.vars.example` を `apps/api/.dev.vars` にコピーし、B2のキーを設定する。
3. Firebaseプロジェクトは `.firebaserc` の `vehicle-management-64` を使用する。

## 起動

Firebase Authentication Emulator:

```powershell
npx -y firebase-tools@latest emulators:start --only auth
```

D1マイグレーション:

```powershell
pnpm --filter api db:migrate:local
pnpm --filter api db:seed:local
```

API:

```powershell
pnpm --filter api dev
```

Web:

```powershell
pnpm --filter web dev
```

APIの確認:

```powershell
Invoke-RestMethod http://127.0.0.1:8787/health
```

ローカル開発時だけ `APP_ENV=development` と `FIREBASE_AUTH_EMULATOR=true` を設定します。エミュレーターのトークンを署名検証なしで受け入れるため、本番環境では必ず無効にしてください。

## D1スキーマ変更

共有スキーマは `packages/database/src/schema.ts` にあります。

```powershell
pnpm --filter api db:generate
pnpm --filter api db:migrate:local
```

リモート適用は、Wrangler設定に本番D1の `database_id` を設定してから、明示的に `--remote` を使用します。

## 秘密情報

`.env.local` と `.dev.vars` はGitへコミットしません。B2のApplication KeyやFirebaseの秘密情報をテンプレート・ソースコードへ記載しないでください。
