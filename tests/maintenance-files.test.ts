import { afterEach, beforeEach, describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { RotatingLog } from '../src/maintenance-files.ts';

let dir: string;
beforeEach(() => { dir = mkdtempSync(join(tmpdir(), 'jv-maintenance-')); });
afterEach(() => { chmodSync(dir, 0o700); rmSync(dir, { recursive: true, force: true }); });

describe('等价类', () => {
  it('第100次写入后超限日志覆盖归档，下次写入创建新日志', () => {
    const path = join(dir, 'nested', 'debug.log');
    const log = new RotatingLog(path);
    log.write('x'.repeat(1_048_577));
    writeFileSync(path + '.1', 'old archive');
    for (let i = 1; i < 99; i++) log.write('line\n');
    assert.equal(readFileSync(path + '.1', 'utf8'), 'old archive');
    log.write('hundred\n');
    assert.equal(existsSync(path), false);
    assert.ok(readFileSync(path + '.1', 'utf8').endsWith('hundred\n'));
    log.write('new\n');
    assert.equal(readFileSync(path, 'utf8'), 'new\n');
  });
});

describe('边界值', () => {
  it('第100次总大小恰好1MB不轮转，下个100次检查才轮转', () => {
    const path = join(dir, 'debug.log');
    const log = new RotatingLog(path);
    log.write('x'.repeat(1_048_576));
    for (let i = 1; i < 100; i++) log.write('');
    assert.equal(existsSync(path + '.1'), false);
    log.write('x');
    for (let i = 101; i < 199; i++) log.write('');
    assert.equal(existsSync(path + '.1'), false);
    log.write('');
    assert.equal(existsSync(path + '.1'), true);
  });
});

describe('异常路径', () => {
  it('日志父路径是普通文件时写失败不抛出，恢复后继续写', () => {
    const parent = join(dir, 'parent');
    writeFileSync(parent, 'block');
    const log = new RotatingLog(join(parent, 'debug.log'));
    assert.doesNotThrow(() => log.write('lost'));
    rmSync(parent);
    log.write('ok');
    assert.equal(readFileSync(join(parent, 'debug.log'), 'utf8'), 'ok');
  });
  it('归档是非空目录时轮转失败保留当前日志，后续继续写', () => {
    const path = join(dir, 'debug.log');
    const log = new RotatingLog(path);
    mkdirSync(path + '.1');
    writeFileSync(join(path + '.1', 'block'), 'block');
    log.write('x'.repeat(1_048_577));
    for (let i = 1; i < 100; i++) assert.doesNotThrow(() => log.write('x'));
    log.write('still works');
    assert.ok(readFileSync(path, 'utf8').endsWith('still works'));
  });
});
