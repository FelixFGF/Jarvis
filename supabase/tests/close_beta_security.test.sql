begin;
select plan(43);

select ok(not has_schema_privilege('anon','beta_private','usage'), 'anonymous users cannot access the private licensing schema');
select ok(not has_table_privilege('authenticated','beta_private.beta_invites','select'), 'authenticated clients cannot read invitation hashes');
select ok(not has_table_privilege('authenticated','beta_private.licenses','update'), 'authenticated clients cannot change licenses');
select ok(not has_function_privilege('anon','public.beta_create_invites(jsonb,timestamptz,text,uuid)','execute'), 'anonymous clients cannot create invitations');

insert into auth.users(id,aud,role,email,encrypted_password,email_confirmed_at,raw_app_meta_data,raw_user_meta_data,created_at,updated_at)
values
 ('10000000-0000-4000-8000-000000000001','authenticated','authenticated','beta-admin@example.invalid','',now(),'{}','{}',now(),now()),
 ('10000000-0000-4000-8000-000000000002','authenticated','authenticated','beta-tester@example.invalid','',now(),'{}','{}',now(),now()),
 ('10000000-0000-4000-8000-000000000003','authenticated','authenticated','other-tester@example.invalid','',now(),'{}','{}',now(),now()),
 ('10000000-0000-4000-8000-000000000004','authenticated','authenticated','pending-tester@example.invalid','',now(),'{}','{}',now(),now());
insert into beta_private.admin_users(user_id) values('10000000-0000-4000-8000-000000000001');
insert into beta_private.beta_invites(code_hash,created_by,expires_at) values(decode(repeat('bb',32),'hex'),'10000000-0000-4000-8000-000000000001',now()-interval '1 day');
select set_config('request.jwt.claim.role','service_role',true);
set local role service_role;

select lives_ok($$select public.beta_create_invites('[{"hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]'::jsonb,null,null,'10000000-0000-4000-8000-000000000001')$$, 'authorized administrator creates a hashed invitation');
select is((public.beta_redeem_invite(decode(repeat('aa',32),'hex'),'10000000-0000-4000-8000-000000000002')->>'ok')::boolean,true,'first authenticated account redeems invitation');
select is((select count(*)::integer from beta_private.licenses where invite_id=(select id from beta_private.beta_invites where code_hash=decode(repeat('aa',32),'hex'))),1,'redemption creates exactly one license');
select is((public.beta_redeem_invite(decode(repeat('aa',32),'hex'),'10000000-0000-4000-8000-000000000003')->>'ok')::boolean,false,'a second account cannot reuse a consumed invitation');
select is((public.beta_redeem_invite(decode(repeat('bb',32),'hex'),'10000000-0000-4000-8000-000000000003')->>'ok')::boolean,false,'an expired invitation is rejected');
select is((select status from beta_private.beta_invites where code_hash=decode(repeat('bb',32),'hex')),'expired','expired invitation state is persisted');
select throws_ok($$select public.beta_admin_change_license('10000000-0000-4000-8000-000000000002',(select id from beta_private.licenses where user_id='10000000-0000-4000-8000-000000000002'),'revoke')$$,'P0001','not_authorized','a regular tester cannot use administrator license operations');
select is((public.beta_validate_license('10000000-0000-4000-8000-000000000002')->>'valid')::boolean,true,'active tester license validates');
select is((public.beta_admin_change_license('10000000-0000-4000-8000-000000000001',(select id from beta_private.licenses where user_id='10000000-0000-4000-8000-000000000002'),'revoke')->>'ok')::boolean,true,'authorized administrator revokes a license');
select is((public.beta_validate_license('10000000-0000-4000-8000-000000000002')->>'valid')::boolean,false,'revoked license is denied by server validation');
select is((public.beta_admin_change_license('10000000-0000-4000-8000-000000000001',(select id from beta_private.licenses where user_id='10000000-0000-4000-8000-000000000002'),'reinstate')->>'ok')::boolean,true,'authorized administrator reinstates a license');
select is((public.beta_validate_license('10000000-0000-4000-8000-000000000002')->>'valid')::boolean,true,'reinstated license validates');

