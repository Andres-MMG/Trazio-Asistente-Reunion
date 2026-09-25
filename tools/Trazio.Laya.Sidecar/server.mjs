import http from "node:http";
import { timingSafeEqual } from "node:crypto";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const host = "127.0.0.1";
const maximumBodyBytes = 512 * 1024;
const maximumStateBytes = 16 * 1024;
const maximumQuestions = 7;
const bundleFiles = [
  "laya.onnx", "laya.onnx.data", "laya_config.json",
  "tokenizer/tokenizer.json", "tokenizer/tokenizer_config.json",
];

function configuredEnvironment(env) {
  const port = Number(env.TRAZIO_LAYA_PORT ?? "48731");
  const token = env.TRAZIO_LAYA_TOKEN ?? "";
  const modelDir = env.LAYA_MODEL_DIR ?? "";
  const modelVersion = env.LAYA_MODEL_VERSION ?? "";
  const threads = Number(env.LAYA_THREADS ?? "4");
  if (!Number.isInteger(port) || port < 1 || port > 65535 ||
      token.length < 32 || token.length > 256 || /[\r\n]/.test(token) ||
      /\s/.test(token) || !path.isAbsolute(modelDir) || !modelVersion || modelVersion.length > 120 ||
      /[\r\n]/.test(modelVersion) || !Number.isInteger(threads) || threads < 1 || threads > 16)
    throw new Error("invalid_local_configuration");
  return { port, token, modelDir, modelVersion, threads };
}

function authorized(request, token) {
  const header = request.headers.authorization;
  if (typeof header !== "string" || !/^Bearer [^\s]+$/i.test(header)) return false;
  const actual = Buffer.from(header.slice(7));
  const expected = Buffer.from(token);
  try {
    return actual.length === expected.length && timingSafeEqual(actual, expected);
  } finally {
    actual.fill(0);
    expected.fill(0);
  }
}

function reply(response, status, body) {
  const payload = Buffer.from(JSON.stringify(body));
  response.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": payload.length,
    "cache-control": "no-store",
    "x-content-type-options": "nosniff",
  });
  response.end(payload);
}

async function readJson(request) {
  if (Number(request.headers["content-length"] ?? 0) > maximumBodyBytes)
    throw new Error("request_too_large");
  const chunks = [];
  let combined;
  let size = 0;
  try {
    for await (const chunk of request) {
      size += chunk.length;
      if (size > maximumBodyBytes) throw new Error("request_too_large");
      chunks.push(chunk);
    }
    combined = Buffer.concat(chunks);
    return JSON.parse(combined.toString("utf8"));
  } finally {
    for (const chunk of chunks) chunk.fill(0);
    combined?.fill(0);
  }
}

function validQuestions(questions) {
  if (!questions || typeof questions !== "object" || Array.isArray(questions)) return false;
  const entries = Object.entries(questions);
  if (entries.length < 1 || entries.length > maximumQuestions) return false;
  return entries.every(([id, question]) => {
    if (!id || id.length > 64 || !question || typeof question !== "object" ||
        typeof question.instructions !== "string" || question.instructions.length > 500) return false;
    if (question.type === "choice") {
      const criteria = question.criteria;
      if (!criteria || typeof criteria !== "object" || Array.isArray(criteria)) return false;
      const options = Object.entries(criteria);
      return options.length >= 2 && options.length <= 4 && options.every(([key, value]) =>
        key.length > 0 && key.length <= 64 && (value === null ||
          typeof value === "string" && value.length <= 120));
    }
    if (question.type === "noul") {
      const criteria = question.criteria;
      return criteria === undefined || criteria && typeof criteria === "object" &&
        typeof criteria.true === "string" && typeof criteria.false === "string" &&
        criteria.true.length <= 120 && criteria.false.length <= 120;
    }
    return false;
  });
}

export function validEnvelope(body) {
  return body && typeof body === "object" && !Array.isArray(body) &&
    typeof body.state === "string" && Buffer.byteLength(body.state, "utf8") <= maximumStateBytes &&
    !body.state.includes("[MASK]") &&
    validQuestions(body.questions);
}

