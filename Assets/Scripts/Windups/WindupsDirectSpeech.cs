using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Windups
{
    // Provider wire format is isolated from microphone capture and UI state.
    public static class WindupsDirectSpeech
    {
        public const string Endpoint = "wss://api.openai.com/v1/realtime?intent=transcription";
        public const string Configure = "{\"type\":\"session.update\",\"session\":{\"type\":\"transcription\",\"audio\":{\"input\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000},\"transcription\":{\"model\":\"gpt-live-transcribe\",\"prompt\":\"A person talks to three small helpers named Spark, Maker and Fixer about something to make or design, such as a coaster, a cup holder, a mug, a lamp, an app or a game. They may say sketchbook, palette, texture, colour, measure, tape, undo, eraser, notebook, make it, print it or start over.\"},\"turn_detection\":null}}}}";
        public const string Commit = "{\"type\":\"input_audio_buffer.commit\"}";
        public static string Audio(byte[] pcm) => "{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + Convert.ToBase64String(pcm) + "\"}";

        public static string Normalize(string json)
        {
            var data = JObject.Parse(json);
            switch ((string)data["type"])
            {
                case "session.updated": return "{\"type\":\"ready\"}";
                case "conversation.item.input_audio_transcription.delta":
                    return JsonConvert.SerializeObject(new { type = "delta", delta = (string)data["delta"] });
                case "conversation.item.input_audio_transcription.completed":
                    return JsonConvert.SerializeObject(new { type = "final", transcript = (string)data["transcript"] });
                case "error":
                case "conversation.item.input_audio_transcription.failed":
                    string code = (string)data["error"]?["code"];
                    string error = code == "invalid_api_key" ? "Voice access could not be verified."
                        : code == "insufficient_quota" || code == "rate_limit_exceeded" ? "Voice service limit reached. Please try again later."
                        : "Speech could not be transcribed. Hold to try again.";
                    // Do not display or log provider payloads, which may echo credentials/input.
                    return JsonConvert.SerializeObject(new { type = "error", error });
                default: return null;
            }
        }
    }
}
