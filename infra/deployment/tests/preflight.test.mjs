import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { once } from "node:events";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { buildArmParameters, MAX_INPUT_BYTES, parseRequestJson, PreflightError } from "../parameter-validation.mjs";

const tenantId = "8ecdae60-49c0-4e23-91ab-5ae219764fea";
const accountContext = {
    subscriptionId: "fe9367f6-38b1-4503-9f9b-1cf4f82e61d7",
    tenantId,
};

// Deliberately fictional data; passing offline validation never verifies these identities.
function parameters(overrides = {}) {
    return {
        location: "test-region",
        environmentName: "staging",
        operationalOwner: "Équipe test only",
        virtualNetworkAddressPrefix: "192.0.2.0/24",
        applicationSubnetAddressPrefix: "192.0.2.0/26",
        privateEndpointSubnetAddressPrefix: "192.0.2.64/26",
        workforceTenantId: tenantId,
        workforceClientId: "621715c8-b2a0-4b0d-bb1a-a2500733b4f9",
        workforceRole: "Test.Workforce",
        bootstrapAdministratorObjectId: "35ba91b7-b5b6-442b-9398-95d025e43ca6",
        sqlAdministratorGroupName: "Test only administrators",
        sqlAdministratorGroupObjectId: "cca0fa8b-4110-44c5-9d4f-7dd6b51e4ff2",
        blobRestoreDays: 7,
        sqlPointInTimeRetentionDays: 14,
        logRetentionDays: 30,
        ...overrides,
    };
}

function request(overrides = {}) {
    return { parameters: parameters(overrides), accountContext: { ...accountContext } };
}

function expectedDocument(input = parameters()) {
    return {
        $schema: "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
        contentVersion: "1.0.0.0",
        parameters: Object.fromEntries(Object.entries({
            ...input,
            enableApplication: false,
            appServiceSku: input.appServiceSku ?? "P1v3",
            sqlSku: input.sqlSku ?? "S1",
        }).map(([key, value]) => [key, { value }])),
    };
}

function rejects(action, code) {
    assert.throws(action, error => {
        assert.ok(error instanceof PreflightError);
        assert.equal(error.code, code);
        assert.equal(error.message, new PreflightError(code).message);
        return true;
    });
}

test("successful exact ARM output uses only template defaults and does not mutate inputs", () => {
    const input = Object.freeze(parameters());
    const context = Object.freeze({ ...accountContext });
    assert.deepEqual(buildArmParameters(input, context), expectedDocument(input));
    assert.deepEqual(input, parameters());
    assert.deepEqual(context, accountContext);
});

test("explicit optional values are preserved while application remains disabled", () => {
    const input = parameters({ enableApplication: false, appServiceSku: "P3v3", sqlSku: "S3" });
    assert.deepEqual(buildArmParameters(input, accountContext), expectedDocument(input));
});

test("missing required data never receives invented defaults", () => {
    for (const key of Object.keys(parameters())) {
        const input = parameters();
        delete input[key];
        rejects(() => buildArmParameters(input, accountContext), "required");
    }
    for (const key of ["subscriptionId", "tenantId"]) {
        const context = { ...accountContext };
        delete context[key];
        rejects(() => buildArmParameters(parameters(), context), "required");
    }
    for (const envelope of [{}, { parameters: parameters() }, { accountContext }]) {
        rejects(() => parseRequestJson(JSON.stringify(envelope)), "required");
    }
});

test("approved descriptive data must be explicit nonblank strings", () => {
    for (const key of ["location", "operationalOwner", "workforceRole", "sqlAdministratorGroupName"]) {
        for (const value of ["", " \t\r\n", 1, false]) {
            rejects(() => buildArmParameters(parameters({ [key]: value }), accountContext), "text");
        }
        const value = "Case-sensitive supplied value";
        assert.equal(buildArmParameters(parameters({ [key]: value }), accountContext).parameters[key].value, value);
    }
});

for (const [key, maximum] of [["blobRestoreDays", 364], ["sqlPointInTimeRetentionDays", 35]]) {
    test(`${key} exact boundaries and adjacent failures`, () => {
        for (const value of [1, 2, maximum - 1, maximum]) {
            assert.equal(buildArmParameters(parameters({ [key]: value }), accountContext).parameters[key].value, value);
        }
        for (const value of [0, -1, maximum + 1, 1.5, "1", true, NaN, Infinity]) {
            rejects(() => buildArmParameters(parameters({ [key]: value }), accountContext), "retention");
        }
    });
}

