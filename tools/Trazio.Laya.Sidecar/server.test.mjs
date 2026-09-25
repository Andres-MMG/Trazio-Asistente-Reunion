import test from "node:test";
import assert from "node:assert/strict";
import { once } from "node:events";
import { allEvidenceFits, createSidecar, validEnvelope } from "./server.mjs";

const token = "t".repeat(32);
const question = {
  preference: {
    type: "choice",
    instructions: "Choose the faithful text.",
    criteria: { keep_original: "Original", human_review: "Ask a person", proposal: "Proposal" },
  },
};
const encode = text => text.trim().split(/\s+/).filter(Boolean).map((_, index) => index);

test("token preflight rejects state and question truncation before inference", () => {
  assert.equal(validEnvelope({ state: "short state", questions: question }), true);
  assert.equal(validEnvelope({ state: "sensitive [MASK] marker", questions: question }), false);
  assert.equal(allEvidenceFits("short state", question, encode, { max_len: 80, head_max_len: 50 }), true);
  assert.equal(allEvidenceFits("context ".repeat(200), question, encode, { max_len: 80, head_max_len: 50 }), false);
  assert.equal(allEvidenceFits("short state", question, encode, { max_len: 80, head_max_len: 4 }), false);
  const longOption = { preference: { ...question.preference,
    criteria: { keep_original: "word ".repeat(60), human_review: "Ask", proposal: "Proposal" } } };
  assert.equal(allEvidenceFits("short state", longOption, encode, { max_len: 500, head_max_len: 400 }), false);
});

test("sidecar authenticates, rejects overlong evidence, distinguishes busy, and returns pinned version", async () => {
  let inferCalls = 0;
  let complete;
  const pending = new Promise(resolve => { complete = resolve; });
  const sidecar = createSidecar({ token, modelVersion: "local-model-v1" }, async () => ({
    model: {
      systemOne: async () => { inferCalls++; await pending; return { model: "laya", answers: {}, usage: {} }; },
      close: async () => {},
    },
    encode,
    config: { max_len: 80, head_max_len: 50 },
  }));
  await sidecar.loadPromise;
  sidecar.server.listen(0, "127.0.0.1");
  await once(sidecar.server, "listening");
  const base = `http://127.0.0.1:${sidecar.server.address().port}`;
  const post = (state, auth = token) => fetch(base + "/v1/system-one", {
    method: "POST",
    headers: { authorization: `Bearer ${auth}`, "content-type": "application/json" },
    body: JSON.stringify({ state, questions: question }),
  });
  try {
    const unauthorized = await post("short", "wrong");
    assert.equal(unauthorized.status, 401);
    const overflow = await post("context ".repeat(200));
    assert.equal(overflow.status, 422);
    assert.equal((await overflow.json()).error, "context_overflow");
    assert.equal(inferCalls, 0);
    const first = post("short state");
    await new Promise(resolve => setTimeout(resolve, 30));
    assert.equal(inferCalls, 1);
    const second = await post("short state");
    assert.equal(second.status, 429);
    assert.equal((await second.json()).error, "busy");
    complete();
    const result = await first;
    assert.equal(result.status, 200);
    assert.equal((await result.json()).model_version, "local-model-v1");
  } finally {
    complete();
    await sidecar.close();
  }
});

test("failed local bundle is distinct from loading and never runs inference", async () => {
  const sidecar = createSidecar({ token, modelVersion: "local-model-v1" }, async () => {
    throw new Error("private model directory must never appear in HTTP");
  });
  await sidecar.loadPromise;
  sidecar.server.listen(0, "127.0.0.1");
  await once(sidecar.server, "listening");
  try {
    const base = `http://127.0.0.1:${sidecar.server.address().port}`;
    const health = await fetch(base + "/health", { headers: { authorization: `Bearer ${token}` } });
    assert.equal(health.status, 500);
    assert.deepEqual(await health.json(), { status: "model_unavailable" });
    const post = await fetch(base + "/v1/system-one", {
      method: "POST", headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
      body: JSON.stringify({ state: "short", questions: question }),
    });
    assert.equal(post.status, 500);
    assert.deepEqual(await post.json(), { error: "model_unavailable" });
  } finally {
    await sidecar.close();
  }
});