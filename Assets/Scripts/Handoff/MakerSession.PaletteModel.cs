using System.Collections.Generic;
using Oculus.Interaction.Input;
using UnityEngine;
using UnityEngine.Rendering;

namespace Windups.Handoff
{
    // 2026-11-15 (the designer's art drop, Assets/Art/WindupsCharacter/Props/Palette.fbx -> Resources/WindupsPalette,
    // AgentScripts/ConfigureNewProps.cs): the palette is the designed wooden board instead of the procedural disc.
    //  - The board lies in the palette's own space like the old disc (its face toward -Z, 4.8 mm thick, centred on 0).
    //  - Its eight paint blobs are the swatch slots: a slot shows the person's colour (or picked material) on that
    //    blob's own mesh; an empty slot shows nothing (the palette fills as colours are picked, as before).
    //  - Each blob has a "Press" blend shape: a right fingertip in it squashes it (dipping looks like a dab of paint).
    //  - The colour-picker clip on the rim is the F58 button that opens the colour wheel (it replaces the paint tube):
    //    its button, which wears the colour wheel, sinks under the fingertip and glows while the wheel is open.
    // The carried palette (Spark) and the intro's mini palette use the same model, small, with its own paints.
    // Without the prefab everything falls back to the old procedural palette.
    public sealed partial class MakerSession
    {
        public const float PaletteHalfThick = .0024f, PaintPressReach = .02f, PickerSink = .0013f;
        private static readonly Quaternion ModelToBoard = Quaternion.Euler(-90f, 0f, 0f); // model +Y (up) -> board -Z (front)
        private Transform paletteModel, pickerButton;
        private Renderer pickerButtonLook;
        private Vector3 pickerButtonRest;
        private float pickerSink, pickerGlow;
        private readonly List<Vector3> paintSlots = new List<Vector3>();
        private readonly List<Mesh> paintMeshes = new List<Mesh>();
        private readonly float[] paintPress = new float[PaletteSlots];
        private Material palettePaint;
        private MaterialPropertyBlock paintTint, pickerTint;
        private static GameObject palettePrefab;
        private static bool palettePrefabLoaded;

        public bool HasPaletteModel => paletteModel != null;
        public Transform PickerButton => pickerButton;

        private static GameObject PalettePrefab
        {
            get
            {
                if (!palettePrefabLoaded) { palettePrefab = Resources.Load<GameObject>("WindupsPalette"); palettePrefabLoaded = true; }
                return palettePrefab;
            }
        }

        // The designed board under `paletteBoard`; its paint blobs become the slots. False: no prefab (old disc).
        private bool BuildPaletteModel()
        {
            var model = PaletteModelPiece(paletteBoard, 1f, false);
            if (model == null) return false;
            paletteModel = model;
            var board = model.Find("Palette");
            paintSlots.Clear();
            paintMeshes.Clear();
            for (int i = 1; i <= PaletteSlots; i++)
            {
                var paint = board != null ? board.Find("Paint_0" + i) : null;
                if (paint == null) continue;
                var skinned = paint.GetComponent<SkinnedMeshRenderer>();
                if (skinned == null || skinned.sharedMesh == null) continue;
                paintMeshes.Add(skinned.sharedMesh);
                Vector3 local = paletteBoard.InverseTransformPoint(paint.position);
                paintSlots.Add(new Vector3(local.x, local.y, -PaletteHalfThick)); // on the board's face
            }
            pickerButton = board != null ? board.Find("ColorPicker/ColorPicker_Top/ColorPicker_Button") : null;
            if (pickerButton != null) { pickerButtonRest = pickerButton.localPosition; pickerButtonLook = pickerButton.GetComponent<Renderer>(); }
            return paintSlots.Count > 0;
        }

        // A copy of the designed palette under `parent`, its face toward the parent's -Z, scaled; `paints` keeps the
        // model's own eight colours (the carried and intro palettes), else they are hidden (the real palette's slots).
        private Transform PaletteModelPiece(Transform parent, float scale, bool paints)
        {
            var prefab = PalettePrefab;
            if (prefab == null) return null;
            var model = Instantiate(prefab, parent, false).transform;
            model.name = "Palette Model";
            model.localRotation = ModelToBoard;
            model.localPosition = new Vector3(0f, 0f, PaletteHalfThick * scale);
            model.localScale = Vector3.one * scale;
            var board = model.Find("Palette");
            if (!paints && board != null)
                for (int i = 1; i <= PaletteSlots; i++)
                {
                    var paint = board.Find("Paint_0" + i);
                    if (paint != null) paint.gameObject.SetActive(false);
                }
            return model;
        }

        private Vector3 ModelSlot(int i) => paintSlots[i % paintSlots.Count];

