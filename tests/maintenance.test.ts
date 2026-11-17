import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { shouldRotate } from '../src/maintenance.ts';

describe('等价类', () => {
  it('日志仅在超过上限时轮转', () => {
    assert.equal(shouldRotate(2_000_000, 1_048_576), true);
    assert.equal(shouldRotate(500_000, 1_048_576), false);
  });
});

describe('边界值', () => {
  it('日志恰好1MB保留，多1字节轮转', () => {
    assert.equal(shouldRotate(1_048_576, 1_048_576), false);
    assert.equal(shouldRotate(1_048_577, 1_048_576), true);
    assert.equal(shouldRotate(0, 1_048_576), false);
  });
});

describe('异常路径', () => {
  it('非法大小或上限不会轮转', () => {
    for (const value of [NaN, Infinity, -1]) {
      assert.equal(shouldRotate(value, 100), false);
      assert.equal(shouldRotate(200, value), false);
    }
  });
});
