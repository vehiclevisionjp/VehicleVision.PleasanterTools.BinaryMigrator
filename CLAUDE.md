# Claude Code 向け指示

@.github/copilot-instructions.md

現在の仕様と未決定事項は `_documents/アーキテクチャ方針.md`、運用は `_documents/開発運用.md` を読むこと。

Pleasanter 本体は Git で追跡しない参照専用ソースであり、実装へ取り込まない。
挙動を確かめる前に `bash scripts/sync-reference.sh` で `_reference/Implem.Pleasanter` を
上流の `main` の先頭へ更新する。`PLEASANTER_REFERENCE_DIR` があればその取得先を直接読む。
CI は参照ソースを取得しない。文書には確認した完全なコミット SHA・固定リンク・参照日を残す。
実機未検証ならその旨を残す。正式な編集元は `.github/copilot-instructions.md`。

外部記憶は、必要になった時点で `<リポジトリ名>.ClaudeMemory`（Internal）を作って管理する。現時点では作っていない。作ったらここへ URL を書く。