select lives_ok($$select public.beta_create_invite_discord(decode(repeat('cc',32),'hex'),null,'ticket-123','123456789012345678','Felix Beta','10000000-0000-4000-8000-000000000001')$$,'administrator creates an invitation with a string Discord snowflake and display name');
select is((public.beta_create_invite_discord(decode(repeat('dd',32),'hex'),null,null,'123456789012345678','Felix Beta','10000000-0000-4000-8000-000000000001')->>'ok')::boolean,false,'a second live invitation for the same Discord identity is rejected');
select is((public.beta_redeem_invite(decode(repeat('cc',32),'hex'),'10000000-0000-4000-8000-000000000003')->>'ok')::boolean,false,'old deployed redemption contract fails closed for Discord-bound invitations');
select throws_ok($$select public.beta_redeem_invite_discord(decode(repeat('cc',32),'hex'),'10000000-0000-4000-8000-000000000003',null,'tester','Tester',null)$$,'P0001','not_authorized','redemption rejects a missing authenticated Discord identity');
select is((public.beta_redeem_invite_discord(decode(repeat('cc',32),'hex'),'10000000-0000-4000-8000-000000000003','123456789012345679','tester','Tester',null)->>'ok')::boolean,false,'a different authenticated Discord account cannot redeem the invitation');
select is(public.beta_redeem_invite_discord(decode(repeat('cc',32),'hex'),'10000000-0000-4000-8000-000000000003','123456789012345679','tester','Tester',null)->>'reason','discord_mismatch','mismatched Discord account receives a distinct server-only diagnostic reason');
select is((public.beta_redeem_invite_discord(decode(repeat('cc',32),'hex'),'10000000-0000-4000-8000-000000000003','123456789012345678','tester','Felix Beta',null)->>'ok')::boolean,true,'matching authenticated Discord identity redeems and binds invitation');
select is((public.beta_redeem_invite_discord(decode(repeat('cc',32),'hex'),'10000000-0000-4000-8000-000000000003','123456789012345678','tester','Felix Beta',null)->>'ok')::boolean,false,'repeating redemption as the same account does not create another license');
select is(public.beta_redeem_invite_discord(decode(repeat('cc',32),'hex'),'10000000-0000-4000-8000-000000000003','123456789012345678','tester','Felix Beta',null)->>'reason','already_used','repeated redemption has a server-only used-invitation diagnostic');
select is((public.beta_admin_delete_invite('10000000-0000-4000-8000-000000000001',(select id from beta_private.beta_invites where code_hash=decode(repeat('cc',32),'hex')))->>'ok')::boolean,false,'administrator cannot delete a redeemed invitation linked to a license');
select is((public.beta_validate_license('10000000-0000-4000-8000-000000000003')->>'valid')::boolean,false,'old deployed license-validation contract fails closed for Discord-bound licenses');
select is((public.beta_redeem_invite(decode(repeat('ee',32),'hex'),'10000000-0000-4000-8000-000000000003')->>'ok')::boolean,false,'an unknown invitation code is rejected generically');
select is(public.beta_validate_license_discord('10000000-0000-4000-8000-000000000003','123456789012345678')->'discord_profile'->>'discord_display_name','Felix Beta','the authenticated user receives the server-bound verified Discord profile');
select is((public.beta_validate_license_discord('10000000-0000-4000-8000-000000000003','123456789012345679')->>'valid')::boolean,false,'a different Discord identity cannot validate the license');
select is((public.beta_admin_list_licenses('10000000-0000-4000-8000-000000000001','123456789012345678')->'licenses'->0->>'discord_identity_status'),'verified','admin license results identify OAuth-verified Discord identity');
select lives_ok($$select public.beta_create_invite_discord(decode(repeat('ff',32),'hex'),null,'revoke test','223456789012345678','Pending Tester','10000000-0000-4000-8000-000000000001')$$,'administrator creates a pending invitation for revocation checks');
select is((public.beta_admin_list_invites('10000000-0000-4000-8000-000000000001','pending','223456789012345678')->'invitations'->0->>'status'),'pending','admin invitation list returns pending identity and status');
select throws_ok($$select public.beta_admin_revoke_invite('10000000-0000-4000-8000-000000000002',(select id from beta_private.beta_invites where code_hash=decode(repeat('ff',32),'hex')))$$,'P0001','not_authorized','regular tester cannot revoke an invitation');
select is((public.beta_admin_revoke_invite('10000000-0000-4000-8000-000000000001',(select id from beta_private.beta_invites where code_hash=decode(repeat('ff',32),'hex')))->>'ok')::boolean,true,'authorized administrator revokes a pending invitation');
select is((public.beta_redeem_invite_discord(decode(repeat('ff',32),'hex'),'10000000-0000-4000-8000-000000000004','223456789012345678','tester','Pending Tester',null)->>'ok')::boolean,false,'a revoked invitation cannot be redeemed');

select lives_ok($$select public.beta_create_invite_discord(decode(repeat('99',32),'hex'),null,'delete test','323456789012345678','Delete Tester','10000000-0000-4000-8000-000000000001')$$,'administrator creates an unused invitation for safe deletion checks');
select throws_ok($$select public.beta_admin_delete_invite('10000000-0000-4000-8000-000000000002',(select id from beta_private.beta_invites where code_hash=decode(repeat('99',32),'hex')))$$,'P0001','not_authorized','regular tester cannot delete an invitation');
select is((public.beta_admin_delete_invite('10000000-0000-4000-8000-000000000001',(select id from beta_private.beta_invites where code_hash=decode(repeat('99',32),'hex')))->>'ok')::boolean,true,'authorized administrator can delete an unused invitation');
select ok(exists(select 1 from beta_private.license_events where action='invite_deleted' and actor_user_id='10000000-0000-4000-8000-000000000001' and metadata->>'discord_user_id'='323456789012345678'),'deleted invitation audit event remains even after row removal');
select lives_ok($$select public.beta_create_invite_discord(decode(repeat('88',32),'hex'),null,'expired discord test','423456789012345678','Expired Tester','10000000-0000-4000-8000-000000000001')$$,'administrator creates a Discord-bound invitation for expiry validation');
update beta_private.beta_invites set expires_at=now()-interval '1 minute' where code_hash=decode(repeat('88',32),'hex');
select is((public.beta_redeem_invite_discord(decode(repeat('88',32),'hex'),'10000000-0000-4000-8000-000000000004','423456789012345678','tester','Expired Tester',null)->>'ok')::boolean,false,'expired Discord-bound invitation is rejected');
select is(public.beta_redeem_invite_discord(decode(repeat('88',32),'hex'),'10000000-0000-4000-8000-000000000004','423456789012345678','tester','Expired Tester',null)->>'reason','expired','expired invitation has a server-only diagnostic reason');

reset role;
select * from finish();
rollback;
