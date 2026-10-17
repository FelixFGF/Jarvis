-- Jarvis Close Beta licensing. Sensitive rows live outside PostgREST's exposed schemas.
begin;

create schema if not exists beta_private;
revoke all on schema beta_private from public, anon, authenticated;
grant usage on schema beta_private to service_role;

create table beta_private.profiles (
  user_id uuid primary key references auth.users(id) on delete cascade,
  email text,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create table beta_private.beta_invites (
  id uuid primary key default gen_random_uuid(),
  code_hash bytea not null unique check (octet_length(code_hash) = 32),
  status text not null default 'pending' check (status in ('pending','redeemed','revoked','expired')),
  created_by uuid not null references auth.users(id),
  created_at timestamptz not null default now(),
  expires_at timestamptz,
  redeemed_by uuid unique references auth.users(id),
  redeemed_at timestamptz,
  note text,
  check ((status = 'redeemed' and redeemed_by is not null and redeemed_at is not null) or
         (status <> 'redeemed' and redeemed_by is null and redeemed_at is null))
);

create table beta_private.licenses (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null unique references auth.users(id) on delete cascade,
  invite_id uuid not null unique references beta_private.beta_invites(id),
  status text not null default 'active' check (status in ('active','revoked','expired')),
  created_at timestamptz not null default now(),
  expires_at timestamptz,
  revoked_at timestamptz,
  revoked_by uuid references auth.users(id),
  reinstated_at timestamptz,
  check ((status = 'revoked' and revoked_at is not null and revoked_by is not null) or
         (status <> 'revoked' and revoked_at is null and revoked_by is null))
);

create table beta_private.admin_users (
  user_id uuid primary key references auth.users(id) on delete cascade,
  added_at timestamptz not null default now(),
  added_by uuid references auth.users(id),
  note text
);

create table beta_private.license_events (
  id uuid primary key default gen_random_uuid(),
  actor_user_id uuid references auth.users(id),
  action text not null check (action in ('invite_created','invite_redeemed','invite_rejected','license_validated','license_revoked','license_reinstated','admin_denied')),
  target_id uuid,
  outcome text not null check (outcome in ('success','denied','error')),
  metadata jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now()
);

create table beta_private.rate_limits (
  bucket_hash bytea primary key check (octet_length(bucket_hash) = 32),
  window_started_at timestamptz not null,
  hits integer not null check (hits >= 0)
);

insert into beta_private.profiles(user_id,email)
select id,email from auth.users
on conflict(user_id) do update set email=excluded.email,updated_at=now();

create index beta_invites_status_expiry_idx on beta_private.beta_invites(status, expires_at);
create index licenses_status_expiry_idx on beta_private.licenses(status, expires_at);
create index license_events_created_idx on beta_private.license_events(created_at desc);
create index license_events_target_idx on beta_private.license_events(target_id, created_at desc);

alter table beta_private.profiles enable row level security;
alter table beta_private.beta_invites enable row level security;
alter table beta_private.licenses enable row level security;
alter table beta_private.admin_users enable row level security;
alter table beta_private.license_events enable row level security;
alter table beta_private.rate_limits enable row level security;
revoke all on all tables in schema beta_private from public, anon, authenticated;
grant select, insert, update, delete on all tables in schema beta_private to service_role;
alter default privileges in schema beta_private revoke all on tables from public, anon, authenticated;

create or replace function public.beta_is_admin(p_user_id uuid)
returns boolean language sql stable security definer set search_path = pg_catalog, beta_private as $$
  select exists(select 1 from beta_private.admin_users where user_id = p_user_id)
$$;
revoke all on function public.beta_is_admin(uuid) from public, anon, authenticated;
grant execute on function public.beta_is_admin(uuid) to service_role;

create or replace function public.beta_admin_audit_denied(p_user_id uuid)
returns void language plpgsql security definer set search_path = pg_catalog, beta_private as $$
begin
  if auth.role() = 'service_role' and p_user_id is not null then
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'admin_denied',null,'denied');
  end if;
