import { afterEach, beforeEach, describe, it, mock } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import childProcess from 'node:child_process';
import { syncBuiltinESMExports } from 'node:module';
import { EventEmitter } from 'node:events';
import { apply } from '../src/index.ts';

let dir: string;
let dispose: (() => void) | void;
let created: { sessionId: string; agentOptions: { provider: string; model: string } }[];

function start(services: Record<string, any>, config: Record<string, unknown>): void {
  const context: any = {
    get: (name: string) => services[name],
    inject: (names: string[], callback: (ctx: any) => void) => {
      if (names.every(name => name in services)) callback(context);
    },
    provide: () => {},
    on: () => {},
    logger: { warn: () => {} },
  };
  dispose = apply(context, {
    audioDir: dir, memoryRoot: dir, lockFile: join(dir, 'lock.json'),
    managedFile: join(dir, 'managed.json'), tasksFile: join(dir, 'tasks.json'),
    runtimeFile: join(dir, 'runtime.json'),
    ...config,
  });
}

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'jv-agent-model-'));
  created = [];
  mock.method(childProcess, 'spawn', () => Object.assign(new EventEmitter(), {
    unref() {}, kill() {}, killed: false,
  }));
  syncBuiltinESMExports();
});

afterEach(() => {
  dispose?.();
  rmSync(dir, { recursive: true, force: true });
});

describe('agent model auto-resolution', () => {
  it('empty provider and model resolve to the first adapter and its first model', async () => {
    start({
      agents: {
        get: () => undefined,
        create: async (opts: any) => {
          created.push({ sessionId: opts.sessionId, agentOptions: opts.agentOptions });
          return { agent: { followup: () => {} }, dispose: () => {} };
        },
        resume: () => Promise.reject(new Error('should not resume')),
      },
      llm: {
        adapters: new Map([['zhipu', {}], ['bailian', {}]]),
        listModels: (provider: string) => provider === 'zhipu' ? [{ id: 'glm-9' }, { id: 'glm-9-air' }] : [],
      },
    }, {});
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(created.length, 1);
    assert.equal(created[0].agentOptions.provider, 'zhipu');
    assert.equal(created[0].agentOptions.model, 'glm-9');
  });

  it('explicit provider and model are kept as-is', async () => {
    start({
      agents: {
        get: () => undefined,
        create: async (opts: any) => {
          created.push({ sessionId: opts.sessionId, agentOptions: opts.agentOptions });
          return { agent: { followup: () => {} }, dispose: () => {} };
        },
        resume: () => Promise.reject(new Error('should not resume')),
      },
      llm: {
        adapters: new Map([['zhipu', {}]]),
        listModels: () => [{ id: 'glm-9' }],
      },
    }, { provider: 'openai', model: 'gpt-x' });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(created.length, 1);
    assert.equal(created[0].agentOptions.provider, 'openai');
    assert.equal(created[0].agentOptions.model, 'gpt-x');
  });

  it('model alone auto-fills from the explicit provider', async () => {
    start({
      agents: {
        get: () => undefined,
        create: async (opts: any) => {
          created.push({ sessionId: opts.sessionId, agentOptions: opts.agentOptions });
          return { agent: { followup: () => {} }, dispose: () => {} };
        },
        resume: () => Promise.reject(new Error('should not resume')),
      },
      llm: {
        adapters: new Map([['zhipu', {}]]),
        listModels: (provider: string) => provider === 'bailian' ? [{ id: 'qwen-max' }] : [],
      },
    }, { provider: 'bailian' });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(created.length, 1);
    assert.equal(created[0].agentOptions.provider, 'bailian');
    assert.equal(created[0].agentOptions.model, 'qwen-max');
  });

  it('an unset reasoning effort is not passed at all, so DSH resolves its own default', async () => {
    start({
      agents: {
        get: () => undefined,
        create: async (opts: any) => {
          created.push({ sessionId: opts.sessionId, agentOptions: opts.agentOptions });
          return { agent: { followup: () => {} }, dispose: () => {} };
        },
        resume: () => Promise.reject(new Error('should not resume')),
      },
      llm: { adapters: new Map([['zhipu', {}]]), listModels: () => [{ id: 'glm-9' }] },
    }, { provider: 'zhipu', model: 'glm-9' });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(created.length, 1);
    assert.deepEqual(created[0].agentOptions, { provider: 'zhipu', model: 'glm-9' });
    // Not merely empty: the key must be absent, because an empty string is a
    // different request from no request.
    assert.ok(!('reasoningEffort' in created[0].agentOptions));
  });

  it('a stored session is resumed instead of created, and carries the same options', async () => {
    const resumeOpts: any[] = [];
    start({
      agents: {
        get: () => undefined,
        create: () => Promise.reject(new Error('should not create: the session is stored')),
        resume: async (opts: any) => {
          resumeOpts.push(opts.agentOptions);
          return { agent: { followup: () => {} }, dispose: () => {} };
        },
      },
      // A live session is what makes the resolve path choose resume.
      sessions: { get: (id: string) => id === 'jarvis' ? { id } : undefined },
      llm: { adapters: new Map([['zhipu', {}]]), listModels: () => [{ id: 'glm-9' }] },
    }, { provider: 'zhipu', model: 'glm-9', reasoningEffort: ' high ' });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(resumeOpts.length, 1, 'a stored session must be resumed');
    assert.deepEqual(resumeOpts[0], { provider: 'zhipu', model: 'glm-9', reasoningEffort: 'high' });
    assert.equal(created.length, 0);
  });

  it('configured reasoning effort reaches create, trimmed', async () => {
    const createOpts: any[] = [];
    start({
      agents: {
        get: () => undefined,
        create: async (opts: any) => {
          createOpts.push(opts.agentOptions);
          return { agent: { followup: () => {} }, dispose: () => {} };
        },
        resume: () => Promise.reject(new Error('should not resume')),
      },
      llm: { adapters: new Map([['zhipu', {}]]), listModels: () => [{ id: 'glm-9' }] },
    }, { provider: 'zhipu', model: 'glm-9', reasoningEffort: ' high ' });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(createOpts.length, 1);
    assert.deepEqual(createOpts[0], { provider: 'zhipu', model: 'glm-9', reasoningEffort: 'high' });
  });

  it('a failed creation over a stored session falls back to resume', async () => {
    const resumeOpts: any[] = [];
    start({
      agents: {
        get: () => undefined,
        // No live session, so resolve tries create; DSH refuses an id whose
        // stored history survived a failed creation, and resume is the retry.
        create: () => Promise.reject(new Error('session "jarvis" already exists')),
        resume: async (opts: any) => {
          resumeOpts.push(opts.agentOptions);
          return { agent: { followup: () => {} }, dispose: () => {} };
        },
      },
      llm: { adapters: new Map([['zhipu', {}]]), listModels: () => [{ id: 'glm-9' }] },
    }, { provider: 'zhipu', model: 'glm-9' });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(resumeOpts.length, 1);
    assert.deepEqual(resumeOpts[0], { provider: 'zhipu', model: 'glm-9' });
  });

  it('any other creation failure is reported instead of retried', async () => {
    let resumed = false;
    start({
      agents: {
        get: () => undefined,
        create: () => Promise.reject(new Error('provider unavailable')),
        resume: () => { resumed = true; return Promise.resolve({ agent: {} }); },
      },
      llm: { adapters: new Map([['zhipu', {}]]), listModels: () => [{ id: 'glm-9' }] },
    }, { provider: 'zhipu', model: 'glm-9' });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(resumed, false);
  });
});
