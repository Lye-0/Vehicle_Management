# 未利用従業員の再追加

従業員削除は組織への所属を削除する。Firebase Authアカウントと、auth_accountsの作成元組織・初期パスワード発行時のFirebase更新日時は保持する。

再追加は、作成元組織一致、初回変更未完了、現在の所属なし、Firebase UID/メール一致、password単独プロバイダ、未確認メール、未停止、発行時からパスワード更新なしの場合だけ許可する。メールや招待の一致だけで作成元を推測しない。既存データの新しい項目はNULLであり、勝手に再発行対象にしない。

## 本番反映

1. APIテスト、Web build/lint、Worker dry-runを確認。
2. 対象Firebaseプロジェクトで専用サービスアカウントに `firebaseauth.users.get` と `firebaseauth.users.update` を付与し、JSON認証情報をWorker secret `FIREBASE_ADMIN_SERVICE_ACCOUNT_JSON` へ設定。鍵やトークンはログ・Gitに保存しない。設定がない場合、再追加は503で停止し、パスワードを変更しない。
3. `0002_employee_reissue.sql` を本番D1へ適用してからWeb/APIを反映。追加列のみであり、既存所属・パスワードは変わらない。
4. 確認済みの旧アカウントだけ、下記の補正ツールをdry-runしてから適用。
5. 管理者が対象者を再追加し、新しい初期パスワードを本人に渡す。従業員一覧の「初回変更待ち」は本人のパスワード変更完了で消える。

## 旧アカウントの補正

`node tools/repair-employee-origin.mjs --project <Firebase project> --organization <organization id> --uid <verified UID> --email <verified email> --firebase-cli-lib <installed firebase-tools/lib>`

既定は読み取り専用。`--apply` を加えた場合だけ指定UIDの作成元と発行時更新日時を補う。現在の所属、初回変更状態、Firebaseの作成時からのパスワード更新を再検証し、条件不一致では停止する。パスワードと所属は変更しない。補正済みの場合は再実行しても書き込まない。対象UID・組織は運用者が確認する。組織数が1であることを通常APIの権限判断には使用しない。

## 通信障害時

FirebaseとD1の間に分散トランザクションはない。D1内の所属・プロフィール・初回変更状態・招待無効化は単一batchで保存する。初回変更と再発行は `initial_password_operation` の条件付き更新で排他する。

Firebaseへの更新開始後に結果が不明になった場合、成功レスポンスやパスワード表示は行わず、operationを残して再発行を停止する。運用者がFirebase/D1を再照合してから復旧する。operationを時間経過だけで消して再試行しない。成功応答がブラウザに届かなかった場合は一覧を再読み込みする。初期パスワードが手元にない場合は、未利用の所属を削除して再追加する。本人のパスワード変更が済んでいたら再発行せず、本人のパスワード再設定を使用する。

起動待機画面のタイムアウト・遷移処理は今回の修正対象外。
