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
