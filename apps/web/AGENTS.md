# Web 開発ガイド

`apps/web` は、車両顧客管理システムのフロントエンドです。
このファイルは、フロントエンド固有の長期的なルールを定義します。
リポジトリルートの `AGENTS.md` と併せて適用してください。

## 技術構成

- React 19 + TypeScript
- Vite（ビルドツール）
- `vite` / `vite preview` による開発・プレビュー
- Lint：`oxlint`（`.oxlintrc.json`）
- 認証：Firebase Authentication（Email/Password、Google、開発用匿名ログイン）
- API 通信：Cloudflare Workers API への `fetch`（`src/lib/api.ts`）
- 帳票：PDF 生成（`pdf-lib`）、SVG → Canvas レンダリング、印刷（`window.print`）
- OCR：添付ファイル用 `tesseract.js`
- アイコン：`lucide-react`

## ディレクトリの役割

- `src/App.tsx`：認証状態、組織切替、サイドバー、メインタブの制御。
- `src/components/`：ページコンポーネント。
  - `DashboardCalendar`、`CustomerVehiclePage`、`SalesPage`、`MaintenancePage`、`InspectionSchedulesPage`、`PaymentsPage`、`SettingsPage` など。
- `src/lib/`：共通処理。
  - `api.ts`：API クライアント、認証トークン、組織 ID ヘッダー。
  - `auth.ts`：Firebase Authentication 操作。
  - `firebase.ts`：Firebase 初期化、Auth Emulator 接続。
  - `pdf.ts`、`print.ts`：PDF 生成、印刷。
  - `salesEstimate.ts`、`salesEstimateSheet.ts`、`maintenanceStatement.ts`：帳票レイアウト計算。
  - `customerApi.ts`、`salesApi.ts`、`maintenanceApi.ts` など：各領域 API。
- `src/assets/`：フォント、画像などの静的アセット。
- `public/`：favicon など、ビルド時にそのまま公開されるファイル。
- `index.html`：アプリケーションのエントリポイント。

## 実装ルール

- 既存の React 構成、コンポーネント分割、命名、スタイルを優先してください。
- 指示されていない UI デザイン、配置、色、余白、文言を変更しないでください。
- 既存のコンポーネントや共通処理を再利用してください。
- 大規模なコンポーネント分割や全面的な書き換えを勝手に行わないでください。
- TypeScript の型安全性を維持してください。`any` や型アサーションを安易に使用しないでください。
- API レスポンスや共有型を推測しないでください。
- 既存の API クライアント、認証、状態管理、エラー処理方式に合わせてください。
- 新しい依存関係は必要性を確認してから追加してください。
- `public`、`assets`、生成物、ビルド成果物の扱いを既存方針に合わせてください。
- `dist` や `node_modules` を直接編集しないでください。
- フォント、画像、PDF、印刷関連の変更では、既存仕様への影響を確認してください。

## Firebase と認証

- Firebase クライアントの設定は `src/lib/firebase.ts` で行われます。
- 開発環境では `VITE_FIREBASE_AUTH_EMULATOR_URL` 経由で Auth Emulator に接続します。
- 本番環境では Emulator URL を使用しません。
- 匿名ログインは開発時のみ有効です。

## API 通信

- API 呼び出しは `src/lib/api.ts` の `apiFetch` / `apiFetchBlob` を使用してください。
- 認証トークンは `Authorization: Bearer` ヘッダー、組織 ID は `X-Organization-Id` ヘッダーで付与されます。
- API エンドポイントやレスポンス型は `apps/api` 側の実装と整合性を保ってください。

## Web 側の検証

以下は `apps/web/package.json` で定義されているコマンドのみを使用してください。

```powershell
# 開発サーバー起動
pnpm dev

# 型チェック + ビルド
pnpm build

# 本番ビルド
pnpm build:production

# Lint
pnpm lint

# ビルド成果物のプレビュー
pnpm preview
```

- `apps/web` にはテストスクリプトが存在しないため、テスト実行は行いません。
- ブラウザ操作や画面の視覚検証は、ユーザーが明示的に依頼した場合のみ行ってください。
- UI 変更でも、まず `pnpm lint` と `pnpm build` を実行してください。
- CLI 検証だけでは視覚的な正しさを保証できない場合は、その限界を明記してください。

## 帳票・印刷・フォント

- PDF 生成は `pdf-lib` と `src/assets/fonts/NotoSansCJKjp-Regular.otf` を使用しています。
- 帳票レイアウトは SVG 化して Canvas から PNG として埋め込む方式です。
- 印刷は `src/lib/print.ts` の `window.print` ラッパーで行います。
- レイアウト、フォント、色、余白を変更する場合は、印刷・プレビュー両方への影響を確認してください。
