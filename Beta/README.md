# Jarvis Close Beta

The close-beta channel is separate from the public Jarvis 1.0.6 release. The normal `Release` configuration has no activation gate; the `Beta` configuration fails closed and requires a live Supabase license. The existing installer and public publish folder are not changed by the beta publish script.

## Architecture

- Jarvis and `BetaManager/Jarvis.BetaManager.csproj` are independent WPF applications. Both use `Beta.Shared` for Supabase Auth, Edge Function requests, and Windows DPAPI session storage.
- Clients contain only the Supabase project URL and public anon/publishable key. The service-role key is used only by Supabase Edge Functions and must never be added to a desktop build or installer.
- Licensing tables live in the non-exposed `beta_private` schema. RLS is enabled; `anon` and `authenticated` have no schema/table privileges. Public-schema `SECURITY DEFINER` RPCs are executable only by `service_role`; Edge Functions authenticate the user's access token and check the administrator table server-side before calling them.
- Invitation codes contain 160 bits of cryptographically generated entropy. Only SHA-256 hashes are persisted. The raw value is returned once from the create-invite function and kept only in the manager's current memory until the window closes.
- Redemption attempts are rate limited by both authenticated user and a hashed proxy-provided client address; administrative endpoints are rate limited by authenticated administrator ID. Supabase Auth's own signup and password-attempt protections must remain enabled.
- Redemption locks the invitation row and creates the license in the same PostgreSQL transaction. Unique constraints on invite, redeemer, and user prevent replay, transfer, and duplicate per-account licenses.
- Jarvis refreshes expired Supabase sessions and validates its license at launch and every ten minutes. It has no offline grace period: a service outage after a periodic check closes the beta app rather than granting indefinite access. This is strict and can interrupt a session during an outage.
- DPAPI protects refresh/access session tokens for the current Windows account. Passwords are only sent to Supabase Auth over TLS and are never persisted by Jarvis.

## Supabase setup

1. In the Discord Developer Portal, create an OAuth2 application and add the Supabase callback URL shown by the Supabase Dashboard (`https://rlcgexffgmdukpkououa.supabase.co/auth/v1/callback`) as its redirect URI. In Supabase Dashboard → Authentication → Sign In / Providers → Discord, enter that application's client ID and client secret. The Discord secret stays in Supabase. In Authentication → URL Configuration, add `http://127.0.0.1:*/oauth/callback/**` to the redirect allow list; the desktop uses an ephemeral loopback port and validates its own random callback state.
2. Install the Supabase CLI and authenticate it locally. Link this checkout to the project: `supabase link --project-ref YOUR_PROJECT_REF`.
3. Review the migrations, then apply pending additive changes using `supabase db push`. Do not edit or reapply already deployed migrations 001–003. Migration 004 returns associated license state in the invite list and adds audited deletion for invitations that have never been redeemed and have no license; redeemed records remain intact.
4. Deploy the changed/new Edge Functions after applying the migration (the checked-in `supabase/config.toml` sets `verify_jwt = false` because each function validates the bearer token itself):

   ```powershell
   supabase functions deploy redeem-invite
   supabase functions deploy validate-license
   supabase functions deploy admin-list-invites
   supabase functions deploy admin-revoke-invite
   supabase functions deploy admin-delete-invite
   supabase functions deploy admin-create-invite
   supabase functions deploy admin-list-licenses
   supabase functions deploy admin-revoke-license
   supabase functions deploy admin-reinstate-license
   supabase functions deploy admin-audit-log
   supabase functions deploy admin-overview
   ```

5. Confirm the project has the server-managed `SUPABASE_URL`, `SUPABASE_ANON_KEY` (or `SUPABASE_PUBLISHABLE_KEY`) and `SUPABASE_SERVICE_ROLE_KEY` function environment variables. If not, set them in the Supabase project secrets/dashboard. Never copy the service-role value into the desktop app, `.env.example`, GitHub, chat, or build command.

For this Discord OAuth and invitation-lifecycle update against project ref `rlcgexffgmdukpkououa`, the required deployment commands are:

