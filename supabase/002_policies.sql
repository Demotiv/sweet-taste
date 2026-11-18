-- Sweet Taste: политики доступа (Row Level Security).
-- Применять после 001_schema.sql. Файл идемпотентен: повторный запуск
-- пересоздаёт политики, а не падает на "already exists".
--
-- Модель: каждая строка принадлежит одному пользователю через user_id.
-- Политика разрешает только строки, где user_id = auth.uid().
-- Публичный publishable-ключ без входа не даёт доступа ни к одной строке.

alter table public.ingredient_categories  enable row level security;
alter table public.recipe_categories      enable row level security;
alter table public.packaging_categories   enable row level security;
alter table public.ingredients            enable row level security;
alter table public.purchases              enable row level security;
alter table public.recipes                enable row level security;
alter table public.recipe_ingredients     enable row level security;
alter table public.customers              enable row level security;
alter table public.orders                 enable row level security;
alter table public.order_items            enable row level security;
alter table public.packaging_items        enable row level security;
alter table public.order_packaging        enable row level security;
alter table public.sync_state             enable row level security;

-- (select auth.uid()) вместо auth.uid(): Postgres вычисляет подзапрос один раз
-- на запрос, а не на каждую строку.

drop policy if exists own_rows on public.ingredient_categories;
create policy own_rows on public.ingredient_categories
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.recipe_categories;
create policy own_rows on public.recipe_categories
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.packaging_categories;
create policy own_rows on public.packaging_categories
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.ingredients;
create policy own_rows on public.ingredients
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.purchases;
create policy own_rows on public.purchases
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.recipes;
create policy own_rows on public.recipes
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.recipe_ingredients;
create policy own_rows on public.recipe_ingredients
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.customers;
create policy own_rows on public.customers
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.orders;
create policy own_rows on public.orders
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.order_items;
create policy own_rows on public.order_items
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.packaging_items;
create policy own_rows on public.packaging_items
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.order_packaging;
create policy own_rows on public.order_packaging
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

drop policy if exists own_rows on public.sync_state;
create policy own_rows on public.sync_state
    for all to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));