        // A swatch on a blob of the designed palette: the colour itself (True Colour, a little gloss), or the material.
        private Transform PaletteBlob(Color colour, Texture2D tile, int slot)
        {
            var root = new GameObject("Paint").transform;
            root.SetParent(transform, false);
            var blob = new GameObject("Blob", typeof(SkinnedMeshRenderer)).transform;
            blob.SetParent(root, false);
            // the blob's mesh starts at the model's board top; back to the model's origin, turned like the board
            blob.localPosition = new Vector3(0f, 0f, PaletteHalfThick * 2f);
            blob.localRotation = ModelToBoard;
            var r = blob.GetComponent<SkinnedMeshRenderer>();
            r.sharedMesh = paintMeshes[(slot < 0 ? 0 : slot) % paintMeshes.Count];
            Vector3 centre = r.sharedMesh.bounds.center;
            blob.localPosition -= ModelToBoard * new Vector3(centre.x, 0f, centre.z); // the blob's middle on the slot
            r.updateWhenOffscreen = false;
            r.skinnedMotionVectors = false;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            if (tile != null) r.sharedMaterial = TileMaterial(tile, Vector2.one);
            else
            {
                r.sharedMaterial = PalettePaintMaterial ?? material;
                paintTint ??= new MaterialPropertyBlock();
                paintTint.Clear();
                paintTint.SetColor("_BaseColor", colour);
                r.SetPropertyBlock(paintTint);
            }
            return root;
        }

        // Paint on the palette: the colour as picked (True Colour), wet-looking (a little gloss, unlike paint on things).
        private Material PalettePaintMaterial
        {
            get
            {
                if (palettePaint != null || TrueTemplate == null) return palettePaint;
                palettePaint = new Material(TrueTemplate) { name = "Palette Paint" };
                palettePaint.SetTexture("_BaseMap", Texture2D.whiteTexture);
                palettePaint.SetColor("_BaseColor", Color.white);
                palettePaint.SetFloat("_BumpScale", 0f);
                if (palettePaint.HasProperty("_Shade")) palettePaint.SetFloat("_Shade", PaintShade);
                if (palettePaint.HasProperty("_Gloss")) palettePaint.SetFloat("_Gloss", .35f);
                owned.Add(palettePaint);
                return palettePaint;
            }
        }

        // Every frame with the palette (ToolsFrame): a right fingertip in a blob presses it.
        private void PalettePressFrame()
        {
            if (paletteModel == null || paletteBoard == null || !paletteBoard.gameObject.activeInHierarchy) return;
            var hand = LiveHand(Handedness.Right);
            float step = Time.deltaTime * 700f;
            for (int i = 0; i < swatchViews.Count && i < paintPress.Length; i++)
            {
                var view = swatchViews[i];
                if (view == null) continue;
                float near = float.MaxValue;
                if (hand != null)
                    foreach (var (tip, _) in Fingers)
                        if (HandIO.Joint(hand, tip, out Pose end)) near = Mathf.Min(near, Vector3.Distance(end.position, view.position));
                float target = near < PaintPressReach ? Mathf.Clamp01(1f - near / PaintPressReach) * 140f : 0f;
                paintPress[i] = Mathf.MoveTowards(paintPress[i], Mathf.Min(100f, target), step);
                var skinned = view.GetComponentInChildren<SkinnedMeshRenderer>();
                if (skinned != null && skinned.sharedMesh != null && skinned.sharedMesh.blendShapeCount > 0) skinned.SetBlendShapeWeight(0, paintPress[i]);
            }
        }

        // The picker's button (ColourWheelFrame): sinks under a fingertip, glows a little while the wheel is open.
        private void PickerButtonFrame(bool touched)
        {
            if (pickerButton == null) return;
            pickerSink = Mathf.MoveTowards(pickerSink, touched ? 1f : WheelOpen ? .35f : 0f, Time.deltaTime * 14f);
            pickerButton.localPosition = pickerButtonRest - Vector3.up * PickerSink * pickerSink;
            float glow = Mathf.MoveTowards(pickerGlow, WheelOpen ? 1f : 0f, Time.deltaTime * 6f);
            if (pickerButtonLook != null && !Mathf.Approximately(glow, pickerGlow))
            {
                pickerTint ??= new MaterialPropertyBlock();
                pickerButtonLook.GetPropertyBlock(pickerTint);
                pickerTint.SetColor("_EmissionColor", new Color(.35f, .35f, .35f) * glow);
                pickerButtonLook.SetPropertyBlock(pickerTint);
            }
            pickerGlow = glow;
        }

        // The top of the picker's button in the palette's space (where the paint tube used to be).
        private Vector3 PickerButtonLocal()
        {
            var filter = pickerButton.GetComponent<MeshFilter>();
            Vector3 top = filter != null && filter.sharedMesh != null ? new Vector3(filter.sharedMesh.bounds.center.x, filter.sharedMesh.bounds.max.y, filter.sharedMesh.bounds.center.z) : Vector3.zero;
            return paletteBoard.InverseTransformPoint(pickerButton.TransformPoint(top));
        }
    }
}
