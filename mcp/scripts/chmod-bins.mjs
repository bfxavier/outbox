import { chmodSync } from 'node:fs';
for (const f of ['dist/index.js', 'dist/cli.js']) {
  try { chmodSync(f, 0o755); } catch (e) { console.error(`chmod ${f}: ${e.message}`); }
}
