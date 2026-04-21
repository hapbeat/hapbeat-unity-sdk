#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// One-shot Editor command that creates <c>Assets/HapbeatKits/</c> with a README
    /// pointing users to Hapbeat Studio — the tool that produces Kit contents
    /// (manifest.json + clips). Provides the SDK-&gt;Studio onboarding link defined
    /// in decision-log DEC-024.
    ///
    /// The README is the SOURCE OF THE FIRST-TIME USER FLOW:
    ///   1. SDK is imported via UPM.
    ///   2. User runs this menu.
    ///   3. User opens the README, clicks through to Studio.
    ///   4. User points Studio at <c>Assets/HapbeatKits/</c> as the working directory.
    ///   5. Studio auto-scaffolds <c>&lt;kit-id&gt;/manifest.json</c> + <c>clips/</c>.
    ///   6. Unity AssetDatabase picks up the new files; EventMap integration follows.
    /// </summary>
    internal static class HapbeatKitsFolderCreator
    {
        private const string kMenu = "Hapbeat/Setup/Create HapbeatKits Folder";
        private const string kFolderAssetPath = "Assets/HapbeatKits";
        private const string kReadmeName = "README.md";

        // Bump this whenever the README template changes so existing users can
        // opt-in to the newer content via "Reset README".
        private const string kTemplateVersion = "1";

        [MenuItem(kMenu, false, 100)]
        private static void CreateFolderMenu()
        {
            EnsureFolderAndReadme(openReadme: true);
        }

        /// <summary>
        /// Create <c>Assets/HapbeatKits/</c> + README if missing. Idempotent — safe
        /// to call multiple times; existing files are preserved.
        /// </summary>
        /// <param name="openReadme">If true, ping/select/open the README in the
        /// editor after creation. Callers that show their own follow-up UI (e.g.
        /// "Reveal stream-clips/") should pass false.</param>
        /// <returns>True if the folder now exists.</returns>
        internal static bool EnsureFolderAndReadme(bool openReadme)
        {
            string folderAbsPath = Path.GetFullPath(kFolderAssetPath);
            bool folderExisted = Directory.Exists(folderAbsPath);
            if (!folderExisted)
            {
                Directory.CreateDirectory(folderAbsPath);
            }

            string readmePath = Path.Combine(folderAbsPath, kReadmeName);
            bool readmeExisted = File.Exists(readmePath);
            if (!readmeExisted)
            {
                File.WriteAllText(readmePath, BuildReadme(), System.Text.Encoding.UTF8);
            }

            AssetDatabase.Refresh();

            if (openReadme)
            {
                var readmeObj = AssetDatabase.LoadAssetAtPath<Object>($"{kFolderAssetPath}/{kReadmeName}");
                if (readmeObj != null)
                {
                    Selection.activeObject = readmeObj;
                    EditorGUIUtility.PingObject(readmeObj);
                    AssetDatabase.OpenAsset(readmeObj);
                }
            }

            string msg = folderExisted && readmeExisted
                ? $"Assets/HapbeatKits/ already exists."
                : $"Created Assets/HapbeatKits/{(readmeExisted ? "" : " + README.md")}.";
            Debug.Log($"[Hapbeat] {msg}");

            return Directory.Exists(folderAbsPath);
        }

        [MenuItem("Hapbeat/Setup/Reset HapbeatKits README (overwrite)", false, 101)]
        private static void ResetReadme()
        {
            string folderAbsPath = Path.GetFullPath(kFolderAssetPath);
            Directory.CreateDirectory(folderAbsPath);
            string readmePath = Path.Combine(folderAbsPath, kReadmeName);
            if (File.Exists(readmePath))
            {
                if (!EditorUtility.DisplayDialog(
                    "Reset HapbeatKits README",
                    $"Overwrite '{kFolderAssetPath}/{kReadmeName}' with the current SDK template?",
                    "Overwrite", "Cancel"))
                {
                    return;
                }
            }
            File.WriteAllText(readmePath, BuildReadme(), System.Text.Encoding.UTF8);
            AssetDatabase.Refresh();
            Debug.Log($"[Hapbeat] Wrote {kFolderAssetPath}/{kReadmeName} (template v{kTemplateVersion}).");
        }

        private static string BuildReadme()
        {
            // Plain markdown — Unity's markdown viewers / Visual Studio Code / GitHub
            // all render this cleanly. Keep under ~150 lines for first-time scannability.
            return
@"# Hapbeat Kits

This folder holds the **Kit** (触覚コンテンツ) files that the Hapbeat SDK consumes at
runtime. You author Kits with **Hapbeat Studio**; Unity just reads what Studio wrote.

## 1. Open Hapbeat Studio

https://devtools.hapbeat.com/studio/

Studio is a browser-based editor. No install.

## 2. Set this folder as the Studio working directory

In Studio:

1. Click **Working Directory** (or equivalent).
2. Point it at `Assets/HapbeatKits/` (this folder).
3. Studio auto-scaffolds `<kit-id>/manifest.json` + `clips/` inside this folder.

## 3. Author events in Studio

Each event has a **mode**:

| Mode | Audio source | Placed on device? |
|------|--------------|-------------------|
| `command` | `clip` wav in Pack | Yes (device plays locally on eventId) |
| `stream_clip` | `clip` wav, Unity streams over UDP | No (Unity sends PCM at runtime) |
| `stream_source` | Live AudioSource capture | No (Unity streams captured audio) |

Set **intensity** per event (0.0 – 1.0). This is the ""baseline strength"" the
creator intends at ``SDK gain = 1.0``.

> 最終出力 = WAV振幅 × intensity × SDK_gain × デバイス音量
> (SDK_gain range: 0.0 – 2.0; 1.0 で intensity そのまま, 2.0 で intensity の倍)

## 4. Write the Pack to a device

Use **Hapbeat Manager** (desktop app) to flash the device-side Pack (the
``command``-mode subset of your Kit) onto a Hapbeat. Stream-mode events don't
ship with the Pack — they live in this folder for Unity to read.

## 5. Use the Kit from Unity

Create a **HapbeatEventMap** asset (``Create > Hapbeat > Event Map``) and link
it to the Kit. EventMap entries reference the Kit by event id and pull
intensity from the Kit at runtime.

- ``entry.gain`` — your per-project multiplier (range 0 – 2, default 1)
- ``intensity`` — baseline from Studio, read-only from Unity
- ``binding`` — dynamic runtime value (e.g. PokeButton push depth)

Final ``gain_field`` sent over UDP = ``intensity × entry.gain × binding``.

## Troubleshooting

- **Studio won't see my audio**: files must be PCM WAV (no MP3/OGG). Put them
  under ``<kit-id>/clips/`` for command mode or ``<kit-id>/stream-clips/`` for
  stream_clip mode.
- **Device plays nothing**: confirm the Pack was flashed (Manager), confirm
  the event ``mode`` is ``command``, and confirm the device has firmware
  version ≥ ``target_device.firmware_version_min`` in the manifest.
- **Unity sees stale values**: Studio edits directly in Assets/, so Unity
  auto-refreshes. If not, right-click this folder → **Reimport**.

## Don't commit these to SDK releases

The SDK package ships empty. Each project adds its own kits here. Add
``Assets/HapbeatKits/**/clips/*.wav`` to ``.gitignore`` if your kits are
large and you manage them outside git.

---

**Template version**: " + kTemplateVersion + @"
**Docs**: ``Packages/Hapbeat SDK/docs/kit-integration.md``
**Decision log**: workspace ``docs/decision-log.md`` DEC-023 / DEC-024
";
        }
    }
}
#endif
