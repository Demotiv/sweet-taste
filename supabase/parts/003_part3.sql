-- Sweet Taste: часть 3 из 5 — клиенты, заказы и позиции заказов.
-- Применять после 003_part2.sql. Файл идемпотентен.
-- Вызывается из replace_snapshot (часть 5) после рецептов.

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
