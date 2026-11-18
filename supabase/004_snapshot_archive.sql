-- Sweet Taste: история снапшотов в облаке (миграция 004).
-- Применять после 003_sync_rpc.sql. Файл идемпотентен, как и части.
--
-- Зачем: replace_snapshot заменяет снапшот целиком, поэтому состояние «до»
-- теряется. archive_snapshot снимает копию текущих 12 таблиц в
-- snapshot_archive перед заменой, а лишние копии обрезает до p_keep.
--
-- Полный текст миграции 004. Для панели Supabase применять по частям:
-- parts/004_part1.sql (таблица, индекс, политика), parts/004_part2.sql
-- (сборка таблиц в jsonb), parts/004_part3.sql (archive_snapshot и гранты).
-- Порядок обязателен: часть 3 вызывает функцию из части 2.
--
-- Ниже — полный текст всех трёх частей подряд: для чтения и для случая, когда
-- редактор позволяет вставить файл целиком.

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

-- Sweet Taste: часть 2 из 3 — сборка всех 12 таблиц в один jsonb.
-- Применять после 004_part1.sql. Файл идемпотентен.
--
-- Отдельная функция только из-за лимита редактора панели Supabase (~100 строк):
-- двенадцать подзапросов jsonb_agg раздувают тело archive_snapshot. Сама по себе
-- она бесполезна и вызывается лишь из archive_snapshot (часть 3).
--
-- Порядок ключей и сортировка повторяют SnapshotRows.LoadOrder и SQL в
-- SnapshotExporter: архив должен совпадать с тем, что выгружает клиент, иначе
-- сравнение на дубликат в archive_snapshot не сработает.
-- user_id вырезан: владелец архива задан колонкой snapshot_archive.user_id,
-- и дублировать его в каждой строке незачем.

create or replace function public._archive_snapshot_tables(p_user_id uuid)
returns jsonb
language sql
stable
security invoker
set search_path = public
as $fn$
select jsonb_build_object(
    'ingredient_categories', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.created_at, t.id) from public.ingredient_categories t
        where t.user_id = p_user_id), '[]'::jsonb),
    'recipe_categories', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.created_at, t.id) from public.recipe_categories t
        where t.user_id = p_user_id), '[]'::jsonb),
    'packaging_categories', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.created_at, t.id) from public.packaging_categories t
        where t.user_id = p_user_id), '[]'::jsonb),
    'ingredients', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.created_at, t.id) from public.ingredients t
        where t.user_id = p_user_id), '[]'::jsonb),
    'purchases', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.purchased_on, t.id) from public.purchases t
        where t.user_id = p_user_id), '[]'::jsonb),
    'recipes', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.created_at, t.id) from public.recipes t
        where t.user_id = p_user_id), '[]'::jsonb),
    'recipe_ingredients', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.recipe_id, t.position) from public.recipe_ingredients t
        where t.user_id = p_user_id), '[]'::jsonb),
    'customers', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.number) from public.customers t
        where t.user_id = p_user_id), '[]'::jsonb),
    'orders', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.number) from public.orders t
        where t.user_id = p_user_id), '[]'::jsonb),
    'order_items', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.order_id, t.id) from public.order_items t
        where t.user_id = p_user_id), '[]'::jsonb),
    'packaging_items', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.created_at, t.id) from public.packaging_items t
        where t.user_id = p_user_id), '[]'::jsonb),
    'order_packaging', coalesce((select jsonb_agg(to_jsonb(t) - 'user_id'
        order by t.order_id, t.position) from public.order_packaging t
        where t.user_id = p_user_id), '[]'::jsonb)
);
$fn$;

-- Помощник не является точкой входа: снаружи его зовут только через
-- archive_snapshot, поэтому права на прямой вызов ему не нужны.
revoke all on function public._archive_snapshot_tables(uuid) from public, anon;
grant execute on function public._archive_snapshot_tables(uuid) to authenticated;

