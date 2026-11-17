using System;
using System.Collections.Generic;
using Oculus.Interaction.Input;
using RoomCapsule;
using UnityEngine;

namespace Windups
{
    // The modelled Windups character (Assets/Art/WindupsCharacter, one FBX for all six; only the face
    // sheet and the body colour differ, Benefit also carries the basket). It replaces the capsule look:
    //  - clips follow what the character does: walking/climbing Run_Loop (Run_Stop on arrival), held
    //    Grab_Struggle_Loop, falling Fall_Loop (Land on landing), thinking Think_Loop (Idea_Pop when the
    //    answer comes), talking Talk_Loop, the user talking Listen_Loop (HeadTilt first), else Idle_Loop;
    //  - sessions add one-shots (Play: Receive, Cheer, HandRaise, Wave, Touch_Jump...) and working
    //    loops (Work: Work_Loop, Creativity_Make_Loop; Work_Done when it ends);
    //  - the expression comes from the Face bone's scale (round(scale.x) - 1 -> a cell of the 3x3 sheet);
    //  - the head bone turns toward what the character looks at; the body turns with the old visual.
    // Clips are in place (root motion off) and cross-fade in 0.15 s. The capsule mesh stays for bounds.
    [DefaultExecutionOrder(200)]
    public sealed partial class WindupBody : MonoBehaviour
    {
        public const float Fade = .15f;
        public static readonly List<WindupBody> All = new List<WindupBody>();
        // The person is talking (push-to-talk held/recording): everyone listens.
        public static bool UserTalking;

        public WindupCharacter character;
        public Animator animator;
        public Renderer head;
        public Transform headBone, faceBone, basketBone;
        public GameObject basket;
        public bool hasBasket;
        // Rest pose, measured when the prefab is built: the head's top in the head bone's space and the
        // basket's centre in the basket bone's space.
        public Vector3 headTopLocal, basketLocal;

        public string Current { get; private set; } = "Idle_Loop";
        public string OneShot { get; private set; }
        public int Face { get; private set; }
        public string Work { get; private set; }
        public Vector3 HeadTop => headBone != null ? headBone.TransformPoint(headTopLocal) : transform.position + Vector3.up * .156f;
        public Vector3 HeadUp => headBone != null ? (headBone.TransformPoint(headTopLocal) - headBone.position).normalized : Vector3.up;
        public Vector3 BasketPoint => basketBone != null ? basketBone.TransformPoint(basketLocal) : transform.position;
        public int Plays { get; private set; }

        private readonly Dictionary<string, float> lengths = new Dictionary<string, float>();
        private Material faceMaterial;
        private float oneShotEnds;
        private string lastCue = "idle";
        private MotionStateCache last;
        private bool wasTalkingUser, wasThinking;
        private Quaternion look = Quaternion.identity;
        private float lookWeight;
        private Vector3? lookOverride;
        // (2026-11-13, the user: "they keep looking down then up - what pose is that?") Look targets jump between the
        // person (up), a speaking teammate (level) and things on the desk (down); the head snapped to each. The target
        // point now glides (LookTargetTime), and LookSource says which rule chose it (checks log it).
        public static bool SmoothLookTargets = true;
        public static float LookTargetTime = .35f;
        public string LookSource { get; private set; } = "";
        private Vector3 lookPoint, lookPointVelocity;
        private bool hasLookPoint;
        private float lookOverrideUntil, touchCooldown;
        private string afterOneShot;
        // Each character keeps its own pace and its own idle phase, so six of them never move in step.
        private float pace = 1f, nextFidgetAt;
        private Quaternion facing;
        private Vector3 lastRoot;
        private float travelSpeed;
        public const float WalkSpeed = .2f; // CapsulePetController.walkingSpeed: Run_Loop plays at 1x at this speed

        private struct MotionStateCache { public bool walking, climbing, falling, held; }
        private CapsulePetController.TraversalPhase lastPhase;
        private bool softLanding, wasJumping, wasHopping;

        public static WindupBody Of(WindupCharacter character) => character != null ? character.GetComponentInChildren<WindupBody>(true) : null;
        public static WindupBody Near(Transform t) => t != null ? t.GetComponentInChildren<WindupBody>(true) ?? t.GetComponentInParent<WindupBody>() : null;

        private void Awake()
        {
            if (character == null) character = GetComponentInParent<WindupCharacter>();
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            animator.applyRootMotion = false;
            // F79 (Quest 2026-11-14: "after I look away for a while their heads are twisted"): with the model's
            // CullUpdateTransforms the animator stops writing the bones while the character is off screen, but the
            // head turn, holds and squash below are applied on top of the bones every LateUpdate, so they piled up
            // frame after frame. Always animating keeps one fresh clip pose under them (three small rigs: cheap).
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (animator.runtimeAnimatorController != null)
                foreach (var clip in animator.runtimeAnimatorController.animationClips) lengths[clip.name] = clip.length;
            if (head != null) faceMaterial = head.material;
            // No shadows on or from the characters (they read as toys in passthrough, not lit models).
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            if (basket != null) basket.SetActive(hasBasket);
            pace = UnityEngine.Random.Range(.92f, 1.08f);
            nextFidgetAt = Time.time + UnityEngine.Random.Range(6f, 14f);
            facing = transform.rotation;
            FaceAxisFromRest();
            MeasureHeadShape();
            // Rest pose: the top of the head and the basket's centre, kept in their bones' space.
            if (headBone != null && head != null && headTopLocal == Vector3.zero)
            {
                var b = head.bounds;
                headTopLocal = headBone.InverseTransformPoint(new Vector3(b.center.x, b.max.y, b.center.z));
            }
            if (basketBone != null && basket != null && basketLocal == Vector3.zero)
            {
                var r = basket.GetComponent<Renderer>();
                basketLocal = basketBone.InverseTransformPoint(r != null ? r.bounds.center : basketBone.position);
            }
        }

        private void Start()
        {
            // Start the idle somewhere in its cycle: no two characters breathe or blink together.
            if (animator != null) animator.Play("Idle_Loop", 0, UnityEngine.Random.value);
            lastRoot = transform.parent != null ? transform.parent.position : transform.position;
        }

        private void OnEnable()
        {
            All.Add(this);
            // The capsule keeps its collider and bounds (hats, signs, stacks measure it) but is not drawn.
            if (character != null && character.visual != null && character.visual.TryGetComponent<MeshRenderer>(out var capsule)) capsule.forceRenderingOff = true;
        }

        private void OnDisable() => All.Remove(this);
        private void OnDestroy() { if (faceMaterial != null) Destroy(faceMaterial); }

