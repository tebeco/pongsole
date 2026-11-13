import { mkdirSync, readFileSync, writeFileSync } from "node:fs";

const project = JSON.parse(readFileSync(new URL("../package.json", import.meta.url)));
const version = project.dependencies["@microsoft/signalr"];
const source = new URL("../node_modules/@microsoft/signalr/", import.meta.url);
const destination = new URL(`../src/Pongsole.Server/wwwroot/vendor/signalr-${version}/`, import.meta.url);
const installed = JSON.parse(readFileSync(new URL("package.json", source)));

if (installed.version !== version) {
    throw new Error(`Expected SignalR ${version}. Run npm ci --ignore-scripts first.`);
}

const files = [
    ["dist/browser/signalr.min.js", "signalr.min.js"],
    ["dist/browser/signalr.min.js.map", "signalr.min.js.map"],
    ["LICENSE.txt", "LICENSE.txt"],
];
const check = process.argv.includes("--check");
if (!check) {
    mkdirSync(destination, { recursive: true });
}

for (const [from, name] of files) {
    const contents = readFileSync(new URL(from, source));
    const target = new URL(name, destination);
    if (!check) {
        writeFileSync(target, contents);
    } else if (!contents.equals(readFileSync(target))) {
        throw new Error(`Vendored ${name} differs. Run npm run vendor:signalr.`);
    }
}

console.log(`SignalR ${version} browser assets ${check ? "match npm" : "copied from npm"}.`);
