-- Backward-compatible Discord identity metadata for close-beta invitations.
-- Values are administrator-entered unless a future server-side Discord OAuth integration verifies them.
begin;

alter table beta_private.beta_invites
  add column if not exists discord_user_id text,
  add column if not exists discord_display_name text,
  add column if not exists discord_username text,
  add column if not exists discord_avatar_url text,
  add column if not exists discord_identity_status text not null default 'admin_entered';

alter table beta_private.beta_invites
  add constraint beta_invites_discord_snowflake_check
  check (discord_user_id is null or discord_user_id ~ '^[0-9]{17,20}$') not valid;
alter table beta_private.beta_invites
  add constraint beta_invites_discord_display_name_check
  check (discord_display_name is null or char_length(btrim(discord_display_name)) between 1 and 100) not valid;
alter table beta_private.beta_invites
  add constraint beta_invites_discord_identity_status_check
  check (discord_identity_status in ('admin_entered','verified')) not valid;

create index if not exists beta_invites_discord_id_idx on beta_private.beta_invites(discord_user_id);
create unique index if not exists beta_invites_one_live_discord_identity_idx
  on beta_private.beta_invites(discord_user_id)
  where discord_user_id is not null and status in ('pending','redeemed');

create or replace function public.beta_create_invite_discord(
  p_hash bytea, p_expiry timestamptz, p_note text,
  p_discord_user_id text, p_discord_display_name text, p_admin uuid
)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare v_id uuid;
begin
  if auth.role() is distinct from 'service_role'
     or not public.beta_is_admin(p_admin)
     or octet_length(p_hash) <> 32 then raise exception 'not_authorized'; end if;
  if p_discord_user_id is null or p_discord_user_id !~ '^[0-9]{17,20}$'
     or p_discord_display_name is null
     or char_length(btrim(p_discord_display_name)) not between 1 and 100 then
    raise exception 'invalid_discord_identity';
  end if;
  if p_expiry is not null and p_expiry <= now() then raise exception 'invalid_expiry'; end if;
  update beta_private.beta_invites set status='expired'
    where discord_user_id=p_discord_user_id and status='pending' and expires_at is not null and expires_at<=now();
  if exists(select 1 from beta_private.beta_invites where discord_user_id=p_discord_user_id and status in ('pending','redeemed')) then
    return jsonb_build_object('ok',false,'reason','discord_identity_exists');
  end if;
  insert into beta_private.beta_invites(
    code_hash,created_by,expires_at,note,discord_user_id,discord_display_name,discord_identity_status
  ) values (
    p_hash,p_admin,p_expiry,nullif(left(btrim(coalesce(p_note,'')),500),''),
    p_discord_user_id,btrim(p_discord_display_name),'admin_entered'
  ) returning id into v_id;
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome,metadata)
    values(p_admin,'invite_created',v_id,'success',jsonb_build_object('identity_status','admin_entered'));
  return jsonb_build_object('ok',true,'invite_id',v_id,'expires_at',p_expiry,'discord_identity_status','admin_entered');
exception when unique_violation then
  return jsonb_build_object('ok',false,'reason','identity_or_code_conflict');
end $$;
revoke all on function public.beta_create_invite_discord(bytea,timestamptz,text,text,text,uuid) from public,anon,authenticated;
grant execute on function public.beta_create_invite_discord(bytea,timestamptz,text,text,text,uuid) to service_role;

create or replace function public.beta_validate_license(p_user_id uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare l beta_private.licenses%rowtype; v_status text; identity jsonb;
begin
  if auth.role() is distinct from 'service_role' or p_user_id is null then raise exception 'not_authorized'; end if;
  select * into l from beta_private.licenses where user_id=p_user_id;
  if not found then return jsonb_build_object('valid',false,'status','missing','message','No beta license is assigned to this account.'); end if;
  v_status := case when l.status='active' and l.expires_at is not null and l.expires_at <= now() then 'expired' else l.status end;
  if v_status = 'expired' and l.status <> 'expired' then update beta_private.licenses set status='expired' where id=l.id; end if;
  select jsonb_build_object(
    'discord_user_id',i.discord_user_id,
    'discord_display_name',i.discord_display_name,
    'discord_username',i.discord_username,
    'discord_avatar_url',case when i.discord_avatar_url ~ '^https://cdn\.discordapp\.com/avatars/[0-9]{17,20}/[A-Za-z0-9_]{2,128}\.(png|jpe?g|webp|gif)(\?size=(32|64|128|256|512|1024))?$' then i.discord_avatar_url else null end,
    'discord_identity_status',i.discord_identity_status
  ) into identity
  from beta_private.beta_invites i where i.id=l.invite_id;
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'license_validated',l.id,case when v_status='active' then 'success' else 'denied' end);
  return jsonb_build_object('valid',v_status='active','status',v_status,'expires_at',l.expires_at,'discord_profile',identity);
end $$;
revoke all on function public.beta_validate_license(uuid) from public,anon,authenticated;
grant execute on function public.beta_validate_license(uuid) to service_role;

create or replace function public.beta_admin_list_licenses(p_admin uuid, p_search text default '')
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
    'discord_username',i.discord_username,
    'discord_avatar_url',case when i.discord_avatar_url ~ '^https://cdn\.discordapp\.com/avatars/[0-9]{17,20}/[A-Za-z0-9_]{2,128}\.(png|jpe?g|webp|gif)(\?size=(32|64|128|256|512|1024))?$' then i.discord_avatar_url else null end,
    'discord_identity_status',i.discord_identity_status,'note',i.note
  ) order by l.created_at desc),'[]'::jsonb)
  into result from beta_private.licenses l
  left join beta_private.profiles p on p.user_id=l.user_id
  left join beta_private.beta_invites i on i.id=l.invite_id
  where coalesce(p_search,'')=''
    or l.user_id::text ilike '%'||left(p_search,100)||'%'
    or coalesce(p.email,'') ilike '%'||left(p_search,100)||'%'
    or coalesce(i.discord_user_id,'') ilike '%'||left(p_search,100)||'%'
    or coalesce(i.discord_display_name,'') ilike '%'||left(p_search,100)||'%'
    or coalesce(i.discord_username,'') ilike '%'||left(p_search,100)||'%';
  return jsonb_build_object('licenses',result);
end $$;
revoke all on function public.beta_admin_list_licenses(uuid,text) from public,anon,authenticated;
grant execute on function public.beta_admin_list_licenses(uuid,text) to service_role;

commit;
