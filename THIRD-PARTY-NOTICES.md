# Third-party notices — LumINE 1.0

| プロジェクト / ライブラリ | 用途 | ライセンスと原文 |
| --- | --- | --- |
| [Vyline](https://github.com/nezumi0627/vyline) | 移植元。認証・通信・同期・キャッシュの設計と実装を参照 | [MIT](licenses/Vyline-MIT.txt) |
| [linejs](https://github.com/evex-dev/linejs) | E2EE、グループ鍵再作成、画像転送の実装を参照 | [MIT](licenses/linejs-MIT.txt) |
| [LINE-Chrome-Android-App](https://github.com/taka-4602/LINE-Chrome-Android-App) | 初期の Python 認証・通信実装を参照 | [BSD-3-Clause](licenses/LINE-Chrome-Android-App-BSD-3-Clause.txt) |
| [XamlAnimatedPng](https://github.com/xupefei/XamlAnimatedPng) / [XamlAnimatedGif](https://github.com/XamlAnimatedGif/XamlAnimatedGif) | スタンプ再生のタイミング処理と UWP アダプター | [Apache-2.0](LumINE/ThirdParty/XamlAnimatedPng/LICENSE.txt)、[変更内容・出典](LumINE/ThirdParty/XamlAnimatedPng/NOTICE.txt) |
| Microsoft.NETCore.UniversalWindowsPlatform 5.2.4 | UWP / .NET ランタイムと標準ライブラリ | 同梱パッケージの Microsoft .NET Library ライセンス条項および各依存パッケージの通知。下記の依存一覧と licenses/dotnet を参照 |

XamlAnimatedPng のソース参照点: `227e62d72bad3b689f9c7250f2f1c47250c36461`。上流の WIP をそのまま APNG デコーダーとして使うものではなく、LumINE のデコーダー・フレームキャッシュと組み合わせています。

直接参照する NuGet パッケージは UWP 5.2.4 です。BouncyCastle は LumINE 本体・バックグラウンドコンポーネントの依存に含まれていません。ローカルの研究用プロジェクトは公開対象から除いています。

推移的依存関係は [依存一覧](licenses/DEPENDENCIES.md) に記載し、復元済み NuGet パッケージから取得したライセンス原文・第三者通知を `licenses/dotnet/` に保持しています。ライセンスの権利表示・免責事項は各原文に従います。本ファイルは LumINE 独自コードのライセンスを新たに指定するものではありません。