-- Sweet Taste: часть 3 из 3 — функция archive_snapshot.
-- Применять после 004_part1.sql и 004_part2.sql. Файл идемпотентен.
--
-- Вызывается перед replace_snapshot: снимок берётся до того, как замена сотрёт
-- строки. Клиент не присылает данные второй раз — архив читает то, что уже
-- лежит в облаке, и потому не может разойтись с заменяемым снапшотом.
--
-- Вызов: POST /rest/v1/rpc/archive_snapshot  {"p_keep": 10}
-- Ответ: {"archived": false, "reason": "no_state" | "empty" | "duplicate"}
--        либо {"archived": true, "id": ..., "total_rows": ..., "row_counts": ...}
--
-- Тело обёрнуто в $fn$, а не в удвоенные доллары: на паре знаков доллара
-- редактор панели Supabase обрывает текст, и функция приезжает битой.

create or replace function public.archive_snapshot(p_keep integer default 10)
returns jsonb
language plpgsql
security invoker
set search_path = public
as $fn$
declare
    v_user_id uuid := auth.uid();
    v_state   public.sync_state;
    v_tables  jsonb;
    v_counts  jsonb;
    v_total   integer;
    v_id      uuid;
begin
    if v_user_id is null then
        raise exception 'Требуется вход' using errcode = '42501';
    end if;

    -- p_keep меньше единицы не может означать «не хранить ничего»: обрезка
    -- стёрла бы только что вставленную строку, поэтому это просто «по умолчанию».
    if p_keep is null or p_keep < 1 then
        p_keep := 10;
    end if;

    -- По умолчанию to_jsonb(bytea) печатает фото в escape-формате, а клиент
    -- присылает и ждёт \x...: без hex архив не совпадёт с исходным снапшотом.
    perform set_config('bytea_output', 'hex', true);

    select * into v_state from public.sync_state where user_id = v_user_id;
    if not found then
        return jsonb_build_object('archived', false, 'reason', 'no_state');
    end if;

    v_tables := public._archive_snapshot_tables(v_user_id);

    select coalesce(jsonb_object_agg(key, jsonb_array_length(value)), '{}'::jsonb),
           coalesce(sum(jsonb_array_length(value)), 0)::integer
      into v_counts, v_total
      from jsonb_each(v_tables);

    -- Пустой архив бесполезен: он ничего не восстановит, зато вытеснит
    -- настоящую историю при обрезке.
    if v_total = 0 then
        return jsonb_build_object('archived', false, 'reason', 'empty');
    end if;

    -- Сравнение jsonb нормализовано по ключам, но чувствительно к порядку
    -- строк в массивах, поэтому повторная выгрузка без правок даёт duplicate.
    if exists (
        select 1 from public.snapshot_archive a
        where a.user_id = v_user_id and a.tables = v_tables
    ) then
        return jsonb_build_object('archived', false, 'reason', 'duplicate');
    end if;

    insert into public.snapshot_archive (
        user_id, taken_at, schema_version, total_rows, row_counts, tables)
    values (v_user_id, coalesce(v_state.last_snapshot_at, now()),
            v_state.schema_version, v_total, v_counts, v_tables)
    returning id into v_id;

    delete from public.snapshot_archive
    where user_id = v_user_id
      and id not in (
          select a.id from public.snapshot_archive a
          where a.user_id = v_user_id
          order by a.archived_at desc, a.id
          limit p_keep);

    return jsonb_build_object(
        'archived', true, 'id', v_id,
        'taken_at', v_state.last_snapshot_at,
        'schema_version', v_state.schema_version,
        'total_rows', v_total, 'row_counts', v_counts);
end;
$fn$;

-- Архив — только для вошедшего пользователя: anon не должен ни читать
-- историю, ни тратить дисковое пространство проекта.
revoke all on function public.archive_snapshot(integer) from public, anon;
grant execute on function public.archive_snapshot(integer) to authenticated;
