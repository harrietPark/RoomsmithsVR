using UnityEngine;

namespace Windups.Handoff
{
    // F78 airplane mode: what the collect mode (WindupsCollect) needs from the crew. It runs before (and instead of)
    // Begin, when the session's line queue does not run yet, so its lines go on the teammate's card at once.
    public sealed partial class MakerSession
    {
        // The crew, its card material and font are set up by EnsureCrew (Begin, Welcome or the introduction's tools); a
        // returning person collects straight after the gathering, before any of those.
        public void PrepareOffline() { if (cast.Count == 0) EnsureCrew(); }

        public WindupCharacter CrewCharacter(Crew crew) => cast.TryGetValue(crew, out var c) ? c : null;

        // The crew's card font (Patrick Hand), for post-its written before any card was shown.
        public TMPro.TMP_FontAsset ToyFont => font;

        // A line on the teammate's own card now, for its reading time, with a hop and its face (Caption mumbles).
        public void OfflineLine(Crew crew, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Caption(crew, text, ReadSeconds(text));
            Talk.Add(MakerRoles.Name(crew) + ": " + text);
            if (cast.TryGetValue(crew, out var c) && c != null)
            {
                WindupBody.Express(c, text, 1.6f);
                StartCoroutine(Bounce(c, 1, .022f));
            }
        }

        // A short reaction without a card: a hop, its face, a pop and a mumble.
        public void OfflineReact(Crew crew, string text)
        {
            if (!cast.TryGetValue(crew, out var c) || c == null) return;
            Talk.Add(MakerRoles.Name(crew) + ": " + text);
            WindupBody.Express(c, text, 1.6f);
            StartCoroutine(Bounce(c, 1, .022f));
            ToySounds.Play(ToySounds.Cue.Pop, c.transform.position + Vector3.up * .15f, .35f, 1.3f);
            MumbleFor(crew, text, true);
        }
    }
}
