#!/usr/bin/env bash
# 参照用サブモジュール `_reference/Implem.Pleasanter` のポインタを上流の最新
# リリースタグへ進め、Issue と Draft PR を出す。
# 日次のワークフロー（.github/workflows/reference-submodule-update.yml）から呼ぶ。
#
# **本文の文面をここに置くのは、YAML の中でヒアドキュメントが書けないため。**
# `run: |` の中は行頭が字下げされるので、閉じの `EOF` が行頭に来ず、
# 素のヒアドキュメントは終端しない。**文面を扱う処理はスクリプト側へ寄せる。**
#
# ⚠️ **サブモジュールは clone しない。** 索引の gitlink（mode 160000）を
#    `git update-index --cacheinfo` で直接書き換える。上流は大きく、
#    ポインタを進めるためだけに毎日取ってくる必要が無い。
#
# ⚠️ **ドキュメントの版記述は書き換えない。** `_reference/README.md` や
#    `_documents/アーキテクチャ方針.md` は「この版の実ソースを根拠にしている」と
#    書いている。**版だけ機械的に上げると、確認していない版を根拠にしたことになる。**
#    PR 本文へ確認先を列挙して、人がやり直す前提にする。
#
# 要る環境変数
#   GH_TOKEN     gh コマンド用
#   LATEST_TAG / LATEST_SHA / CURRENT_TAG / CURRENT_SHA
#                scripts/reference-submodule-check.sh の出力
#   BASE_BRANCH  PR の base（既定ブランチ。develop）
#
# 参照した一次情報（いずれも 2026-09-11 参照）
#   - git update-index --cacheinfo（gitlink は mode 160000）
#     https://git-scm.com/docs/git-update-index
#   - gh issue create / gh pr create
#     https://cli.github.com/manual/gh_issue_create
#     https://cli.github.com/manual/gh_pr_create
set -euo pipefail
cd "$(dirname "$0")/.."

: "${LATEST_TAG:?LATEST_TAG が要ります}"
: "${LATEST_SHA:?LATEST_SHA が要ります}"
: "${CURRENT_SHA:?CURRENT_SHA が要ります}"
BASE_BRANCH="${BASE_BRANCH:-develop}"
CURRENT_TAG="${CURRENT_TAG:-}"

version="${LATEST_TAG#Pleasanter_}"
branch="chore/reference-submodule-${LATEST_TAG}"
# 表示用。タグが付いていないコミットに固定されていることもある
current_label="${CURRENT_TAG:-$CURRENT_SHA}"

summary() {
    if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
        cat >>"$GITHUB_STEP_SUMMARY"
    else
        cat
    fi
}

# **毎日同じ PR を作らない。** 前日の PR が未マージなら何もしない
existing="$(gh pr list --head "$branch" --state open --json number --jq '.[0].number // ""')"
if [[ -n "$existing" ]]; then
    summary <<MARKDOWN
## 既に PR が出ています

\`$LATEST_TAG\` への更新は #$existing で進行中です。
MARKDOWN
    exit 0
fi

issue_url="$(
    gh issue create \
        --title "参照用サブモジュール Implem.Pleasanter を ${version} へ更新する" \
        --label "1-owner:automation" \
        --label "2-type:dependencies" \
        --label "3-area:pleasanter" \
        --body "$(
            cat <<MARKDOWN
## 背景

日次の検査（\`.github/workflows/reference-submodule-update.yml\`）が、
\`_reference/Implem.Pleasanter\` の固定位置が上流の最新リリースタグより
遅れていることを検出した。

| | タグ | コミット |
|---|---|---|
| いま | \`${CURRENT_TAG:-（タグ無し）}\` | \`$CURRENT_SHA\` |
| 上流の最新 | \`$LATEST_TAG\` | \`$LATEST_SHA\` |

このサブモジュールは事実確認のための参照専用（\`_reference/README.md\`）だが、
**古いままだと API 仕様・スキーマの確認結果が現行の Pleasanter と食い違う。**

## 対応内容

- サブモジュールのポインタを \`$LATEST_TAG\` へ進める（自動 PR を用意した）
- **版を書いてあるドキュメントを、その版で確認し直して揃える**（人の作業）

## 制約

- **コードの取り込みは行わない。** 参照ポインタの更新のみ
MARKDOWN
        )"
)"
issue_number="${issue_url##*/}"

git switch -c "$branch"
git update-index --cacheinfo "160000,$LATEST_SHA,_reference/Implem.Pleasanter"

git -c user.name="github-actions[bot]" \
    -c user.email="41898282+github-actions[bot]@users.noreply.github.com" \
    commit -m "$(
        cat <<MESSAGE
参照用サブモジュール Implem.Pleasanter を ${version} へ進める

固定位置が ${current_label} のままで、上流の最新リリースタグ ${LATEST_TAG} より
遅れていた。参照専用のサブモジュールが古いと、API 仕様やスキーマの確認結果が
現行の Pleasanter と食い違う。

検証したこと: 日次のワークフローが上流の Pleasanter_* タグを版順に並べ、
最大の版が指すコミット（${LATEST_SHA}）と固定位置が違うことを確認した。
サブモジュールはビルド対象外のため本体のビルドには影響しない。
コードの取り込みは行っていない。

版を書いてあるドキュメントは自動で書き換えていない（PR 本文を参照）。

Closes #${issue_number}
MESSAGE
    )"

git push origin "$branch"

# **Draft ＋ WIP: で出す。** Draft 解除＝レビュー可能、WIP: 除去＝マージ可能の
# 2 段のゲートとして使う（.github/copilot-instructions.md）。
# ドキュメントの版記述を人が揃えるまでは、どちらも外れない
gh pr create \
    --draft \
    --base "$BASE_BRANCH" \
    --head "$branch" \
    --title "WIP: 参照用サブモジュール Implem.Pleasanter を ${version} へ更新する" \
    --label "1-owner:automation" \
    --label "2-type:dependencies" \
    --label "3-area:pleasanter" \
    --body "$(
        cat <<MARKDOWN
## 概要

\`_reference/Implem.Pleasanter\` のポインタを
\`${current_label}\` から \`$LATEST_TAG\`（\`$LATEST_SHA\`）へ進める。

日次の検査（\`.github/workflows/reference-submodule-update.yml\`）が自動で出した。

## ⚠️ マージ前にやること

**版を書いてあるドキュメントは自動で書き換えていない。**
「この版の実ソースを根拠にしている」と書いてある文書の版だけ機械的に上げると、
**確認していない版を根拠にしたことになる。** 次を人が確認して揃えること。

- [ ] \`_reference/README.md\` の「固定位置」
- [ ] \`_documents/アーキテクチャ方針.md\` 冒頭の「根拠にした版」と、
      各節が引用するソースパスが新しい版にも存在すること
- [ ] \`README.md\` の技術スタックが指す版（対象フレームワークの変化も見る）

GitHub App の短期トークンで作成した PR のため、通常の CI が起動する。
必須チェックの成功を確認してからマージすること。

## 制約の確認

- **コードの取り込みは行っていない。** 参照専用のポインタ更新のみ
- サブモジュールはビルド対象・ソリューションに含まれないため、実装への影響は無い

Closes #${issue_number}
MARKDOWN
    )"

summary <<MARKDOWN
## 更新の PR を出しました

- \`${current_label}\` → \`$LATEST_TAG\`
- Issue #${issue_number}
MARKDOWN
