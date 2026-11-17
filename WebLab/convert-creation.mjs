import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFile } from "node:child_process";
import { promisify } from "node:util";

const run = promisify(execFile);
const script = fileURLToPath(new URL("./convert-creation.py", import.meta.url));

export async function addEditingFormats(files) {
  const executable = process.env.ROOMSMITHS_BLENDER;
  const glb = files.find(([format]) => format === "glb")?.[1];
  if (!executable || !glb || glb.length < 12 || glb.toString("ascii", 0, 4) !== "glTF") return;
  const work = await fs.mkdtemp(path.join(os.tmpdir(), "roomsmiths-convert-"));
  try {
    const source = path.join(work, "source.glb");
    await fs.writeFile(source, glb);
    await run(executable, ["--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1",
      "--python", script, "--", source, work], { timeout: 25000, maxBuffer: 256 * 1024, windowsHide: true });
    for (const [format, name] of [["fbx", "creation.fbx"], ["obj", "creation.zip"]]) {
      if (files.some(([current]) => current === format)) continue;
      const file = path.join(work, name);
      const stat = await fs.stat(file);
      if (stat.size > 0 && stat.size <= 25 * 1024 * 1024) files.push([format, await fs.readFile(file)]);
    }
  } catch (error) {
    console.warn("Roomsmiths optional format conversion failed:", error.code || error.name);
  } finally { await fs.rm(work, { recursive: true, force: true }); }
}
