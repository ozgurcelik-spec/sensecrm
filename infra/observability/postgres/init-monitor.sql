-- Read-only monitoring role for postgres-exporter (observability overlay). Idempotent; run by the `monitor-init` one-shot service as the
-- PostgreSQL superuser on every `up`. The password is passed as the psql variable `pw` (from the pg_monitor_password Docker secret).
--
-- crm_monitor: LOGIN, no superuser/createdb/createrole, member of pg_monitor (pg_stat_* views, no table data). It has no CONNECT on the
-- application databases beyond what PUBLIC already lost in init-roles.sql; the exporter connects to the maintenance database "postgres".

\set ON_ERROR_STOP on

SELECT 'CREATE ROLE crm_monitor LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE'
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'crm_monitor') \gexec

ALTER ROLE crm_monitor WITH LOGIN PASSWORD :'pw';
GRANT pg_monitor TO crm_monitor;