end $$;
revoke all on function public.beta_admin_audit_denied(uuid) from public, anon, authenticated;
grant execute on function public.beta_admin_audit_denied(uuid) to service_role;

create or replace function public.beta_rate_limit(p_bucket_hash bytea, p_limit integer, p_window_seconds integer)
returns boolean language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare r beta_private.rate_limits%rowtype;
begin
  if auth.role() is distinct from 'service_role' or octet_length(p_bucket_hash) <> 32 or p_limit < 1 or p_window_seconds < 1 then return false; end if;
  insert into beta_private.rate_limits(bucket_hash, window_started_at, hits) values(p_bucket_hash, now(), 1)
    on conflict (bucket_hash) do update set
      window_started_at = case when beta_private.rate_limits.window_started_at + make_interval(secs => p_window_seconds) <= now() then now() else beta_private.rate_limits.window_started_at end,
      hits = case when beta_private.rate_limits.window_started_at + make_interval(secs => p_window_seconds) <= now() then 1 else beta_private.rate_limits.hits + 1 end;
  select * into r from beta_private.rate_limits where bucket_hash = p_bucket_hash;
  return r.hits <= p_limit;
end $$;
revoke all on function public.beta_rate_limit(bytea,integer,integer) from public, anon, authenticated;
grant execute on function public.beta_rate_limit(bytea,integer,integer) to service_role;

create or replace function public.beta_create_invite(p_hash bytea, p_expiry timestamptz, p_note text, p_admin uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare v_id uuid;
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) or octet_length(p_hash) <> 32 then raise exception 'not_authorized'; end if;
  if p_expiry is not null and p_expiry <= now() then raise exception 'invalid_expiry'; end if;
  insert into beta_private.beta_invites(code_hash, created_by, expires_at, note) values(p_hash, p_admin, p_expiry, nullif(left(coalesce(p_note,''),500),'')) returning id into v_id;
  insert into beta_private.license_events(actor_user_id, action, target_id, outcome) values(p_admin,'invite_created',v_id,'success');
  return jsonb_build_object('invite_id',v_id,'expires_at',p_expiry);
end $$;
revoke all on function public.beta_create_invite(bytea,timestamptz,text,uuid) from public, anon, authenticated;
grant execute on function public.beta_create_invite(bytea,timestamptz,text,uuid) to service_role;

create or replace function public.beta_create_invites(p_hashes jsonb, p_expiry timestamptz, p_note text, p_admin uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare item jsonb; created jsonb := '[]'::jsonb; v_hash bytea; v_id uuid; idx integer := 0;
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) or jsonb_typeof(p_hashes) <> 'array' or jsonb_array_length(p_hashes) not between 1 and 25 then raise exception 'not_authorized'; end if;
  if p_expiry is not null and p_expiry <= now() then raise exception 'invalid_expiry'; end if;
  for item in select value from jsonb_array_elements(p_hashes) with ordinality as item_rows(value,ordinality) order by ordinality loop
    if coalesce(item->>'hash','') !~ '^[0-9a-f]{64}$' then raise exception 'invalid_hash'; end if;
    v_hash := decode(item->>'hash','hex');
    insert into beta_private.beta_invites(code_hash,created_by,expires_at,note) values(v_hash,p_admin,p_expiry,nullif(left(coalesce(p_note,''),500),'')) returning id into v_id;
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_admin,'invite_created',v_id,'success');
    created := created || jsonb_build_array(jsonb_build_object('index',idx,'invite_id',v_id,'expires_at',p_expiry));
    idx := idx + 1;
  end loop;
  return jsonb_build_object('invitations',created);
end $$;
revoke all on function public.beta_create_invites(jsonb,timestamptz,text,uuid) from public, anon, authenticated;
grant execute on function public.beta_create_invites(jsonb,timestamptz,text,uuid) to service_role;

