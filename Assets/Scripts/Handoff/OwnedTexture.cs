using UnityEngine;

namespace Windups.Handoff
{
    // F104: a picture's texture belongs to the picture's own object and goes with it. The finished note's photo and the
    // journey cards used to share the notebook's texture list, which every notebook refresh (open, flip, a new photo)
    // destroyed, so the photo on the wall note turned into a black square.
    public sealed class OwnedTexture : MonoBehaviour
    {
        public Texture2D texture;

        public static void Own(GameObject owner, Texture2D texture) => owner.AddComponent<OwnedTexture>().texture = texture;

        private void OnDestroy()
        {
            if (texture != null) Destroy(texture);
            texture = null;
        }
    }
}
