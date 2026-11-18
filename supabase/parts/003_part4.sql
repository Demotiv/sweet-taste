-- Sweet Taste: часть 4 из 5 — упаковка и упаковка в заказах.
-- Применять после 003_part3.sql. Файл идемпотентен.
-- Вызывается из replace_snapshot (часть 5) после заказов.

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
