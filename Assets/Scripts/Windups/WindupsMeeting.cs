using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RoomCapsule;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace Windups
{
    public sealed class WindupsMeeting : MonoBehaviour
    {
        public WindupCharacter[] characters;
        public CapsuleRoomEnvironment environment;
        public Text status;
        public Text transcript;
        public Text modeLabel;
        public InputField typedInput;
        public string serverUrl = "http://127.0.0.1:8787";
        [Tooltip("Test only: call OpenAI directly. A packaged key can be extracted from the APK.")]
        public bool directOpenAI = true;
        [Tooltip("Restricted gateway token, never an OpenAI key.")]
        public string gatewayToken;
        public bool LocalDemoEnabled { get; private set; }
        public Transform Head { get; private set; }
        public WindupCharacter Speaker { get; private set; }
        public bool IsListening { get; private set; }
        // True while the user holds a character's card: everyone falls quiet and looks at the user.
        public bool UserHolding { get; set; }
        public bool Busy { get; private set; }
        public int PresentedCount { get; private set; }
        public int ClarificationCount { get; private set; }
        public string SessionId => sessionId;
        public bool WaitingForAnswer => waitingForAnswer;
        public List<MeetingLine> History { get; } = new List<MeetingLine>();
        private readonly string sessionId = Guid.NewGuid().ToString("N");
        private int generation;
        public bool UsesArrivalSeats { get; private set; }
        public string SelectedDirection { get; private set; }
        private WindupsMicrophone arrivalMicrophone;
        private bool choosingDirection;
        private readonly Queue<(WindupCharacter reviewer, MeetingLine source)> handoffReviews = new();
        private (WindupCharacter reviewer, MeetingLine source)? activeReview;
        private bool presentingReview;

        public void BeginArrivedMeeting(string topic, WindupsMicrophone microphone)
        {
            UsesArrivalSeats = true;
            arrivalMicrophone = microphone;
            Submit(topic);
        }

        private void Hint(string message)
        {
            status.text = message;
            arrivalMicrophone?.ShowMeetingHint(message);
        }
        private int automaticTurns;
        // The team carries on by itself at NextTurnAt (shown by the hourglass) when nobody picks an idea.
        public float NextTurnAt => nextTurnAt;
        public bool AutoTurnWaiting => meetingActive && Speaker == null && !waitingForAnswer && !choosingDirection && automaticTurns < 2 &&
                                       characters.Any(c => c.Pending.Count > 0) && Time.time < nextTurnAt;
        private bool waitingForAnswer;
        private bool wrappingUp;
        private bool meetingActive;
        private float nextTurnAt;
        private float finishAt;
        private UnityWebRequest activeRequest;
        private float placementAt;
        private bool uiPlaced;
        private float diagnosticAt;
        private int roomRevision = -1;
        private readonly Dictionary<WindupCharacter, Vector3> seats = new Dictionary<WindupCharacter, Vector3>();

        private void Start()
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            Head = rig != null ? rig.centerEyeAnchor : Camera.main != null ? Camera.main.transform : null;
#if UNITY_EDITOR
            if (!UnityEngine.XR.XRSettings.isDeviceActive)
            {
                var pointable = FindFirstObjectByType<Oculus.Interaction.PointableCanvasModule>();
                if (pointable != null)
                {
                    pointable.enabled = false; // Mouse/keyboard only in desktop Editor preview.
                    var mouse = pointable.gameObject.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
                    mouse.AssignDefaultActions();
                }
            }
#endif
            foreach (var character in characters) character.Refresh();
            status.text = "Hold the microphone to speak.\nYou can also type a topic below.";
            Debug.Log("[Windups] Meeting started; awaiting tracked head for UI placement");
        }

        public void SubmitTyped()
        {
            FindFirstObjectByType<WindupsMicrophone>()?.Cancel();
            string input = typedInput.text;
            typedInput.text = "";
            Submit(input);
        }
        public void StartLocalDemo()
        {
            FindFirstObjectByType<WindupsMicrophone>()?.Cancel();
            LocalDemoEnabled = true;
            modeLabel.text = "LOCAL SCRIPTED DEMO — no AI / no API cost";
            ClarificationCount = 0;
            History.Clear();
            foreach (var actor in characters) { actor.Pending.Clear(); actor.Finish(); }
            Submit("I want to make an app.");
        }
        // Editor preview: answer with the scripted fixture from the next turn on, without
        // restarting the conversation (used by the "skip to meeting" shortcut).
        public void UseScriptedReplies()
        {
            LocalDemoEnabled = true;
            if (modeLabel != null) modeLabel.text = "LOCAL SCRIPTED DEMO — no AI / no API cost";
        }

        public void UseGateway()
        {
            FindFirstObjectByType<WindupsMicrophone>()?.Cancel();
            Interrupt();
            LocalDemoEnabled = false;
            meetingActive = false;
            waitingForAnswer = false;
            foreach (var actor in characters) { actor.Pending.Clear(); actor.Finish(); actor.movement.RoamingEnabled = true; }
            modeLabel.text = "OPENAI";
            status.text = "Type a topic or hold the microphone to speak.";
        }

        public void SetListening(bool listening)
        {
            IsListening = listening;
            if (listening)
            {
                Interrupt();
                automaticTurns = 0;
                status.text = "Listening — release the microphone when you're done.";
                foreach (var actor in characters) actor.SetCue("listen");
            }
            else nextTurnAt = Time.time + 10;
        }

        public void ReportError(string message)
        {
            IsListening = false;
            Hint(message);
            automaticTurns = 2;
            nextTurnAt = Time.time + 10;
        }

        public void Submit(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) { ReportError("No words received. Please try again or type a topic."); return; }
            if (choosingDirection)
            {
                RecordDirection(input.Trim());
                return;
            }
            if (UsesArrivalSeats && meetingActive && !waitingForAnswer && input.Trim().TrimEnd('.').Equals("continue", StringComparison.OrdinalIgnoreCase))
            {
                ContinueMeeting();
                return;
            }
            bool clearOptions = waitingForAnswer;
            bool wasActive = meetingActive;
            Interrupt();
            if (clearOptions) ClearChoices();
            IsListening = false;
            meetingActive = true;
            waitingForAnswer = false;
            automaticTurns = 0;
            transcript.text = input;
            History.Add(new MeetingLine { id = Guid.NewGuid().ToString("N"), speaker = "User", body = input, summary = input });
            string trigger = (!UsesArrivalSeats || wasActive) && input.Trim().TrimEnd('.', '!', '?').Equals("wrap up", StringComparison.OrdinalIgnoreCase) ? "wrapup" : "user";
            wrappingUp = trigger == "wrapup";
            StartCoroutine(RequestTurn(trigger, input, generation));
        }

        public void WrapUp() { FindFirstObjectByType<WindupsMicrophone>()?.Cancel(); Submit("Wrap up"); }
        public void ContinueMeeting()
        {
            automaticTurns = 0;
            nextTurnAt = Time.time;
            if (UsesArrivalSeats) Hint("Pick an idea, or hold to talk.");
        }
        public void Roam()
        {
            FindFirstObjectByType<WindupsMicrophone>()?.Cancel();
            Interrupt();
            meetingActive = false;
            seats.Clear();
            handoffReviews.Clear();
            foreach (var actor in characters) { actor.movement.UnlockMeetingSeat(); actor.movement.RoamingEnabled = true; actor.SetCue("idle"); }
            UsesArrivalSeats = false;
            status.text = "Exploring the room. Tap the microphone to meet again.";
        }

        public void RequestHandoffReview(WindupCharacter reviewer, MeetingLine source)
        {
            if (!UsesArrivalSeats || !meetingActive || reviewer == null || source == null ||
                string.IsNullOrWhiteSpace(source.id) || string.IsNullOrWhiteSpace(source.body) ||
                reviewer.characterName == source.speaker) return;
            bool Same((WindupCharacter reviewer, MeetingLine source) job) =>
                job.reviewer == reviewer && job.source.id == source.id;
            if ((activeReview.HasValue && Same(activeReview.Value)) || handoffReviews.Any(Same)) return;
            handoffReviews.Enqueue((reviewer, source));
        }

        private void Interrupt()
        {
            if (activeReview.HasValue) activeReview.Value.reviewer.SetCue("idle");
            activeReview = null;
            presentingReview = false;
            generation++;
            activeRequest?.Abort();
            activeRequest = null;
            Busy = false;
            if (Speaker != null) { Speaker.Finish(); Speaker = null; }
        }

        private IEnumerator RequestTurn(string trigger, string input, int version,
            WindupCharacter reviewer = null, MeetingLine source = null)
        {
            Busy = true;
            if (reviewer != null)
            {
                Hint(reviewer.characterName + " is reviewing the idea…");
                reviewer.SetCue("thinking");
            }
            else
            {
                Hint("Thinking together…");
                // Not everyone has to look busy: only a character addressed by name ("Benefit, …")
                // shows the thinking pill; otherwise the team shares one small indicator.
                string asked = input?.TrimStart() ?? "";
                var addressed = characters.FirstOrDefault(a => asked.StartsWith(a.characterName + ",", StringComparison.OrdinalIgnoreCase));
                if (addressed != null) addressed.SetCue("thinking");
            }
            if (LocalDemoEnabled)
            {
                yield return new WaitForSeconds(0.5f);
                if (version != generation) yield break;
                Busy = false;
                if (reviewer != null)
                {
                    activeReview = null;
                    PresentReview(reviewer, source, new MeetingLine
                    {
                        id = Guid.NewGuid().ToString("N"),
                        speaker = reviewer.characterName,
                        summary = "Scripted card review",
                        body = "Let's test one part of this idea before taking it further.",
                        replyTo = source.id, relation = "build", cue = "propose"
                    });
                }
                else ApplyResponse(WindupsDemo.Create(trigger, ClarificationCount, History.LastOrDefault()?.id ?? ""));
                yield break;
            }
            var request = new MeetingRequest { requestId = Guid.NewGuid().ToString("N"), sessionId = sessionId,
                trigger = trigger, input = input, clarificationCount = ClarificationCount,
                reviewer = reviewer != null ? reviewer.characterName : null, reviewOf = source,
                history = History.Skip(Math.Max(0, History.Count - 12)).ToList() };
            bool direct = directOpenAI;
            string body = JsonUtility.ToJson(request);
            string credential = gatewayToken;
            string setupError = null;
            if (direct)
            {
                if (!WindupsDirectCredentials.TryGetKey(out credential)) setupError = "AI access is not configured in this test build.";
                else try { body = WindupsDirectMeeting.CreateRequest(request); }
                catch (Exception) { setupError = "The topic or conversation is too long. Please shorten it."; }
            }
            if (setupError != null)
            {
                Busy = false;
                activeReview = null;
                ReportError(setupError);
                foreach (var actor in characters) actor.SetCue("idle");
                yield break;
            }
            using (var http = new UnityWebRequest(direct ? WindupsDirectMeeting.Endpoint : serverUrl.TrimEnd('/') + "/v1/turns", "POST"))
            {
                http.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                http.downloadHandler = new DownloadHandlerBuffer();
                http.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(credential)) http.SetRequestHeader("Authorization", "Bearer " + credential);
                http.timeout = 25;
                activeRequest = http;
                yield return http.SendWebRequest();
                if (version != generation) yield break;
                activeRequest = null;
                Busy = false;
                if (http.result != UnityWebRequest.Result.Success)
                {
                    activeReview = null;
                    ReportError(http.responseCode == 401 ? "AI access could not be verified."
                        : http.responseCode == 429 ? "AI service limit reached. Please try again later."
                        : "Could not connect. Check your internet connection and try again.");
                    foreach (var actor in characters) actor.SetCue("idle");
                    yield break;
                }
                MeetingResponse response = null;
                try { response = direct ? WindupsDirectMeeting.ReadResponse(http.downloadHandler.text, request) : JsonUtility.FromJson<MeetingResponse>(http.downloadHandler.text); }
                catch (Exception) { ReportError("The response could not be read. Please try again."); }
                if (response == null || response.utterances == null)
                {
                    activeReview = null;
                    if (reviewer != null) reviewer.SetCue("idle");
                    yield break;
                }
                if (reviewer == null)
                {
                    ApplyResponse(response);
                    yield break;
                }
                if (!WindupsDirectMeeting.IsReviewResponse(response, request))
                {
                    activeReview = null;
                    reviewer.SetCue("idle");
                    ReportError("The review did not match this card. Hand it over again to retry.");
                    yield break;
                }
                while (UserHolding)
                {
                    Busy = true;
                    yield return null;
                    if (version != generation) yield break;
                }
                activeReview = null;
                Busy = false;
                    PresentReview(reviewer, source, response.utterances[0]);
            }
        }

        private void PresentReview(WindupCharacter reviewer, MeetingLine source, MeetingLine line)
        {
            Speaker = reviewer;
            presentingReview = true;
            reviewer.Present(line);
            if (!History.Any(entry => entry.id == source.id)) History.Add(source);
            History.Add(line);
            PresentedCount++;
            finishAt = Time.time + Mathf.Clamp(2 + line.body.Split(' ').Length / 3f, 4, 12);
            Hint(reviewer.characterName + " · Card review");
        }

        public void ApplyResponse(MeetingResponse response)
        {
            foreach (var actor in characters) actor.SetCue("idle");
            if (UsesArrivalSeats && (response.clarification || wrappingUp)) ClearChoices();
            foreach (var line in response.utterances)
            {
                if (UsesArrivalSeats && (response.clarification || wrappingUp) &&
                    line.speaker != "Decisions" && line.relation != "idea") continue;
                var actor = characters.FirstOrDefault(c => c.characterName == line.speaker);
                if (actor != null) actor.Enqueue(line);
            }
            waitingForAnswer = response.clarification;
            if (waitingForAnswer || wrappingUp)
            {
                if (waitingForAnswer) ClarificationCount++;
                var decisions = characters.First(c => c.characterName == "Decisions");
                // Clarifications must be presented immediately even with older opinions waiting.
                var question = response.utterances.FirstOrDefault(l => l.speaker == "Decisions");
                if (question != null)
                {
                    var remaining = decisions.Pending.Where(l => l.id != question.id).ToArray();
                    decisions.Pending.Clear();
                    decisions.Pending.Enqueue(question);
                    foreach (var line in remaining) decisions.Pending.Enqueue(line);
                    Select(decisions, false);
                    if (wrappingUp) automaticTurns = 2;
                }
            }
            choosingDirection = UsesArrivalSeats && wrappingUp;
            foreach (var actor in characters) actor.Refresh();
            Hint(waitingForAnswer ? "Choose a direction or hold to answer." : wrappingUp ? "Choose a direction or say your next step." : "Pick an idea, or hold to talk.");
            nextTurnAt = Time.time + 10;
        }

        public string OptionLabel(WindupCharacter actor)
        {
            if (waitingForAnswer && actor.characterName == "Decisions") return "Not sure yet";
            if (actor.Pending.Count == 0) return null;
            return waitingForAnswer || choosingDirection ? "Choose" : "Read";
        }

        private void ClearChoices()
        {
            foreach (var actor in characters)
            {
                actor.Pending.Clear();
                actor.Finish();
            }
        }

        private void RecordDirection(string direction)
        {
            Interrupt();
            choosingDirection = wrappingUp = waitingForAnswer = meetingActive = false;
            handoffReviews.Clear();
            SelectedDirection = direction;
            History.Add(new MeetingLine { id = Guid.NewGuid().ToString("N"), speaker = "User", body = direction, summary = direction, relation = "decision" });
            ClearChoices();
            characters.First(actor => actor.characterName == "Decisions").Present(new MeetingLine
            {
                summary = "Direction chosen", body = direction, cue = "agree"
            });
            Hint("Direction chosen");
        }

        public void Select(WindupCharacter actor, bool userSelected)
        {
            if (IsListening || (UsesArrivalSeats && Busy)) return;
            if (UsesArrivalSeats && userSelected && waitingForAnswer)
            {
                if (actor.characterName == "Decisions") Submit("Not sure yet. Explore with tentative assumptions.");
                else if (actor.Pending.Count > 0) Submit(actor.Pending.Peek().body);
                return;
            }
            if (userSelected && choosingDirection)
            {
                if (actor.Pending.Count > 0) RecordDirection(actor.Pending.Peek().body);
                return;
            }
            if (actor.Pending.Count == 0) return;
            Interrupt();
            if (userSelected) automaticTurns = 0;
            else if (!waitingForAnswer) automaticTurns++;
            Speaker = actor;
            var line = actor.Pending.Dequeue();
            actor.Present(line);
            WindupBody.Express(actor, line.body, 6f);
            History.Add(line);
            PresentedCount++;
            finishAt = Time.time + Mathf.Clamp(2 + line.body.Split(' ').Length / 3f, 4, 12);
            Hint(actor.characterName + " is speaking");
        }

        private void Update()
        {
            if (!uiPlaced && Head != null && (Application.isEditor ? environment.IsReady : HasTrackedHead()))
            {
#if UNITY_EDITOR
                if (!UnityEngine.XR.XRSettings.isDeviceActive && environment.IsReady)
                {
                    float floor = environment.Surfaces.First(s => s.IsFloor).Origin.y;
                    var rig = FindFirstObjectByType<OVRCameraRig>();
                    rig.transform.position += Vector3.up * (floor + 1.6f - Head.position.y);
                }
#endif
                transform.position = Head.position - Vector3.up * 1.6f;
                transform.rotation = Quaternion.Euler(0, Head.eulerAngles.y, 0);
                uiPlaced = true;
                Debug.Log("[Windups] UI placed head="+Head.position+" root="+transform.position);
            }
            if (Time.unscaledTime >= diagnosticAt)
            {
                diagnosticAt=Time.unscaledTime+5;
                Debug.Log("[Windups] room="+environment.Status+" ready="+environment.IsReady+" head="+(Head==null?"missing":Head.position.ToString())+" ui="+uiPlaced);
                if (!environment.IsReady && !meetingActive) status.text="Room: "+environment.Status+"\nMicrophone controls are available while room loading completes.";
            }
            if (roomRevision != environment.Revision) { roomRevision = environment.Revision; seats.Clear(); }
            if (!meetingActive) return;
            foreach (var actor in characters) actor.movement.RoamingEnabled = false;
            if (!UsesArrivalSeats && Time.time >= placementAt) { PlaceMeeting(); placementAt = Time.time + 2; }
            if (IsListening || Busy || (arrivalMicrophone != null && arrivalMicrophone.IsVoiceBusy) ||
                characters.Any(c => c.movement.IsHeld || (c.authoredBubble != null && c.authoredBubble.IsHeld)))
            {
                nextTurnAt = Time.time + 10;
                finishAt += Time.deltaTime;
                return;
            }
            if (choosingDirection) return;
            if (!waitingForAnswer && !UserHolding && handoffReviews.Count > 0 && !presentingReview)
            {
                var review = handoffReviews.Dequeue();
                Interrupt();
                activeReview = review;
                StartCoroutine(RequestTurn("review", "Review the handed-over idea.", generation, review.reviewer, review.source));
                return;
            }
            if (Speaker != null && !waitingForAnswer && Time.time >= finishAt)
            {
                Speaker.Finish(); Speaker = null;
                Hint("Pick an idea, or say ‘wrap up’.");
                nextTurnAt = Time.time + 10;
                if (!presentingReview && automaticTurns < 2)
                    StartCoroutine(RequestTurn("reaction", "Respond to the most recently presented idea if you have something new to add.", generation));
                presentingReview = false;
                return;
            }
            if (Speaker != null || waitingForAnswer || Time.time < nextTurnAt) return;
            if (automaticTurns >= 2)
            {
                const string turnHint = "Your turn. Speak, say ‘continue’ or ‘wrap up’.";
                if (status.text != turnHint) Hint(turnHint);
                return;
            }
            var next = characters.Where(c => c.Pending.Count > 0)
                .OrderBy(c => History.Count(l => l.speaker == c.characterName)).FirstOrDefault();
            if (next != null)
            {
                WindupBody.Of(next)?.Play("HandRaise");
                Select(next, false);
            }
            nextTurnAt = Time.time + 10;
        }

        private void PlaceMeeting()
        {
            if (!environment.IsReady || Head == null) return;
            foreach (var actor in characters)
            {
                if (actor.movement.IsHeld) { seats.Remove(actor); continue; }
                if (!seats.TryGetValue(actor, out var seat))
                {
                    var samples = new List<Vector3>();
                    foreach (var surface in environment.Surfaces)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            Vector3 guess = Head.position + Head.forward * 0.75f + Head.right * UnityEngine.Random.Range(-0.6f, 0.6f);
                            var p = new Vector2(guess.x, guess.z);
                            if (!surface.Contains(p, 0.06f)) continue;
                            if (environment.TrySameLevelNavPoint(surface.Point(p), out var candidate) &&
                                seats.Values.All(s => Vector3.Distance(s, candidate) > 0.40f)) samples.Add(candidate);
                        }
                    }
                    foreach (var candidate in samples.OrderBy(p => Vector3.Distance(p, Head.position)))
                        if (actor.movement.GoTo(candidate)) { seats[actor] = candidate; break; }
                }
                else if (Vector3.Distance(actor.transform.position, seat) > 0.10f)
                {
                    if (!actor.movement.GoTo(seat) && actor.movement.State == CapsulePetController.MotionState.Idle) seats.Remove(actor);
                }
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) uiPlaced=false;
            if (paused) { Interrupt(); IsListening = false; }
            nextTurnAt = Time.time + 10;
        }
        private static bool HasTrackedHead()
        {
            var device=UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
            return device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked,out bool tracked)&&tracked;
        }
        private void OnDestroy() { activeRequest?.Abort(); }
    }
}
