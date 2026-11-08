using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OrderCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "OrderSagas",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "OrderEventAudit",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
            migrationBuilder.Sql("UPDATE \"OrderSagas\" SET \"CorrelationId\" = \"OrderId\" WHERE \"CorrelationId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql("UPDATE \"OrderEventAudit\" SET \"CorrelationId\" = \"OrderId\" WHERE \"CorrelationId\" = '00000000-0000-0000-0000-000000000000'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "OrderSagas");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "OrderEventAudit");
        }
    }
}
