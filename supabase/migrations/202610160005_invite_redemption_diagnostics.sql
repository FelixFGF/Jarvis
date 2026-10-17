-- Adds a per-attempt correlation ID to server-side invitation rejection events.
-- Existing six-argument callers continue to use the existing RPC until the
-- updated Edge Function is deployed; no existing function or data is removed.
begin;

create or replace function public.beta_redeem_invite_discord(
  p_hash bytea, p_user_id uuid, p_discord_user_id text,
  p_discord_username text, p_discord_global_name text, p_discord_avatar_hash text,
  p_attempt_id uuid
) returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare i beta_private.beta_invites%rowtype; l_id uuid; v_name text; v_reason text;
begin
  if auth.role() is distinct from 'service_role' or p_user_id is null or octet_length(p_hash) <> 32
     or p_attempt_id is null or p_discord_user_id is null or p_discord_user_id !~ '^[0-9]{17,20}$' then
    raise exception 'not_authorized';
  end if;
  select * into i from beta_private.beta_invites where code_hash=p_hash for update;
  if not found then
    v_reason := 'not_found';
  elsif i.status <> 'pending' then
    v_reason := case when i.status='revoked' then 'revoked' when i.status='expired' then 'expired' else 'already_used' end;
  elsif i.expires_at is not null and i.expires_at<=now() then
    update beta_private.beta_invites set status='expired' where id=i.id;
    v_reason := 'expired';
  elsif i.discord_user_id is null then
    v_reason := 'discord_binding_missing';
  elsif i.discord_user_id <> p_discord_user_id then
    v_reason := 'discord_mismatch';
  elsif exists(select 1 from beta_private.licenses where user_id=p_user_id) then
    v_reason := 'account_already_licensed';
  end if;
  if v_reason is not null then
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome,metadata)
      values(p_user_id,'invite_rejected',case when i.id is not null then i.id else null end,'denied',
        jsonb_build_object('reason',v_reason,'attempt_id',p_attempt_id));
    return jsonb_build_object('ok',false,'reason',v_reason,'message','Invitation could not be redeemed.');
  end if;
  v_name := coalesce(nullif(left(btrim(coalesce(p_discord_global_name,'')),100),''), nullif(left(btrim(coalesce(p_discord_username,'')),100),''));
  update beta_private.beta_invites set status='redeemed',redeemed_by=p_user_id,redeemed_at=now(),
    discord_username=nullif(left(btrim(coalesce(p_discord_username,'')),100),''),
    discord_global_name=nullif(left(btrim(coalesce(p_discord_global_name,'')),100),''),
    discord_display_name=coalesce(v_name,discord_display_name),discord_identity_status='verified',
    discord_avatar_url=case when p_discord_avatar_hash ~ '^[A-Za-z0-9_]{2,128}$' then 'https://cdn.discordapp.com/avatars/'||p_discord_user_id||'/'||p_discord_avatar_hash||'.png?size=256' else null end
    where id=i.id;
  insert into beta_private.licenses(user_id,invite_id) values(p_user_id,i.id) returning id into l_id;
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome,metadata)
    values(p_user_id,'invite_redeemed',l_id,'success',jsonb_build_object('identity_status','verified','attempt_id',p_attempt_id));
  return jsonb_build_object('ok',true,'license_id',l_id);
exception when unique_violation then
  -- The exception subtransaction rolls back the attempted mutations; this
  -- event is written from the handler so duplicate/racing attempts are audited.
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome,metadata)
    values(p_user_id,'invite_rejected',case when i.id is not null then i.id else null end,'denied',
      jsonb_build_object('reason','concurrent_or_duplicate_redemption','attempt_id',p_attempt_id));
  return jsonb_build_object('ok',false,'reason','concurrent_or_duplicate_redemption','message','Invitation could not be redeemed.');
end $$;
revoke all on function public.beta_redeem_invite_discord(bytea,uuid,text,text,text,text,uuid) from public,anon,authenticated;
grant execute on function public.beta_redeem_invite_discord(bytea,uuid,text,text,text,text,uuid) to service_role;

commit;
