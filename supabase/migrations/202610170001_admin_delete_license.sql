-- Permanently delete an individual beta license while retaining its invitation,
-- Supabase account, and an audit snapshot. The operation is admin-only and atomic.
begin;

alter table beta_private.license_events
  drop constraint if exists license_events_action_check;

alter table beta_private.license_events
  add constraint license_events_action_check check (action in (
    'invite_created','invite_redeemed','invite_rejected','invite_revoked','invite_deleted',
    'license_validated','license_revoked','license_reinstated','license_deleted','admin_denied'
  ));

create or replace function public.beta_admin_delete_license(p_admin uuid, p_license_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = pg_catalog, beta_private
as $$
declare
  l beta_private.licenses%rowtype;
  i beta_private.beta_invites%rowtype;
begin
  if auth.role() is distinct from 'service_role'
     or p_admin is null
     or p_license_id is null
     or not public.beta_is_admin(p_admin) then
    raise exception 'not_authorized';
  end if;

  select * into l
    from beta_private.licenses
    where id = p_license_id
    for update;

  if not found then
    return jsonb_build_object('ok', false, 'message', 'License not found.');
  end if;

  -- Snapshot identity and relationship metadata before deletion. Keep the
  -- invitation, auth user, profile, and all prior audit events untouched.
  select * into i from beta_private.beta_invites where id = l.invite_id for update;
  insert into beta_private.license_events(actor_user_id, action, target_id, outcome, metadata)
  values (
    p_admin,
    'license_deleted',
    l.id,
    'success',
    jsonb_build_object(
      'user_id', l.user_id,
      'invite_id', l.invite_id,
      'invite_redeemed_by', i.redeemed_by,
      'license_status', case
        when l.status = 'active' and l.expires_at is not null and l.expires_at <= now() then 'expired'
        else l.status
      end,
      'discord_user_id', i.discord_user_id,
      'discord_display_name', i.discord_display_name
    )
  );

  -- A redeemed invitation cannot remain logically active after its license is
  -- removed. Keep its row and identity data, mark it revoked, and clear only
  -- the redemption binding so a later invitation can be issued deliberately.
  update beta_private.beta_invites
    set status = 'revoked', redeemed_by = null, redeemed_at = null
    where id = l.invite_id and status = 'redeemed';
  if found then
    insert into beta_private.license_events(actor_user_id, action, target_id, outcome, metadata)
    values (p_admin, 'invite_revoked', l.invite_id, 'success', jsonb_build_object(
      'reason', 'license_deleted',
      'license_id', l.id
    ));
  end if;

  delete from beta_private.licenses where id = l.id;
  return jsonb_build_object('ok', true, 'license_id', l.id);
end
$$;

revoke all on function public.beta_admin_delete_license(uuid,uuid) from public, anon, authenticated;
grant execute on function public.beta_admin_delete_license(uuid,uuid) to service_role;

commit;