        // ------------------------------------------------------------ what to play

        public float Play(string clip, string then = null)
        {
            if (string.IsNullOrEmpty(clip) || animator == null) return 0f;
            if (clip != "Land") softLanding = false;
            OneShot = clip;
            afterOneShot = then;
            float length = Length(clip);
            oneShotEnds = Time.time + Mathf.Max(.2f, length - Fade * .5f);
            CrossFade(clip, true);
            Plays++;
            return length;
        }

        public static void PlayAll(string clip) { foreach (var body in All) body.Play(clip); }

        // A working loop (Work_Loop, Creativity_Make_Loop) until StopWork; done = finish with Work_Done.
        public void StartWork(string loop = "Work_Loop") { Work = loop; }
        public void StopWork(bool done)
        {
            ClearPosture();
            if (Work == null) return;
            Work = null;
            if (done) Play("Work_Done");
        }

        // The head turns to a point for a while (pointing, showing an object).
        public void LookAtPoint(Vector3 point, float seconds) { lookOverride = point; lookOverrideUntil = Time.time + seconds; }

        public float Length(string clip) => lengths.TryGetValue(clip, out float l) ? l : 1f;

        // ------------------------------------------------------------ footsteps

        // Pitter-patter while running (2026-11-11): two taps per run cycle, in time with the animation, each
        // character with its own tap, quiet (they are small).
        public const float StepVolume = .2f;
        public int Footsteps { get; private set; }
        private AudioSource feet;
        private float lastStride = -1f;

        private static ToySounds.StepVoice StepVoiceOf(string role) => role switch
        {
            "Creativity" => ToySounds.StepVoice.Tik,   // Spark: light and bright
            "Decisions" => ToySounds.StepVoice.Tok,    // Maker: woody, steady
            "Caution" => ToySounds.StepVoice.Tup,      // Fixer: soft and low
            "Emotions" => ToySounds.StepVoice.Pip,
            "Benefit" => ToySounds.StepVoice.Pat,
            _ => ToySounds.StepVoice.Tap
        };

        private void StepSounds(bool walking)
        {
            if (!walking || Current != "Run_Loop" || animator.IsInTransition(0)) { lastStride = -1f; return; }
            float stride = animator.GetCurrentAnimatorStateInfo(0).normalizedTime * 2f;
            if (lastStride >= 0f && Mathf.Floor(stride) != Mathf.Floor(lastStride)) Footstep((int)Mathf.Floor(stride));
            lastStride = stride;
        }

        private void Footstep(int foot)
        {
            if (ToySounds.Muted) return; // F55: Sounds off in the settings box
            if (feet == null)
            {
                feet = gameObject.AddComponent<AudioSource>();
                feet.playOnAwake = false;
                feet.spatialBlend = 1f;
                feet.rolloffMode = AudioRolloffMode.Logarithmic;
                feet.minDistance = .25f;
                feet.maxDistance = 3f;
                feet.dopplerLevel = 0f;
            }
            feet.pitch = UnityEngine.Random.Range(.95f, 1.05f);
            feet.PlayOneShot(ToySounds.Step(StepVoiceOf(Role), foot), StepVolume);
            Footsteps++;
        }

        // ------------------------------------------------------------ personality (by role)

        public string Role => character != null ? character.characterName : "";

        // Hello after gathering.
        public static string Greeting(string role) => role switch
        {
            "Creativity" => "Cheer",          // Spark / Dreamer: excited
            "Caution" => "React_Nod",         // Fixer / Critic: a dry nod
            "Emotions" => "Cheer",
            "Benefit" => "HandRaise",
            _ => "Wave"                        // Information, Decisions (the guides)
        };

        // When it is introduced.
        public static string Introduction(string role) => role switch
        {
            "Creativity" => "Idea_Pop",
            "Caution" => "React_Nod",
            "Decisions" => "Decision_Present",
            "Emotions" => "Cheer",
            "Benefit" => "HandRaise",
            _ => "Touch_Jump"
        };

        // Small habits while idle (now and then, each on its own clock).
        private static string[] Fidgets(string role) => role switch
        {
            "Creativity" => new[] { "Peek", "HeadTilt", "Idea_Pop" },
            "Caution" => new[] { "HeadTilt", "React_Shake" },
            "Decisions" => new[] { "React_Nod", "HeadTilt" },
            "Emotions" => new[] { "HeadTilt", "React_Nod" },
            "Benefit" => new[] { "HeadTilt", "Peek" },
            _ => new[] { "HeadTilt", "React_Nod" }
        };

        // Plays `clip` after a short random delay (so a group never moves in unison).
        public void PlaySoon(string clip, float maxDelay = .6f) => StartCoroutine(PlayLater(clip, UnityEngine.Random.Range(0f, maxDelay)));
        private System.Collections.IEnumerator PlayLater(string clip, float delay)
        {
            yield return new WaitForSeconds(delay);
            Play(clip);
        }

        // Everyone reacts to the same moment (a shh, a pointing finger), each at its own time and, where it
        // makes sense, in its own way: `clips[i]` for the i-th character in a shuffled order (null = only
        // look), starting `spread` seconds apart on average. Never all on one frame.
        public static void ReactAll(string[] clips, float spread, Vector3? lookAt = null, float lookSeconds = 0f)
        {
            var group = All.FindAll(b => b.isActiveAndEnabled);
            for (int i = group.Count - 1; i > 0; i--) { int j = UnityEngine.Random.Range(0, i + 1); (group[i], group[j]) = (group[j], group[i]); }
            float at = UnityEngine.Random.Range(0f, spread * .5f);
            for (int k = 0; k < group.Count; k++)
            {
                group[k].StartCoroutine(group[k].ReactLater(clips.Length > 0 ? clips[k % clips.Length] : null, at, lookAt, lookSeconds));
                at += UnityEngine.Random.Range(spread * .6f, spread * 1.4f);
            }
        }

        private System.Collections.IEnumerator ReactLater(string clip, float delay, Vector3? lookAt, float lookSeconds)
        {
            // The eyes go first, the body follows.
            if (lookAt.HasValue) { yield return new WaitForSeconds(delay * .5f); LookAtPoint(lookAt.Value, lookSeconds); }
            yield return new WaitForSeconds(lookAt.HasValue ? delay * .5f : delay);
            if (clip != null) { Play(clip); Log(this, clip + " (group)"); }
        }

