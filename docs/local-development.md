# ローカル開発環境

## 初回設定

1. `apps/web/.env.development.example` を `apps/web/.env.local` にコピーし、Firebase Webアプリの値を設定する。
2. `apps/api/.dev.vars.example` を `apps/api/.dev.vars` にコピーし、B2のキーを設定する。
3. `VITE_APP_ENV=development`、`APP_ENV=development`、`DATA_ENV=development`、`FIREBASE_AUTH_EMULATOR=true` を確認する。
4. 開発用Firebaseプロジェクトは `.firebaserc` の `vehicle-management-64` を使用する。

## 起動

Firebase Authentication Emulator:

```powershell
npx -y firebase-tools@latest emulators:start --only auth
```

D1マイグレーション:

```powershell
pnpm --filter api db:migrate:local
pnpm --filter api db:seed:development
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

開発用シードは `tools/seed-development.mjs` が `.dev.vars` を検査してから、`--local` を付けたD1へだけ適用します。`APP_ENV`、`FIREBASE_AUTH_EMULATOR`、`DATA_ENV` の組み合わせが開発用でない場合は停止します。`db:seed:local` は後方互換のエイリアスですが、本番シードとして使用しないでください。

## D1スキーマ変更

共有スキーマは `packages/database/src/schema.ts` にあります。

```powershell
pnpm --filter api db:generate
pnpm --filter api db:migrate:local
```

本番への適用は、Wrangler設定の `env.production.d1_databases` に本番D1の `database_id` を設定してから、`pnpm --filter api db:migrate:production` を使用します。開発用の `db:seed:development` を本番D1へ向けて実行してはいけません。

## 秘密情報

`.env.local` と `.dev.vars` はGitへコミットしません。B2のApplication KeyやFirebaseの秘密情報をテンプレート・ソースコードへ記載しないでください。

開発環境と本番環境の切り替え手順は [環境分離ガイド](./environment-separation.md) を参照してください。
