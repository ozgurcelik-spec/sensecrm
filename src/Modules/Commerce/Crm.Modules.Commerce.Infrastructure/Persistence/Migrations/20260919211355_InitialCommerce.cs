using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Modules.Commerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCommerce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "commerce");

            migrationBuilder.CreateTable(
                name: "document_counters",
                schema: "commerce",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    last_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_counters", x => new { x.tenant_id, x.kind, x.year });
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "commerce",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handler = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => new { x.message_id, x.handler });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", maxLength: 500, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_dead = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    code_normalized = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    terms = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quotes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sales_orders",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    order_date = table.Column<DateOnly>(type: "date", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: true),
                    fulfilled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    terms = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quote_lines",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    line_subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    line_discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    line_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_quote_lines_quotes_quote_id",
                        column: x => x.quote_id,
                        principalSchema: "commerce",
                        principalTable: "quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sales_order_lines",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    discount_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    line_subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    line_discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    line_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_order_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_sales_order_lines_sales_orders_order_id",
                        column: x => x.order_id,
                        principalSchema: "commerce",
                        principalTable: "sales_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_counters_tenant_id",
                schema: "commerce",
                table: "document_counters",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed_at_next_attempt_at",
                schema: "commerce",
                table: "outbox_messages",
                columns: new[] { "processed_at", "next_attempt_at" },
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_tenant_id",
                schema: "commerce",
                table: "outbox_messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id",
                schema: "commerce",
                table: "products",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_created_at",
                schema: "commerce",
                table: "products",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_is_active",
                schema: "commerce",
                table: "products",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_name",
                schema: "commerce",
                table: "products",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ux_products_tenant_code",
                schema: "commerce",
                table: "products",
                columns: new[] { "tenant_id", "code_normalized" },
                unique: true,
                filter: "is_deleted = false AND code_normalized IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_quote_lines_quote_id",
                schema: "commerce",
                table: "quote_lines",
                column: "quote_id");

            migrationBuilder.CreateIndex(
                name: "ix_quote_lines_tenant_id",
                schema: "commerce",
                table: "quote_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_quote_lines_tenant_id_product_id",
                schema: "commerce",
                table: "quote_lines",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quote_lines_tenant_id_quote_id_position",
                schema: "commerce",
                table: "quote_lines",
                columns: new[] { "tenant_id", "quote_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_tenant_id",
                schema: "commerce",
                table: "quotes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_quotes_tenant_id_account_id",
                schema: "commerce",
                table: "quotes",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_tenant_id_contact_id",
                schema: "commerce",
                table: "quotes",
                columns: new[] { "tenant_id", "contact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_tenant_id_created_at",
                schema: "commerce",
                table: "quotes",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_tenant_id_deal_id",
                schema: "commerce",
                table: "quotes",
                columns: new[] { "tenant_id", "deal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_tenant_id_owner_user_id",
                schema: "commerce",
                table: "quotes",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_tenant_id_status",
                schema: "commerce",
                table: "quotes",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_tenant_id_valid_until",
                schema: "commerce",
                table: "quotes",
                columns: new[] { "tenant_id", "valid_until" });

            migrationBuilder.CreateIndex(
                name: "ux_quotes_tenant_number",
                schema: "commerce",
                table: "quotes",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_order_lines_order_id",
                schema: "commerce",
                table: "sales_order_lines",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_order_lines_tenant_id",
                schema: "commerce",
                table: "sales_order_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_order_lines_tenant_id_order_id_position",
                schema: "commerce",
                table: "sales_order_lines",
                columns: new[] { "tenant_id", "order_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_order_lines_tenant_id_product_id",
                schema: "commerce",
                table: "sales_order_lines",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_tenant_id",
                schema: "commerce",
                table: "sales_orders",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_tenant_id_account_id",
                schema: "commerce",
                table: "sales_orders",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_tenant_id_contact_id",
                schema: "commerce",
                table: "sales_orders",
                columns: new[] { "tenant_id", "contact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_tenant_id_created_at",
                schema: "commerce",
                table: "sales_orders",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_tenant_id_deal_id",
                schema: "commerce",
                table: "sales_orders",
                columns: new[] { "tenant_id", "deal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_tenant_id_order_date",
                schema: "commerce",
                table: "sales_orders",
                columns: new[] { "tenant_id", "order_date" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_tenant_id_owner_user_id",
                schema: "commerce",
                table: "sales_orders",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_tenant_id_status",
                schema: "commerce",
                table: "sales_orders",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_sales_orders_tenant_number",
                schema: "commerce",
                table: "sales_orders",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sales_orders_tenant_quote",
                schema: "commerce",
                table: "sales_orders",
                columns: new[] { "tenant_id", "quote_id" },
                unique: true,
                filter: "quote_id IS NOT NULL AND is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_counters",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "products",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "quote_lines",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "sales_order_lines",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "quotes",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "sales_orders",
                schema: "commerce");
        }
    }
}
