using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// On-screen help text. ZoneSwitcher と連動して、現在ゾーンのコマンド
    /// だけを表示する。
    ///
    /// <para><b>Two-column mode (推奨):</b> <see cref="_keysText"/> と
    /// <see cref="_descText"/> の両方を割り当てると、各行を <c>KEY | DESCRIPTION</c>
    /// 形式で分割して左列 (キー) / 右列 (説明) に振り分ける。</para>
    ///
    /// <para><b>Single-column mode (fallback):</b> <see cref="_guideText"/> だけ
    /// 割り当てた場合は、改行付きの単一テキストとしてそのまま表示する。</para>
    /// </summary>
    public class HudGuide : MonoBehaviour
    {
        [Header("Two-column mode (推奨)")]
        [SerializeField] private Text _keysText;
        [SerializeField] private Text _descText;

        [Header("Single-column mode (fallback)")]
        [SerializeField] private Text _guideText;

        [Header("Connection status")]
        [SerializeField] private Text _connectionStatusText;

        [Header("Zone")]
        [SerializeField] private ZoneSwitcher _zoneSwitcher;

        [Header("Header (常に表示, 各行 \"KEY | DESC\" 形式)")]
        [TextArea(2, 8)]
        [SerializeField] private string _globalHeader =
@"WASD | move
Mouse | look
1-5 | zone switch
Q | manual fire
P | ping";

        [Header("Fallback (ZoneSwitcher 未設定時に single-column のみ使用)")]
        [TextArea(4, 12)]
        [SerializeField] private string _guideContent =
@"(ZoneSwitcher 未割当)";

        private const string SEP = "|"; // 行内の key/desc 区切り

        private void OnEnable()
        {
            if (_zoneSwitcher != null)
                _zoneSwitcher.OnZoneChanged.AddListener(OnZoneChanged);
        }

        private void OnDisable()
        {
            if (_zoneSwitcher != null)
                _zoneSwitcher.OnZoneChanged.RemoveListener(OnZoneChanged);
        }

        private void Start()
        {
            RefreshGuide();
            UpdateConnectionStatus();
        }

        private void Update()
        {
            UpdateConnectionStatus();
        }

        private void OnZoneChanged(int oneBasedIndex)
        {
            RefreshGuide();
        }

        private void RefreshGuide()
        {
            // どのソースを表示するかを決める。
            string header = _globalHeader ?? "";
            string body;
            string zoneTitle = null;
            if (_zoneSwitcher != null && _zoneSwitcher.CurrentEntry != null)
            {
                var e = _zoneSwitcher.CurrentEntry;
                zoneTitle = $"[Zone {_zoneSwitcher.CurrentZone}] {e.label}";
                body = string.IsNullOrEmpty(e.commands) ? "(コマンド未設定)" : e.commands;
            }
            else
            {
                body = _guideContent ?? "";
            }

            bool twoCol = _keysText != null && _descText != null;
            if (twoCol)
            {
                // 2 列モード: header + (zoneTitle が span 全体) + body の順で
                // それぞれの行を KEY|DESC で split し、keys/desc 列に積む。
                var (keys, descs) = SplitTwoCol(header, zoneTitle, body);
                _keysText.text = keys;
                _descText.text = descs;
                if (_guideText != null) _guideText.text = ""; // 単一列は空にしておく
            }
            else if (_guideText != null)
            {
                _guideText.text = AssembleSingleCol(header, zoneTitle, body);
            }
        }

        private static (string keys, string descs) SplitTwoCol(string header, string zoneTitle, string body)
        {
            var keys = new System.Text.StringBuilder();
            var descs = new System.Text.StringBuilder();

            AppendBlock(keys, descs, header);

            if (!string.IsNullOrEmpty(zoneTitle))
            {
                // 1 行空けてゾーンタイトル。`[Zone X] | Name` 形式で
                //   keys 列: "[Zone X]" (default yellow)
                //   desc 列: "| Name"   (default white)
                // ゾーン番号と名前を入力で受け取った想定: "[Zone 3] Fishing"
                // から "[Zone 3]" と "Fishing" を分割。形式が違えば desc に全文。
                if (keys.Length > 0)
                {
                    keys.Append("\n\n");
                    descs.Append("\n\n");
                }
                int closeBracket = zoneTitle.IndexOf(']');
                if (closeBracket > 0 && closeBracket + 1 < zoneTitle.Length)
                {
                    // keys 列の [Zone X] は黄色 (default) ではなく白で目立たせる
                    keys.Append("<color=#FFFFFF>")
                        .Append(zoneTitle.Substring(0, closeBracket + 1))
                        .Append("</color>");
                    descs.Append("| ").Append(zoneTitle.Substring(closeBracket + 1).TrimStart());
                }
                else
                {
                    keys.Append("<color=#FFFFFF>").Append(zoneTitle).Append("</color>");
                    descs.Append("");
                }
            }

            if (!string.IsNullOrEmpty(body))
            {
                if (keys.Length > 0) { keys.Append('\n'); descs.Append('\n'); }
                AppendBlock(keys, descs, body);
            }

            return (keys.ToString(), descs.ToString());
        }

        private static void AppendBlock(
            System.Text.StringBuilder keys, System.Text.StringBuilder descs, string block)
        {
            if (string.IsNullOrEmpty(block)) return;
            var lines = block.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) { keys.Append('\n'); descs.Append('\n'); }
                var line = lines[i];
                int sep = line.IndexOf(SEP);
                if (sep >= 0)
                {
                    keys.Append(line.Substring(0, sep).TrimEnd());
                    // desc 側 prefix "| " で区切りを視認可能に。
                    descs.Append("| ").Append(line.Substring(sep + 1).TrimStart());
                }
                else
                {
                    // 区切り無し: desc 列にそのまま全文を入れる。
                    descs.Append(line);
                }
            }
        }

        private static string AssembleSingleCol(string header, string zoneTitle, string body)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(header))
            {
                sb.Append(header.Replace(SEP, ":"));
                sb.Append("\n\n");
            }
            if (!string.IsNullOrEmpty(zoneTitle))
            {
                sb.AppendLine(zoneTitle);
            }
            if (!string.IsNullOrEmpty(body))
            {
                sb.Append(body.Replace(SEP, ":"));
            }
            return sb.ToString();
        }

        private void UpdateConnectionStatus()
        {
            if (_connectionStatusText == null) return;
            if (HapbeatManager.Instance == null)
            {
                _connectionStatusText.text = "Hapbeat: (no manager)";
                _connectionStatusText.color = Color.gray;
                return;
            }
            // AliveDeviceCount = 直近 pingInterval×3 秒以内に PONG を返した device 数
            int count = HapbeatManager.Instance.AliveDeviceCount;
            _connectionStatusText.text = $"Hapbeat: {count} connected";
            _connectionStatusText.color = count > 0
                ? new Color(0.5f, 1f, 0.5f)
                : new Color(1f, 0.7f, 0.4f);
        }
    }
}
