# VehicleVision.PleasanterTools.BinaryMigrator

Pleasanter に添付されたファイル（`Binaries`）の保管先を、**Rds・Local・AzureBlob の間で移す**ためのコマンドラインツールです。
Pleasanter 本体の設定（`BinaryStorage.json` の `Provider`）を切り替える前後に、既存のファイルを新しい保管先へ移すときに使います。

> **実機での確認はまだ行っていません。** 実機の Pleasanter・DB（SQL Server・PostgreSQL・MySQL）・Azure Blob に対する動作確認はしておらず、
> 自動テストは DB・Azure へ接続しない範囲（メモリ上の偽の保管先、Local の一時フォルダ、SQL の組み立て、引数の解析など）です。
> **本番で使う前に、必ずバックアップを取り、検証環境で試し、`--dry-run` で内容を確かめてください。**
> 仕様と事実確認の結果は [アーキテクチャ方針](_documents/アーキテクチャ方針.md) にあります（未決定の事項もあります）。

## 移行できる組み合わせ

| 移行元 | 移行先 |
| --- | --- |
| Rds（`Binaries` 表の `Bin` など） | Local、AzureBlob |
| Local | AzureBlob |
| AzureBlob | Local |

**Rds への書き戻しは対象外です。**

対象は、添付ファイル（`Attachments/{Guid}`）、画像（`Images/{Guid}`）、サイト画像（`SiteImage/…`）、テナント画像（`TenantImage/…`）です。
エクスポートの一時データや、履歴（`Binaries_history`）・論理削除（`Binaries_deleted`）の行は対象外です（詳しくはアーキテクチャ方針）。

## 安全上の注意

- **まず `--dry-run`**。何をどこへ移すかを表示するだけで、何も書かず、消さず、保管先のフォルダも作りません
- **バックアップを取ってから実行してください。** 特に `--delete-source` を使うときは、移行元（DB・フォルダ・コンテナー）のバックアップが前提です
- **既定では移行元を消しません。** 消すのは `--delete-source` を付けたときだけで、`--yes` も要ります
- コピーしたあとに、移行先を読み戻して**大きさと SHA-256 を確かめます**。確かめられなかったものは失敗として数え、移行元を消しません
- 途中で止めても、同じコマンドを再実行すれば、移行先に同じ大きさ・同じハッシュのものがあるものはとばして続きから移します
- **Pleasanter 本体の `BinaryStorage.json` は書き換えません。** 移行が済み、移行先で読めることを確かめてから、利用者が `Provider` を切り替えてください
- パスワードやキーはコマンドラインへ直接書けません（環境変数・ファイル・標準入力で渡します）。ログ・コンソールにも出しません

### `--delete-source` の意味

移行先で整合を確かめられたものについてだけ、移行元を消します。

| 移行元 | 消す内容 |
| --- | --- |
| Rds | 移した列（`Bin`。サイト画像は `Thumbnail`・`Icon` も）を **NULL にする UPDATE**。行は残る。書き込み権限が要る |
| Local | そのファイルを削除（空になったフォルダは残る） |
| AzureBlob | そのブロブを削除 |

Pleasanter は、行の `Bin` が NULL でなければ Rds の値を返し、NULL なら外部ストレージ（`Provider` で指定したもの）から読みます。
このため、**Rds から外部へ移す手順は次を推奨します。**

1. `--dry-run` で確認する
2. `--delete-source` なしで移す（Pleasanter は引き続き Rds の `Bin` から返すので、壊れない）
3. `BinaryStorage.json` の `Provider` を移行先へ切り替えて、Pleasanter を再起動し、ファイルが開けることを確かめる
4. 必要なら、同じコマンドに `--delete-source --yes` を付けて再実行する（コピー済みのものは、ハッシュを確かめてから `Bin` を NULL にする）

`Bin` を NULL にしたまま `Provider` が `Rds` のままだと、そのファイルは開けなくなります。

## 導入

.NET 10 ランタイムが必要です。次のどちらかで入れます。

- **dotnet tool（NuGet）**: `dotnet tool install --global VehicleVision.PleasanterTools.BinaryMigrator` で入れ、`pleasanter-binary-migrator` コマンドで実行します（更新は `dotnet tool update --global VehicleVision.PleasanterTools.BinaryMigrator`）。
  以降の例の `binary-migrator` は、このコマンド名（`pleasanter-binary-migrator`）に読み替えてください
- **ZIP**: [GitHub Releases](https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.BinaryMigrator/releases) の `…-portable.zip` を展開します

## 使い方

```text
binary-migrator migrate --from <rds|local|azure> --to <local|azure> [オプション]
binary-migrator --help
```

ZIP やビルドしたものは `VehicleVision.PleasanterTools.BinaryMigrator`（`dotnet VehicleVision.PleasanterTools.BinaryMigrator.dll`）として実行します。
コンソールに出る文字列は英語です（Azure の Kudu の Debug console で日本語が化けるため）。

### 共通のオプション

| オプション | 意味 |
| --- | --- |
| `--from`、`--to` | 移行元・移行先（`rds`・`local`・`azure`）。必須 |
| `--dry-run` | 何をどこへ移すか（コピー・とばす・削除）だけを表示する。何も書かず、消さない |
| `--delete-source` | 検証が通ったものだけ、移行元を消す（上の表）。`--yes` が要る |
| `--yes`、`-y` | `--delete-source` の確認 |
| `--types` | 対象の種別（コンマ区切り。`attachments`、`images`、`siteimage`、`tenantimage`。既定はすべて） |
| `--parallel N` | 同時にコピーする件数（1〜16。既定は 1 ＝直列） |
| `--max-retries N` | 一時的な失敗（ネットワークなど）の再試行の回数（0〜10。既定は 3） |
| `--quiet` | 成功した件の行を出さない（失敗と最後の集計は出す） |
| `--help`、`-h`、`--version` | ヘルプ、版 |