        private void Update()
        {
            if (animator == null || character == null) return;
            var pet = character.movement;
            var state = pet != null ? pet.State : CapsulePetController.MotionState.Idle;
            var now = new MotionStateCache
            {
                // F51: standing on the person's palm is held for the session, but the body stands at ease.
                held = pet != null && (pet.IsHeld || state == CapsulePetController.MotionState.Held) && !pet.OnPalm,
                falling = state == CapsulePetController.MotionState.Falling,
                walking = state == CapsulePetController.MotionState.Walking,
                climbing = state == CapsulePetController.MotionState.Climbing
            };
            string cue = character.Cue ?? "idle";
            bool thinking = cue == "thinking";
            bool talking = cue == "speak" || cue == "propose" || character.IsPresenting ||
                           (character.meeting != null && character.meeting.Speaker == character) || StudioVoice.IsSpeakingFrom(character.transform);
            bool listening = UserTalking && !talking;

            // The group's listening is directed once per frame (who reacts how, and when).
            DirectListening();
            var phase = pet != null && now.climbing ? pet.Phase : CapsulePetController.TraversalPhase.None;
            bool jumping = pet != null && pet.Jumping;
            bool hopping = pet != null && pet.Hopping && now.falling;
            // Moments that start a one-shot by themselves. A jump or a hop lands with Land (no wince);
            // a walk ends with Run_Stop.
            if (jumping && phase == CapsulePetController.TraversalPhase.Land && lastPhase != CapsulePetController.TraversalPhase.Land) { softLanding = true; Play("Land"); }
            else if ((last.walking || last.climbing) && !now.walking && !now.climbing && !now.falling && !now.held && !wasJumping) Play("Run_Stop");
            if (last.falling && !now.falling && !now.held) { softLanding = wasHopping; Play("Land"); if (wasHopping) Squash(.85f, .3f); }
            if (wasThinking && !thinking && (talking || cue == "propose")) Play("Idea_Pop");
            // A clip a session asked for wins over the nod/shake a cue change would start.
            if (cue != lastCue && OneShot == null)
            {
                if (cue == "agree" || cue == "encourage") Play("React_Nod");
                else if (cue == "challenge") Play("React_Shake");
            }
            last = now;
            lastCue = cue;
            wasThinking = thinking;
            wasTalkingUser = UserTalking;
            wasJumping = jumping;
            wasHopping = hopping || (wasHopping && now.falling);
            lastPhase = phase;

            // Body states that override everything: held, falling, running, jumping.
            string loop = now.held ? "Grab_Struggle_Loop"
                : hopping ? "Touch_Jump"
                : now.falling ? "Fall_Loop"
                : jumping ? (phase == CapsulePetController.TraversalPhase.Air ? "Touch_Jump" : phase == CapsulePetController.TraversalPhase.Land ? Current : "Idle_Loop")
                : now.climbing ? (pet != null && pet.ActiveTraversal != null && pet.ActiveTraversal.Kind == CapsuleTraversalKind.Drop
                    ? (phase == CapsulePetController.TraversalPhase.Crouch ? "Idle_Loop" : "Run_Loop") : "Float_Loop")
                : now.walking ? "Run_Loop"
                : IntroActive ? "Idle_Loop" : Work ?? (thinking ? "Think_Loop" : talking ? "Talk_Loop" : listening ? ListenClip : "Idle_Loop");
            // The jump's landing keeps its Land one-shot.
            bool hard = now.held || now.falling || now.walking || (now.climbing && phase != CapsulePetController.TraversalPhase.Land);
            // The run cycle matches how fast it really goes (no sliding feet); everything else at its own pace.
            animator.speed = Current == "Run_Loop" ? pace * Mathf.Clamp(travelSpeed / WalkSpeed, .6f, 1.6f) : pace;
            StepSounds(now.walking);
            if (!IntroActive && !hard && OneShot == null && loop == "Idle_Loop" && Time.time > nextFidgetAt)
            {
                var habits = Fidgets(Role);
                Play(habits[UnityEngine.Random.Range(0, habits.Length)]);
                nextFidgetAt = Time.time + UnityEngine.Random.Range(7f, 15f);
            }
            else if (loop != "Idle_Loop" || OneShot != null) nextFidgetAt = Mathf.Max(nextFidgetAt, Time.time + 4f);
            if (OneShot != null && (hard || Time.time >= oneShotEnds))
            {
                string then = afterOneShot;
                OneShot = null;
                afterOneShot = null;
                if (!hard && !string.IsNullOrEmpty(then)) { Play(then); return; }
            }
            if (OneShot == null && Current != loop) CrossFade(loop, false);
            if (!IntroActive) { Touch(); NoticeGaze(); }
        }

        // A loop starts somewhere in its cycle (2026-11-11): characters that start listening, walking or
        // thinking at the same moment never move in step. One-shots start at their beginning.
        private void CrossFade(string clip, bool restart)
        {
            if (!restart && Current == clip) return;
            Current = clip;
            float offset = !restart && clip.EndsWith("_Loop") ? UnityEngine.Random.value * Length(clip) : 0f;
            animator.CrossFadeInFixedTime(clip, Fade, 0, offset);
        }

        // ------------------------------------------------------------ listening as a group (2026-11-11)
        // When the person starts talking, everyone used to tilt the same way on the same frame and then hold
        // Listen_Loop's tilt in step. Now each character gets its own reaction from its role's habits (no two
        // the same, at most one head tilt), at its own moment; only one listens with Listen_Loop, the others
        // stand and look; and they look at slightly different things. While the person keeps talking, now and
        // then ONE of them nods.
        public enum Gaze { Face, FaceSide, Hands }
        public string ListenReaction { get; private set; }
        public float ListenReactionAt { get; private set; } = -1f;
        public Gaze ListenGaze { get; private set; }
        public string ListenClip { get; private set; } = "Idle_Loop";
        public static int ListenTurns { get; private set; }
        public static readonly List<string> ReactionLog = new List<string>(); // "time role clip"
        private static int directedFrame = -1;
        private static bool directedTalking;
        private static float nextSmallReaction;
        private static WindupBody lastSmallReactor;

        private static string[] ListenHabits(string role) => role switch
        {
            "Creativity" => new[] { "Peek", "React_Nod", "HeadTilt", null },   // leans in, eager
            "Decisions" => new[] { "React_Nod", null, "HeadTilt" },          // a steady nod
            "Caution" => new[] { null, "React_Nod", "HeadTilt" },            // just looks; nods later
            "Emotions" => new[] { "HeadTilt", "React_Nod", null },
            "Benefit" => new[] { "React_Nod", "Peek", null },
            _ => new[] { "React_Nod", "HeadTilt", null }
        };

        private bool Available => isActiveAndEnabled && character != null && character.movement != null && !character.movement.IsHeld &&
                                  character.movement.State != CapsulePetController.MotionState.Walking &&
                                  character.movement.State != CapsulePetController.MotionState.Climbing &&
                                  character.movement.State != CapsulePetController.MotionState.Falling;

