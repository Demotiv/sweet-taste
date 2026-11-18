-- Sweet Taste: схема облачной копии (Supabase / Postgres).
-- Применять в SQL Editor панели Supabase: сначала этот файл, затем 002_policies.sql.
--
-- Отличия от локальной SQLite:
--   * TEXT-GUID -> uuid;
--   * *_milli / *_kopecks остаются bigint (масштабированные целые, без потерь);
--   * cost_estimated INTEGER 0/1 -> boolean;
--   * BLOB -> bytea;
--   * у каждой таблицы есть user_id uuid, составной первичный ключ (user_id, id);
--   * внешние ключи составные по (user_id, ...), чтобы строки разных
--     пользователей не могли ссылаться друг на друга;
--   * app_settings и confectioner_profile в облако не переносятся:
--     это локальные настройки устройства, а не данные кондитера.
--
-- Порядок применения важен: таблицы ссылаются друг на друга, поэтому
-- справочники идут первыми, а индексы создаются в конце файла.

-- ---------------------------------------------------------------------------
-- Справочники
-- ---------------------------------------------------------------------------

create table if not exists public.ingredient_categories (
    user_id    uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id         uuid not null,
    name       text not null,
    created_at timestamptz not null,
    primary key (user_id, id)
);

create table if not exists public.recipe_categories (
    user_id    uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id         uuid not null,
    name       text not null,
    created_at timestamptz not null,
    primary key (user_id, id)
);

create table if not exists public.packaging_categories (
    user_id    uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id         uuid not null,
    name       text not null,
    created_at timestamptz not null,
    primary key (user_id, id)
);

-- ---------------------------------------------------------------------------
-- Ингредиенты и закупки
-- ---------------------------------------------------------------------------

create table if not exists public.ingredients (
    user_id     uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id          uuid not null,
    name        text not null,
    brand       text,
    unit        text not null,
    category_id uuid,
    created_at  timestamptz not null,
    updated_at  timestamptz not null,
    deleted_at  timestamptz,
    primary key (user_id, id),
    -- set null (category_id): обнуляется только ссылка, user_id остаётся
    -- заполненным (он not null и входит в составной ключ).
    foreign key (user_id, category_id)
        references public.ingredient_categories (user_id, id)
        on delete set null (category_id)
);

create table if not exists public.purchases (
    user_id             uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id                  uuid not null,
    ingredient_id       uuid not null,
    purchased_on        date not null,
    store               text,
    quantity_milli      bigint not null check (quantity_milli > 0),
    total_price_kopecks bigint not null check (total_price_kopecks >= 0),
    order_id            uuid,
    note                text,
    created_at          timestamptz not null,
    primary key (user_id, id),
    foreign key (user_id, ingredient_id)
        references public.ingredients (user_id, id) on delete restrict
);

-- ---------------------------------------------------------------------------
-- Рецепты
-- ---------------------------------------------------------------------------

create table if not exists public.recipes (
    user_id               uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id                    uuid not null,
    name                  text not null,
    category_id           uuid,
    output_quantity_milli bigint not null check (output_quantity_milli > 0),
    output_unit           text not null,
    markup_percent        integer not null check (markup_percent >= 0),
    technology            text,
    photo                 bytea,
    created_at            timestamptz not null,
    updated_at            timestamptz not null,
    deleted_at            timestamptz,
    primary key (user_id, id),
    foreign key (user_id, category_id)
        references public.recipe_categories (user_id, id)
        on delete set null (category_id)
);

create table if not exists public.recipe_ingredients (
    user_id       uuid not null default auth.uid() references auth.users (id) on delete cascade,
    recipe_id     uuid not null,
    ingredient_id uuid not null,
    amount_milli  bigint not null check (amount_milli > 0),
    position      integer not null,
    primary key (user_id, recipe_id, ingredient_id),
    foreign key (user_id, recipe_id)
        references public.recipes (user_id, id) on delete cascade,
    foreign key (user_id, ingredient_id)
        references public.ingredients (user_id, id) on delete restrict
);

-- ---------------------------------------------------------------------------
-- Клиенты и заказы
-- ---------------------------------------------------------------------------

create table if not exists public.customers (
    user_id          uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id               uuid not null,
    number           integer not null,
    first_name       text not null,
    last_name        text,
    phone            text not null,
    phone_normalized text not null,
    created_at       timestamptz not null,
    updated_at       timestamptz not null,
    primary key (user_id, id),
    unique (user_id, number),
    unique (user_id, phone_normalized)
);

