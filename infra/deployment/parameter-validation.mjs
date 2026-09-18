export const MAX_INPUT_BYTES = 16 * 1024;

const messages = Object.freeze({
    json: "Provide valid UTF-8 JSON with no duplicate keys.",
    size: "Keep the JSON request within 16384 UTF-8 bytes.",
    shape: "Provide plain JSON objects with only the documented fields and scalar parameter values.",
    keys: "Remove unexpected fields; use only the documented request, parameter and account-context keys.",
    required: "Supply every required template parameter and both account-context identifiers.",
    text: "Supply nonblank strings for location, operationalOwner, workforceRole and sqlAdministratorGroupName.",
    guid: "Supply nonempty hyphenated GUIDs for all identity and account-context identifiers.",
    synthetic: "Replace known repository synthetic identifiers with independently verified deployment identifiers.",
    tenant: "Use an authenticated deployment account in the same tenant as workforceTenantId.",
    enum: "Use the exact environmentName, appServiceSku, sqlSku and logRetentionDays values documented in main.bicep.",
    retention: "Use integer blobRestoreDays from 1 through 364 and sqlPointInTimeRetentionDays from 1 through 35.",
    activation: "Keep enableApplication false; activation requires a separate verified release flow.",
    cidr: "Supply canonical IPv4 network CIDRs with decimal octets, prefixes 0 through 32 and zero host bits.",
    containment: "Place both subnet ranges entirely inside virtualNetworkAddressPrefix.",
    overlap: "Use disjoint application and private-endpoint subnet ranges.",
    io: "Check the input/output streams and retry; no deployment was attempted.",
    usage: "Usage: node infra\\deployment\\preflight.mjs < request.json > parameters.json (or --help).",
});

export class PreflightError extends Error {
    constructor(code) {
        super(messages[code] ?? messages.shape);
        this.name = "PreflightError";
        this.code = Object.hasOwn(messages, code) ? code : "shape";
    }
}

function fail(code) {
    throw new PreflightError(code);
}

// These exact IDs belong to DevelopmentPersonas.cs, not a general fake-ID heuristic.
const syntheticIds = new Set([
    "11111111-1111-4111-8111-111111111111",
    "22222222-2222-4222-8222-222222222221",
    "22222222-2222-4222-8222-222222222222",
    "22222222-2222-4222-8222-222222222223",
    "22222222-2222-4222-8222-222222222224",
]);

function guid(value) {
    if (typeof value !== "string"
        || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
        || value === "00000000-0000-0000-0000-000000000000") fail("guid");
    if (syntheticIds.has(value.toLowerCase())) fail("synthetic");
}

function text(value) {
    if (typeof value !== "string" || value.trim().length === 0) fail("text");
}

function enumeration(allowed) {
    return value => {
        if (!allowed.includes(value)) fail("enum");
    };
}

function retention(maximum) {
    return value => {
        if (!Number.isInteger(value) || value < 1 || value > maximum) fail("retention");
    };
}

function cidr(value) {
    if (typeof value !== "string"
        || !/^(?:0|[1-9][0-9]{0,2})(?:\.(?:0|[1-9][0-9]{0,2})){3}\/(?:0|[1-9]|[12][0-9]|3[0-2])$/.test(value)) {
        fail("cidr");
    }
    const [address, prefix] = value.split("/");
    const octets = address.split(".").map(Number);
    if (octets.some(octet => octet > 255)) fail("cidr");
    const start = octets.reduce((result, octet) => result * 256 + octet, 0);
    const size = 2 ** (32 - Number(prefix));
    if (start % size !== 0) fail("cidr");
    return { start, end: start + size - 1 };
}

const rules = Object.freeze({
    location: { check: text },
    environmentName: { check: enumeration(["staging", "production"]) },
    operationalOwner: { check: text },
    virtualNetworkAddressPrefix: { check: cidr },
    applicationSubnetAddressPrefix: { check: cidr },
    privateEndpointSubnetAddressPrefix: { check: cidr },
    workforceTenantId: { check: guid },
    workforceClientId: { check: guid },
    workforceRole: { check: text },
    bootstrapAdministratorObjectId: { check: guid },
    sqlAdministratorGroupName: { check: text },
    sqlAdministratorGroupObjectId: { check: guid },
    blobRestoreDays: { check: retention(364) },
    sqlPointInTimeRetentionDays: { check: retention(35) },
    logRetentionDays: { check: enumeration([30, 31, 60, 90, 120, 180, 270, 365, 550, 730]) },
    enableApplication: { check: value => { if (value !== false) fail("activation"); }, defaultValue: false },
    appServiceSku: { check: enumeration(["P1v3", "P2v3", "P3v3"]), defaultValue: "P1v3" },
    sqlSku: { check: enumeration(["S0", "S1", "S2", "S3"]), defaultValue: "S1" },
});

