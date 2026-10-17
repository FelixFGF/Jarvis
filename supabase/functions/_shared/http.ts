export const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, apikey, content-type, x-client-info",
  "Access-Control-Allow-Methods": "POST, GET, OPTIONS",
};

export function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { ...corsHeaders, "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" } });
}

export async function body(req: Request): Promise<Record<string, unknown>> {
  const raw = await req.text();
  if (raw.length > 16_384) throw new ApiError(413, "Request is too large.");
  try {
    const value = JSON.parse(raw);
    if (!value || Array.isArray(value) || typeof value !== "object") throw new Error();
    return value as Record<string, unknown>;
  } catch { throw new ApiError(400, "Invalid request body."); }
}

export class ApiError extends Error {
  public readonly status: number;
  constructor(status: number, message: string) { super(message); this.status = status; }
}

export function handleError(error: unknown): Response {
  if (error instanceof ApiError) return json(error.status, { ok: false, error: error.message });
  // Do not serialize upstream bodies or request data; they may contain credentials.
  console.error("Beta licensing operation failed", error instanceof Error ? error.name : "unknown");
  return json(500, { ok: false, error: "The licensing service could not complete the request." });
}

export function bearer(req: Request): string {
  const value = req.headers.get("authorization") ?? "";
  if (!value.startsWith("Bearer ") || value.length < 20) throw new ApiError(401, "Authentication required.");
  return value.slice(7);
}

export function requiredString(value: unknown, max: number, label: string): string {
  if (typeof value !== "string" || value.trim().length === 0 || value.length > max) throw new ApiError(400, `Invalid ${label}.`);
  return value.trim();
}

export function optionalExpiry(value: unknown): string | null {
  if (value === null || value === undefined || value === "") return null;
  if (typeof value !== "string") throw new ApiError(400, "Invalid expiration date.");
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed) || parsed <= Date.now() || parsed > Date.now() + 366 * 24 * 60 * 60 * 1000) throw new ApiError(400, "Expiration must be within the next 366 days.");
  return new Date(parsed).toISOString();
}

export function normalizeInvite(code: string): string {
  // Generated codes use hyphen-separated groups. Treat case and whitespace as
  // presentation only, but reject other punctuation instead of silently
  // transforming a potentially mistyped code into another code.
  return code.trim().toUpperCase().replace(/[\s-]/g, "");
}

export async function sha256(value: string): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value)));
}

export function canonicalInvite(code: string): string {
  const normalized = normalizeInvite(code);
  if (!/^JRV[A-Z0-9]{32}$/.test(normalized)) throw new ApiError(400, "Invitation could not be redeemed.");
  return `JRV-${normalized.slice(3).match(/.{4}/g)!.join("-")}`;
}

/** Hash exactly the canonical representation returned by randomInvite(). */
export async function hashInviteCode(code: string): Promise<Uint8Array> {
  return sha256(canonicalInvite(code));
}

