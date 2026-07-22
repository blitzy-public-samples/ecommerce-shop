using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace Infrastructure.Data.Migrations
{
    public partial class AddFlashSaleAndInventoryReservation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    Version = table.Column<uint>(type: "oid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlashSales", x => x.Id);
                    // Flash-Sale feature (review finding M2): database-integrity CHECK constraints — a positive
                    // sale price, a positive stock allocation, and a correctly-ordered non-empty [StartAt, EndAt]
                    // window, so a direct/buggy row can never corrupt availability accounting or event routing.
                    table.CheckConstraint("CK_FlashSales_SalePrice_Positive", "\"SalePrice\" > 0");
                    table.CheckConstraint("CK_FlashSales_StockAllocation_Positive", "\"StockAllocation\" > 0");
                    table.CheckConstraint("CK_FlashSales_EndAt_After_StartAt", "\"EndAt\" > \"StartAt\"");
                    table.ForeignKey(
                        name: "FK_FlashSales_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryReservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
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
                    table.CheckConstraint("CK_InventoryReservations_Quantity_Positive", "\"Quantity\" > 0");
                    // Flash-Sale feature (review finding M2): Status must stay within the ReservationStatus enum
                    // domain (Active=0, Consumed=1, Released=2, Expired=3) so a hold can never fall out of the
                    // availability aggregation (which counts only Active/Consumed rows).
                    table.CheckConstraint("CK_InventoryReservations_Status_Valid", "\"Status\" >= 0 AND \"Status\" <= 3");
                    table.ForeignKey(
                        name: "FK_InventoryReservations_FlashSales_FlashSaleId",
                        column: x => x.FlashSaleId,
                        principalTable: "FlashSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryReservations_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "IX_InventoryReservations_FlashSaleId_Status",
                table: "InventoryReservations",
                columns: new[] { "FlashSaleId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_ProductId_SessionId",
                table: "InventoryReservations",
                columns: new[] { "ProductId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_Status_ExpiresAt",
                table: "InventoryReservations",
                columns: new[] { "Status", "ExpiresAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryReservations");

            migrationBuilder.DropTable(
                name: "FlashSales");
        }
    }
}