create table if not exists public.orders (
    user_id             uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id                  uuid not null,
    number              integer not null,
    first_name          text not null,
    last_name           text,
    phone               text not null,
    due_date            date not null,
    status              text not null
        check (status in ('New', 'Cooking', 'Ready', 'Done', 'Cancelled')),
    comment             text,
    total_price_kopecks bigint not null,
    paid_amount_kopecks bigint not null default 0,
    discount_kopecks    bigint not null default 0,
    customer_id         uuid,
    created_at          timestamptz not null,
    updated_at          timestamptz not null,
    primary key (user_id, id),
    unique (user_id, number),
    foreign key (user_id, customer_id)
        references public.customers (user_id, id)
        on delete set null (customer_id)
);

create table if not exists public.order_items (
    user_id               uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id                    uuid not null,
    order_id              uuid not null,
    recipe_id             uuid,
    recipe_name           text not null,
    quantity_milli        bigint not null check (quantity_milli > 0),
    unit_price_kopecks    bigint not null,
    cost_price_kopecks    bigint not null default 0,
    cost_estimated        boolean not null default true,
    output_quantity_milli bigint not null check (output_quantity_milli > 0),
    output_unit           text not null,
    primary key (user_id, id),
    foreign key (user_id, order_id)
        references public.orders (user_id, id) on delete cascade,
    foreign key (user_id, recipe_id)
        references public.recipes (user_id, id)
        on delete set null (recipe_id)
);

-- ---------------------------------------------------------------------------
-- Упаковка
-- ---------------------------------------------------------------------------

create table if not exists public.packaging_items (
    user_id       uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id            uuid not null,
    category_id   uuid,
    name          text not null,
    size          text,
    price_kopecks bigint not null check (price_kopecks >= 0),
    created_at    timestamptz not null,
    updated_at    timestamptz not null,
    primary key (user_id, id),
    foreign key (user_id, category_id)
        references public.packaging_categories (user_id, id)
        on delete set null (category_id)
);

create table if not exists public.order_packaging (
    user_id            uuid not null default auth.uid() references auth.users (id) on delete cascade,
    id                 uuid not null,
    order_id           uuid not null,
    packaging_id       uuid,
    name               text not null,
    size               text,
    cost_kopecks       bigint not null check (cost_kopecks >= 0),
    sale_price_kopecks bigint not null check (sale_price_kopecks >= 0),
    quantity           integer not null check (quantity > 0),
    position           integer not null,
    primary key (user_id, id),
    foreign key (user_id, order_id)
        references public.orders (user_id, id) on delete cascade,
    foreign key (user_id, packaging_id)
        references public.packaging_items (user_id, id)
        on delete set null (packaging_id)
);

-- ---------------------------------------------------------------------------
-- Состояние синхронизации
-- ---------------------------------------------------------------------------

create table if not exists public.sync_state (
    user_id          uuid not null primary key references auth.users (id) on delete cascade,
    schema_version   integer not null,
    last_snapshot_at timestamptz not null,
    row_counts       jsonb not null default '{}'::jsonb
);

-- ---------------------------------------------------------------------------
-- Индексы
-- ---------------------------------------------------------------------------

create index if not exists ix_ingredients_name on public.ingredients (user_id, name);
create index if not exists ix_ingredients_deleted_at on public.ingredients (user_id, deleted_at);
create index if not exists ix_purchases_ingredient_date on public.purchases (user_id, ingredient_id, purchased_on);
create index if not exists ix_purchases_order on public.purchases (user_id, order_id);
create index if not exists ix_recipes_name on public.recipes (user_id, name);
create index if not exists ix_recipes_deleted_at on public.recipes (user_id, deleted_at);
create index if not exists ix_recipe_ingredients_ingredient on public.recipe_ingredients (user_id, ingredient_id);
create index if not exists ix_orders_status_due on public.orders (user_id, status, due_date);
create index if not exists ix_orders_customer on public.orders (user_id, customer_id);
create index if not exists ix_order_items_order on public.order_items (user_id, order_id);
create index if not exists ix_packaging_items_name on public.packaging_items (user_id, name);
create index if not exists ix_order_packaging_order on public.order_packaging (user_id, order_id);

-- ---------------------------------------------------------------------------
-- Проверка: все таблицы должны быть видны в списке
-- ---------------------------------------------------------------------------

select table_name
from information_schema.tables
where table_schema = 'public'
order by table_name;
