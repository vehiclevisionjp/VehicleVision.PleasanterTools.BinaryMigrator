# 共通リポジトリ指示

<!-- AGENT-RULES:BEGIN
     このブロックは scripts/sync-agents.sh で AGENTS.md へ複写する。 -->

## 必ず守る規約

1. 応答、Issue、PR、コミット、レビュー、コメントとドキュメントは日本語で書く。CLI の進行メッセージだけは英語でよい。
2. 作業ブランチは `develop` から分岐し、PR の base も `develop` とする。`master` はリリース確定用。
3. 作業に着手する前に、これからやること・やるべきこと（作業中に見つけたものを含む）を、先に Issue にする。会話の「残りの作業」も Issue に起こし、リポジトリをまたぐ作業は、中心のリポジトリに親の Issue を起こして各リポジトリの Issue から参照する。Issue を作ってから変更し、PR 本文に `Closes #N` を書く。初期リポジトリ作成のみ直接配置する。
4. 作業中の PR は Draft とし、タイトルに `WIP:` を付ける。Draft 解除はレビュー可能、WIP 除去はマージ可能の合図。
5. CI をスキップするコミット指定を使わない。実装変更後は Release ビルドと影響するテストを実行する。
6. Pleasanter 本体は AGPL v3 の参照専用ソース。必要時に取得し、Git では追跡しない。
   コピー・流用・リンク・プロジェクト参照せず、ビルドや配布物へ含めない。
   **本製品はデュアルライセンス（AGPL ＋ 商用）。本体の著作権は自社に無く、取り込むと商用側を提供できなくなる。**
7. API キーとパスワードはコード・設定ファイル・ログ・公開ファイル・ブラウザへ出さない。環境変数か Git 管理外の設定から読む。未検証を動作済みとして説明しない。
8. 依存 OSS は寛容ライセンス（MIT / BSD 系 / Apache-2.0）に限定する。追加時にライセンスを一次情報で確認し、NOTICE とロックファイルを更新する。**デュアルライセンスの商用側を提供できなくなるため、GPL・AGPL・LGPL の依存を入れない。**
9. 作業前に [共通指示](../.github/copilot-instructions.md) の全文を読む。外部仕様は一次情報で確認し、出典と参照日を残す。
10. README は利用者向けの取扱説明・製品説明の入口にする。ビルド・テスト・CI・エージェント運用など開発者向けの説明は [開発ガイド](../_documents/開発ガイド.md) と [開発運用](../_documents/開発運用.md) に分離する。
11. `if` / `else` / `for` / `foreach` / `while` / `using` / `lock` などの本文の `{}` を省略しない（1 行で済む本文も同じ）。C# は `.editorconfig` とビルドで、TypeScript・Svelte は ESLint の `curly` で強制する。
<!-- AGENT-RULES:END -->

## プロジェクト

Pleasanter に添付されたファイル（`Binaries`）の保管先を、Rds・Local・AzureBlob の間で移すコマンドラインツール。C#／.NET 10。
移行元は Rds・Local・AzureBlob、移行先は Local・AzureBlob（**Rds への書き戻しは対象外**）。**既定では移行元を消さない**（消すのは明示のオプション）。
現状と未決定事項は [アーキテクチャ方針](../_documents/アーキテクチャ方針.md) を確認する。

## 作業の進め方

Issue → `develop` から作業ブランチ → 実装と検証 → push → Draft PR まで進める。認証・権限・外部連携・利用者に見える挙動の変更はレビューを待つ。CI で検証できる開発環境整備やドキュメント修正は検証後にマージできる。

日本語のコミットを意図ごとに分け、本文に理由と検証結果を書く。並行作業は `.worktrees/` 内の `git worktree`、一時成果物は `temp/yyyyMMdd_内容/` に置く。

共通規約の編集元はこのファイル。`scripts/sync-agents.sh` で AGENTS.md を再生成し、`--check` で確認する。Claude のローカル許可や秘密は追跡しない。

Claude や Codex が外部記憶を必要とするときは、個別のリポジトリを作らず、Internal の `VehicleVision.PleasanterTools.ClaudeMemory` に `BinaryMigrator/` フォルダを作って管理する。本体へ記憶ファイルを置かない。正式な規約は常にこのファイルへ残し、記憶と食い違ったら記憶側を直す。規約を変えたら記憶に古い記述が無いか確認する。

Pleasanter の挙動を確かめる前に `bash scripts/sync-reference.sh` で
`_reference/Implem.Pleasanter` を上流の `main` の先頭へ更新し、実ソースと実機で確認する。
`PLEASANTER_REFERENCE_DIR` を指定した場合は、その取得先を直接読む（ローカルへのリンクは作らない）。
CI は参照ソースを取得しない。
文書には確認した完全なコミット SHA・ソースへの固定リンク・参照日を残す。
再確認していない過去の根拠は自動で更新しない。実機未検証ならその旨を残す。
取得スクリプトの英語コンソール出力は規約 1 の例外とする。

詳細は [開発運用](../_documents/開発運用.md)、[参照ソース](../_reference/README.md)、[ライセンス](../_documents/ライセンス.md) を読む。
