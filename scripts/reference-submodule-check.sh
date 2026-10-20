#!/usr/bin/env bash
# 参照用サブモジュール `_reference/Implem.Pleasanter` の固定位置が、
# 上流の最新リリースタグと一致しているかを見る。
#
# **なぜタグを見るのか。** このサブモジュールは API 仕様・スキーマ・挙動の
# 事実確認にのみ使う参照専用で、`_reference/README.md` の方針どおり
# **リリースタグへ固定する。** 未タグのコミットへ動かすと、
# 「どの版の事実を根拠にしたか」が言えなくなる。
#
# ⚠️ **`Release_*` 系のタグは使わない。** 2020 年で更新が止まっており、
#    版の新しさを表していない（`Release_1190` が 2020-11-27、
#    `Pleasanter_1.5.8.1` が 2026-09-10。2026-09-11 に実測）。
#    見るのは `Pleasanter_<x>.<y>.<z>.<w>` の形のタグだけ。
#
# ⚠️ **上流のタグは注釈付きタグ（annotated tag）である。** `git ls-remote --tags` は
#    タグオブジェクトの SHA と、`^{}` を付けた行に指し先のコミット SHA を出す。
#    **サブモジュールのポインタはコミット SHA でなければならない**ので、
#    `^{}` の行だけを採る（2026-09-11 に実測）。
#
# 出力（`$GITHUB_OUTPUT` があればそこへ、無ければ標準出力へ）
#   latest_tag    上流の最新リリースタグ
#   latest_sha    そのタグが指すコミット SHA
#   current_tag   いま固定しているコミットに付いたリリースタグ（無ければ空）
#   current_sha   いま固定しているコミット SHA
#   needs_update  更新が要るなら true、最新なら false
#
# 参照した一次情報（いずれも 2026-09-11 参照）
#   - git ls-remote / gitrevisions の `^{}`（peel）
#     https://git-scm.com/docs/git-ls-remote
#     https://git-scm.com/docs/gitrevisions
#   - git ls-tree（gitlink は mode 160000）
#     https://git-scm.com/docs/git-ls-tree
set -euo pipefail
cd "$(dirname "$0")/.."

SUBMODULE_PATH="_reference/Implem.Pleasanter"
UPSTREAM_URL="https://github.com/Implem/Implem.Pleasanter.git"

# いま固定しているコミット。gitlink は ls-tree の 3 列目に出る
current_sha="$(git ls-tree HEAD "$SUBMODULE_PATH" | awk '$2 == "commit" { print $3 }')"
if [[ -z "$current_sha" ]]; then
    echo "::error::$SUBMODULE_PATH がサブモジュールとして登録されていません。" >&2
    exit 1
fi

# 上流のリリースタグを「版<TAB>コミットSHA<TAB>タグ名」で並べ、版順に整える。
#
# **`sort -V` を素の版文字列へ掛ける。** タグ名ごと渡すと接頭辞が比較に混ざる
tags="$(
    git ls-remote --tags "$UPSTREAM_URL" 'refs/tags/Pleasanter_*' |
        sed -n 's|^\([0-9a-f]\{40\}\)\trefs/tags/Pleasanter_\([0-9][0-9.]*\)\^{}$|\2\t\1|p' |
        sort -V
)"

if [[ -z "$tags" ]]; then
    echo "::error::上流に Pleasanter_* のリリースタグが見つかりません。取得先: $UPSTREAM_URL" >&2
    exit 1
fi

latest_line="$(printf '%s\n' "$tags" | tail -n 1)"
latest_version="${latest_line%%$'\t'*}"
latest_sha="${latest_line##*$'\t'}"
latest_tag="Pleasanter_${latest_version}"

# いま固定しているコミットにタグが付いているか（付いていないこともある）
current_tag="$(
    printf '%s\n' "$tags" |
        awk -v sha="$current_sha" -F '\t' '$2 == sha { print "Pleasanter_" $1 }' |
        tail -n 1
)"

if [[ "$current_sha" == "$latest_sha" ]]; then
    needs_update=false
else
    needs_update=true
fi

emit() {
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
        printf '%s\n' "$1" >>"$GITHUB_OUTPUT"
    else
        printf '%s\n' "$1"
    fi
}

emit "latest_tag=$latest_tag"
emit "latest_sha=$latest_sha"
emit "current_tag=$current_tag"
emit "current_sha=$current_sha"
emit "needs_update=$needs_update"