test("logRetentionDays accepts exactly the template enum including adjacent gaps", () => {
    const allowed = [30, 31, 60, 90, 120, 180, 270, 365, 550, 730];
    for (const value of allowed) {
        assert.equal(buildArmParameters(parameters({ logRetentionDays: value }), accountContext).parameters.logRetentionDays.value, value);
    }
    const adjacent = [...new Set(allowed.flatMap(value => [value - 1, value + 1]))].filter(value => !allowed.includes(value));
    for (const value of [...adjacent, 0, -1, 30.5, "30", true, NaN, Infinity]) {
        rejects(() => buildArmParameters(parameters({ logRetentionDays: value }), accountContext), "enum");
    }
});

test("environment and SKU enums are exact and reject coercion", () => {
    for (const [key, allowed] of [
        ["environmentName", ["staging", "production"]],
        ["appServiceSku", ["P1v3", "P2v3", "P3v3"]],
        ["sqlSku", ["S0", "S1", "S2", "S3"]],
    ]) {
        for (const value of allowed) {
            assert.equal(buildArmParameters(parameters({ [key]: value }), accountContext).parameters[key].value, value);
        }
        for (const value of ["", "other", allowed[0].toUpperCase(), allowed[0].toLowerCase(), `${allowed[0]} `, 0, false]) {
            if (allowed.includes(value)) continue;
            rejects(() => buildArmParameters(parameters({ [key]: value }), accountContext), "enum");
        }
    }
});

test("application activation and truthy or false-like coercion are rejected", () => {
    for (const value of [true, "true", "false", "", 0, 1]) {
        rejects(() => buildArmParameters(parameters({ enableApplication: value }), accountContext), "activation");
    }
    for (const key of ["enableApplication", "appServiceSku", "sqlSku"]) {
        for (const value of [null, undefined]) {
            rejects(() => buildArmParameters(parameters({ [key]: value }), accountContext), "shape");
        }
    }
});

const guidFields = [
    "workforceTenantId", "workforceClientId", "bootstrapAdministratorObjectId", "sqlAdministratorGroupObjectId",
];

function withIdentifier(key, value) {
    const input = parameters();
    const context = { ...accountContext };
    if (key.startsWith("account.")) context[key.slice(8)] = value;
    else input[key] = value;
    return () => buildArmParameters(input, context);
}

test("all identity and account identifiers require canonical nonempty GUID syntax", () => {
    const invalid = [
        "", " ", "00000000-0000-0000-0000-000000000000",
        tenantId.replaceAll("-", ""), `{${tenantId}}`, `urn:uuid:${tenantId}`,
        ` ${tenantId}`, `${tenantId}\n`, `${tenantId}x`, `g${tenantId.slice(1)}`, 17, false,
    ];
    for (const key of [...guidFields, "account.subscriptionId", "account.tenantId"]) {
        for (const value of invalid) rejects(withIdentifier(key, value), "guid");
    }
});

test("tenant equality ignores hexadecimal case without rewriting GUID values", () => {
    const input = parameters({
        workforceTenantId: tenantId.toUpperCase(),
        workforceClientId: parameters().workforceClientId.toUpperCase(),
    });
    const result = buildArmParameters(input, accountContext);
    assert.deepEqual(result, expectedDocument(input));
});

test("deployment tenant mismatch fails even when all GUIDs have valid syntax", () => {
    rejects(() => buildArmParameters(parameters(), { ...accountContext, tenantId: parameters().workforceClientId }), "tenant");
});

test("known synthetic tenant and every persona ID are rejected in every GUID position", () => {
    for (const value of [
        "11111111-1111-4111-8111-111111111111",
        "22222222-2222-4222-8222-222222222221",
        "22222222-2222-4222-8222-222222222222",
        "22222222-2222-4222-8222-222222222223",
        "22222222-2222-4222-8222-222222222224",
    ]) {
        for (const key of [...guidFields, "account.subscriptionId", "account.tenantId"]) {
            rejects(withIdentifier(key, value), "synthetic");
        }
    }
});

