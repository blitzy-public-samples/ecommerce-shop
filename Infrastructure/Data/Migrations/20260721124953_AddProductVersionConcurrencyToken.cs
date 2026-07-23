using Microsoft.EntityFrameworkCore.Migrations;

namespace Infrastructure.Data.Migrations
{
    public partial class AddProductVersionConcurrencyToken : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Flash-Sale feature (review finding M20): the optimistic-concurrency token is CLR `uint`, which
            // the Npgsql provider maps to the PostgreSQL `oid` type. Create the column as `oid` (default 0u) so
            // this historical migration, its Designer, the model snapshot, and the entity model all agree on the
            // provider-native type. This migration keeps its original identity (20260721124953) so databases that
            // already recorded it are never asked to re-add the column (review finding C01).
            migrationBuilder.AddColumn<uint>(
                name: "Version",
                table: "Products",
                type: "oid",
                nullable: false,
                defaultValue: 0u);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "Products");
        }
    }
}
