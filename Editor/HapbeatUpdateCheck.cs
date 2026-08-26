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
    ///   <item><b>Editor セッションごとに 1 回</b> (<see cref="SessionState"/> で管理)。
    ///   domain reload では重複させず、Editor を開き直せばまた 1 回出る。Console の
    ///   1 行は「閉じる」操作を要求しないので、版ごとに永続抑制して見逃させるより、
    ///   起動のたびに 1 度流れる方が親切という判断 (§5.1 B)。目立つ UI を出す場合は
    ///   版ごと 1 回に絞ること (§5.1 A)。</item>
    ///   <item>取得失敗は<b>完全にサイレント</b>。オフライン環境で「確認できません
    ///   でした」を出さない。</item>
    ///   <item>ダイアログを出さない・Editor の起動を止めない。Warning ではなく
    ///   <see cref="Debug.Log"/> にしているのも同じ理由 (Warning は「壊れている」の
    ///   合図として温存する)。</item>
    ///   <item>Console 出力は英語。この SDK の Editor ログは全て英語で統一されている
    ///   (<c>HapbeatLocalization</c> は EditorWindow UI 用の機構で、ログには使わない)。</item>
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
        private const string PrefCached   = "Hapbeat.UpdateCheck.CachedLatest";
        // SessionState は domain reload をまたいで残り、Editor を閉じると消える。
        // 「起動ごとに 1 回」がこれ 1 つで表現できる。
        private const string SessionNotified = "Hapbeat.UpdateCheck.NotifiedThisSession";

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
                Debug.Log("[Hapbeat] Could not determine the SDK version " +
                          "(not recognised as a UPM package).");
                return;
            }
            // 手動確認はキャッシュを使わず必ず取りに行く ("いま" を聞かれているため)。
            Fetch(latest =>
            {
                if (string.IsNullOrEmpty(latest))
                {
                    Debug.Log("[Hapbeat] Could not reach the release feed — you may be offline.");
                    return;
                }
                CacheLatest(latest);
                if (IsNewer(latest, current))
                {
                    Debug.Log(NoticeMessage(current, latest));
                    // 手動で見た以上、このセッションで自動通知を重ねる必要はない。
                    SessionState.SetBool(SessionNotified, true);
                }
                else
                {
                    Debug.Log($"[Hapbeat] Unity SDK is up to date (v{current}).");
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

            // このセッションで既に出した (domain reload で重複させない)。
            if (SessionState.GetBool(SessionNotified, false)) return;

            // ローカル / embedded 配置 = この SDK 自体を開発しているプロジェクト。
            // 自分の作業ツリーに「更新してください」と言っても意味がないので黙る。
            if (HapbeatDevModeMenuGate.IsLocalDevInstall()) return;

            string current = CurrentVersion();
            if (string.IsNullOrEmpty(current)) return;

            // 24h 以内に取得済みならその値で判定する。ネットワークアクセスは 1 日 1 回に
            // 抑えつつ、通知自体は Editor を開くたびに 1 回出せるようにするため
            // (fetch 間隔と通知頻度を分けている)。
            string cached = CachedLatest();
            if (!string.IsNullOrEmpty(cached))
            {
                Announce(current, cached);
                return;
            }

            // 問い合わせる時点で記録する (失敗しても間隔を空ける — オフライン環境で
            // Editor を開くたびにタイムアウト待ちを繰り返さないため)。
            EditorPrefs.SetString(PrefLastTick, DateTime.UtcNow.Ticks.ToString());

            Fetch(latest =>
            {
                if (string.IsNullOrEmpty(latest)) return;
                CacheLatest(latest);
                Announce(current, latest);
            });
        }

        private static void Announce(string current, string latest)
        {
            if (!IsNewer(latest, current)) return;
            if (SessionState.GetBool(SessionNotified, false)) return;
            SessionState.SetBool(SessionNotified, true);
            Debug.Log(NoticeMessage(current, latest));
        }

        /// <summary>24h 以内に取得した latest。無ければ空文字。</summary>
        private static string CachedLatest()
        {
            string raw = EditorPrefs.GetString(PrefLastTick, "");
            if (string.IsNullOrEmpty(raw) || !long.TryParse(raw, out long ticks)) return "";
            if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) >= CheckInterval) return "";
            return EditorPrefs.GetString(PrefCached, "");
        }

        private static void CacheLatest(string latest)
        {
            EditorPrefs.SetString(PrefCached, latest);
            EditorPrefs.SetString(PrefLastTick, DateTime.UtcNow.Ticks.ToString());
        }

        private static string NoticeMessage(string current, string latest)
        {
            return $"[Hapbeat] Unity SDK v{latest} is available (using v{current}).\n" +
                   "→ Update Hapbeat SDK in the Package Manager, or edit the URL suffix in " +
                   $"`Packages/manifest.json` to `#v{latest}`.\n" +
                   "Changelog: https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/changelog/\n" +
                   "(Shown once per Editor session. Toggle it off via " +
                   "Hapbeat > Diagnostics > Check for SDK Updates on Startup.)";
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
            var pkg = UpmPackageInfo.FindForAssembly(typeof(HapbeatManager).Assembly);
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
