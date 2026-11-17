using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Windups
{
    public static class WindupsDirectMeeting
    {
        public const string Endpoint = "https://api.openai.com/v1/responses";
        public static string CreateRequest(MeetingRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.input) || request.input.Length > 3000 || request.history.Count > 12)
                throw new InvalidOperationException("Topic is too long. Please shorten it.");
            string input = JsonUtility.ToJson(request);
            if (input.Length > 24000) throw new InvalidOperationException("Conversation is too long. Please start a new session.");
            var contract = JObject.Parse(Resources.Load<TextAsset>("WindupsMeetingContract").text);
            return new JObject {
                ["model"] = "gpt-5.4-mini", ["store"] = false,
                ["instructions"] = contract["instructions"], ["input"] = input,
                ["max_output_tokens"] = 2300,
                ["text"] = new JObject { ["format"] = new JObject {
                    ["type"] = "json_schema", ["name"] = "windups_meeting", ["strict"] = true,
                    ["schema"] = contract["schema"] } }
            }.ToString(Formatting.None);
        }

        public static bool IsReviewResponse(MeetingResponse response, MeetingRequest request)
        {
            if (response == null || response.clarification || response.utterances?.Length != 1) return false;
            var line = response.utterances[0];
            return line != null && line.speaker == request.reviewer && request.reviewOf != null &&
                   line.replyTo == request.reviewOf.id && !string.IsNullOrWhiteSpace(line.body);
        }

        public static MeetingResponse ReadResponse(string json, MeetingRequest request)
        {
            var envelope = JObject.Parse(json);
            if ((string)envelope["status"] != "completed") throw new InvalidOperationException("Incomplete response");
            string output = string.Concat(envelope["output"].Children()
                .SelectMany(item => item["content"]?.Children() ?? Enumerable.Empty<JToken>())
                .Where(item => (string)item["type"] == "output_text").Select(item => (string)item["text"]));
            var result = JsonUtility.FromJson<MeetingResponse>(output);
            var contract = JObject.Parse(Resources.Load<TextAsset>("WindupsMeetingContract").text);
            var properties = contract["schema"]["properties"]["utterances"]["items"]["properties"];
            bool Allowed(string name, string value) => properties[name]["enum"].Values<string>().Contains(value);
            if (result?.utterances == null || result.utterances.Length > (request.trigger == "reaction" ? 2 : 6))
                throw new InvalidOperationException("Invalid reply count");
            if (result.clarification && (request.trigger != "user" || request.clarificationCount >= 2 ||
                result.utterances.Length < 1 || result.utterances.Length > 4 ||
                result.utterances.Count(line => line.speaker == "Decisions") != 1))
                throw new InvalidOperationException("Invalid clarification");
            if (request.trigger == "review" && !IsReviewResponse(result, request))
                throw new InvalidOperationException("Invalid handoff review");
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var line in result.utterances)
            {
                if (!Allowed("speaker", line.speaker) || !seen.Add(line.speaker) || !Allowed("cue", line.cue) ||
                    !Allowed("relation", line.relation) || string.IsNullOrWhiteSpace(line.body) || line.body.Length > 1600 ||
                    line.summary == null || line.summary.Length > 180 || line.replyTo == null ||
                    (line.replyTo.Length > 0 && !request.history.Any(h => h.id == line.replyTo) &&
                     !(request.trigger == "review" && line.replyTo == request.reviewOf.id)))
                    throw new InvalidOperationException("Invalid contribution");
                line.id = Guid.NewGuid().ToString("N");
            }
            return result;
        }
    }
}
