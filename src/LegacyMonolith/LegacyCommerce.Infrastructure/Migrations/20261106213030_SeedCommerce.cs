using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LegacyCommerce.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedCommerce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Customers",
                columns: new[] { "Id", "Email", "Name" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000101"), "sample@example.test", "Sample Customer" });

            migrationBuilder.InsertData(
                table: "Products",
                columns: new[] { "Id", "Name", "Price" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000201"), "Sample Product", 19.99m });

            migrationBuilder.InsertData(
                table: "Stock",
                columns: new[] { "ProductId", "Quantity" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000201"), 25 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Customers",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000101"));

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000201"));

            migrationBuilder.DeleteData(
                table: "Stock",
                keyColumn: "ProductId",
                keyValue: new Guid("00000000-0000-0000-0000-000000000201"));
        }
    }
}
