using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Pet = RoomCapsule.CapsulePetController;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        private List<(Vector3 at, float radius)> DeskObstacles(bool includeMaker, bool omitProduct = false)
        {
            var zones = new List<(Vector3, float)>();
            if (arrival.TopicRing != null && arrival.TopicRing.enabled)
                zones.Add((arrival.TopicRing.transform.position, arrival.RingRadius + .015f));
            if (ringNote != null && !NoteOnWall && ringNote.gameObject.activeInHierarchy) zones.Add((ringNote.position, .055f));
            if (arrival.MicShown) zones.Add((arrival.MicPosition, .06f));
            foreach (var t in MadeThings())
            {
                // F52: the stand being placed (or waiting for its make) is the work place itself; so is the stand
                // under the made thing when the made thing is left out.
                // A stand that carries something counts as that thing (its rim is only a little wider).
                // The stand under the work (a repaint hides the made thing on it meanwhile) is the work place too.
                if (t == Draft || t == placementView || t == pendingStand || t == sizeBox || (omitProduct && t == Product) || (IsStand(t) && !Empty(t)) || // F75: the size box is the work place too
                    (omitProduct && IsStand(t) && Vector3.ProjectOnPlane(StandTopOf(t) - sculptSpot, Vector3.up).magnitude < StandRadiusOf(t) && Mathf.Abs(StandTopOf(t).y - sculptSpot.y) < .03f)) continue;
                var r = t.GetComponentInChildren<MeshRenderer>();
                if (r != null) zones.Add((r.bounds.center, Mathf.Max(r.bounds.extents.x, r.bounds.extents.z) + .01f));
            }
            foreach (var pair in cast)
            {
                if ((!includeMaker && pair.Key == Crew.Maker) || pair.Value == null) continue;
                var c = pair.Value;
                zones.Add((c.transform.position, CrewBodyRadius));
                if (c.movement.State == Pet.MotionState.Walking) zones.Add((c.movement.Destination, CrewBodyRadius));
            }
            return zones;
        }

        private bool MakerStandClear(Vector3 at) => IntoZones(DeskObstacles(false, true), at, MakerWorkRoom) <= .001f;

        public static bool NoWorkStandForChecks; // F100 checks: a desk too crowded for Maker to stand beside the work
        private bool HasWorkStand(Vector3 at, float radius)
        {
            if (NoWorkStandForChecks) return false;
            var room = arrival.handoff.meeting.environment;
            if (!cast.TryGetValue(Crew.Maker, out var maker) || maker == null || room == null) return false;
            float offset = Mathf.Max(.105f, radius + .055f);
            for (int i = 0; i < 16; i++)
            {
                Vector3 want = at + Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward * offset;
                if (!room.TrySameLevelNavPoint(want, out var nav) || Mathf.Abs(nav.y - at.y) > .04f ||
                    Vector3.Distance(want, nav) > .025f || !MakerStandClear(nav)) continue;
                if (Walkable(maker.transform.position, nav, 2.5f)) return true;
            }
            return false;
        }

        private bool sketchSpaceReady;
        private IEnumerator PrepareSketchSpace(WindupCharacter maker)
        {
            sketchSpaceReady = false;
            if (!KeepWorkSpace || MakerStandClear(maker.transform.position)) { sketchSpaceReady = true; yield break; }
            var zones = DeskObstacles(false).Select(z => (z.at, z.radius + MakerWorkRoom - CrewBodyRadius)).ToList();
            if (!SpotOutside(Crew.Maker, zones, out var free))
            {
                Say(Crew.Maker, "I need clear desk space for my sketchbook. Move me away from the note and the others.");
                float until = Time.time + 20f;
                while (Time.time < until && !MakerStandClear(maker.transform.position)) yield return null;
            }
            else yield return StepAside(Crew.Maker, maker, free);
            sketchSpaceReady = !maker.movement.IsHeld && MakerStandClear(maker.transform.position);
        }

        // Test the full panel rectangle, including edges, against the actual room.
        public bool PanelVisible(Vector3 at, float halfWidth, float halfHeight, Quaternion rotation)
        {
            if (Head == null) return true;
            int mask = arrival.handoff.meeting.environment.GeometryMask;
            if (Physics.CheckBox(at, new Vector3(halfWidth, halfHeight, .008f), rotation, mask, QueryTriggerInteraction.Ignore)) return false;
            foreach (float x in new[] { -halfWidth, 0f, halfWidth })
                foreach (float y in new[] { -halfHeight, 0f, halfHeight })
                {
                    Vector3 p = at + rotation * new Vector3(x, y, -.009f);
                    if (Physics.Linecast(Head.position, p, mask, QueryTriggerInteraction.Ignore)) return false;
                }
            return true;
        }

        private Vector3 VisiblePanelSpot(Vector3 preferred, float halfWidth, float halfHeight, float below = 0f)
        {
            // Picture groups have their origin at the image, with notes extending below.
            float shift = below > 0f ? -(below - .08f) * .5f : 0f;
            bool Clear(Vector3 p) => PanelVisible(p + Vector3.up * shift, halfWidth, halfHeight, SpeechBubbleDock.FacingAt(p));
            if (Clear(preferred)) return preferred;
            foreach (float distance in new[] { .65f, .5f, .4f, .3f })
                foreach (float lift in new[] { .08f, .2f, -.05f, .3f })
                    foreach (float sideOffset in new[] { 0f, -.15f, .15f })
                    {
                        Vector3 p = FrontPoint(distance, lift, out var side) + side * sideOffset;
                        if (Clear(p)) return p;
                    }
            return FrontPoint(.35f, .12f, out _);
        }

        private bool LessonPinned(WindupCharacter c) => CurrentLesson != null && c != null &&
            cast.TryGetValue(CurrentLesson.who, out var teacher) && teacher == c;

        private void SpeakLesson(Lesson lesson)
        {
            Hush();
            Voice.Stop();
            if (string.IsNullOrEmpty(lesson.line)) lesson.line = lesson.again;
            if (string.IsNullOrEmpty(lesson.line)) return;
            Caption(lesson.who, lesson.line, 3600f);
            Talk.Add(MakerRoles.Name(lesson.who) + ": " + lesson.line);
            LastSay = lesson.line;
            LastSpeaker = lesson.who;
            lastLineAt = Time.time;
            nextLineAt = Time.time + 1.4f + MakerRoles.Words(lesson.line) * .28f;
            if (cast.TryGetValue(lesson.who, out var c))
            {
                WindupBody.Express(c, lesson.line, 4f);
                Voice.Enabled = !Scripted && Voiced;
                Voice.Speak(MakerRoles.VoiceOf(lesson.who), c.transform, lesson.line, _ => { });
            }
        }

        private void TutorialCaptionFrame()
        {
            var lesson = CurrentLesson;
            if (lesson == null || !CardShown || string.IsNullOrEmpty(lesson.line)) return;
            if (cast.TryGetValue(lesson.who, out var c) && c != null && c.authoredBubble != null && !c.authoredBubble.IsHeld &&
                (!c.authoredBubble.gameObject.activeSelf || c.authoredBubble.title.text != lesson.line))
                Caption(lesson.who, lesson.line, 3600f);
        }
    }
}
