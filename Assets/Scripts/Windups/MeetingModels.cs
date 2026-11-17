using System;
using System.Collections.Generic;

namespace Windups
{
    [Serializable]
    public sealed class MeetingLine
    {
        public string id;
        public string speaker;
        public string summary;
        public string body;
        public string replyTo;
        public string relation;
        public string cue;
    }

    [Serializable]
    public sealed class MeetingRequest
    {
        public string requestId;
        public string sessionId;
        public string trigger;
        public string input;
        public int clarificationCount;
        public string reviewer;
        public MeetingLine reviewOf;
        public List<MeetingLine> history = new List<MeetingLine>();
    }

    [Serializable]
    public sealed class MeetingResponse
    {
        public bool clarification;
        public MeetingLine[] utterances;
        public string error;
        public float costUsd;
    }
}
