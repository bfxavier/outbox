import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

export interface Config {
  relay_url: string;
  handle: string;
  token: string;
}

export function configDir(): string {
  return process.env.OUTBOX_CONFIG_DIR ?? join(homedir(), '.outbox');
}

export function configPath(): string {
  return join(configDir(), 'config.json');
}

export function loadConfig(): Config {
  const file = configPath();
  let data: string;
  try {
    data = readFileSync(file, 'utf8');
  } catch {
    throw new Error(`Outbox config missing at ${file}. Run \`outbox setup\` first.`);
  }
  const cfg = JSON.parse(data) as Partial<Config>;
  for (const k of ['relay_url', 'handle', 'token'] as const) {
    if (!cfg[k]) throw new Error(`Outbox config at ${file} missing field: ${k}`);
  }
  return cfg as Config;
}
