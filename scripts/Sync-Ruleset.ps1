<#
.SYNOPSIS
    .github/rulesets/protected-branches.json を GitHub リポジトリのルールセットへ作成・更新する。
.DESCRIPTION
    gh CLI でログイン済みで、リポジトリの管理者権限があること。同名のルールセットがあれば更新する。
    対象は develop と master。削除と強制 push を禁止し、PR と必須チェックを要求する。
    リポジトリの管理者は迂回できる（初期配置と緊急対応のため）。
.PARAMETER Repository
    owner/name。省略時は現在のリポジトリ。
.PARAMETER IncludeCodeQL
    必須チェックへ CodeQL を加える。public か、GHAS を有効にして CODEQL_ENABLED=true にしたリポジトリで使う。
#>
param(
    [string]$Repository = '',
    [switch]$IncludeCodeQL
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Repository) {
    $Repository = gh repo view --json nameWithOwner --jq .nameWithOwner
    if ($LASTEXITCODE -ne 0) { throw "リポジトリを特定できません。-Repository を指定してください。" }
}

$ruleset = Get-Content -LiteralPath (Join-Path $repoRoot '.github/rulesets/protected-branches.json') -Raw -Encoding utf8 | ConvertFrom-Json
if ($IncludeCodeQL) {
    $checks = ($ruleset.rules | Where-Object type -eq 'required_status_checks').parameters.required_status_checks
    $checks += [pscustomobject]@{ context = 'CodeQL 解析（C#）' }
    ($ruleset.rules | Where-Object type -eq 'required_status_checks').parameters.required_status_checks = $checks
}
$body = $ruleset | ConvertTo-Json -Depth 10

$existing = gh api "repos/$Repository/rulesets" --jq ".[] | select(.name == `"$($ruleset.name)`") | .id"
if ($LASTEXITCODE -ne 0) { throw 'ルールセットを取得できません。' }
if ($existing) {
    $body | gh api --method PUT "repos/$Repository/rulesets/$existing" --input - | Out-Null
    $action = '更新'
}
else {
    $body | gh api --method POST "repos/$Repository/rulesets" --input - | Out-Null
    $action = '作成'
}
if ($LASTEXITCODE -ne 0) { throw 'ルールセットを反映できません。' }
Write-Output "ルールセットを${action}しました: $Repository"
