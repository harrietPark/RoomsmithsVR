using System;
namespace Windups
{
    // Deliberately scripted fixture. Never used as a fallback for an AI/network failure.
    public static class WindupsDemo
    {
        private static MeetingLine Line(string speaker, string summary, string body, string cue = "speak", string reply = "", string relation = "idea") =>
            new MeetingLine { id = Guid.NewGuid().ToString("N"), speaker = speaker, summary = summary, body = body, cue = cue, replyTo = reply, relation = relation };
        public static MeetingResponse Create(string trigger, int clarifications, string reply)
        {
            if (trigger == "wrapup") return new MeetingResponse { utterances = new[] {
                Line("Decisions", "Try one small experiment", "We have two directions: a tiny task companion or a full planner. Try the tiny companion with one person tomorrow. What would make that test worthwhile?", "speak", reply, "summary") } };
            if (trigger == "user" && clarifications == 0) return new MeetingResponse { clarification = true, utterances = new[] {
                Line("Decisions", "Who would you like to help?", "An app could mean many things. Who would you like to help, and what is one task they struggle with?", "speak", reply, "question") } };
            if (trigger == "reaction") return new MeetingResponse { utterances = new[] {
                Line("Caution", "Could this become another distraction?", "That could help, but another notification might interrupt people. Can we test a version that waits for them to ask?", "challenge", reply, "challenge"),
                Line("Creativity", "Make it a quiet companion", "Let's build on that: a small companion offers one next step only when invited. We can test that with a paper prototype.", "propose", reply, "build") } };
            // Only the roles with something to add speak (here three), not a roll call.
            return new MeetingResponse { utterances = new[] {
                Line("Benefit", "You can start small", "You don't need the whole product figured out. Helping one person take one next step is a useful place to begin.", "encourage"),
                Line("Caution", "Avoid a feature pile", "A full planner would take a lot to build. Test whether one small suggestion helps before adding reminders.", "challenge"),
                Line("Creativity", "A tiny task companion", "What if the companion turns a vague task into a two-minute first move, then celebrates that small start?", "propose") } };
        }
    }
}
