using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sense.Crm.Shared.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// C-SEC2 M3: <c>audit.audit_log_entries</c> salt-eklemeli (tamper-resistant). UPDATE hiçbir işaretle yapılamaz (imha satırı siler, redakte etmez); DELETE yalnız KVKK imhasında
    /// (<c>SET LOCAL crm.audit_maintenance = 'erasure'</c>) ve satırın kiracısı <c>platform.tenant_accounts</c>'ta <c>pending_deletion|deleted</c> iken serbesttir. DBA/test sıfırlaması için
    /// <c>superuser</c> işareti yalnız süper kullanıcı oturumlarında geçerlidir (<c>crm_app</c> süper kullanıcı değildir). Yeni bir saklama işi eklenirse tetikleyici bilerek genişletilmelidir.
    /// </summary>
    public partial class AuditImmutability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION audit.guard_audit_log_entries() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                DECLARE
                  marker text := coalesce(current_setting('crm.audit_maintenance', true), '');
                BEGIN
                  IF marker = 'superuser' AND coalesce((SELECT rolsuper FROM pg_roles WHERE rolname = session_user), FALSE) THEN
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                  END IF;

                  IF TG_OP = 'DELETE'
                     AND marker = 'erasure'
                     AND to_regclass('platform.tenant_accounts') IS NOT NULL
                     AND EXISTS (SELECT 1 FROM platform.tenant_accounts a WHERE a.tenant_id = OLD.tenant_id AND a.status IN ('pending_deletion', 'deleted')) THEN
                    RETURN OLD;
                  END IF;

                  RAISE EXCEPTION 'audit_immutable: audit_log_entries is append-only (% is not allowed; DELETE only during a tenant erasure)', TG_OP USING ERRCODE = '42501';
                END
                $fn$;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_audit_log_entries_guard
                BEFORE UPDATE OR DELETE ON audit.audit_log_entries
                FOR EACH ROW EXECUTE FUNCTION audit.guard_audit_log_entries();
                """);
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION audit.guard_audit_truncate() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF coalesce(current_setting('crm.audit_maintenance', true), '') = 'superuser' AND coalesce((SELECT rolsuper FROM pg_roles WHERE rolname = session_user), FALSE) THEN
                    RETURN NULL;
                  END IF;
                  RAISE EXCEPTION 'audit_immutable: audit_log_entries cannot be truncated' USING ERRCODE = '42501';
                END
                $fn$;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_audit_log_entries_no_truncate
                BEFORE TRUNCATE ON audit.audit_log_entries
                FOR EACH STATEMENT EXECUTE FUNCTION audit.guard_audit_truncate();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_audit_log_entries_no_truncate ON audit.audit_log_entries;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_audit_log_entries_guard ON audit.audit_log_entries;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit.guard_audit_truncate();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit.guard_audit_log_entries();");
        }
    }
}
