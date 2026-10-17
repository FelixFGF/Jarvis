-- Add Discord-authenticated redemption and administrator invitation lifecycle.
-- Existing migrations are already applied; this migration is additive and preserves rows.
begin;

alter table beta_private.beta_invites add column if not exists discord_global_name text;
alter table beta_private.license_events drop constraint if exists license_events_action_check;
alter table beta_private.license_events add constraint license_events_action_check check (action in (
  'invite_created','invite_redeemed','invite_rejected','invite_revoked','license_validated',
  'license_revoked','license_reinstated','admin_denied'
));

-- Old deployed wrappers call the legacy RPC signatures. Make those signatures
-- fail closed for Discord-assigned records during the migration/deploy window.
create or replace function public.beta_redeem_invite(p_hash bytea,p_user_id uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare i beta_private.beta_invites%rowtype; l_id uuid;
begin
  if auth.role() is distinct from 'service_role' or p_user_id is null or octet_length(p_hash)<>32 then raise exception 'not_authorized'; end if;
  select * into i from beta_private.beta_invites where code_hash=p_hash for update;
  if not found or i.status<>'pending' or i.discord_user_id is not null or (i.expires_at is not null and i.expires_at<=now())
     or exists(select 1 from beta_private.licenses where user_id=p_user_id) then
    if found and i.status='pending' and i.expires_at is not null and i.expires_at<=now() then update beta_private.beta_invites set status='expired' where id=i.id; end if;
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'invite_rejected',case when found then i.id else null end,'denied');
    return jsonb_build_object('ok',false,'message','Invitation could not be redeemed.');
  end if;
  update beta_private.beta_invites set status='redeemed',redeemed_by=p_user_id,redeemed_at=now() where id=i.id;
  insert into beta_private.licenses(user_id,invite_id) values(p_user_id,i.id) returning id into l_id;
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'invite_redeemed',l_id,'success');
  return jsonb_build_object('ok',true,'license_id',l_id);
exception when unique_violation then return jsonb_build_object('ok',false,'message','Invitation could not be redeemed.');
end $$;
revoke all on function public.beta_redeem_invite(bytea,uuid) from public,anon,authenticated;
grant execute on function public.beta_redeem_invite(bytea,uuid) to service_role;

create or replace function public.beta_validate_license(p_user_id uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare l beta_private.licenses%rowtype; v_status text;
begin
  if auth.role() is distinct from 'service_role' or p_user_id is null then raise exception 'not_authorized'; end if;
  select * into l from beta_private.licenses where user_id=p_user_id;
  if not found then return jsonb_build_object('valid',false,'status','missing','message','No beta license is assigned to this account.'); end if;
  if exists(select 1 from beta_private.beta_invites where id=l.invite_id and discord_user_id is not null) then
    return jsonb_build_object('valid',false,'status','discord_validation_required','message','Update Jarvis Beta to validate Discord-bound beta licenses.');
  end if;
  v_status:=case when l.status='active' and l.expires_at is not null and l.expires_at<=now() then 'expired' else l.status end;
  if v_status='expired' and l.status<>'expired' then update beta_private.licenses set status='expired' where id=l.id; end if;
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'license_validated',l.id,case when v_status='active' then 'success' else 'denied' end);
  return jsonb_build_object('valid',v_status='active','status',v_status,'expires_at',l.expires_at);
end $$;
revoke all on function public.beta_validate_license(uuid) from public,anon,authenticated;
grant execute on function public.beta_validate_license(uuid) to service_role;

