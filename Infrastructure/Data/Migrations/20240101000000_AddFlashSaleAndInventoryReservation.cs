using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace Infrastructure.Data.Migrations
{
    public partial class AddFlashSaleAndInventoryReservation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Flash-Sale feature: optimistic-concurrency token added to the existing Products table
            // (additive only; no other Products column is altered; Npgsql resolves this uint column to "bigint").
            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Products",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Flash-Sale feature: schedulable time-boxed promotion table
            migrationBuilder.CreateTable(
                name: "FlashSales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    StockAllocation = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlashSales", x => x.Id);
                });

            // Flash-Sale feature: bounded-time stock reservation table (zero-oversell guard)
            migrationBuilder.CreateTable(
                name: "InventoryReservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    // Flash-Sale feature: sale-scoped hold (FlashSaleId) and durable lifecycle Status
                    // (ReservationStatus enum persisted as int) — both required by the hardened model so
                    // availability can be aggregated per sale and filtered by Status.
                    FlashSaleId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryReservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlashSales_ProductId",
                table: "FlashSales",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_FlashSales_StartAt_EndAt",
                table: "FlashSales",
                columns: new[] { "StartAt", "EndAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_ExpiresAt",
                table: "InventoryReservations",
                column: "ExpiresAt");

            // Flash-Sale feature: per-sale availability aggregation (SUM by FlashSaleId filtered by Status).
            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_FlashSaleId_Status",
                table: "InventoryReservations",
                columns: new[] { "FlashSaleId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_ProductId",
                table: "InventoryReservations",
                column: "ProductId");

            // Flash-Sale feature: checkout-consume and explicit release match by (SessionId, ProductId);
            // a SessionId-leading composite serves that session-first predicate.
            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_SessionId_ProductId",
                table: "InventoryReservations",
                columns: new[] { "SessionId", "ProductId" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlashSales");

            migrationBuilder.DropTable(
                name: "InventoryReservations");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Products");
        }
    }
}
