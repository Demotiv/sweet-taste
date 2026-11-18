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
-- Все функции security invoker: RLS действует как обычно, user_id берётся
-- из auth.uid(). Чужие строки удалить или вставить нельзя.
--
-- ВАЖНО: редактор панели Supabase ограничен ~100 строками, поэтому файл
-- применяется пятью частями из supabase/parts/ (003_part1..5.sql) по порядку.
-- Каждая часть — законченный оператор create or replace function: тело функции
-- нельзя резать, незакрытая кавычка тела даёт 42601 ещё до выполнения.
-- Части 1-4 создают функции-помощники для групп таблиц, часть 5 — сборную
-- replace_snapshot, которая их вызывает.
--
-- Ниже — полный текст всех пяти частей подряд: для чтения и для случая, когда
-- редактор позволяет вставить файл целиком.

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

-- ---------------------------------------------------------------------------
-- Часть 2: рецепты и состав
-- ---------------------------------------------------------------------------

create or replace function public._replace_snapshot_recipes(
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
    insert into public.recipes (
        user_id, id, name, category_id, output_quantity_milli, output_unit,
        markup_percent, technology, photo, created_at, updated_at, deleted_at)
    select p_user_id, r.id, r.name, r.category_id, r.output_quantity_milli, r.output_unit,
           r.markup_percent, r.technology, r.photo, r.created_at, r.updated_at, r.deleted_at
    from jsonb_to_recordset(coalesce(p_tables -> 'recipes', '[]'::jsonb))
        as r(id uuid, name text, category_id uuid, output_quantity_milli bigint, output_unit text,
             markup_percent integer, technology text, photo bytea,
             created_at timestamptz, updated_at timestamptz, deleted_at timestamptz);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('recipes', v_count);

    insert into public.recipe_ingredients (user_id, recipe_id, ingredient_id, amount_milli, position)
    select p_user_id, r.recipe_id, r.ingredient_id, r.amount_milli, r.position
    from jsonb_to_recordset(coalesce(p_tables -> 'recipe_ingredients', '[]'::jsonb))
        as r(recipe_id uuid, ingredient_id uuid, amount_milli bigint, position integer);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('recipe_ingredients', v_count);

    return v_counts;
end;
$fn$;

-- ---------------------------------------------------------------------------
-- Часть 3: клиенты, заказы и позиции заказов
-- ---------------------------------------------------------------------------

create or replace function public._replace_snapshot_orders(
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
    insert into public.customers (
        user_id, id, number, first_name, last_name, phone, phone_normalized,
        created_at, updated_at)
    select p_user_id, r.id, r.number, r.first_name, r.last_name, r.phone, r.phone_normalized,
           r.created_at, r.updated_at
    from jsonb_to_recordset(coalesce(p_tables -> 'customers', '[]'::jsonb))
        as r(id uuid, number integer, first_name text, last_name text, phone text,
             phone_normalized text, created_at timestamptz, updated_at timestamptz);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('customers', v_count);

    insert into public.orders (
        user_id, id, number, first_name, last_name, phone, due_date, status, comment,
        total_price_kopecks, paid_amount_kopecks, discount_kopecks, customer_id,
        created_at, updated_at)
    select p_user_id, r.id, r.number, r.first_name, r.last_name, r.phone, r.due_date,
           r.status, r.comment, r.total_price_kopecks, r.paid_amount_kopecks,
           r.discount_kopecks, r.customer_id, r.created_at, r.updated_at
    from jsonb_to_recordset(coalesce(p_tables -> 'orders', '[]'::jsonb))
        as r(id uuid, number integer, first_name text, last_name text, phone text,
             due_date date, status text, comment text, total_price_kopecks bigint,
             paid_amount_kopecks bigint, discount_kopecks bigint, customer_id uuid,
             created_at timestamptz, updated_at timestamptz);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('orders', v_count);

    insert into public.order_items (
        user_id, id, order_id, recipe_id, recipe_name, quantity_milli,
        unit_price_kopecks, cost_price_kopecks, cost_estimated,
        output_quantity_milli, output_unit)
    select p_user_id, r.id, r.order_id, r.recipe_id, r.recipe_name, r.quantity_milli,
           r.unit_price_kopecks, r.cost_price_kopecks, r.cost_estimated,
           r.output_quantity_milli, r.output_unit
    from jsonb_to_recordset(coalesce(p_tables -> 'order_items', '[]'::jsonb))
        as r(id uuid, order_id uuid, recipe_id uuid, recipe_name text, quantity_milli bigint,
             unit_price_kopecks bigint, cost_price_kopecks bigint, cost_estimated boolean,
             output_quantity_milli bigint, output_unit text);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('order_items', v_count);

    return v_counts;
end;
$fn$;

-- ---------------------------------------------------------------------------
-- Часть 4: упаковка и упаковка в заказах
-- ---------------------------------------------------------------------------

create or replace function public._replace_snapshot_packaging(
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
    insert into public.packaging_items (
        user_id, id, category_id, name, size, price_kopecks, created_at, updated_at)
    select p_user_id, r.id, r.category_id, r.name, r.size, r.price_kopecks,
           r.created_at, r.updated_at
    from jsonb_to_recordset(coalesce(p_tables -> 'packaging_items', '[]'::jsonb))
        as r(id uuid, category_id uuid, name text, size text, price_kopecks bigint,
             created_at timestamptz, updated_at timestamptz);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('packaging_items', v_count);

    insert into public.order_packaging (
        user_id, id, order_id, packaging_id, name, size, cost_kopecks,
        sale_price_kopecks, quantity, position)
    select p_user_id, r.id, r.order_id, r.packaging_id, r.name, r.size, r.cost_kopecks,
           r.sale_price_kopecks, r.quantity, r.position
    from jsonb_to_recordset(coalesce(p_tables -> 'order_packaging', '[]'::jsonb))
        as r(id uuid, order_id uuid, packaging_id uuid, name text, size text,
             cost_kopecks bigint, sale_price_kopecks bigint, quantity integer, position integer);
    get diagnostics v_count = row_count;
    v_counts := v_counts || jsonb_build_object('order_packaging', v_count);

    return v_counts;
end;
$fn$;

-- ---------------------------------------------------------------------------
-- Часть 5: сборная replace_snapshot
-- ---------------------------------------------------------------------------

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
