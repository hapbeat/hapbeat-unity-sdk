---
title: インストール
description: Hapbeat Unity SDK を Unity プロジェクトに導入する手順 (UPM 経由)。
---

Hapbeat Unity SDK は Unity Package Manager (UPM) 経由で **Git URL から直接インストール** できます。`.unitypackage` のダウンロードや手動コピーは不要です。

## 動作環境

- **Unity 2022.3 LTS 以上**（Unity 6 で動作確認）
- **Git** が PC にインストール済み・PATH 通り済み (Unity が裏で `git clone` するため必須)
- Wi-Fi UDP broadcast でデバイスと同一ネットワークに接続できる環境
- Active Input Handling は **"Both"**（Unity 2022+ デフォルト）または "Old" を推奨 (`HapbeatKeyDispatcher` が legacy Input を使用)

## インストール

### 1. Package Manager から Git URL で追加

1. Unity Editor で `Window` → `Package Manager`
2. 左上の **`+`** → **`Add package from git URL...`**
3. 次の URL を貼り付けて **Add**:

```
https://github.com/Hapbeat/hapbeat-unity-sdk.git
```

特定バージョンを固定する場合は末尾にタグ:

```
https://github.com/Hapbeat/hapbeat-unity-sdk.git#v0.1.0
```

### 2. 更新

- Package Manager → Hapbeat SDK を選択 → 右ペインに **Update** が出ていればクリック
- Tag 固定 URL の場合は `Packages/manifest.json` の `#vX.Y.Z` を書き換えて保存 → Unity が自動 reimport

### 3. SDK フォルダを作成 (任意・初回のみ便利)

`Hapbeat → Setup → Create HapbeatSDK Folder` を実行すると以下が生成されます:

```
Assets/HapbeatSDK/
  Kits/        ← 触覚波形と manifest.json (Studio 連携先)
  Scenes/      ← 生成シーン
  EventMaps/   ← EventMap.asset
```

サンプルの Build メニューを使う場合は自動で生成されるので、明示的に呼ぶ必要はありません。

## サンプル

Package Manager で Hapbeat SDK を選択 → 右パネル **Samples** タブから **Import**:

| サンプル | 内容 | 動作要件 |
|---|---|---|
| **Basic Example** | Trigger × 3 + Helper + Dispatcher + StatusOverlay の最小組合せ。Space/L/E/S/P キーで動作確認 | デバイス + Studio または Helper 起動 |
| **Tutorial** | 5 ゾーン × Without/With の SDK 全機能ツアー (Bowling / Door / Pickup / Stream Console / Target Range)。キーマウスで完結、XR 不要 | 同上 |
| **XR Helpers** | XR Interaction Toolkit 連携フィルター (XRGrabFilter / XRSocketFilter) | XRI パッケージが入っているプロジェクトのみ |

Sample は `Assets/Samples/Hapbeat SDK/<version>/<sample>/` に展開されます。

### Build メニューで Scene / EventMap / Kit を生成

Basic Example / Tutorial の **シーンは Sample import 直後にはまだ存在しません**。Editor メニューから自動生成します:

- **Hapbeat → Build Samples → 1. Basic Example** — `Assets/HapbeatSDK/{Kits, Scenes, EventMaps}/` 一式を生成
- **Hapbeat → Build Samples → 2. Tutorial (full scene)** — Tutorial シーンと EventMap を生成

実行後、`Assets/HapbeatSDK/Scenes/` 配下に `.unity` が現れるので、それを開いて Play。

## 動作確認

1. **Hapbeat Studio** または **Hapbeat Helper** を起動し、デバイスがオンライン表示になることを確認
2. Unity で `Assets/HapbeatSDK/Scenes/BasicExample.unity` を開く
3. Play モード突入
4. **Space** キーで Stream 1-shot, **E** キーで Command が再生され、デバイスから振動が出れば成功

UI に `Pong: RTT=...ms` が表示されれば SDK ↔ デバイスの通信は確立しています。

## ビルド時の注意

- **iOS / Android**: 標準で動作 (UDP socket 利用可)
- **Quest (Android)**: マニフェストに `INTERNET` 権限が自動付与される
- **WebGL**: UDP socket 不可。WebGL ビルドでは Hapbeat 通信は動作しません

## トラブルシューティング

| 症状 | 対処 |
|---|---|
| `Package Manager` で URL を貼っても進まない | Git が PATH に通っているか確認 (`git --version` がコマンドラインで通る必要あり) |
| `Hapbeat → Build Samples → ...` が出ない | 該当 Sample が Import 済みか確認。古い Sample を再 Import すると最新の Editor スクリプトが反映される |
| Play しても触覚が来ない | Studio/Helper が起動・デバイスがオンラインか / `HapbeatConfig` の Group 設定がデバイスと一致するか / Active Input Handling が "Both" か |
| `'InputSystem' does not exist` 等のコンパイルエラー | 古い import が残っている可能性。`Assets/Samples/Hapbeat SDK/` 配下の該当 Sample を削除して再 Import |

## 次のステップ

- [Getting Started](/docs/unity-sdk/getting-started/) — 最初のシーンを 5 分で作る
- [Tutorial サンプル](/docs/unity-sdk/tutorial/) — SDK 全機能を 30 分で体験
- [Trigger コンポーネント](/docs/unity-sdk/triggers/) — Animator / Collision / Sequence 等
- [EventMap ウィンドウ](/docs/unity-sdk/event-map/) — Event ID と波形の対応を GUI 管理
- [Streaming buffer](/docs/unity-sdk/streaming/) — StreamClip 用バッファの調整
