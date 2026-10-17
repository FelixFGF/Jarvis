-- Safely expose associated license status and allow audited deletion of
-- invitations that have never been redeemed and have no license relation.
begin;

alter table beta_private.license_events drop constraint if exists license_events_action_check;
alter table beta_private.license_events add constraint license_events_action_check check (action in (
  'invite_created','invite_redeemed','invite_rejected','invite_revoked','invite_deleted','license_validated',
  'license_revoked','license_reinstated','admin_denied'
));

-- Preserve the generic client response but attach a safe internal reason code
-- for server diagnostics. The authoritative Discord ID still comes from the
-- authenticated Supabase identity resolved by the Edge Function.
create or replace function public.beta_redeem_invite_discord(
  p_hash bytea, p_user_id uuid, p_discord_user_id text,
  p_discord_username text, p_discord_global_name text, p_discord_avatar_hash text
) returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare i beta_private.beta_invites%rowtype; l_id uuid; v_name text; v_reason text;
begin
  if auth.role() is distinct from 'service_role' or p_user_id is null or octet_length(p_hash) <> 32
     or p_discord_user_id is null or p_discord_user_id !~ '^[0-9]{17,20}$' then raise exception 'not_authorized'; end if;
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
      values(p_user_id,'invite_rejected',case when i.id is not null then i.id else null end,'denied',jsonb_build_object('reason',v_reason));
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
    values(p_user_id,'invite_redeemed',l_id,'success',jsonb_build_object('identity_status','verified'));
  return jsonb_build_object('ok',true,'license_id',l_id);
exception when unique_violation then
  return jsonb_build_object('ok',false,'reason','concurrent_or_duplicate_redemption','message','Invitation could not be redeemed.');
end $$;
revoke all on function public.beta_redeem_invite_discord(bytea,uuid,text,text,text,text) from public,anon,authenticated;
grant execute on function public.beta_redeem_invite_discord(bytea,uuid,text,text,text,text) to service_role;

create or replace function public.beta_admin_list_invites(p_admin uuid,p_status text default 'all',p_search text default '')
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare result jsonb;
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) then raise exception 'not_authorized'; end if;
  update beta_private.beta_invites set status='expired' where status='pending' and expires_at is not null and expires_at<=now();
  select coalesce(jsonb_agg(jsonb_build_object(
    'invite_id',i.id,'status',i.status,'created_at',i.created_at,'expires_at',i.expires_at,
    'redeemed_at',i.redeemed_at,'discord_user_id',i.discord_user_id,'discord_display_name',i.discord_display_name,
    'discord_global_name',i.discord_global_name,'discord_username',i.discord_username,
    'discord_identity_status',i.discord_identity_status,'note',i.note,'code_available',false,
    'license_id',l.id,'license_status',case when l.status='active' and l.expires_at is not null and l.expires_at<=now() then 'expired' else l.status end,
    'redeemed_by',i.redeemed_by
  ) order by i.created_at desc),'[]'::jsonb)
  into result from beta_private.beta_invites i left join beta_private.licenses l on l.invite_id=i.id
  where (p_status='all' or i.status=p_status) and
    (coalesce(p_search,'')='' or i.discord_user_id ilike '%'||left(p_search,100)||'%' or i.discord_display_name ilike '%'||left(p_search,100)||'%' or i.discord_username ilike '%'||left(p_search,100)||'%');
  return jsonb_build_object('invitations',result);
end $$;
revoke all on function public.beta_admin_list_invites(uuid,text,text) from public,anon,authenticated;
grant execute on function public.beta_admin_list_invites(uuid,text,text) to service_role;

create or replace function public.beta_admin_delete_invite(p_admin uuid,p_invite_id uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare i beta_private.beta_invites%rowtype;
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) then raise exception 'not_authorized'; end if;
  select * into i from beta_private.beta_invites where id=p_invite_id for update;
  if not found then return jsonb_build_object('ok',false,'message','Invitation not found.'); end if;
  if i.status='redeemed' or i.redeemed_by is not null or i.redeemed_at is not null
     or exists(select 1 from beta_private.licenses where invite_id=i.id) then
    return jsonb_build_object('ok',false,'message','Redeemed invitations or invitations linked to a license cannot be deleted. Revoke the license separately.');
  end if;
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome,metadata)
    values(p_admin,'invite_deleted',i.id,'success',jsonb_build_object('discord_user_id',i.discord_user_id,'status',i.status));
  delete from beta_private.beta_invites where id=i.id;
  return jsonb_build_object('ok',true);
end $$;
revoke all on function public.beta_admin_delete_invite(uuid,uuid) from public,anon,authenticated;
grant execute on function public.beta_admin_delete_invite(uuid,uuid) to service_role;

commit;
