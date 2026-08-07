using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowEmu.Data.Db.Migrations.Characters
{
    /// <inheritdoc />
    public partial class ItemRandomProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "randomPropertyId",
                table: "item_instance",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<uint>(
                name: "randomSuffix",
                table: "item_instance",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "randomPropertyId",
                table: "item_instance");

            migrationBuilder.DropColumn(
                name: "randomSuffix",
                table: "item_instance");
        }
    }
}
