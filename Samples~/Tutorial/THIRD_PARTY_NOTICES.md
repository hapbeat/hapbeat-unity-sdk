# Tutorial Sample — Third Party Notices

## Audio assets (`Audio/`)

`Samples~/Tutorial/Audio/` 以下の WAV / MP3 ファイルは、配布 OK のフリー効果音サイトから取得した素材を、Hapbeat の触覚信号用にリサンプル・トリミング・ゲイン調整して再配布しています。

由来として可能性のあるサイト (SDK ルート `README.md` のクレジットと共通):

- 効果音ラボ — https://soundeffect-lab.info/
- 魔王魂 — https://maou.audio/
- 効果音辞典（小森平） — https://taira-komori.net/
- OtoLogic — https://otologic.jp/
- 音人 — https://on-jin.com/

配布前に各サイトの利用規約に従って加工しており、著作権・作者メタデータは除去済みです。
出典が明確でないファイルについて、権利者・配布元からご連絡をいただければ確認の上、整合を取ります (削除 / 差し替え / クレジット追記など)。Issue または GitHub の連絡先までお知らせください。

## 3D Models (`Models/`)

将来的に [Kenney CC0 アセット](https://kenney.nl/assets) のモデルを `Models/` フォルダに配置する予定です。
現状の Build メニュー (`Hapbeat → Build Samples → 2. Tutorial`) は Unity プリミティブ (Cube / Sphere / Cylinder) のみで構成されているため、Models フォルダは空です。

Kenney アセットを利用する場合は、以下を本ファイルに追記してください:

```
- Source: Kenney.nl
- License: CC0 1.0 Universal (Public Domain Dedication)
- URL: https://creativecommons.org/publicdomain/zero/1.0/
- Used assets: <pack name> / <file name>
```

Kenney のライセンスでは attribution は任意ですが、出所の記録のために本ファイルに記載することを推奨します。