test("unknown parameter account and envelope keys fail without echoing arbitrary content", () => {
    const marker = "SECRET-key-and-value-\n-token";
    rejects(() => buildArmParameters(parameters({ [marker]: marker }), accountContext), "keys");
    rejects(() => buildArmParameters(parameters(), { ...accountContext, [marker]: marker }), "keys");
    rejects(() => parseRequestJson(JSON.stringify({ ...request(), [marker]: marker })), "keys");
    for (const key of ["__proto__", "constructor", "toJSON"]) {
        rejects(() => parseRequestJson(JSON.stringify({ ...request(), parameters: { ...parameters(), [key]: marker } })), "keys");
    }
    assert.equal(Object.prototype[marker], undefined);
});

test("object shapes reject null arrays nested values symbols accessors and inherited records", () => {
    for (const value of [null, undefined, [], "text", 1, true, new Date(), new String("text"), Object.create({ inherited: true })]) {
        rejects(() => buildArmParameters(value, accountContext), "shape");
        rejects(() => buildArmParameters(parameters(), value), "shape");
    }
    for (const value of [null, undefined, [], {}, 1n, () => {}, Symbol("secret")]) {
        rejects(() => buildArmParameters(parameters({ location: value }), accountContext), "shape");
        rejects(() => buildArmParameters(parameters(), { ...accountContext, tenantId: value }), "shape");
    }
    const symbol = parameters();
    symbol[Symbol("SECRET")] = "SECRET";
    rejects(() => buildArmParameters(symbol, accountContext), "keys");
    const getter = parameters();
    let calls = 0;
    Object.defineProperty(getter, "location", { get() { calls++; return "SECRET"; }, enumerable: true });
    rejects(() => buildArmParameters(getter, accountContext), "shape");
    assert.equal(calls, 0);
    const hidden = parameters();
    Object.defineProperty(hidden, "location", { value: "SECRET", enumerable: false });
    rejects(() => buildArmParameters(hidden, accountContext), "shape");
    const circular = parameters();
    circular.location = circular;
    rejects(() => buildArmParameters(circular, accountContext), "shape");
});

test("plain null-prototype data records are accepted", () => {
    assert.deepEqual(
        buildArmParameters(Object.assign(Object.create(null), parameters()), Object.assign(Object.create(null), accountContext)),
        expectedDocument(),
    );
});

test("canonical IPv4 adjacent subnets and address-space endpoints are accepted", () => {
    for (const [network, application, endpoints] of [
        ["192.0.2.0/24", "192.0.2.0/25", "192.0.2.128/25"],
        ["0.0.0.0/0", "0.0.0.0/32", "255.255.255.255/32"],
        ["0.0.0.0/0", "0.0.0.0/1", "128.0.0.0/1"],
        ["255.255.255.254/31", "255.255.255.254/32", "255.255.255.255/32"],
    ]) {
        const input = parameters({
            virtualNetworkAddressPrefix: network,
            applicationSubnetAddressPrefix: application,
            privateEndpointSubnetAddressPrefix: endpoints,
        });
        assert.deepEqual(buildArmParameters(input, accountContext), expectedDocument(input));
    }
});

test("IPv4 octets prefixes malformed and noncanonical CIDRs are rejected", () => {
    const invalid = [
        "", "192.0.2.0", "192.0.2/24", "192.0.2.0.0/24", "192.0.2.256/24",
        "256.0.2.0/24", "192.256.2.0/24", "192.0.256.0/24",
        "-1.0.2.0/24", "192.0.2.-1/24", "192.0.2.00/24", "0192.0.2.0/24",
        "192.00.2.0/24", "192.0.02.0/24", "192.0.2.0/024", "192.0.2.0/00",
        "192.0.2.0/-1", "192.0.2.0/33", "192.0.2.0/1.5", "192.0.2.0/+24",
        "192.0.2.0/24 ", " 192.0.2.0/24", "192.0.2.0/24\n",
        "192.0.2.1/24", "192.0.2.64/25", "192.0.2.0/0", "::/0", "::ffff:192.0.2.0/120",
        "0xc0.0.2.0/24", "192.0.2.0/24/24", 24, true,
    ];
    for (const key of ["virtualNetworkAddressPrefix", "applicationSubnetAddressPrefix", "privateEndpointSubnetAddressPrefix"]) {
        for (const value of invalid) rejects(() => buildArmParameters(parameters({ [key]: value }), accountContext), "cidr");
    }
});

test("subnet containment rejects outside and larger ranges for either subnet", () => {
    for (const key of ["applicationSubnetAddressPrefix", "privateEndpointSubnetAddressPrefix"]) {
        for (const value of ["192.0.1.255/32", "192.0.3.0/32", "192.0.2.0/23", "0.0.0.0/0"]) {
            rejects(() => buildArmParameters(parameters({ [key]: value }), accountContext), "containment");
        }
    }
});

