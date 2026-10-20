# VehicleVision.PleasanterTools.BinaryMigrator

Pleasanter に添付されたファイル（`Binaries`）の保管先を、**Rds・Local・AzureBlob の間で移す**ためのコマンドラインツールです。
Pleasanter 本体の設定（`BinaryStorage.json` の `Provider`）を切り替えたあと、既存のファイルを新しい保管先へ移すときに使います。

> **開発中です。** まだ動作するものはありません。仕様は [アーキテクチャ方針](_documents/アーキテクチャ方針.md) にあります。

## 移行できる組み合わせ

| 移行元 | 移行先 |
| --- | --- |
| Rds（`Binaries.Bin`） | Local、AzureBlob |
| Local | AzureBlob |
| AzureBlob | Local |

**Rds への書き戻しは対象外です。**

## 方針

- **既定では移行元を消しません。** 移行元の削除は、明示したオプションのときだけ行います
- コピーしたあとに、大きさとハッシュで整合を確かめます
- Pleasanter 本体のコードは取り込みません（保管先の置き方は、本体のソースを事実確認の参照として読んだ上で、このツールで実装しています）

## ライセンス

AGPL-3.0-or-later と商用のデュアルライセンスです。詳しくは [LICENSING.md](LICENSING.md) を、貢献は [CONTRIBUTING.md](CONTRIBUTING.md) を参照してください。
依存のライセンスは [NOTICE](NOTICE) にあります。開発環境と運用は [開発ガイド](_documents/開発ガイド.md) と [開発運用](_documents/開発運用.md) にあります。
