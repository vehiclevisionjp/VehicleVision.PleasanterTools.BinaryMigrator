# Pleasanter 参照ソース

`Implem.Pleasanter` は <https://github.com/Implem/Implem.Pleasanter> の Git サブモジュールです。固定位置はリリースタグ `Pleasanter_1.5.8.1`（`626a173`）です。現在の固定位置は `git submodule status` を正としてください。

本体とツールはともに AGPL v3 系です。このサブモジュールは API の事実確認に使う参照専用です。薄い独立したツールを保ち、デュアルライセンスの選択肢を残すため、本体コードのコピー・流用・リンク・プロジェクト参照は行いません。CI・CodeQL・配布物はこのフォルダを取得しません。

日次 Actions（`.github/workflows/reference-submodule-update.yml`）が最新リリースタグとの差を検出して Issue と Draft PR を作ります。更新時は、ツールが根拠にした API の仕様を新しいソースで確認し直してください。

テンプレートから新しいツールを作った直後は、サブモジュールの固定位置が古い可能性があります。最初の日次実行で更新 PR が出ます。
