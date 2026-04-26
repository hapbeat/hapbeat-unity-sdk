---
title: インストール
description: Hapbeat Unity SDK を Unity プロジェクトに導入する手順 (UPM 経由)。
---

Hapbeat Unity SDK は Unity Package Manager (UPM) 経由でインストールします。

## 動作環境

- Unity 2022.3 LTS 以上推奨（Unity 6 で動作確認）
- 通信: Wi-Fi UDP broadcast（Bridge 不要、デバイスと同じネットワークに接続できる環境）
- VR: Quest / Pico / Vision Pro 等の HMD でも、HMD が同じ Wi-Fi にいれば動作

## インストール（UPM）

### Git URL から追加

1. Unity Editor で `Window` → `Package Manager` を開く
2. 左上の `+` ボタン → `Install package from git URL...`
3. 次の URL を入力:

```
https://github.com/Hapbeat/hapbeat-unity-sdk.git
```

特定バージョンを固定する場合は `#v0.5.0` 等のタグを末尾に付ける:

```
https://github.com/Hapbeat/hapbeat-unity-sdk.git#v0.5.0
```

### サンプルの import

Package Manager で Hapbeat SDK を選択 → 右パネルの `Samples` セクションから必要なサンプルを Import:

| サンプル | 内容 |
|---------|-----|
| **BasicExample** | 最小構成（Bridge + Trigger 1 個） |
| **PlayerDemo** | プレイヤー視点のデモ（Animator / Collision Trigger 統合） |
| **CreatorTutorial** | 既存アセットへの触覚追加を Before/After で実演 |
| **XriHelpers** | XR Interaction Toolkit 用ヘルパー（opt-in） |

サンプルは `Assets/Samples/Hapbeat SDK/x.y.z/` に展開されます。自由に編集してプロジェクトに取り込んでください。

## 動作確認

1. Hapbeat Manager を起動し、デバイスが Devices タブに表示されていることを確認
2. Unity プロジェクトで `BasicExample` シーンを開く
3. Play モード突入
4. シーン内の Trigger コンポーネントが発火する操作（クリック等）を行い、デバイスから振動が出れば成功

## ビルド時の注意

- iOS / Android: 標準で動作（UDP socket 利用可）
- Quest（Android）: マニフェストに `INTERNET` 権限が自動付与される
- WebGL: UDP socket 不可。WebGL ビルドでは Hapbeat 通信は動作しません

## 次のステップ

- [Getting Started](/docs/unity-sdk/getting-started/) — 最初のシーンを 5 分で作る
- [Trigger コンポーネント](/docs/unity-sdk/triggers/) — Animator / Collision / Sequence 等
- [EventMap ウィンドウ](/docs/unity-sdk/event-map/) — Event ID と波形の対応を GUI 管理