        private static void DirectListening()
        {
            if (directedFrame == Time.frameCount) return;
            directedFrame = Time.frameCount;
            if (UserTalking && !directedTalking) AssignListening();
            directedTalking = UserTalking;
            if (!UserTalking || Time.time < nextSmallReaction) return;
            nextSmallReaction = Time.time + UnityEngine.Random.Range(2.6f, 5.2f);
            // One small nod from someone free who did not just react.
            var free = All.FindAll(b => b.Available && b.OneShot == null && b.Work == null && b != lastSmallReactor);
            if (free.Count == 0) return;
            var who = free[UnityEngine.Random.Range(0, free.Count)];
            lastSmallReactor = who;
            float delay = UnityEngine.Random.Range(0f, .3f);
            who.StartCoroutine(who.PlayLater("React_Nod", delay));
            Log(who, "React_Nod (small)", delay);
        }

        private static void AssignListening()
        {
            ListenTurns++;
            var group = All.FindAll(b => b.isActiveAndEnabled && b.character != null);
            for (int i = group.Count - 1; i > 0; i--) { int j = UnityEngine.Random.Range(0, i + 1); (group[i], group[j]) = (group[j], group[i]); }
            var used = new HashSet<string>();
            float at = UnityEngine.Random.Range(.1f, .35f);
            int listener = -1;
            var gazes = new List<Gaze> { Gaze.Face, Gaze.FaceSide, Gaze.Hands };
            for (int k = 0; k < group.Count; k++)
            {
                var b = group[k];
                b.ListenGaze = gazes[k % gazes.Count];
                b.ListenClip = "Idle_Loop";
                b.ListenReaction = null;
                if (!b.Available) continue;
                if (listener < 0 && b.Work == null) { listener = k; b.ListenClip = "Listen_Loop"; }
                if (b.Work != null) continue; // working hands keep working; the head still turns
                string pick = null;
                foreach (var habit in ListenHabits(b.Role))
                {
                    if (habit != null && used.Contains(habit)) continue;
                    // Mostly the first habit that is free, sometimes the next one (the same person is not
                    // always the one who nods first).
                    if (habit != null && UnityEngine.Random.value < .25f) continue;
                    pick = habit;
                    break;
                }
                if (pick != null) used.Add(pick);
                b.ListenReaction = pick;
                b.ListenReactionAt = Time.time + at;
                if (pick != null) { b.StartCoroutine(b.PlayLater(pick, at)); Log(b, pick, at); }
                at += UnityEngine.Random.Range(.2f, .45f);
            }
            nextSmallReaction = Time.time + UnityEngine.Random.Range(2.6f, 4f);
        }

        // Logged with the time the clip starts (a reaction scheduled `delay` seconds from now).
        private static void Log(WindupBody b, string what, float delay = 0f)
        {
            ReactionLog.Add((Time.time + delay).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " " + b.Role + " " + what);
            if (ReactionLog.Count > 200) ReactionLog.RemoveAt(0);
        }

        // ------------------------------------------------------------ squash and stretch (jumps)
        private Vector3 modelScale = Vector3.one;
        private bool hasModelScale;
        private float squashFrom = -10f, squashLength = .3f, squashDepth = .85f;
        // depth < 1 squashes (a landing), > 1 stretches (a take-off); either eases back to 1 over `seconds`.
        public void Squash(float depth, float seconds) { squashFrom = Time.time; squashDepth = depth; squashLength = seconds; }

        // A jump it meant to make (a hop driven by a session, e.g. onto the title plate) lands with Land's
        // body and a happy face (Land's wince is for being dropped) and a squash.
        public void LandSoftly(float depth = .82f)
        {
            Play("Land");
            softLanding = true;
            Squash(depth, .3f);
        }

        private void SquashAndStretch()
        {
            if (animator == null) return;
            var model = animator.transform;
            if (!hasModelScale) { modelScale = model.localScale; hasModelScale = true; }
            var pet = character.movement;
            float y = 1f;
            if (pet != null && pet.State == CapsulePetController.MotionState.Climbing && pet.Phase == CapsulePetController.TraversalPhase.Crouch)
                y = Mathf.Lerp(1f, .8f, Mathf.SmoothStep(0f, 1f, pet.PhaseTime / CapsulePetController.CrouchTime));
            else if (pet != null && pet.Jumping && pet.Phase == CapsulePetController.TraversalPhase.Air)
                y = Mathf.Lerp(1.1f, 1f, Mathf.Clamp01(pet.PhaseTime / .25f));
            else if (pet != null && pet.Jumping && pet.Phase == CapsulePetController.TraversalPhase.Land)
                y = Mathf.Lerp(.82f, 1f, Mathf.SmoothStep(0f, 1f, pet.PhaseTime / (CapsulePetController.LandTime + .05f)));
            else if (Time.time - squashFrom < squashLength)
                y = Mathf.Lerp(squashDepth, 1f, Mathf.SmoothStep(0f, 1f, (Time.time - squashFrom) / squashLength));
            if (IntroActive) y = 1f; // Intro motion preserves the authored toy proportions.
            float xz = 1f / Mathf.Sqrt(y); // keeps the volume
            model.localScale = new Vector3(modelScale.x * xz, modelScale.y * y, modelScale.z * xz);
        }

        // A fingertip touching the character (not grabbing): a little jump.
        private void Touch()
        {
            if (Time.time < touchCooldown || character.movement == null || character.movement.IsHeld) return;
            Vector3 centre = transform.position + Vector3.up * .08f;
            foreach (var hand in Handoff.HandSet.Both())
            {
                if (hand == null || !hand.isActiveAndEnabled || !hand.IsConnected || hand.GetIndexFingerIsPinching()) continue;
                if (!hand.GetJointPose(HandJointId.HandIndexTip, out Pose tip)) continue;
                if (Vector3.Distance(tip.position, centre) > .06f) continue;
                touchCooldown = Time.time + 1.6f;
                Play("Touch_Jump");
                TouchedAt = Time.time;
                return;
            }
        }

        public float TouchedAt { get; private set; } = -10f;

