using UnityEngine;

namespace Windups.Handoff
{
    // A real clap (2026-11-11: hands that only rest together must not count). Pure logic, fed every frame:
    //  - a fast approach: the palms close in at 0.8 m/s or more while both hands are tracked well, open
    //    (fingertips away from the palm), not pinching, palms facing each other, in front of the person;
    //  - then, within 0.2 s, the palms meet (within 9 cm), or the approach stops short within 13 cm
    //    (tracked hands are kept a little apart when they touch), or the hands vanish from tracking right
    //    after a fast approach that was already within 14 cm (palms that meet often hide each other);
    //  - then nothing for `Cooldown` seconds.
    // Hands resting together never clap: there is no fast approach.
    public sealed class StrictClap
    {
        public const float FastApproach = .8f, Meet = .09f, StopShort = .13f, LostWithin = .14f, Window = .2f;
        public float Cooldown = 2f;
        public int Claps { get; private set; }
        public Vector3 Point { get; private set; }
        public float PointAt { get; private set; } = -10f;

        private float last = -1f, lastAt, fastAt = -10f, fastGap = 1f, next;
        private Vector3 lastMid;

        public void Reset()
        {
            last = -1f;
            fastAt = -10f;
            fastGap = 1f;
        }

        // `tracked` false when either palm is missing this frame (then the other values are ignored).
        // `a`/`b` are the palm centres; `facing` says the palms face each other; `steady` that both hands
        // are tracked with high confidence; `ready` that both are open and not pinching; `inFront` that the
        // hands are in front of the person.
        public bool Step(float now, bool tracked, Vector3 a, Vector3 b, bool facing, bool steady, bool ready, bool inFront)
        {
            if (now < next) { last = -1f; return false; }
            if (!tracked)
            {
                bool hidden = last > 0f && now - fastAt < Window * .75f && fastGap < LostWithin;
                last = -1f;
                return hidden && Clapped(lastMid, now);
            }
            float d = Vector3.Distance(a, b);
            Vector3 mid = (a + b) * .5f;
            float dt = now - lastAt;
            float closing = last > 0f && dt > 0f && dt < .1f ? (last - d) / dt : 0f;
            if (closing > FastApproach && d < .4f && facing && steady && ready && inFront) { fastAt = now; fastGap = d; }
            bool fastBefore = fastAt < now && now - fastAt < Window;
            bool met = d < Meet && (fastBefore || fastAt == now);
            bool stopped = fastBefore && last > 0f && closing < .1f && d < StopShort;
            last = d;
            lastAt = now;
            lastMid = mid;
            return (met || stopped) && Clapped(mid, now);
        }

        private bool Clapped(Vector3 at, float now)
        {
            next = now + Cooldown;
            fastAt = -10f;
            fastGap = 1f;
            last = -1f;
            Claps++;
            Point = at;
            PointAt = now;
            return true;
        }
    }
}
