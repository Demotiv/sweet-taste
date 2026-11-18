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