        // ------------------------------------------------------------ expressions (3x3 face sheet)
        // Follows the delivered interaction sheet: the face is Neutral almost all the time and each
        // clip shows its own designed face. On top of that:
        //  - a held/falling body and a one-shot reaction clip (Cheer, Nod, Shake, Touch_Jump...) keep
        //    their designed face, so face and body always agree;
        //  - a line the character says gives a short accent (about 1.5 s) only when the words are
        //    clearly happy, worried, excited or a question; otherwise the face stays as the clip has it;
        //  - Run_Stop's few-frame wince is dropped (it read as a glitch at every stop);
        //  - clip faces are taken only when the Face bone sits on one cell for 0.06 s and no
        //    cross-fade runs, so nothing flashes in between;
        //  - a change between two open-eyed faces goes through a 0.07 s blink, the way an eye swap
        //    hides on a puppet; blinking itself is the character's own (2.5-6 s, sometimes twice).
        public const int Neutral = 0, Blink = 1, Happy = 2, Thinking = 3, Focused = 4, Excited = 5, Surprised = 6, Worried = 7, Wince = 8;
        public static readonly List<string> MoodLog = new List<string>();
        public int ClipFace { get; private set; }
        public int FaceChanges { get; private set; }
        public int Blinks { get; private set; }
        public int GazeReactions { get; private set; }
        public string FaceSource { get; private set; } = "clip";
        public readonly List<Vector2> FaceLog = new List<Vector2>(); // (time, face), last 600 changes
        private int emoteFace = -1, lastFace = -1, seenFace = -1, wantFace = -1;
        private float seenSince, wantSince, swapUntil;
        private float emoteUntil, nextBlinkAt = -1f, blinkUntil, gazeSince = -1f, nextGazeAt;
        private bool gazeArmed = true;
        private static float anyGazeAt = -100f;

        public void Emote(int face, float seconds)
        {
            emoteFace = Mathf.Clamp(face, 0, 8);
            emoteUntil = Time.time + seconds;
        }

        // The mood of a line, from its words (lines are English). Neutral unless the words are clear.
        public static int MoodOf(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Neutral;
            string t = " " + text.ToLowerInvariant() + " ";
            bool Has(params string[] words) { foreach (var w in words) if (t.Contains(w)) return true; return false; }
            if (Has("too tight", "too loose", "careful", "soften", "warp", "won't", "can't", "couldn't", "didn't", "wasn't", "isn't", "not fit", "risk", "problem", "break", "crack", "went wrong", "'s wrong", "fail", "lost my", "hot coffee", "watch out", "sorry", "oops"))
                return Worried;
            if (Has("it fits", "done!", "yay", "great", "love", "perfect", "nice", "thank", "welcome", " hi!", "hello", "yes!", "here it is", "let's"))
                return Happy;
            if (Has("ooh", "wow", "what if", "idea")) return Excited;
            if (t.TrimEnd().EndsWith("?") || Has("hmm", "maybe", "wonder")) return Thinking;
            return Neutral;
        }

        // What a character says gives its face a short accent as the line starts.
        public static void Express(WindupCharacter character, string text, float seconds)
        {
            var body = Of(character);
            if (body == null) return;
            int mood = MoodOf(text);
            MoodLog.Add(body.Role + ":" + mood + ":" + text);
            if (MoodLog.Count > 200) MoodLog.RemoveAt(0);
            if (mood != Neutral) body.Emote(mood, Mathf.Min(seconds, mood == Worried ? 2f : mood == Thinking ? 1.2f : 1.5f));
        }

        private static bool OpenEyed(int face) => face == Neutral || face == Thinking || face == Focused || face == Excited || face == Worried;

        private void UpdateFace()
        {
            float now = Time.time;
            float raw = faceBone.localScale.x - 1f;
            int near = Mathf.RoundToInt(raw);
            if (OneShot == "Run_Stop" && near == Wince) near = Neutral;
            // A jump or hop it meant to do lands happily (Land's worried/wince faces are for being dropped).
            if (OneShot == "Land" && softLanding && (near == Wince || near == Worried)) near = Neutral;
            if (!animator.IsInTransition(0) && Mathf.Abs(raw - near) < .2f && near != Blink)
            {
                if (near != seenFace) { seenFace = near; seenSince = now; }
                else if (now - seenSince >= .06f) ClipFace = Mathf.Clamp(near, 0, 8);
            }
            var pet = character.movement;
            bool hard = pet != null && ((pet.IsHeld && !pet.OnPalm) || pet.State == CapsulePetController.MotionState.Falling);
            int face; string source;
            // F68 (2026-11-14, the user: "when we pick a character up, make it happy"): lifted by the person it smiles;
            // falling keeps the clip's own face.
            if (hard && pet.IsHeld && !pet.OnPalm) { face = Happy; source = "held"; }
            else if (hard) { face = ClipFace; source = "falling"; }
            else if (OneShot != null && ClipFace != Neutral) { face = ClipFace; source = "clip:" + OneShot; }
            else if (now < emoteUntil && emoteFace >= 0) { face = emoteFace; source = "accent"; }
            else { face = ClipFace; source = "clip:" + (OneShot ?? Current); }

            // An eye swap between two open-eyed faces hides behind a quick blink.
            if (face != wantFace)
            {
                if (wantFace >= 0 && !hard && OpenEyed(face) && OpenEyed(wantFace) && now - wantSince > .25f) swapUntil = now + .07f;
                wantFace = face;
                wantSince = now;
            }
            if (now < swapUntil) { face = Blink; source = "swap"; }

            if (nextBlinkAt < 0f) nextBlinkAt = now + UnityEngine.Random.Range(.5f, 4f);
            if (now >= nextBlinkAt)
            {
                blinkUntil = now + .11f;
                Blinks++;
                nextBlinkAt = UnityEngine.Random.value < .15f ? now + .26f : now + UnityEngine.Random.Range(2.5f, 6f);
            }
            if (now < blinkUntil && OpenEyed(face)) { face = Blink; source = "blink"; }
            if (face != lastFace)
            {
                FaceChanges++;
                FaceLog.Add(new Vector2(now, face));
                if (FaceLog.Count > 600) FaceLog.RemoveAt(0);
                lastFace = face;
            }
            Face = face;
            FaceSource = source;
            var offset = new Vector2((face % 3) / 3f, -(face / 3) / 3f);
            faceMaterial.SetTextureOffset("_BaseMap", offset);
            faceMaterial.mainTextureOffset = offset;
        }