/** Match @receptron/laya 0.1.2 sequence.ts; reject every path that would slice tokens. */
export function allEvidenceFits(state, questions, encode, config) {
  if (!validEnvelope({ state, questions }) ||
      !Number.isInteger(config?.max_len) || !Number.isInteger(config?.head_max_len)) return false;
  const scrub = text => text.split("[MASK]").join(" ");
  const stateTokens = encode(scrub(state)).length;
  for (const question of Object.values(questions)) {
    const options = question.type === "choice"
      ? Object.entries(question.criteria).map(([key, value]) => value ? `${key}: ${value}` : key)
      : [
          `false: ${question.criteria?.false || "no, the statement does not hold"}`,
          `true: ${question.criteria?.true || "yes, the statement holds"}`,
        ];
    const optionLengths = options.map(option => 1 + encode(" " + scrub(option)).length);
    if (optionLengths.some(length => length > 49)) return false;
    const optionsTotal = optionLengths.reduce((sum, value) => sum + value, 0);
    const headBudget = config.head_max_len - optionsTotal;
    if (headBudget < 16) return false;
    const headLength = encode(`${question.type} question: ${scrub(question.instructions)}`).length;
    if (headLength > Math.max(8, headBudget)) return false;
    const fullSequenceLength = 1 + headLength + 1 + optionsTotal + 1 + stateTokens + 1;
    if (fullSequenceLength > config.max_len) return false;
  }
  return true;
}

async function loadLocalModel(config) {
  for (const relative of bundleFiles) {
    const file = path.join(config.modelDir, relative);
    const info = await stat(file);
    if (!info.isFile() || info.size === 0) throw new Error("incomplete_local_bundle");
  }
  const [{ Laya }, { Tokenizer }] = await Promise.all([
    import("@receptron/laya"), import("@huggingface/tokenizers"),
  ]);
  const model = await Laya.load({
    modelDir: config.modelDir,
    executionProviders: ["cpu"],
    sessionOptions: { intraOpNumThreads: config.threads },
  });
  const tokenizer = new Tokenizer(
    JSON.parse(await readFile(path.join(config.modelDir, "tokenizer/tokenizer.json"), "utf8")),
    JSON.parse(await readFile(path.join(config.modelDir, "tokenizer/tokenizer_config.json"), "utf8")),
  );
  return {
    model,
    encode: text => tokenizer.encode(text, { add_special_tokens: false }).ids,
    config: model.config,
  };
}

export function createSidecar(config, loader = loadLocalModel) {
  let loaded;
  let loadFailed = false;
  let busy = false;
  const loadPromise = loader(config).then(value => { loaded = value; }).catch(() => { loadFailed = true; });
  const server = http.createServer(async (request, response) => {
    if (!authorized(request, config.token)) return reply(response, 401, { error: "unauthorized" });
    if (request.method === "GET" && request.url === "/health") {
      if (loadFailed) return reply(response, 500, { status: "model_unavailable" });
      if (!loaded) return reply(response, 503, { status: "loading" });
      return reply(response, 200, { status: "ready", model: "laya", model_version: config.modelVersion });
    }
    if (request.method !== "POST" || request.url !== "/v1/system-one")
      return reply(response, 404, { error: "not_found" });
    if (loadFailed) return reply(response, 500, { error: "model_unavailable" });
    if (!loaded) return reply(response, 503, { error: "loading" });
    if (busy) return reply(response, 429, { error: "busy" });
    busy = true;
    try {
      const body = await readJson(request);
      if (!validEnvelope(body)) return reply(response, 400, { error: "invalid_request" });
      let fits;
      try { fits = allEvidenceFits(body.state, body.questions, loaded.encode, loaded.config); }
      catch { return reply(response, 500, { error: "tokenizer_failed" }); }
      if (!fits) return reply(response, 422, { error: "context_overflow" });
      let result;
      try { result = await loaded.model.systemOne(body.state, body.questions); }
      catch { return reply(response, 500, { error: "inference_failed" }); }
      return reply(response, 200, { ...result, model_version: config.modelVersion });
    } catch (error) {
      if (error?.message === "request_too_large")
        return reply(response, 413, { error: "request_too_large" });
      return reply(response, 400, { error: "invalid_request" });
    } finally {
      busy = false;
    }
  });
  return { server, loadPromise, close: async () => {
    await loadPromise;
    await new Promise(resolve => server.close(resolve));
    if (loaded?.model) await loaded.model.close();
  } };
}

async function main() {
  let config;
  try { config = configuredEnvironment(process.env); }
  catch { process.stderr.write("Invalid local Laya configuration.\n"); process.exitCode = 2; return; }
  const sidecar = createSidecar(config);
  sidecar.server.listen(config.port, host);
  const shutdown = async () => { await sidecar.close(); process.exitCode = 0; };
  process.once("SIGINT", shutdown);
  process.once("SIGTERM", shutdown);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1]))
  await main();
