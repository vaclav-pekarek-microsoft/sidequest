import { buildArmParameters, MAX_INPUT_BYTES, parseRequestJson, PreflightError } from "./parameter-validation.mjs";

const usage = "Usage: node infra\\deployment\\preflight.mjs < request.json > parameters.json\n"
    + "Reads { parameters, accountContext: { subscriptionId, tenantId } } as UTF-8 JSON from stdin.\n"
    + "Writes only disabled-application ARM parameters to stdout; errors go to stderr with exit code 1.\n"
    + "Offline input validation only: not approval, identity verification or deployment authorization.\n"
    + "Account context must come from the authenticated deployment job, never untrusted PR inputs.\n";

function report(error) {
    process.exitCode = 1;
    const safe = error instanceof PreflightError ? error : new PreflightError("io");
    process.stderr.write(`${safe.message}\n`);
}

// Stream errors must not fall through to Node's raw, potentially sensitive diagnostics.
process.stdout.on("error", () => report(new PreflightError("io")));
process.stderr.on("error", () => { process.exitCode = 1; });

async function main() {
    const args = process.argv.slice(2);
    if (args.length === 1 && args[0] === "--help") {
        process.stdout.write(usage);
        return;
    }
    if (args.length !== 0) throw new PreflightError("usage");
    const chunks = [];
    let size = 0;
    for await (const chunk of process.stdin) {
        size += chunk.length;
        if (size > MAX_INPUT_BYTES) throw new PreflightError("size");
        chunks.push(chunk);
    }
    let json;
    try {
        json = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(Buffer.concat(chunks));
    } catch {
        throw new PreflightError("json");
    }
    const { parameters, accountContext } = parseRequestJson(json);
    const document = buildArmParameters(parameters, accountContext);
    process.stdout.write(`${JSON.stringify(document, null, 2)}\n`);
}

try {
    await main();
} catch (error) {
    process.stdin.destroy();
    report(error);
}