create or replace function public.beta_redeem_invite(p_hash bytea, p_user_id uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare i beta_private.beta_invites%rowtype; l_id uuid;
begin
  if auth.role() is distinct from 'service_role' or p_user_id is null or octet_length(p_hash) <> 32 then raise exception 'not_authorized'; end if;
  select * into i from beta_private.beta_invites where code_hash = p_hash for update;
  if not found then
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'invite_rejected',null,'denied');
    return jsonb_build_object('ok',false,'message','Invitation could not be redeemed.');
  end if;
  if i.status <> 'pending' then
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'invite_rejected',i.id,'denied');
    return jsonb_build_object('ok',false,'message','Invitation could not be redeemed.');
  end if;
  if i.expires_at is not null and i.expires_at <= now() then
    update beta_private.beta_invites set status='expired' where id=i.id;
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'invite_rejected',i.id,'denied');
    return jsonb_build_object('ok',false,'message','Invitation could not be redeemed.');
  end if;
  if exists(select 1 from beta_private.licenses where user_id = p_user_id) then
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'invite_rejected',i.id,'denied');
    return jsonb_build_object('ok',false,'message','Invitation could not be redeemed.');
  end if;
  update beta_private.beta_invites set status='redeemed', redeemed_by=p_user_id, redeemed_at=now() where id=i.id;
  insert into beta_private.licenses(user_id,invite_id) values(p_user_id,i.id) returning id into l_id;
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'invite_redeemed',l_id,'success');
  return jsonb_build_object('ok',true,'license_id',l_id);
exception when unique_violation then
  return jsonb_build_object('ok',false,'message','Invitation could not be redeemed.');
end $$;
revoke all on function public.beta_redeem_invite(bytea,uuid) from public, anon, authenticated;
grant execute on function public.beta_redeem_invite(bytea,uuid) to service_role;

create or replace function public.beta_validate_license(p_user_id uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare l beta_private.licenses%rowtype; v_status text;
begin
  if auth.role() is distinct from 'service_role' or p_user_id is null then raise exception 'not_authorized'; end if;
  select * into l from beta_private.licenses where user_id=p_user_id;
  if not found then return jsonb_build_object('valid',false,'status','missing','message','No beta license is assigned to this account.'); end if;
  v_status := case when l.status='active' and l.expires_at is not null and l.expires_at <= now() then 'expired' else l.status end;
  if v_status = 'expired' and l.status <> 'expired' then update beta_private.licenses set status='expired' where id=l.id; end if;
  insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_user_id,'license_validated',l.id,case when v_status='active' then 'success' else 'denied' end);
  return jsonb_build_object('valid',v_status='active','status',v_status,'expires_at',l.expires_at);
end $$;
revoke all on function public.beta_validate_license(uuid) from public, anon, authenticated;
grant execute on function public.beta_validate_license(uuid) to service_role;

create or replace function public.beta_admin_list_licenses(p_admin uuid, p_search text default '')
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare result jsonb;
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) then raise exception 'not_authorized'; end if;
  update beta_private.licenses set status='expired' where status='active' and expires_at is not null and expires_at<=now();
  update beta_private.beta_invites set status='expired' where status='pending' and expires_at is not null and expires_at<=now();
  select coalesce(jsonb_agg(jsonb_build_object('license_id',l.id,'user_id',l.user_id,'email',p.email,'status',case when l.status='active' and l.expires_at is not null and l.expires_at<=now() then 'expired' else l.status end,'created_at',l.created_at,'expires_at',l.expires_at,'revoked_at',l.revoked_at) order by l.created_at desc),'[]'::jsonb)
  into result from beta_private.licenses l left join beta_private.profiles p on p.user_id=l.user_id
  where coalesce(p_search,'')='' or l.user_id::text ilike '%'||left(p_search,100)||'%' or coalesce(p.email,'') ilike '%'||left(p_search,100)||'%';
  return jsonb_build_object('licenses',result);
end $$;
revoke all on function public.beta_admin_list_licenses(uuid,text) from public, anon, authenticated;
grant execute on function public.beta_admin_list_licenses(uuid,text) to service_role;

