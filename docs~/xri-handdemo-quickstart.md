---
title: XRI HandDemo に触覚を追加する
description: Unity 公式の XR Hand Demo シーンに、ボタン1クリックで Hapbeat の触覚イベントを組み込む手順。
---

Unity が公式に提供している **XR Interaction Toolkit (XRI) の HandDemo シーン** を使って、Hapbeat の触覚を最短で体験するためのガイドです。掴む・押す・スライドさせるといった基本操作に、デバイスからの触覚フィードバックがそのまま乗ります。

> **このガイドが向いている人**
> - Quest 3 / 3S を持っていて、Unity でハンドトラッキングを試したい
> - Hapbeat デバイスを買ったばかりで、まず動くサンプルを触ってみたい
> - 自分でシーンを組まずに、用意された動作確認シーンで遊びたい

---

## なぜ「Augment（追加適用）」方式なの？

XRI の HandDemo シーンは Unity 公式の配布物で、Hapbeat 側からそのままコピーして配ることはできません。
そのため Hapbeat SDK では、ユーザーが**自分の手元にインポートした HandDemo シーンに対して、メニュー1つで触覚コンポーネントを後付けする**仕組みを提供しています。

イメージ:

```
[XRI HandDemo シーン]  +  [Hapbeat SDK のメニュー1クリック]
        ↓
触覚イベントマップ・コンポーネントが自動で配線された HandDemo シーン
```

ユーザーが手作業で組み立てる必要はなく、**ダブルクリックに近い感覚で試せる** ことを目指しています。

---

## 必要なもの

- Unity Editor (XRI HandDemo の動作要件に準拠)
- **XR Interaction Toolkit (XRI)** の指定バージョン
  ※ 対応バージョンは SDK のリリースノートに記載しています。違うバージョンだと自動配線が失敗する場合があります。
- **Hapbeat Unity SDK**（このページで導入します）
- **Hapbeat デバイス** + **Hapbeat Studio** または **Helper** が起動していて、デバイスがオンラインになっていること

---

## 手順

### 1. XRI と HandDemo シーンを入れる

1. Unity の **Window → Package Manager** を開く
2. 左上のドロップダウンを **Unity Registry** に切り替え
3. `XR Interaction Toolkit` を検索してインストール
4. インストール後、画面下の **Samples** タブを開く
5. **Hands Interaction Demo** の **Import** をクリック

これで `Assets/Samples/XR Interaction Toolkit/.../Hands Interaction Demo/` が作成されます。

### 2. Hapbeat SDK を入れる

1. Package Manager の **+ → Install package from git URL...** を選択
2. Hapbeat Unity SDK の Git URL を貼り付けて **Install**

> SDK の詳しい導入手順は [Installation](/docs/unity-sdk/installation/) を参照してください。

### 3. メニュー1つで触覚を組み込む

1. **Hapbeat → Samples → Augment XRI HandDemo with Haptics** を選択
2. 確認ダイアログで **OK** をクリック

裏側では次のことが自動で実行されます:

- HandDemo シーンを開く
- 各インタラクション対象（掴むキューブ・スライダー・ボタンなど）に Hapbeat のコンポーネントを追加
- 触覚イベントの定義ファイル (EventMap) を関連付ける
- 必要なら HapbeatBridge をシーンに追加

完了するとダイアログに「適用件数」「スキップ件数」が表示されます。

### 4. 試す

1. Hapbeat デバイスがオンラインであることを **Studio または Helper** で確認
2. **Hands Interaction Demo シーン** を開いたまま **Play** を押す
3. Quest をかぶり、キューブを掴む / スライダーを動かす / ボタンを押す
   → デバイスから触覚フィードバックが返ってくれば成功です

---

## うまくいかないとき

| 症状 | 原因と対処 |
|---|---|
| メニューを押しても何も起きない | HandDemo シーンが Project にない。手順 1 をやり直す |
| 「対応バージョン外」と警告が出る | XRI のバージョンが SDK の対応範囲外。リリースノートのバージョンに合わせる |
| 触覚が鳴らない | デバイスがオフライン、または Studio / Helper が未起動。デバイス LED と Studio の接続表示を確認 |
| 一部のオブジェクトだけ触覚が出ない | XRI のシーンが更新されていてオブジェクト名が変わった可能性。スキップ件数があれば SDK 更新を待つ |

---

## 元に戻したいとき

メニュー実行直後なら **Edit → Undo** で 1 回戻せば、すべての配線が取り消されます。
保存後にやり直したい場合は、HandDemo シーンを Package Manager から再 Import すれば初期状態に戻ります。

---

## 自分のシーンで触覚を使いたくなったら

このサンプルで「どのコンポーネントが何をしているか」を確認したら、次は自分のシーンで同じ仕組みを組み立ててみましょう。
[Getting Started](/docs/unity-sdk/getting-started/) と [Triggers](/docs/unity-sdk/triggers/) で、HapbeatBridge / EventMap / 各種 Trigger の役割を解説しています。
