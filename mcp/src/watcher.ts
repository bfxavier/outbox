import { EventSource } from 'eventsource';
import notifier from 'node-notifier';
import { OutboxClient } from './client.js';
import { writeCache, type Cache } from './cache.js';

export function startWatcher(client: OutboxClient): void {
  reconcile(client).catch((e) => warn('initial reconcile failed', e));

  let backoff = 1000;
  let es: EventSource | null = null;
  let stopped = false;

  const connect = () => {
    if (stopped) return;
    es = new EventSource(`${client.relayUrl}/v1/stream`, {
      fetch: (input, init) =>
        fetch(input, {
          ...init,
          headers: { ...(init?.headers ?? {}), Authorization: `Bearer ${client.token}` },
        }),
    });

    es.addEventListener('open', () => {
      backoff = 1000;
      reconcile(client).catch((e) => warn('reconcile on open failed', e));
    });

    es.addEventListener('new', (ev: MessageEvent) => {
      try {
        const data = JSON.parse(ev.data) as { id: string; from: string; subject: string };
        try {
          notifier.notify({
            title: `Outbox: @${data.from}`,
            message: data.subject,
            sound: false,
            wait: false,
          });
        } catch (e) {
          warn('desktop notify failed', e);
        }
        reconcile(client).catch((e) => warn('reconcile on new failed', e));
      } catch (e) {
        warn('parse event failed', e);
      }
    });

    es.addEventListener('error', () => {
      es?.close();
      es = null;
      if (stopped) return;
      const delay = Math.min(backoff, 30_000);
      backoff = Math.min(backoff * 2, 30_000);
      setTimeout(connect, delay);
    });
  };

  connect();

  process.on('SIGTERM', () => { stopped = true; es?.close(); });
  process.on('SIGINT', () => { stopped = true; es?.close(); });
}

async function reconcile(client: OutboxClient): Promise<void> {
  const items = await client.inbox({ unread_only: true, limit: 5 });
  const cache: Cache = {
    unread_count: items.length,
    latest: items.slice(0, 5).map((i) => ({ from: i.from, subject: i.subject, id: i.id })),
    updated_at: new Date().toISOString(),
  };
  writeCache(cache);
}

function warn(label: string, err: unknown): void {
  const msg = err instanceof Error ? err.message : String(err);
  process.stderr.write(`[outbox-mcp] ${label}: ${msg}\n`);
}
