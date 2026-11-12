# 謝辞

LumINE 1.0 は、Windows 10 Mobile 1703 向けの非公式 LINE クライアントです。

本プロジェクトは [Vyline](https://github.com/nezumi0627/vyline) の設計と実装を出発点としています。LINE の通信、認証、E2EE、トーク同期、キャッシュ管理を公開し、移植の道筋を示してくださった **Vyline Team、nezumi0627 氏、各コントリビューター**に感謝します。

また、E2EE の鍵管理・グループ鍵の再作成・画像転送の相互運用性を調べる際には [linejs](https://github.com/evex-dev/linejs) の実装を参照しました。[LINE-Chrome-Android-App](https://github.com/taka-4602/LINE-Chrome-Android-App) の Python 実装も、初期の認証・通信処理を理解するために参照しています。Chrome 拡張向けの未使用通信経路は LumINE の公開版から削除しています。

動くスタンプには [XamlAnimatedPng](https://github.com/xupefei/XamlAnimatedPng) / [XamlAnimatedGif](https://github.com/XamlAnimatedGif/XamlAnimatedGif) のタイミング制御と、LumINE 向けの UWP/APNG アダプターを使用しています。

参照元の名称は出典と感謝を示すものです。各開発者による LumINE の推奨・動作保証を意味するものではありません。LumINE は LINE ヤフー株式会社の公式製品ではありません。

各ライセンスの原文と依存関係は [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) および [licenses](licenses/) を参照してください。アプリ内の「設定 → 謝辞・ライセンス」からも確認できます。