```powershell
supabase db push --project-ref rlcgexffgmdukpkououa
supabase functions deploy redeem-invite --project-ref rlcgexffgmdukpkououa
supabase functions deploy validate-license --project-ref rlcgexffgmdukpkououa
supabase functions deploy admin-list-invites --project-ref rlcgexffgmdukpkououa
supabase functions deploy admin-revoke-invite --project-ref rlcgexffgmdukpkououa
supabase functions deploy admin-delete-invite --project-ref rlcgexffgmdukpkououa
```

The migration also replaces the server-side `beta_admin_list_licenses` and `beta_validate_license` RPCs, so their existing Edge Function wrappers need no source redeploy. Verify the new migration is recorded and exercise invitation creation/redemption in a separate test project before distributing the beta clients.
6. Create the initial admin Auth user through Supabase Auth (or have the admin sign up in Jarvis Beta), then grant that exact account administrator access in the Supabase SQL Editor, using the authenticated user's UUID:

   ```sql
   insert into beta_private.admin_users(user_id, note)
   select id, 'Initial close-beta administrator'
   from auth.users
   where lower(email) = lower('admin@example.com');
   ```

   Verify the insert affected exactly one row. This owner-only Dashboard step is the bootstrap boundary. Add future administrators only through the same server-side process; the Beta Manager has no admin-grant operation.
7. Check Supabase Auth email settings and allowed signup policy. The system has no test credentials, invitation backdoor, or universal admin account.

## Build the separate Windows beta applications

On Windows with .NET 8 SDK, run this from the repository:

```powershell
.\Beta\Publish-Beta.ps1 -SupabaseUrl "https://YOUR_PROJECT_REF.supabase.co" -SupabasePublishableKey "YOUR_PUBLIC_ANON_OR_PUBLISHABLE_KEY"
```

Outputs are placed under the ignored `Beta/artifacts/` folder:

- `Jarvis-Beta\Jarvis.exe` — beta Jarvis, with mandatory activation and periodic license checks.
- `Jarvis-Beta-Manager\Jarvis.BetaManager.exe` — the separate administrator client.

The publishable key is intended to be public; still review published resources and verify no server key is present. A manager user can also save the URL and public key in `%LocalAppData%\JarvisBetaManager\backend.json`; this is local client configuration, not server authorization. The beta Jarvis uses the backend URL and publishable key embedded by the beta build, with environment variables `JARVIS_SUPABASE_URL` and `JARVIS_SUPABASE_PUBLISHABLE_KEY` available for local development overrides. It does not trust a mutable local settings file to choose where it sends account credentials.

The script produces self-contained framework-dependent-to-the-user output directories (the Windows runtime is included) but not a new installer. The beta manager and beta Jarvis must remain separate artifacts from the public installer until acceptance testing is complete.

## Tester onboarding

1. An authorized admin signs into Beta Manager.
2. Open **Generate invite**, enter the tester's Discord User ID as a 17–20 digit string and their display name, choose an expiry and optional internal note, then confirm. This is an admin-entered assignment until Discord OAuth proves control of that ID.
3. Select a returned code and use **Copy selected code**. Codes are not posted to Discord or logged. Send the code manually in the approved ticket and treat it as a credential.
4. The tester launches beta Jarvis, selects **Continue with Discord**, authorizes in Discord's official browser page, then enters their invitation code. Supabase Auth's one-time PKCE code is exchanged for a Supabase session; the beta functions derive the Discord identity from the authenticated Supabase user and atomically compare it with the invitation assignment. The app opens only after server validation succeeds.
5. The tester can later sign in again without a new invitation; the DPAPI-protected Supabase refresh token and display profile are scoped to their Windows account. The Discord provider token is not saved. Signing out is available from the Jarvis profile menu in the beta build.

## Manage licenses and incidents

