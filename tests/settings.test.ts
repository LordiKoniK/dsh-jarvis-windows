import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { Config } from '../src/index.ts';
import { mergeSettings, type JarvisSettings } from '../src/settings.ts';
const defaults = Config({}) as JarvisSettings;

describe('等价类', () => {
  it('merges editable values, trims strings and preserves the base', () => {
    const merged = mergeSettings(defaults, { provider: ' custom ', model: ' test ', reasoningEffort: ' high ',
      judgeEnabled: false, judgeProvider: ' other ', judgeModel: ' judge ', judgeTimeoutMs: 30000, maxContinueRounds: 4 });
    assert.deepEqual(merged, { provider: 'custom', model: 'test', reasoningEffort: 'high',
      judgeEnabled: false, askInterception: false, autoApprove: 'off', judgeProvider: 'other', judgeModel: 'judge', judgeTimeoutMs: 30000, maxContinueRounds: 4 });
    assert.equal(defaults.judgeEnabled, true);
  });
  it('partial source falls back to composition, unknown/internal keys are excluded', () => {
    const base = { ...defaults, provider: 'my-provider', maxContinueRounds: 3 };
    const value = mergeSettings(base, { judgeModel: 'x', runtimeFile: '/wrong', unknownField: true });
    assert.equal(value.provider, 'my-provider'); assert.equal(value.maxContinueRounds, 3);
    assert.ok(!('runtimeFile' in value)); assert.ok(!('unknownField' in value));
  });
  it('empty judge overrides follow the main model', () => {
    const value = mergeSettings({ ...defaults, judgeProvider: 'x', judgeModel: 'y' },
      { judgeProvider: ' ', judgeModel: '' });
    assert.equal(value.judgeProvider, ''); assert.equal(value.judgeModel, '');
  });
  it('keys removed with the speech engine are ignored, not fatal', () => {
    const value = mergeSettings(defaults, { edgeVoice: 'en-GB-RyanNeural', managedNarration: 'relay', greetings: ['hi'] });
    assert.deepEqual(value, mergeSettings(defaults, {}));
  });
});
describe('边界值', () => {
  for (const rounds of [0, 10]) it(`accepts round limit ${rounds}`, () => {
    assert.equal(mergeSettings(defaults, { maxContinueRounds: rounds }).maxContinueRounds, rounds);
  });
  for (const timeout of [1000, 120000]) it(`accepts timeout ${timeout}`, () => {
    assert.equal(mergeSettings(defaults, { judgeTimeoutMs: timeout }).judgeTimeoutMs, timeout);
  });
  it('blank required strings fall back to base', () => {
    const value = mergeSettings(defaults, { provider: '', model: '  ', reasoningEffort: ' ' });
    assert.equal(value.provider, defaults.provider); assert.equal(value.model, defaults.model);
    // Empty effort must survive as empty: DSH resolves its own default, and an
    // explicit value here would silently change the agent.
    assert.equal(value.reasoningEffort, '');
  });
});
describe('异常路径', () => {
  for (const invalid of [-1, 11, 1.5, NaN, Infinity, '3', null]) it(`rejects invalid round limit ${invalid}`, () => {
    assert.equal(mergeSettings(defaults, { maxContinueRounds: invalid }).maxContinueRounds, 2);
  });
  for (const invalid of [999, 120001, 2000.5, NaN, '20000']) it(`rejects invalid timeout ${invalid}`, () => {
    assert.equal(mergeSettings(defaults, { judgeTimeoutMs: invalid }).judgeTimeoutMs, 20000);
  });
  it('malformed fields fall back without coercion', () => {
    const value = mergeSettings(defaults, { judgeEnabled: 'false', judgeModel: {}, edgeVoice: false });
    assert.equal(value.judgeEnabled, true); assert.equal(value.judgeModel, '');
  });
  for (const source of [null, undefined, [], 'broken']) it(`ignores malformed source ${source}`, () => {
    assert.deepEqual(mergeSettings(defaults, source), mergeSettings(defaults, {}));
  });
});
