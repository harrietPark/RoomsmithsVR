using UnityEngine;
using UnityEngine.EventSystems;

namespace Windups
{
    // Poke, ray or (Editor) mouse on the mic's button. It is one of the mic's holders (WindupsMicrophone.BeginPress):
    // letting go here ends the press only when no other holder (the touching fingertip) still holds it, after the
    // microphone's ReleaseGrace (F46).
    public sealed class PressAndHoldMicrophone : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public const string Holder = "poke";
        // F60 (set by WindupsArrival): true when a press now would be an accident (a hand holding something, or a
        // pinch's ray from a hand working elsewhere). Fingertip presses come through the arrival's own touch too.
        public static System.Func<bool> Blocked;
        public WindupsMicrophone microphone;
        private bool pressed;
        private int pressingPointer;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!isActiveAndEnabled || pressed || microphone == null || eventData.button != PointerEventData.InputButton.Left) return;
            if (Blocked != null && Blocked()) return;
            if (!microphone.BeginPress(Holder)) return;
            pressed = true;
            pressingPointer = eventData.pointerId;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.pointerId == pressingPointer) Release("poke up");
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (eventData.pointerId == pressingPointer) Release("poke left the button");
        }

        private void Release(string why)
        {
            if (!pressed) return;
            pressed = false;
            if (microphone != null) microphone.EndPress(Holder, why);
        }

        private void OnDisable()
        {
            Release("button hidden");
        }
    }
}
