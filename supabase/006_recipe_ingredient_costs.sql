-- Sweet Taste: состав рецепта с ценами в облаке (миграция 006).
-- Применять после 005_recipe_costs.sql. Файл идемпотентен.
--
-- Зачем: карточке рецепта в веб-интерфейсе нужен состав — ингредиент,
-- количество на партию, средняя цена и вклад в себестоимость. Средняя цена
-- в облаке не хранится, она считается из закупок, поэтому формула живёт
-- здесь, а не в TypeScript (см. 005_recipe_costs.sql).
--
-- Формула повторяет RecipeService.GetRecipeDetails шаг в шаг:
--   avg_price = sum(total_price_kopecks) / sum(quantity_milli) * 1000 / 100
--               по закупкам: копейки / тысячные доли единицы -> рубли за единицу
--   cost      = amount_milli * avg_price / 1000
--               тысячные доли единицы * рубли за единицу -> рубли
-- Ингредиент без закупок даёт avg_price = null и вклад 0 — как в desktop.
--
-- Строки удалённых ингредиентов не отбрасываются: desktop показывает их
-- в составе (GetIngredients(includeDeleted: true)), иначе сумма вкладов
-- не сошлась бы с batch_cost из recipe_costs.
--
-- Вью security invoker: RLS таблиц действует как обычно, чужие строки не видны.
-- Отдельная политика вью не нужна и не должна создаваться — она бы обошла RLS.
--
-- ВАЖНО: редактор панели Supabase ограничен ~100 строкой, поэтому файл
-- применяется одной частью из supabase/parts/006_part1.sql. Полный текст
-- ниже совпадает с ней байт в байт.

-- ---------------------------------------------------------------------------
-- Часть 1: вью recipe_ingredient_costs
-- ---------------------------------------------------------------------------

create or replace view public.recipe_ingredient_costs
with (security_invoker = true)
as
with avg_prices as (
    select p.user_id,
           p.ingredient_id,
           sum(p.total_price_kopecks)::numeric / sum(p.quantity_milli) * 1000 / 100 as avg_price
    from public.purchases p
    group by p.user_id, p.ingredient_id
    having sum(p.quantity_milli) > 0
)
select ri.user_id,
       ri.recipe_id,
       ri.ingredient_id,
       ri.position,
       ri.amount_milli,
       i.name as ingredient_name,
       i.unit as ingredient_unit,
       ap.avg_price,
       ri.amount_milli * coalesce(ap.avg_price, 0) / 1000 as cost
from public.recipe_ingredients ri
join public.ingredients i
    on i.user_id = ri.user_id
   and i.id = ri.ingredient_id
left join avg_prices ap
    on ap.user_id = ri.user_id
   and ap.ingredient_id = ri.ingredient_id;

-- Вью читается только вошедшим пользователем: anon не должен видеть ни
-- себестоимость, ни цены. RLS таблиц под вью всё равно отсечёт чужие строки,
-- но грант на anon открыл бы саму вью в списке схемы.
revoke all on public.recipe_ingredient_costs from public, anon;
grant select on public.recipe_ingredient_costs to authenticated;
