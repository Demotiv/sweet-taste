-- Sweet Taste: часть 5 из 5 — сборная replace_snapshot.
-- Применять последней, после 003_part1..4.sql. Файл идемпотентен.
--
-- Функция очищает строки пользователя в обратном порядке зависимостей и
-- собирает снапшот из четырёх помощников. Всё в одной транзакции: обрыв сети
-- не оставит облако пустым или недособранным.

create or replace function public.replace_snapshot(
    p_schema_version integer,
    p_taken_at       timestamptz,
    p_tables         jsonb
)
returns jsonb
language plpgsql
security invoker
set search_path = public
as $fn$
declare
    v_user_id uuid := auth.uid();
    v_counts  jsonb := '{}'::jsonb;
begin
    if v_user_id is null then
        raise exception 'replace_snapshot: нет пользователя в сессии'
            using errcode = '42501';
    end if;

    if p_tables is null or jsonb_typeof(p_tables) <> 'object' then
        raise exception 'replace_snapshot: p_tables должен быть объектом'
            using errcode = '22023';
    end if;

    -- Удаление в обратном порядке зависимостей: сначала дети, потом родители.
    delete from public.order_packaging       where user_id = v_user_id;
    delete from public.packaging_items       where user_id = v_user_id;
    delete from public.order_items           where user_id = v_user_id;
    delete from public.orders                where user_id = v_user_id;
    delete from public.customers             where user_id = v_user_id;
    delete from public.recipe_ingredients    where user_id = v_user_id;
    delete from public.recipes               where user_id = v_user_id;
    delete from public.purchases             where user_id = v_user_id;
    delete from public.ingredients           where user_id = v_user_id;
    delete from public.packaging_categories  where user_id = v_user_id;
    delete from public.recipe_categories     where user_id = v_user_id;
    delete from public.ingredient_categories where user_id = v_user_id;

    -- Вставка в прямом порядке: сначала родители, потом дети.
    v_counts := v_counts || public._replace_snapshot_refs(v_user_id, p_tables);
    v_counts := v_counts || public._replace_snapshot_recipes(v_user_id, p_tables);
    v_counts := v_counts || public._replace_snapshot_orders(v_user_id, p_tables);
    v_counts := v_counts || public._replace_snapshot_packaging(v_user_id, p_tables);

    insert into public.sync_state (user_id, schema_version, last_snapshot_at, row_counts)
    values (v_user_id, p_schema_version, p_taken_at, v_counts)
    on conflict (user_id) do update
        set schema_version   = excluded.schema_version,
            last_snapshot_at = excluded.last_snapshot_at,
            row_counts       = excluded.row_counts;

    return v_counts;
end;
$fn$;

-- Функции вызываются только вошедшим пользователем.
revoke all on function public.replace_snapshot(integer, timestamptz, jsonb) from public;
revoke all on function public.replace_snapshot(integer, timestamptz, jsonb) from anon;
grant execute on function public.replace_snapshot(integer, timestamptz, jsonb) to authenticated;

revoke all on function public._replace_snapshot_refs(uuid, jsonb) from public;
revoke all on function public._replace_snapshot_refs(uuid, jsonb) from anon;
grant execute on function public._replace_snapshot_refs(uuid, jsonb) to authenticated;

revoke all on function public._replace_snapshot_recipes(uuid, jsonb) from public;
revoke all on function public._replace_snapshot_recipes(uuid, jsonb) from anon;
grant execute on function public._replace_snapshot_recipes(uuid, jsonb) to authenticated;

revoke all on function public._replace_snapshot_orders(uuid, jsonb) from public;
revoke all on function public._replace_snapshot_orders(uuid, jsonb) from anon;
grant execute on function public._replace_snapshot_orders(uuid, jsonb) to authenticated;

revoke all on function public._replace_snapshot_packaging(uuid, jsonb) from public;
revoke all on function public._replace_snapshot_packaging(uuid, jsonb) from anon;
grant execute on function public._replace_snapshot_packaging(uuid, jsonb) to authenticated;
