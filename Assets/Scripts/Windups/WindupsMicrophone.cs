using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace Windups
{
    // Manual start/stop, mono PCM16 at 24 kHz; gateway or explicit direct-test transport.
    public sealed class WindupsMicrophone : MonoBehaviour
    {
        [Serializable] private sealed class SpeechEvent { public string type; public string delta; public string transcript; public string error; }
        public WindupsMeeting meeting;
        public Renderer cubeRenderer;
        public event Action<string> TranscriptUpdated;
        public event Func<string, bool> TranscriptReady;
        public bool IsCapturing => capturing;
        public bool IsVoiceBusy => connecting || capturing || stopping;
        public void ShowMeetingHint(string message) => ShowArrivalFeedback(message, ReadyColor);
        private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
        private ClientWebSocket socket;
        private CancellationTokenSource cancellation;
        private AudioClip recording;
        private string device;
        private int sentSamples;
        private const int SpeechRate = 24000;
        private Text arrivalFeedback;
        private Image arrivalFeedbackPanel;
        // The pill above the puck also shows what is being said (live), then the final words for a few
        // seconds; it grows for a sentence and shrinks back for short hints.
        private Vector2 pillSize, pillPosition;
        private string spokenShown;
        private float spokenUntil;
        public const float SpokenSeconds = 3.5f;
        public string PillText => arrivalFeedback != null ? arrivalFeedback.text : "";
        public bool PillBig { get; private set; }
        // Toy style: a white pill with dark ink, or a coral pill with white ink while recording/failing.
        private static readonly Color32 ReadyColor = new Color32(255, 255, 255, 245);
        private static readonly Color32 RecordingColor = new Color32(240, 115, 94, 255);
        private static readonly Color32 ReadyInk = new Color32(42, 42, 46, 255);
        public string ArrivalFeedback => arrivalFeedback != null ? arrivalFeedback.text : "";
        public bool IsConnecting => connecting;
        public bool IsStopping => stopping;
        public bool IsHolding => holdRequested;
        // Smoothed 0..1 loudness of the live recording, for the listening ring.
        public float InputLevel { get; private set; }
        public float LastFailureTime { get; private set; } = -10f;
        // True from the moment of the press while audio is actually being recorded.
        public bool IsRecordingAudio => (recording != null && device != null && !stopping && !finishWhenReady && Microphone.IsRecording(device)) || DryCapturing;

        // Who holds the mic down right now: the touching fingertip, a poke or ray, the Editor. The press ends only
        // when nobody holds it any more and ReleaseGrace has passed, so a finger that slips off for a moment and
        // comes back keeps the same recording, and one way of pressing letting go does not end a press another
        // way still holds (F46, 2026-11-12: one slip used to end the recording and send half a sentence).
        public const float ReleaseGrace = .7f; // F68: .4 -> .7 (a hand shifting on the mic while talking)
        private readonly HashSet<string> holders = new HashSet<string>();
        private float releaseAt = -1f;
        private string releaseReason = "";
        public bool IsReleasing => releaseAt >= 0f;
        public int RecordingsStarted { get; private set; }
        public int Releases { get; private set; }
        public int Resumed { get; private set; }
        // Presses made while the last words were still being written down (answered with "One sec…").
        public int BusyPresses { get; private set; }
        public string LastRelease { get; private set; } = "";

#if UNITY_EDITOR
        // Editor checks of the press (AgentScripts/VerifyMicPress): no device mic and no voice service. A press
        // "records" until it is let go, then "writes it down" for DryWriteSeconds.
        public static bool DryRun;
        public const float DryWriteSeconds = .6f;
        private float dryWrittenAt = -1f;
        private bool DryCapturing => DryRun && capturing;
#else
        private const bool DryCapturing = false;
        private const bool DryRun = false; // (Editor checks only; BeginPress reads it in the player build too)
#endif
        private float[] levelSamples;
        private int recordedEnd = -1;
        private bool finishWhenReady;
        private float microphoneCheckAt;
        private bool connecting, capturing, sending, stopping;
        private float startedAt;
        private float deadline;
        private string partial = "";
        private int generation;
        private bool holdRequested;
        private bool authorizing;
        private bool directConnection;
        private float ignoreToggleUntil;

        public void ConfigureArrivalButton(Transform button, Texture2D icon, Material puckMaterial = null, Font font = null, TMPro.TMP_FontAsset crispFont = null)
        {
            if (button == null) return;
            if (cubeRenderer != null) cubeRenderer.enabled = false;
            var label = button.GetComponentInChildren<Text>(true);
            if (label == null) return;
            bool toy = puckMaterial != null;
            var buttonImage = button.GetComponent<Image>();
            // With the toy puck the Image stays as the invisible Poke/Ray target, as big as the mic's head
            // (about 11 cm) so a poke anywhere on it presses.
            buttonImage.color = toy ? new Color(1f, 1f, 1f, 0f) : Color.white;
            if (toy && button is RectTransform buttonRect) buttonRect.sizeDelta = new Vector2(140f, 140f);
            var rect = label.rectTransform;
            if (font != null) label.font = font;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            arrivalFeedback = label;
            if (toy) BuildToyPill(label, crispFont);
            else
            {
                rect.anchorMin = new Vector2(.5f, 1f);
                rect.anchorMax = rect.anchorMin;
                rect.sizeDelta = new Vector2(380f, 104f);
                rect.anchoredPosition = new Vector2(0f, 62f);
                label.fontSize = 24;
                label.color = Color.white;
                label.resizeTextForBestFit = true;
                label.resizeTextMinSize = 18;
                label.resizeTextMaxSize = 24;
                var panel = new GameObject("Microphone Status Background", typeof(RectTransform), typeof(Image));
                var panelRect = (RectTransform)panel.transform;
                panelRect.SetParent(rect, false);
                panelRect.anchorMin = Vector2.zero;
                panelRect.anchorMax = Vector2.one;
                panelRect.offsetMin = new Vector2(-8, -5);
                panelRect.offsetMax = new Vector2(8, 5);
                arrivalFeedbackPanel = panel.GetComponent<Image>();
                arrivalFeedbackPanel.raycastTarget = false;
                // Draw the backdrop before the sibling text, sized with the text (FitPill).
                panelRect.SetParent(rect.parent, false);
                panelRect.SetSiblingIndex(rect.GetSiblingIndex());
                pillSize = rect.sizeDelta;
                pillPosition = rect.anchoredPosition;
                FitPill(false);
                if (crispFont != null)
                {
                    var mirror = label.GetComponent<ToyTextMirror>();
                    if (mirror == null) mirror = label.gameObject.AddComponent<ToyTextMirror>();
                    mirror.Initialize(crispFont, false);
                }
            }
            RawImage image = null;
            if (icon != null)
            {
                image = new GameObject("Microphone Icon", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
                image.rectTransform.SetParent(button, false);
                image.rectTransform.sizeDelta = new Vector2(76f * icon.width / icon.height, 76f);
                image.texture = icon;
                image.raycastTarget = false;
            }
            if (toy)
            {
                var puck = button.GetComponent<ToyMicrophoneButton>();
                if (puck == null) puck = button.gameObject.AddComponent<ToyMicrophoneButton>();
                puck.Initialize(this, image, puckMaterial);
            }
            ResetArrivalFeedback();
        }

        public void ResetArrivalFeedback()
        {
            ShowArrivalFeedback("Hold to talk", ReadyColor);
        }

        public void ShowArrivalTranscriptReady()
        {
            ShowArrivalFeedback("Transcript ready\nTap the note to say it again", ReadyColor);
        }

        // Plain (non-toy) panel only: a sentence (what is being said) grows the panel upward from where it sits.
        public static readonly Vector2 SpokenSize = new Vector2(214f, 46f);
        private Image recordDot;
        private void FitPill(bool spoken)
        {
            if (arrivalFeedback == null || arrivalFeedbackPanel == null) return;
            PillBig = spoken;
            var rect = arrivalFeedback.rectTransform;
            Vector2 size = spoken ? SpokenSize : pillSize;
            rect.sizeDelta = size;
            rect.anchoredPosition = pillPosition + new Vector2(0f, (size.y - pillSize.y) * .5f);
            arrivalFeedback.resizeTextMinSize = spoken ? 10 : 18;
            arrivalFeedback.resizeTextMaxSize = spoken ? 16 : 24;
            var panel = arrivalFeedbackPanel.rectTransform;
            panel.anchorMin = rect.anchorMin;
            panel.anchorMax = rect.anchorMax;
            panel.pivot = rect.pivot;
            panel.anchoredPosition = rect.anchoredPosition;
            panel.sizeDelta = size + new Vector2(16f, 10f);
        }

        // ------------------------------------------------------------ the toy pill above the mic
        // 2026-11-12 (the old pill, about 21 x 6 cm whatever it said, was too big): a small speech bubble that
        // hugs its words, its tail pointing down at the mic, a soft shadow under it, popping in when it
        // changes. A two-line hint shows its first line as the title and the rest smaller and muted
        // ("Listening…" / "Release to stop"). The legacy Text still owns the plain string (PillText).
        // Canvas units (MicButton canvas: 0.00075 m each).
        // The tail's tip above the button's centre: clear of the mic's top as seen from above (the canvas faces the
        // eyes, so the back of the mic's top shows above its centre).
        public const float PillLift = 66f;
        public const int TitleSize = 19, SubPercent = 74, SpokenTextSize = 15;
        private const float PillPadX = 13f, PillPadY = 6.5f, PillMaxText = 176f, SpokenMaxText = 168f;
        private const float TailWidth = 15f, TailHeight = 9f, DotSize = 9f, DotGap = 5f, CornerRadius = 15f;
        private static readonly Color32 PillCream = new Color32(255, 253, 249, 250);
        private static readonly Color32 SubInk = new Color32(132, 124, 142, 255);
        private RectTransform pillGroup;
        private Image pillShadow, pillTail;
        private ToyTextMirror pillMirror;
        private bool pillAlert, pillDot, pillDirty;
        private float pillPopAt = -10f;
        public Vector2 PillPanelSize => arrivalFeedbackPanel != null ? arrivalFeedbackPanel.rectTransform.sizeDelta : Vector2.zero;

        private void BuildToyPill(Text label, TMPro.TMP_FontAsset crispFont)
        {
            var rect = label.rectTransform;
            pillGroup = new GameObject("Microphone Pill", typeof(RectTransform)).GetComponent<RectTransform>();
            pillGroup.SetParent(rect.parent, false);
            pillGroup.SetSiblingIndex(rect.GetSiblingIndex());
            pillGroup.anchorMin = pillGroup.anchorMax = new Vector2(.5f, .5f);
            pillGroup.pivot = new Vector2(.5f, 0f);
            pillGroup.sizeDelta = Vector2.zero;
            pillGroup.anchoredPosition = new Vector2(0f, PillLift);
            pillShadow = PillImage("Shadow", ToyUiSprites.SoftPill, new Color(.2f, .16f, .3f, .2f));
            pillTail = PillImage("Tail", ToyUiSprites.Tail, PillCream);
            arrivalFeedbackPanel = PillImage("Microphone Status Background", ToyUiSprites.Pill, PillCream);
            recordDot = PillImage("Recording Dot", ToyUiSprites.Pill, RecordingColor);
            recordDot.type = Image.Type.Simple;
            recordDot.rectTransform.sizeDelta = new Vector2(DotSize, DotSize);
            recordDot.gameObject.SetActive(false);
            rect.SetParent(pillGroup, false);
            rect.SetAsLastSibling();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0f);
            rect.pivot = new Vector2(.5f, .5f);
            label.resizeTextForBestFit = false;
            label.fontSize = TitleSize;
            label.color = ReadyInk;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.lineSpacing = .92f;
            if (crispFont != null)
            {
                pillMirror = label.GetComponent<ToyTextMirror>();
                if (pillMirror == null) pillMirror = label.gameObject.AddComponent<ToyTextMirror>();
                pillMirror.collapseLineBreaks = false;
                pillMirror.richFormat = PillRich;
                pillMirror.Initialize(crispFont, false);
                if (pillMirror.Target != null) pillMirror.Target.lineSpacing = -6f;
            }
        }

        private Image PillImage(string name, Sprite sprite, Color colour)
        {
            var image = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            var rect = image.rectTransform;
            rect.SetParent(pillGroup, false);
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, 0f);
            rect.pivot = new Vector2(.5f, .5f);
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        // The TMP copy: the first line as the title, the rest smaller and muted; spoken words as one block.
        // Everything said or written stays literal (noparse).
        private string PillRich(string text)
        {
            text = (text ?? "").Replace("\r", "");
            int split = PillBig ? -1 : text.IndexOf('\n');
            if (split < 0) return "<noparse>" + text.Replace('\n', ' ') + "</noparse>";
            string title = text.Substring(0, split).Trim(), rest = text.Substring(split + 1).Replace('\n', ' ').Trim();
            string ink = pillAlert ? "#FFFFFFD8" : "#" + ColorUtility.ToHtmlStringRGB(SubInk);
            return "<noparse>" + title + "</noparse>\n<size=" + SubPercent + "%><color=" + ink + "><noparse>" + rest + "</noparse></color></size>";
        }

        // Sizes the bubble round its words (wrapped past PillMaxText) and puts the tail, shadow and dot round it.
        private void LayoutToyPill()
        {
            // TMP measures only once it is awake: while the mic is hidden this waits for it to show.
            pillDirty = !pillGroup.gameObject.activeInHierarchy;
            if (pillDirty) return;
            var label = arrivalFeedback;
            string text = label.text ?? "";
            int firstLine = text.IndexOf('\n');
            if (firstLine < 0) firstLine = text.Length;
            // A long first line (an error) is set a little smaller so the bubble stays small.
            int size = PillBig ? SpokenTextSize : firstLine > 24 ? TitleSize - 3 : TitleSize;
            float limit = PillBig ? SpokenMaxText : PillMaxText;
            label.fontSize = size;
            Vector2 words;
            if (pillMirror != null && pillMirror.Target != null && pillMirror.CanShow(text))
            {
                var tmp = pillMirror.Target;
                tmp.enableAutoSizing = false;
                tmp.fontSize = size;
                tmp.richText = true;
                string rich = PillRich(text);
                Vector2 natural = tmp.GetPreferredValues(rich, 4000f, 4000f);
                if (natural.x <= limit) words = natural;
                else
                {
                    // Wrapped: lines of about the same length (no lonely last word), as narrow as that allows.
                    float height = tmp.GetPreferredValues(rich, limit, 4000f).y, width = limit;
                    int lines = Mathf.Max(2, Mathf.RoundToInt(height / Mathf.Max(1f, tmp.GetPreferredValues("Ag", 4000f, 4000f).y)));
                    for (float tryWidth = Mathf.Ceil(natural.x / lines) + 4f; tryWidth < limit; tryWidth += 6f)
                        if (tmp.GetPreferredValues(rich, tryWidth, 4000f).y <= height + .5f) { width = tryWidth; break; }
                    words = new Vector2(Mathf.Min(width, tmp.GetPreferredValues(rich, width, 4000f).x + 1f), height);
                }
            }
            else
            {
                var layout = label.cachedTextGeneratorForLayout;
                float natural = layout.GetPreferredWidth(text, label.GetGenerationSettings(Vector2.zero)) / label.pixelsPerUnit;
                float width = Mathf.Min(natural, limit);
                words = new Vector2(width, layout.GetPreferredHeight(text, label.GetGenerationSettings(new Vector2(width, 0f))) / label.pixelsPerUnit);
            }
            words = new Vector2(Mathf.Ceil(words.x + 2f), Mathf.Ceil(words.y));
            float dotSpace = pillDot ? DotSize + DotGap : 0f;
            var panel = new Vector2(words.x + PillPadX * 2f + dotSpace, words.y + PillPadY * 2f);
            panel.x = Mathf.Max(panel.x, panel.y + 14f);
            // One line: a capsule; more lines: a rounded card.
            bool oneLine = words.y < size * 1.6f;
            float radius = oneLine ? panel.y * .5f : Mathf.Min(panel.y * .5f, CornerRadius);
            var centre = new Vector2(0f, TailHeight - 1.5f + panel.y * .5f);
            var back = arrivalFeedbackPanel.rectTransform;
            back.sizeDelta = panel;
            back.anchoredPosition = centre;
            arrivalFeedbackPanel.pixelsPerUnitMultiplier = 31f / radius;
            var shadow = pillShadow.rectTransform;
            shadow.sizeDelta = panel + new Vector2(10f, 10f);
            shadow.anchoredPosition = centre + new Vector2(0f, -3f);
            pillShadow.pixelsPerUnitMultiplier = 31f / (radius + 5f);
            var tail = pillTail.rectTransform;
            tail.pivot = new Vector2(.5f, 0f);
            tail.sizeDelta = new Vector2(TailWidth, TailHeight);
            tail.anchoredPosition = Vector2.zero;
            pillTail.type = Image.Type.Simple;
            label.rectTransform.sizeDelta = words;
            label.rectTransform.anchoredPosition = centre + new Vector2(dotSpace * .5f, 0f);
            recordDot.rectTransform.anchoredPosition = centre + new Vector2(-panel.x * .5f + PillPadX + DotSize * .5f - 2f, 0f);
            recordDot.color = pillAlert ? new Color(1f, 1f, 1f, .95f) : (Color)RecordingColor;
            recordDot.gameObject.SetActive(pillDot);
        }

        private void AnimateToyPill()
        {
            if (pillGroup == null) return;
            if (pillDirty && pillGroup.gameObject.activeInHierarchy) { LayoutToyPill(); pillPopAt = Time.unscaledTime; }
            // A small pop (overshoot) from the tail when the bubble changes.
            float t = (Time.unscaledTime - pillPopAt) / .22f;
            float scale = 1f;
            if (t < 1f)
            {
                float s = 1.70158f, u = t - 1f;
                scale = Mathf.Lerp(.86f, 1f, 1f + (s + 1f) * u * u * u + s * u * u);
            }
            pillGroup.localScale = Vector3.one * scale;
        }

        // The words being said, live (only the last few words), then the final words for a moment.
        // Live words sit right of a small coral dot that pulses with the voice.
        private void ShowSpoken(string words, bool final)
        {
            words = (words ?? "").Trim();
            if (words.Length == 0) return;
            if (words.Length > 38) words = "…" + words.Substring(words.Length - 37).TrimStart();
            string shown = final ? "“" + words + "”" : words;
            ShowArrivalFeedback(shown, ReadyColor, true, !final);
            spokenShown = shown;
            spokenUntil = final ? Time.unscaledTime + SpokenSeconds : 0f;
        }

#if UNITY_EDITOR
        // Editor tests: what the pill shows for live and final words.
        public void PreviewSpoken(string words, bool final) => ShowSpoken(words, final);
        // Editor tests: a hint as it shows while ready (cream) or while recording/failing (coral).
        public void PreviewHint(string message, bool alert) => ShowArrivalFeedback(message, alert ? RecordingColor : ReadyColor);
#endif

        private void ShowArrivalFeedback(string message, Color32 color, bool big = false, bool dot = false)
        {
            if (arrivalFeedback == null) return;
            bool alert = color.Equals(RecordingColor);
            if (pillGroup != null)
            {
                // Pops when it says something new (not while live words keep growing).
                bool changed = arrivalFeedback.text != message || PillBig != big || pillAlert != alert;
                if (changed && !(big && PillBig)) pillPopAt = Time.unscaledTime;
                PillBig = big;
                if (!big) spokenShown = null;
                pillAlert = alert;
                pillDot = dot;
                arrivalFeedback.text = message;
                Color fill = alert ? (Color)RecordingColor : (Color)PillCream;
                arrivalFeedbackPanel.color = fill;
                pillTail.color = fill;
                arrivalFeedback.color = alert ? Color.white : (Color)ReadyInk;
                LayoutToyPill();
                return;
            }
            if (PillBig != big) FitPill(big);
            if (!big) spokenShown = null;
            arrivalFeedback.text = message;
            arrivalFeedbackPanel.color = alert ? new Color32(153, 34, 44, 255) : new Color32(28, 48, 63, 255);
            arrivalFeedback.color = Color.white;
        }

        public void Cancel() { holders.Clear(); releaseAt = -1f; holdRequested = false; Cleanup(); if (meeting != null) meeting.SetListening(false); }

        public void Toggle()
        {
            if (Time.unscaledTime < ignoreToggleUntil) return;
            if (meeting.LocalDemoEnabled) { Fail("Voice is unavailable in Local Demo."); return; }
            if (capturing) { holdRequested = false; _ = Finish(); return; }
            if (connecting || stopping) return;
            holdRequested = true;
            StartCoroutine(Authorize());
        }
        public void BeginPress() => BeginPress("press");
        public void EndPress() => EndPress("press");

        // `by` names who presses (see holders). Returns false when the press is not taken: the last words are
        // still being written down (said on the pill, F46: such a press used to be ignored without a word).
        public bool BeginPress(string by)
        {
            // (DryRun: the Editor checks press the mic in the scripted demo, with no device mic or voice service.)
            if (meeting.LocalDemoEnabled && !DryRun) { Fail("Voice is unavailable in Local Demo."); return false; }
            if (stopping || finishWhenReady)
            {
                BusyPresses++;
                LogPress("busy, press not taken (" + by + ")");
                ShowArrivalFeedback("One sec…\nStill writing it down", ReadyColor);
                return false;
            }
            bool live = capturing || connecting;
            bool back = live && releaseAt >= 0f;
            holders.Add(by);
            releaseAt = -1f;
            holdRequested = true;
            if (back) { Resumed++; LogPress("back within " + ReleaseGrace + " s, same recording (" + by + ")"); return true; }
            if (live) return true;
            RecordingsStarted++;
            LogPress("press (" + by + ")");
            StartCoroutine(Authorize());
            return true;
        }

        // Lets go for `by`; the press ends ReleaseGrace later unless someone holds it again by then.
        public void EndPress(string by, string why = "let go")
        {
            if (!holders.Remove(by) || holders.Count > 0) return;
            releaseAt = Time.unscaledTime + ReleaseGrace;
            releaseReason = why + " (" + by + ")";
        }

        private void ReleaseNow()
        {
            releaseAt = -1f;
            holdRequested = false;
            Releases++;
            LastRelease = releaseReason;
            LogPress("let go: " + releaseReason);
            ignoreToggleUntil = Time.unscaledTime + .35f;
            if (capturing) { _ = Finish(); return; }
            // Released while the voice service is still connecting: keep what was said and
            // send it as soon as the connection is ready, instead of throwing it away.
            if (connecting && !authorizing && recording != null && device != null)
            {
                recordedEnd = Microphone.GetPosition(device);
                if (Microphone.IsRecording(device)) Microphone.End(device);
                if (AvailableOutputSamples(recordedEnd) < SpeechRate / 10)
                {
                    Cleanup();
                    meeting.SetListening(false);
                    ShowArrivalFeedback("Too short\nHold and speak", ReadyColor);
                    return;
                }
                finishWhenReady = true;
                meeting.status.text = "Finishing transcription…";
                ShowArrivalFeedback("Got it\nWriting it down…", ReadyColor);
                return;
            }
            if (connecting && !authorizing)
            {
                Cleanup();
                meeting.SetListening(false);
                meeting.status.text = "Hold the microphone to speak.";
                ShowArrivalFeedback("Released before recording\nHold until it says Speak now", ReadyColor);
            }
        }
        private IEnumerator Authorize()
        {
            connecting = true;
            authorizing = true;
            ShowArrivalFeedback("Getting ready…\nKeep holding", ReadyColor);
#if UNITY_EDITOR
            if (DryRun)
            {
                yield return null;
                authorizing = false;
                connecting = false;
                if (!holdRequested) { ResetArrivalFeedback(); yield break; }
                capturing = true;
                startedAt = Time.realtimeSinceStartup;
                meeting.SetListening(true);
                ShowArrivalFeedback("Listening…\nRelease to stop", RecordingColor);
                yield break;
            }
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
            // Asked with the others at start; refused, it is asked again now (each time the mic is held).
            if (!WindupsPermissions.Has(WindupsPermissions.Microphone))
            {
                yield return WindupsPermissions.Ensure(WindupsPermissions.Microphone, null);
                if (!WindupsPermissions.Has(WindupsPermissions.Microphone))
                {
                    Fail(WindupsPermissions.Blocked(WindupsPermissions.Microphone) ? "Allow the microphone in the app's permissions." : "The microphone is needed to talk.");
                    yield break;
                }
            }
#else
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
#endif
            authorizing = false;
            if (!holdRequested)
            {
                connecting = false;
                meeting.status.text = "Hold the microphone to speak.";
                ResetArrivalFeedback();
                yield break;
            }
            if (Microphone.devices.Length == 0) { Fail("No microphone found. Check the input device."); yield break; }
            device = Microphone.devices[0];
            // People start talking the moment they press. Record immediately and buffer;
            // the backlog is streamed once the voice service is ready.
            if (!StartRecording()) yield break;
            meeting.SetListening(true);
            meeting.status.text = "Recording — speak now. Release to stop.";
            ShowArrivalFeedback("Listening…\nRelease to stop", RecordingColor);
            deadline = Time.realtimeSinceStartup + 15;
            _ = Connect();
        }

        private bool StartRecording()
        {
            try
            {
                Microphone.GetDeviceCaps(device, out int minimum, out int maximum);
                int requestedRate = maximum == 0 ? SpeechRate : Mathf.Clamp(SpeechRate, minimum, maximum);
                recording = Microphone.Start(device, false, 61, requestedRate);
            }
            catch (Exception) { Fail("Could not start the microphone. Check microphone permission."); return false; }
            if (recording == null) { Fail("Could not start the microphone. Check microphone permission."); return false; }
            sentSamples = 0; partial = ""; startedAt = Time.realtimeSinceStartup;
            PeakLevel = 0f;
            recordedEnd = -1; finishWhenReady = false;
            microphoneCheckAt = Time.realtimeSinceStartup + 3f;
            return true;
        }
        private async Task Connect()
        {
            int version = ++generation;
            cancellation = new CancellationTokenSource();
            socket = new ClientWebSocket();
            directConnection = meeting.directOpenAI;
            try
            {
                Uri endpoint;
                if (directConnection)
                {
                    if (!WindupsDirectCredentials.TryGetKey(out string key)) { Fail("Voice access is not configured in this test build."); return; }
                    socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
                    endpoint = new Uri(WindupsDirectSpeech.Endpoint);
                }
                else
                {
                    if (!string.IsNullOrEmpty(meeting.gatewayToken)) socket.Options.SetRequestHeader("Authorization", "Bearer " + meeting.gatewayToken);
                    endpoint = new UriBuilder(meeting.serverUrl) { Scheme = meeting.serverUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws", Path = "/v1/speech" }.Uri;
                }
                await socket.ConnectAsync(endpoint, cancellation.Token);
                if (version != generation) return;
                if (directConnection) await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(WindupsDirectSpeech.Configure)), WebSocketMessageType.Text, true, cancellation.Token);
                await Receive(socket, version, cancellation.Token);
                if (version == generation) messages.Enqueue("{\"type\":\"error\",\"error\":\"Voice connection ended. Hold to reconnect.\"}");
            }
            catch (Exception) { if (version == generation) messages.Enqueue("{\"type\":\"error\",\"error\":\"Could not connect to voice service. Check your internet connection and try again.\"}"); }
        }
        private async Task Receive(ClientWebSocket connection, int version, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            while (connection.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using var payload = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await connection.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    payload.Write(buffer, 0, result.Count);
                    if (payload.Length > 64000) throw new InvalidOperationException("Speech event too large");
                } while (!result.EndOfMessage);
                if (version == generation)
                {
                    string message = Encoding.UTF8.GetString(payload.ToArray());
                    if (directConnection) message = WindupsDirectSpeech.Normalize(message);
                    if (message != null) messages.Enqueue(message);
                }
            }
        }
        private void Update()
        {
            if (releaseAt >= 0f && holders.Count == 0 && Time.unscaledTime >= releaseAt) ReleaseNow();
#if UNITY_EDITOR
            if (DryRun && stopping && dryWrittenAt > 0f && Time.unscaledTime >= dryWrittenAt)
            {
                dryWrittenAt = -1f;
                stopping = false;
                ResetArrivalFeedback();
            }
#endif
            while (messages.TryDequeue(out string message))
            {
                SpeechEvent e;
                try { e = JsonUtility.FromJson<SpeechEvent>(message); } catch { Fail("Invalid speech response."); return; }
                if (e.type == "ready" && connecting)
                {
                    if (recording == null) { Fail("Could not start the microphone. Check microphone permission."); return; }
                    // Already recording since the press; the buffered audio goes out from sample 0.
                    capturing = true; connecting = false; deadline = 0f;
                    meeting.transcript.text = "";
                    if (finishWhenReady) { finishWhenReady = false; _ = Finish(); }
                }
                else if (e.type == "delta" && (capturing || stopping) && !string.IsNullOrEmpty(e.delta))
                {
                    partial += e.delta;
                    meeting.transcript.text = partial;
                    ShowSpoken(partial, false);
                    TranscriptUpdated?.Invoke(partial);
                }
                else if (e.type == "final")
                {
                    string final = e.transcript;
                    // Keep the live text on screen until the final text replaces it (no blank flash).
                    partial = "";
                    Cleanup();
                    meeting.SetListening(false);
                    if (string.IsNullOrWhiteSpace(final))
                    {
                        Fail("No speech detected. Hold and try again.");
                        return;
                    }
                    // Only what the model makes up from a nearly silent recording is dropped ("Thank you.",
                    // "Thanks for watching"); anything else that was transcribed is kept.
                    bool silent = Hallucinated(final) && PeakLevel < MadeUpPeak;
                    LogHeard(final, silent);
                    if (silent)
                    {
                        DroppedSilent++;
                        Fail("I didn't hear you. Hold and speak a bit closer.");
                        return;
                    }
                    // The final words stay on the pill a moment (unless the session puts up a hint).
                    ShowSpoken(final, true);
                    bool handled = false;
                    if (TranscriptReady != null)
                        foreach (var callback in TranscriptReady.GetInvocationList())
                            handled |= ((Func<string, bool>)callback)(final);
                    if (!handled) meeting.Submit(final);
                }
                else if (e.type == "error") { Fail(e.error); return; }
            }
            if (microphoneCheckAt > 0 && Time.realtimeSinceStartup > microphoneCheckAt)
            {
                microphoneCheckAt = 0;
                if (recording != null && device != null && Microphone.IsRecording(device) && Microphone.GetPosition(device) <= 0)
                {
                    Fail("Microphone did not start. Check the input device.");
                    return;
                }
            }
            if (deadline > 0 && Time.realtimeSinceStartup > deadline)
            {
                Fail("Voice service timed out. Hold to retry.");
                return;
            }
            if (capturing && !sending && !stopping)
            {
                if (Time.realtimeSinceStartup - startedAt >= 59) _ = Finish();
                else if (recording != null && Microphone.IsRecording(device) && AvailableOutputSamples(Microphone.GetPosition(device)) - sentSamples >= 2400)
                    _ = SendAudio(Microphone.GetPosition(device));
            }
            // The dot breathes with the voice while listening; once nothing is heard any more it goes and the
            // bubble closes round the words again.
            if (recordDot != null && recordDot.gameObject.activeSelf)
            {
                float pulse = 1f + InputLevel * .7f + .08f * Mathf.Sin(Time.unscaledTime * 8f);
                recordDot.rectTransform.localScale = Vector3.one * pulse;
                if (!capturing && !stopping)
                {
                    pillDot = false;
                    if (pillGroup != null) LayoutToyPill();
                    else recordDot.gameObject.SetActive(false);
                }
            }
            AnimateToyPill();
            // After a few seconds the said words give way to "Hold to talk" again (only if nothing else
            // has been shown since).
            if (spokenShown != null && spokenUntil > 0f && Time.unscaledTime > spokenUntil && !IsVoiceBusy)
            {
                if (arrivalFeedback != null && arrivalFeedback.text == spokenShown) ResetArrivalFeedback();
                spokenShown = null;
            }
            UpdateInputLevel();
            if (cubeRenderer != null && cubeRenderer.enabled) cubeRenderer.material.color = capturing ? new Color(1,.2f,.15f) : connecting || stopping ? Color.yellow : new Color(.2f,.7f,.9f);
        }

        // Loudest moment of the last recording (0-1, like InputLevel). A transcript that is only one of the
        // phrases models make up from silence is dropped when the recording stayed under MadeUpPeak; real
        // words (even quiet ones, even "okay") always go through. (2026-11-11: a fixed loudness cut-off and a
        // reset on every press attempt threw away real speech on the headset.)
        public float PeakLevel { get; private set; }
        public int DroppedSilent { get; private set; }
        public const float MadeUpPeak = .12f;
        private static readonly string[] MadeUp = { "thank you", "thanks for watching", "thank you for watching", "thank you so much for watching", "you", "bye", "bye bye",
            "please subscribe", "subscribe", "시청해 주셔서 감사합니다", "시청해주셔서 감사합니다", "구독과 좋아요", "구독" };

        // Every final transcript with its loudness, kept on the headset (mic_log.txt) for checking.
        public static string MicLogPath => System.IO.Path.Combine(Application.persistentDataPath, "mic_log.txt");
        private void LogHeard(string text, bool dropped)
        {
            AppendMicLog(DateTime.Now.ToString("MM-dd HH:mm:ss") + " peak " + PeakLevel.ToString("F3") + (dropped ? " DROPPED " : " kept ") + "\"" + text + "\"");
        }

        // Press events too (F46: press, let go and why, back within the grace, busy), so a headset try shows why a
        // recording started or ended.
        private void LogPress(string what) => AppendMicLog(DateTime.Now.ToString("MM-dd HH:mm:ss.f") + " " + what);

        private static void AppendMicLog(string line)
        {
            Debug.Log("[Windups Mic] " + line);
            try
            {
                if (System.IO.File.Exists(MicLogPath) && new System.IO.FileInfo(MicLogPath).Length > 100000) System.IO.File.Delete(MicLogPath);
                System.IO.File.AppendAllText(MicLogPath, line + "\n");
            }
            catch { }
        }
        public static bool Hallucinated(string text)
        {
            string t = (text ?? "").ToLowerInvariant().Trim().TrimEnd('.', '!', '?', ',').Trim();
            return t.Length <= 1 || System.Array.IndexOf(MadeUp, t) >= 0;
        }
        public void SimulateFinal(string text, float peak) { PeakLevel = peak; messages.Enqueue("{\"type\":\"final\",\"transcript\":\"" + text + "\"}"); }

        private void UpdateInputLevel()
        {
            float target = 0f;
            if (IsRecordingAudio && recording != null)
            {
                const int window = 512;
                int position = Microphone.GetPosition(device);
                int channels = Mathf.Max(1, recording.channels);
                if (position > window)
                {
                    if (levelSamples == null || levelSamples.Length != window * channels) levelSamples = new float[window * channels];
                    if (recording.GetData(levelSamples, position - window))
                    {
                        float sum = 0f;
                        for (int i = 0; i < levelSamples.Length; i++) sum += levelSamples[i] * levelSamples[i];
                        target = Mathf.Clamp01(Mathf.Sqrt(sum / levelSamples.Length) * 9f);
                        PeakLevel = Mathf.Max(PeakLevel, target);
                    }
                }
            }
            InputLevel = Mathf.Lerp(InputLevel, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * (target > InputLevel ? 22f : 7f)));
        }

        private int AvailableOutputSamples(int inputFrames) =>
            (int)((long)inputFrames * SpeechRate / recording.frequency);

        private async Task SendAudio(int end)
        {
            end = AvailableOutputSamples(end);
            if (end <= sentSamples) return;
            int version = generation;
            sending = true;
            try
            {
                // Bound packets to <= 0.5 seconds even if a frame stalls.
                while (sentSamples < end)
                {
                    int count = Math.Min(end - sentSamples, 12000);
                    int rate = recording.frequency;
                    int channels = recording.channels;
                    int firstFrame = (int)((long)sentSamples * rate / SpeechRate);
                    int lastFrame = (int)((long)(sentSamples + count) * rate / SpeechRate);
                    float[] samples = new float[Math.Max(1, lastFrame - firstFrame) * channels];
                    if (!recording.GetData(samples, firstFrame)) throw new InvalidOperationException("Microphone samples unavailable");
                    byte[] pcm = new byte[count * 2];
                    for (int i = 0; i < count; i++)
                    {
                        int first = (int)((long)(sentSamples + i) * rate / SpeechRate) - firstFrame;
                        int last = (int)((long)(sentSamples + i + 1) * rate / SpeechRate) - firstFrame;
                        first = Math.Min(first * channels, samples.Length - channels);
                        last = Math.Max(last * channels, first + channels);
                        float sum = 0f;
                        for (int j = first; j < last; j++) sum += samples[j];
                        short value = (short)(Mathf.Clamp(sum / (last - first), -1, 1) * 32767);
                        pcm[i * 2] = (byte)value;
                        pcm[i * 2 + 1] = (byte)(value >> 8);
                    }
                    byte[] packet = directConnection ? Encoding.UTF8.GetBytes(WindupsDirectSpeech.Audio(pcm)) : pcm;
                    await socket.SendAsync(new ArraySegment<byte>(packet), directConnection ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, cancellation.Token);
                    if (version != generation) return;
                    sentSamples += count;
                }
            }
            catch (Exception) { if (version == generation) Fail("Audio upload failed. Please try again."); }
            finally { if (version == generation) sending = false; }
        }
        private async Task Finish()
        {
            if (!capturing || stopping) return;
            capturing = false; stopping = true;
#if UNITY_EDITOR
            if (DryRun)
            {
                dryWrittenAt = Time.unscaledTime + DryWriteSeconds;
                meeting.SetListening(false);
                ShowArrivalFeedback("Got it\nWriting it down…", ReadyColor);
                return;
            }
#endif
            int version = generation;
            int end = recordedEnd >= 0 ? recordedEnd : Microphone.GetPosition(device);
            if (Microphone.IsRecording(device)) Microphone.End(device);
            if (AvailableOutputSamples(end) < SpeechRate / 10)
            {
                Fail("Recording too short. Hold until Speak now.");
                return;
            }
            meeting.status.text = "Finishing transcription…";
            ShowArrivalFeedback("Got it\nWriting it down…", ReadyColor);
            deadline = Time.realtimeSinceStartup + 25;
            while (sending && version == generation) await Task.Yield();
            if (version != generation) return;
            await SendAudio(end);
            if (version != generation || socket == null) return;
            try { await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(directConnection ? WindupsDirectSpeech.Commit : "commit")), WebSocketMessageType.Text, true, cancellation.Token); }
            catch (Exception) { if (version == generation) Fail("Could not finish transcription."); }
        }
        private void Fail(string error)
        {
            Cleanup();
            LastFailureTime = Time.unscaledTime;
            Debug.LogWarning("[Windups Speech] " + error);
            ShowArrivalFeedback(error + "\nHold to retry", RecordingColor);
            if (meeting != null) meeting.ReportError(error);
        }
        private void Cleanup()
        {
            StopAllCoroutines();
            generation++;
            if (device != null && Microphone.IsRecording(device)) Microphone.End(device);
            cancellation?.Cancel(); socket?.Abort(); socket?.Dispose(); cancellation?.Dispose();
            socket = null; cancellation = null;
            if (recording != null) Destroy(recording);
            recording = null;
            if (partial.Length > 0)
            {
                partial = "";
                meeting.transcript.text = "";
                TranscriptUpdated?.Invoke("");
            }
            connecting = capturing = stopping = sending = authorizing = false; deadline = 0;
            recordedEnd = -1; finishWhenReady = false; microphoneCheckAt = 0;
            while (messages.TryDequeue(out _)) { }
        }
        private void OnApplicationPause(bool paused) { if (paused) { Cleanup(); ResetArrivalFeedback(); meeting.SetListening(false); } }
        private void OnDisable() { Cleanup(); }
    }
}
