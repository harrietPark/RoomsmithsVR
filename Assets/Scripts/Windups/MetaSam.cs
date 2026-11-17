using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Windups
{
    // F83: Meta's Segment Anything 3.1 on the Meta Model API (docs read 2026-11-14: dev.meta.ai/docs/sam).
    // One request = one picture and ONE short noun phrase ("beak"); the answer is a line of records, each a box
    // and a mask of that box's size. Everything here is plain arithmetic on arrays and strings, so it runs in
    // Task.Run (never on the main thread: FPS). The key is the test-build credential (WindupsDirectCredentials).
    //
    // The mask decoder (Unpack, RangeDecoder, DecodeOneBit) is a C# port of the `one_bit` path of Meta's
    // `@meta-sam/parser` 0.1.0 (mask-codec.js), Copyright (c) Meta Platforms, Inc. and affiliates, used under
    // the SAM License: a copy is in Assets/ThirdParty/MetaSam/LICENSE.txt.
    public static class MetaSam
    {
        public const string Endpoint = "https://api.meta.ai/v1/responses";
        public const string Model = "sam-3.1";
        public static bool Available => WindupsDirectCredentials.TryGetMetaKey(out _);

        public sealed class Record
        {
            public int id;
            public int x1, y1, x2, y2;       // the box in the picture's pixels, both ends included
            public float confidence = 1f;
            public int width, height;        // the mask's own size (the box's)
            public char encoding;            // '!' one_bit, '~' lossless
            public string payload;           // after the encoding mark
        }

        // The request for one picture (a PNG) and one phrase. One-bit masks: a few hundred characters each.
        public static byte[] RequestBody(byte[] png, string phrase)
        {
            var sb = new StringBuilder(png.Length * 4 / 3 + 512);
            sb.Append("{\"model\":\"").Append(Model).Append("\",\"store\":false,\"metadata\":{\"include_confidence\":\"true\",\"mask_encoding\":\"one_bit\"},");
            sb.Append("\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"").Append(Plain(phrase)).Append("\"},");
            sb.Append("{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,").Append(Convert.ToBase64String(png)).Append("\"}]}]}");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // A phrase SAM reads well: lower-case letters and single spaces only (lists and punctuation find nothing).
        public static string Plain(string phrase)
        {
            var sb = new StringBuilder();
            foreach (char c in (phrase ?? "").ToLowerInvariant())
            {
                if (c >= 'a' && c <= 'z') sb.Append(c);
                else if ((c == ' ' || c == '-') && sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        // The answer's text out of the Responses envelope ("" when nothing was found).
        public static string OutputText(string json)
        {
            var envelope = JObject.Parse(json);
            var sb = new StringBuilder();
            if (envelope["output"] is JArray output)
                foreach (var item in output)
                    if (item["content"] is JArray content)
                        foreach (var c in content)
                            if ((string)c["type"] == "output_text") sb.Append((string)c["text"]);
            return sb.ToString();
        }

        // "<0f>0<|box;x1=..;y1=..;x2=..;y2=..;w=..;h=..;c=..|><|mask;x=0;y=0;c=..;data=H,W,!payload|>,1<|box;...".
        // Fields are read by key. The payload's alphabet has none of < > | , ; so the tokens split cleanly.
        public static List<Record> Parse(string text)
        {
            var list = new List<Record>();
            if (string.IsNullOrEmpty(text)) return list;
            int at = 0;
            while (true)
            {
                int box = text.IndexOf("<|box;", at, StringComparison.Ordinal);
                if (box < 0) break;
                int boxEnd = text.IndexOf("|>", box, StringComparison.Ordinal);
                if (boxEnd < 0) break;
                int mask = text.IndexOf("<|mask;", boxEnd, StringComparison.Ordinal);
                int maskEnd = mask < 0 ? -1 : text.IndexOf("|>", mask, StringComparison.Ordinal);
                if (maskEnd < 0) break;
                var r = new Record();
                int digits = box;
                while (digits > 0 && char.IsDigit(text[digits - 1])) digits--;
                int.TryParse(text.Substring(digits, box - digits), NumberStyles.Integer, CultureInfo.InvariantCulture, out r.id);
                foreach (string field in text.Substring(box + 6, boxEnd - box - 6).Split(';'))
                {
                    int eq = field.IndexOf('=');
                    if (eq < 0) continue;
                    string key = field.Substring(0, eq), value = field.Substring(eq + 1);
                    if (key == "c") { float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out r.confidence); continue; }
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) continue;
                    if (key == "x1") r.x1 = n; else if (key == "y1") r.y1 = n; else if (key == "x2") r.x2 = n; else if (key == "y2") r.y2 = n;
                }
                string body = text.Substring(mask + 7, maskEnd - mask - 7);
                int data = body.IndexOf("data=", StringComparison.Ordinal);
                if (data >= 0)
                {
                    string d = body.Substring(data + 5);
                    int c1 = d.IndexOf(','), c2 = c1 < 0 ? -1 : d.IndexOf(',', c1 + 1);
                    if (c2 > 0 && c2 + 1 < d.Length
                        && int.TryParse(d.Substring(0, c1), NumberStyles.Integer, CultureInfo.InvariantCulture, out r.height)
                        && int.TryParse(d.Substring(c1 + 1, c2 - c1 - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out r.width))
                    {
                        r.encoding = d[c2 + 1];
                        r.payload = d.Substring(c2 + 2);
                        if (r.width > 0 && r.height > 0) list.Add(r);
                    }
                }
                at = maskEnd + 2;
            }
            return list;
        }

        // ------------------------------------------------------------ the one_bit mask (port of mask-codec.js)

        private const string Excluded = "\"\\,;<>|";
        private static readonly int[] Digit = MakeDigits();
        private const uint Top = 1u << 24, Bottom = 1u << 16;
        private const int Increment = 14, CountLimit = 4096, ContextCount = 1 << 12, Padding = 2;

        private static int[] MakeDigits()
        {
            var values = new int[128];
            for (int i = 0; i < values.Length; i++) values[i] = -1;
            int index = 0;
            for (int code = 0x21; code <= 0x7e; code++)
                if (Excluded.IndexOf((char)code) < 0) values[code] = index++;
            return values;
        }

        private static int DigitOf(char c)
        {
            int v = c < 128 ? Digit[c] : -1;
            if (v < 0) throw new FormatException("mask payload: bad character");
            return v;
        }

        // Base 85 (five characters = four bytes, big end first) after a five-character length.
        private static byte[] Unpack(string payload)
        {
            if (payload.Length < 5) throw new FormatException("mask payload: no length");
            long length = 0;
            for (int i = 0; i < 5; i++) length = length * 85 + DigitOf(payload[i]);
            int remainder = (int)(length % 4);
            long expected = 5 + (length / 4) * 5 + (remainder == 0 ? 0 : remainder + 1);
            if (payload.Length != expected) throw new FormatException("mask payload: length does not match");
            var output = new byte[length];
            int source = 5, destination = 0, full = (int)length - remainder;
            for (; destination < full; destination += 4)
            {
                ulong value = 0;
                for (int i = 0; i < 5; i++) value = value * 85 + (ulong)DigitOf(payload[source + i]);
                source += 5;
                output[destination] = (byte)(value >> 24);
                output[destination + 1] = (byte)(value >> 16);
                output[destination + 2] = (byte)(value >> 8);
                output[destination + 3] = (byte)value;
            }
            if (remainder > 0)
            {
                ulong value = 0;
                for (int i = 0; i < 5; i++) value = value * 85 + (ulong)(i < remainder + 1 ? DigitOf(payload[source + i]) : 84);
                for (int i = 0; i < remainder; i++) output[destination + i] = (byte)(value >> (24 - i * 8));
            }
            return output;
        }

        private struct RangeDecoder
        {
            private readonly byte[] input;
            private int position;
            private uint low, range, code, scaled;

            public RangeDecoder(byte[] input)
            {
                if (input.Length < 4) throw new FormatException("mask payload: too short");
                this.input = input;
                position = 0; low = 0; range = 0xffffffff; code = 0; scaled = 0;
                for (int i = 0; i < 4; i++) code = (code << 8) | Read();
            }

            public uint Frequency(uint total)
            {
                scaled = range / total;
                if (scaled == 0) throw new FormatException("mask payload: bad coding state");
                uint value = unchecked(code - low) / scaled;
                return value >= total ? total - 1 : value;
            }

            public void Decode(uint cumulative, uint frequency)
            {
                unchecked
                {
                    low += scaled * cumulative;
                    range = scaled * frequency;
                    while (true)
                    {
                        if ((low ^ (low + range)) >= Top)
                        {
                            if (range >= Bottom) break;
                            range = (0u - low) & (Bottom - 1);
                        }
                        code = (code << 8) | Read();
                        low <<= 8;
                        range <<= 8;
                    }
                }
            }

            private uint Read() => position < input.Length ? input[position++] : 0u;
        }

        // The mask as rows of 0 and 1 (width x height, first row on top). `payload` is what follows the '!'.
        public static byte[] DecodeOneBit(string payload, int width, int height)
        {
            var decoder = new RangeDecoder(Unpack(payload));
            var counts = new ushort[ContextCount * 2];
            for (int i = 0; i < counts.Length; i++) counts[i] = 1;
            int paddedWidth = width + 2 * Padding;
            var padded = new byte[paddedWidth * (height + Padding)];
            var output = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                int row = (y + Padding) * paddedWidth + Padding;
                for (int x = 0; x < width; x++)
                {
                    int p = row + x, prior = p - paddedWidth, earlier = p - 2 * paddedWidth;
                    int context = padded[p - 1] | (padded[p - 2] << 1) | (padded[prior - 2] << 2) | (padded[prior - 1] << 3) | (padded[prior] << 4)
                        | (padded[prior + 1] << 5) | (padded[prior + 2] << 6) | (padded[earlier - 2] << 7) | (padded[earlier - 1] << 8)
                        | (padded[earlier] << 9) | (padded[earlier + 1] << 10) | (padded[earlier + 2] << 11);
                    int offset = context << 1;
                    uint zero = counts[offset], one = counts[offset + 1];
                    uint value = decoder.Frequency(zero + one);
                    int bit = value < zero ? 0 : 1;
                    if (bit == 0) decoder.Decode(0, zero); else decoder.Decode(zero, one);
                    padded[p] = (byte)bit;
                    output[y * width + x] = (byte)bit;
                    counts[offset + bit] = (ushort)(counts[offset + bit] + Increment);
                    if (counts[offset] + counts[offset + 1] >= CountLimit)
                    {
                        counts[offset] = (ushort)Math.Max(1, counts[offset] >> 1);
                        counts[offset + 1] = (ushort)Math.Max(1, counts[offset + 1] >> 1);
                    }
                }
            }
            return output;
        }

        public static byte[] Decode(Record r)
        {
            if (r.encoding != '!') throw new FormatException("mask encoding " + r.encoding + " is not read here (ask for one_bit)");
            return DecodeOneBit(r.payload, r.width, r.height);
        }
    }
}