function record(value, allowedKeys, scalar = false) {
    if (value === null || typeof value !== "object"
        || ![Object.prototype, null].includes(Object.getPrototypeOf(value))) fail("shape");
    const keys = Reflect.ownKeys(value);
    if (keys.some(key => !allowedKeys.includes(key))) fail("keys");
    for (const key of keys) {
        const descriptor = Object.getOwnPropertyDescriptor(value, key);
        if (!descriptor.enumerable || !Object.hasOwn(descriptor, "value")) fail("shape");
        if (scalar && !["string", "number", "boolean"].includes(typeof descriptor.value)) fail("shape");
    }
}

/**
 * Parse a bounded JSON envelope without executing accessors or accepting duplicate
 * property names. JSON syntax is delegated to the built-in parser; this scan only
 * constrains depth, arrays and duplicate keys before parsing.
 */
export function parseRequestJson(json) {
    if (typeof json !== "string") fail("json");
    if (Buffer.byteLength(json, "utf8") > MAX_INPUT_BYTES) fail("size");
    try {
        const objects = [];
        const tokens = [...json.matchAll(/"(?:[^"\\]|\\[\s\S])*"|[{}[\]:,]/g)];
        for (let index = 0; index < tokens.length; index++) {
            const token = tokens[index][0];
            if (token === "[" || token === "]") fail("shape");
            if (token === "{") {
                objects.push(new Set());
                if (objects.length > 2) fail("shape");
            } else if (token === "}") {
                objects.pop();
            } else if (token.startsWith('"') && tokens[index + 1]?.[0] === ":") {
                const key = JSON.parse(token);
                const keys = objects.at(-1);
                if (keys?.has(key)) fail("json");
                keys?.add(key);
            }
        }
        const request = JSON.parse(json);
        record(request, ["parameters", "accountContext"]);
        if (!Object.hasOwn(request, "parameters") || !Object.hasOwn(request, "accountContext")) fail("required");
        record(request.parameters, Object.keys(rules), true);
        record(request.accountContext, ["subscriptionId", "tenantId"], true);
        return request;
    } catch (error) {
        if (error instanceof PreflightError) throw error;
        fail("json");
    }
}

/**
 * Validate explicit template values against explicit account context and return
 * only an ARM parameters document. This checks input, never approval or identity
 * existence. Callers must obtain account context from the authenticated job.
 */
export function buildArmParameters(parameters, accountContext) {
    record(parameters, Object.keys(rules), true);
    record(accountContext, ["subscriptionId", "tenantId"], true);
    if (Buffer.byteLength(JSON.stringify({ parameters, accountContext }), "utf8") > MAX_INPUT_BYTES) fail("size");
    for (const key of ["subscriptionId", "tenantId"]) {
        if (!Object.hasOwn(accountContext, key)) fail("required");
        guid(accountContext[key]);
    }
    const values = {};
    for (const [key, rule] of Object.entries(rules)) {
        if (!Object.hasOwn(parameters, key) && !Object.hasOwn(rule, "defaultValue")) fail("required");
        const value = Object.hasOwn(parameters, key) ? parameters[key] : rule.defaultValue;
        rule.check(value);
        values[key] = { value };
    }
    if (parameters.workforceTenantId.toLowerCase() !== accountContext.tenantId.toLowerCase()) fail("tenant");
    const network = cidr(parameters.virtualNetworkAddressPrefix);
    const application = cidr(parameters.applicationSubnetAddressPrefix);
    const endpoints = cidr(parameters.privateEndpointSubnetAddressPrefix);
    if ([application, endpoints].some(subnet => subnet.start < network.start || subnet.end > network.end)) fail("containment");
    if (application.start <= endpoints.end && endpoints.start <= application.end) fail("overlap");
    return {
        $schema: "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
        contentVersion: "1.0.0.0",
        parameters: values,
    };
}