test("subnet overlap rejects equal nested and shared-endpoint ranges in either order", () => {
    for (const [application, endpoints] of [
        ["192.0.2.0/26", "192.0.2.0/26"],
        ["192.0.2.0/25", "192.0.2.64/26"],
        ["192.0.2.64/26", "192.0.2.0/25"],
        ["192.0.2.0/26", "192.0.2.63/32"],
        ["192.0.2.63/32", "192.0.2.0/26"],
        ["192.0.2.0/24", "192.0.2.0/32"],
    ]) {
        rejects(() => buildArmParameters(parameters({
            applicationSubnetAddressPrefix: application,
            privateEndpointSubnetAddressPrefix: endpoints,
        }), accountContext), "overlap");
    }
});

test("JSON parsing accepts exact envelopes and rejects malformed syntax and duplicate keys", () => {
    const input = request({ operationalOwner: 'Quoted "text" with {braces}: [not an array]' });
    assert.deepEqual(parseRequestJson(JSON.stringify(input)), input);
    for (const value of [
        "", "SECRET", '{"parameters":', '{"SECRET": "unterminated}', "{} {}",
        '{"parameters":{},"parameters":{},"accountContext":{}}',
        '{"parameters":{},"param\\u0065ters":{},"accountContext":{}}',
        '{"parameters":{"location":"first","location":"SECRET"},"accountContext":{}}',
        '{"parameters":{},"accountContext":{"tenantId":"first","tenantId":"SECRET"}}',
        '{"parameters":{"location":"bad\\q"},"accountContext":{}}',
    ]) rejects(() => parseRequestJson(value), "json");
    rejects(() => parseRequestJson(undefined), "json");
});

test("JSON parsing rejects wrong envelope shapes nested objects arrays and unexpected fields", () => {
    for (const input of [null, [], 17, "SECRET", true,
        { parameters: [], accountContext },
        { parameters: null, accountContext },
        { parameters: parameters({ location: { value: "SECRET" } }), accountContext },
        { parameters: parameters(), accountContext: [] },
        { parameters: parameters(), accountContext: null },
        { parameters: parameters(), accountContext: { tenantId: {} } },
    ]) rejects(() => parseRequestJson(JSON.stringify(input)), "shape");
    rejects(() => parseRequestJson(JSON.stringify({ ...request(), parameters: parameters({ SECRET: "SECRET" }) })), "keys");
    rejects(() => parseRequestJson(JSON.stringify({ ...request(), accountContext: { ...accountContext, SECRET: "SECRET" } })), "keys");
    rejects(() => parseRequestJson('{"parameters":{"location":' + '{"nested":'.repeat(500) + "0" + "}".repeat(500) + "}}"), "shape");
});

test("JSON and direct library byte bounds accept exactly 16384 bytes and reject the next byte", () => {
    const input = request({ operationalOwner: "" });
    const overhead = Buffer.byteLength(JSON.stringify(input), "utf8");
    input.parameters.operationalOwner = "é" + "a".repeat(MAX_INPUT_BYTES - overhead - 2);
    const json = JSON.stringify(input);
    assert.equal(Buffer.byteLength(json, "utf8"), MAX_INPUT_BYTES);
    assert.deepEqual(parseRequestJson(json), input);
    assert.deepEqual(buildArmParameters(input.parameters, input.accountContext), expectedDocument(input.parameters));
    rejects(() => parseRequestJson(json + " "), "size");
    input.parameters.operationalOwner += "a";
    rejects(() => buildArmParameters(input.parameters, input.accountContext), "size");
});

const cliPath = fileURLToPath(new URL("../preflight.mjs", import.meta.url));

function cli(input, args = []) {
    const result = spawnSync(process.execPath, [cliPath, ...args], {
        input, encoding: "utf8", timeout: 10_000, maxBuffer: 128 * 1024,
        windowsHide: true,
    });
    assert.equal(result.error, undefined);
    assert.equal(result.signal, null);
    return result;
}

function cliFailure(input, code, args = []) {
    const result = cli(input, args);
    assert.equal(result.status, 1);
    assert.equal(result.stdout, "");
    assert.equal(result.stderr, `${new PreflightError(code).message}\n`);
    assert.equal(result.stderr.includes("SECRET"), false);
}

