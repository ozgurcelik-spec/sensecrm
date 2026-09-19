using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Crm.Tests.Shared.Fixtures;
using Shouldly;
using Xunit;
using static Crm.Modules.Commerce.Tests.Api.CommerceApiKit;
using static Crm.Tests.Shared.Fixtures.ApiTestClient;

namespace Crm.Modules.Commerce.Tests.Api;

/// <summary>Teklifler (<c>/quotes</c>): oluşturma/güncelleme/silme, doğrulama, bağlı kayıt kuralları, filtre/sıralama, durum makinesi ve denetim.</summary>
[Collection(ApiCollection.Name)]
public sealed partial class QuoteApiTests(CrmApiFactory factory)
{
    [GeneratedRegex(@"^Q-\d{4}-\d{4,}$")]
    private static partial Regex QuoteNumber();

    private static string Iso(DateOnly date) => date.DateString();

    [Fact]
    public async Task Create_StartsAsDraft_WithServerComputedTotals_IgnoringClientComputedFields()
    {
        var org = await factory.NewOrgAsync("Teklif Hesap");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Acme A.Ş.");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { firstName = "Ayşe", lastName = "Kaya", accountId = account.Id() });
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Yıllık lisans", accountId = account.Id() });

        var response = await admin.PostAsJsonAsync(
            QuotesPath,
            new
            {
                subject = "  Yıllık lisans teklifi ",
                accountId = account.Id(),
                contactId = contact.Id(),
                dealId = deal.Id(),
                validUntil = Iso(TenantToday().AddDays(30)),
                terms = "30 gün vade",
                notes = "Not",
                number = "Q-1999-0001",
                status = "accepted",
                grandTotal = 1m,
                subtotal = 1m,
                lines = new object[]
                {
                    new { description = "Lisans", quantity = 3, unitPrice = 19.99m, discountPercent = 10, taxRate = 20, lineTotal = 1m, lineSubtotal = 1m },
                    new { description = "Danışmanlık", quantity = 2.5m, unitPrice = 10.10m, discountPercent = 0, taxRate = 18 },
                },
            },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var quote = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.Headers.Location!.ToString().ShouldEndWith($"{QuotesPath}/{quote.Id()}");
        QuoteNumber().IsMatch(quote.Str("number")).ShouldBeTrue(quote.Str("number"));
        quote.Str("number").ShouldNotBe("Q-1999-0001");
        quote.Str("status").ShouldBe("draft");
        quote.Str("subject").ShouldBe("Yıllık lisans teklifi");
        quote.Str("accountName").ShouldBe("Acme A.Ş.");
        quote.Str("contactName").ShouldBe("Ayşe Kaya");
        quote.Str("dealName").ShouldBe("Yıllık lisans");
        quote.Str("ownerName").ShouldBe(org.AdminName);
        quote.GetProperty("ownerUserId").GetGuid().ShouldBe(org.AdminUserId);
        quote.Str("currency").ShouldBe("TRY");
        quote.Dec("subtotal").ShouldBe(85.22m);
        quote.Dec("discountTotal").ShouldBe(6.00m);
        quote.Dec("taxTotal").ShouldBe(15.34m);
        quote.Dec("grandTotal").ShouldBe(94.56m);
        quote.TryGetProperty("convertedOrderId", out _).ShouldBeFalse();

        var lines = quote.GetProperty("lines").EnumerateArray().ToList();
        lines.Select(l => l.GetProperty("position").GetInt32()).ShouldBe([0, 1]);
        lines[0].Dec("lineSubtotal").ShouldBe(59.97m);
        lines[0].Dec("lineDiscount").ShouldBe(6.00m);
        lines[0].Dec("lineTax").ShouldBe(10.79m);
        lines[0].Dec("lineTotal").ShouldBe(64.76m);
        lines[1].Dec("lineTotal").ShouldBe(29.80m);
        lines.ShouldAllBe(l => l.Id() != Guid.Empty);
        lines.Sum(l => l.Dec("lineTotal")).ShouldBe(quote.Dec("grandTotal"));

        var read = await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}");
        read.Dec("grandTotal").ShouldBe(94.56m);
        read.Str("terms").ShouldBe("30 gün vade");
    }

