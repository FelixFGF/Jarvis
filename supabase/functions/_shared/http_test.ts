import { canonicalInvite, discordIdentityFromAuthUser, hashInviteCode, hex, normalizeInvite, randomInvite, sha256 } from "./http.ts";

Deno.test("an invitation created by the generator hashes identically at redemption", async () => {
  const code = randomInvite();
  const creationHash = hex(await hashInviteCode(code));
  const redemptionHash = hex(await hashInviteCode(code));
  if (creationHash !== redemptionHash) throw new Error("Creation and redemption hashes differ");
  if (canonicalInvite(code) !== code) throw new Error("Generated code was not already canonical");
  if (hex(await sha256(code)) === hex(await sha256(normalizeInvite(code)))) {
    throw new Error("The regression fixture did not demonstrate the old separator mismatch");
  }
});

Deno.test("redemption accepts case and whitespace variants but canonicalizes to the stored representation", async () => {
  const code = randomInvite();
  const variants = [code.toLowerCase(), ` ${code} `, code.replaceAll("-", " "), `\n${code.toLowerCase()}\t`];
  const expected = hex(await hashInviteCode(code));
  for (const variant of variants) {
    if (canonicalInvite(variant) !== code || hex(await hashInviteCode(variant)) !== expected) {
      throw new Error("A supported presentation variant produced a different invitation hash");
    }
  }
});

Deno.test("redemption rejects malformed codes rather than stripping arbitrary punctuation", async () => {
  for (const code of ["", "JRV-TOO-SHORT", `JRV@${"A".repeat(32)}`]) {
    let rejected = false;
    try { await hashInviteCode(code); } catch { rejected = true; }
    if (!rejected) throw new Error("Malformed invitation code was accepted");
  }
});

Deno.test("uses Discord provider_id when identity_data is absent; never substitutes Supabase UUID", () => {
  const resolved = discordIdentityFromAuthUser({
    id: "11111111-1111-4111-8111-111111111111",
    identities: [{ provider: "discord", provider_id: "123456789012345678" }],
  });
  if (resolved?.id !== "123456789012345678") throw new Error("Discord provider ID was not extracted");
});

Deno.test("uses the string provider ID and profile metadata", () => {
  const resolved = discordIdentityFromAuthUser({
    id: "11111111-1111-4111-8111-111111111111",
    identities: [{ provider: "discord", provider_id: "123456789012345678", identity_data: { username: "felix", global_name: "Felix FGF", avatar: "hash_123" } }],
  });
  if (resolved?.id !== "123456789012345678" || resolved.username !== "felix" || resolved.globalName !== "Felix FGF" || resolved.avatar !== "hash_123") throw new Error("Discord profile fields were not normalized");
});

Deno.test("rejects missing, malformed, and non-Discord identities without falling back to Auth UUID", () => {
  const uuid = "11111111-1111-4111-8111-111111111111";
  for (const user of [
    { id: uuid, identities: [] },
    { id: uuid, identities: [{ provider: "discord", identity_data: { sub: uuid } }] },
    { id: uuid, identities: [{ provider: "google", provider_id: "123456789012345678" }] },
  ]) {
    if (discordIdentityFromAuthUser(user) !== undefined) throw new Error("Untrusted identity fallback was accepted");
  }
});
