-- Sweet Taste: история снапшотов в облаке — таблица архива.
-- Применять после 001_schema.sql, до 004_part2.sql. Файл идемпотентен.
--
-- Зачем: replace_snapshot стирает строки пользователя и вставляет новые, так что
-- предыдущее состояние облака исчезает. Архив держит снимок тех же 12 таблиц
-- одной строкой jsonb: восстановление — это повторный вызов replace_snapshot с
-- этими tables, и вторая реляционная схема, которую надо держать в согласии с
-- первой, не нужна.
--
-- row_counts лежит рядом с tables, чтобы список архивов рисовался без
-- распаковки всего jsonb.
--
-- ВАЖНО: редактор панели Supabase ограничен ~100 строками, поэтому миграция
-- применяется частями (004_part1..3.sql) по порядку. Части — целые операторы:
-- тело функции нельзя резать, незакрытая кавычка даёт 42601 ещё до выполнения.

create table if not exists public.snapshot_archive (
    user_id        uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id             uuid not null default gen_random_uuid(),
    taken_at       timestamptz not null,
    schema_version integer not null,
    total_rows     integer not null,
    row_counts     jsonb not null default '{}'::jsonb,
    tables         jsonb not null,
    archived_at    timestamptz not null default now(),
    primary key (user_id, id)
);

-- Обрезка старых архивов и показ последних идут от свежих к старым,
-- поэтому индекс совпадает с порядком в запросах archive_snapshot.
create index if not exists ix_snapshot_archive_recent
    on public.snapshot_archive (user_id, archived_at desc);

alter table public.snapshot_archive enable row level security;
drop policy if exists own_rows on public.snapshot_archive;
create policy own_rows on public.snapshot_archive
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));
