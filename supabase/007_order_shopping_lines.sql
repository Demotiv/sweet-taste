-- Sweet Taste: список покупок под заказы в облаке (миграция 007).
-- Применять после 006_recipe_ingredient_costs.sql. Файл идемпотентен.
--
-- Зачем: в магазине список покупок нужен в телефоне. Вью отдаёт ингредиенты
-- каждой позиции заказа, пересчитанные на заказанное количество. Сложение
-- по выбранным заказам делает веб: это сумма строк, а не новая формула.
--
-- Формула повторяет OrderService.GetOrderDetails шаг в шаг:
--   scale     = quantity_milli / output_quantity_milli   (выход ТЕКУЩЕГО рецепта)
--   amount    = amount_milli / 1000 * scale              единицы ингредиента
--   avg_price = sum(total_price_kopecks) / sum(quantity_milli) * 1000 / 100
--   cost      = avg_price * amount                       рубли
-- Как в desktop:
--   - позиция без рецепта или с удалённым рецептом строк не даёт
--     (TryGetRecipeDetails -> FindRecipe с deleted_at IS NULL);
--   - удалённые ингредиенты в составе остаются (GetIngredients(includeDeleted: true));
--   - ингредиент без закупок даёт avg_price = null и стоимость 0.
--
-- Вью security invoker: RLS таблиц действует как обычно, чужие строки не видны.
-- Отдельная политика вью не нужна и не должна создаваться — она бы обошла RLS.
--
-- ВАЖНО: редактор панели Supabase ограничен ~100 строкой, поэтому файл
-- применяется одной частью из supabase/parts/007_part1.sql. Полный текст
-- ниже совпадает с ней байт в байт.

-- ---------------------------------------------------------------------------
-- Часть 1: вью order_shopping_lines
-- ---------------------------------------------------------------------------

create or replace view public.order_shopping_lines
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
select oi.user_id,
       oi.order_id,
       oi.id as order_item_id,
       ri.ingredient_id,
       ri.position,
       i.name as ingredient_name,
       i.unit as ingredient_unit,
       ri.amount_milli::numeric / 1000
           * oi.quantity_milli / r.output_quantity_milli as amount,
       ap.avg_price,
       coalesce(ap.avg_price, 0) * ri.amount_milli / 1000
           * oi.quantity_milli / r.output_quantity_milli as cost
from public.order_items oi
join public.recipes r
    on r.user_id = oi.user_id
   and r.id = oi.recipe_id
   and r.deleted_at is null
join public.recipe_ingredients ri
    on ri.user_id = r.user_id
   and ri.recipe_id = r.id
join public.ingredients i
    on i.user_id = ri.user_id
   and i.id = ri.ingredient_id
left join avg_prices ap
    on ap.user_id = ri.user_id
   and ap.ingredient_id = ri.ingredient_id;

-- Вью читается только вошедшим пользователем: anon не должен видеть ни
-- заказы, ни цены. RLS таблиц под вью всё равно отсечёт чужие строки,
-- но грант на anon открыл бы саму вью в списке схемы.
revoke all on public.order_shopping_lines from public, anon;
grant select on public.order_shopping_lines to authenticated;
