begin;
select plan(23);

select ok(not has_function_privilege('anon','public.beta_admin_delete_license(uuid,uuid)','execute'), 'anonymous users cannot execute license deletion');
select ok(not has_function_privilege('authenticated','public.beta_admin_delete_license(uuid,uuid)','execute'), 'authenticated users cannot execute license deletion');
select ok(has_function_privilege('service_role','public.beta_admin_delete_license(uuid,uuid)','execute'), 'server service role can call the protected deletion RPC');

insert into auth.users(id,aud,role,email,encrypted_password,email_confirmed_at,raw_app_meta_data,raw_user_meta_data,created_at,updated_at)
values
 ('60000000-0000-4000-8000-000000000001','authenticated','authenticated','delete-admin@example.invalid','',now(),'{}','{}',now(),now()),
 ('60000000-0000-4000-8000-000000000002','authenticated','authenticated','delete-active@example.invalid','',now(),'{}','{}',now(),now()),
 ('60000000-0000-4000-8000-000000000003','authenticated','authenticated','delete-revoked@example.invalid','',now(),'{}','{}',now(),now()),
 ('60000000-0000-4000-8000-000000000004','authenticated','authenticated','delete-expired@example.invalid','',now(),'{}','{}',now(),now()),
 ('60000000-0000-4000-8000-000000000005','authenticated','authenticated','delete-unlinked@example.invalid','',now(),'{}','{}',now(),now()),
 ('60000000-0000-4000-8000-000000000006','authenticated','authenticated','delete-nonadmin@example.invalid','',now(),'{}','{}',now(),now());
insert into beta_private.admin_users(user_id) values('60000000-0000-4000-8000-000000000001');

insert into beta_private.beta_invites(id,code_hash,status,created_by,redeemed_by,redeemed_at,discord_user_id,discord_display_name)
values
 ('61000000-0000-4000-8000-000000000001',decode(repeat('61',32),'hex'),'redeemed','60000000-0000-4000-8000-000000000001','60000000-0000-4000-8000-000000000002',now(),'123456789012345671','Active tester'),
 ('61000000-0000-4000-8000-000000000002',decode(repeat('62',32),'hex'),'redeemed','60000000-0000-4000-8000-000000000001','60000000-0000-4000-8000-000000000003',now(),'123456789012345672','Revoked tester'),
 ('61000000-0000-4000-8000-000000000003',decode(repeat('63',32),'hex'),'redeemed','60000000-0000-4000-8000-000000000001','60000000-0000-4000-8000-000000000004',now(),'123456789012345673','Expired tester'),
 ('61000000-0000-4000-8000-000000000004',decode(repeat('64',32),'hex'),'redeemed','60000000-0000-4000-8000-000000000001','60000000-0000-4000-8000-000000000005',now(),null,null),
 ('61000000-0000-4000-8000-000000000005',decode(repeat('65',32),'hex'),'redeemed','60000000-0000-4000-8000-000000000001','60000000-0000-4000-8000-000000000006',now(),null,null);
insert into beta_private.licenses(id,user_id,invite_id,status,revoked_at,revoked_by,expires_at)
values
 ('62000000-0000-4000-8000-000000000001','60000000-0000-4000-8000-000000000002','61000000-0000-4000-8000-000000000001','active',null,null,null),
 ('62000000-0000-4000-8000-000000000002','60000000-0000-4000-8000-000000000003','61000000-0000-4000-8000-000000000002','revoked',now(),'60000000-0000-4000-8000-000000000001',null),
 ('62000000-0000-4000-8000-000000000003','60000000-0000-4000-8000-000000000004','61000000-0000-4000-8000-000000000003','expired',null,null,now()-interval '1 day'),
 ('62000000-0000-4000-8000-000000000004','60000000-0000-4000-8000-000000000005','61000000-0000-4000-8000-000000000004','active',null,null,null),
 ('62000000-0000-4000-8000-000000000005','60000000-0000-4000-8000-000000000006','61000000-0000-4000-8000-000000000005','active',null,null,null);

select set_config('request.jwt.claim.role','service_role',true);
set local role service_role;