    [Fact]
    public async Task Create_WithoutLines_IsAllowed_AndTotalsAreZero()
    {
        var admin = (await factory.NewOrgAsync("Teklif Kalemsiz")).Admin;
        var account = await admin.CreateAccountAsync("Firma");

        var quote = await admin.PostJsonAsync(QuotesPath, new { subject = "Boş", accountId = account.Id() });

        quote.Dec("grandTotal").ShouldBe(0m);
        quote.GetProperty("lines").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Update_ReplacesEverything_KeepsNumberStatusAndOwner_AndBumpsTotals()
    {
        var org = await factory.NewOrgAsync("Teklif Güncelle");
        var admin = org.Admin;
        var (member, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.quotes.read");
        var account = await admin.CreateAccountAsync("Firma");
        var quote = await admin.CreateQuoteAsync(account.Id(), new { ownerUserId = memberId, terms = "eski", notes = "eski not", validUntil = Iso(TenantToday().AddDays(5)) });

        await admin.PutJsonAsync(
            $"{QuotesPath}/{quote.Id()}",
            new { subject = "Yeni konu", accountId = account.Id(), currency = "usd", lines = new[] { Line(1m, 10m, 0m, 0m, "Tek"), Line(3m, 5m, 0m, 0m, "İkinci") } });

        var updated = await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}");
        updated.Str("number").ShouldBe(quote.Str("number"));
        updated.Str("status").ShouldBe("draft");
        updated.Str("subject").ShouldBe("Yeni konu");
        updated.Str("currency").ShouldBe("USD");
        updated.GetProperty("ownerUserId").GetGuid().ShouldBe(memberId, "ownerUserId verilmezse mevcut korunur");
        updated.TryGetProperty("terms", out _).ShouldBeFalse("gönderilmeyen isteğe bağlı alan temizlenir");
        updated.TryGetProperty("notes", out _).ShouldBeFalse();
        updated.TryGetProperty("validUntil", out _).ShouldBeFalse();
        updated.Dec("grandTotal").ShouldBe(25m);
        updated.GetProperty("lines").EnumerateArray().Select(l => l.Str("description")).ShouldBe(["Tek", "İkinci"]);
        updated.GetProperty("lines").EnumerateArray().Select(l => l.Id()).ShouldNotContain(quote.GetProperty("lines")[0].Id(), "kalem kümesi tümden değişir");
        (await admin.GetJsonAsync(QuotesPath)).GetProperty("totalCount").GetInt64().ShouldBe(1);
        (await CountLinesAsync(org.TenantId)).ShouldBe(2, "eski kalemler fiziksel silinir (yetim kalmaz)");
        _ = member;
    }

    private Task<long> CountLinesAsync(Guid tenantId) => factory.CountAsync("quote_lines", tenantId);

    [Fact]
    public async Task Delete_IsSoft_OnlyForDraft()
    {
        var org = await factory.NewOrgAsync("Teklif Sil");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var draft = await admin.CreateQuoteAsync(account.Id());
        var sent = await admin.CreateQuoteAsync(account.Id());
        await admin.ActAsync($"{QuotesPath}/{sent.Id()}/send").ShouldBeNoContentAsync();

        await admin.DeleteJsonAsync($"{QuotesPath}/{draft.Id()}");
        await (await admin.GetAsync($"{QuotesPath}/{draft.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.DeleteAsync($"{QuotesPath}/{draft.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await admin.DeleteAsync($"{QuotesPath}/{sent.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "quote.not_editable");

        (await factory.CountAsync("quotes", org.TenantId)).ShouldBe(2, "yumuşak silme: satır kalır");
        (await factory.ScalarAsync<long>("SELECT count(*) FROM commerce.quote_lines WHERE quote_id = @id", ("id", draft.Id()))).ShouldBe(1, "silinen teklifin kalemleri korunur");
    }

    [Fact]
    public async Task Update_NonDraft_IsNotEditable()
    {
        var admin = (await factory.NewOrgAsync("Teklif Kilit")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var quote = await admin.CreateQuoteAsync(account.Id());
        await admin.ActAsync($"{QuotesPath}/{quote.Id()}/send").ShouldBeNoContentAsync();

        await (await admin.PutAsJsonAsync($"{QuotesPath}/{quote.Id()}", new { subject = "x", accountId = account.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "quote.not_editable");
    }

    [Theory]
    [InlineData("lines[1].quantity", 0)]
    [InlineData("lines[1].quantity", -3)]
    [InlineData("lines[1].quantity", 1_000_000.5)]
    [InlineData("lines[1].quantity", 1.00001)]
    [InlineData("lines[1].unitPrice", -1)]
    [InlineData("lines[1].unitPrice", 1_000_000_001)]
    [InlineData("lines[1].unitPrice", 1.12345)]
    [InlineData("lines[1].discountPercent", 100.5)]
    [InlineData("lines[1].discountPercent", -1)]
    [InlineData("lines[1].discountPercent", 10.123)]
    [InlineData("lines[1].taxRate", 101)]
    [InlineData("lines[1].taxRate", 18.999)]
    [InlineData("lines[1].description", "")]
    public async Task InvalidLineFields_AreReportedWithIndexedKeys(string key, object value)
    {
        var admin = (await factory.NewOrgAsync("Teklif Kalem Doğrulama")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var property = key[(key.IndexOf('.') + 1)..];
        var bad = new Dictionary<string, object?> { ["description"] = "Kalem", ["quantity"] = 1, ["unitPrice"] = 1, ["discountPercent"] = 0, ["taxRate"] = 0 };
        bad[property] = value;

        var response = await admin.PostAsJsonAsync(QuotesPath, new { subject = "K", accountId = account.Id(), lines = new object[] { Line(), bad, Line() } }, Ct);

        await response.ShouldBeValidationErrorAsync(key);
    }

    [Fact]
    public async Task InvalidHeaderFields_AreReported()
    {
        var admin = (await factory.NewOrgAsync("Teklif Başlık Doğrulama")).Admin;
        var account = await admin.CreateAccountAsync("Firma");

        await (await admin.PostAsJsonAsync(QuotesPath, new { subject = "", accountId = account.Id() }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PostAsJsonAsync(QuotesPath, new { subject = new string('s', 201), accountId = account.Id() }, Ct)).ShouldBeValidationErrorAsync("subject");
        await (await admin.PostAsJsonAsync(QuotesPath, new { subject = "x" }, Ct)).ShouldBeValidationErrorAsync("accountId");
        await (await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), currency = "TL" }, Ct)).ShouldBeValidationErrorAsync("currency");
        await (await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), terms = new string('t', 4001) }, Ct)).ShouldBeValidationErrorAsync("terms");
        await (await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), notes = new string('n', 2001) }, Ct)).ShouldBeValidationErrorAsync("notes");
        await (await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), lines = Enumerable.Repeat(Line(), 101).ToArray() }, Ct)).ShouldBeValidationErrorAsync("lines");
        (await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), lines = Enumerable.Repeat(Line(), 100).ToArray() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task DocumentTotalBeyondStorableLimit_IsRejected_WithoutConsumingANumber()
    {
        var org = await factory.NewOrgAsync("Teklif Taşma");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var huge = Line(1_000_000m, 1_000_000_000m, 0m, 100m);

        var response = await admin.PostAsJsonAsync(QuotesPath, new { subject = "Dev", accountId = account.Id(), lines = Enumerable.Repeat(huge, 100).ToArray() }, Ct);

        await response.ReadProblemAsync(HttpStatusCode.BadRequest, "commerce.total_too_large");
        (await factory.CountAsync("quotes", org.TenantId)).ShouldBe(0);
        (await factory.CounterAsync(org.TenantId, "quote", DateTime.UtcNow.Year)).ShouldBe(0, "sayaç geri döndü");
        var big = await admin.PostJsonAsync(QuotesPath, new { subject = "Tek dev", accountId = account.Id(), lines = new[] { huge } });
        big.Dec("grandTotal").ShouldBe(2_000_000_000_000_000m);
    }

    [Fact]
    public async Task Owner_DefaultsToCaller_MustBeActiveMember()
    {
        var org = await factory.NewOrgAsync("Teklif Sahip");
        var other = await factory.NewOrgAsync("Teklif Sahip Diğer");
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.quotes.read");
        var account = await org.Admin.CreateAccountAsync("Firma");

        (await org.Admin.CreateQuoteAsync(account.Id())).GetProperty("ownerUserId").GetGuid().ShouldBe(org.AdminUserId);
        var owned = await org.Admin.CreateQuoteAsync(account.Id(), new { ownerUserId = memberId });
        owned.GetProperty("ownerUserId").GetGuid().ShouldBe(memberId);

        await (await org.Admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), ownerUserId = other.AdminUserId }, Ct)).ReadProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
        await (await org.Admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), ownerUserId = Guid.NewGuid() }, Ct)).ReadProblemAsync(HttpStatusCode.BadRequest, "owner.not_member");
    }

    [Fact]
    public async Task RelatedRecords_AreVerified_AndMustBeConsistent()
    {
        var org = await factory.NewOrgAsync("Teklif Bağlı");
        var other = await factory.NewOrgAsync("Teklif Bağlı Diğer");
        var admin = org.Admin;
        var acme = await admin.CreateAccountAsync("Acme");
        var beta = await admin.CreateAccountAsync("Beta");
        var acmeContact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Kaya", accountId = acme.Id() });
        var loneContact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Yalnız" });
        var betaDeal = await admin.PostJsonAsync($"{Base}/deals", new { name = "Beta fırsatı", accountId = beta.Id() });
        var foreignAccount = await other.Admin.CreateAccountAsync("Yabancı");
        var foreignContact = await other.Admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Yabancı" });
        var foreignDeal = await other.Admin.PostJsonAsync($"{Base}/deals", new { name = "Yabancı fırsat", accountId = foreignAccount.Id() });

        async Task<HttpResponseMessage> Post(object body) => await admin.PostAsJsonAsync(QuotesPath, body, Ct);

        await (await Post(new { subject = "x", accountId = foreignAccount.Id() })).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await Post(new { subject = "x", accountId = Guid.NewGuid() })).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await Post(new { subject = "x", accountId = acme.Id(), contactId = foreignContact.Id() })).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await Post(new { subject = "x", accountId = acme.Id(), dealId = foreignDeal.Id() })).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await Post(new { subject = "x", accountId = acme.Id(), contactId = Guid.NewGuid() })).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
        await (await Post(new { subject = "x", accountId = beta.Id(), contactId = acmeContact.Id() })).ReadProblemAsync(HttpStatusCode.BadRequest, "commerce.contact_account_mismatch");
        await (await Post(new { subject = "x", accountId = acme.Id(), dealId = betaDeal.Id() })).ReadProblemAsync(HttpStatusCode.BadRequest, "commerce.deal_account_mismatch");

        // Firmasız kişi herhangi bir firmayla kullanılabilir; aynı firmanın kişisi/fırsatı geçerli.
        (await Post(new { subject = "x", accountId = acme.Id(), contactId = loneContact.Id() })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Post(new { subject = "x", accountId = acme.Id(), contactId = acmeContact.Id() })).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await Post(new { subject = "x", accountId = beta.Id(), dealId = betaDeal.Id() })).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Silinen firma artık bulunamaz.
        var doomed = await admin.CreateAccountAsync("Silinecek");
        await admin.DeleteJsonAsync($"{Base}/accounts/{doomed.Id()}");
        await (await Post(new { subject = "x", accountId = doomed.Id() })).ReadProblemAsync(HttpStatusCode.NotFound, "commerce.related_not_found");
    }

    [Fact]
    public async Task RelatedRecordDeletedLater_LeavesTheQuote_WithEmptyName()
    {
        var admin = (await factory.NewOrgAsync("Teklif Silinen Bağ")).Admin;
        var account = await admin.CreateAccountAsync("Geçici");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Geçici Kişi", accountId = account.Id() });
        var quote = await admin.CreateQuoteAsync(account.Id(), new { contactId = contact.Id() });
        await admin.DeleteJsonAsync($"{Base}/contacts/{contact.Id()}");

        var read = await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}");

        read.GetProperty("contactId").GetGuid().ShouldBe(contact.Id());
        read.TryGetProperty("contactName", out _).ShouldBeFalse("silinmiş bağlı kayıtta ad boş döner");
        read.Str("accountName").ShouldBe("Geçici");
        await admin.PutJsonAsync($"{QuotesPath}/{quote.Id()}", new { subject = "Yine düzenlenebilir", accountId = account.Id(), contactId = contact.Id() });
    }

    [Fact]
    public async Task Products_AreSnapshotted_AndValidated()
    {
        var admin = (await factory.NewOrgAsync("Teklif Ürün")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var product = await admin.CreateProductAsync("CRM Pro", new { code = "P-1", unitPrice = 100m, taxRate = 20 });
        var usd = await admin.CreateProductAsync("Dolar ürünü", new { currency = "USD" });
        var inactive = await admin.CreateProductAsync("Pasif ürün", new { isActive = false });

        var quote = await admin.CreateQuoteAsync(account.Id(), null, Line(2m, 100m, 0m, 20m, "CRM Pro lisansı", product.Id()));
        quote.GetProperty("lines")[0].GetProperty("productId").GetGuid().ShouldBe(product.Id());

        // Anlık görüntü: ürün sonradan değişse/silinse belge değişmez.
        await admin.PutJsonAsync($"{ProductsPath}/{product.Id()}", new { name = "Yeni ad", unitPrice = 999m, taxRate = 0 });
        await admin.DeleteJsonAsync($"{ProductsPath}/{product.Id()}");
        var read = await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}");
        read.GetProperty("lines")[0].Str("description").ShouldBe("CRM Pro lisansı");
        read.GetProperty("lines")[0].Dec("unitPrice").ShouldBe(100m);
        read.Dec("grandTotal").ShouldBe(240m);

        async Task<HttpResponseMessage> Post(params object[] lines) =>
            await admin.PostAsJsonAsync(QuotesPath, new { subject = "x", accountId = account.Id(), lines }, Ct);

        await (await Post(Line(), Line(productId: Guid.NewGuid()))).ShouldBeValidationErrorAsync("lines[1].productId");
        await (await Post(Line(productId: usd.Id()))).ShouldBeValidationErrorAsync("lines[0].productId");
        await (await Post(Line(productId: inactive.Id()))).ShouldBeValidationErrorAsync("lines[0].productId");
        await (await Post(Line(productId: product.Id()))).ShouldBeValidationErrorAsync("lines[0].productId");

        // Aynı ürün belge para birimiyle uyuşuyorsa geçerli; USD belge + USD ürün.
        var usdQuote = await admin.PostAsJsonAsync(QuotesPath, new { subject = "usd", accountId = account.Id(), currency = "USD", lines = new[] { Line(productId: usd.Id()) } }, Ct);
        usdQuote.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task InactiveProduct_StaysOnAnExistingLine_ButCannotBeAddedNew()
    {
        var admin = (await factory.NewOrgAsync("Teklif Pasif Ürün")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var product = await admin.CreateProductAsync("Sonradan pasif");
        var quote = await admin.CreateQuoteAsync(account.Id(), null, Line(productId: product.Id()));
        await admin.PutJsonAsync($"{ProductsPath}/{product.Id()}", new { name = "Sonradan pasif", isActive = false });

        // Mevcut kalem korunur.
        await admin.PutJsonAsync($"{QuotesPath}/{quote.Id()}", new { subject = "x", accountId = account.Id(), lines = new[] { Line(3m, productId: product.Id()) } });

        // Belgede olmayan pasif ürün eklenemez.
        var another = await admin.CreateQuoteAsync(account.Id());
        var response = await admin.PutAsJsonAsync($"{QuotesPath}/{another.Id()}", new { subject = "x", accountId = account.Id(), lines = new[] { Line(productId: product.Id()) } }, Ct);
        await response.ShouldBeValidationErrorAsync("lines[0].productId");
    }

    [Fact]
    public async Task List_FiltersByStatusOwnerAccountDatesAndConversion()
    {
        var org = await factory.NewOrgAsync("Teklif Liste");
        var admin = org.Admin;
        var (_, memberId) = await factory.AddMemberAsync(org, "Üye", "crm.quotes.read");
        var acme = await admin.CreateAccountAsync("Acme");
        var beta = await admin.CreateAccountAsync("Beta");
        var contact = await admin.PostJsonAsync($"{Base}/contacts", new { lastName = "Kaya", accountId = acme.Id() });
        var deal = await admin.PostJsonAsync($"{Base}/deals", new { name = "F", accountId = acme.Id() });
        var today = TenantToday();

        var draft = await admin.CreateQuoteAsync(acme.Id(), new { subject = "Taslak teklif", contactId = contact.Id(), dealId = deal.Id(), validUntil = Iso(today.AddDays(10)) });
        var sent = await admin.CreateQuoteAsync(beta.Id(), new { subject = "Gönderilmiş", ownerUserId = memberId, validUntil = Iso(today.AddDays(20)) });
        var accepted = await admin.CreateQuoteAsync(acme.Id(), new { subject = "Kabul" });
        var rejected = await admin.CreateQuoteAsync(beta.Id(), new { subject = "Ret" });
        var expired = await admin.CreateQuoteAsync(beta.Id(), new { subject = "Süresi dolan", validUntil = Iso(today.AddDays(1)) });
        foreach (var quote in new[] { sent, accepted, rejected, expired })
        {
            await admin.ActAsync($"{QuotesPath}/{quote.Id()}/send").ShouldBeNoContentAsync();
        }

        await admin.ActAsync($"{QuotesPath}/{accepted.Id()}/accept").ShouldBeNoContentAsync();
        await admin.ActAsync($"{QuotesPath}/{rejected.Id()}/reject", new { reason = "pahalı" }).ShouldBeNoContentAsync();
        await factory.ExecuteAsync("UPDATE commerce.quotes SET valid_until = @d WHERE id = @id", ("d", today.AddDays(-2)), ("id", expired.Id()));
        await admin.ActAsync($"{QuotesPath}/{accepted.Id()}/convert").ShouldBeStatusAsync(HttpStatusCode.Created);

        async Task<List<Guid>> Ids(string query) => await admin.ListIdsAsync(QuotesPath, query);

        (await Ids("?status=draft")).ShouldBe([draft.Id()]);
        (await Ids("?status=sent")).ShouldBe([sent.Id()], "sent süresi dolmamışları döner");
        (await Ids("?status=expired")).ShouldBe([expired.Id()]);
        (await Ids("?status=accepted")).ShouldBe([accepted.Id()]);
        (await Ids("?status=rejected")).ShouldBe([rejected.Id()]);
        (await Ids($"?accountId={acme.Id()}")).ShouldBe([draft.Id(), accepted.Id()], ignoreOrder: true);
        (await Ids($"?contactId={contact.Id()}")).ShouldBe([draft.Id()]);
        (await Ids($"?dealId={deal.Id()}")).ShouldBe([draft.Id()]);
        (await Ids($"?ownerUserId={memberId}")).ShouldBe([sent.Id()]);
        (await Ids($"?validFrom={Iso(today.AddDays(5))}&validTo={Iso(today.AddDays(15))}")).ShouldBe([draft.Id()]);
        (await Ids($"?validFrom={Iso(today.AddDays(10))}&validTo={Iso(today.AddDays(20))}")).ShouldBe([draft.Id(), sent.Id()], ignoreOrder: true, "uçlar dahil; validUntil boş olanlar dışarıda");
        (await Ids("?converted=true")).ShouldBe([accepted.Id()]);
        (await Ids("?converted=false")).Count.ShouldBe(4);
        (await Ids("?q=süresi")).ShouldBe([expired.Id()]);
        (await Ids($"?q={draft.Str("number")}")).ShouldBe([draft.Id()]);

        var summary = (await admin.GetJsonAsync($"{QuotesPath}?status=accepted")).GetProperty("items")[0];
        summary.Str("status").ShouldBe("accepted");
        summary.Str("accountName").ShouldBe("Acme");
        summary.Str("ownerName").ShouldBe(org.AdminName);
        summary.GetProperty("convertedOrderId").ValueKind.ShouldBe(JsonValueKind.String);
        summary.TryGetProperty("lines", out _).ShouldBeFalse("liste özeti kalem taşımaz");
        (await admin.GetJsonAsync($"{QuotesPath}?status=expired")).GetProperty("items")[0].Str("status").ShouldBe("expired");
    }

    [Fact]
    public async Task List_SortsByWhitelist_NullValidUntilAlwaysLast_AndDefaultIsNewestFirst()
    {
        var admin = (await factory.NewOrgAsync("Teklif Sıra")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var today = TenantToday();
        var first = await admin.CreateQuoteAsync(account.Id(), new { subject = "B konu", validUntil = Iso(today.AddDays(20)) }, Line(1m, 300m, 0m, 0m));
        var second = await admin.CreateQuoteAsync(account.Id(), new { subject = "A konu" }, Line(1m, 100m, 0m, 0m));
        var third = await admin.CreateQuoteAsync(account.Id(), new { subject = "C konu", validUntil = Iso(today.AddDays(10)) }, Line(1m, 200m, 0m, 0m));

        (await admin.ListIdsAsync(QuotesPath)).ShouldBe([third.Id(), second.Id(), first.Id()], "varsayılan -createdAt");
        (await admin.ListIdsAsync(QuotesPath, "?sort=createdAt")).ShouldBe([first.Id(), second.Id(), third.Id()]);
        (await admin.ListIdsAsync(QuotesPath, "?sort=number")).ShouldBe([first.Id(), second.Id(), third.Id()]);
        (await admin.ListIdsAsync(QuotesPath, "?sort=-number")).ShouldBe([third.Id(), second.Id(), first.Id()]);
        (await admin.ListIdsAsync(QuotesPath, "?sort=subject")).ShouldBe([second.Id(), first.Id(), third.Id()]);
        (await admin.ListIdsAsync(QuotesPath, "?sort=-grandTotal")).ShouldBe([first.Id(), third.Id(), second.Id()]);
        (await admin.ListIdsAsync(QuotesPath, "?sort=grandTotal")).ShouldBe([second.Id(), third.Id(), first.Id()]);
        (await admin.ListIdsAsync(QuotesPath, "?sort=validUntil")).ShouldBe([third.Id(), first.Id(), second.Id()], "boş validUntil sonda");
        (await admin.ListIdsAsync(QuotesPath, "?sort=-validUntil")).ShouldBe([first.Id(), third.Id(), second.Id()], "boş validUntil azalanda da sonda");
        (await admin.ListIdsAsync(QuotesPath, "?sort=yok")).ShouldBe([third.Id(), second.Id(), first.Id()]);

        var page = await admin.GetJsonAsync($"{QuotesPath}?sort=number&pageSize=2&page=2");
        page.GetProperty("totalCount").GetInt64().ShouldBe(3);
        page.GetProperty("items").EnumerateArray().Select(i => i.Id()).ShouldBe([third.Id()]);
    }

    [Fact]
    public async Task StateMachine_Endpoints_FollowThePlanTable()
    {
        var admin = (await factory.NewOrgAsync("Teklif Durum")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var quote = await admin.CreateQuoteAsync(account.Id());
        var url = $"{QuotesPath}/{quote.Id()}";

        // draft: yalnız send geçerli.
        var acceptDraft = await (await admin.ActAsync($"{url}/accept")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");
        acceptDraft.GetProperty("args").GetProperty("from").GetString().ShouldBe("draft");
        acceptDraft.GetProperty("args").GetProperty("to").GetString().ShouldBe("accepted");
        await (await admin.ActAsync($"{url}/reject")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");
        await (await admin.ActAsync($"{url}/revert")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");
        await (await admin.ActAsync($"{url}/extend", new { validUntil = Iso(TenantToday().AddDays(3)) })).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");
        await (await admin.ActAsync($"{url}/convert")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.not_accepted");

        await admin.ActAsync($"{url}/send").ShouldBeNoContentAsync();
        var sent = await admin.GetJsonAsync(url);
        sent.Str("status").ShouldBe("sent");
        sent.TryGetProperty("sentAt", out _).ShouldBeTrue();
        await (await admin.ActAsync($"{url}/send")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");

        // sent → revert → draft (sentAt temizlenir) → düzenlenebilir.
        await admin.ActAsync($"{url}/revert").ShouldBeNoContentAsync();
        var reverted = await admin.GetJsonAsync(url);
        reverted.Str("status").ShouldBe("draft");
        reverted.TryGetProperty("sentAt", out _).ShouldBeFalse();
        await admin.PutJsonAsync(url, new { subject = "Yeniden düzenlendi", accountId = account.Id(), lines = new[] { Line() } });

        // sent → reject (nedenli) → rejected → revert.
        await admin.ActAsync($"{url}/send").ShouldBeNoContentAsync();
        await admin.ActAsync($"{url}/reject", new { reason = "Bütçe yok" }).ShouldBeNoContentAsync();
        var rejected = await admin.GetJsonAsync(url);
        rejected.Str("status").ShouldBe("rejected");
        rejected.Str("rejectionReason").ShouldBe("Bütçe yok");
        rejected.TryGetProperty("rejectedAt", out _).ShouldBeTrue();
        await (await admin.PutAsJsonAsync(url, new { subject = "x", accountId = account.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "quote.not_editable");
        await admin.ActAsync($"{url}/revert").ShouldBeNoContentAsync();
        var afterRevert = await admin.GetJsonAsync(url);
        afterRevert.TryGetProperty("rejectionReason", out _).ShouldBeFalse();
        afterRevert.TryGetProperty("rejectedAt", out _).ShouldBeFalse();

        // sent → accept → accepted (uç).
        await admin.ActAsync($"{url}/send").ShouldBeNoContentAsync();
        await admin.ActAsync($"{url}/accept").ShouldBeNoContentAsync();
        (await admin.GetJsonAsync(url)).TryGetProperty("acceptedAt", out _).ShouldBeTrue();
        await (await admin.ActAsync($"{url}/accept")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");
        await (await admin.ActAsync($"{url}/revert")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");
        await (await admin.ActAsync($"{url}/reject")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");
        await (await admin.ActAsync($"{url}/send")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.invalid_transition");
        await (await admin.DeleteAsync(url, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "quote.not_editable");
        await (await admin.PutAsJsonAsync(url, new { subject = "x", accountId = account.Id() }, Ct)).ReadProblemAsync(HttpStatusCode.Conflict, "quote.not_editable");
    }

    [Fact]
    public async Task Send_RequiresLines_AndAFutureOrBlankValidUntil()
    {
        var org = await factory.NewOrgAsync("Teklif Gönder");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var empty = await admin.PostJsonAsync(QuotesPath, new { subject = "Boş", accountId = account.Id() });
        await (await admin.ActAsync($"{QuotesPath}/{empty.Id()}/send")).ReadProblemAsync(HttpStatusCode.UnprocessableEntity, "quote.no_lines");
        (await admin.GetJsonAsync($"{QuotesPath}/{empty.Id()}")).Str("status").ShouldBe("draft");

        var stale = await admin.CreateQuoteAsync(account.Id(), new { validUntil = Iso(TenantToday().AddDays(1)) });
        await factory.ExecuteAsync("UPDATE commerce.quotes SET valid_until = @d WHERE id = @id", ("d", TenantToday().AddDays(-1)), ("id", stale.Id()));
        await (await admin.ActAsync($"{QuotesPath}/{stale.Id()}/send")).ShouldBeValidationErrorAsync("validUntil");

        var today = await admin.CreateQuoteAsync(account.Id(), new { validUntil = Iso(TenantToday()) });
        await admin.ActAsync($"{QuotesPath}/{today.Id()}/send").ShouldBeNoContentAsync();
        var blank = await admin.CreateQuoteAsync(account.Id());
        await admin.ActAsync($"{QuotesPath}/{blank.Id()}/send").ShouldBeNoContentAsync();
    }

    [Fact]
    public async Task Expiry_IsDerived_AcceptIsBlocked_AndExtendOrRevertRecovers()
    {
        var admin = (await factory.NewOrgAsync("Teklif Süre")).Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var today = TenantToday();

        async Task<Guid> ExpiredAsync()
        {
            var quote = await admin.CreateQuoteAsync(account.Id(), new { validUntil = Iso(today.AddDays(1)) });
            await admin.ActAsync($"{QuotesPath}/{quote.Id()}/send").ShouldBeNoContentAsync();
            await factory.ExecuteAsync("UPDATE commerce.quotes SET valid_until = @d WHERE id = @id", ("d", today.AddDays(-1)), ("id", quote.Id()));
            return quote.Id();
        }

        var id = await ExpiredAsync();
        var read = await admin.GetJsonAsync($"{QuotesPath}/{id}");
        read.Str("status").ShouldBe("expired");
        (await factory.ScalarAsync<string>("SELECT status FROM commerce.quotes WHERE id = @id", ("id", id))).ShouldBe("Sent", "saklanan değer sent kalır");

        await (await admin.ActAsync($"{QuotesPath}/{id}/accept")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.expired");
        await (await admin.ActAsync($"{QuotesPath}/{id}/extend", new { validUntil = Iso(today.AddDays(-1)) })).ShouldBeValidationErrorAsync("validUntil");
        await admin.ActAsync($"{QuotesPath}/{id}/extend", new { validUntil = Iso(today.AddDays(7)) }).ShouldBeNoContentAsync();
        (await admin.GetJsonAsync($"{QuotesPath}/{id}")).Str("status").ShouldBe("sent");
        await admin.ActAsync($"{QuotesPath}/{id}/accept").ShouldBeNoContentAsync();

        var toReject = await ExpiredAsync();
        await admin.ActAsync($"{QuotesPath}/{toReject}/reject", new { reason = "geç kaldı" }).ShouldBeNoContentAsync();
        (await admin.GetJsonAsync($"{QuotesPath}/{toReject}")).Str("status").ShouldBe("rejected");

        var toRevert = await ExpiredAsync();
        await admin.ActAsync($"{QuotesPath}/{toRevert}/revert").ShouldBeNoContentAsync();
        (await admin.GetJsonAsync($"{QuotesPath}/{toRevert}")).Str("status").ShouldBe("draft");

        // extend gövdesi zorunlu.
        var another = await ExpiredAsync();
        await (await admin.ActAsync($"{QuotesPath}/{another}/extend", new { })).ShouldBeValidationErrorAsync("validUntil");
    }

    [Fact]
    public async Task Expiry_UsesTheTenantTimeZone_ForTheDayBoundary()
    {
        // Pago Pago (UTC-11) ile Kiritimati (UTC+14) arasında 25 saat fark vardır: aynı anda yerel takvim günü Kiritimati'de mutlaka
        // Pago Pago'dakinden ileridedir. Böylece saatten bağımsız deterministik: validUntil = Pago Pago bugünü → o saat diliminde süresi
        // dolmamış; kiracı saat dilimi Kiritimati'ye alınınca aynı teklif "expired" olur (süre dolumu kiracı saatinden türetilir).
        var org = await factory.NewOrgAsync("Teklif Saat Dilimi");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        async Task SetZoneAsync(string zone) =>
            (await admin.PutAsJsonAsync($"{Base}/organization", new { name = "TZ Org", defaultLocale = "tr", timeZone = zone }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await SetZoneAsync("Pacific/Pago_Pago");
        var quote = await admin.CreateQuoteAsync(account.Id(), new { validUntil = Iso(TenantToday("Pacific/Pago_Pago")) });
        await admin.ActAsync($"{QuotesPath}/{quote.Id()}/send").ShouldBeNoContentAsync();
        (await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}")).Str("status").ShouldBe("sent");

        await SetZoneAsync("Pacific/Kiritimati");
        (await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}")).Str("status").ShouldBe("expired");
        (await admin.ListIdsAsync(QuotesPath, "?status=expired")).ShouldBe([quote.Id()]);
        await (await admin.ActAsync($"{QuotesPath}/{quote.Id()}/accept")).ReadProblemAsync(HttpStatusCode.Conflict, "quote.expired");

        await SetZoneAsync("Pacific/Pago_Pago");
        (await admin.GetJsonAsync($"{QuotesPath}/{quote.Id()}")).Str("status").ShouldBe("sent");
    }

    [Fact]
    public async Task Audit_RecordsQuoteLifecycle_WithCamelCaseEnumsAndTotalsDiff()
    {
        var org = await factory.NewOrgAsync("Teklif Denetim");
        var admin = org.Admin;
        var account = await admin.CreateAccountAsync("Firma");
        var quote = await admin.CreateQuoteAsync(account.Id(), null, Line(1m, 100m, 0m, 0m));
        await admin.PutJsonAsync($"{QuotesPath}/{quote.Id()}", new { subject = "Yeni konu", accountId = account.Id(), lines = new[] { Line(1m, 100m, 0m, 0m), Line(1m, 50m, 0m, 0m) } });
        await admin.ActAsync($"{QuotesPath}/{quote.Id()}/send").ShouldBeNoContentAsync();
        await admin.ActAsync($"{QuotesPath}/{quote.Id()}/reject").ShouldBeNoContentAsync();
        await admin.ActAsync($"{QuotesPath}/{quote.Id()}/revert").ShouldBeNoContentAsync();
        await admin.DeleteJsonAsync($"{QuotesPath}/{quote.Id()}");
        var (reader, _) = await factory.AddMemberAsync(org, "Okuyucu", "crm.quotes.read");
        var (writerOnly, _) = await factory.AddMemberAsync(org, "Yazıcı", "crm.quotes.write");

        var audit = await reader.GetJsonAsync($"{Base}/audit?entityType=Quote&entityId={quote.Id()}");
        var items = audit.GetProperty("items").EnumerateArray().ToList();

        items.Select(i => i.Str("action")).ShouldBe(["deleted", "updated", "updated", "updated", "updated", "created"]);
        items.ShouldAllBe(i => i.Str("entityType") == "Quote");
        var created = items[^1].GetProperty("changes");
        created.GetProperty("status").GetProperty("new").GetString().ShouldBe("draft");
        created.GetProperty("grandTotal").GetProperty("new").GetDecimal().ShouldBe(100m);
        var updated = items[^2].GetProperty("changes");
        updated.GetProperty("grandTotal").GetProperty("old").GetDecimal().ShouldBe(100m, "kalem değişikliği başlığın toplam farkıyla görünür");
        updated.GetProperty("grandTotal").GetProperty("new").GetDecimal().ShouldBe(150m);
        updated.GetProperty("subject").GetProperty("new").GetString().ShouldBe("Yeni konu");
        var sent = items[^3].GetProperty("changes");
        sent.GetProperty("status").GetProperty("old").GetString().ShouldBe("draft");
        sent.GetProperty("status").GetProperty("new").GetString().ShouldBe("sent");
        items[1].GetProperty("changes").GetProperty("status").GetProperty("new").GetString().ShouldBe("draft");
        audit.GetRawText().ShouldNotContain("\"Draft\"", Case.Sensitive);

        await (await writerOnly.GetAsync($"{Base}/audit?entityType=Quote&entityId={quote.Id()}", Ct)).ReadProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        (await admin.GetJsonAsync($"{Base}/organization/audit?page=1&pageSize=100")).GetProperty("items").EnumerateArray().Any(i => i.Str("entityType") == "Quote").ShouldBeTrue("org.audit.read akışında görünür");
    }
}

internal static class QuoteAssertions
{
    public static async Task ShouldBeStatusAsync(this Task<HttpResponseMessage> pending, HttpStatusCode expected)
    {
        var response = await pending;
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(CommerceApiKit.Ct));
    }
}
