import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { configDir } from './config.js';

export interface Cache {
  unread_count: number;
  latest: { from: string; subject: string; id: string }[];
  updated_at: string;
}

function file(): string {
  return join(configDir(), 'cache.json');
}

export function readCache(): Cache | null {
  try {
    return JSON.parse(readFileSync(file(), 'utf8')) as Cache;
  } catch {
    return null;
  }
}

export function writeCache(c: Cache): void {
  mkdirSync(configDir(), { recursive: true });
  writeFileSync(file(), JSON.stringify(c, null, 2));
}
