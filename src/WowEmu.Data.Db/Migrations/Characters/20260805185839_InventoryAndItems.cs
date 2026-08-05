using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowEmu.Data.Db.Migrations.Characters
{
    /// <inheritdoc />
    public partial class InventoryAndItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "character_inventory",
                columns: table => new
                {
                    item = table.Column<uint>(type: "int unsigned", nullable: false),
                    guid = table.Column<uint>(type: "int unsigned", nullable: false),
                    bag = table.Column<uint>(type: "int unsigned", nullable: false),
                    slot = table.Column<byte>(type: "tinyint unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_inventory", x => x.item);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "item_instance",
                columns: table => new
                {
                    guid = table.Column<uint>(type: "int unsigned", nullable: false),
                    item_entry = table.Column<uint>(type: "int unsigned", nullable: false),
                    owner_guid = table.Column<uint>(type: "int unsigned", nullable: false),
                    count = table.Column<uint>(type: "int unsigned", nullable: false),
                    durability = table.Column<uint>(type: "int unsigned", nullable: false),
                    duration = table.Column<uint>(type: "int unsigned", nullable: false),
                    charges = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    flags = table.Column<uint>(type: "int unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_item_instance", x => x.guid);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "ix_character_inventory_owner",
                table: "character_inventory",
                column: "guid");

            migrationBuilder.CreateIndex(
                name: "ix_item_instance_owner",
                table: "item_instance",
                column: "owner_guid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "character_inventory");

            migrationBuilder.DropTable(
                name: "item_instance");
        }
    }
}
