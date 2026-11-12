# Building LumINE 1.0

## 必要な環境

- Windows 上の Visual Studio と UWP / C# 開発ツール
- Windows SDK と Windows Mobile Extensions SDK（プロジェクト既定値は 10.0.19041.0）
- NuGet: Microsoft.NETCore.UniversalWindowsPlatform **5.2.4**
- 実機対象: Windows 10 Mobile **1703 / 10.0.15063** 以降、ARM

`LumINE.sln` を開き、NuGet を復元して ARM を選択します。実機向けには Debug / Release とも .NET Native を使用します。古い Mobile OS との互換性のため、UWP パッケージを安易に新しい世代へ更新しないでください。

## 署名

個人の署名用秘密鍵 `.pfx` は公開していません。鍵がない環境ではプロジェクトは未署名パッケージを生成します。実機への配置・配布には Visual Studio のパッケージ作成機能で自分の証明書を設定し、マニフェストの Publisher を証明書に合わせてください。アプリのデータやトークン、鍵ファイルをコミットしないでください。

## 確認

`tools/Check-*.ps1` を PowerShell 7 で実行します。これらは実際のソースと OS 境界のテストアダプターを組み合わせた回帰テストで、LINE アカウントへの接続やメッセージの送信は行いません。

ライセンス原文は `licenses/` と `LumINE/ThirdParty/` にあります。アプリ内表示には `LumINE/Assets/ThirdPartyNotices.txt` を同梱しています。依存関係を変えた場合は一覧と原文、アプリ内表示を更新してください。
