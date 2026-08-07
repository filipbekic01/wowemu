using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowEmu.Data.Db.Migrations.Characters
{
    /// <inheritdoc />
    public partial class CharacterVitals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "death_expire_time",
                table: "characters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<uint>(
                name: "health",
                table: "characters",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "power1",
                table: "characters",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "power2",
                table: "characters",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "power3",
                table: "characters",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "power4",
                table: "characters",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "power5",
                table: "characters",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "power6",
                table: "characters",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "power7",
                table: "characters",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "death_expire_time",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "health",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "power1",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "power2",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "power3",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "power4",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "power5",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "power6",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "power7",
                table: "characters");
        }
    }
}
