using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PosSystem.Backend.Migrations
{
    /// <inheritdoc />
    public partial class InitialBackendSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    terminal_id = table.Column<string>(type: "text", nullable: false),
                    action_type = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    performed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by_name = table.Column<string>(type: "text", nullable: false),
                    authorized_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authorized_by_name = table.Column<string>(type: "text", nullable: false),
                    reference_id = table.Column<string>(type: "text", nullable: true),
                    is_synced = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cashiers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    pin_hash = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cashiers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    color_hex = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    loyalty_points = table.Column<int>(type: "integer", nullable: false),
                    store_credit_balance = table.Column<decimal>(type: "numeric", nullable: false),
                    total_spent = table.Column<decimal>(type: "numeric", nullable: false),
                    visit_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inventory_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    quantity_delta = table.Column<int>(type: "integer", nullable: false),
                    stock_after = table.Column<int>(type: "integer", nullable: false),
                    reference_id = table.Column<string>(type: "text", nullable: true),
                    performed_by = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_movements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_number = table.Column<string>(type: "text", nullable: false),
                    terminal_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cashier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cashier_name = table.Column<string>(type: "text", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_name = table.Column<string>(type: "text", nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric", nullable: false),
                    discount_percentage = table.Column<decimal>(type: "numeric", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    tax_rate_percentage = table.Column<decimal>(type: "numeric", nullable: false),
                    is_tax_inclusive = table.Column<bool>(type: "boolean", nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    refunded_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    payment_method = table.Column<int>(type: "integer", nullable: false),
                    cash_tendered = table.Column<decimal>(type: "numeric", nullable: false),
                    card_tendered = table.Column<decimal>(type: "numeric", nullable: false),
                    transfer_tendered = table.Column<decimal>(type: "numeric", nullable: false),
                    amount_tendered = table.Column<decimal>(type: "numeric", nullable: false),
                    change_due = table.Column<decimal>(type: "numeric", nullable: false),
                    payment_reference = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    original_sale_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shift_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    terminal_id = table.Column<string>(type: "text", nullable: false),
                    cashier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_name = table.Column<string>(type: "text", nullable: false),
                    opened_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    opening_float = table.Column<decimal>(type: "numeric", nullable: false),
                    cash_sales_total = table.Column<decimal>(type: "numeric", nullable: false),
                    card_sales_total = table.Column<decimal>(type: "numeric", nullable: false),
                    transfer_sales_total = table.Column<decimal>(type: "numeric", nullable: false),
                    refunds_total = table.Column<decimal>(type: "numeric", nullable: false),
                    cash_drops_total = table.Column<decimal>(type: "numeric", nullable: false),
                    expected_cash = table.Column<decimal>(type: "numeric", nullable: false),
                    actual_counted_cash = table.Column<decimal>(type: "numeric", nullable: true),
                    variance = table.Column<decimal>(type: "numeric", nullable: true),
                    total_transactions = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    is_closed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shift_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "store_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_store_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tax_rates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    percentage = table.Column<decimal>(type: "numeric", nullable: false),
                    is_inclusive = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_rates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    barcode = table.Column<string>(type: "text", nullable: true),
                    unit = table.Column<string>(type: "text", nullable: false),
                    cost_price = table.Column<decimal>(type: "numeric", nullable: false),
                    price = table.Column<decimal>(type: "numeric", nullable: false),
                    is_taxable = table.Column<bool>(type: "boolean", nullable: false),
                    image_url = table.Column<string>(type: "text", nullable: true),
                    stock = table.Column<int>(type: "integer", nullable: false),
                    reorder_level = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.ForeignKey(
                        name: "fk_products_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_line_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    returned_quantity = table.Column<int>(type: "integer", nullable: false),
                    measured_weight_kg = table.Column<decimal>(type: "numeric", nullable: true),
                    unit_price = table.Column<decimal>(type: "numeric", nullable: false),
                    cost_price = table.Column<decimal>(type: "numeric", nullable: false),
                    line_discount_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    is_taxable = table.Column<bool>(type: "boolean", nullable: false),
                    line_subtotal = table.Column<decimal>(type: "numeric", nullable: false),
                    line_tax = table.Column<decimal>(type: "numeric", nullable: false),
                    line_total = table.Column<decimal>(type: "numeric", nullable: false),
                    is_negative_stock_authorized = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale_line_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_sale_line_items_sales_sale_id",
                        column: x => x.sale_id,
                        principalTable: "sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_created_at",
                table: "audit_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_cashiers_username",
                table: "cashiers",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customers_phone",
                table: "customers",
                column: "phone");

            migrationBuilder.CreateIndex(
                name: "ix_products_barcode",
                table: "products",
                column: "barcode");

            migrationBuilder.CreateIndex(
                name: "ix_products_category_id",
                table: "products",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_sku",
                table: "products",
                column: "sku");

            migrationBuilder.CreateIndex(
                name: "ix_sale_line_items_sale_id",
                table: "sale_line_items",
                column: "sale_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_receipt_number",
                table: "sales",
                column: "receipt_number");

            migrationBuilder.CreateIndex(
                name: "ix_store_settings_key",
                table: "store_settings",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "cashiers");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "inventory_movements");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "sale_line_items");

            migrationBuilder.DropTable(
                name: "shift_sessions");

            migrationBuilder.DropTable(
                name: "store_settings");

            migrationBuilder.DropTable(
                name: "tax_rates");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "sales");
        }
    }
}
