using UnityEngine;

namespace Windups
{
    // F78 airplane mode: typing a post-it. On the Quest this is the system keyboard (keyboard overlay,
    // OculusProjectConfig.requiresSystemKeyboard): Meta's own keyboard comes up in front of the person, and while it
    // is up the system has the input focus (the app sees no hands). Where there is no overlay keyboard (the Editor, or
    // a build without it) a physical keyboard stands in through the Input System: typed characters, Backspace, Enter =
    // done, Escape = cancel. Checks type with Simulate. Poll once a frame.
    public sealed class WindupsKeyboard
    {
        public enum State { Typing, Done, Cancelled }

        public const int MaxLength = 90; // a wall note keeps up to 90 characters

        public string Text { get; private set; } = "";
        public State Status { get; private set; } = State.Typing;
        public bool UsesSystemKeyboard => system != null;
        public static bool Typing => open != null && open.Status == State.Typing;

        private static WindupsKeyboard open;
        private TouchScreenKeyboard system;
        private readonly System.Text.StringBuilder typed = new System.Text.StringBuilder();
        private UnityEngine.InputSystem.Keyboard listening;

        // `text`: the words already there (a post-it being rewritten), ready to be changed.
        public static WindupsKeyboard Open(string placeholder, string text = "")
        {
            open?.Close();
            var k = new WindupsKeyboard();
            open = k;
            text = text ?? "";
            if (text.Length > MaxLength) text = text.Substring(0, MaxLength);
            k.Text = text;
            k.typed.Append(text);
            if (TouchScreenKeyboard.isSupported)
            {
                try { k.system = TouchScreenKeyboard.Open(text, TouchScreenKeyboardType.Default, false, false, false, false, placeholder, MaxLength); }
                catch (System.Exception exception) { Debug.LogWarning("[Windups Keyboard] " + exception.Message); }
            }
            if (k.system == null && UnityEngine.InputSystem.Keyboard.current is { } keys)
            {
                k.listening = keys;
                keys.onTextInput += k.OnCharacter;
            }
            Debug.Log("[Windups Keyboard] open (" + (k.system != null ? "system keyboard" : k.listening != null ? "physical keyboard" : "no keyboard") + ")");
            return k;
        }

        public void Poll()
        {
            if (Status != State.Typing) return;
            if (system != null)
            {
                string now = system.text ?? "";
                if (now.Length > MaxLength) now = now.Substring(0, MaxLength);
                Text = now;
                switch (system.status)
                {
                    case TouchScreenKeyboard.Status.Done: Finish(State.Done); break;
                    case TouchScreenKeyboard.Status.Canceled: Finish(State.Cancelled); break;
                    // The keyboard went away without Done (the person pressed outside it): keep what was typed.
                    case TouchScreenKeyboard.Status.LostFocus: Finish(Text.Trim().Length > 0 ? State.Done : State.Cancelled); break;
                }
                return;
            }
            var keys = listening;
            if (keys == null) { Finish(State.Cancelled); return; }
            if (keys.backspaceKey.wasPressedThisFrame && typed.Length > 0) typed.Length--;
            Text = typed.ToString();
            if (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame) Finish(State.Done);
            else if (keys.escapeKey.wasPressedThisFrame) Finish(State.Cancelled);
        }

        // Checks: the words typed so far, and whether Done was pressed.
        public void Simulate(string text, bool done)
        {
            if (Status != State.Typing) return;
            typed.Clear();
            typed.Append(text ?? "");
            Text = typed.ToString();
            if (done) Finish(State.Done);
        }

        public void Cancel() { if (Status == State.Typing) Finish(State.Cancelled); }

        public void Close()
        {
            if (Status == State.Typing) Finish(State.Cancelled);
        }

        private void OnCharacter(char c)
        {
            if (Status != State.Typing || char.IsControl(c) || typed.Length >= MaxLength) return;
            typed.Append(c);
        }

        private void Finish(State state)
        {
            Status = state;
            if (listening != null) listening.onTextInput -= OnCharacter;
            listening = null;
            if (system != null && system.active) system.active = false;
            if (open == this) open = null;
            Debug.Log("[Windups Keyboard] " + state + ": \"" + Text + "\"");
        }
    }
}
