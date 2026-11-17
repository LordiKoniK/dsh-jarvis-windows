import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { QuestionQueue } from '../src/questions.ts';

function setup() {
  const managed = new Set(['a', 'b']);
  const questions = new QuestionQueue({ managed: id => managed.has(id) });
  return { questions, managed };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}
async function flush() { for (let i = 0; i < 10; i++) await Promise.resolve(); }

describe('等价类', () => {
  it('提问按 FIFO 串行', async () => {
    const { questions } = setup();
    const first = deferred<string>();
    const started: number[] = [];
    const a = questions.ask(() => { started.push(1); return first.promise; });
    const b = questions.ask(async () => { started.push(2); return '二'; });
    const c = questions.ask(async () => { started.push(3); return '三'; });
    await flush();
    assert.deepEqual(started, [1]);
    first.resolve('一');
    assert.deepEqual(await Promise.all([a, b, c]), ['一', '二', '三']);
    assert.deepEqual(started, [1, 2, 3]);
  });
});

describe('边界值', () => {
  it('移出托管的排队问题丢弃，后续问题继续', async () => {
    const { questions, managed } = setup();
    const hold = deferred<string>();
    const a = questions.ask(() => hold.promise);
    let called = false;
    const b = questions.ask(async () => { called = true; return '旧问题'; }, { session: 'a' });
    managed.delete('a');
    hold.resolve('回答');
    assert.deepEqual(await Promise.all([a, b]), ['回答', undefined]);
    assert.equal(called, false);
  });
  it('显式移出会清除排队问题，重新托管不恢复旧问题', async () => {
    const { questions } = setup();
    const hold = deferred<string>();
    const a = questions.ask(() => hold.promise);
    await flush();
    const b = questions.ask(async () => assert.fail('旧问题不应执行'), { session: 'a' });
    questions.dropSession('a');
    assert.equal(await b, undefined);
    hold.resolve('完成');
    await a;
  });
  it('卸载清除排队问题，后续调用无操作', async () => {
    const { questions } = setup();
    const hold = deferred<string>();
    const a = questions.ask(() => hold.promise);
    await flush();
    const b = questions.ask(async () => assert.fail('卸载不启动问题'));
    questions.dispose();
    assert.equal(await b, undefined);
    assert.equal(await questions.ask(async () => '卸载后'), undefined);
    hold.resolve('已在问的答案');
    await a;
  });
});

describe('异常路径', () => {
  it('同步抛错和异步拒绝均不阻塞后续提问', async () => {
    const { questions } = setup();
    const a = questions.ask(() => { throw new Error('sync'); });
    const b = questions.ask(async () => { throw new Error('async'); });
    const c = questions.ask(async () => '成功');
    await assert.rejects(a, /sync/);
    await assert.rejects(b, /async/);
    assert.equal(await c, '成功');
  });
  it('排队取消后不提问，也不阻塞下一项', async () => {
    const { questions } = setup();
    const hold = deferred<string>();
    const a = questions.ask(() => hold.promise);
    const controller = new AbortController();
    const b = questions.ask(async () => assert.fail('已取消'), { signal: controller.signal });
    controller.abort();
    assert.equal(await b, undefined);
    hold.resolve('第一项');
    await a;
    assert.equal(await questions.ask(async () => '下一项'), '下一项');
  });
});
