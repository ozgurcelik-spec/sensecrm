-- Idempotent database bootstrap (run by the `db-init` one-shot service on EVERY `up`, as the PostgreSQL superuser).
-- Fresh installs and upgrades take the same path; re-running only re-syncs passwords and privileges.
--
-- Roles (least privilege):
--   crm_owner  owns database "crm" and every object in it. Used ONLY by the migrator job (DDL).
--   crm_app    used by api + worker: LOGIN, no superuser / createdb / createrole, DML only on the module schemas.
--   conductor  owns database "conductor" (Conductor OSS keeps its own tables there); cannot connect to "crm".
-- Passwords are passed as psql variables (owner_pw, app_pw, conductor_pw); they never appear in this file.

\set ON_ERROR_STOP on

SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE', r)
FROM (VALUES ('crm_owner'), ('crm_app'), ('conductor')) AS v(r)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = v.r) \gexec

ALTER ROLE crm_owner WITH LOGIN PASSWORD :'owner_pw';
ALTER ROLE crm_app WITH LOGIN PASSWORD :'app_pw';
ALTER ROLE conductor WITH LOGIN PASSWORD :'conductor_pw';

SELECT 'CREATE DATABASE crm OWNER crm_owner ENCODING ''UTF8'' TEMPLATE template0'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'crm') \gexec
SELECT 'CREATE DATABASE conductor OWNER conductor ENCODING ''UTF8'' TEMPLATE template0'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'conductor') \gexec

ALTER DATABASE crm OWNER TO crm_owner;
ALTER DATABASE conductor OWNER TO conductor;

REVOKE ALL ON DATABASE crm FROM PUBLIC;
REVOKE ALL ON DATABASE conductor FROM PUBLIC;
GRANT CONNECT ON DATABASE crm TO crm_app;

-- ---- database "crm" -------------------------------------------------------------------------------------------------
\connect crm

CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS citext;

-- Accent-insensitive search helper (same as infra/postgres/init.sql).
CREATE OR REPLACE FUNCTION f_unaccent(text)
RETURNS text AS $$
  SELECT public.unaccent('public.unaccent', $1)
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT;

REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- Objects the migrator (crm_owner) creates from now on are usable by crm_app without further grants ...
ALTER DEFAULT PRIVILEGES FOR ROLE crm_owner GRANT USAGE ON SCHEMAS TO crm_app;
ALTER DEFAULT PRIVILEGES FOR ROLE crm_owner GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO crm_app;
ALTER DEFAULT PRIVILEGES FOR ROLE crm_owner GRANT USAGE, SELECT ON SEQUENCES TO crm_app;

-- ... and objects that already exist (upgrade, restore) are brought in line.
DO $$
DECLARE s text;
BEGIN
  FOR s IN
    SELECT nspname FROM pg_namespace
    WHERE nspname NOT LIKE 'pg\_%' AND nspname NOT IN ('information_schema', 'public')
  LOOP
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO crm_app', s);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO crm_app', s);
    EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO crm_app', s);
  END LOOP;
END
$$;