create or replace function public.beta_redeem_invite_discord(
  p_hash bytea, p_user_id uuid, p_discord_user_id text,
  p_discord_username text, p_discord_global_name text, p_discord_avatar_hash text
) returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare i beta_private.beta_invites%rowtype; l_id uuid; v_name text;
begin
  if auth.role() is distinct from 'service_role' or p_user_id is null or octet_length(p_hash) <> 32
     or p_discord_user_id is null or p_discord_user_id !~ '^[0-9]{17,20}$' then raise exception 'not_authorized'; end if;
  select * into i from beta_private.beta_invites where code_hash=p_hash for update;
  if not found or i.status <> 'pending' or (i.expires_at is not null and i.expires_at<=now())
     or i.discord_user_id is null or i.discord_user_id <> p_discord_user_id
     or exists(select 1 from beta_private.licenses where user_id=p_user_id) then
    if found and i.status='pending' and i.expires_at is not null and i.expires_at<=now() then update beta_private.beta_invites set status='expired' where id=i.id; end if;
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'invite_rejected',case when found then i.id else null end,'denied');
    return jsonb_build_object('ok',false,'message','Invitation could not be redeemed by this Discord account.');
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
    values(p_user_id,'invite_redeemed',l_id,'success',jsonb_build_object('identity_status','verified'));
  return jsonb_build_object('ok',true,'license_id',l_id);
exception when unique_violation then
  return jsonb_build_object('ok',false,'message','Invitation could not be redeemed by this Discord account.');
end $$;
revoke all on function public.beta_redeem_invite_discord(bytea,uuid,text,text,text,text) from public,anon,authenticated;
grant execute on function public.beta_redeem_invite_discord(bytea,uuid,text,text,text,text) to service_role;

create or replace function public.beta_validate_license_discord(p_user_id uuid,p_discord_user_id text)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare l beta_private.licenses%rowtype; i beta_private.beta_invites%rowtype; v_status text;
begin
  if auth.role() is distinct from 'service_role' or p_user_id is null or p_discord_user_id is null or p_discord_user_id !~ '^[0-9]{17,20}$' then raise exception 'not_authorized'; end if;
  select * into l from beta_private.licenses where user_id=p_user_id;
  if not found then return jsonb_build_object('valid',false,'status','missing','message','No active beta license was found.'); end if;
  select * into i from beta_private.beta_invites where id=l.invite_id;
  v_status := case when l.status='active' and l.expires_at is not null and l.expires_at<=now() then 'expired' else l.status end;
  if v_status='expired' and l.status<>'expired' then update beta_private.licenses set status='expired' where id=l.id; end if;
  if i.discord_user_id is distinct from p_discord_user_id or i.discord_identity_status <> 'verified' then
    v_status := 'discord_mismatch';
  end if;
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome)
    values(p_user_id,'license_validated',l.id,case when v_status='active' then 'success' else 'denied' end);
  return jsonb_build_object('valid',v_status='active','status',v_status,'expires_at',l.expires_at,
    'message',case when v_status='discord_mismatch' then 'The authenticated Discord account does not match this license.' else null end,
    'discord_profile',case when v_status='active' then jsonb_build_object(
      'discord_user_id',i.discord_user_id,'discord_display_name',i.discord_display_name,
      'discord_global_name',i.discord_global_name,'discord_username',i.discord_username,
      'discord_avatar_url',case when i.discord_avatar_url ~ '^https://cdn\.discordapp\.com/avatars/[0-9]{17,20}/[A-Za-z0-9_]{2,128}\.png\?size=256$' then i.discord_avatar_url else null end,
      'discord_identity_status','verified') else null end);
end $$;
revoke all on function public.beta_validate_license_discord(uuid,text) from public,anon,authenticated;
grant execute on function public.beta_validate_license_discord(uuid,text) to service_role;

