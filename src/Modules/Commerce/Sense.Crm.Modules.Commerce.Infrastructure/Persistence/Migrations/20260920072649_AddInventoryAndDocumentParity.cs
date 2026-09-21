using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sense.Crm.Modules.Commerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryAndDocumentParity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "adjustment",
                schema: "commerce",
                table: "sales_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "billing_building",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_city",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_country",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_postal_code",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_state",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_street",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "carrier",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "customer_po_number",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "due_date",
                schema: "commerce",
                table: "sales_orders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "excise_tax",
                schema: "commerce",
                table: "sales_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "price_book_id",
                schema: "commerce",
                table: "sales_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "sales_commission",
                schema: "commerce",
                table: "sales_orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_building",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_city",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_country",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_postal_code",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_state",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_street",
                schema: "commerce",
                table: "sales_orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "adjustment",
                schema: "commerce",
                table: "quotes",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "billing_building",
                schema: "commerce",
                table: "quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_city",
                schema: "commerce",
                table: "quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_country",
                schema: "commerce",
                table: "quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_postal_code",
                schema: "commerce",
                table: "quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_state",
                schema: "commerce",
                table: "quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "billing_street",
                schema: "commerce",
                table: "quotes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "carrier",
                schema: "commerce",
                table: "quotes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "price_book_id",
                schema: "commerce",
                table: "quotes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_building",
                schema: "commerce",
                table: "quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_city",
                schema: "commerce",
                table: "quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_country",
                schema: "commerce",
                table: "quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_postal_code",
                schema: "commerce",
                table: "quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_state",
                schema: "commerce",
                table: "quotes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "shipping_street",
                schema: "commerce",
                table: "quotes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "purchase_price",
                schema: "commerce",
                table: "products",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "vendor_id",
                schema: "commerce",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_price_books",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_book_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_price_books", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    invoice_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_po_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    excise_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    sales_commission = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    paid_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    terms = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    carrier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    billing_street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    billing_building = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    billing_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    billing_state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    billing_postal_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    billing_country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    shipping_building = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_postal_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    adjustment = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    grand_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_book_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "price_books",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    name_normalized = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    pricing_model = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    adjustment_percent = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
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
                    table.PrimaryKey("pk_price_books", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "purchase_orders",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    vendor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    po_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    excise_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    sales_commission = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    terms = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    carrier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    billing_street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    billing_building = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    billing_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    billing_state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    billing_postal_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    billing_country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    shipping_building = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_postal_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    adjustment = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false, defaultValue: 0m),
                    grand_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchase_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vendors",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    website = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    gl_account = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address_street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    address_building = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address_state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address_postal_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address_country = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    email_opt_out = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_vendors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_invoice_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "commerce",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoice_payments",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: false),
                    method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_payments", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_payments_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "commerce",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "price_book_entries",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_book_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_book_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_price_book_entries_price_books_price_book_id",
                        column: x => x.price_book_id,
                        principalSchema: "commerce",
                        principalTable: "price_books",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "purchase_order_lines",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_order_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_purchase_order_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_purchase_order_lines_purchase_orders_purchase_order_id",
                        column: x => x.purchase_order_id,
                        principalSchema: "commerce",
                        principalTable: "purchase_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sales_orders_tenant_id_price_book_id",
                schema: "commerce",
                table: "sales_orders",
                columns: new[] { "tenant_id", "price_book_id" });

            migrationBuilder.CreateIndex(
                name: "ix_quotes_tenant_id_price_book_id",
                schema: "commerce",
                table: "quotes",
                columns: new[] { "tenant_id", "price_book_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_vendor_id",
                schema: "commerce",
                table: "products",
                columns: new[] { "tenant_id", "vendor_id" });

            migrationBuilder.CreateIndex(
                name: "ix_account_price_books_tenant_id",
                schema: "commerce",
                table: "account_price_books",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_price_books_tenant_id_price_book_id",
                schema: "commerce",
                table: "account_price_books",
                columns: new[] { "tenant_id", "price_book_id" });

            migrationBuilder.CreateIndex(
                name: "ux_account_price_books_tenant_account",
                schema: "commerce",
                table: "account_price_books",
                columns: new[] { "tenant_id", "account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_invoice_id",
                schema: "commerce",
                table: "invoice_lines",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_tenant_id",
                schema: "commerce",
                table: "invoice_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_tenant_id_invoice_id_position",
                schema: "commerce",
                table: "invoice_lines",
                columns: new[] { "tenant_id", "invoice_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_tenant_id_product_id",
                schema: "commerce",
                table: "invoice_lines",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_payments_invoice_id",
                schema: "commerce",
                table: "invoice_payments",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_payments_tenant_id",
                schema: "commerce",
                table: "invoice_payments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_payments_tenant_id_invoice_id_paid_on",
                schema: "commerce",
                table: "invoice_payments",
                columns: new[] { "tenant_id", "invoice_id", "paid_on" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id",
                schema: "commerce",
                table: "invoices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_account_id",
                schema: "commerce",
                table: "invoices",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_created_at",
                schema: "commerce",
                table: "invoices",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_deal_id",
                schema: "commerce",
                table: "invoices",
                columns: new[] { "tenant_id", "deal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_due_date",
                schema: "commerce",
                table: "invoices",
                columns: new[] { "tenant_id", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_invoice_date",
                schema: "commerce",
                table: "invoices",
                columns: new[] { "tenant_id", "invoice_date" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_owner_user_id",
                schema: "commerce",
                table: "invoices",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_status",
                schema: "commerce",
                table: "invoices",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_invoices_tenant_number",
                schema: "commerce",
                table: "invoices",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_invoices_tenant_order",
                schema: "commerce",
                table: "invoices",
                columns: new[] { "tenant_id", "order_id" },
                unique: true,
                filter: "order_id IS NOT NULL AND is_deleted = false AND status <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "ix_price_book_entries_price_book_id",
                schema: "commerce",
                table: "price_book_entries",
                column: "price_book_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_book_entries_tenant_id",
                schema: "commerce",
                table: "price_book_entries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_book_entries_tenant_id_product_id",
                schema: "commerce",
                table: "price_book_entries",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ux_price_book_entries_tenant_book_product",
                schema: "commerce",
                table: "price_book_entries",
                columns: new[] { "tenant_id", "price_book_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_books_tenant_id",
                schema: "commerce",
                table: "price_books",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_books_tenant_id_is_active",
                schema: "commerce",
                table: "price_books",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_price_books_tenant_id_owner_user_id",
                schema: "commerce",
                table: "price_books",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_price_books_tenant_name",
                schema: "commerce",
                table: "price_books",
                columns: new[] { "tenant_id", "name_normalized" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_purchase_order_id",
                schema: "commerce",
                table: "purchase_order_lines",
                column: "purchase_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_tenant_id",
                schema: "commerce",
                table: "purchase_order_lines",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_tenant_id_product_id",
                schema: "commerce",
                table: "purchase_order_lines",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_order_lines_tenant_id_purchase_order_id_position",
                schema: "commerce",
                table: "purchase_order_lines",
                columns: new[] { "tenant_id", "purchase_order_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_tenant_id",
                schema: "commerce",
                table: "purchase_orders",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_tenant_id_created_at",
                schema: "commerce",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_tenant_id_owner_user_id",
                schema: "commerce",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_tenant_id_po_date",
                schema: "commerce",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "po_date" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_tenant_id_status",
                schema: "commerce",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_orders_tenant_id_vendor_id",
                schema: "commerce",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "vendor_id" });

            migrationBuilder.CreateIndex(
                name: "ux_purchase_orders_tenant_number",
                schema: "commerce",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vendors_tenant_id",
                schema: "commerce",
                table: "vendors",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_vendors_tenant_id_category",
                schema: "commerce",
                table: "vendors",
                columns: new[] { "tenant_id", "category" });

            migrationBuilder.CreateIndex(
                name: "ix_vendors_tenant_id_created_at",
                schema: "commerce",
                table: "vendors",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vendors_tenant_id_name",
                schema: "commerce",
                table: "vendors",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_vendors_tenant_id_owner_user_id",
                schema: "commerce",
                table: "vendors",
                columns: new[] { "tenant_id", "owner_user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_price_books",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "invoice_payments",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "price_book_entries",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "purchase_order_lines",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "vendors",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "price_books",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "purchase_orders",
                schema: "commerce");

            migrationBuilder.DropIndex(
                name: "ix_sales_orders_tenant_id_price_book_id",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropIndex(
                name: "ix_quotes_tenant_id_price_book_id",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropIndex(
                name: "ix_products_tenant_id_vendor_id",
                schema: "commerce",
                table: "products");

            migrationBuilder.DropColumn(
                name: "adjustment",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "billing_building",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "billing_city",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "billing_country",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "billing_postal_code",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "billing_state",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "billing_street",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "carrier",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "customer_po_number",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "due_date",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "excise_tax",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "pending",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "price_book_id",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "sales_commission",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "shipping_building",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "shipping_city",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "shipping_country",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "shipping_postal_code",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "shipping_state",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "shipping_street",
                schema: "commerce",
                table: "sales_orders");

            migrationBuilder.DropColumn(
                name: "adjustment",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "billing_building",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "billing_city",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "billing_country",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "billing_postal_code",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "billing_state",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "billing_street",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "carrier",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "price_book_id",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "shipping_building",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "shipping_city",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "shipping_country",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "shipping_postal_code",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "shipping_state",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "shipping_street",
                schema: "commerce",
                table: "quotes");

            migrationBuilder.DropColumn(
                name: "purchase_price",
                schema: "commerce",
                table: "products");

            migrationBuilder.DropColumn(
                name: "vendor_id",
                schema: "commerce",
                table: "products");
        }
    }
}
