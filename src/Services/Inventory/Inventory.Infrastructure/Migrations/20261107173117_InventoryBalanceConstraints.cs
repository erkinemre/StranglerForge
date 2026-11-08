using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InventoryBalanceConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_StockReservations_Quantity",
                table: "StockReservations",
                sql: "\"Quantity\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockItems_Balances",
                table: "StockItems",
                sql: "\"Quantity\" >= 0 AND \"Reserved\" >= 0 AND \"Reserved\" <= \"Quantity\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockReservations_Quantity",
                table: "StockReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockItems_Balances",
                table: "StockItems");
        }
    }
}
