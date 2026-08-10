import { DurableObject } from "cloudflare:workers";

interface Env { TABLES: DurableObjectNamespace<TableRoom>; }
interface SocketAttachment { role: "host" | "player"; clientId: string; windowStart: number; messages: number; }

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), {
  status,
  headers: { "content-type": "application/json", "cache-control": "no-store" },
});

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/health") return json({ ok: true, protocol: 1 });
    if (url.pathname === "/create" && request.method === "POST") {
      const code = randomCode();
      const hostToken = crypto.randomUUID();
      const room = env.TABLES.getByName(code);
      await room.fetch(new Request("https://room/initialize", { method: "POST", body: hostToken }));
      return json({ code, hostToken });
    }
    const match = url.pathname.match(/^\/room\/([A-Z0-9]{6})$/i);
    if (!match) return json({ error: "not_found" }, 404);
    return env.TABLES.getByName(match[1].toUpperCase()).fetch(request);
  },
};

export class TableRoom extends DurableObject<Env> {
  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/initialize" && request.method === "POST") {
      if (await this.ctx.storage.get("hostToken")) return json({ error: "exists" }, 409);
      await this.ctx.storage.put("hostToken", await request.text());
      await this.ctx.storage.setAlarm(Date.now() + 6 * 60 * 60 * 1000);
      return json({ ok: true });
    }
    if (request.headers.get("Upgrade") !== "websocket") return json({ error: "upgrade_required" }, 426);
    if (!(await this.ctx.storage.get("hostToken"))) return json({ error: "unknown_room" }, 404);
    if (this.ctx.getWebSockets().length >= 8) return json({ error: "room_full" }, 409);

    const role = url.searchParams.get("role") === "host" ? "host" : "player";
    const clientId = url.searchParams.get("clientId")?.slice(0, 80) || crypto.randomUUID();
    if (role === "host") {
      const expected = await this.ctx.storage.get<string>("hostToken");
      if (url.searchParams.get("token") !== expected) return json({ error: "invalid_host_token" }, 403);
      if (this.ctx.getWebSockets("host").length > 0) return json({ error: "host_connected" }, 409);
    }

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    const attachment: SocketAttachment = { role, clientId, windowStart: Date.now(), messages: 0 };
    server.serializeAttachment(attachment);
    this.ctx.acceptWebSocket(server, [role]);
    server.send(JSON.stringify({ kind: "connected", clientId, protocol: 1 }));
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(socket: WebSocket, data: string | ArrayBuffer): Promise<void> {
    const attachment = socket.deserializeAttachment() as SocketAttachment;
    const size = typeof data === "string" ? new TextEncoder().encode(data).length : data.byteLength;
    if (size > 65_536) return socket.close(1009, "message_too_large");
    const now = Date.now();
    if (now - attachment.windowStart > 10_000) { attachment.windowStart = now; attachment.messages = 0; }
    if (++attachment.messages > 60) return socket.close(1008, "rate_limited");
    socket.serializeAttachment(attachment);

    if (attachment.role === "host") {
      let recipientId = "";
      try { recipientId = JSON.parse(typeof data === "string" ? data : new TextDecoder().decode(data)).recipientId || ""; }
      catch { return; }
      for (const peer of this.ctx.getWebSockets("player")) {
        const peerInfo = peer.deserializeAttachment() as SocketAttachment;
        if (!recipientId || peerInfo.clientId === recipientId) peer.send(data);
      }
    } else {
      const host = this.ctx.getWebSockets("host")[0];
      if (host) host.send(data);
    }
  }

  async webSocketClose(): Promise<void> {}
  async webSocketError(): Promise<void> {}
  async alarm(): Promise<void> {
    for (const socket of this.ctx.getWebSockets()) socket.close(1001, "room_expired");
    await this.ctx.storage.deleteAll();
  }
}

function randomCode(): string {
  const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  const bytes = crypto.getRandomValues(new Uint8Array(6));
  return Array.from(bytes, byte => alphabet[byte % alphabet.length]).join("");
}