- Use **Licenses** to search by Discord ID/display name, email or Supabase user UUID. The table and details keep Discord identity separate from the actual Jarvis/Supabase account. **Revoke** and **Reinstate** require confirmation and refresh backend data afterward.
- Use **Audit log** for the actor, action, target, outcome, and UTC timestamp. Invitation hashes and authentication tokens are never exposed in the UI.
- Use **Invitations** to search/filter pending, redeemed, revoked, or expired entries. An unused pending invitation can be revoked; redemption and revocation serialize on the server-side invitation row lock. A code is copyable only in the manager process that created it because only its hash is stored.
- A reinstated license remains expired if an expiry policy has passed; reinstatement does not extend expiry.
- If an Auth session is revoked or cannot refresh, Jarvis returns to activation. A revoked/expired license is denied by `validate-license` at the next launch or periodic check.

## Tests and deployment checks

Run client checks on Windows:

```powershell
dotnet run --project Tests\Jarvis.Persistence.Tests\Jarvis.Persistence.Tests.csproj -c Release
dotnet build Jarvis.csproj -c Release
dotnet build Jarvis.csproj -c Beta -p:JarvisSupabaseUrl=https://YOUR_PROJECT_REF.supabase.co -p:JarvisSupabasePublishableKey=YOUR_PUBLIC_KEY
dotnet build BetaManager\Jarvis.BetaManager.csproj -c Release
```

Run backend checks with local Supabase and Deno installed:

```powershell
supabase start
supabase db reset
supabase test db
deno test --allow-env supabase\functions\_shared\http_test.ts
   deno check supabase\functions\redeem-invite\index.ts supabase\functions\validate-license\index.ts supabase\functions\admin-create-invite\index.ts supabase\functions\admin-list-invites\index.ts supabase\functions\admin-revoke-invite\index.ts supabase\functions\admin-list-licenses\index.ts supabase\functions\admin-revoke-license\index.ts supabase\functions\admin-reinstate-license\index.ts supabase\functions\admin-audit-log\index.ts supabase\functions\admin-overview\index.ts
```

`supabase/tests/close_beta_security.test.sql` exercises private-schema access, least-privilege grants, atomic single-use redemption, duplicate-account rejection, license validation, revoke, and reinstate. These tests require a disposable local Supabase database; do not run test fixtures against production.

Before inviting testers, separately verify in a test project: signup/confirmation, invalid/expired invite generic errors, concurrent redemption, second-account replay, unauthorized admin calls, refresh failures, revoke latency, service outage behavior, and clean installs on a Windows PC without the source tree. Backend deployment and these live acceptance tests require the project owner to configure the actual Supabase project.

## Operational limitations

- Supabase is not configured by this repository. No live database or Edge Function calls have been performed by the implementation build.
- Desktop authorization cannot make a client tamper-proof; the server is authoritative, but a determined user can patch local UI behavior. Protect database/function secrets, keep RLS and grants applied, monitor audit events, and revoke compromised accounts server-side.
- Edge function rate limiting is per authenticated user and operation. Supabase Auth's own anti-abuse limits should also remain enabled.
- Email confirmation, SMTP, OAuth provider setup, Supabase backup retention, and production alerting must be configured in the Supabase project before distribution.
- Discord OAuth is implemented through Supabase Auth and configured per project. It is not ready for testers until the Discord application credentials are entered in Supabase, the Discord redirect is set to the Supabase callback, and the desktop loopback redirect is allowed. No Discord client secret is distributed. The manager's manually entered Discord name remains explicitly unverified until the authenticated Discord OAuth identity matches the assigned snowflake during redemption. Discord usernames/global names and avatar metadata are taken from Supabase's authenticated identity record; the client avatar preview is obtained with the temporary OAuth provider token and is not saved as a credential.
- Existing email/password beta accounts without a linked Discord identity cannot validate under the new Discord-bound edge contracts. They need an explicit account transition/admin re-invitation plan; this change does not silently link or transfer licenses.
- Use Supabase's configured database backup/PITR retention for production data. Test a restore in a separate project before the beta begins; never commit a database dump. If an account is compromised, revoke its license in Beta Manager and rotate backend secrets in Supabase.
- Troubleshooting: a `403` in Beta Manager means the signed-in Auth UUID is not in `beta_private.admin_users`; a `401` requires signing in again; a backend `503` usually indicates missing Edge Function secrets or an unavailable project. The app intentionally does not fall back to a local “licensed” flag.