        // Looked at on purpose (head ray within ~7 deg of its face for a second): it looks back with a
        // small smile. Then not again until the person has looked away, at most once per character in
        // 25 s and one character in any 6 s, so being watched all the time does not make them grin on a loop.
        private void NoticeGaze()
        {
            var head = character.meeting != null ? character.meeting.Head : null;
            var pet = character.movement;
            float now = Time.time;
            if (head == null || pet == null || (!pet.OnPalm && (pet.IsHeld || pet.State != CapsulePetController.MotionState.Idle)) || Work != null || OneShot != null) { gazeSince = -1f; return; }
            Vector3 to = HeadTop - Vector3.up * .05f - head.position;
            bool looked = to.magnitude < 2.5f && Vector3.Angle(head.forward, to) < 7f;
            if (!looked) { gazeSince = -1f; gazeArmed = true; return; }
            if (!gazeArmed) return;
            if (gazeSince < 0f) gazeSince = now;
            if (now - gazeSince < 1f || now < nextGazeAt || now - anyGazeAt < 6f) return;
            nextGazeAt = now + 25f;
            anyGazeAt = now;
            gazeArmed = false;
            gazeSince = -1f;
            GazeReactions++;
            Emote(Happy, 1f);
            LookAtPoint(head.position, 2f);
        }

        // ------------------------------------------------------------ face, body turn, head look

        private void LateUpdate()
        {
            if (character == null) return;
            if (faceBone != null && faceMaterial != null) UpdateFace();
            // Moving: face where it goes. Otherwise turn where the old capsule visual turns (yaw only; the
            // clips do the tilting).
            var root = transform.parent != null ? transform.parent : transform;
            Vector3 moved = Vector3.ProjectOnPlane(root.position - lastRoot, Vector3.up);
            lastRoot = root.position;
            float dt = Mathf.Max(Time.deltaTime, .0001f);
            travelSpeed = Mathf.Lerp(travelSpeed, moved.magnitude / dt, 1f - Mathf.Exp(-dt * 8f));
            var pet = character.movement;
            bool travelling = pet != null && !pet.IsHeld && (pet.State == CapsulePetController.MotionState.Walking || pet.State == CapsulePetController.MotionState.Climbing);
            Vector3 want = Vector3.zero;
            if (travelling) want = moved.sqrMagnitude > .0000004f ? moved : Vector3.ProjectOnPlane(root.forward, Vector3.up);
            else if (character.visual != null) want = Vector3.ProjectOnPlane(character.visual.forward, Vector3.up);
            if (want.sqrMagnitude > 1e-9f) facing = Quaternion.Slerp(facing, Quaternion.LookRotation(want.normalized, Vector3.up), 1f - Mathf.Exp(-dt * (travelling ? 10f : 8f)));
            transform.rotation = facing;
            transform.localPosition = Vector3.up * (pet != null && pet.IsHeld ? 0f : character.hop);
            SquashAndStretch();
            KeepLevelWhileMoving(travelling);
            LookWithHead();
            ApplyIntroPose();
            ApplyHolds();
            MeasurePose(travelling);
        }

        // ------------------------------------------------------------ holding a prop
        // The hands are mittens without arms, so holding something is just putting the hand there after
        // the clip: Hold(true, pose) each frame moves the right hand to the pose (blended in and out).
        // The clips do not key the hand bones, so the animator never puts a moved hand back: each frame
        // starts from the hand's own rest pose (recorded before the first hold), and when a hold ends the
        // hand eases back to it.
        private Transform rigHandL, rigHandR;
        private Pose? holdL, holdR;
        private Transform holdFrameL, holdFrameR;
        private Pose lastHoldL, lastHoldR;
        private float holdWL, holdWR;
        private Vector3 restPosL, restPosR;
        private Quaternion restRotL, restRotR;

        public Transform RigHand(bool right)
        {
            if (rigHandL == null && animator != null)
            {
                foreach (var t in animator.GetComponentsInChildren<Transform>())
                    if (t.parent != null && t.parent.name != "Model")
                    {
                        if (t.name == "Hand_L") rigHandL = t;
                        else if (t.name == "Hand_R") rigHandR = t;
                    }
                if (rigHandL != null) { restPosL = rigHandL.localPosition; restRotL = rigHandL.localRotation; }
                if (rigHandR != null) { restPosR = rigHandR.localPosition; restRotR = rigHandR.localRotation; }
            }
            return right ? rigHandR : rigHandL;
        }

        public void Hold(bool right, Pose? pose, Transform relativeTo = null)
        {
            RigHand(right);
            if (right) { holdR = pose; holdFrameR = relativeTo; }
            else { holdL = pose; holdFrameL = relativeTo; }
        }

        public bool HoldReady(bool right) => right ? rigHandR != null && holdWR >= 1f : rigHandL != null && holdWL >= 1f;

        // (2026-11-13, the user: "Maker's hands look turned around while drawing") The mitten bones' own axes do not
        // point along the hand, so a hold given as LookRotation(toward the pencil) turned the mitten round. This is
        // how the clip holds the mitten right now (its rest pose under the animated body); a natural hold only yaws it.
        public Quaternion RestHandRotation(bool right)
        {
            var hand = RigHand(right);
            if (hand == null) return transform.rotation;
            var parent = hand.parent;
            return (parent != null ? parent.rotation : Quaternion.identity) * (right ? restRotR : restRotL);
        }

        // The rest orientation turned (about up) toward `toward`, at most `maxYaw` degrees from where the body faces.
        public Quaternion NaturalGrip(bool right, Vector3 toward, float maxYaw = 60f, float tiltDown = 0f)
        {
            var rest = RestHandRotation(right);
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            Vector3 to = Vector3.ProjectOnPlane(toward - transform.position, Vector3.up);
            if (fwd.sqrMagnitude < 1e-8f || to.sqrMagnitude < 1e-8f) return rest;
            float yaw = Mathf.Clamp(Vector3.SignedAngle(fwd.normalized, to.normalized, Vector3.up), -maxYaw, maxYaw);
            Vector3 side = Vector3.Cross(Vector3.up, to.normalized);
            return Quaternion.AngleAxis(tiltDown, side) * Quaternion.AngleAxis(yaw, Vector3.up) * rest;
        }

        private void ApplyHolds()
        {
            if (rigHandL == null && rigHandR == null) return;
            float step = Time.deltaTime * 5f;
            Apply(rigHandL, WorldHold(holdL, holdFrameL), ref lastHoldL, ref holdWL, restPosL, restRotL);
            Apply(rigHandR, WorldHold(holdR, holdFrameR), ref lastHoldR, ref holdWR, restPosR, restRotR);
            // Resolve carried props after this frame's body turn, so a sharp turn cannot leave
            // the mitten and its prop pointing through the torso for one frame.
            Pose? WorldHold(Pose? pose, Transform frame) => !pose.HasValue || frame == null ? pose
                : new Pose(frame.TransformPoint(pose.Value.position), frame.rotation * pose.Value.rotation);
            void Apply(Transform hand, Pose? want, ref Pose last, ref float weight, Vector3 restPos, Quaternion restRot)
            {
                if (hand == null) return;
                bool was = weight > 0f;
                weight = Mathf.MoveTowards(weight, want.HasValue ? 1f : 0f, step);
                if (want.HasValue) last = want.Value;
                if (!was && weight <= 0f) return;
                hand.localPosition = restPos;
                hand.localRotation = restRot;
                if (weight <= 0f) return;
                float w = Mathf.SmoothStep(0f, 1f, weight);
                hand.SetPositionAndRotation(Vector3.Lerp(hand.position, last.position, w), Quaternion.Slerp(hand.rotation, last.rotation, w));
            }
        }

