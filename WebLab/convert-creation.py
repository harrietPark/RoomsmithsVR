"""Optional server-side FBX and OBJ conversion for a Roomsmiths GLB."""
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

import bpy

source = Path(sys.argv[-2]).resolve()
output = Path(sys.argv[-1]).resolve()
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(source))
if not any(obj.type == "MESH" for obj in bpy.context.scene.objects):
    raise ValueError("The GLB contains no mesh")
bpy.ops.export_scene.fbx(
    filepath=str(output / "creation.fbx"),
    object_types={"MESH"},
    path_mode="COPY",
    embed_textures=True,
)
obj = output / "obj"
obj.mkdir()
bpy.ops.wm.obj_export(
    filepath=str(obj / "creation.obj"), export_materials=True, path_mode="COPY"
)
with ZipFile(output / "creation.zip", "w", ZIP_DEFLATED) as archive:
    for file in sorted(obj.iterdir()):
        if file.is_file():
            archive.write(file, file.name)
    archive.writestr("README.txt", "Extract together and import creation.obj. Coordinates are in metres.\n")
