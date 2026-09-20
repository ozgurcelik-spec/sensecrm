using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sense.Crm.Modules.Platform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// C-SEC2: (1) <c>deletion_requests.xmin</c> eşzamanlılık belirteci (sistem sütunu; Npgsql sütun üretmez); (2) H1 — eski kurulumlarda eksik olabilecek
    /// <c>is_system</c> bayrağı: aktif platform yöneticisi üyesi olan her kiracı hesabı işaretlenir (Migrator <c>backfill</c> aynı kuralı yeniden uygular);
    /// (3) M3 — <c>platform_audit_entries</c> salt-eklemeli tetikleyicisi: UPDATE yalnız imha (<c>erasure</c>) işaretiyle ve yalnız <c>target_tenant_name</c>/<c>details</c> için,
    /// DELETE yalnız saklama (<c>retention</c>) işaretiyle ve 30 günden eski satırlar için; DBA/test için <c>superuser</c> işareti yalnız süper kullanıcıya açık.
    /// </summary>
    public partial class PlatformSecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "platform",
                table: "deletion_requests",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            // H1: identity şeması (aynı veritabanı) varsa aktif platform yöneticisi üyesi olan kiracıların hesabı is_system = true.
            migrationBuilder.Sql(
                """
                DO $mig$
                BEGIN
                  IF to_regclass('identity.memberships') IS NOT NULL AND to_regclass('identity.users') IS NOT NULL THEN
                    UPDATE platform.tenant_accounts a
                       SET is_system = TRUE
                     WHERE NOT a.is_system
                       AND EXISTS (
                         SELECT 1 FROM identity.memberships m
                         JOIN identity.users u ON u.id = m.user_id
                         WHERE m.tenant_id = a.tenant_id AND m.is_active AND m.status = 'Active' AND u.is_active AND u.is_platform_admin);
                  END IF;
                END
                $mig$;
                """);

            // M3: salt-eklemeli platform denetimi.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION platform.guard_platform_audit_entries() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                DECLARE
                  marker text := coalesce(current_setting('crm.audit_maintenance', true), '');
                BEGIN
                  -- DBA break-glass / entegrasyon testi sıfırlaması: yalnız süper kullanıcı oturumları.
                  IF marker = 'superuser' AND coalesce((SELECT rolsuper FROM pg_roles WHERE rolname = session_user), FALSE) THEN
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                  END IF;

                  IF TG_OP = 'DELETE' THEN
                    IF marker = 'retention' AND OLD.occurred_at < now() - interval '30 days' THEN
                      RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'audit_immutable: platform_audit_entries is append-only (DELETE is allowed only for rows older than 30 days by the retention job)' USING ERRCODE = '42501';
                  END IF;

                  -- UPDATE: yalnız KVKK imhasında, hedef kiracı silme sürecindeyken ve yalnız redakte edilen iki sütun için.
                  IF marker = 'erasure'
                     AND (to_jsonb(NEW) - 'target_tenant_name' - 'details') = (to_jsonb(OLD) - 'target_tenant_name' - 'details')
                     AND EXISTS (SELECT 1 FROM platform.tenant_accounts a WHERE a.tenant_id = OLD.target_tenant_id AND a.status IN ('pending_deletion', 'deleted')) THEN
                    RETURN NEW;
                  END IF;
                  RAISE EXCEPTION 'audit_immutable: platform_audit_entries is append-only (UPDATE is allowed only for redaction of target_tenant_name/details during a tenant erasure)' USING ERRCODE = '42501';
                END
                $fn$;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_platform_audit_entries_guard
                BEFORE UPDATE OR DELETE ON platform.platform_audit_entries
                FOR EACH ROW EXECUTE FUNCTION platform.guard_platform_audit_entries();
                """);
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION platform.guard_platform_audit_truncate() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF coalesce(current_setting('crm.audit_maintenance', true), '') = 'superuser' AND coalesce((SELECT rolsuper FROM pg_roles WHERE rolname = session_user), FALSE) THEN
                    RETURN NULL;
                  END IF;
                  RAISE EXCEPTION 'audit_immutable: platform_audit_entries cannot be truncated' USING ERRCODE = '42501';
                END
                $fn$;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_platform_audit_entries_no_truncate
                BEFORE TRUNCATE ON platform.platform_audit_entries
                FOR EACH STATEMENT EXECUTE FUNCTION platform.guard_platform_audit_truncate();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_platform_audit_entries_no_truncate ON platform.platform_audit_entries;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_platform_audit_entries_guard ON platform.platform_audit_entries;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform.guard_platform_audit_truncate();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform.guard_platform_audit_entries();");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "platform",
                table: "deletion_requests");
        }
    }
}
