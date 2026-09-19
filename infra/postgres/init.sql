-- CRM PostgreSQL başlangıç betiği (yalnızca ilk oluşturma sırasında çalışır)
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS citext;

-- Türkçe uyumlu, aksan duyarsız arama için yardımcı fonksiyon (Milestone 2 arama/filtre)
CREATE OR REPLACE FUNCTION f_unaccent(text)
RETURNS text AS $$
  SELECT public.unaccent('public.unaccent', $1)
$$ LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT;
