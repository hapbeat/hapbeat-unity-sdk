#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Editor API differences between the Unity 6 releases the SDK supports, kept in one place.
    /// </summary>
    public static class HapbeatEditorCompat
    {
        /// <summary>
        /// Resolves an instance id to its object.
        ///
        /// Unity renamed this API mid-Unity-6: <c>EditorUtility.InstanceIDToObject(int)</c> became
        /// <c>EditorUtility.EntityIdToObject(EntityId)</c>, and the new name does not exist in
        /// Unity 6000.0 LTS at all. Calling it directly broke every editor script in the package
        /// on 6000.0 (verified: the symbol is absent from 6000.0.59f2's UnityEditor.dll and
        /// present in 6000.3.12f1's), which took the whole project's compilation down with it.
        ///
        /// The old name is still present on 6000.3, so the pre-6000.3 branch is the safe one to
        /// fall back to. <c>EntityId</c> converts implicitly from <c>int</c> - instance ids and
        /// entity ids are numerically the same - so callers keep passing the ints that Unity's
        /// own callbacks hand them.
        /// </summary>
        public static Object IdToObject(int id)
        {
#if UNITY_6000_3_OR_NEWER
            return EditorUtility.EntityIdToObject(id);
#else
            return EditorUtility.InstanceIDToObject(id);
#endif
        }
    }
}
#endif
