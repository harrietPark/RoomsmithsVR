using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;

namespace Windups.Handoff
{
    public sealed partial class MakerSession
    {
        [Serializable] public sealed class JourneyPlacement
        {
            public string image, anchor;
            public float[] local, rotation;
        }
        private readonly Dictionary<Picture, (NotebookWork work, string image)> journeyCards = new();

        private void ShowJourneyCard(NotebookWork work, string image, Transform note, int index)
        {
            var existing = journeyCards.FirstOrDefault(c => c.Value.work == work && c.Value.image == image).Key;
            if (existing != null && existing.root != null) { existing.root.gameObject.SetActive(true); return; }
            var node = new GameObject("Journey card", typeof(RectTransform), typeof(Canvas));
            var root = (RectTransform)node.transform;
            root.SetParent(transform, false);
            root.sizeDelta = new Vector2(120, 100);
            root.localScale = Vector3.one * .001f;
            root.SetPositionAndRotation(note.position + note.right * (.14f + index % 2 * .14f) + note.up * (.06f - index / 2 * .12f), note.rotation);
            node.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            work.journeyPlaces ??= new List<JourneyPlacement>();
            var saved = work.journeyPlaces.FirstOrDefault(p => p.image == image);
            if (saved != null && PlaceFromAnchor(saved.anchor, saved.local, saved.rotation, out var at, out var rotation)) root.SetPositionAndRotation(at, rotation);
            JourneyPicture(root, image, 0, 0, 120, 100);
            var picture = new Picture { root = root, home = root.position, size = .12f };
            journeyCards[picture] = (work, image);
        }

        private Picture JourneyAt(Vector3 point, Ray? ray, out float distance)
        {
            distance = 0;
            foreach (var p in journeyCards.Keys)
            {
                if (p.root == null || !p.root.gameObject.activeInHierarchy) continue;
                var local = p.root.InverseTransformPoint(point);
                if (Mathf.Abs(local.x) < 60 && Mathf.Abs(local.y) < 50 && Mathf.Abs(local.z) < 30) return p;
            }
            if (!ray.HasValue) return null;
            Picture nearest = null;
            float best = 4f;
            foreach (var p in journeyCards.Keys)
            {
                if (p.root == null || !p.root.gameObject.activeInHierarchy || !new Plane(p.root.forward, p.root.position).Raycast(ray.Value, out float d) || d > best) continue;
                var local = p.root.InverseTransformPoint(ray.Value.GetPoint(d));
                if (Mathf.Abs(local.x) > 60 || Mathf.Abs(local.y) > 50) continue;
                nearest = p; best = d;
            }
            if (nearest != null) distance = best;
            return nearest;
        }

        private void SaveJourneyCard(Picture picture)
        {
            if (!journeyCards.TryGetValue(picture, out var card)) return;
            var anchor = AnchorUnder(picture.root.position);
            if (anchor == null) return;
            var position = anchor.transform.InverseTransformPoint(picture.root.position);
            var rotation = Quaternion.Inverse(anchor.transform.rotation) * picture.root.rotation;
            card.work.journeyPlaces ??= new List<JourneyPlacement>();
            card.work.journeyPlaces.RemoveAll(p => p.image == card.image);
            card.work.journeyPlaces.Add(new JourneyPlacement { image = card.image, anchor = AnchorKey(anchor),
                local = new[] { position.x, position.y, position.z }, rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w } });
            SaveNotebook();
        }

        private void PlaceStoredWork(NotebookWork work)
        {
            if (Head == null || MadeThings().Any(t => notebookOwners.TryGetValue(t, out var owner) && owner == work.id)) return;
            var memory = LoadMemory();
            var item = memory.items.LastOrDefault(m => m.notebookId == work.id && m.design != null);
            if (item == null) { Say(Crew.Maker, "This page has ideas and pictures. There's no model to place yet."); return; }
            var design = item.design.Clamped();
            var mesh = CoasterMesh.Build(design);
            var rotation = Quaternion.Euler(0, Head.eulerAngles.y, 0);
            Vector3 point = default;
            bool found = false;
            foreach (var offset in new[] { new Vector3(0, -.3f, .65f), new Vector3(.3f, -.3f, .65f), new Vector3(-.3f, -.3f, .65f), new Vector3(0, -.1f, .9f) })
            {
                point = Head.position + rotation * offset;
                var extent = mesh.bounds.extents + Vector3.one * .03f;
                if (Physics.CheckBox(point + rotation * mesh.bounds.center, extent, rotation, arrival.handoff.meeting.environment.GeometryMask, QueryTriggerInteraction.Ignore)) continue;
                if (MadeThings().Any(t => t.GetComponentInChildren<Renderer>() is Renderer renderer && renderer.bounds.Intersects(new Bounds(point, extent * 2)))) continue;
                found = true; break;
            }
            if (!found) { Destroy(mesh); Say(Crew.Maker, "Look toward a clear space, then tap Edit again."); return; }
            var anchor = AnchorUnder(point);
            if (anchor == null) { Destroy(mesh); Say(Crew.Maker, "Let the room finish loading, then tap Edit again."); return; }
            var made = Solid(mesh, "Placed " + design.name, point, out _, design);
            made.rotation = rotation;
            AfterSolid(made, design);
            notebookOwners[made] = work.id;
            TrackRestoredWork(item, made);
            madeNames[made] = item.name;
            ApplyMarks(made, item.paint);
            RoomItems.Add(made);
            var local = anchor.transform.InverseTransformPoint(point);
            var turn = Quaternion.Inverse(anchor.transform.rotation) * rotation;
            item.anchor = AnchorKey(anchor); item.local = new[] { local.x, local.y, local.z };
            item.rotation = new[] { turn.x, turn.y, turn.z, turn.w }; item.world = new[] { point.x, point.y, point.z };
            item.turn = new[] { rotation.x, rotation.y, rotation.z, rotation.w }; // F91
            File.WriteAllText(MemoryPath, JsonConvert.SerializeObject(memory, Formatting.Indented));
            RestoreStand(made, item.stand, item.standAt, item.standTurn);
            FillSpread();
            Say(Crew.Maker, "Your creation is here. Grab it and put it where you like.");
        }

        // F77: the notebook's Edit: a finished work is opened again; when it is not in this room it is brought out first
        // (in front of the person, as Place did).
        private void EditWork(NotebookWork work)
        {
            if (work == null) return;
            bool Present() => MadeThings().Any(t => notebookOwners.TryGetValue(t, out var owner) && owner == work.id);
            if (!Present()) PlaceStoredWork(work);
            if (Present()) ReopenWork(work);
        }
    }
}