        // ------------------------------------------------------------ posture while moving (2026-11-11)
        // History: Run_Loop leans 12 deg and looks 8 deg down, which from a person's eye height read as
        // walking head-down; then the chin was lifted 20, then 7 deg, which read as walking with the head
        // thrown back. Now: the body keeps a runner's lean (RunLean), and the head simply looks where it is
        // going - a point LookAhead along the path, at its own eye height (level, RunFacePitch) - so on a
        // curve the head turns into it before the body does (LookWithHead).
        public const float RunLean = 8f, RunFacePitch = 0f, LookAhead = .14f, RunYawLimit = 35f;
        public float HeadTilt { get; private set; } // degrees the face looks below the horizon (+ = down)
        private float levelWeight;
        private Vector3 faceInHead = Vector3.forward; // where the face looks, in head-bone space
        private bool hasFaceInHead;

        // A pose limit a session asks for (Maker drawing in its sketchbook: Creativity_Make_Loop bends far
        // over the book, and its big head went through the pages): the body leans at most `maxLean` degrees
        // and the face looks at most `maxDown` degrees below the horizon.
        public float PostureMaxLean { get; private set; } = -1f;
        public float PostureMaxDown { get; private set; } = -1f;
        private float postureWeight;
        public void LimitPosture(float maxLean, float maxDown) { PostureMaxLean = maxLean; PostureMaxDown = maxDown; }
        public void ClearPosture() { PostureMaxLean = -1f; PostureMaxDown = -1f; }

        // The head as an ellipsoid in head-bone space, measured once from the rest mesh, so props (the
        // sketchbook) can keep out of it: HeadDepth(point) < 1 means inside the head.
        private Vector3 headCentreLocal, headRadiiLocal;
        private bool hasHeadShape;
        private void MeasureHeadShape()
        {
            if (hasHeadShape || headBone == null || !(head is SkinnedMeshRenderer skin)) return;
            var mesh = new Mesh();
            skin.BakeMesh(mesh, true);
            var toWorld = skin.transform.localToWorldMatrix;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var v in mesh.vertices)
            {
                Vector3 l = headBone.InverseTransformPoint(toWorld.MultiplyPoint3x4(v));
                min = Vector3.Min(min, l);
                max = Vector3.Max(max, l);
            }
            Destroy(mesh);
            if (min.x > max.x) return;
            headCentreLocal = (min + max) * .5f;
            headRadiiLocal = Vector3.Max((max - min) * .5f, Vector3.one * 1e-4f);
            hasHeadShape = true;
        }
        // The same, for a point given in the head bone's own space (to test where the head WOULD be).
        public float HeadDepthInHead(Vector3 headLocal)
        {
            if (!hasHeadShape) return 9f;
            Vector3 l = headLocal - headCentreLocal;
            return new Vector3(l.x / headRadiiLocal.x, l.y / headRadiiLocal.y, l.z / headRadiiLocal.z).magnitude;
        }

        public float HeadDepth(Vector3 world)
        {
            if (!hasHeadShape || headBone == null) return 9f;
            Vector3 l = headBone.InverseTransformPoint(world) - headCentreLocal;
            return new Vector3(l.x / headRadiiLocal.x, l.y / headRadiiLocal.y, l.z / headRadiiLocal.z).magnitude;
        }

        private void FaceAxisFromRest()
        {
            // The prefab's rest pose looks straight ahead along the body (the head bone's own axes point
            // backwards, so no angle is read off them); Idle_Loop samples refine it at runtime.
            if (hasFaceInHead || headBone == null) return;
            faceInHead = (Quaternion.Inverse(headBone.rotation) * transform.forward).normalized;
            upInHead = (Quaternion.Inverse(headBone.rotation) * transform.up).normalized;
            hasFaceInHead = true;
        }

        private void KeepLevelWhileMoving(bool moving)
        {
            float dt = Time.deltaTime;
            levelWeight = Mathf.MoveTowards(levelWeight, moving ? 1f : 0f, dt * 5f);
            postureWeight = Mathf.MoveTowards(postureWeight, PostureMaxLean >= 0f ? 1f : 0f, dt * 4f);
            if (headBone == null || headBone.parent == null) return;
            FaceAxisFromRest();
            var bodyBone = headBone.parent;
            Vector3 forward = transform.forward, right = transform.right;
            if (!moving && levelWeight < .001f && Current == "Idle_Loop" && OneShot == null && !animator.IsInTransition(0))
                faceInHead = Vector3.Slerp(faceInHead, Quaternion.Inverse(headBone.rotation) * forward, dt * 2f).normalized;
            Vector3 spine = headBone.position - bodyBone.position;
            float lean = Mathf.Atan2(Vector3.Dot(spine, forward), Vector3.Dot(spine, Vector3.up)) * Mathf.Rad2Deg;
            float back = 0f;
            if (levelWeight > .001f && lean > RunLean) back = Mathf.Max(back, (lean - RunLean) * levelWeight);
            if (postureWeight > .001f && PostureMaxLean >= 0f && lean > PostureMaxLean) back = Mathf.Max(back, (lean - PostureMaxLean) * postureWeight);
            if (back > 0f) bodyBone.rotation = Quaternion.AngleAxis(-back, right) * bodyBone.rotation;
        }

