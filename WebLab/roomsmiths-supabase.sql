-- Run in the SQL Editor of the Roomsmiths Supabase project.
insert into storage.buckets (id, name, public, file_size_limit)
values ('roomsmiths-creations', 'roomsmiths-creations', false, 20971520)
on conflict (id) do update set public = false, file_size_limit = excluded.file_size_limit;

create table if not exists public.roomsmiths_collections (
  code text primary key,
  owner text not null,
  expires_at timestamptz not null
);
create index if not exists roomsmiths_collections_owner on public.roomsmiths_collections(owner, expires_at desc);
create table if not exists public.roomsmiths_uploads (
  revision uuid primary key,
  code text not null references public.roomsmiths_collections(code),
  work_id text not null,
  name text not null,
  description text not null,
  created_on text,
  files jsonb not null,
  total_bytes bigint not null,
  created_at timestamptz not null default now(),
  committed_at timestamptz,
  retired boolean not null default false
);
create index if not exists roomsmiths_uploads_code on public.roomsmiths_uploads(code, committed_at desc);
alter table public.roomsmiths_collections enable row level security;
alter table public.roomsmiths_uploads enable row level security;
revoke all on public.roomsmiths_collections, public.roomsmiths_uploads from anon, authenticated;
grant select, insert, update, delete on public.roomsmiths_collections, public.roomsmiths_uploads to service_role;

create or replace function public.roomsmiths_reserve(
  p_owner text, p_code text, p_revision uuid, p_work text, p_name text,
  p_description text, p_date text, p_files jsonb, p_bytes integer
) returns jsonb language plpgsql set search_path = '' as $$
declare picked public.roomsmiths_collections;
begin
  -- Serialize the small quota update across Worker instances.
  perform pg_advisory_xact_lock(72440741);
  if (select coalesce(sum(total_bytes), 0) from public.roomsmiths_uploads) + p_bytes > 681574400 then
    return jsonb_build_object('error', 'storage-full');
  end if;
  select * into picked from public.roomsmiths_collections
    where owner = p_owner and expires_at > now() + interval '5 minutes'
    order by expires_at desc limit 1;
  if not found then
    insert into public.roomsmiths_collections values (p_code, p_owner, now() + interval '7 days')
      on conflict do nothing returning * into picked;
    if not found then return jsonb_build_object('error','code-collision'); end if;
  end if;
  if (select count(*) from public.roomsmiths_uploads where code = picked.code and not retired) >= 80
    and not exists (select 1 from public.roomsmiths_uploads where code = picked.code and work_id = p_work
      and committed_at is not null and not retired)
  then return jsonb_build_object('error','collection-full'); end if;
  insert into public.roomsmiths_uploads (revision, code, work_id, name, description, created_on, files, total_bytes)
    values (p_revision, picked.code, p_work, p_name, p_description, p_date, p_files, p_bytes);
  return jsonb_build_object('revision', p_revision);
end $$;

create or replace function public.roomsmiths_commit(p_owner text, p_revision uuid)
returns jsonb language plpgsql set search_path = '' as $$
declare target public.roomsmiths_uploads; collection public.roomsmiths_collections;
begin
  perform pg_advisory_xact_lock(72440741);
  select * into target from public.roomsmiths_uploads where revision = p_revision;
  if not found or target.retired then return null; end if;
  select * into collection from public.roomsmiths_collections
    where code = target.code and owner = p_owner and expires_at > now();
  if not found then return null; end if;
  if target.committed_at is null then
    if target.created_at < now() - interval '1 hour' then return null; end if;
    if exists (
      select 1 from jsonb_each_text(target.files) entry
      where not exists (select 1 from storage.objects o where o.bucket_id = 'roomsmiths-creations'
        and o.name = target.revision::text || '/' || entry.key
        and o.metadata->>'size' = entry.value)
    ) then return null; end if;
    update public.roomsmiths_uploads set retired = true
      where code = target.code and work_id = target.work_id and committed_at is not null and revision <> p_revision;
    update public.roomsmiths_uploads set committed_at = now() where revision = p_revision;
  end if;
  return jsonb_build_object('code', collection.code, 'expires', collection.expires_at);
end $$;

create or replace function public.roomsmiths_expired_uploads()
returns table(revision uuid, files jsonb) language sql set search_path = '' as $$
  select u.revision, u.files from public.roomsmiths_uploads u
  join public.roomsmiths_collections c on c.code = u.code
  where c.expires_at < now() - interval '2 minutes'
     or (u.retired and u.created_at < now() - interval '1 hour')
     or (u.committed_at is null and u.created_at < now() - interval '62 minutes')
  order by u.created_at limit 35
$$;

revoke all on function public.roomsmiths_reserve(text,text,uuid,text,text,text,text,jsonb,integer) from public, anon, authenticated;
revoke all on function public.roomsmiths_commit(text,uuid) from public, anon, authenticated;
revoke all on function public.roomsmiths_expired_uploads() from public, anon, authenticated;
grant execute on function public.roomsmiths_reserve(text,text,uuid,text,text,text,text,jsonb,integer) to service_role;
grant execute on function public.roomsmiths_commit(text,uuid) to service_role;
grant execute on function public.roomsmiths_expired_uploads() to service_role;
