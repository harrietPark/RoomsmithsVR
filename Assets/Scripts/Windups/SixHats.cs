using System;
using UnityEngine;

namespace Windups
{
    // de Bono's six thinking hats, one per Windups character. The hat colours and words are still used
    // by the big idea cards (HatIdeaCard) in the maker session.
    public enum Hat { White, Red, Yellow, Black, Green, Blue }

    [Serializable] public sealed class HatIdea { public string id; public string title; public string detail; public bool mine; public string[] notes; }

    public static class SixHats
    {
        public static readonly string[] Keys = { "white", "red", "yellow", "black", "green", "blue" };
        public static readonly string[] Verbs = { "Facts", "Feelings", "Upside", "Risks", "New ideas", "Next step" };
        public static readonly string[] Characters = { "Information", "Emotions", "Benefit", "Caution", "Creativity", "Decisions" };
        public static readonly Color[] Colors =
        {
            new Color(.96f, .95f, .91f), new Color(.89f, .29f, .29f), new Color(.94f, .62f, .15f),
            new Color(.17f, .17f, .16f), new Color(.39f, .6f, .13f), new Color(.22f, .54f, .87f)
        };

        // What each hat asks, shown under its verb so the user knows whom to tap.
        public static readonly string[] Questions =
        {
            "What do we know?", "How does it feel?", "What is good?", "What could go wrong?", "What else could it be?", "Which one, and what now?"
        };

        public static string Key(Hat hat) => Keys[(int)hat];
        public static string Verb(Hat hat) => Verbs[(int)hat];
        public static string Question(Hat hat) => Questions[(int)hat];
        public static Color Color(Hat hat) => Colors[(int)hat];
        // Readable on white paper (white and yellow hats need a darker ink).
        public static Color Ink(Hat hat) => hat == Hat.White ? new Color(.45f, .45f, .47f) : hat == Hat.Yellow ? new Color(.8f, .52f, .05f) : Colors[(int)hat];
        // Text drawn on the hat colour itself.
        public static Color TextOn(Hat hat) => hat == Hat.White || hat == Hat.Yellow ? new Color(.17f, .17f, .18f) : UnityEngine.Color.white;
        public static Hat ForCharacter(string name)
        {
            int index = Array.IndexOf(Characters, name);
            return index < 0 ? Hat.Blue : (Hat)index;
        }
        public static string CharacterFor(Hat hat) => Characters[(int)hat];
        public static bool TryParse(string key, out Hat hat)
        {
            int index = Array.IndexOf(Keys, key ?? "");
            hat = index < 0 ? Hat.Blue : (Hat)index;
            return index >= 0;
        }
    }
}