        // Render-time pose numbers for the checks (after the clip, the lean fix, the head aim and the holds).
        public float FacePitch { get; private set; }      // degrees above the horizon
        public float FaceYawToTravel { get; private set; } // degrees between where the face looks and where it goes
        public float FaceYawToBody { get; private set; }
        public float BodyLean { get; private set; }
        public float HeadRoll { get; private set; }
        private Vector3 travelDir;
        private void MeasurePose(bool travelling)
        {
            if (headBone == null || headBone.parent == null) return;
            Vector3 face = headBone.rotation * faceInHead;
            FacePitch = Mathf.Asin(Mathf.Clamp(face.y, -1f, 1f)) * Mathf.Rad2Deg;
            HeadTilt = -FacePitch;
            Vector3 flat = Vector3.ProjectOnPlane(face, Vector3.up);
            FaceYawToBody = flat.sqrMagnitude > 1e-6f ? Vector3.SignedAngle(transform.forward, flat, Vector3.up) : 0f;
            var root = transform.parent != null ? transform.parent : transform;
            if (travelling && travelSpeed > .05f) travelDir = Vector3.ProjectOnPlane(root.position - lastMeasured, Vector3.up);
            lastMeasured = root.position;
            FaceYawToTravel = travelling && travelDir.sqrMagnitude > 1e-8f && flat.sqrMagnitude > 1e-6f ? Vector3.SignedAngle(travelDir, flat, Vector3.up) : 0f;
            Vector3 spine = headBone.position - headBone.parent.position;
            BodyLean = Mathf.Atan2(Vector3.Dot(spine, transform.forward), Vector3.Dot(spine, Vector3.up)) * Mathf.Rad2Deg;
            HeadRoll = Vector3.SignedAngle(Vector3.ProjectOnPlane(Vector3.up, face), Vector3.ProjectOnPlane(headBone.rotation * upInHead, face), face);
        }
        private Vector3 lastMeasured;
        private Vector3 upInHead = Vector3.up;

        // The head aims its face at what the character looks at (2026-11-11). It used to ADD the angle to
        // the target on top of the clip's own head angle, so a clip that already looks down (Creativity_Make
        // -16 deg) looking at something below went to -36 deg. Now it turns only by what is left between the
        // clip's average face direction (smoothed, so the clip's own nods and tilts stay) and the target.
        // Walking or climbing, the target is where it is going; held, falling or in mid-jump, no aim.
        private Vector3 clipBaseline;
        private bool hasBaseline;
        private float noiseSeed = -1f;
        private void LookWithHead()
        {
            if (headBone == null) return;
            if (noiseSeed < 0f) noiseSeed = UnityEngine.Random.Range(0f, 100f);
            float dt = Time.deltaTime;
            var pet = character.movement;
            var state = pet != null ? pet.State : CapsulePetController.MotionState.Idle;
            bool held = pet != null && pet.IsHeld && !pet.OnPalm;
            bool moving = state == CapsulePetController.MotionState.Walking || state == CapsulePetController.MotionState.Climbing;
            Vector3? target = null;
            float yawLimit = 40f, pitchMin = -20f, pitchMax = 30f, rate = 3f, want = 1f;
            bool idleLook = false;
            string source = "none";
            if (held || state == CapsulePetController.MotionState.Falling) want = 0f;
            else if (moving)
            {
                rate = 6f;
                if (pet.Jumping && pet.Phase == CapsulePetController.TraversalPhase.Air) want = .3f;
                if (pet.TryLookAhead(LookAhead, out var ahead))
                {
                    source = "path";
                    if (state == CapsulePetController.MotionState.Walking)
                    {
                        // Level, straight down the path (the point is lifted to eye height).
                        target = new Vector3(ahead.x, headBone.position.y, ahead.z);
                        pitchMin = pitchMax = RunFacePitch;
                        yawLimit = RunYawLimit;
                    }
                    else target = ahead + Vector3.up * .05f; // up where it climbs or jumps to
                }
            }
            else if (lookOverride.HasValue && Time.time < lookOverrideUntil) { target = lookOverride.Value; source = "override"; }
            else if (character.lookTarget != null) { target = character.lookTarget.position; source = "target"; } // (no per-frame strings: the target's name is read by the checks themselves)
            else if (character.meeting != null)
            {
                var speaker = character.meeting.Speaker;
                var head = character.meeting.Head;
                if (speaker != null && speaker != character && !character.meeting.IsListening && !character.meeting.UserHolding) { target = speaker.transform.position + Vector3.up * .1f; source = "speaker"; }
                else if (head != null)
                {
                    idleLook = true;
                    source = !UserTalking ? "person" : ListenGaze == Gaze.Hands ? "listen hands" : ListenGaze == Gaze.FaceSide ? "listen side" : "listen face";
                    // While the person talks each one looks at something a little different: the face, beside
                    // it, or what the hands hold.
                    if (UserTalking && ListenGaze == Gaze.Hands) target = head.position - Vector3.up * .32f + Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized * .22f;
                    else if (UserTalking && ListenGaze == Gaze.FaceSide) target = head.position + head.right * (noiseSeed > 50f ? .05f : -.05f) - Vector3.up * .03f;
                    else target = head.position;
                }
            }
            if (PostureMaxDown >= 0f && postureWeight > .5f) pitchMin = Mathf.Max(pitchMin, -PostureMaxDown);
            LookSource = source;
            if (target.HasValue)
            {
                if (SmoothLookTargets && !moving && hasLookPoint)
                    lookPoint = Vector3.SmoothDamp(lookPoint, target.Value, ref lookPointVelocity, LookTargetTime, Mathf.Infinity, dt);
                else { lookPoint = target.Value; lookPointVelocity = Vector3.zero; }
                hasLookPoint = true;
                target = lookPoint;
            }
            lookWeight = Mathf.MoveTowards(lookWeight, target.HasValue ? want : 0f, dt * rate);

            // The clip's average face direction in the body's frame.
            Vector3 clipFace = Quaternion.Inverse(transform.rotation) * (headBone.rotation * faceInHead);
            clipBaseline = hasBaseline ? Vector3.Slerp(clipBaseline, clipFace, 1f - Mathf.Exp(-dt * 3f)).normalized : clipFace;
            hasBaseline = true;
            Quaternion aim = Quaternion.identity;
            if (target.HasValue)
            {
                Vector3 local = Quaternion.Inverse(transform.rotation) * (target.Value - headBone.position);
                float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                float pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
                if (idleLook)
                {
                    // A living head never sits perfectly still: a slow drift of a few degrees, its own per character.
                    float t = Time.time;
                    yaw += (Mathf.PerlinNoise(noiseSeed, t * .23f) - .5f) * 6f;
                    pitch += (Mathf.PerlinNoise(noiseSeed + 17f, t * .19f) - .5f) * 3.5f;
                }
                yaw = Mathf.Clamp(yaw, -yawLimit, yawLimit);
                pitch = Mathf.Clamp(pitch, pitchMin, pitchMax);
                Vector3 desired = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
                aim = transform.rotation * Quaternion.FromToRotation(clipBaseline, desired) * Quaternion.Inverse(transform.rotation);
            }
            look = Quaternion.Slerp(look, aim, 1f - Mathf.Exp(-dt * (moving ? 12f : 8f)));
            headBone.rotation = Quaternion.Slerp(Quaternion.identity, look, lookWeight) * headBone.rotation;
        }
    }
}