### Rds（移行元）のオプション

Pleasanter の DB へ**直接つなぎます**（`Rds.json` は読みません）。読み取りは SELECT だけで、読み取り専用の接続にします。

| オプション・環境変数 | 意味 |
| --- | --- |
| `--rds-dbms`、`BINARY_MIGRATOR_RDS_DBMS` | `sqlserver`、`postgresql`、`mysql` |
| `BINARY_MIGRATOR_RDS_CONNECTION_STRING` | 接続文字列（ADO.NET 形式。DBMS ごとのドライバの書式） |
| `--rds-connection-string-file <パス>` | 接続文字列をファイルから読む（環境変数より優先） |
| `--rds-connection-string-stdin` | 接続文字列を標準入力から読む |
| `BINARY_MIGRATOR_RDS_WRITE_CONNECTION_STRING` | `--delete-source` の UPDATE に使う接続文字列（省略時は上の接続文字列）。読み取り専用の資格情報と、更新できる資格情報を分けたいときに使う |

接続文字列を引数へ直接書く指定（`--rds-connection-string 値`）は、パスワードが見えるため**受け付けません**。
SQL Server は暗号化が既定で有効です。証明書の検証でつまずく場合は、接続文字列の `TrustServerCertificate` などを利用環境に合わせて指定してください。

### Local のオプション

| オプション・環境変数 | 意味 |
| --- | --- |
| `--local-path`、`BINARY_MIGRATOR_LOCAL_PATH` | 保管先のルートのフォルダ（Pleasanter の `BinaryStorage.json` の `Path`。空なら Pleasanter の `App_Data/BinaryStorage`） |

ルートの下の `Attachments/`、`Images/`、`SiteImage/`、`TenantImage/` を対象にします。シンボリックリンクはたどりません。

### AzureBlob のオプション

| オプション・環境変数 | 意味 |
| --- | --- |
| `--azure-container`、`BINARY_MIGRATOR_AZURE_CONTAINER` | コンテナー名（Pleasanter の `AzureBlobContainerName`）。**コンテナーは作りません** |
| `--azure-account-uri`、`BINARY_MIGRATOR_AZURE_ACCOUNT_URI` | ストレージアカウントの URI（Pleasanter の `AzureBlobStorageAccountUri`）。`DefaultAzureCredential` で認証する |
| `BINARY_MIGRATOR_AZURE_CONNECTION_STRING` | 接続文字列で認証する場合 |
| `--azure-connection-string-file <パス>`、`--azure-connection-string-stdin` | 接続文字列をファイル・標準入力から読む |

`DefaultAzureCredential` は、環境変数・マネージド ID・Azure CLI（`az login`）などの順に資格情報を探します。必要な権限は、読み取りなら Storage Blob Data Reader、
書き込みなら Storage Blob Data Contributor 相当です（`--delete-source` も Contributor 相当）。

### 例

```bash
# Rds（PostgreSQL）から Local へ。まずドライラン
export BINARY_MIGRATOR_RDS_CONNECTION_STRING='Host=db;Database=pleasanter;Username=reader;Password=...'
binary-migrator migrate --from rds --to local --rds-dbms postgresql --local-path /var/pleasanter/BinaryStorage --dry-run

# 本番の移行（移行元は残る）
binary-migrator migrate --from rds --to local --rds-dbms postgresql --local-path /var/pleasanter/BinaryStorage --parallel 4

# 接続文字列をファイルから渡す（権限は 600 などにする）
binary-migrator migrate --from rds --to azure --rds-dbms sqlserver --rds-connection-string-file ./rds.conn \
  --azure-account-uri https://myaccount.blob.core.windows.net --azure-container pleasanter

# Local から AzureBlob へ。検証済みのファイルだけ、移行元から消す
binary-migrator migrate --from local --to azure --local-path D:\Pleasanter\App_Data\BinaryStorage \
  --azure-account-uri https://myaccount.blob.core.windows.net --azure-container pleasanter --delete-source --yes
```

### 終了コードと出力

| コード | 意味 |
| --- | --- |
| 0 | 全件成功 |
| 1 | 失敗した件がある、または途中で止めた（接続できない・認証できない・中断） |
| 2 | 引数・設定の誤り |

1 件ずつ `COPIED`・`SKIPPED`・`FAILED`（ドライランは `WOULD-COPY`・`WOULD-SKIP`）の行を出し、最後に、件数・コピーした量・とばした数・失敗の一覧をまとめて出します。
1 件の失敗では全体を止めません（最後に終了コード 1 で終わります）。失敗した原因を直して再実行すれば、済んだものはとばして残りだけを移します。

## 方針

- **既定では移行元を消しません。** 移行元の削除は、明示したオプションのときだけ行います
- コピーしたあとに、大きさとハッシュで整合を確かめます
- Pleasanter 本体のコードは取り込みません（保管先の置き方は、本体のソースを事実確認の参照として読んだ上で、このツールで実装しています）

## ライセンス

AGPL-3.0-or-later と商用のデュアルライセンスです。詳しくは [LICENSING.md](LICENSING.md) を、貢献は [CONTRIBUTING.md](CONTRIBUTING.md) を参照してください。
依存のライセンスは [NOTICE](NOTICE) にあります（SQL Server のドライバが引き込む一部の部品のライセンスは、配布前に確認が要ります）。
開発環境と運用は [開発ガイド](_documents/開発ガイド.md) と [開発運用](_documents/開発運用.md) にあります。
