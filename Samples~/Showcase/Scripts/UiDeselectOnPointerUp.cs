using UnityEngine;
using UnityEngine.EventSystems;

namespace Hapbeat.Samples.Showcase
{
    /// <summary>
    /// UI 要素 (Slider 等) を pointer up (= マウスドラッグ離した) 時点で
    /// EventSystem の選択から外す。Unity の UI Input Module は WASD / 矢印キー
    /// で navigate (Slider 値増減) するため、選択残しだとゲーム入力と被る。
    ///
    /// 使い方: WASD でゲーム移動も行う scene で、Slider にこのコンポーネントを
    /// AddComponent するだけ。マウスでドラッグ → 離す → 自動 deselect →
    /// WASD で値が変化しなくなる。
    /// </summary>
    public class UiDeselectOnPointerUp : MonoBehaviour, IPointerUpHandler
    {
        public void OnPointerUp(PointerEventData eventData)
        {
            var es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject == gameObject)
                es.SetSelectedGameObject(null);
        }
    }
}
