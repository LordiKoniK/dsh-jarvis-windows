import { appendFileSync, mkdirSync, renameSync, statSync } from 'node:fs';
import { dirname } from 'node:path';
import { shouldRotate } from './maintenance.ts';

export class RotatingLog {
  private path: string;
  private writes = 0;

  constructor(path: string) { this.path = path; }

  write(line: string): void {
    try {
      mkdirSync(dirname(this.path), { recursive: true, mode: 0o700 });
      appendFileSync(this.path, line);
      this.writes = (this.writes + 1) % 100;
      if (this.writes === 0 && shouldRotate(statSync(this.path).size)) {
        renameSync(this.path, this.path + '.1');
      }
    } catch { /* Diagnostics must never interrupt the plugin. */ }
  }
}