create or replace function public.beta_admin_list_licenses(p_admin uuid,p_search text default '')
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare result jsonb;
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) then raise exception 'not_authorized'; end if;
  update beta_private.licenses set status='expired' where status='active' and expires_at is not null and expires_at<=now();
  update beta_private.beta_invites set status='expired' where status='pending' and expires_at is not null and expires_at<=now();
  select coalesce(jsonb_agg(jsonb_build_object(
    'license_id',l.id,'user_id',l.user_id,'email',p.email,
    'status',case when l.status='active' and l.expires_at is not null and l.expires_at<=now() then 'expired' else l.status end,
    'created_at',l.created_at,'expires_at',l.expires_at,'revoked_at',l.revoked_at,
    'invite_created_at',i.created_at,'invite_redeemed_at',i.redeemed_at,
    'discord_user_id',i.discord_user_id,'discord_display_name',i.discord_display_name,
    'discord_global_name',i.discord_global_name,'discord_username',i.discord_username,
    'discord_avatar_url',case when i.discord_avatar_url ~ '^https://cdn\.discordapp\.com/avatars/[0-9]{17,20}/[A-Za-z0-9_]{2,128}\.png\?size=256$' then i.discord_avatar_url else null end,
    'discord_identity_status',i.discord_identity_status,'note',i.note
  ) order by l.created_at desc),'[]'::jsonb) into result
  from beta_private.licenses l left join beta_private.profiles p on p.user_id=l.user_id
  left join beta_private.beta_invites i on i.id=l.invite_id
  where coalesce(p_search,'')='' or l.user_id::text ilike '%'||left(p_search,100)||'%'
    or coalesce(p.email,'') ilike '%'||left(p_search,100)||'%'
    or coalesce(i.discord_user_id,'') ilike '%'||left(p_search,100)||'%'
    or coalesce(i.discord_display_name,'') ilike '%'||left(p_search,100)||'%'
    or coalesce(i.discord_username,'') ilike '%'||left(p_search,100)||'%';
  return jsonb_build_object('licenses',result);
end $$;
revoke all on function public.beta_admin_list_licenses(uuid,text) from public,anon,authenticated;
grant execute on function public.beta_admin_list_licenses(uuid,text) to service_role;

create or replace function public.beta_admin_list_invites(p_admin uuid,p_status text default 'all',p_search text default '')
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare result jsonb;
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) then raise exception 'not_authorized'; end if;
  update beta_private.beta_invites set status='expired' where status='pending' and expires_at is not null and expires_at<=now();
  select coalesce(jsonb_agg(jsonb_build_object('invite_id',i.id,'status',i.status,'created_at',i.created_at,'expires_at',i.expires_at,
    'redeemed_at',i.redeemed_at,'discord_user_id',i.discord_user_id,'discord_display_name',i.discord_display_name,
    'discord_global_name',i.discord_global_name,'discord_username',i.discord_username,'discord_identity_status',i.discord_identity_status,
    'note',i.note,'code_available',false) order by i.created_at desc),'[]'::jsonb)
  into result from beta_private.beta_invites i
  where (p_status='all' or i.status=p_status) and
    (coalesce(p_search,'')='' or i.discord_user_id ilike '%'||left(p_search,100)||'%' or i.discord_display_name ilike '%'||left(p_search,100)||'%' or i.discord_username ilike '%'||left(p_search,100)||'%');
  return jsonb_build_object('invitations',result);
end $$;
revoke all on function public.beta_admin_list_invites(uuid,text,text) from public,anon,authenticated;
grant execute on function public.beta_admin_list_invites(uuid,text,text) to service_role;

create or replace function public.beta_admin_revoke_invite(p_admin uuid,p_invite_id uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare i beta_private.beta_invites%rowtype;
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) then raise exception 'not_authorized'; end if;
  select * into i from beta_private.beta_invites where id=p_invite_id for update;
  if not found then return jsonb_build_object('ok',false,'message','Invitation not found.'); end if;
  if i.status<>'pending' then return jsonb_build_object('ok',false,'message','Only an unused pending invitation can be revoked.'); end if;
  update beta_private.beta_invites set status='revoked' where id=i.id;
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome,metadata)
    values(p_admin,'invite_revoked',i.id,'success',jsonb_build_object('discord_user_id',i.discord_user_id));
  return jsonb_build_object('ok',true);
end $$;
revoke all on function public.beta_admin_revoke_invite(uuid,uuid) from public,anon,authenticated;
grant execute on function public.beta_admin_revoke_invite(uuid,uuid) to service_role;

commit;
