import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { compile } from "sass";
import { styleSources } from "./styles.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const web = join(root, "src", "Sidequest.Web");

test("Every authored stylesheet compiles deterministically to its matching global or isolated CSS asset", async () => {
    const sources = [...await styleSources(join(web, "Components")), ...await styleSources(join(web, "wwwroot"))];
    assert.ok(sources.length >= 20);
    for (const source of sources) {
        const expected = compile(source, { style: "expanded", sourceMap: false, charset: false }).css + "\n";
        assert.equal(await readFile(source.replace(/\.scss$/, ".css"), "utf8"), expected, source);
    }
});

test("Authored Razor and static HTML contain no inline CSS declarations", async () => {
    async function inspect(directory) {
        for (const entry of await readdir(directory, { withFileTypes: true })) {
            const path = join(directory, entry.name);
            if (entry.isDirectory()) await inspect(path);
            else if (/\.(razor|html)$/.test(entry.name)) {
                assert.doesNotMatch(await readFile(path, "utf8"), /<style\b|\sstyle\s*=/i, path);
            }
        }
    }
    await inspect(join(web, "Components"));
    await inspect(join(web, "wwwroot"));
});

test("Navigation hides compatibility and account switching while retaining safe sign-out", async () => {
    const layout = await readFile(join(web, "Components", "Layout", "MainLayout.razor"), "utf8");
    assert.doesNotMatch(layout, /href="\/foundation"|Switch account/);
    assert.match(layout, /action="\/auth\/logout" method="post"/);
    assert.match(layout, /<AntiforgeryToken\s*\/>/);
    assert.match(layout, /data-authentication-change/);
});

test("Only the requested NuGet proxy is enabled and local settings are excluded from publishing", async () => {
    const config = await readFile(join(root, "NuGet.Config"), "utf8");
    assert.match(config, /https:\/\/packagefeedproxy\.microsoft\.io\/nuget\/v3\/index\.json/);
    assert.doesNotMatch(config, /api\.nuget\.org/);
    const project = await readFile(join(web, "Sidequest.Web.csproj"), "utf8");
    assert.match(project, /Update="appsettings\.Development\.json;appsettings\.Development\.example\.json" CopyToPublishDirectory="Never"/);
    assert.match(await readFile(join(root, ".gitignore"), "utf8"), /src\/Sidequest\.Web\/appsettings\.Development\.json/);
});
