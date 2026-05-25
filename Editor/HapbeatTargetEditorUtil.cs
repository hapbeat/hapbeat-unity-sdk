#if UNITY_EDITOR
using System.Collections.Generic;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Editor-only helper for parsing and building device-addressing target strings.
    /// Shared by EventMap Window (per-entry Targeting section) and HapbeatManager
    /// Inspector (Test play Targeting) so encoding stays in sync with
    /// <c>device-addressing.md §2</c>: <c>[prefix/]player_M[/pos_X][/group_N]</c>.
    /// </summary>
    internal static class HapbeatTargetEditorUtil
    {
        /// <summary>
        /// Decompose a target string into its semantic parts. Returns sentinel
        /// values for unset parts (player=-1, group=-1, position="", prefix="").
        /// Tokens are picked out by prefix so the parser is tolerant to order.
        /// </summary>
        public static void ParseTarget(string target,
            out string prefix, out int player, out string position, out int group)
        {
            prefix = "";
            player = -1;
            position = "";
            group = -1;

            if (string.IsNullOrEmpty(target)) return;

            var parts = target.Split('/');
            var prefixParts = new List<string>();

            foreach (var part in parts)
            {
                if (part.StartsWith("player_") && int.TryParse(part.Substring(7), out int p))
                    player = p;
                else if (part.StartsWith("group_") && int.TryParse(part.Substring(6), out int g))
                    group = g;
                else if (part.StartsWith("pos_"))
                    position = part;
                else if (part != "*")
                    prefixParts.Add(part);
            }

            prefix = string.Join("/", prefixParts);
        }

        /// <summary>
        /// Build a target string from separate parts.
        /// Path layout (per <c>device-addressing.md §2</c>):
        ///   <c>[prefix/]player_M[/pos_X][/group_N]</c>
        /// <list type="bullet">
        /// <item><c>player == -1</c> → wildcard <c>*</c> or omitted entirely</item>
        /// <item><c>position == ""</c> → omitted (or wildcarded when group is present)</item>
        /// <item><c>group == -1</c> → omitted</item>
        /// </list>
        /// Empty result = broadcast to all devices.
        /// </summary>
        public static string BuildTargetFromParts(string prefix, int player, string position, int group)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(prefix))
                parts.Add(prefix.Trim());

            if (player >= 1)
                parts.Add($"player_{player}");
            else if (!string.IsNullOrEmpty(position) || group >= 1)
                parts.Add("*"); // wildcard player when only position/group is set

            if (!string.IsNullOrEmpty(position))
                parts.Add(position);
            else if (group >= 1)
                parts.Add("*"); // wildcard position when only group is set

            if (group >= 1)
                parts.Add($"group_{group}");

            return string.Join("/", parts);
        }
    }
}
#endif
