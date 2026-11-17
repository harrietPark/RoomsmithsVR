using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace Windups.Handoff
{
    // F81 (2026-11-14, the user after the Quest try):
    //  - "when v2 is made, Maker carves it on the plate where the current one still stands - clear the plate and make v2
    //    fresh": the version on the stand leaves before the carving starts. It shrinks and flies into Maker's notebook
    //    (IntoNotebook: it stays in Versions and in the notebook's history, hidden in the room), so the plate is empty.
    //    If the new make fails, the earlier version comes back out (SculptFail).
    //  - "I came back after finishing and v1, v2, v3 were all still there, with four plates stacked - only the final
    //    one": a version put into the notebook is forgotten as a room item (ForgetRoomItem), and on return only the last
    //    saved make of each project comes back (FinalOnly), with its one stand.
    // VersionsIntoNotebook = false keeps F77 (the last version steps aside and stays; "put the one you like on the stand").
    public sealed partial class MakerSession
    {
        public static bool VersionsIntoNotebook = true;
        public int VersionsNotebooked { get; private set; }
        private Transform versionCleared;

        // The made thing leaves the stand into Maker's notebook (hidden; kept as a version).
        private void ClearPlateForVersion(Transform old)
        {
            if (!VersionsIntoNotebook || old == null || !old.gameObject.activeSelf) return;
            if (held == old) { holdHand = null; mouseHolding = false; held = null; heldBody = null; }
            versionCleared = old;
            ForgetRoomItem(old);
            if (PlatePagerOn) { StartCoroutine(IntoPager(old)); return; } // F94: into the stand's <, from where it comes back
            StartCoroutine(IntoNotebook(old));
        }

        private IEnumerator IntoNotebook(Transform thing)
        {
            VersionsNotebooked++;
            Vector3 from = thing.position, scale = thing.localScale;
            Vector3 to = cast.TryGetValue(Crew.Maker, out var maker) && maker != null ? maker.transform.position + Vector3.up * .1f : from + Vector3.up * .15f;
            ToySounds.Play(ToySounds.Cue.Whoosh, from, .35f, 1.4f);
            ToyFx.Puff(from + Vector3.up * .01f, .01f, 4);
            Body(Crew.Maker)?.Play("Receive");
            for (float t = 0f; t < 1f && thing != null; t += Time.deltaTime / .5f)
            {
                float e = Mathf.SmoothStep(0f, 1f, t);
                thing.position = Vector3.Lerp(from, to, e) + Vector3.up * Mathf.Sin(t * Mathf.PI) * .06f;
                thing.localScale = scale * Mathf.Lerp(1f, .12f, e);
                yield return null;
            }
            if (thing == null) yield break;
            thing.gameObject.SetActive(false);
            thing.position = from;      // (a failed make brings it back where it stood)
            thing.localScale = scale;
            ToySounds.Play(ToySounds.Cue.Paper, to, .3f, 1.3f);
            WriteInputLog("version into Maker's notebook: " + thing.name, true);
        }

        // A new make did not come: the version that went into the notebook comes back onto the stand.
        private void BringClearedVersionBack()
        {
            if (versionCleared == null) return;
            if (Product == versionCleared && !versionCleared.gameObject.activeSelf)
            {
                versionCleared.gameObject.SetActive(true);
                ToyFx.Puff(versionCleared.position + Vector3.up * .01f, .01f, 4);
                Remember();
            }
            versionCleared = null;
        }

        // The room no longer keeps this made thing (it lives in the notebook only).
        private void ForgetRoomItem(Transform thing)
        {
            try
            {
                var memory = LoadMemory();
                var item = SavedItem(memory, thing, madeNames.TryGetValue(thing, out var n) ? n : Design?.name);
                if (item == null) return;
                memory.items.Remove(item);
                File.WriteAllText(MemoryPath, JsonConvert.SerializeObject(memory, Formatting.Indented));
            }
            catch (Exception exception) { Debug.LogWarning("[Windups Maker] memory: " + exception.Message); }
        }

        // Only the last saved make of each project (older saves kept every version, each with its stand).
        private static List<Made> FinalOnly(List<Made> items)
        {
            if (!VersionsIntoNotebook || items == null) return items ?? new List<Made>();
            var lastOf = new Dictionary<string, int>();
            for (int i = 0; i < items.Count; i++)
                if (!string.IsNullOrEmpty(items[i]?.notebookId)) lastOf[items[i].notebookId] = i;
            var kept = new List<Made>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                if (!string.IsNullOrEmpty(item.notebookId) && lastOf[item.notebookId] != i) continue;
                kept.Add(item);
            }
            return kept;
        }

        // Checks: how many made things the next start would bring back.
        public static int RoomItemsToRestore() => FinalOnly(LoadMemory().items).Count;
        public static int RoomItemsSaved() => LoadMemory().items.Count;
    }
}
