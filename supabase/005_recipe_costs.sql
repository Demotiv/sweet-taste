-- Sweet Taste: себестоимость и цена рецепта в облаке (миграция 005).
-- Применять после 004_snapshot_archive.sql. Файл идемпотентен.
--
-- Зачем: цена ингредиента в облаке не хранится — она считается из закупок.
-- Читающим клиентам (веб-интерфейс кондитера, PDF-меню) нужна та же цифра,
-- что показывает desktop. Дублировать формулу в TypeScript нельзя: два
-- источника истины разойдутся на округлении. Поэтому формула живёт здесь,
-- одним SQL-объектом, и все читатели берут готовое число.
--
-- Формула повторяет RecipeService (desktop) шаг в шаг:
--   avg_price     = sum(total_price_kopecks) / sum(quantity_milli) * 1000 / 100
--                   по закупкам: копейки / тысячные доли единицы -> рубли за единицу
--   batch_cost    = sum(amount_milli * avg_price) / 1000            по составу
--                   тысячные доли единицы * рубли за единицу -> рубли
--   cost_per_unit = batch_cost / output_quantity_milli * 1000
--                   рубли / тысячные доли единицы -> рубли за единицу
--   price_per_unit = ceil_по_единице(cost_per_unit * (1 + markup_percent/100))
--
-- Округление цены за единицу (RecipeUnits.RoundPricePerUnit): для «г» — вверх
-- до копейки (множитель 100), для «кг» и «шт» — вверх до рубля (множитель 1).
-- Иначе дешёвые весовые позиции завышались бы в разы.
--
-- Средняя цена берётся по всем закупкам ингредиента, включая удалённые
-- ингредиенты: desktop считает так же (GetAveragePrices -> includeDeleted).
-- Ингредиент без закупок даёт avg_price = null и вклад 0 в себестоимость.
--
-- Вью security invoker: RLS таблиц действует как обычно, чужие строки не видны.
-- Отдельная политика вью не нужна и не должна создаваться — она бы обошла RLS.
--
-- ВАЖНО: редактор панели Supabase ограничен ~100 строкой, поэтому файл
-- применяется одной частью из supabase/parts/005_part1.sql. Полный текст
-- ниже совпадает с ней байт в байт.

-- ---------------------------------------------------------------------------
-- Часть 1: вью recipe_costs
-- ---------------------------------------------------------------------------

create or replace view public.recipe_costs
with (security_invoker = true)
as
with avg_prices as (
    select p.user_id,
           p.ingredient_id,
           sum(p.total_price_kopecks)::numeric / sum(p.quantity_milli) * 1000 / 100 as avg_price
    from public.purchases p
    group by p.user_id, p.ingredient_id
    having sum(p.quantity_milli) > 0
),
batch as (
    select ri.user_id,
           ri.recipe_id,
           sum(ri.amount_milli * coalesce(ap.avg_price, 0)) / 1000 as batch_cost
    from public.recipe_ingredients ri
    left join avg_prices ap
        on ap.user_id = ri.user_id
       and ap.ingredient_id = ri.ingredient_id
    group by ri.user_id, ri.recipe_id
),
unit_cost as (
    select r.user_id,
           r.id as recipe_id,
           r.name,
           r.category_id,
           r.output_quantity_milli,
           r.output_unit,
           r.markup_percent,
           r.updated_at,
           coalesce(b.batch_cost, 0) as batch_cost,
           coalesce(b.batch_cost, 0) / r.output_quantity_milli * 1000 as cost_per_unit
    from public.recipes r
    left join batch b
        on b.user_id = r.user_id
       and b.recipe_id = r.id
    where r.deleted_at is null
)
select u.user_id,
       u.recipe_id,
       u.name,
       u.category_id,
       u.output_quantity_milli,
       u.output_unit,
       u.markup_percent,
       u.updated_at,
       u.batch_cost,
       u.cost_per_unit,
       ceil(u.cost_per_unit * (1 + u.markup_percent / 100.0)
            * case when u.output_unit = 'г' then 100 else 1 end)
       / case when u.output_unit = 'г' then 100 else 1 end as price_per_unit
from unit_cost u;

-- Вью читается только вошедшим пользователем: anon не должен видеть ни
-- себестоимость, ни цены. RLS таблиц под вью всё равно отсечёт чужие строки,
-- но грант на anon открыл бы саму вью в списке схемы.
revoke all on public.recipe_costs from public, anon;
grant select on public.recipe_costs to authenticated;
