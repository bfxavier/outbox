import type { Config } from './config.js';

export interface InboxItem {
  id: string;
  from: string;
  to: string;
  subject: string;
  created_at: string;
  read: boolean;
}

export interface FullMessage {
  id: string;
  from: string;
  to: string;
  subject: string;
  body: string;
  metadata: unknown;
  created_at: string;
  read_at: string | null;
}

export class OutboxClient {
  constructor(private cfg: Config) {}

  get handle(): string { return this.cfg.handle; }
  get relayUrl(): string { return this.cfg.relay_url; }
  get token(): string { return this.cfg.token; }

  private headers(): Record<string, string> {
    return {
      Authorization: `Bearer ${this.cfg.token}`,
      'Content-Type': 'application/json',
    };
  }

  async send(req: { to: string; subject: string; body: string; metadata?: unknown }): Promise<{ id: string }> {
    const to = req.to.replace(/^@/, '').toLowerCase();
    const res = await fetch(`${this.cfg.relay_url}/v1/messages`, {
      method: 'POST',
      headers: this.headers(),
      body: JSON.stringify({ to, subject: req.subject, body: req.body, metadata: req.metadata ?? null }),
    });
    if (!res.ok) throw new Error(`send failed (${res.status}): ${await res.text()}`);
    return await res.json() as { id: string };
  }

  async inbox(opts: { unread_only?: boolean; limit?: number; since?: string } = {}): Promise<InboxItem[]> {
    const params = new URLSearchParams();
    if (opts.unread_only) params.set('unread', 'true');
    if (opts.limit) params.set('limit', String(opts.limit));
    if (opts.since) params.set('since', opts.since);
    const res = await fetch(`${this.cfg.relay_url}/v1/inbox?${params}`, { headers: this.headers() });
    if (!res.ok) throw new Error(`inbox failed (${res.status}): ${await res.text()}`);
    return await res.json() as InboxItem[];
  }

  async read(id: string): Promise<FullMessage> {
    const res = await fetch(`${this.cfg.relay_url}/v1/messages/${encodeURIComponent(id)}`, { headers: this.headers() });
    if (!res.ok) throw new Error(`read failed (${res.status}): ${await res.text()}`);
    return await res.json() as FullMessage;
  }

  async ack(id: string): Promise<void> {
    const res = await fetch(`${this.cfg.relay_url}/v1/messages/${encodeURIComponent(id)}/ack`, {
      method: 'POST',
      headers: this.headers(),
    });
    if (!res.ok) throw new Error(`ack failed (${res.status}): ${await res.text()}`);
  }

  async ping(): Promise<boolean> {
    try {
      const res = await fetch(`${this.cfg.relay_url}/v1/inbox?limit=1`, { headers: this.headers() });
      return res.ok;
    } catch {
      return false;
    }
  }
}
