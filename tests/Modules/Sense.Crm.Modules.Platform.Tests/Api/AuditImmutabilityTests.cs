using Npgsql;
using Sense.Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Sense.Crm.Modules.Platform.Tests.Api.ConsoleTestKit;
using static Sense.Crm.Modules.Platform.Tests.Api.PlatformKit;

namespace Sense.Crm.Modules.Platform.Tests.Api;

/// <summary>
/// C-SEC2 M3: denetim tabloları (<c>audit.audit_log_entries</c>, <c>platform.platform_audit_entries</c>) veritabanı tetikleyicileriyle salt-eklemelidir. Testler <b>süper kullanıcı olmayan</b>
/// bir rolle (üretimdeki <c>crm_app</c> gibi tam DML yetkili) bağlanır ve iki yönü de sınar: doğrudan UPDATE/DELETE/TRUNCATE reddedilir; imha (<c>erasure</c>) ve saklama
/// (<c>retention</c>) işaretleri yalnız koşullarıyla (pending_deletion kiracı / 30 günden eski satır / yalnız redakte sütunlar) geçer; <c>superuser</c> işareti süper olmayan role işlemez.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditImmutabilityTests(CrmApiFactory factory)
{
    private const string ProbeRole = "crm_app_probe";
    private const string ProbePassword = "Probe.Sifre.12345";

    // ---- platform.platform_audit_entries -------------------------------------------------------------------------------

    [Fact]
    public async Task PlatformAudit_UpdateAndDelete_AreRejected_ForANonSuperuserWithFullDmlGrants()
    {
        var id = await InsertPlatformAuditAsync(Guid.NewGuid(), DateTime.UtcNow.AddDays(-90));
        await using var app = await OpenProbeAsync();

        (await ExpectImmutableAsync(app, "UPDATE platform.platform_audit_entries SET action = 'tampered' WHERE id = @id", id)).ShouldBeTrue();
        (await ExpectImmutableAsync(app, "UPDATE platform.platform_audit_entries SET target_tenant_name = 'x' WHERE id = @id", id)).ShouldBeTrue("işaretsiz redaksiyon da reddedilir");
        (await ExpectImmutableAsync(app, "DELETE FROM platform.platform_audit_entries WHERE id = @id", id)).ShouldBeTrue();
        (await ExpectImmutableAsync(app, "TRUNCATE platform.platform_audit_entries", id)).ShouldBeTrue();
        (await PlatformAuditExistsAsync(id)).ShouldBeTrue();
    }

    [Fact]
    public async Task PlatformAudit_RetentionMarker_DeletesOnlyRowsOlderThanThirtyDays()
    {
        var old = await InsertPlatformAuditAsync(Guid.NewGuid(), DateTime.UtcNow.AddDays(-60));
        var young = await InsertPlatformAuditAsync(Guid.NewGuid(), DateTime.UtcNow.AddDays(-5));
        await using var app = await OpenProbeAsync();

        await using (var transaction = await app.BeginTransactionAsync(Ct))
        {
            await ExecuteAsync(app, transaction, "SELECT set_config('crm.audit_maintenance', 'retention', true)");
            await ExecuteAsync(app, transaction, "DELETE FROM platform.platform_audit_entries WHERE id = @id", old);
            await transaction.CommitAsync(Ct);
        }

        (await PlatformAuditExistsAsync(old)).ShouldBeFalse("30 günden eski satır saklama işaretiyle silinir");

        await using var second = await app.BeginTransactionAsync(Ct);
        await ExecuteAsync(app, second, "SELECT set_config('crm.audit_maintenance', 'retention', true)");
        var rejected = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, second, "DELETE FROM platform.platform_audit_entries WHERE id = @id", young));
        rejected.Message.ShouldContain("audit_immutable");
        await second.RollbackAsync(Ct);
        (await PlatformAuditExistsAsync(young)).ShouldBeTrue("30 günden genç satır hiçbir işaretle silinmez");
    }

    [Fact]
    public async Task PlatformAudit_ErasureMarker_RedactsOnlyNameAndDetails_AndOnlyForATenantInDeletion()
    {
        var org = await factory.SyncedOrgAsync(Token("aud") + " denetim");
        var id = await InsertPlatformAuditAsync(org.TenantId, DateTime.UtcNow, details: """{"reason":"gizli","x":1}""");
        await using var app = await OpenProbeAsync();

        // Kiracı silme sürecinde değil: erasure işareti bile geçmez.
        await using (var early = await app.BeginTransactionAsync(Ct))
        {
            await ExecuteAsync(app, early, "SELECT set_config('crm.audit_maintenance', 'erasure', true)");
            var rejected = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, early, "UPDATE platform.platform_audit_entries SET details = details - 'reason' WHERE id = @id", id));
            rejected.Message.ShouldContain("audit_immutable");
            await early.RollbackAsync(Ct);
        }

        await factory.SqlAsync("UPDATE platform.tenant_accounts SET status = 'pending_deletion' WHERE tenant_id = @t", ("t", org.TenantId));

        await using (var ok = await app.BeginTransactionAsync(Ct))
        {
            await ExecuteAsync(app, ok, "SELECT set_config('crm.audit_maintenance', 'erasure', true)");
            await ExecuteAsync(app, ok, "UPDATE platform.platform_audit_entries SET target_tenant_name = '[deleted]', details = details - 'reason' WHERE id = @id", id);
            await ok.CommitAsync(Ct);
        }

        (await factory.ScalarAsync<string>("SELECT details::text FROM platform.platform_audit_entries WHERE id = @id", ("id", id))).ShouldNotContain("gizli");

        // Başka sütun (action) redaksiyon değildir: işaret + koşul olsa da reddedilir.
        await using var tamper = await app.BeginTransactionAsync(Ct);
        await ExecuteAsync(app, tamper, "SELECT set_config('crm.audit_maintenance', 'erasure', true)");
        var tampered = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, tamper, "UPDATE platform.platform_audit_entries SET action = 'tampered' WHERE id = @id", id));
        tampered.Message.ShouldContain("audit_immutable");
        await tamper.RollbackAsync(Ct);
    }

    [Fact]
    public async Task TheSuperuserMarker_DoesNothingForANonSuperuser_ButWorksForASuperuserSession()
    {
        var id = await InsertPlatformAuditAsync(Guid.NewGuid(), DateTime.UtcNow.AddDays(-2));
        await using var app = await OpenProbeAsync();
        await using (var transaction = await app.BeginTransactionAsync(Ct))
        {
            await ExecuteAsync(app, transaction, "SELECT set_config('crm.audit_maintenance', 'superuser', true)");
            var rejected = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, transaction, "DELETE FROM platform.platform_audit_entries WHERE id = @id", id));
            rejected.Message.ShouldContain("audit_immutable");
            await transaction.RollbackAsync(Ct);
        }

        // DBA kırılma-cam yolu: süper kullanıcı oturumu + işaret (test sıfırlaması da bunu kullanır).
        await using var superuser = new NpgsqlConnection(factory.ConnectionString);
        await superuser.OpenAsync(Ct);
        await using var superTransaction = await superuser.BeginTransactionAsync(Ct);
        await ExecuteAsync(superuser, superTransaction, "SELECT set_config('crm.audit_maintenance', 'superuser', true)");
        await ExecuteAsync(superuser, superTransaction, "DELETE FROM platform.platform_audit_entries WHERE id = @id", id);
        await superTransaction.CommitAsync(Ct);
        (await PlatformAuditExistsAsync(id)).ShouldBeFalse();
    }

    // ---- audit.audit_log_entries ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TenantAudit_UpdateIsNeverAllowed_AndDeleteOnlyDuringAnErasureOfThatTenant()
    {
        var org = await factory.SyncedOrgAsync(Token("tau") + " kiraci denetimi");
        var id = await InsertTenantAuditAsync(org.TenantId);
        await using var app = await OpenProbeAsync();

        (await ExpectImmutableAsync(app, "UPDATE audit.audit_log_entries SET changes = '{}'::jsonb WHERE id = @id", id)).ShouldBeTrue();
        (await ExpectImmutableAsync(app, "DELETE FROM audit.audit_log_entries WHERE id = @id", id)).ShouldBeTrue();
        (await ExpectImmutableAsync(app, "TRUNCATE audit.audit_log_entries", id)).ShouldBeTrue();

        // Erasure işareti + aktif kiracı: reddedilir.
        await using (var active = await app.BeginTransactionAsync(Ct))
        {
            await ExecuteAsync(app, active, "SELECT set_config('crm.audit_maintenance', 'erasure', true)");
            var rejected = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, active, "DELETE FROM audit.audit_log_entries WHERE id = @id", id));
            rejected.Message.ShouldContain("audit_immutable");
            await active.RollbackAsync(Ct);
        }

        // Retention işareti bu tablo için yoktur.
        await using (var retention = await app.BeginTransactionAsync(Ct))
        {
            await ExecuteAsync(app, retention, "SELECT set_config('crm.audit_maintenance', 'retention', true)");
            var rejected = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, retention, "DELETE FROM audit.audit_log_entries WHERE id = @id", id));
            rejected.Message.ShouldContain("audit_immutable");
            await retention.RollbackAsync(Ct);
        }

        (await TenantAuditExistsAsync(id)).ShouldBeTrue();

        // Kiracı silme sürecinde: erasure işaretiyle silinir; başka kiracının satırı hâlâ silinemez.
        var other = await InsertTenantAuditAsync((await factory.SyncedOrgAsync(Token("tau2") + " diger")).TenantId);
        await factory.SqlAsync("UPDATE platform.tenant_accounts SET status = 'pending_deletion' WHERE tenant_id = @t", ("t", org.TenantId));
        await using var deletion = await app.BeginTransactionAsync(Ct);
        await ExecuteAsync(app, deletion, "SELECT set_config('crm.audit_maintenance', 'erasure', true)");
        await ExecuteAsync(app, deletion, "DELETE FROM audit.audit_log_entries WHERE id = @id", id);
        var otherRejected = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(app, deletion, "DELETE FROM audit.audit_log_entries WHERE id = @id", other));
        otherRejected.Message.ShouldContain("audit_immutable");
        await deletion.RollbackAsync(Ct);
    }

    // ---- yardımcılar ---------------------------------------------------------------------------------------------------

    private async Task<NpgsqlConnection> OpenProbeAsync()
    {
        await factory.SqlAsync(
            $$"""
            DO $$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{ProbeRole}}') THEN
                CREATE ROLE {{ProbeRole}} LOGIN PASSWORD '{{ProbePassword}}' NOSUPERUSER;
              END IF;
              GRANT USAGE ON SCHEMA audit, platform TO {{ProbeRole}};
              GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE ON audit.audit_log_entries, platform.platform_audit_entries TO {{ProbeRole}};
              GRANT SELECT ON platform.tenant_accounts TO {{ProbeRole}};
            END
            $$;
            """);
        var builder = new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Username = ProbeRole, Password = ProbePassword, Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(Ct);
        (await factory.ScalarAsync<bool>("SELECT rolsuper FROM pg_roles WHERE rolname = @r", ("r", ProbeRole))).ShouldBeFalse();
        return connection;
    }

    private static async Task<bool> ExpectImmutableAsync(NpgsqlConnection connection, string sql, Guid id)
    {
        var error = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connection, null, sql, id));
        return error.SqlState == "42501" && error.Message.Contains("audit_immutable", StringComparison.Ordinal);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, Guid? id = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (id is { } value && sql.Contains("@id", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("id", value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<Guid> InsertPlatformAuditAsync(Guid tenantId, DateTime occurredAtUtc, string details = "{}")
    {
        var id = Guid.CreateVersion7();
        (await factory.SqlAsync(
            "INSERT INTO platform.platform_audit_entries (id, occurred_at, action, target_tenant_id, target_tenant_name, details) VALUES (@id, @at, 'subscription.changed', @t, 'Sentetik', @d::jsonb)",
            ("id", id), ("at", occurredAtUtc), ("t", tenantId), ("d", details))).ShouldBe(1);
        return id;
    }

    private async Task<Guid> InsertTenantAuditAsync(Guid tenantId)
    {
        var id = Guid.CreateVersion7();
        (await factory.SqlAsync(
            "INSERT INTO audit.audit_log_entries (id, tenant_id, entity_type, entity_id, action, changes, occurred_at) VALUES (@id, @t, 'Account', 'x', 'created', '{}'::jsonb, now())",
            ("id", id), ("t", tenantId))).ShouldBe(1);
        return id;
    }

    private async Task<bool> PlatformAuditExistsAsync(Guid id) =>
        await factory.ScalarAsync<long>("SELECT count(*) FROM platform.platform_audit_entries WHERE id = @id", ("id", id)) == 1;

    private async Task<bool> TenantAuditExistsAsync(Guid id) =>
        await factory.ScalarAsync<long>("SELECT count(*) FROM audit.audit_log_entries WHERE id = @id", ("id", id)) == 1;
}
