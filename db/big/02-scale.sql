-- Grows the sample shop into a "big" database: ~100,000 customers, ~2,000,000 orders over three years,
-- ~5,000,000 order lines and ~2,000,000 activity events. Runs after db/init/01-seed.sql, once, when the
-- big-db container is first created (takes a minute or two). Same tables as the small sample, so the
-- sample dashboard works on it unchanged.

SELECT setseed(0.42);

INSERT INTO users (name, email, country, created_at)
SELECT 'User ' || g,
       'user' || g || '@example.com',
       (ARRAY['US', 'GB', 'DE', 'IN', 'BR', 'FR', 'JP', 'CA', 'AU', 'ES'])[1 + floor(random() * 10)::int],
       now() - (random() * interval '1100 days')
FROM generate_series(201, 100000) g;

INSERT INTO orders (user_id, status, created_at)
SELECT 1 + floor(random() * 100000)::int,
       (ARRAY['paid', 'paid', 'paid', 'shipped', 'refunded', 'pending'])[1 + floor(random() * 6)::int],
       now() - (random() * interval '1095 days')
FROM generate_series(1, 2000000);

-- one to four lines per new order; products picked by arithmetic, which is far cheaper than random() joins
INSERT INTO order_items (order_id, product_id, quantity, unit_price)
SELECT o.id, p.id, 1 + floor(random() * 3)::int, p.price
FROM orders o
CROSS JOIN LATERAL generate_series(1, 1 + (o.id % 4)) k
JOIN products p ON p.id = 1 + ((o.id * 13 + k * 7) % 40)
WHERE o.id > 2000;

INSERT INTO legacy_events (user_id, event, happened_at)
SELECT 1 + floor(random() * 100000)::int,
       (ARRAY['login', 'view', 'search', 'logout'])[1 + floor(random() * 4)::int],
       now() - (random() * interval '365 days')
FROM generate_series(1, 2000000);

ANALYZE;
