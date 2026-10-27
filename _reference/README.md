# _reference（参考専用・AGPL）

Pleasanter 本体（**GNU AGPL v3**）は、AI（Claude・Copilot・Codex）が
スキーマ・保管先・挙動を事実確認するためだけに読む参照ソース。
本リポジトリでは追跡せず、必要なときに上流の `main` の先頭を取得する。
CI は参照ソースを使わず、checkout の `submodules: false` も維持する。

- 上流: <https://github.com/Implem/Implem.Pleasanter>
- 既定の取得先: リポジトリ直下の `_reference/Implem.Pleasanter/`（Git 管理外）
- 共有取得先: 環境変数 `PLEASANTER_REFERENCE_DIR`（その場所を直接読む）
- この `README.md` は引き続き Git で追跡する

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。**
  本ツールは DB・Local・AzureBlob に接続する別アプリであり、本体を改造しない
- ビルド対象・配布物・ソリューションへ含めない
- 取得した本体の作業ツリーを編集しない。本リポジトリへのコミット対象でもない
- 実機未検証の結果を動作確認済みと書かない。DB・Azure の接続・認証・読み書きは実機で確認する

> **本製品は AGPL と商用のデュアルライセンス。**
> 本体の著作権は自社に無く、コードを取り込むと商用側を提供できなくなる。
> 「どうせ AGPL 側があるのだから取り込んでよい」ではない。

## 取得と更新

事実確認を始める前に毎回実行する。Git と bash が必要で、Windows では Git Bash を使う。
一覧ファイルは置かず、本体の `main` 1 件だけを取得する。

```bash
bash scripts/sync-reference.sh
```

worktree 間で共有するときは、PowerShell から取得先を指定できる（絶対パスを推奨）。

```powershell
$env:PLEASANTER_REFERENCE_DIR = 'D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
```

未取得なら `main` を `git clone --depth 1` で浅く取得する。
既存なら `git fetch --depth 1 origin main` と `git checkout --detach FETCH_HEAD` で更新する。
終了時に Path・短い SHA・コミット日時・Status・完全な FullSHA・取得日時（UTC）・csproj の Version を出す。
Version が読めない場合は警告と `Version=unknown` を出し、版を推測しない。

変更済み・ステージ済み・未追跡ファイルがある作業ツリー、上流 URL が異なる origin、
別リポジトリや非空の取得先では停止する。通信失敗は失敗コードで終了し、既存の HEAD とファイルを維持する。
新規 clone は一時取得先で成功してから配置する。並行取得はロックで止める。
異常終了で `.sync-lock` ディレクトリが残った場合は、実行中の同期が無いことを確認してから空ディレクトリを取り除く。

`PLEASANTER_REFERENCE_DIR` 指定時は取得先の案内だけを出し、
`_reference/Implem.Pleasanter` にジャンクションやシンボリックリンクを作らない。
相対パスはリポジトリ直下を基準にする。共有取得先の更新中に他の AI が読まないよう作業を調整する。

## 事実の記録と再現性

サブモジュール・日次更新 workflow による追従を廃止し、必要時の取得へ切り替える。
AI 用の参照専用で CI は使わないため、ポインタ更新の PR や参照更新用 bot の迂回権限は不要。
再現性は gitlink ではなく、**文書に書く確認済み SHA** が担う。

文書へ事実を書くときは、完全なコミット SHA・該当ソースへの固定リンク・参照日を併記する。
現在の取得先が同じ SHA とは限らないので、過去の記述を最新ソースの根拠として扱わない。
`main` はリリース前の変更も含む。Version だけでリリース済みとは判断せず、
必要なら上流の `Pleasanter_*` タグとの対応も確認する。
再確認していない過去の事実の SHA や参照日を、自動で最新の値へ置き換えない。

このツールの過去の調査結果は [アーキテクチャ方針](../_documents/アーキテクチャ方針.md) にある。
DB の列・外部ストレージの名前・読み出しの判定などは、そこに記録した版のソース確認であり、
実機の Pleasanter・DB・Azure での動作を保証しない。

採用元（参照日: 2026-10-27）:

- [Template の配布手順][procedure]（SHA `3312f003fbe9827090e20bc643800824bdf30e22`）
- [git clone](https://git-scm.com/docs/git-clone)（浅い clone とブランチ指定）
- [git fetch](https://git-scm.com/docs/git-fetch)（`--depth` と `FETCH_HEAD`）

[procedure]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/blob/3312f003fbe9827090e20bc643800824bdf30e22/_documents/参照ソースのオンデマンド取得への配布手順.md
