-- Sweet Taste: атомарная замена снапшота в облаке.
-- Применять после 002_policies.sql. Файл идемпотентен.
--
-- Зачем: выгрузка через PostgREST идёт отдельными запросами (удалить, вставить,
-- вставить, ...). Если сеть пропадёт посередине, в облаке останется пустая или
-- недособранная копия. Функция replace_snapshot делает всё в одной транзакции:
-- либо снапшот заменён целиком, либо облако осталось прежним.
--
-- Вызов: POST /rest/v1/rpc/replace_snapshot
--   { "p_schema_version": 15, "p_taken_at": "...", "p_tables": { "ingredients": [...], ... } }
--
-- ВАЖНО: редактор панели Supabase ограничен ~100 строками, поэтому файл
-- применяется пятью частями из supabase/parts/ (003_part1..5.sql) по порядку.
-- Каждая часть — законченный оператор create or replace function: тело функции
-- нельзя резать, незакрытая кавычка тела даёт 42601 ещё до выполнения.
-- Части 1-4 создают функции-помощники для групп таблиц, часть 5 — сборную
-- replace_snapshot, которая их вызывает.
--
-- Все функции security invoker: RLS действует как обычно, user_id берётся
-- из auth.uid(). Чужие строки удалить или вставить нельзя.

-- ---------------------------------------------------------------------------
-- Часть 1: справочники, ингредиенты, закупки
-- ---------------------------------------------------------------------------

create or replace function public._replace_snapshot_refs(
    p_user_id uuid,
    p_tables  jsonb
)
returns jsonb
language plpgsql
security invoker
set search_path = public
as $fn$
declare
    v_counts jsonb := '{}'::jsonb;
    v_count  integer;
begin
    insert into public.ingredient_categories (user_id, id, name, created_at)
    select p_user_id, r.id, r.name, r.created_at
    from jsonb_to_recordset(coalesce(p_tables -> 'ingredient_categories', '[]'::jsonb))
        as r(id uuid, name text, created_at timestamptz);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('ingredient_categories', v_count);

    insert into public.recipe_categories (user_id, id, name, created_at)
    select p_user_id, r.id, r.name, r.created_at
    from jsonb_to_recordset(coalesce(p_tables -> 'recipe_categories', '[]'::jsonb))
        as r(id uuid, name text, created_at timestamptz);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('recipe_categories', v_count);

    insert into public.packaging_categories (user_id, id, name, created_at)
    select p_user_id, r.id, r.name, r.created_at
    from jsonb_to_recordset(coalesce(p_tables -> 'packaging_categories', '[]'::jsonb))
        as r(id uuid, name text, created_at timestamptz);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('packaging_categories', v_count);

    insert into public.ingredients (
        user_id, id, name, brand, unit, category_id, created_at, updated_at, deleted_at)
    select p_user_id, r.id, r.name, r.brand, r.unit, r.category_id,
           r.created_at, r.updated_at, r.deleted_at
    from jsonb_to_recordset(coalesce(p_tables -> 'ingredients', '[]'::jsonb))
        as r(id uuid, name text, brand text, unit text, category_id uuid,
             created_at timestamptz, updated_at timestamptz, deleted_at timestamptz);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('ingredients', v_count);

    insert into public.purchases (
        user_id, id, ingredient_id, purchased_on, store, quantity_milli,
        total_price_kopecks, order_id, note, created_at)
    select p_user_id, r.id, r.ingredient_id, r.purchased_on, r.store, r.quantity_milli,
           r.total_price_kopecks, r.order_id, r.note, r.created_at
    from jsonb_to_recordset(coalesce(p_tables -> 'purchases', '[]'::jsonb))
        as r(id uuid, ingredient_id uuid, purchased_on date, store text, quantity_milli bigint,
             total_price_kopecks bigint, order_id uuid, note text, created_at timestamptz);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('purchases', v_count);

    return v_counts;
end;
$fn$;
