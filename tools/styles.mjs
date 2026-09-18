import { readdir, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { compile } from "sass";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "src", "Sidequest.Web");

export async function styleSources(directory) {
    const sources = [];
    for (const entry of await readdir(directory, { withFileTypes: true })) {
        const path = join(directory, entry.name);
        if (entry.isDirectory()) sources.push(...await styleSources(path));
        else if (entry.name.endsWith(".scss") && !entry.name.startsWith("_")) sources.push(path);
    }
    return sources.sort();
}

for (const directory of ["Components", "wwwroot"]) {
    for (const source of await styleSources(join(root, directory))) {
        const result = compile(source, { style: "expanded", sourceMap: false, charset: false });
        await writeFile(source.replace(/\.scss$/, ".css"), result.css + "\n", "utf8");
    }
}