create or replace function public.beta_admin_overview(p_admin uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare active_count integer; pending_count integer; inactive_count integer; recent jsonb;
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) then raise exception 'not_authorized'; end if;
  update beta_private.licenses set status='expired' where status='active' and expires_at is not null and expires_at<=now();
  update beta_private.beta_invites set status='expired' where status='pending' and expires_at is not null and expires_at<=now();
  select count(*) into active_count from beta_private.licenses where status='active' and (expires_at is null or expires_at>now());
  select count(*) into pending_count from beta_private.beta_invites where status='pending' and (expires_at is null or expires_at>now());
  select count(*) into inactive_count from beta_private.licenses where status='revoked' or (status='active' and expires_at is not null and expires_at<=now());
  select coalesce(jsonb_agg(jsonb_build_object('event_id',e.id,'admin_user_id',e.actor_user_id,'action',e.action,'target_id',e.target_id,'outcome',e.outcome,'created_at',e.created_at,'metadata',nullif(e.metadata,'{}'::jsonb)) order by e.created_at desc),'[]'::jsonb)
  into recent from (select * from beta_private.license_events order by created_at desc limit 10) e;
  return jsonb_build_object('active_licenses',active_count,'pending_invites',pending_count,'inactive_licenses',inactive_count,'recent_activity',recent);
end $$;
revoke all on function public.beta_admin_overview(uuid) from public, anon, authenticated;
grant execute on function public.beta_admin_overview(uuid) to service_role;

create or replace function public.beta_admin_change_license(p_admin uuid, p_license_id uuid, p_action text)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
declare l beta_private.licenses%rowtype;
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) or p_action not in ('revoke','reinstate') then raise exception 'not_authorized'; end if;
  select * into l from beta_private.licenses where id=p_license_id for update;
  if not found then return jsonb_build_object('ok',false,'message','License not found.'); end if;
  if p_action='revoke' then update beta_private.licenses set status='revoked',revoked_at=now(),revoked_by=p_admin where id=l.id;
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_admin,'license_revoked',l.id,'success');
  else update beta_private.licenses set status=case when expires_at is not null and expires_at<=now() then 'expired' else 'active' end,revoked_at=null,revoked_by=null,reinstated_at=now() where id=l.id;
    insert into beta_private.license_events(actor_user_id,action,target_id,outcome) values(p_admin,'license_reinstated',l.id,'success');
  end if;
  return jsonb_build_object('ok',true);
end $$;
revoke all on function public.beta_admin_change_license(uuid,uuid,text) from public, anon, authenticated;
grant execute on function public.beta_admin_change_license(uuid,uuid,text) to service_role;

create or replace function public.beta_admin_audit(p_admin uuid, p_limit integer default 100)
returns jsonb language plpgsql security definer set search_path = pg_catalog, beta_private as $$
begin
  if auth.role() is distinct from 'service_role' or not public.beta_is_admin(p_admin) then raise exception 'not_authorized'; end if;
  return jsonb_build_object('events',coalesce((select jsonb_agg(jsonb_build_object('event_id',e.id,'admin_user_id',e.actor_user_id,'action',e.action,'target_id',e.target_id,'outcome',e.outcome,'created_at',e.created_at,'metadata',nullif(e.metadata,'{}'::jsonb)) order by e.created_at desc) from (select * from beta_private.license_events order by created_at desc limit greatest(1,least(p_limit,250))) e),'[]'::jsonb));
end $$;
revoke all on function public.beta_admin_audit(uuid,integer) from public, anon, authenticated;
grant execute on function public.beta_admin_audit(uuid,integer) to service_role;

create or replace function beta_private.sync_beta_profile()
returns trigger language plpgsql security definer set search_path = pg_catalog, beta_private as $$
begin
  insert into beta_private.profiles(user_id,email) values(new.id,new.email) on conflict(user_id) do update set email=excluded.email,updated_at=now();
  return new;
end $$;
revoke all on function beta_private.sync_beta_profile() from public, anon, authenticated;
drop trigger if exists beta_profile_on_auth_user on auth.users;
create trigger beta_profile_on_auth_user after insert or update of email on auth.users for each row execute function beta_private.sync_beta_profile();

commit;
