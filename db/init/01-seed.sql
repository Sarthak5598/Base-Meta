-- Sample "shop" database for developing DataLens against.
-- Runs once, when the Postgres container is first created.

SELECT setseed(0.42);  -- same sample data every time

CREATE TABLE categories (
  id    serial PRIMARY KEY,
  name  text NOT NULL UNIQUE
);

CREATE TABLE products (
  id           serial PRIMARY KEY,
  category_id  integer NOT NULL REFERENCES categories (id),
  name         text NOT NULL,
  price        numeric(10, 2) NOT NULL,
  active       boolean NOT NULL DEFAULT true
);

CREATE TABLE users (
  id          serial PRIMARY KEY,
  name        text NOT NULL,
  email       text NOT NULL UNIQUE,
  country     text NOT NULL,
  created_at  timestamptz NOT NULL
);

CREATE TABLE orders (
  id          serial PRIMARY KEY,
  user_id     integer NOT NULL REFERENCES users (id),
  status      text NOT NULL,
  created_at  timestamptz NOT NULL
);
CREATE INDEX orders_user_id_idx ON orders (user_id);
CREATE INDEX orders_created_at_idx ON orders (created_at);

CREATE TABLE order_items (
  id          serial PRIMARY KEY,
  order_id    integer NOT NULL REFERENCES orders (id),
  product_id  integer NOT NULL REFERENCES products (id),
  quantity    integer NOT NULL,
  unit_price  numeric(10, 2) NOT NULL
);
CREATE INDEX order_items_order_id_idx ON order_items (order_id);

-- Deliberately has NO foreign key: user_id is only a convention here.
-- Shows what happens when a relationship exists in the data but is not declared.
CREATE TABLE legacy_events (
  id          serial PRIMARY KEY,
  user_id     integer,
  event       text NOT NULL,
  happened_at timestamptz NOT NULL
);

-- ---- data ---------------------------------------------------------------------------------

INSERT INTO categories (name)
VALUES ('Coffee'), ('Tea'), ('Equipment'), ('Snacks'), ('Gifts');

INSERT INTO products (category_id, name, price)
SELECT 1 + (g % 5), 'Product ' || g, round((3 + random() * 57)::numeric, 2)
FROM generate_series(1, 40) g;

INSERT INTO users (name, email, country, created_at)
SELECT 'User ' || g,
       'user' || g || '@example.com',
       (ARRAY['US', 'GB', 'DE', 'IN', 'BR', 'FR'])[1 + floor(random() * 6)::int],
       now() - (random() * interval '700 days')
FROM generate_series(1, 200) g;

INSERT INTO orders (user_id, status, created_at)
SELECT 1 + floor(random() * 200)::int,
       (ARRAY['paid', 'paid', 'paid', 'shipped', 'refunded', 'pending'])[1 + floor(random() * 6)::int],
       now() - (random() * interval '365 days')
FROM generate_series(1, 2000);

INSERT INTO order_items (order_id, product_id, quantity, unit_price)
SELECT o.id, p.id, 1 + floor(random() * 3)::int, p.price
FROM orders o
CROSS JOIN LATERAL (
  SELECT id, price FROM products ORDER BY random() LIMIT 1 + (o.id % 3)
) p;

INSERT INTO legacy_events (user_id, event, happened_at)
SELECT 1 + floor(random() * 200)::int,
       (ARRAY['login', 'view', 'search', 'logout'])[1 + floor(random() * 4)::int],
       now() - (random() * interval '90 days')
FROM generate_series(1, 1000);

CREATE VIEW order_totals AS
SELECT o.id AS order_id, o.user_id, o.status, o.created_at,
       sum(i.quantity * i.unit_price) AS total
FROM orders o
JOIN order_items i ON i.order_id = o.id
GROUP BY o.id;

-- ---- roles --------------------------------------------------------------------------------
-- datalens_ro: what you SHOULD connect with. Can read, cannot write.
CREATE ROLE datalens_ro LOGIN PASSWORD 'readonly';
GRANT USAGE ON SCHEMA public TO datalens_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO datalens_ro;

-- The container's admin user (shop_admin) can write: use it to see the warning.
