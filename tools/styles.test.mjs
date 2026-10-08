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
    assert.doesNotMatch(layout, /My Quests|Discover Quests|Invited Quests/);
    assert.ok(layout.indexOf('href="/quests"') < layout.indexOf('href="/events"'));
    assert.match(layout, /<footer>[\s\S]*href="\/install">Install and device privacy/);
    assert.match(layout, /<footer>[\s\S]*<ConnectionStatus @rendermode="InteractiveServer"/);
    const home = await readFile(join(web, "Components", "Pages", "Home.razor"), "utf8");
    assert.match(home, /<PageHero[\s\S]*<Actions>[\s\S]*<SignInLink Class="button-link"/);
    assert.doesNotMatch(home, /View sign-in options|href="\/install"/);
    const styles = compile(join(web, "wwwroot", "app.scss")).css;
    assert.match(styles, /\.page-hero-actions\s*\{[^}]*display: flex/s);
    assert.match(styles, /\[hidden\]\s*\{[^}]*display: none !important/s);
});

test("Event management tabs use the menu palette without hover movement", () => {
    const styles = compile(join(web, "wwwroot", "app.scss")).css;
    assert.match(
        styles,
        /\.management-tabs\s*\{[^}]*flex-wrap: wrap;[^}]*overflow: visible;[^}]*background: transparent;[^}]*border: 0;[^}]*border-bottom: 2px solid var\(--sq-line\);[^}]*border-radius: 0;/s);
    assert.match(
        styles,
        /\.management-tabs button\s*\{[^}]*flex: 0 1 auto;[^}]*min-width: max-content;[^}]*color: var\(--sq-muted\);[^}]*background: transparent;[^}]*box-shadow: none;[^}]*margin-bottom: -2px;/s);
    const hover = styles.match(
        /\.management-tabs button:hover:not\(:disabled\)\s*\{(?<body>[^}]*)\}/s);
    assert.ok(hover);
    assert.match(hover.groups.body, /color: var\(--sq-accent\);/);
    assert.match(hover.groups.body, /background: var\(--sq-accent-soft\);/);
    assert.doesNotMatch(hover.groups.body, /transform:/);
    const selected = styles.match(
        /\.management-tabs button\.selected,\s*\.management-tabs button\.selected:hover:not\(:disabled\)\s*\{(?<body>[^}]*)\}/s);
    assert.ok(selected);
    assert.match(selected.groups.body, /color: #fff;/);
    assert.match(selected.groups.body, /background: var\(--sq-ink-soft\);/);
    assert.doesNotMatch(selected.groups.body, /linear-gradient|border-radius:\s*(?:999|100%|50%)/);
    assert.doesNotMatch(styles, /main \.quest-card\.quest-card,\s*main \.event-card\.event-card\s*\{[^}]*transform:/s);
    assert.doesNotMatch(styles, /button, \.button-link\s*\{[^}]*transform:/s);
});

test("Event detail cards keep equal columns, cap media to its column, and collapse at the mobile boundary", () => {
    const styles = compile(join(web, "wwwroot", "app.scss")).css;
    assert.match(
        styles,
        /main \.event-card\.event-card-detail\s*\{[^}]*display: grid;[^}]*grid-template-columns: minmax\(0, 1fr\) minmax\(0, 1fr\);[^}]*overflow: hidden;/s);
    assert.match(
        styles,
        /main \.event-card\.event-card-detail \.event-card-media\s*\{[^}]*max-width: 100%;[^}]*min-height: 100%;/s);
    assert.match(
        styles,
        /@media \(max-width: 640px\)\s*\{[\s\S]*?main \.event-card\.event-card-detail\s*\{[^}]*grid-template-columns: 1fr;/s);
});

test("Only the requested NuGet proxy is enabled and local settings are excluded from publishing", async () => {
    const config = await readFile(join(root, "NuGet.Config"), "utf8");
    assert.match(config, /https:\/\/packagefeedproxy\.microsoft\.io\/nuget\/v3\/index\.json/);
    assert.doesNotMatch(config, /api\.nuget\.org/);
    const project = await readFile(join(web, "Sidequest.Web.csproj"), "utf8");
    assert.match(project, /Update="appsettings\.Development\.json;appsettings\.Development\.example\.json" CopyToPublishDirectory="Never"/);
    assert.match(await readFile(join(root, ".gitignore"), "utf8"), /src\/Sidequest\.Web\/appsettings\.Development\.json/);
});
