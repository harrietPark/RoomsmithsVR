using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Windups
{
    // The three voices the crew speaks with (OpenAI text-to-speech, StudioVoice). MakerRoles.VoiceOf picks
    // one per teammate: Spark = Dreamer, Maker = Realist, Fixer = Critic.
    public enum Role { Critic, Dreamer, Realist }

    public static class StudioRoles
    {
        public static readonly string[] Keys = { "critic", "dreamer", "realist" };
        public static readonly string[] Names = { "Critic", "Dreamer", "Realist" };
        public static readonly string[] Voices = { "ash", "coral", "sage" };
        public static readonly string[] VoiceStyles =
        {
            "You are a small, honest toy critic. Calm, slightly dry, kind. Speak clearly and briskly.",
            "You are a small, cheerful toy dreamer. Bright, playful and curious. Speak quickly with a smile.",
            "You are a small, practical toy builder. Warm, steady and encouraging. Speak clearly."
        };

        public static string Key(Role role) => Keys[(int)role];
        public static string Name(Role role) => Names[(int)role];
        public static bool TryParse(string key, out Role role)
        {
            int index = Array.IndexOf(Keys, (key ?? "").ToLowerInvariant());
            role = index < 0 ? Role.Realist : (Role)index;
            return index >= 0;
        }

        public static string SpeechRequest(Role role, string text) => new JObject
        {
            ["model"] = "gpt-4o-mini-tts", ["voice"] = Voices[(int)role], ["input"] = text,
            ["instructions"] = VoiceStyles[(int)role], ["response_format"] = "pcm"
        }.ToString(Formatting.None);
    }
}