export function hex(bytes: Uint8Array): string {
  return [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** Extracts only a Discord provider identity. Supabase user.id is a UUID and
 * must never be used as a Discord snowflake fallback. */
export function discordIdentityFromAuthUser(user: Record<string, unknown>): { id: string; username: string | null; globalName: string | null; avatar: string | null } | undefined {
  const identities = Array.isArray(user.identities) ? user.identities : [];
  const identity = identities.find((item: unknown) => item !== null && typeof item === "object" && (item as Record<string, unknown>).provider === "discord") as Record<string, unknown> | undefined;
  if (!identity) return undefined;
  const data = identity.identity_data && typeof identity.identity_data === "object" ? identity.identity_data as Record<string, unknown> : {};
  const idValue = identity.provider_id ?? data.sub ?? data.provider_id;
  // Discord snowflakes exceed JavaScript's safe integer range; accept strings only.
  const id = typeof idValue === "string" ? idValue.trim() : "";
  if (!/^[0-9]{17,20}$/.test(id)) return undefined;
  let avatar = typeof data.avatar === "string" ? data.avatar : null;
  if (!avatar || !/^[A-Za-z0-9_]{2,128}$/.test(avatar)) {
    const avatarUrl = typeof data.avatar_url === "string" ? data.avatar_url : "";
    const match = new RegExp(`^https://cdn\\.discordapp\\.com/avatars/${id}/([A-Za-z0-9_]{2,128})(?:\\.(?:png|jpe?g|webp|gif))?(?:\\?.*)?$`).exec(avatarUrl);
    avatar = match?.[1] ?? null;
  }
  return {
    id,
    username: typeof data.username === "string" ? data.username.slice(0, 100) : null,
    globalName: typeof data.global_name === "string" ? data.global_name.slice(0, 100) : typeof data.full_name === "string" ? data.full_name.slice(0, 100) : null,
    avatar: avatar && /^[A-Za-z0-9_]{2,128}$/.test(avatar) ? avatar : null,
  };
}

export function randomInvite(): string {
  const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  const picked: string[] = [];
  while (picked.length < 32) {
    for (const byte of crypto.getRandomValues(new Uint8Array(48))) {
      if (byte >= 224) continue; // 224 is the largest multiple of 32 below 256; avoids modulo bias.
      picked.push(alphabet[byte % alphabet.length]);
      if (picked.length === 32) break;
    }
  }
  const value = picked.join("");
  return `JRV-${value.match(/.{1,4}/g)!.join("-")}`;
}

export class SupabaseAdmin {
  readonly url: string;
  readonly key: string;
  readonly anon: string;
  constructor() {
    this.url = Deno.env.get("SUPABASE_URL") ?? "";
    this.key = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY") ?? "";
    this.anon = Deno.env.get("SUPABASE_ANON_KEY") ?? Deno.env.get("SUPABASE_PUBLISHABLE_KEY") ?? "";
    if (!this.url || !this.key || !this.anon) throw new ApiError(503, "Licensing service is not configured.");
  }

  async authenticatedUser(token: string): Promise<{ id: string; email?: string; discord?: { id: string; username: string | null; globalName: string | null; avatar: string | null } }> {
    const response = await fetch(`${this.url}/auth/v1/user`, { headers: { apikey: this.anon, authorization: `Bearer ${token}` } });
    if (!response.ok) throw new ApiError(401, "Authentication required.");
    const user = await response.json();
    if (typeof user.id !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(user.id)) throw new ApiError(401, "Authentication required.");
    const discord = discordIdentityFromAuthUser(user as Record<string, unknown>);
    return { id: user.id, email: typeof user.email === "string" ? user.email : undefined, discord };
  }

  async rpc<T>(name: string, args: unknown): Promise<T> {
    const response = await fetch(`${this.url}/rest/v1/rpc/${name}`, {
      method: "POST",
      headers: { apikey: this.key, authorization: `Bearer ${this.key}`, "content-type": "application/json" },
      body: JSON.stringify(args),
    });
    if (!response.ok) throw new Error(`database operation ${name} failed with status ${response.status}`);
    return await response.json() as T;
  }

  async rateLimit(identity: string, operation: string, limit: number, windowSeconds: number): Promise<void> {
    const digest = await sha256(`${operation}\0${identity}`);
    const allowed = await this.rpc<boolean>("beta_rate_limit", { p_bucket_hash: `\\x${hex(digest)}`, p_limit: limit, p_window_seconds: windowSeconds });
    if (!allowed) throw new ApiError(429, "Too many attempts. Please wait and try again.");
  }

  async rateLimitClient(req: Request, userId: string, operation: string, limit: number, windowSeconds: number): Promise<void> {
    await this.rateLimit(userId, `${operation}-account`, limit, windowSeconds);
    const forwarded = req.headers.get("cf-connecting-ip") ?? req.headers.get("x-real-ip") ?? req.headers.get("x-forwarded-for")?.split(",", 1)[0].trim();
    if (forwarded && forwarded.length <= 128) await this.rateLimit(forwarded, `${operation}-network`, limit * 5, windowSeconds);
  }

  async requireAdmin(userId: string): Promise<void> {
    await this.rateLimit(userId, "admin-authorization", 60, 60);
    const authorized = await this.rpc<boolean>("beta_is_admin", { p_user_id: userId });
    if (!authorized) {
      await this.rpc("beta_admin_audit_denied", { p_user_id: userId }).catch(() => undefined);
      throw new ApiError(403, "Administrator access required.");
    }
  }
}

export function preflight(req: Request): Response | null {
  if (req.method === "OPTIONS") return new Response("ok", { headers: corsHeaders });
  if (req.method !== "POST") return json(405, { ok: false, error: "Method not allowed." });
  return null;
}