test("actual CLI valid UTF-8 stdin produces exact ARM stdout and exit zero", () => {
    const result = cli(JSON.stringify(request()));
    assert.equal(result.status, 0);
    assert.equal(result.stderr, "");
    assert.deepEqual(JSON.parse(result.stdout), expectedDocument());
    assert.equal(result.stdout, `${JSON.stringify(expectedDocument(), null, 2)}\n`);
});

test("actual CLI invalid JSON UTF-8 shapes and unknown keys fail without sensitive echo", () => {
    cliFailure('{"SECRET": "SECRET"', "json");
    cliFailure(Buffer.from([0xff, 0xfe, 0x53]), "json");
    cliFailure(`\ufeff${JSON.stringify(request())}`, "json");
    cliFailure("", "json");
    cliFailure("null", "shape");
    cliFailure(JSON.stringify({ ...request(), parameters: parameters({ SECRET: "SECRET" }) }), "keys");
    cliFailure(JSON.stringify({ ...request(), accountContext: { ...accountContext, SECRET: "SECRET" } }), "keys");
    cliFailure(JSON.stringify({ ...request(), SECRET: "SECRET" }), "keys");
    cliFailure(JSON.stringify(request({ operationalOwner: { SECRET: "SECRET" } })), "shape");
    cliFailure('{"parameters":{},"parameters":{"SECRET":"SECRET"},"accountContext":{}}', "json");
});

test("actual CLI semantic failures emit only actionable errors and exit one", () => {
    cliFailure(JSON.stringify({ parameters: {}, accountContext }), "required");
    cliFailure(JSON.stringify(request({ workforceClientId: "SECRET" })), "guid");
    cliFailure(JSON.stringify(request({ bootstrapAdministratorObjectId: "22222222-2222-4222-8222-222222222221" })), "synthetic");
    cliFailure(JSON.stringify({ ...request(), accountContext: { ...accountContext, tenantId: parameters().workforceClientId } }), "tenant");
    cliFailure(JSON.stringify(request({ enableApplication: true })), "activation");
    cliFailure(JSON.stringify(request({ blobRestoreDays: 365 })), "retention");
    cliFailure(JSON.stringify(request({ sqlSku: "SECRET" })), "enum");
    cliFailure(JSON.stringify(request({ location: "" })), "text");
    cliFailure(JSON.stringify(request({ virtualNetworkAddressPrefix: "SECRET" })), "cidr");
    cliFailure(JSON.stringify(request({ applicationSubnetAddressPrefix: "192.0.3.0/24" })), "containment");
    cliFailure(JSON.stringify(request({ privateEndpointSubnetAddressPrefix: "192.0.2.0/26" })), "overlap");
});

test("actual CLI enforces the exact UTF-8 byte limit without emitting oversized input", () => {
    const json = JSON.stringify(request());
    const bounded = json + " ".repeat(MAX_INPUT_BYTES - Buffer.byteLength(json, "utf8"));
    const result = cli(bounded);
    assert.equal(result.status, 0);
    assert.equal(result.stderr, "");
    assert.deepEqual(JSON.parse(result.stdout), expectedDocument());
    cliFailure(bounded + " ", "size");
    cliFailure("SECRET".repeat(MAX_INPUT_BYTES), "size");
});

test("actual CLI help explains trust limits and unexpected arguments never echo values", () => {
    const result = cli("", ["--help"]);
    assert.equal(result.status, 0);
    assert.equal(result.stderr, "");
    assert.match(result.stdout, /authenticated deployment job, never untrusted PR inputs/);
    assert.match(result.stdout, /not approval, identity verification or deployment authorization/);
    cliFailure("", "usage", ["SECRET"]);
    cliFailure("", "usage", ["--help", "SECRET"]);
});

test("actual CLI closed output stream fails with a fixed non-sensitive I/O diagnostic", async () => {
    const child = spawn(process.execPath, [cliPath], {
        stdio: ["pipe", "pipe", "pipe"], windowsHide: true, timeout: 10_000,
    });
    const closed = once(child, "close");
    let diagnostic = "";
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", chunk => { diagnostic += chunk; });
    child.stdout.destroy();
    child.stdin.end(JSON.stringify(request({ operationalOwner: "SECRET" })));
    const [code, signal] = await closed;
    assert.equal(signal, null);
    assert.equal(code, 1);
    assert.equal(diagnostic, `${new PreflightError("io").message}\n`);
    assert.equal(diagnostic.includes("SECRET"), false);
});
