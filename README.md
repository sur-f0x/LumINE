# LumINE 
Windows 10 Mobile に 2026 年に動作する LINE アプリを実装する非公式プロジェクト。

Vyline と LINE-Chrome-Android-App を参考に、 Codex と一緒に作成しました！


![icon](LumINE/Assets/StoreLogo.scale-200.png)



このプロジェクトは LINE ヤフー株式会社とは一切関係ありません。

**非公式クライアントのため、不具合・アカウント BAN 等このアプリを使用した結果生じたいかなる損害について一切責任を負いかねます。**

## テスト済みの端末
- Microsoft Lumia 950 (Snapdragon 808/3GB RAM/Windows 10 Mobile 1709)
- Lenovo 503LV (Snapdragon 617/3GB RAM/Windows 10 Mobile 1709)
## インストール
※ Windows 10 Mobile 1703/1709 へのアップグレードが必要です。
1. Releases から APPX パッケージをダウンロード
2. 開発者モードを有効にする
3. パッケージインストーラーまたは Device Portal 等からインストール

## 利用方法
1. 上記の方法でアプリをインストール
2. LINE のメールアドレスとパスワードを入力
3. 表示された6桁のコードをメイン端末に入力しログイン
4. 成功すると、PC 版 LINE としてログインされる

## ビルドについて
### ビルド環境
- Visual Studio 2017/2019/2022（UWP開発ワークロード）
- Windows 10 SDK
- 対象: Windows 10 Mobile 1703 (MinVersion 10.0.15063.0)

### ビルド手順
詳細は [docs/BUILDING.md](https://github.com/sur-f0x/LumINE/blob/main/docs/BUILDING.md) を参照

## ライセンス
本アプリには複数の参照元に由来するコードが含まれるため、配布時は各ライセンスと著作権表示を保持してください。

- LINE-Chrome-Android-App: BSD-3-Clause ((元プロジェクト)[https://github.com/taka-4602/LINE-Chrome-Android-App])
- vyline 由来の移植コード: MIT、Copyright (c) 2026 Vyline Team & nezumi0627（[ライセンス原文](https://github.com/nezumi0627/vyline/blob/main/LICENSE)）