select throws_ok($$select public.beta_admin_delete_license('60000000-0000-4000-8000-000000000006','62000000-0000-4000-8000-000000000005')$$,'P0001','not_authorized','non-administrator cannot delete a license');
select ok(exists(select 1 from beta_private.licenses where id='62000000-0000-4000-8000-000000000005'),'unauthorized deletion leaves the license intact');

select is((public.beta_admin_delete_license('60000000-0000-4000-8000-000000000001','62000000-0000-4000-8000-000000000001')->>'ok')::boolean,true,'active license can be permanently deleted');
select ok(not exists(select 1 from beta_private.licenses where id='62000000-0000-4000-8000-000000000001'),'active license row is removed');
select ok(exists(select 1 from beta_private.beta_invites where id='61000000-0000-4000-8000-000000000001' and status='revoked' and redeemed_by is null and redeemed_at is null),'linked invitation is retained but invalidated as revoked');
select ok(exists(select 1 from beta_private.license_events where target_id='61000000-0000-4000-8000-000000000001' and action='invite_revoked' and metadata->>'reason'='license_deleted'),'linked invitation invalidation is independently audited');
select ok(exists(select 1 from auth.users where id='60000000-0000-4000-8000-000000000002'),'linked Supabase account is not deleted');
select ok(exists(select 1 from beta_private.license_events where target_id='62000000-0000-4000-8000-000000000001' and action='license_deleted' and metadata->>'invite_id'='61000000-0000-4000-8000-000000000001' and metadata->>'license_status'='active'),'deletion audit snapshot remains with the original license relationship');

select is((public.beta_admin_delete_license('60000000-0000-4000-8000-000000000001','62000000-0000-4000-8000-000000000002')->>'ok')::boolean,true,'revoked license can be permanently deleted');
select ok(not exists(select 1 from beta_private.licenses where id='62000000-0000-4000-8000-000000000002'),'revoked license row is removed');
select is((public.beta_admin_delete_license('60000000-0000-4000-8000-000000000001','62000000-0000-4000-8000-000000000003')->>'ok')::boolean,true,'expired license can be permanently deleted');
select ok(not exists(select 1 from beta_private.licenses where id='62000000-0000-4000-8000-000000000003'),'expired license row is removed');

select is((public.beta_admin_delete_license('60000000-0000-4000-8000-000000000001','62000000-0000-4000-8000-000000000004')->>'ok')::boolean,true,'license without a Discord association can be deleted');
select ok(exists(select 1 from beta_private.license_events where target_id='62000000-0000-4000-8000-000000000004' and action='license_deleted' and metadata->>'discord_user_id' is null),'unlinked identity is represented safely in audit metadata');
select ok(exists(select 1 from beta_private.beta_invites where id='61000000-0000-4000-8000-000000000004' and status='revoked'),'the invitation relation is retained and revoked after license deletion');

select is((public.beta_admin_delete_license('60000000-0000-4000-8000-000000000001','62000000-0000-4000-8000-000000000004')->>'ok')::boolean,false,'repeated deletion reports that the license no longer exists');
select ok(not exists(select 1 from beta_private.license_events where target_id='62000000-0000-4000-8000-000000000004' and action='license_deleted' group by target_id having count(*) > 1),'repeated deletion does not duplicate successful audit events');

reset role;
create function public.test_fail_license_delete_audit() returns trigger language plpgsql as $$
begin
  if new.action = 'license_deleted' then raise exception using errcode='P0001', message='injected_audit_failure'; end if;
  return new;
end $$;
create trigger test_fail_license_delete_audit before insert on beta_private.license_events
for each row execute function public.test_fail_license_delete_audit();
set local role service_role;
select throws_ok($$select public.beta_admin_delete_license('60000000-0000-4000-8000-000000000001','62000000-0000-4000-8000-000000000005')$$,'P0001','injected_audit_failure','database audit failure aborts deletion');
select ok(exists(select 1 from beta_private.licenses where id='62000000-0000-4000-8000-000000000005'),'database failure leaves the license intact');
select ok(not exists(select 1 from beta_private.license_events where target_id='62000000-0000-4000-8000-000000000005' and action='license_deleted'),'failed deletion creates no false success audit event');

reset role;
drop trigger test_fail_license_delete_audit on beta_private.license_events;
drop function public.test_fail_license_delete_audit();
select * from finish();
rollback;
