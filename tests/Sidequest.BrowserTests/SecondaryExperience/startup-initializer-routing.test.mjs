import { readFile } from "node:fs/promises";
import vm from "node:vm";
import assert from "node:assert/strict";
import test from "node:test";

const source = await readFile(new URL("./AuthenticationStartupBrowserTests.cs", import.meta.url), "utf8");
const match = source.match(/private const string InitializerImportScript = """\r?\n([\s\S]*?)\r?\n\s*""";/);
assert.ok(match, "Execute the exact browser scenario's import-map resolver.");

function resolve(map) {
    return vm.runInNewContext(`(${match[1]})()`, {
        URL,
        document: {
            baseURI: "http://127.0.0.1:5078/",
            querySelector(selector) {
                assert.equal(selector, 'script[type="importmap"]');
                return map === undefined ? null : { textContent: JSON.stringify(map) };
            }
        }
    });
}

test("Startup barrier resolves the rendered fingerprint before the compound Razor-JavaScript extension", () => {
    assert.equal(resolve({ imports: {
        "./Components/App.razor.js": "./Components/App.nr20ghu6hy.razor.js",
        "./Sidequest.Web.lib.module.js": "./Sidequest.Web.mten1e7bkq.lib.module.js"
    } }), "http://127.0.0.1:5078/Components/App.nr20ghu6hy.razor.js");
    assert.equal(resolve({ imports: { "./Components/App.razor.js": "./Components/App.a123b456c7.razor.js" } }),
        "http://127.0.0.1:5078/Components/App.a123b456c7.razor.js");
});

test("Startup barrier rejects missing or invalid import mappings instead of silently matching a guessed asset", () => {
    assert.throws(() => resolve(), /no import map/);
    for (const target of [undefined, null, "", 42]) {
        assert.throws(() => resolve({ imports: { "./Components/App.razor.js": target } }), /does not identify/);
    }
});
