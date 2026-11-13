import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";

const directory = new URL("../native/libdatachannel/", import.meta.url);
const manifest = JSON.parse(await readFile(new URL("manifest.json", directory), "utf8"));
for (const [path, expected] of Object.entries(manifest.assets)) {
  const actual = createHash("sha256").update(await readFile(new URL(path, directory))).digest("hex");
  if (actual !== expected) throw new Error("Native RTC asset hash mismatch: " + path);
  process.stdout.write("Verified native RTC asset: " + path + "\n");
}
