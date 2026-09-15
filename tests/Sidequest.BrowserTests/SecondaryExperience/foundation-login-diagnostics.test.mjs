import { readFile } from "node:fs/promises";
import vm from "node:vm";
import assert from "node:assert/strict";
import test from "node:test";

const source = await readFile(new URL("./SyntheticLoginDiagnostics.cs", import.meta.url), "utf8");
const match = source.match(/private const string LoginDiagnosticsScript = """\r?\n([\s\S]*?)\r?\n\s*""";/);
assert.ok(match, "Execute the exact failure-only Foundation diagnostic projection.");

function capture({ completion = null, form = null, warning = null } = {}) {
    return vm.runInNewContext(`(${match[1]})()`, {
        document: {
            readyState: "complete",
            cookie: "NEVER-LOG-COOKIE",
            querySelector(selector) {
                return ({
                    "[data-authentication-completion]": completion,
                    "form[data-authentication-change]": form,
                    "[data-authentication-warning]": warning
                })[selector];
            }
        },
        location: {
            href: "http://NEVER-LOG-CREDENTIAL@localhost/auth/complete?proof=NEVER-LOG-QUERY",
            search: "?proof=NEVER-LOG-QUERY"
        }
    });
}

test("Login diagnostics distinguish a pending form from an absent completion without exposing values", () => {
    const raw = capture({
        form: {
            querySelector(selector) {
                assert.equal(selector, 'input[name="experienceEpoch"]');
                return { value: "NEVER-LOG-GENERATION" };
            },
            innerHTML: "NEVER-LOG-PERSONA"
        },
        warning: { hidden: false, textContent: "NEVER-LOG-WARNING-CONTENT" }
    });
    assert.deepEqual(JSON.parse(raw), {
        readyState: "complete", formPresent: true, formGenerationPresent: true,
        completionPresent: false, completionGenerationPresent: false,
        completionState: "absent", clearWarningVisible: true
    });
    assert.doesNotMatch(raw, /NEVER-LOG/);
});

for (const [name, generation, message, expected] of [
    ["pending verified completion", "NEVER-LOG-GENERATION", "Checking the device-clearing boundary before opening Sidequest.", "checking"],
    ["missing initiating generation", "", "Sign-in succeeded, but device saving could not be activated or this sign-in was superseded.", "blocked"],
    ["rejected completion with a generation", "NEVER-LOG-GENERATION", "Sign-in succeeded, but device saving could not be activated or this sign-in was superseded.", "blocked"],
    ["unrecognized content", "NEVER-LOG-GENERATION", "NEVER-LOG-PRIVATE-CONTENT".repeat(500), "other"]
]) {
    test(`Login diagnostics classify ${name} without logging the generation or result text`, () => {
        const raw = capture({
            completion: {
                dataset: { authenticationCompletion: generation },
                querySelector(selector) {
                    assert.equal(selector, "[data-authentication-result]");
                    return { textContent: message };
                }
            }
        });
        assert.deepEqual(JSON.parse(raw), {
            readyState: "complete", formPresent: false, formGenerationPresent: false,
            completionPresent: true, completionGenerationPresent: Boolean(generation),
            completionState: expected, clearWarningVisible: false
        });
        assert.doesNotMatch(raw, /NEVER-LOG/);
        assert.ok(raw.length < 300);
    });
}
