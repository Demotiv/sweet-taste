-- Sweet Taste: часть 2 из 5 — рецепты и состав.
-- Применять после 003_part1.sql. Файл идемпотентен.
-- Вызывается из replace_snapshot (часть 5) после справочников и ингредиентов.

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
