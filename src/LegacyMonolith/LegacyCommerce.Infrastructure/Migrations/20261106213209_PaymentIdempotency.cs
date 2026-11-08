using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LegacyCommerce.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId",
                table: "Payments",
                column: "OrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_OrderId",
                table: "Payments");
        }
    }
}
