#if UNITY_EDITOR
using System;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
// Alias to disambiguate from UnityEditor.PackageInfo (legacy AssetStore type).
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Hapbeat.Editor
{
    /// <summary>
    /// 「新しい Hapbeat Unity SDK が出ています」を Console に 1 行だけ知らせる。
    ///
    /// <para>なぜ自前で持つのか: この SDK は UPM の <b>Git URL</b> で配布している。
    /// <c>#v0.3.0</c> のようにタグを固定した URL は Package Manager が更新を
    /// 検出してくれないため、放っておくと何世代も前を使い続けることになる。</para>
    ///
    /// <para>作法 (hapbeat-contracts <c>specs/release-feed.md</c> §5, DEC-053):</para>
    /// <list type="bullet">
    ///   <item><b>1 版につき 1 回だけ</b>。出した版は記録し、より新しい版が出るまで
    ///   黙る。Editor を開くたびに同じ行が出るのは、版を意図的に固定している人に
    ///   とってノイズでしかない。</item>
    ///   <item>取得失敗は<b>完全にサイレント</b>。オフライン環境で「確認できません
    ///   でした」を出さない。</item>
    ///   <item>ダイアログを出さない・Editor の起動を止めない。Warning ではなく
    ///   <see cref="Debug.Log"/> にしているのも同じ理由 (Warning は「壊れている」の
    ///   合図として温存する)。</item>
    /// </list>
    ///
    /// <para>手動確認は <c>Hapbeat/Diagnostics/Check for SDK Updates</c>。
    /// こちらは「ユーザーが自分で聞きに来た」場面なので、抑制を無視して毎回答える。
    /// 自動チェックは同メニューのトグルで止められる。</para>
    /// </summary>
    [InitializeOnLoad]
    internal static class HapbeatUpdateCheck
    {
        private const string FeedUrl = "https://devtools.hapbeat.com/releases.json";
        private const string ProductId = "unity-sdk";

        private const string PrefEnabled  = "Hapbeat.UpdateCheck.Enabled";
        private const string PrefLastTick = "Hapbeat.UpdateCheck.LastCheckTicks";
        private const string PrefNotified = "Hapbeat.UpdateCheck.NotifiedVersion";

        private const string MenuCheckNow = "Hapbeat/Diagnostics/Check for SDK Updates";
        private const string MenuAuto     = "Hapbeat/Diagnostics/Check for SDK Updates on Startup";

        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
        private const int TimeoutSeconds = 3;

        static HapbeatUpdateCheck()
        {
            // delayCall: AssetDatabase / PackageManager が揃ってから。
            EditorApplication.delayCall += AutoCheck;
        }

        // ------------------------------------------------------------------
        // menu

        [MenuItem(MenuCheckNow, false, 1900)]
        private static void CheckNow()
        {
            string current = CurrentVersion();
            if (string.IsNullOrEmpty(current))
            {
                Debug.Log("[Hapbeat] SDK のバージョンを特定できませんでした (UPM パッケージとして認識されていません)。");
                return;
            }
            Fetch(latest =>
            {
                if (string.IsNullOrEmpty(latest))
                {
                    Debug.Log("[Hapbeat] 最新版を確認できませんでした (オフラインの可能性があります)。");
                    return;
                }
                if (IsNewer(latest, current))
                {
                    Debug.Log(NoticeMessage(current, latest));
                    // 手動確認でも「見た」ことに変わりはないので通知済みにする。
                    EditorPrefs.SetString(PrefNotified, latest);
                }
                else
                {
                    Debug.Log($"[Hapbeat] Unity SDK は最新です (v{current})。");
                }
            });
        }

        [MenuItem(MenuAuto, false, 1901)]
        private static void ToggleAuto()
        {
            bool next = !AutoEnabled;
            EditorPrefs.SetBool(PrefEnabled, next);
            Menu.SetChecked(MenuAuto, next);
        }

        [MenuItem(MenuAuto, true)]
        private static bool ToggleAutoValidate()
        {
            Menu.SetChecked(MenuAuto, AutoEnabled);
            return true;
        }

        private static bool AutoEnabled => EditorPrefs.GetBool(PrefEnabled, true);

        // ------------------------------------------------------------------
        // auto check

        private static void AutoCheck()
        {
            if (!AutoEnabled) return;

            // ローカル / embedded 配置 = この SDK 自体を開発しているプロジェクト。
            // 自分の作業ツリーに「更新してください」と言っても意味がないので黙る。
            if (HapbeatDevModeMenuGate.IsLocalDevInstall()) return;

            if (!IntervalElapsed()) return;

            string current = CurrentVersion();
            if (string.IsNullOrEmpty(current)) return;

            // 実際に問い合わせる時点で記録する (失敗しても間隔を空ける — オフライン
            // 環境で Editor を開くたびにタイムアウト待ちを繰り返さないため)。
            EditorPrefs.SetString(PrefLastTick, DateTime.UtcNow.Ticks.ToString());

            Fetch(latest =>
            {
                if (string.IsNullOrEmpty(latest)) return;
                if (!IsNewer(latest, current)) return;

                string notified = EditorPrefs.GetString(PrefNotified, "");
                // 未通知なら出す。通知済みなら、それより新しい版のときだけ。
                if (!string.IsNullOrEmpty(notified) && !IsNewer(latest, notified)) return;

                Debug.Log(NoticeMessage(current, latest));
                EditorPrefs.SetString(PrefNotified, latest);
            });
        }

        private static bool IntervalElapsed()
        {
            string raw = EditorPrefs.GetString(PrefLastTick, "");
            if (string.IsNullOrEmpty(raw) || !long.TryParse(raw, out long ticks)) return true;
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) >= CheckInterval;
        }

        private static string NoticeMessage(string current, string latest)
        {
            return $"[Hapbeat] Unity SDK v{latest} が公開されています (使用中: v{current})。\n" +
                   "→ Package Manager で Hapbeat SDK を更新するか、`Packages/manifest.json` の " +
                   $"URL 末尾を `#v{latest}` に書き換えてください。\n" +
                   "変更履歴: https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/changelog/\n" +
                   "（このお知らせは同じ版では再表示されません。自動確認は " +
                   "Hapbeat > Diagnostics > Check for SDK Updates on Startup で切り替えられます）";
        }

        // ------------------------------------------------------------------
        // feed

        /// <summary>
        /// release feed からこの SDK の最新版を取る。取得できなければ
        /// <c>null</c> を渡してコールバックする (呼び出し側は黙ること)。
        /// </summary>
        private static void Fetch(Action<string> onDone)
        {
            UnityWebRequest req = UnityWebRequest.Get(FeedUrl);
            req.timeout = TimeoutSeconds;
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                string latest = null;
                try
                {
                    if (req.result == UnityWebRequest.Result.Success)
                        latest = ParseLatest(req.downloadHandler.text, ProductId);
                }
                catch
                {
                    latest = null; // 壊れた JSON — 黙る
                }
                finally
                {
                    req.Dispose();
                }
                onDone(latest);
            };
        }

        /// <summary>
        /// feed から 1 product の <c>latest</c> を抜く。
        ///
        /// <para>JsonUtility は <c>products</c> のような動的キーの map を扱えない。
        /// product エントリはスカラーのみでネストしない (release-feed.schema.json)
        /// ため、当該エントリの <c>{...}</c> を切り出して <c>latest</c> を読む。</para>
        /// </summary>
        internal static string ParseLatest(string json, string productId)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var entry = Regex.Match(json, "\"" + Regex.Escape(productId) + "\"\\s*:\\s*\\{([^{}]*)\\}");
            if (!entry.Success) return null;
            var latest = Regex.Match(entry.Groups[1].Value, "\"latest\"\\s*:\\s*\"([^\"]+)\"");
            return latest.Success ? latest.Groups[1].Value : null;
        }

        // ------------------------------------------------------------------
        // version

        private static string CurrentVersion()
        {
            var pkg = UpmPackageInfo.FindForAssembly(typeof(HapbeatBridge).Assembly);
            return pkg?.version;
        }

        /// <summary>
        /// <paramref name="candidate"/> が <paramref name="baseline"/> より新しいか。
        /// 判定できない文字列は <c>false</c> (= 黙る側に倒す)。
        /// </summary>
        internal static bool IsNewer(string candidate, string baseline)
        {
            int[] a = ParseVersion(candidate);
            int[] b = ParseVersion(baseline);
            if (a == null || b == null) return false;
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        /// <summary>"v0.3.1" / "0.3.1-rc1" → {0,3,1}。解釈できなければ null。</summary>
        internal static int[] ParseVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            string s = v.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0) s = s.Substring(0, cut);
            string[] parts = s.Split('.');
            var outv = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                // "1d4" のような dev 接尾辞は先頭の数字までで切る。
                int end = 0;
                while (end < parts[i].Length && char.IsDigit(parts[i][end])) end++;
                if (end == 0) return null;
                outv[i] = int.Parse(parts[i].Substring(0, end));
            }
            return outv;
        }
    }
}
#endif
