using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Windups
{
    // F48 (2026-11-12) the Crew Director: one reading of what the person said.
    //   The keyword router decided what a sentence meant from word lists and the step it was said in, so "can you
    //   make version two?" became a flag and a question asked while Maker was building became a change. The
    //   Director reads each utterance once and answers with: the intent (question / instruction / answer / idea /
    //   reaction / unclear), who answers, the app tools to run (named, with arguments; only the ones possible in
    //   this step are offered) and the crew's few words.
    //   The contract (rules, character sheets, step manuals, tools) is Resources/CrewDirector.json, shared with
    //   the text bench in WebLab/director (director.mjs builds the same request: keep the two in step).
    [Serializable] public sealed class DirectorAction { public string tool; public string text; public int index = -1; public string who; public string part; public string axis; public float mm; }
    [Serializable] public sealed class DirectorLine { public string who; public string text; public string gesture; }
    [Serializable] public sealed class DirectorAsk { public string who; public string text; public string[] options; }
    [Serializable] public sealed class DirectorReply { public string lang; public string intent; public string to; public DirectorAction[] actions; public DirectorLine[] lines; public DirectorAsk ask; public string note; }

    [Serializable] public sealed class DirectorTool
    {
        public string name, help;
        public string[] steps, needs, args;
        public bool narrates, costly, changes;
    }

    public static class CrewDirector
    {
        public const string Model = "gpt-5.4-mini"; // bench 2026-11-12: as accurate as gpt-5.4 on the cases, and faster
        public static readonly string[] CrewNames = { "Spark", "Maker", "Fixer" };
        public static readonly string[] Intents = { "question", "instruction", "answer", "idea", "reaction", "unclear" };
        public static readonly string[] Parts = { "rim", "base", "side", "top", "inside", "none" };
        public static readonly string[] Axes = { "width", "height", "depth", "all", "none" };
        public static readonly string[] Gestures = { "none", "nod", "shake", "idea", "cheer", "present", "wave", "tilt" };

        [Serializable] private sealed class Contract
        {
            public int version;
            public string[] rules;
            public Dictionary<string, string> sheets, manuals;
            public DirectorTool[] tools;
        }

        private static Contract contract;
        private static readonly Dictionary<string, (string instructions, JObject schema)> cache = new Dictionary<string, (string, JObject)>();
        private static readonly object gate = new object();

        public static bool Ready => contract != null;

        // The contract text (the Resources TextAsset, read on the main thread) may be parsed on any thread.
        public static bool Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            var parsed = JsonConvert.DeserializeObject<Contract>(json);
            if (parsed?.rules == null || parsed.tools == null || parsed.manuals == null || parsed.sheets == null) return false;
            lock (gate) { contract = parsed; cache.Clear(); }
            return true;
        }

        public static bool HasManual(string step) => contract != null && contract.manuals.ContainsKey(step);
        public static DirectorTool Tool(string name) => contract?.tools.FirstOrDefault(t => t.name == name);

        // Possible now: its step, and every flag it needs ("!flag" = must be off).
        public static bool IsAvailable(DirectorTool tool, string step, IDictionary<string, bool> flags)
        {
            if (tool == null || !(tool.steps.Contains("*") || tool.steps.Contains(step))) return false;
            foreach (string need in tool.needs)
            {
                bool off = need.StartsWith("!", StringComparison.Ordinal);
                flags.TryGetValue(off ? need.Substring(1) : need, out bool on);
                if (on == off) return false;
            }
            return true;
        }

        public static List<DirectorTool> Available(string step, IDictionary<string, bool> flags) =>
            contract == null ? new List<DirectorTool>() : contract.tools.Where(t => IsAvailable(t, step, flags)).ToList();

        public static string Instructions(string step, List<DirectorTool> tools)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join("\n", contract.rules));
            sb.Append("\n\nTHE CREW\n").Append(string.Join("\n", CrewNames.Select(c => "- " + c + ": " + contract.sheets[c])));
            sb.Append("\n\nTHIS STEP: ").Append(step).Append('\n').Append(contract.manuals.TryGetValue(step, out string manual) ? manual : "").Append('\n').Append(contract.manuals["always"]);
            sb.Append("\n\nTOOLS YOU MAY USE NOW\n");
            sb.Append(string.Join("\n", tools.Select(t => "- " + t.name + "(" + string.Join(", ", t.args) + ")" +
                (t.changes ? " [changes]" : "") + (t.costly ? " [costly]" : "") + (t.narrates ? " [narrates]" : "") + ": " + t.help)));
            return sb.ToString();
        }

        private static JObject Obj(JObject properties) => new JObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties,
            ["required"] = new JArray(properties.Properties().Select(p => p.Name))
        };

        private static JObject Enum(IEnumerable<string> values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };

        public static JObject Schema(List<DirectorTool> tools)
        {
            JObject Str() => new JObject { ["type"] = "string" };
            return Obj(new JObject
            {
                ["lang"] = Str(),
                ["intent"] = Enum(Intents),
                ["to"] = Enum(CrewNames.Concat(new[] { "team" })),
                ["actions"] = new JObject { ["type"] = "array", ["items"] = Obj(new JObject
                {
                    ["tool"] = Enum(tools.Select(t => t.name)), ["text"] = Str(), ["index"] = new JObject { ["type"] = "integer" },
                    ["who"] = Enum(CrewNames.Concat(new[] { "none" })), ["part"] = Enum(Parts), ["axis"] = Enum(Axes), ["mm"] = new JObject { ["type"] = "number" }
                }) },
                ["lines"] = new JObject { ["type"] = "array", ["items"] = Obj(new JObject { ["who"] = Enum(CrewNames), ["text"] = Str(), ["gesture"] = Enum(Gestures) }) },
                ["ask"] = Obj(new JObject { ["who"] = Enum(CrewNames), ["text"] = Str(), ["options"] = new JObject { ["type"] = "array", ["items"] = Str() } }),
                ["note"] = Str()
            });
        }

        // The whole request body. state / recent / heard are plain data (safe to serialise off the main thread).
        public static string CreateRequest(string step, IDictionary<string, bool> flags, object state, object recent, object heard)
        {
            var tools = Available(step, flags);
            string key = step + "|" + string.Join(",", tools.Select(t => t.name));
            (string instructions, JObject schema) built;
            lock (gate)
            {
                if (!cache.TryGetValue(key, out built))
                {
                    built = (Instructions(step, tools), Schema(tools));
                    cache[key] = built;
                }
            }
            return new JObject
            {
                ["model"] = Model, ["store"] = false,
                ["instructions"] = built.instructions,
                ["input"] = JsonConvert.SerializeObject(new { state, recent, heard }),
                ["max_output_tokens"] = 2000,
                ["text"] = new JObject { ["format"] = new JObject { ["type"] = "json_schema", ["name"] = "windups_director", ["strict"] = true, ["schema"] = built.schema } }
            }.ToString(Formatting.None);
        }

        // Only letters the headset font can show (Latin): a line in another script is dropped, not shown as boxes.
        public static bool Showable(string text)
        {
            foreach (char c in text ?? "")
                if (c >= 'ɐ' && !(c >= '‐' && c <= '⁞')) return false;
            return true;
        }
    }
}
